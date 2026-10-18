using System.Numerics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FlexCompanion.Services;

/// <summary>
/// Read-only WASAPI loopback of the DAX TX playback endpoint. Does not create
/// Flex radio streams, transmit audio, take audio-device ownership or control PTT.
/// Audio FFT is NOT calibrated RF feedback.
/// </summary>
public sealed class TxAudioLoopback : IDisposable
{
    const int N = 2048;
    readonly object _gate = new();
    readonly float[] _samples = new float[N];
    WasapiLoopbackCapture? _capture;
    int _write, _filled;
    int _rate = 48000;
    long _generation;
    DateTime _lastAudioUtc;
    public string Status { get; private set; } = "not started";
    public bool IsRunning => _capture != null;

    public void Start()
    {
        if (_capture != null) return;
        try
        {
            using var endpoints = new MMDeviceEnumerator();
            var device = endpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .FirstOrDefault(d => d.FriendlyName.Contains("DAX Audio TX", StringComparison.OrdinalIgnoreCase)
                    || d.FriendlyName.Contains("DAX TX", StringComparison.OrdinalIgnoreCase));
            if (device == null)
            {
                Status = "DAX TX playback device not found";
                return;
            }
            var capture = new WasapiLoopbackCapture(device);
            _rate = capture.WaveFormat.SampleRate;
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, _) => { };
            _capture = capture;
            capture.StartRecording();
            Status = "DAX TX audio loopback · " + device.FriendlyName;
        }
        catch (Exception ex)
        {
            Status = "TX loopback unavailable: " + ex.Message;
            Stop();
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        var fmt = _capture?.WaveFormat;
        if (fmt == null) return;
        int channels = fmt.Channels;
        int bits = fmt.BitsPerSample;
        if (channels < 1 || bits is not (16 or 32)) return;
        int bytesPerSample = bits / 8;
        int frameBytes = bytesPerSample * channels;
        bool floating = fmt.Encoding == WaveFormatEncoding.IeeeFloat
            || (fmt is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        lock (_gate)
        {
            for (int offset = 0; offset + frameBytes <= e.BytesRecorded; offset += frameBytes)
            {
                float sample = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    int p = offset + ch * bytesPerSample;
                    sample += floating && bits == 32 ? BitConverter.ToSingle(e.Buffer, p)
                        : bits == 16 ? BitConverter.ToInt16(e.Buffer, p) / 32768f
                        : BitConverter.ToInt32(e.Buffer, p) / 2147483648f;
                }
                sample /= channels;
                _samples[_write] = float.IsFinite(sample) ? sample : 0;
                _write = (_write + 1) % N;
                _filled = Math.Min(_filled + 1, N);
            }
            _lastAudioUtc = DateTime.UtcNow;
            _generation++;
        }
    }

    public bool TrySpectrum(double spanHz, out float[] spectrum, out double maxHz)
    {
        spectrum = Array.Empty<float>();
        maxHz = Math.Max(500, spanHz / 2);
        var data = new Complex[N];
        lock (_gate)
        {
            if (_filled < N || (DateTime.UtcNow - _lastAudioUtc).TotalMilliseconds > 900) return false;
            for (int i = 0; i < N; i++)
            {
                double window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1));
                data[i] = new Complex(_samples[(_write + i) % N] * window, 0);
            }
        }
        // In-place radix-2 FFT, only the nonnegative audio sideband is shown.
        for (int i = 1, j = 0; i < N; i++)
        {
            int bit = N >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (int len = 2; len <= N; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            Complex step = new(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < N; i += len)
            {
                Complex w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    Complex u = data[i + j], v = data[i + j + len / 2] * w;
                    data[i + j] = u + v;
                    data[i + j + len / 2] = u - v;
                    w *= step;
                }
            }
        }
        // Symmetric display centered on carrier: negative bins are silence,
        // while positive bins show DAX TX modulation at their audio frequencies.
        const int displayBins = 512;
        spectrum = new float[displayBins];
        double hzPerBin = (double)_rate / N;
        for (int i = 0; i < displayBins; i++)
        {
            double offsetHz = ((i + 0.5) / displayBins - 0.5) * spanHz;
            int k = (int)Math.Round(offsetHz / hzPerBin);
            spectrum[i] = k >= 0 && k < N / 2
                ? (float)(20 * Math.Log10(Math.Max(1e-9, data[k].Magnitude / N)))
                : -160;
        }
        return true;
    }

    public void Stop()
    {
        var c = _capture;
        _capture = null;
        if (c == null) return;
        try { c.StopRecording(); } catch { }
        try { c.Dispose(); } catch { }
        lock (_gate) { _filled = 0; _generation = 0; }
    }
    public void Dispose() => Stop();
}
