"""One radio slot: header, station/slice pickers, meter, spectrum and DSP controls."""
from __future__ import annotations

import math
from typing import Dict, List

from PySide6.QtCore import Qt
from PySide6.QtWidgets import (QCheckBox, QComboBox, QGridLayout, QHBoxLayout, QLabel, QScrollArea, QSizePolicy,
                               QVBoxLayout, QWidget)

from .. import meters as m
from ..session import CW_MODES, RadioSession
from ..settings import SlotPrefs
from ..spectrum import SPAN_OPTIONS_KHZ
from .agc_card import AgcCard
from .widgets import MeterBar, ParamRow, SpectrumView, button, card, label


class RadioPanel(QScrollArea):
    def __init__(self, session: RadioSession, prefs: SlotPrefs, parent=None):
        super().__init__(parent)
        self.s = session
        self.prefs = prefs
        self.setWidgetResizable(True)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        body = QWidget()
        body.setObjectName("body")
        self.setWidget(body)
        root = QVBoxLayout(body)
        root.setContentsMargins(8, 8, 8, 8)
        root.setSpacing(8)
        self._updating = False

        # ── header ──
        head, hl = card()
        top = QHBoxLayout()
        self.title = label("No radio", "title")
        self.title.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        top.addWidget(self.title, 1)
        self.reconnect_btn = button("Reconnect")
        self.reconnect_btn.clicked.connect(session.reconnect)
        self.disconnect_btn = button("Disconnect")
        self.disconnect_btn.clicked.connect(self._disconnect)
        top.addWidget(self.reconnect_btn)
        top.addWidget(self.disconnect_btn)
        hl.addLayout(top)
        self.status = label("", "dim")
        self.status.setWordWrap(True)
        hl.addWidget(self.status)
        pick = QGridLayout()
        pick.setHorizontalSpacing(8)
        pick.setVerticalSpacing(6)
        self.station_cb = QComboBox()
        self.station_cb.activated.connect(self._station_picked)
        self.slice_cb = QComboBox()
        self.slice_cb.activated.connect(self._slice_picked)
        for cb in (self.station_cb, self.slice_cb):
            cb.setSizeAdjustPolicy(QComboBox.AdjustToMinimumContentsLengthWithIcon)
            cb.setMinimumContentsLength(8)
            cb.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        self.follow = QCheckBox("Follow active")
        self.follow.setChecked(True)
        self.follow.toggled.connect(session.set_follow_active)
        self._pick = pick
        self._pick_labels = (label("Station", "dim"), label("Slice", "dim"))
        hl.addLayout(pick)
        self._pick_wide = None
        self._layout_pick(True)
        root.addWidget(head)

        # ── meter ──
        mcard, ml = card("Meter")
        self.meter = MeterBar()
        ml.addWidget(self.meter)
        row = QHBoxLayout()
        self.pwr = label("PWR 0 W")
        self.swr = label("SWR 1.0:1")
        self.extra = label("", "dim")
        for w in (self.pwr, self.swr, self.extra):
            row.addWidget(w)
        row.addStretch(1)
        row.addWidget(label("TX meter", "dim"))
        self.tx_cb = QComboBox()
        self.tx_cb.setSizeAdjustPolicy(QComboBox.AdjustToMinimumContentsLengthWithIcon)
        self.tx_cb.setMinimumContentsLength(6)
        self.tx_cb.addItems(m.TX_METER_OPTIONS)
        self.tx_cb.setCurrentText(session.tx_meter)
        self.tx_cb.currentTextChanged.connect(self._tx_meter)
        row.addWidget(self.tx_cb)
        ml.addLayout(row)
        root.addWidget(mcard)

        # ── spectrum ──
        scard, sl = card()
        srow = QHBoxLayout()
        sec = label("SPECTRUM", "section")
        srow.addWidget(sec)
        self.fft_btn = button("ON", checkable=True)
        self.fft_btn.setChecked(session.show_fft)
        self.fft_btn.toggled.connect(self._fft_toggled)
        srow.addWidget(self.fft_btn)
        self.span_cb = QComboBox()
        for k in SPAN_OPTIONS_KHZ:
            self.span_cb.addItem(f"{k:g} kHz", k)
        self.span_cb.setCurrentIndex(SPAN_OPTIONS_KHZ.index(session.fft_span_khz))
        self.span_cb.currentIndexChanged.connect(self._span)
        srow.addWidget(self.span_cb)
        self.spec_status = label("", "dim")
        self.spec_status.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        srow.addWidget(self.spec_status, 1)
        sl.addLayout(srow)
        self.spec = SpectrumView()
        self.spec.clicked.connect(self._spectrum_clicked)
        self.spec.setToolTip("Tap to tune the slice to that frequency")
        sl.addWidget(self.spec, 1)
        self.spec_card = scard
        root.addWidget(scard)

        # ── Best AGC-T ──
        self.agc_card = AgcCard(session, prefs)
        root.addWidget(self.agc_card)

        # ── DSP ──
        self.rows: List[ParamRow] = []
        self.dsp_cards: List[QWidget] = []
        for name, controls in session.groups.items():
            c, cl = card(name)
            for ctl in controls:
                r = ParamRow(ctl)
                self.rows.append(r)
                cl.addWidget(r)
            if name == "ESC / Diversity":
                self.esc_note = label("", "dim")
                self.esc_note.setWordWrap(True)
                cl.addWidget(self.esc_note)
            cl.addStretch(1)
            self.dsp_cards.append(c)
        cw, cwl = card("CW")
        self.autotune = button("CW AUTO TUNE")
        self.autotune.clicked.connect(session.auto_tune_once)
        cwl.addWidget(self.autotune)
        cwl.addWidget(label("Zero-beats the selected CW slice onto the strongest nearby signal.", "dim"))
        cwl.itemAt(cwl.count() - 1).widget().setWordWrap(True)
        cwl.addStretch(1)
        self.dsp_cards.append(cw)
        self.dsp_grid = QGridLayout()
        self.dsp_grid.setSpacing(8)
        root.addLayout(self.dsp_grid)
        self._cols = 0
        self._layout_dsp(2)

        self.message = label("", "message")
        self.message.setWordWrap(True)
        root.addWidget(self.message)
        root.addStretch(1)

        session.subscribe(self._on_event)
        self._refresh_all()

    # ───────────────────────── layout ─────────────────────────

    def _layout_dsp(self, cols: int) -> None:
        if cols == self._cols:
            return
        self._cols = cols
        for w in self.dsp_cards:
            self.dsp_grid.removeWidget(w)
        for i, w in enumerate(self.dsp_cards):
            self.dsp_grid.addWidget(w, i // cols, i % cols)
        for c in range(3):
            self.dsp_grid.setColumnStretch(c, 1 if c < cols else 0)

    def _layout_pick(self, wide: bool) -> None:
        """Station + slice side by side when there's room, stacked on narrow panels."""
        if wide == self._pick_wide:
            return
        self._pick_wide = wide
        g = self._pick
        st_l, sl_l = self._pick_labels
        for w in (st_l, self.station_cb, sl_l, self.slice_cb, self.follow):
            g.removeWidget(w)
        if wide:
            g.addWidget(st_l, 0, 0)
            g.addWidget(self.station_cb, 0, 1)
            g.addWidget(sl_l, 0, 2)
            g.addWidget(self.slice_cb, 0, 3)
            g.addWidget(self.follow, 0, 4)
            for c, st in enumerate((0, 1, 0, 1, 0)):
                g.setColumnStretch(c, st)
        else:
            g.addWidget(st_l, 0, 0)
            g.addWidget(self.station_cb, 0, 1, 1, 2)
            g.addWidget(sl_l, 1, 0)
            g.addWidget(self.slice_cb, 1, 1)
            g.addWidget(self.follow, 1, 2)
            for c, st in enumerate((0, 1, 0, 0, 0)):
                g.setColumnStretch(c, st)

    def _apply_short_screen(self) -> None:
        # On 480-pixel-high screens the radio name already says we're connected; save the line.
        short = self.viewport().height() < 520
        self.status.setVisible(not (short and self.s.connected))

    def resizeEvent(self, ev) -> None:
        super().resizeEvent(ev)
        self._apply_short_screen()
        w = self.viewport().width()
        self._layout_dsp(3 if w >= 1250 else 2 if w >= 640 else 1)
        self._layout_pick(w >= 900)

    # ───────────────────────── user actions ─────────────────────────

    def _disconnect(self) -> None:
        self.s.disconnect()

    def _station_picked(self, i: int) -> None:
        if 0 <= i < len(self.s.stations):
            self.s.select_station(self.s.stations[i])

    def _slice_picked(self, i: int) -> None:
        idx = self.slice_cb.itemData(i)
        if idx is not None:
            if self.follow.isChecked():
                self.follow.setChecked(False)   # a manual pick stops following the active slice
            self.s.select_slice(int(idx))

    def _tx_meter(self, key: str) -> None:
        if not self._updating:
            self.s.set_tx_meter(key)
            self.prefs.tx_meter = key

    def _fft_toggled(self, on: bool) -> None:
        self.fft_btn.setText("ON" if on else "OFF")
        if not self._updating:
            self.s.set_show_fft(on)
            self.prefs.show_fft = on

    def _span(self, i: int) -> None:
        if not self._updating and i >= 0:
            k = float(self.span_cb.itemData(i))
            self.s.set_fft_span(k)
            self.prefs.fft_span_khz = k

    def _spectrum_clicked(self, offset_hz: float) -> None:
        sl = self.s.selected_slice
        if sl is None or sl.freq_mhz is None:
            return
        target = sl.freq_mhz + offset_hz / 1e6
        step = 0.0001 if sl.mode.upper() not in CW_MODES else 0.00001    # 100 Hz, 10 Hz in CW
        self.s.tune(round(target / step) * step)

    # ───────────────────────── model -> view ─────────────────────────

    def _on_event(self, kind: str) -> None:
        if kind == "meters":
            self._refresh_meter()
        elif kind == "spectrum":
            self._refresh_spectrum()
        elif kind == "controls":
            for r in self.rows:
                r.refresh()
        elif kind == "message":
            self.message.setText(self.s.last_message)
        elif kind in ("slices", "selection"):
            self._refresh_slices()
            self._refresh_mode_bits()
        elif kind == "stations":
            self._refresh_stations()
        elif kind == "connection":
            self._refresh_all()

    def _refresh_all(self) -> None:
        s = self.s
        connected = s.connected
        if connected or s.connecting:
            self.title.setText(s.radio_title)
        else:
            self.title.setText(f"Slot {s.slot} is free")
        self.status.setText(s.status_text if (connected or s.connecting or s.status_text != "Not connected")
                            else f'Pick a radio and press "Connect {s.slot}", or type its IP.')
        self._apply_short_screen()
        self.reconnect_btn.setVisible(not connected and bool(s._last[0]) and not s.connecting)
        self.disconnect_btn.setEnabled(connected or s._retrying or s.connecting)
        for w in [self.station_cb, self.slice_cb, self.follow, self.tx_cb, self.fft_btn, self.span_cb, self.agc_card] + self.dsp_cards:
            w.setEnabled(connected)
        self.message.setText(s.last_message)
        self._refresh_stations()
        self._refresh_slices()
        self._refresh_mode_bits()
        for r in self.rows:
            r.refresh()
        self._refresh_meter()
        self._refresh_spectrum()

    def _refresh_stations(self) -> None:
        self._updating = True
        try:
            self.station_cb.clear()
            for st in self.s.stations:
                self.station_cb.addItem(st.name)
            if self.s.selected_station in self.s.stations:
                self.station_cb.setCurrentIndex(self.s.stations.index(self.s.selected_station))
        finally:
            self._updating = False

    def _refresh_slices(self) -> None:
        self._updating = True
        try:
            self.slice_cb.clear()
            for sl in self.s.ordered_slices:
                owner = f"   · {sl.station}" if sl.station else ""
                self.slice_cb.addItem(f"{sl}{owner}", sl.index)
            if self.s.selected_index is not None:
                i = self.slice_cb.findData(self.s.selected_index)
                if i >= 0:
                    self.slice_cb.setCurrentIndex(i)
            if self.follow.isChecked() != self.s.follow_active:
                self.follow.setChecked(self.s.follow_active)
        finally:
            self._updating = False

    def _refresh_mode_bits(self) -> None:
        sl = self.s.selected_slice
        mode = sl.mode.upper() if sl else ""
        self.autotune.setEnabled(self.s.connected and mode in CW_MODES)
        if hasattr(self, "esc_note"):
            self.esc_note.setText(
                "This is a diversity child slice. Select the parent slice to adjust ESC."
                if sl is not None and sl.is_diversity_child else
                "ESC needs a dual-SCU radio with DIV on. The radio decides whether the command is accepted.")

    def _refresh_meter(self) -> None:
        s = self.s
        r = s.readings
        spec = s.tx_spec
        sel = r.tx_value(spec.key)
        if r.transmitting:
            sub = f"SWR {r.swr:.1f}:1" if spec.key == "Power" else f"{r.fwd_watts:.0f} W"
            self.meter.set_state(sel, spec.min, spec.max, spec.red, spec.scale, spec.title, spec.fmt(sel), sub, True)
        else:
            dbm = r.rx_dbm
            self.meter.set_state(dbm, m.RX_MIN, m.RX_MAX, m.RX_RED, m.RX_SCALE, "S",
                                 m.s_text(dbm), "" if math.isnan(dbm) else f"{dbm:.0f} dBm", False)
        self.pwr.setText(f"PWR {r.fwd_watts:.0f} W")
        self.swr.setText(f"SWR {r.swr:.1f}:1")
        self.extra.setText("" if spec.key in ("Power", "SWR") else f"{spec.title.split()[0]} {spec.fmt(sel)}")

    def _refresh_spectrum(self) -> None:
        s = self.s
        self.spec_status.setText(s.spectrum_status)
        if not s.show_fft:
            self.spec.set_frame(None)
            self.spec.set_message("Spectrum off — press ON to open a DAX IQ stream for this slice's panadapter.")
            return
        self.spec.set_message(s.spectrum_status or ("Waiting for I/Q…" if s.connected else "Not connected"))
        self.spec.set_frame(s.spectrum_frame)
