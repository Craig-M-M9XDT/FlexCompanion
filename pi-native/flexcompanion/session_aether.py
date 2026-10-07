"""AetherSDR shared-pan preference for a radio session (port of RadioViewModel.Audio part 2/3/5).

When a patched AetherSDR on this machine is already receiving the radio's panadapter FFT, the
compact spectrum re-slices those frames and Companion releases its own DAX IQ stream. If no
frame arrives within 1.2 s of the spectrum being needed, or the frames stop, it falls back to
DAX IQ automatically.
"""
from __future__ import annotations

import time
from typing import Optional

from . import kv, spectrum
from .aether import AetherPanBridge, PanFrame, normalize_serial

AETHER_STATUS = "Aether shared pan · radio FFT · no extra DAX IQ stream"
PROBE_S = 1.2
FALLBACK_CHECK_S = 0.5


class AetherMixin:
    def _init_aether(self, enabled: bool = True) -> None:
        self.aether_enabled = enabled
        self._bridge: Optional[AetherPanBridge] = None
        self._aether_pending = False
        self._aether_seq = 0
        self.radio_serial = ""
        self._fft_enabled_at = 0.0
        self._aether_timer = None

    # ── lifecycle ──
    def _start_aether(self) -> None:
        if not self.aether_enabled:
            return
        self._bridge = AetherPanBridge.instance()
        self._bridge.subscribe(self._on_aether_frame)
        self._fft_enabled_at = time.monotonic()
        self._schedule_fallback_check()

    def _stop_aether(self) -> None:
        if self._bridge is not None:
            self._bridge.unsubscribe(self._on_aether_frame)
        if self._aether_timer is not None:
            self._aether_timer.cancel()
            self._aether_timer = None
        self._aether_pending = False
        self._aether_seq = 0

    def _mark_fft_wanted(self) -> None:
        """Spectrum switched on / slice changed: give Aether a moment before opening DAX IQ."""
        self._fft_enabled_at = time.monotonic()
        self._aether_seq = 0

    # ── state ──
    @property
    def aether_active(self) -> bool:
        return self._fresh_aether_frame() is not None

    def _fresh_aether_frame(self) -> Optional[PanFrame]:
        b = self._bridge
        s = self.selected_slice
        if b is None or not self.show_fft or s is None or not self.radio_serial:
            return None
        sid = kv.parse_hex_id(s.pan)
        return b.latest(self.radio_serial, sid) if sid else None

    @property
    def _aether_probing(self) -> bool:
        return (self._bridge is not None and self._fft_enabled_at > 0
                and time.monotonic() - self._fft_enabled_at < PROBE_S)

    # ── frames ──
    def _on_aether_frame(self, frame: PanFrame) -> None:
        """Bridge thread: coalesce into one dispatcher update with the latest frame."""
        if not self.connected or not self.show_fft or frame.serial != normalize_serial(self.radio_serial):
            return
        if self._aether_pending:
            return
        self._aether_pending = True
        epoch = self._epoch

        def apply():
            self._aether_pending = False
            if epoch != self._epoch:
                return
            f = self._fresh_aether_frame()
            if f is not None:
                self._apply_aether_frame(f)
        self.d.post(apply)

    def _apply_aether_frame(self, f: PanFrame) -> None:
        if f.sequence == self._aether_seq:
            return
        self._aether_seq = f.sequence
        if self._iq_stream_id:
            self.ensure_iq_stream()              # releases our DAX IQ stream while Aether serves
        s = self.selected_slice
        pan = self.pans.get(s.pan) if s else None
        frame = spectrum.reslice_pan(
            f.bins,
            kv.to_float(pan.get("center")) if pan is not None else None,
            kv.to_float(pan.get("bandwidth")) if pan is not None else None,
            s.freq_mhz if s else None,
            self._window_full_span())
        if s is not None:
            spectrum.filter_fractions(frame, s.state.get("filter_lo"), s.state.get("filter_hi"))
        self._spec_avg = None
        self.spectrum_frame = frame
        self.spectrum_status = AETHER_STATUS
        self._notify("spectrum")

    def _window_full_span(self) -> spectrum.SpectrumWindow:
        # Aether frames add no network load, so Network saver doesn't cap their span.
        s = self.selected_slice
        st = s.state if s else kv.CIDict()
        return spectrum.window_for(s.mode if s else "", st.get("filter_lo"), st.get("filter_hi"),
                                   self.fft_span_khz * 1000.0)

    def _schedule_fallback_check(self) -> None:
        epoch = self._epoch

        def check():
            self._aether_timer = None
            if epoch != self._epoch or not self.connected:
                return
            if self.show_fft and not self._iq_busy and not self._iq_lost:
                fresh = self.aether_active
                if (not fresh and not self._iq_stream_id and not self._aether_probing) \
                        or (fresh and self._iq_stream_id):
                    self.ensure_iq_stream()
            self._aether_timer = self.d.call_later(FALLBACK_CHECK_S, check)
        self._aether_timer = self.d.call_later(FALLBACK_CHECK_S, check)
