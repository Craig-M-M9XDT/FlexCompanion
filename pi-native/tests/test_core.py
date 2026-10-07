"""The C++ core and the numpy fallback must agree exactly on parsing and closely on FFT output."""
import struct

import numpy as np
import pytest

from flexcompanion import _fallback
from flexcompanion.sim import vita_packet

try:
    from flexcompanion import flexcore
except ImportError:  # extension not built
    flexcore = None

BACKENDS = [pytest.param(_fallback, id="numpy")]
BACKENDS.append(pytest.param(flexcore, id="cpp", marks=pytest.mark.skipif(flexcore is None, reason="C++ core not built")))


@pytest.mark.parametrize("core", BACKENDS)
def test_meter_packet(core):
    payload = struct.pack(">HhHhHh", 1, -97 * 128, 2, 50 * 128, 7, -1)
    pkt = vita_packet(0x8002, 0x700, payload)
    assert core.peek(pkt) == (0x8002, 0x700)
    assert core.parse_meters(pkt) == [(1, -97 * 128), (2, 50 * 128), (7, -1)]


@pytest.mark.parametrize("core", BACKENDS)
def test_trailer_and_short_packets(core):
    pkt = bytearray(vita_packet(0x8002, 0x700, struct.pack(">Hh", 5, 10) + b"\0\0\0\0"))
    pkt[0] |= 0x04                                  # trailer flag: last word is not a meter
    assert core.parse_meters(bytes(pkt)) == [(5, 10)]
    assert core.peek(b"\x00" * 10) == (0, 0)
    no_class = bytearray(vita_packet(0x8002, 1, b""))
    no_class[0] &= ~0x08
    assert core.peek(bytes(no_class)) == (0, 0)
    assert core.parse_meters(bytes(no_class)) == []


@pytest.mark.parametrize("core", BACKENDS)
def test_audio_float_and_int16(core):
    lr = np.array([0.5, 0.25, -1.0, 1.0, np.nan, 0.0], dtype=">f4")
    sid, mono = core.parse_audio(vita_packet(0x03E3, 0x04000008, lr.tobytes()))
    assert sid == 0x04000008
    np.testing.assert_allclose(mono, [0.375, 0.0, 0.0])
    pcm = np.array([16384, -32768], dtype=">i2")
    sid, mono = core.parse_audio(vita_packet(0x0123, 9, pcm.tobytes()))
    np.testing.assert_allclose(mono[:2], [0.5, -1.0])
    assert core.parse_audio(vita_packet(0x8002, 1, b"")) is None


def _iq_packet(sid, x):
    inter = np.empty(len(x) * 2, dtype="<f4")
    inter[0::2], inter[1::2] = x.real, x.imag
    return vita_packet(0x02E3, sid, inter.tobytes())


@pytest.mark.parametrize("core", BACKENDS)
def test_fft_peak_position(core):
    n, rate, f = 1024, 48000, 6000.0
    t = np.arange(n)
    x = np.exp(2j * np.pi * f * t / rate).astype(np.complex64)
    db = core.fft_db(x.real.astype(np.float32), x.imag.astype(np.float32))
    assert len(db) == n
    peak = int(np.argmax(db))
    assert peak == n // 2 + int(f / (rate / n))
    assert -1.0 < db[peak] < 0.5             # unit tone ~0 dB after Hann gain correction


@pytest.mark.skipif(flexcore is None, reason="C++ core not built")
def test_cpp_matches_numpy_fft():
    rng = np.random.default_rng(1)
    i = rng.standard_normal(2048).astype(np.float32)
    q = rng.standard_normal(2048).astype(np.float32)
    np.testing.assert_allclose(flexcore.fft_db(i, q), _fallback.fft_db(i, q), atol=2e-3)


@pytest.mark.parametrize("core", BACKENDS)
def test_engine_latest_frame_only(core):
    eng = core.SpectrumEngine(4096)
    eng.configure(1024)
    assert eng.compute() is None                     # not enough samples yet
    x = np.exp(2j * np.pi * 0.1 * np.arange(1024)).astype(np.complex64)
    assert eng.push_packet(_iq_packet(0x20000001, x), 0x20000001) == 1024
    assert eng.push_packet(_iq_packet(0x20000002, x), 0x20000001) == 0   # other stream ignored
    assert eng.is_live(800)
    a = eng.compute()
    assert a is not None and len(a) == 1024
    assert eng.compute() is None                     # no new samples -> no new frame
    eng.push(x.real, x.imag)
    assert eng.compute() is not None
    eng.reset()
    assert eng.available == 0 and not eng.is_live(800)
    with pytest.raises((ValueError, Exception)):
        eng.configure(1000)


@pytest.mark.parametrize("core", BACKENDS)
def test_engine_matches_one_shot(core):
    rng = np.random.default_rng(3)
    x = (rng.standard_normal(3000) + 1j * rng.standard_normal(3000)).astype(np.complex64)
    eng = core.SpectrumEngine(4096)
    eng.configure(1024)
    eng.push_packet(_iq_packet(5, x), 5)
    tail = x[-1024:]
    np.testing.assert_allclose(eng.compute(), core.fft_db(tail.real.copy(), tail.imag.copy()), atol=1e-3)


@pytest.mark.parametrize("core", BACKENDS)
def test_resample_linear(core):
    # positions -1, -0.25, 0.5, 1.25, 2 -> out of range below 0 gives the floor
    out = core.resample_linear(np.array([0, 10, 20], dtype=np.float32), 5, -1, 2, -99)
    np.testing.assert_allclose(out, [-99, -99, 5, 12.5, 20])
    np.testing.assert_allclose(core.resample_linear(np.array([0, 10], dtype=np.float32), 3, 0, 1), [0, 5, 10])
