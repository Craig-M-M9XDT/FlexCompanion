"""DSP control rows: an on/off toggle and/or a level mapped to radio keys (port of ParamControl.cs).

Set-keys and status-keys differ because SmartSDR+ features are written with one name
and reported with another (e.g. set ``lms_nr``, status ``nrl``).
"""
from __future__ import annotations

import math
import time
from typing import Callable, Dict, List, Optional

from . import kv
from .dispatch import Dispatcher, TimerHandle

THROTTLE_S = 0.070          # at most one level write per 70 ms while a slider moves
ECHO_GUARD_S = 0.600        # ignore radio level echoes this soon after a local change


class ParamControl:
    def __init__(
        self,
        label: str,
        tooltip: str = "",
        scope: str = "slice",
        toggle_set: Optional[str] = None,
        toggle_status: Optional[str] = None,
        level_set: Optional[str] = None,
        level_status: Optional[str] = None,
        lo: float = 0,
        hi: float = 100,
        step: float = 1,
        default_level: float = 50,
        requires_report: bool = True,
        toggle_format: Callable[[bool], str] = lambda b: "1" if b else "0",
        level_format: Callable[[float], str] = lambda v: str(int(round(v))),
        level_parse: Callable[[str], Optional[float]] = kv.to_float,
        level_display: Callable[[float], str] = lambda v: f"{v:.0f}",
    ) -> None:
        self.label = label
        self.tooltip = tooltip
        self.scope = scope
        self.toggle_set = toggle_set
        self.toggle_status = toggle_status
        self.level_set = level_set
        self.level_status = level_status
        self.min = lo
        self.max = hi
        self.step = step
        self.default_level = default_level
        self.requires_report = requires_report
        self.toggle_format = toggle_format
        self.level_format = level_format
        self.level_parse = level_parse
        self.level_display = level_display

        # Wired by the session.
        self.sender: Optional[Callable[[str, str], None]] = None
        self.dispatcher: Optional[Dispatcher] = None
        self.on_change: Optional[Callable[["ParamControl"], None]] = None

        self.is_on = False
        self.level = default_level
        self._reported = False
        self._unsupported = False
        self._temporary = False
        self.temporary_reason = ""
        self._last_sent_level = math.nan
        self._last_local_change = 0.0
        self._throttle: Optional[TimerHandle] = None
        self._last_send_time = 0.0

    # ───────────────────────── capability state ─────────────────────────

    @property
    def has_toggle(self) -> bool:
        return self.toggle_set is not None

    @property
    def has_level(self) -> bool:
        return self.level_set is not None

    @property
    def is_available(self) -> bool:
        return not self._unsupported and not self._temporary and (self._reported or not self.requires_report)

    @property
    def is_temporarily_unavailable(self) -> bool:
        return self._temporary

    @property
    def unavailable_text(self) -> str:
        if self._temporary and self.temporary_reason:
            return self.temporary_reason
        if self._unsupported:
            return "Unsupported by this radio / firmware for this connection."
        return "Not reported by this radio (model, licence or firmware), so it can't be changed here."

    @property
    def level_text(self) -> str:
        return self.level_display(self.level) if self.has_level else ""

    def uses(self, key: str) -> bool:
        k = key.lower()
        return k == (self.toggle_set or "").lower() or k == (self.level_set or "").lower()

    def mark_unsupported(self) -> None:
        """Radio rejected the key as unknown (0x5000002D): dim until the next connect."""
        if self._unsupported:
            return
        self._unsupported = True
        self._cancel_throttle()
        self.is_on = False
        self._changed()

    def clear_unsupported(self) -> None:
        if self._unsupported:
            self._unsupported = False
            self._changed()

    def set_temporary_unavailable(self, unavailable: bool, reason: str = "") -> None:
        reason = reason if unavailable else ""
        if self._temporary == unavailable and self.temporary_reason == reason:
            return
        self._temporary = unavailable
        self.temporary_reason = reason
        self._cancel_throttle()
        self._changed()

    # ───────────────────────── user actions ─────────────────────────

    def set_on(self, value: bool) -> None:
        if value == self.is_on:
            return
        self.is_on = value
        if self.toggle_set and self.is_available and self.sender:
            self.sender(self.scope, f"{self.toggle_set}={self.toggle_format(value)}")
        self._changed()

    def snap(self, value: float) -> float:
        v = round((value - self.min) / self.step) * self.step + self.min
        return min(self.max, max(self.min, v))

    def set_level(self, value: float) -> None:
        v = self.snap(value)
        if abs(v - self.level) < 1e-9:
            return
        self.level = v
        if self.level_set and self.is_available:
            self._last_local_change = time.monotonic()
            self._queue_level_send()
        self._changed()

    def _queue_level_send(self) -> None:
        """Send immediately, then at most every 70 ms while moving, then once more at rest."""
        if self._throttle is not None:
            return
        now = time.monotonic()
        if now - self._last_send_time >= THROTTLE_S:
            self._send_level()
        self._arm_throttle()

    def _arm_throttle(self) -> None:
        if self.dispatcher is None:
            return
        self._throttle = self.dispatcher.call_later(THROTTLE_S, self._throttle_tick)

    def _throttle_tick(self) -> None:
        self._throttle = None
        if abs(self.level - self._last_sent_level) > 1e-9 and self.is_available:
            self._send_level()
            self._arm_throttle()

    def _send_level(self) -> None:
        self._last_sent_level = self.level
        self._last_send_time = time.monotonic()
        if self.sender and self.level_set:
            self.sender(self.scope, f"{self.level_set}={self.level_format(self.level)}")

    def _cancel_throttle(self) -> None:
        if self._throttle is not None:
            self._throttle.cancel()
            self._throttle = None

    # ───────────────────────── radio status ─────────────────────────

    def apply_status(self, d: Dict[str, str]) -> None:
        """Apply a status update from the radio without echoing it back."""
        d = kv.as_dict(d)
        changed = False
        if self.toggle_status and self.toggle_status in d:
            self.is_on = kv.is_on(d[self.toggle_status])
            self._reported = True
            changed = True
        if self.level_status and self.level_status in d:
            if time.monotonic() - self._last_local_change > ECHO_GUARD_S:
                v = self.level_parse(d[self.level_status])
                if v is not None:
                    self.level = self.snap(v)
            self._reported = True
            changed = True
        if changed:
            self._changed()

    def reset(self) -> None:
        self.is_on = False
        self.level = self.default_level
        self._last_sent_level = math.nan
        self._cancel_throttle()
        self._temporary = False
        self.temporary_reason = ""
        self._reported = False
        self._changed()

    def _changed(self) -> None:
        cb = self.on_change
        if cb:
            cb(self)


def _slice(label: str, tip: str, t_set: str, t_status: str, l_set: Optional[str], l_status: Optional[str],
           requires_report: bool = True) -> ParamControl:
    return ParamControl(label, tip, "slice", t_set, t_status, l_set, l_status, requires_report=requires_report)


def build_controls() -> Dict[str, List[ParamControl]]:
    """The same control set as the .NET RadioViewModel constructor."""
    opt = "Sent optimistically; the radio is authoritative."
    return {
        "Noise reduction": [
            _slice("NR", "Legacy noise reduction", "nr", "nr", "nr_level", "nr_level"),
            _slice("NRF", f"Spectral subtraction filtering (FLEX-8000 / Aurora). {opt}", "nrf", "nrf", "nrf_level", "nrf_level", False),
            _slice("NRL", f"LMS noise reduction. {opt}", "lms_nr", "nrl", "lms_nr_level", "lms_nr_level", False),
            _slice("NRS", f"Spectral subtraction with voice detection (FLEX-8000 / Aurora). {opt}", "speex_nr", "nrs", "speex_nr_level", "speex_nr_level", False),
            _slice("RNN", "AI noise reduction (FLEX-8000 / Aurora). The radio has no level for RNN.", "rnnoise", "rnn", None, None, False),
        ],
        "Blanker": [
            _slice("NB", "Noise blanker", "nb", "nb", "nb_level", "nb_level"),
        ],
        "Notch": [
            _slice("ANF", "Legacy automatic notch filter", "anf", "anf", "anf_level", "anf_level"),
            _slice("ANFL", f"LMS automatic notch filter. {opt}", "lms_anf", "anfl", "lms_anf_level", "lms_anf_level", False),
            _slice("ANFT", "FFT automatic notch filter. The radio has no level for ANFT.", "anft", "anft", None, None, False),
        ],
        "Display": [
            ParamControl("FLOOR", f"Relative noise floor scaling for this slice's panadapter. {opt}", "pan",
                         "noise_floor_position_enable", "noise_floor_position_enable",
                         "noise_floor_position", "noise_floor_position", default_level=75, requires_report=False),
        ],
        "ESC / Diversity": [
            ParamControl("DIV", "Diversity: adds a child slice on the second SCU (dual-SCU radios only)",
                         toggle_set="diversity", toggle_status="diversity"),
            ParamControl("ESC", f"Enhanced Signal Clarity beam steering. Slider = phase in degrees. {opt}",
                         toggle_set="esc", toggle_status="esc", level_set="esc_phase_shift", level_status="esc_phase_shift",
                         lo=0, hi=360, step=5, default_level=0, requires_report=False,
                         toggle_format=lambda b: "on" if b else "off",
                         level_format=lambda deg: f"{math.radians(deg):.6f}",
                         level_parse=lambda s: None if kv.to_float(s) is None else math.degrees(kv.to_float(s)),
                         level_display=lambda deg: f"{deg:.0f}°"),
            ParamControl("GAIN", "ESC antenna balance. 1.00 = equal, below favours the main antenna",
                         level_set="esc_gain", level_status="esc_gain", lo=0, hi=2, step=0.05, default_level=1,
                         level_format=lambda v: f"{v:.3f}", level_display=lambda v: f"{v:.2f}"),
        ],
    }
