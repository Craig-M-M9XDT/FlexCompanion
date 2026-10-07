"""Receive-passband-centred spectrum maths (port of RadioViewModel.Audio.Part01/05.cs).

The compact spectrum is centred on the selected slice's filter passband: USB/LSB and
digital modes are offset to their filter centre, AM/FM stay symmetrical, and narrow
spans widen when needed so the whole filter stays visible.
"""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Optional

import numpy as np

SPAN_OPTIONS_KHZ = [3.0, 6.0, 12.0, 24.0, 48.0, 96.0, 192.0]
SAVER_SPAN_HZ = 24000.0

_FALLBACK_FILTERS = {
    "USB": (100, 3000), "DIGU": (100, 3000), "FDVU": (100, 3000),
    "LSB": (-3000, -100), "DIGL": (-3000, -100), "RTTY": (-3000, -100), "FDVL": (-3000, -100),
    "AM": (-3000, 3000), "SAM": (-3000, 3000),
    "FM": (-8000, 8000), "NFM": (-6000, 6000),
    "CW": (-500, 500), "CWL": (-500, 500), "CWU": (-500, 500), "CWR": (-500, 500),
}


@dataclass
class SpectrumWindow:
    centre_offset_hz: float
    span_hz: float


def window_for(mode: str, filter_lo: Optional[str], filter_hi: Optional[str], requested_span_hz: float) -> SpectrumWindow:
    fb_lo, fb_hi = _FALLBACK_FILTERS.get((mode or "").upper(), (0, 0))
    try:
        lo = float(filter_lo) if filter_lo is not None else fb_lo
    except ValueError:
        lo = fb_lo
    try:
        hi = float(filter_hi) if filter_hi is not None else fb_hi
    except ValueError:
        hi = fb_hi
    if hi < lo:
        lo, hi = hi, lo
    centre = (lo + hi) * 0.5
    width = max(0.0, hi - lo)
    half_extent = max(abs(lo - centre), abs(hi - centre), abs(centre))
    margin = max(400.0, width * 0.12)
    span = max(requested_span_hz, 1000.0, half_extent * 2 + margin)
    return SpectrumWindow(centre, span)


def effective_window(mode: str, filter_lo: Optional[str], filter_hi: Optional[str],
                     span_khz: float, saver: bool) -> SpectrumWindow:
    requested = min(span_khz, 24.0) if saver else span_khz
    w = window_for(mode, filter_lo, filter_hi, requested * 1000.0)
    if saver and w.span_hz > SAVER_SPAN_HZ:
        w.span_hz = SAVER_SPAN_HZ
    return w


def rate_for_span(span_hz: float, saver: bool) -> int:
    if saver:
        return 24000
    k = span_hz / 1000.0
    if k <= 24:
        return 24000
    if k <= 48:
        return 48000
    if k <= 96:
        return 96000
    return 192000


def fft_size_for(rate: int, span_hz: float) -> int:
    """Enough bins that the visible span gets ~512 points (1024..8192)."""
    want = rate / max(1.0, span_hz) * 512
    n = 1024
    while n < want and n < 8192:
        n <<= 1
    return n


@dataclass
class SpectrumFrame:
    bins: np.ndarray            # dB values, low -> high frequency
    half_span_hz: float         # +/- extent of the view
    centre_offset_hz: float     # view centre relative to the slice frequency
    slice_fraction: float       # 0..1 position of the carrier marker, NaN if off-screen
    filter_lo_frac: float = math.nan
    filter_hi_frac: float = math.nan


def crop_to_span(full: np.ndarray, rate: int, window: SpectrumWindow,
                 slice_offset_hz: Optional[float]) -> SpectrumFrame:
    """Crop a full fftshifted DAX-IQ spectrum to the passband-centred window.

    ``slice_offset_hz`` is the slice frequency relative to the pan centre (DAX IQ is
    centred on the pan), or None if unknown.
    """
    n = len(full)
    bin_hz = rate / n
    visible = min(n, max(16, int(round(window.span_hz / bin_hz))))
    have_slice = slice_offset_hz is not None and abs(slice_offset_hz) < rate / 2.0
    wanted_centre = (slice_offset_hz + window.centre_offset_hz) if have_slice else 0.0
    centre_bin = n // 2 + int(round(wanted_centre / bin_hz))
    start = min(max(centre_bin - visible // 2, 0), n - visible)
    out = np.array(full[start:start + visible], dtype=np.float32)

    view_centre = (start + visible / 2.0 - n / 2.0) * bin_hz
    if have_slice:
        centre_off = view_centre - slice_offset_hz
        frac = (slice_offset_hz - (start - n / 2.0) * bin_hz) / (visible * bin_hz)
        frac = frac if 0 <= frac <= 1 else math.nan
    else:
        centre_off = view_centre
        frac = math.nan
    return SpectrumFrame(out, visible * bin_hz * 0.5, centre_off, frac)


def smooth(prev: Optional[np.ndarray], spec: np.ndarray) -> np.ndarray:
    """Fast attack (0.65), slow release (0.22) — same as the .NET renderer."""
    if prev is None or len(prev) != len(spec):
        return spec
    up = spec > prev
    return np.where(up, prev + (spec - prev) * 0.65, prev + (spec - prev) * 0.22).astype(np.float32)


def filter_fractions(frame: SpectrumFrame, filter_lo: Optional[str], filter_hi: Optional[str]) -> SpectrumFrame:
    """Add the passband edges (as 0..1 view fractions) for drawing the filter shading."""
    if math.isnan(frame.slice_fraction):
        return frame
    try:
        lo = float(filter_lo)
        hi = float(filter_hi)
    except (TypeError, ValueError):
        return frame
    span = frame.half_span_hz * 2
    if span <= 0:
        return frame
    frame.filter_lo_frac = frame.slice_fraction + lo / span
    frame.filter_hi_frac = frame.slice_fraction + hi / span
    return frame
