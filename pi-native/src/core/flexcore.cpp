// flexcore — native DSP / packet core for Flex Control Companion (Pi native build).
//
// Ported from the .NET Companion's FlexClient.ParseVita / IqAnalyzer. The Python app
// does networking, state and UI; this module does the per-packet and per-frame work
// that would otherwise run in the interpreter on every UDP datagram:
//
//   * VITA-49 classification and decoding (meters, DAX audio, DAX IQ)
//   * a thread-safe IQ ring buffer + Hann-windowed radix-2 FFT (latest-frame-only)
//   * linear resampling helper for spectrum re-slicing
//
// All heavy calls release the GIL, so the UDP thread, the FFT worker and the Qt UI
// thread can run concurrently on a Pi 4/5.

#include <pybind11/pybind11.h>
#include <pybind11/numpy.h>
#include <pybind11/stl.h>

#include <string>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <vector>

namespace py = pybind11;

namespace {

constexpr int kHeader = 28;          // FLEX VITA header incl. class id + timestamps
constexpr int kPccMeter = 0x8002;
constexpr int kPccAudioFloat = 0x03E3;
constexpr int kPccAudioInt16 = 0x0123;

inline uint16_t be16(const uint8_t* p) { return static_cast<uint16_t>((p[0] << 8) | p[1]); }
inline uint32_t be32(const uint8_t* p) {
    return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) | uint32_t(p[3]);
}
inline uint32_t le32(const uint8_t* p) {
    return uint32_t(p[0]) | (uint32_t(p[1]) << 8) | (uint32_t(p[2]) << 16) | (uint32_t(p[3]) << 24);
}
inline float bits_to_float(uint32_t u) {
    float f;
    std::memcpy(&f, &u, sizeof f);
    return std::isfinite(f) ? f : 0.0f;
}

bool is_iq_pcc(int pcc) { return pcc == 0x02E3 || pcc == 0x02E4 || pcc == 0x02E5 || pcc == 0x02E6; }

struct Packet {
    const uint8_t* d = nullptr;
    int len = 0;
    int pcc = 0;
    uint32_t stream_id = 0;
    int payload_end = 0;  // exclusive, trailer removed
    bool valid = false;
};

Packet classify(const uint8_t* d, int len) {
    Packet p;
    p.d = d;
    p.len = len;
    if (len < kHeader) return p;
    if ((d[0] & 0x08) == 0) return p;  // no class id
    p.pcc = be16(d + 14);
    p.stream_id = be32(d + 4);
    bool trailer = (d[0] & 0x04) != 0;
    int size_bytes = int(be16(d + 2)) * 4;
    int end = std::min(len, size_bytes > 0 ? size_bytes : len) - (trailer ? 4 : 0);
    p.payload_end = end;
    p.valid = true;
    return p;
}

std::pair<const uint8_t*, int> buffer_of(const py::buffer& b) {
    py::buffer_info info = b.request();
    return {static_cast<const uint8_t*>(info.ptr), int(info.size * info.itemsize)};
}

int64_t now_ms() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count();
}

// ───────────────────────────── FFT ─────────────────────────────

struct FftPlan {
    int n;
    std::vector<float> window, cos_t, sin_t;
    std::vector<int> bitrev;

    explicit FftPlan(int size) : n(size), window(size), cos_t(size / 2), sin_t(size / 2), bitrev(size) {
        int bits = 0;
        while ((1 << bits) < n) ++bits;
        const double pi = 3.14159265358979323846;
        for (int k = 0; k < n; ++k) {
            window[k] = float(0.5 - 0.5 * std::cos(2.0 * pi * k / std::max(1, n - 1)));
            int r = 0, v = k;
            for (int b = 0; b < bits; ++b) { r = (r << 1) | (v & 1); v >>= 1; }
            bitrev[k] = r;
        }
        for (int k = 0; k < n / 2; ++k) {
            double a = -2.0 * pi * k / n;
            cos_t[k] = float(std::cos(a));
            sin_t[k] = float(std::sin(a));
        }
    }
};

std::shared_ptr<FftPlan> plan_for(int n) {
    static std::mutex m;
    static std::map<int, std::shared_ptr<FftPlan>> plans;
    std::lock_guard<std::mutex> g(m);
    auto it = plans.find(n);
    if (it != plans.end()) return it->second;
    auto p = std::make_shared<FftPlan>(n);
    plans[n] = p;
    return p;
}

void transform(const FftPlan& plan, float* re, float* im) {
    const int n = plan.n;
    for (int len = 2; len <= n; len <<= 1) {
        const int half = len >> 1;
        const int stride = n / len;
        for (int start = 0; start < n; start += len) {
            for (int j = 0; j < half; ++j) {
                const int a = start + j, b = a + half, tw = j * stride;
                const float wr = plan.cos_t[tw], wi = plan.sin_t[tw];
                const float vr = re[b] * wr - im[b] * wi;
                const float vi = re[b] * wi + im[b] * wr;
                const float ur = re[a], ui = im[a];
                re[a] = ur + vr; im[a] = ui + vi;
                re[b] = ur - vr; im[b] = ui - vi;
            }
        }
    }
}

bool valid_fft_size(int n) { return n >= 64 && n <= 65536 && (n & (n - 1)) == 0; }

// Windowed FFT of complex input -> fftshifted power in dBFS-ish units (Hann gain corrected).
void power_db(const FftPlan& plan, std::vector<float>& re, std::vector<float>& im, float* out) {
    transform(plan, re.data(), im.data());
    const int n = plan.n;
    const float norm = n * 0.5f;
    const float norm_sq = norm * norm;
    for (int k = 0; k < n; ++k) {
        const int src = (k + n / 2) & (n - 1);
        const float mag_sq = re[src] * re[src] + im[src] * im[src];
        out[k] = 10.0f * std::log10(std::max(1e-24f, mag_sq / norm_sq));
    }
}

// ───────────────────────────── IQ spectrum engine ─────────────────────────────

class SpectrumEngine {
public:
    explicit SpectrumEngine(int capacity = 16384) : cap_(capacity), i_(capacity), q_(capacity) {
        if (capacity < 64) throw std::invalid_argument("capacity must be >= 64");
    }

    void configure(int fft_size) {
        if (fft_size != 0 && !valid_fft_size(fft_size))
            throw std::invalid_argument("fft_size must be 0 or a power of two between 64 and 65536");
        fft_size_.store(fft_size);
    }

    int fft_size() const { return fft_size_.load(); }

    void reset() {
        std::lock_guard<std::mutex> g(m_);
        write_ = 0;
        count_ = 0;
        ++version_;
        last_packet_ms_ = 0;
        processed_version_ = -1;
    }

    void push(py::array_t<float, py::array::c_style | py::array::forcecast> i,
              py::array_t<float, py::array::c_style | py::array::forcecast> q) {
        auto n = std::min(i.size(), q.size());
        const float* ip = i.data();
        const float* qp = q.data();
        py::gil_scoped_release nogil;
        push_raw(ip, qp, int(n));
    }

    // Decode a DAX-IQ VITA packet straight into the ring buffer. Returns the number of
    // I/Q pairs consumed (0 when the packet is not DAX IQ or not for `stream_id`).
    int push_packet(const py::buffer& data, uint32_t stream_id) {
        auto [d, len] = buffer_of(data);
        py::gil_scoped_release nogil;
        Packet p = classify(d, len);
        if (!p.valid || !is_iq_pcc(p.pcc)) return 0;
        if (stream_id != 0 && p.stream_id != stream_id) return 0;
        int pairs = (p.payload_end - kHeader) / 8;
        if (pairs <= 0) return 0;
        std::lock_guard<std::mutex> g(m_);
        int skip = std::max(0, pairs - cap_);
        for (int k = skip; k < pairs; ++k) {
            const uint8_t* o = d + kHeader + k * 8;
            i_[write_] = bits_to_float(le32(o));      // DAX IQ is little-endian (FLEX exception)
            q_[write_] = bits_to_float(le32(o + 4));
            write_ = (write_ + 1) % cap_;
        }
        count_ = std::min(cap_, count_ + (pairs - skip));
        ++version_;
        last_packet_ms_ = now_ms();
        return pairs;
    }

    bool is_live(int max_age_ms) const {
        std::lock_guard<std::mutex> g(m_);
        return last_packet_ms_ != 0 && (now_ms() - last_packet_ms_) < max_age_ms;
    }

    int available() const {
        std::lock_guard<std::mutex> g(m_);
        return count_;
    }

    // Latest-frame-only: returns None when there are no new samples since the previous call
    // (or not yet enough for one transform). Safe to call from a worker thread.
    py::object compute() {
        int n = fft_size_.load();
        if (n == 0) return py::none();
        auto plan = plan_for(n);
        std::vector<float> re(n), im(n);
        bool ok = false;
        {
            py::gil_scoped_release nogil;
            {
                std::lock_guard<std::mutex> g(m_);
                if (count_ >= n && version_ != processed_version_) {
                    int start = (write_ - n + cap_) % cap_;
                    for (int k = 0; k < n; ++k) {
                        int idx = start + k;
                        if (idx >= cap_) idx -= cap_;
                        const int dst = plan->bitrev[k];
                        const float w = plan->window[k];
                        re[dst] = i_[idx] * w;
                        im[dst] = q_[idx] * w;
                    }
                    processed_version_ = version_;
                    ok = true;
                }
            }
        }
        if (!ok) return py::none();
        py::array_t<float> out(n);
        float* o = out.mutable_data();
        {
            py::gil_scoped_release nogil;
            power_db(*plan, re, im, o);
        }
        return out;
    }

private:
    void push_raw(const float* ip, const float* qp, int n) {
        if (n <= 0) return;
        int src = 0;
        if (n > cap_) { src = n - cap_; n = cap_; }
        std::lock_guard<std::mutex> g(m_);
        for (int k = 0; k < n; ++k) {
            float iv = ip[src + k], qv = qp[src + k];
            i_[write_] = std::isfinite(iv) ? iv : 0.0f;
            q_[write_] = std::isfinite(qv) ? qv : 0.0f;
            write_ = (write_ + 1) % cap_;
        }
        count_ = std::min(cap_, count_ + n);
        ++version_;
        last_packet_ms_ = now_ms();
    }

    const int cap_;
    mutable std::mutex m_;
    std::vector<float> i_, q_;
    int write_ = 0, count_ = 0;
    int64_t version_ = 0, processed_version_ = -1;
    int64_t last_packet_ms_ = 0;
    std::atomic<int> fft_size_{0};
};

// ───────────────────────────── packet decoding ─────────────────────────────

// (pcc, stream_id) without decoding; (0, 0) when the datagram isn't a classed VITA packet.
py::tuple peek(const py::buffer& data) {
    auto [d, len] = buffer_of(data);
    Packet p = classify(d, len);
    if (!p.valid) return py::make_tuple(0, 0u);
    return py::make_tuple(p.pcc, p.stream_id);
}

// Meter packet -> list of (meter_id, raw int16). Empty list for non-meter packets.
std::vector<std::pair<int, int>> parse_meters(const py::buffer& data) {
    auto [d, len] = buffer_of(data);
    std::vector<std::pair<int, int>> out;
    Packet p = classify(d, len);
    if (!p.valid || p.pcc != kPccMeter) return out;
    out.reserve(std::max(0, (p.payload_end - kHeader) / 4));
    for (int i = kHeader; i + 4 <= p.payload_end; i += 4)
        out.emplace_back(int(be16(d + i)), int(int16_t(be16(d + i + 2))));
    return out;
}

// DAX / slice audio -> mono float32 in -1..1. None for non-audio packets.
py::object parse_audio(const py::buffer& data) {
    auto [d, len] = buffer_of(data);
    Packet p = classify(d, len);
    if (!p.valid || (p.pcc != kPccAudioFloat && p.pcc != kPccAudioInt16)) return py::none();
    int bytes = p.payload_end - kHeader;
    if (bytes <= 0) return py::none();
    if (p.pcc == kPccAudioFloat) {
        int frames = bytes / 8;
        py::array_t<float> out(frames);
        float* o = out.mutable_data();
        for (int f = 0; f < frames; ++f) {
            const uint8_t* s = d + kHeader + f * 8;
            float l = bits_to_float(be32(s)), r = bits_to_float(be32(s + 4));
            float m = (l + r) * 0.5f;
            o[f] = std::isfinite(m) ? m : 0.0f;
        }
        return py::make_tuple(p.stream_id, out);
    }
    int n = bytes / 2;
    py::array_t<float> out(n);
    float* o = out.mutable_data();
    for (int k = 0; k < n; ++k) o[k] = float(int16_t(be16(d + kHeader + k * 2))) / 32768.0f;
    return py::make_tuple(p.stream_id, out);
}

// One-shot FFT of complex samples (used by tests and by callers without an engine).
py::array_t<float> fft_db(py::array_t<float, py::array::c_style | py::array::forcecast> i,
                          py::array_t<float, py::array::c_style | py::array::forcecast> q) {
    int n = int(std::min(i.size(), q.size()));
    if (!valid_fft_size(n)) throw std::invalid_argument("length must be a power of two >= 64");
    auto plan = plan_for(n);
    std::vector<float> re(n), im(n);
    for (int k = 0; k < n; ++k) {
        re[plan->bitrev[k]] = i.data()[k] * plan->window[k];
        im[plan->bitrev[k]] = q.data()[k] * plan->window[k];
    }
    py::array_t<float> out(n);
    float* o = out.mutable_data();
    {
        py::gil_scoped_release nogil;
        power_db(*plan, re, im, o);
    }
    return out;
}

py::array_t<float> resample_linear(py::array_t<float, py::array::c_style | py::array::forcecast> src,
                                   int count, double start, double end, float floor_db) {
    py::array_t<float> out(std::max(0, count));
    if (count <= 0) return out;
    float* o = out.mutable_data();
    const float* s = src.data();
    const int n = int(src.size());
    if (n == 0) { std::fill(o, o + count, floor_db); return out; }
    if (n == 1) { std::fill(o, o + count, s[0]); return out; }
    for (int k = 0; k < count; ++k) {
        double pos = start + (end - start) * k / std::max(1.0, count - 1.0);
        if (pos < 0 || pos > n - 1) { o[k] = floor_db; continue; }
        int i0 = int(pos);
        int i1 = std::min(i0 + 1, n - 1);
        double f = pos - i0;
        o[k] = float(s[i0] * (1.0 - f) + s[i1] * f);
    }
    return out;
}

// AetherSDR shared-pan bridge datagram (FCSP v1, little-endian):
//   "FCSP" | u8 version=1 | u8 flags | u16 serial_len | u32 pan stream id | u16 bin count |
//   u16 reserved | i64 source timestamp ns | serial (UTF-8) | bin_count x f32 dBm
// Returns (serial, stream_id, bins float32, source_ns) or None.
py::object parse_fcsp(const py::buffer& data) {
    auto [d, len] = buffer_of(data);
    constexpr int header = 24;
    if (len < header || std::memcmp(d, "FCSP", 4) != 0 || d[4] != 1) return py::none();
    auto le16 = [&](int o) { return int(d[o] | (d[o + 1] << 8)); };
    const int serial_len = le16(6);
    const uint32_t stream_id = le32(d + 8);
    const int bins = le16(12);
    int64_t ns = 0;
    for (int k = 7; k >= 0; --k) ns = (ns << 8) | d[16 + k];
    if (serial_len == 0 || bins < 2 || bins > 16384 || stream_id == 0) return py::none();
    const int payload = header + serial_len;
    if (payload + bins * 4 > len) return py::none();
    std::string serial(reinterpret_cast<const char*>(d + header), size_t(serial_len));
    py::array_t<float> out(bins);
    float* o = out.mutable_data();
    for (int k = 0; k < bins; ++k) {
        uint32_t u = le32(d + payload + k * 4);
        float f;
        std::memcpy(&f, &u, sizeof f);
        o[k] = std::isfinite(f) ? f : -160.0f;
    }
    return py::make_tuple(py::str(serial), stream_id, out, ns);
}

}  // namespace

PYBIND11_MODULE(flexcore, m) {
    m.doc() = "Native VITA-49 / FFT core for Flex Control Companion";
    m.attr("PCC_METER") = kPccMeter;
    m.attr("PCC_AUDIO_FLOAT") = kPccAudioFloat;
    m.attr("PCC_AUDIO_INT16") = kPccAudioInt16;

    m.def("peek", &peek, py::arg("data"), "Return (packet_class_code, stream_id), or (0, 0).");
    m.def("is_iq_pcc", &is_iq_pcc, py::arg("pcc"));
    m.def("parse_meters", &parse_meters, py::arg("data"), "Meter packet -> [(id, raw int16)].");
    m.def("parse_audio", &parse_audio, py::arg("data"), "Audio packet -> (stream_id, mono float32) or None.");
    m.def("parse_fcsp", &parse_fcsp, py::arg("data"),
          "AetherSDR shared-pan datagram -> (serial, stream_id, bins, source_ns) or None.");
    m.def("fft_db", &fft_db, py::arg("i"), py::arg("q"), "Hann-windowed FFT, fftshifted, in dB.");
    m.def("resample_linear", &resample_linear, py::arg("src"), py::arg("count"), py::arg("start"),
          py::arg("end"), py::arg("floor_db") = -160.0f);

    py::class_<SpectrumEngine>(m, "SpectrumEngine")
        .def(py::init<int>(), py::arg("capacity") = 16384)
        .def("configure", &SpectrumEngine::configure, py::arg("fft_size"))
        .def_property_readonly("fft_size", &SpectrumEngine::fft_size)
        .def("reset", &SpectrumEngine::reset)
        .def("push", &SpectrumEngine::push, py::arg("i"), py::arg("q"))
        .def("push_packet", &SpectrumEngine::push_packet, py::arg("data"), py::arg("stream_id") = 0u)
        .def("is_live", &SpectrumEngine::is_live, py::arg("max_age_ms") = 800)
        .def_property_readonly("available", &SpectrumEngine::available)
        .def("compute", &SpectrumEngine::compute);
}
