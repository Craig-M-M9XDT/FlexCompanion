"""Best AGC-T for a radio session (port of RadioViewModel.Audio parts 1/2/5/6).

Mixed into ``RadioSession``. Owns the Companion's DAX RX audio stream (opened only while a
calibration needs it), temporarily disables noise processing during the sweep, and restores
everything afterwards. Notifies ``agc`` when anything the AGC-T card shows changes.
"""
from __future__ import annotations

import math
import time
from typing import Dict, List, Optional, Tuple

from . import _core, kv
from .agc import AgcTCalibrator, AudioAnalyzer
from .client import error_text

# (status key, set key) of every filter that would distort the noise measurement
AGC_FILTER_KEYS: List[Tuple[str, str]] = [
    ("nr", "nr"), ("nrf", "nrf"), ("nrl", "lms_nr"), ("nrs", "speex_nr"), ("rnn", "rnnoise"),
    ("nb", "nb"), ("anf", "anf"), ("anfl", "lms_anf"), ("anft", "anft"),
]
FILTER_SETTLE_S = 0.4
AUDIO_PCCS = (_core.core.PCC_AUDIO_FLOAT, _core.core.PCC_AUDIO_INT16)


class AgcMixin:
    # ───────────────────────── setup / teardown ─────────────────────────

    def _init_agc(self, target_db: float = -28.0) -> None:
        self.audio = AudioAnalyzer()
        self.agc = AgcTCalibrator(self.d)
        self.agc.target_db = target_db
        self.agc.get_value = lambda: self.agc_value
        self.agc.is_off_mode = lambda: self._agc_key == "agc_off_level"
        self.agc.rms_db = lambda: self.audio.rms_db
        self.agc.apply_value = self._agc_apply
        self.agc.on_change = lambda: self._notify("agc")
        self.agc.on_finished = self._agc_finished
        self._agc_slice = -1
        self._agc_key = "agc_threshold"
        self._agc_waiting = False
        self._agc_preparing = False
        self._agc_epoch = 0
        self._agc_filters_changed = 0.0
        self._agc_snapshot: Dict[str, bool] = {}
        self._dax_stream_id = 0
        self._dax_channel = 0
        self._audio_busy = False
        self._audio_recheck = False
        self.audio_status = ""
        self.needs_dax = False

    def _cleanup_agc(self) -> None:
        """Called before the client is closed: put the radio back as it was, without waiting."""
        self._agc_epoch += 1
        self._agc_preparing = False
        c = self.client
        if (self.agc.running or self.agc.recommended >= 0) and self.agc.original_value >= 0 \
                and self._agc_slice >= 0 and c is not None:
            c.send_no_reply(f"slice set {self._agc_slice} {self._agc_key}={self.agc.original_value}")
        if self.agc.running:
            self.agc.running = False
            self.agc._cancel()
        self._agc_waiting = False
        self._restore_filters(no_reply=True)
        if self._dax_stream_id and c is not None:
            c.send_no_reply(f"stream remove 0x{self._dax_stream_id:08X}")
        self._dax_stream_id = self._dax_channel = 0
        self._audio_busy = self._audio_recheck = False
        self.audio.reset()
        self.agc.curve = []
        self.agc.recommended = -1
        self.agc.original_value = -1
        self.audio_status = ""
        self.needs_dax = False
        self._notify("agc")

    # ───────────────────────── read-outs ─────────────────────────

    @property
    def agc_mode(self) -> str:
        s = self.selected_slice
        return s.state.get("agc_mode", "") if s else ""

    @property
    def agc_is_off(self) -> bool:
        return self.agc_mode.lower() == "off"

    @property
    def agc_value(self) -> int:
        s = self.selected_slice
        if s is None:
            return 0
        key = "agc_off_level" if self.agc_is_off else "agc_threshold"
        return int(round(kv.to_float(s.state.get(key)) or 0))

    @property
    def agc_mode_text(self) -> str:
        s = self.selected_slice
        if s is None:
            return "No slice selected"
        if self.agc_is_off:
            return f"Slice {s.letter}: AGC off — the knob is a fixed gain; the sweep finds a comfortable noise level"
        return f"Slice {s.letter}: AGC {self.agc_mode.upper() or '?'} — the sweep finds the knee where noise just starts to drop"

    @property
    def agc_busy(self) -> bool:
        return self.agc.running or self._agc_waiting or self._agc_preparing

    @property
    def agc_result_text(self) -> str:
        if self._agc_waiting:
            return "Waiting for slice audio…"
        if self._agc_preparing:
            return "Temporarily disabling noise processing and letting the DSP settle…"
        if self.agc.running:
            return f"Sweeping… {self.agc.percent}%"
        if self.agc.recommended >= 0 and self.agc.original_value < 0:
            return f"AGC-T {self.agc.recommended} kept."
        if self.agc.recommended >= 0:
            kind = "knee" if self.agc.recommended_is_knee else "target level"
            return (f"Best AGC-T: {self.agc.recommended} ({kind}). It's applied now: Keep it, "
                    f"or Restore to go back to {self.agc.original_value}.")
        return "Tune to a clear spot with no signals, then press Find best AGC-T."

    @property
    def agc_warning(self) -> str:
        s = self.selected_slice
        if s is None:
            return ""
        if self.agc_busy:
            return ("Noise reduction / blanker / auto-notch are temporarily disabled for the AGC-T scan "
                    "and will be restored automatically.")
        on = sorted({sk.upper() for sk, _ in AGC_FILTER_KEYS if kv.is_on(s.state.get(sk))})
        return (f"Best AGC-T will temporarily disable {', '.join(on)} and restore them when the scan finishes."
                if on else "")

    # ───────────────────────── user actions ─────────────────────────

    def set_agc_value(self, value: int) -> None:
        s = self.selected_slice
        if s is None or self.agc.running:
            return
        value = min(100, max(0, int(value)))
        key = "agc_off_level" if self.agc_is_off else "agc_threshold"
        if self.agc_value == value:
            return
        s.state[key] = str(value)
        self.send(f"slice set {s.index} {key}={value}")
        self._notify("agc")

    def set_agc_target(self, db: float) -> None:
        self.agc.target_db = float(min(-6, max(-60, round(db))))
        self._notify("agc")

    def start_agc_sweep(self) -> None:
        s = self.selected_slice
        if s is None or self.client is None or self.agc_busy:
            return
        self._agc_epoch += 1
        self._restore_filters()
        self._agc_slice = s.index
        self._agc_key = "agc_off_level" if self.agc_is_off else "agc_threshold"
        self.agc.clear()
        self._suppress_filters(s)
        if self.audio.is_live and self._dax_channel and self._dax_channel == self._slice_dax_channel():
            self._begin_sweep()
        else:
            self._agc_waiting = True
            self._notify("agc")
            self.ensure_audio_stream()

    @property
    def agc_can_decide(self) -> bool:
        """A finished sweep is waiting for Keep or Restore."""
        return self.agc.recommended >= 0 and self.agc.original_value >= 0 and not self.agc.running

    def agc_keep(self) -> None:
        self.agc.keep()
        self._restore_filters()
        self._notify("agc")
        self._release_audio_if_unused()

    def agc_restore(self) -> None:
        self._agc_epoch += 1
        self._agc_preparing = False
        self._agc_waiting = False
        self.agc.stop()
        self._restore_filters()
        self._notify("agc")
        self._release_audio_if_unused()

    def assign_dax(self) -> None:
        """Give the selected slice a free DAX RX channel (1-8) so AGC-T has audio to analyse."""
        s = self.selected_slice
        if s is None:
            return
        used = set()
        for x in self.slices.values():
            if x is not s:
                try:
                    used.add(int(x.state.get("dax", "0")))
                except ValueError:
                    pass
        free = next((ch for ch in range(1, 9) if ch not in used), 0)
        if not free:
            self._set_message("All 8 DAX RX channels are in use by other slices.")
            return
        self.send(f"slice set {s.index} dax={free}")
        self.audio_status = f"Assigning DAX RX {free} to slice {s.letter}…"
        self._notify("agc")

    # ───────────────────────── sweep plumbing ─────────────────────────

    def _agc_apply(self, v: int) -> None:
        if self._agc_slice < 0:
            return
        v = min(100, max(0, v))
        self.send(f"slice set {self._agc_slice} {self._agc_key}={v}")
        s = self.slices.get(self._agc_slice)
        if s is not None:
            s.state[self._agc_key] = str(v)

    def _agc_finished(self) -> None:
        self._restore_filters()
        self._notify("agc")
        self._release_audio_if_unused()

    def _begin_sweep(self) -> None:
        if self._agc_preparing or self.agc.running:
            return
        self._agc_preparing = True
        epoch = self._agc_epoch
        self._notify("agc")
        remain = FILTER_SETTLE_S - (time.monotonic() - self._agc_filters_changed)

        def go():
            self._agc_preparing = False
            if epoch != self._agc_epoch or not self.connected or self._agc_slice < 0:
                self._notify("agc")
                return
            self.agc.start()
            self._notify("agc")
        self.d.call_later(max(0.0, remain), go)

    def _suppress_filters(self, s) -> None:
        self._agc_snapshot = {}
        for status_key, set_key in AGC_FILTER_KEYS:
            raw = s.state.get(status_key)
            if raw is None:
                continue
            was_on = kv.is_on(raw)
            self._agc_snapshot[set_key] = was_on
            if was_on:
                self.send(f"slice set {s.index} {set_key}=0")
        self._agc_filters_changed = time.monotonic()

    def _restore_filters(self, no_reply: bool = False) -> None:
        if not self._agc_snapshot or self._agc_slice < 0:
            return
        snap, self._agc_snapshot = self._agc_snapshot, {}
        for set_key, was_on in snap.items():
            if not was_on:
                continue           # only filters we switched off need switching back on
            cmd = f"slice set {self._agc_slice} {set_key}=1"
            if no_reply and self.client is not None:
                self.client.send_no_reply(cmd)
            else:
                self.send(cmd)

    def _agc_on_slice_changed(self) -> None:
        if self.agc_busy:
            self._agc_epoch += 1
            self._agc_preparing = self._agc_waiting = False
            self.agc.stop()
            self._restore_filters()
        elif self.agc.recommended >= 0:
            self.agc.keep()
            self.agc.clear()
        self._notify("agc")
        self.ensure_audio_stream()

    def _audio_tick(self) -> None:
        """Runs with every meter update: keeps the audio status current and starts a waiting sweep."""
        if self._dax_stream_id:
            text = (f"DAX RX {self._dax_channel} · AGC-T" if self.audio.is_live
                    else f"DAX RX {self._dax_channel}: waiting for audio…")
            if text != self.audio_status and not self.audio_status.startswith("Couldn't"):
                self.audio_status = text
                self._notify("agc")
        if self._agc_waiting and self.audio.is_live and not math.isnan(self.audio.rms_db):
            self._agc_waiting = False
            self._begin_sweep()

    # ───────────────────────── DAX RX stream ─────────────────────────

    def _slice_dax_channel(self) -> int:
        s = self.selected_slice
        try:
            return int(s.state.get("dax", "0")) if s else 0
        except ValueError:
            return 0

    @property
    def _audio_wanted(self) -> bool:
        return self.connected and self.agc_busy

    def _release_audio_if_unused(self) -> None:
        if not self._audio_wanted:
            self.ensure_audio_stream()

    def ensure_audio_stream(self) -> None:
        if self._audio_busy:
            self._audio_recheck = True
            return
        c = self.client
        if c is None:
            return
        self._audio_busy = True
        self._audio_recheck = False
        epoch = self._epoch
        plan = {"want": self._audio_wanted, "ch": self._slice_dax_channel(),
                "cur_id": self._dax_stream_id, "cur_ch": self._dax_channel,
                "letter": self.selected_slice.letter if self.selected_slice else "?"}
        self._pool.submit(self._audio_worker, epoch, c, plan)

    def _audio_worker(self, epoch: int, c, p: dict) -> None:
        r: dict = {}
        try:
            def remove():
                if p["cur_id"]:
                    c.send(f"stream remove 0x{p['cur_id']:08X}")
                r["set"] = (0, 0)

            if not p["want"]:
                if p["cur_id"]:
                    remove()
                r["status"], r["needs_dax"] = "", False
                return
            ch = p["ch"]
            if ch <= 0:
                if p["cur_id"]:
                    remove()
                r["needs_dax"] = True
                r["status"] = f"Slice {p['letter']} has no DAX RX channel, so AGC-T has no audio to analyse."
                return
            r["needs_dax"] = False
            if p["cur_id"] and p["cur_ch"] == ch:
                return
            if p["cur_id"]:
                remove()
            code, body = c.send(f"stream create type=dax_rx dax_channel={ch}")
            if code != 0:
                r["status"] = f"Couldn't open DAX RX {ch} ({error_text(code)})."
                return
            sid = kv.parse_hex_id(body)
            if not sid:
                r["status"] = f'Radio returned an unexpected DAX RX stream id "{body}".'
                return
            r["set"] = (sid, ch)
            r["status"] = f"DAX RX {ch} · AGC-T"
        finally:
            def finish():
                self._audio_busy = False
                if epoch != self._epoch:
                    return
                if "set" in r:
                    self._dax_stream_id, self._dax_channel = r["set"]
                    self.audio.reset()
                if "status" in r:
                    self.audio_status = r["status"]
                if "needs_dax" in r:
                    self.needs_dax = r["needs_dax"]
                self._notify("agc")
                if self._audio_recheck:
                    self.ensure_audio_stream()
            self.d.post(finish)

    def _on_audio_packet(self, sid: int, data: bytes) -> None:
        """UDP thread."""
        if sid and sid == self._dax_stream_id:
            parsed = _core.parse_audio(data)
            if parsed is not None:
                self.audio.push(parsed[1])
