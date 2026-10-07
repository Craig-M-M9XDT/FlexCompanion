"""Best AGC-T: slice audio level analysis and the sweep calibrator.

Ported from the .NET ``AudioAnalyzer`` and ``AgcTCalibrator``, which in turn port AetherSDR's
AgcTCalibrator (src/core/AgcTCalibrator.cpp, GPL-3.0, https://github.com/aethersdr/AetherSDR).

* AGC slow/med/fast: AGC-T is ``agc_threshold`` (the AGC knee). Sweep 100 -> 0 and record
  post-AGC audio RMS; the knee is where the noise "just begins to drop", found as the point of
  maximum perpendicular distance from the chord of the RMS-vs-value curve.
* AGC off: the knob is ``agc_off_level`` (fixed gain). There is no knee, so solve for the value
  that puts the audio noise at a comfortable target level.

The audio must be post-AGC and pre-volume, which is exactly what a DAX RX stream is.
"""
from __future__ import annotations

import math
import threading
import time
from dataclasses import dataclass
from typing import Callable, List, Optional

import numpy as np

from .dispatch import Dispatcher, TimerHandle

RMS_ALPHA = 0.30          # same smoothing as AetherSDR's engine
LIVE_S = 0.5


class AudioAnalyzer:
    """Smoothed RMS of DAX RX audio. ``push`` runs on the UDP thread."""

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._rms = 0.0
        self._have = False
        self._last = 0.0

    def reset(self) -> None:
        with self._lock:
            self._rms = 0.0
            self._have = False
            self._last = 0.0

    def push(self, samples: np.ndarray) -> None:
        n = len(samples)
        if n == 0:
            return
        rms = float(np.sqrt(np.mean(np.square(samples, dtype=np.float64))))
        with self._lock:
            if not self._have:
                self._rms, self._have = rms, True
            else:
                self._rms = (1 - RMS_ALPHA) * self._rms + RMS_ALPHA * rms
            self._last = time.monotonic()

    @property
    def is_live(self) -> bool:
        return self._last != 0.0 and time.monotonic() - self._last < LIVE_S

    @property
    def rms_db(self) -> float:
        """Smoothed post-AGC RMS in dBFS, or NaN when no audio is flowing."""
        with self._lock:
            if not self._have or not self.is_live:
                return math.nan
            return 20 * math.log10(max(self._rms, 1e-6))


@dataclass
class CurvePoint:
    value: int
    rms_db: float


class AgcTCalibrator:
    SWEEP_STEP = 4

    def __init__(self, dispatcher: Dispatcher) -> None:
        self.d = dispatcher
        self.get_value: Optional[Callable[[], int]] = None
        self.apply_value: Optional[Callable[[int], None]] = None
        self.is_off_mode: Optional[Callable[[], bool]] = None
        self.rms_db: Optional[Callable[[], float]] = None
        self.on_change: Optional[Callable[[], None]] = None
        self.on_finished: Optional[Callable[[], None]] = None

        self.settle_s = 0.280
        self.target_db = -28.0
        self.running = False
        self.original_value = -1
        self.recommended = -1
        self.recommended_is_knee = False
        self.curve: List[CurvePoint] = []
        self._sweep = 100
        self._timer: Optional[TimerHandle] = None

    @property
    def percent(self) -> int:
        if self.running:
            return int(100 * (100 - self._sweep) / 100)
        return 100 if self.recommended >= 0 else 0

    def _changed(self) -> None:
        if self.on_change:
            self.on_change()

    def _arm(self) -> None:
        self._timer = self.d.call_later(self.settle_s, self._step)

    def _cancel(self) -> None:
        if self._timer is not None:
            self._timer.cancel()
            self._timer = None

    def start(self) -> None:
        if self.running or self.get_value is None or self.apply_value is None:
            return
        self.original_value = self.get_value()
        self.recommended = -1
        self.curve = []
        self.running = True
        self._sweep = 100
        self.apply_value(self._sweep)
        self._arm()
        self._changed()

    def _step(self) -> None:
        self._timer = None
        if not self.running:
            return
        self._record(self._sweep)
        if self._sweep <= 0:
            self._finish()
            return
        self._sweep = max(0, self._sweep - self.SWEEP_STEP)
        if self.apply_value:
            self.apply_value(self._sweep)
        self._arm()
        self._changed()

    def _finish(self) -> None:
        self._cancel()
        self.running = False
        self._recompute()
        if self.recommended >= 0 and self.apply_value:
            self.apply_value(self.recommended)
        self._changed()
        if self.on_finished:
            self.on_finished()

    def stop(self) -> None:
        """Abort / Restore: put the original value back."""
        self._cancel()
        had = self.running or self.recommended >= 0
        self.running = False
        if self.original_value >= 0 and had and self.apply_value:
            self.apply_value(self.original_value)
        self.recommended = -1
        self._changed()

    def keep(self) -> None:
        self._cancel()
        self.running = False
        if self.recommended >= 0 and self.apply_value:
            self.apply_value(self.recommended)
        self.original_value = -1
        self._changed()

    def clear(self) -> None:
        if self.running:
            return
        self.curve = []
        self.recommended = -1
        self.original_value = -1
        self._changed()

    def _record(self, value: int) -> None:
        db = self.rms_db() if self.rms_db else math.nan
        if math.isnan(db):
            db = -120.0
        for p in self.curve:
            if p.value == value:
                p.rms_db = db
                break
        else:
            self.curve.append(CurvePoint(value, db))
            self.curve.sort(key=lambda p: p.value)

    def _recompute(self) -> None:
        pts = self.curve
        if len(pts) < 3:
            return
        if self.is_off_mode and self.is_off_mode():
            best, best_err = pts[0].value, abs(pts[0].rms_db - self.target_db)
            for a, b in zip(pts, pts[1:]):
                if (a.rms_db - self.target_db) * (b.rms_db - self.target_db) <= 0 and abs(b.rms_db - a.rms_db) > 1e-3:
                    t = (self.target_db - a.rms_db) / (b.rms_db - a.rms_db)
                    self.recommended = min(100, max(0, int(round(a.value + t * (b.value - a.value)))))
                    self.recommended_is_knee = False
                    return
                err = abs(b.rms_db - self.target_db)
                if err < best_err:
                    best_err, best = err, b.value
            self.recommended = best
            self.recommended_is_knee = False
            return

        first, last = pts[0], pts[-1]
        dx = last.value - first.value
        dy = last.rms_db - first.rms_db
        length = math.hypot(dx, dy)
        if length < 1e-6:
            return
        knee, max_dist = first.value, -1.0
        for p in pts:
            dist = abs(dy * (p.value - first.value) - dx * (p.rms_db - first.rms_db)) / length
            if dist > max_dist:
                max_dist, knee = dist, p.value
        self.recommended = min(100, max(0, knee))
        self.recommended_is_knee = True
