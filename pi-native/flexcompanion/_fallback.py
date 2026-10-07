"""Pure numpy implementation of the ``flexcore`` native module.

Used automatically when the C++ extension isn't built (for example on a machine
without a compiler). It mirrors the native API exactly so the rest of the app never
needs to know which one is active; the native module is just faster.
"""
from __future__ import annotations

import struct
import threading
import time
from typing import List, Optional, Tuple

import numpy as np

PCC_METER = 0x8002
PCC_AUDIO_FLOAT = 0x03E3
PCC_AUDIO_INT16 = 0x0123
_HEADER = 28
NATIVE = False


def is_iq_pcc(pcc: int) -> bool:
    return pcc in (0x02E3, 0x02E4, 0x02E5, 0x02E6)


def _classify(d: bytes):
    if len(d) < _HEADER or (d[0] & 0x08) == 0:
        return None
    pcc = (d[14] << 8) | d[15]
    stream_id = struct.unpack_from(">I", d, 4)[0]
    trailer = (d[0] & 0x04) != 0
    size_bytes = ((d[2] << 8) | d[3]) * 4
    end = min(len(d), size_bytes if size_bytes > 0 else len(d)) - (4 if trailer else 0)
    return pcc, stream_id, end


def peek(data) -> Tuple[int, int]:
    c = _classify(bytes(data))
    return (0, 0) if c is None else (c[0], c[1])


def parse_meters(data) -> List[Tuple[int, int]]:
    d = bytes(data)
    c = _classify(d)
    if c is None or c[0] != PCC_METER:
        return []
    end = c[2]
    n = max(0, (end - _HEADER) // 4)
    if n == 0:
        return []
    arr = np.frombuffer(d, dtype=">u2", count=n * 2, offset=_HEADER).reshape(n, 2)
    ids = arr[:, 0].astype(np.int64)
    vals = arr[:, 1].astype(np.uint16).view(np.int16).astype(np.int64)
    return list(zip(ids.tolist(), vals.tolist()))


def _finite(a: np.ndarray) -> np.ndarray:
    return np.where(np.isfinite(a), a, 0).astype(np.float32)


def parse_audio(data):
    d = bytes(data)
    c = _classify(d)
    if c is None or c[0] not in (PCC_AUDIO_FLOAT, PCC_AUDIO_INT16):
        return None
    pcc, sid, end = c
    nbytes = end - _HEADER
    if nbytes <= 0:
        return None
    if pcc == PCC_AUDIO_FLOAT:
        frames = nbytes // 8
        with np.errstate(invalid="ignore", over="ignore"):
            lr = _finite(np.frombuffer(d, dtype=">f4", count=frames * 2, offset=_HEADER).astype(np.float32))
            mono = _finite((lr[0::2] + lr[1::2]) * 0.5)
        return sid, mono
    n = nbytes // 2
    return sid, (np.frombuffer(d, dtype=">i2", count=n, offset=_HEADER).astype(np.float32) / 32768.0)


def parse_fcsp(data):
    """AetherSDR shared-pan datagram -> (serial, stream_id, bins, source_ns) or None."""
    d = bytes(data)
    if len(d) < 24 or d[:4] != b"FCSP" or d[4] != 1:
        return None
    serial_len, stream_id, bins = struct.unpack_from("<HIH", d, 6)
    ns = struct.unpack_from("<q", d, 16)[0]
    if serial_len == 0 or bins < 2 or bins > 16384 or stream_id == 0:
        return None
    payload = 24 + serial_len
    if payload + bins * 4 > len(d):
        return None
    serial = d[24:payload].decode("utf-8", "replace")
    with np.errstate(invalid="ignore"):
        arr = np.frombuffer(d, dtype="<f4", count=bins, offset=payload).astype(np.float32)
    arr[~np.isfinite(arr)] = -160.0
    return serial, stream_id, arr, ns


def _valid_size(n: int) -> bool:
    return 64 <= n <= 65536 and (n & (n - 1)) == 0


_windows: dict = {}


def _window(n: int) -> np.ndarray:
    w = _windows.get(n)
    if w is None:
        k = np.arange(n, dtype=np.float64)
        w = (0.5 - 0.5 * np.cos(2 * np.pi * k / max(1, n - 1))).astype(np.float32)
        _windows[n] = w
    return w


def _power_db(i: np.ndarray, q: np.ndarray) -> np.ndarray:
    n = len(i)
    x = (i.astype(np.float32) + 1j * q.astype(np.float32)) * _window(n)
    spec = np.fft.fftshift(np.fft.fft(x))
    norm_sq = (n * 0.5) ** 2
    mag_sq = (spec.real ** 2 + spec.imag ** 2) / norm_sq
    return (10.0 * np.log10(np.maximum(1e-24, mag_sq))).astype(np.float32)


def fft_db(i, q) -> np.ndarray:
    i = np.asarray(i, dtype=np.float32)
    q = np.asarray(q, dtype=np.float32)
    n = min(len(i), len(q))
    if not _valid_size(n):
        raise ValueError("length must be a power of two >= 64")
    return _power_db(i[:n], q[:n])


def resample_linear(src, count: int, start: float, end: float, floor_db: float = -160.0) -> np.ndarray:
    src = np.asarray(src, dtype=np.float32)
    if count <= 0:
        return np.zeros(0, dtype=np.float32)
    n = len(src)
    if n == 0:
        return np.full(count, floor_db, dtype=np.float32)
    if n == 1:
        return np.full(count, src[0], dtype=np.float32)
    pos = start + (end - start) * np.arange(count) / max(1.0, count - 1.0)
    out = np.interp(pos, np.arange(n), src).astype(np.float32)
    out[(pos < 0) | (pos > n - 1)] = floor_db
    return out


class SpectrumEngine:
    """Thread-safe IQ ring buffer with latest-frame-only FFT (numpy version)."""

    def __init__(self, capacity: int = 16384):
        if capacity < 64:
            raise ValueError("capacity must be >= 64")
        self._cap = capacity
        self._i = np.zeros(capacity, dtype=np.float32)
        self._q = np.zeros(capacity, dtype=np.float32)
        self._lock = threading.Lock()
        self._write = 0
        self._count = 0
        self._version = 0
        self._processed = -1
        self._last = 0.0
        self._n = 0

    def configure(self, fft_size: int) -> None:
        if fft_size != 0 and not _valid_size(fft_size):
            raise ValueError("fft_size must be 0 or a power of two between 64 and 65536")
        self._n = fft_size

    @property
    def fft_size(self) -> int:
        return self._n

    def reset(self) -> None:
        with self._lock:
            self._write = self._count = 0
            self._version += 1
            self._processed = -1
            self._last = 0.0

    def _push(self, i: np.ndarray, q: np.ndarray) -> None:
        n = min(len(i), len(q))
        if n <= 0:
            return
        if n > self._cap:
            i, q, n = i[-self._cap:], q[-self._cap:], self._cap
        with self._lock:
            w = self._write
            first = min(n, self._cap - w)
            self._i[w:w + first] = i[:first]
            self._q[w:w + first] = q[:first]
            rest = n - first
            if rest:
                self._i[:rest] = i[first:n]
                self._q[:rest] = q[first:n]
            self._write = (w + n) % self._cap
            self._count = min(self._cap, self._count + n)
            self._version += 1
            self._last = time.monotonic()

    def push(self, i, q) -> None:
        self._push(_finite(np.asarray(i, dtype=np.float32)), _finite(np.asarray(q, dtype=np.float32)))

    def push_packet(self, data, stream_id: int = 0) -> int:
        d = bytes(data)
        c = _classify(d)
        if c is None or not is_iq_pcc(c[0]):
            return 0
        if stream_id and c[1] != stream_id:
            return 0
        pairs = (c[2] - _HEADER) // 8
        if pairs <= 0:
            return 0
        with np.errstate(invalid="ignore", over="ignore"):
            iq = _finite(np.frombuffer(d, dtype="<f4", count=pairs * 2, offset=_HEADER))
        self._push(iq[0::2], iq[1::2])
        return pairs

    def is_live(self, max_age_ms: int = 800) -> bool:
        with self._lock:
            return self._last != 0.0 and (time.monotonic() - self._last) * 1000 < max_age_ms

    @property
    def available(self) -> int:
        return self._count

    def compute(self) -> Optional[np.ndarray]:
        n = self._n
        if n == 0:
            return None
        with self._lock:
            if self._count < n or self._version == self._processed:
                return None
            start = (self._write - n) % self._cap
            idx = (start + np.arange(n)) % self._cap
            i = self._i[idx].copy()
            q = self._q[idx].copy()
            self._processed = self._version
        return _power_db(i, q)
