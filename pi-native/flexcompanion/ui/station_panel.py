"""Station tab and the compact quick-controls block used in the sidebar."""
from __future__ import annotations

from typing import Callable, List

from PySide6.QtCore import Qt
from PySide6.QtWidgets import QComboBox, QHBoxLayout, QLineEdit, QPushButton, QScrollArea, QVBoxLayout, QWidget

from ..session import RadioSession
from ..station import BANDS, MODES, StationController
from .widgets import button, card, grid, label


class QuickControls(QWidget):
    """Band / mode / ATU / TUNE / MOX buttons bound to a StationController."""

    def __init__(self, station: StationController, bands: List[str] = BANDS, modes: List[str] = MODES[:4],
                 cols: int = 6, parent=None):
        super().__init__(parent)
        self.st = station
        lay = QVBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.setSpacing(6)
        band_btns = []
        for b in bands:
            btn = button(b)
            btn.clicked.connect(lambda _=False, b=b: station.set_band(b))
            band_btns.append(btn)
        lay.addWidget(grid(band_btns, cols))
        mode_btns = []
        for md in modes:
            btn = button(md)
            btn.clicked.connect(lambda _=False, md=md: station.set_mode(md))
            mode_btns.append(btn)
        lay.addWidget(grid(mode_btns, min(cols, 4)))
        self.atu = button("ATU")
        self.atu.clicked.connect(station.atu_start)
        self.byp = button("BYP")
        self.byp.clicked.connect(station.atu_bypass)
        self.tune = button("TUNE", checkable=True, role="danger")
        self.tune.clicked.connect(lambda: station.toggle_tune())
        self.mox = button("MOX", checkable=True, role="danger")
        self.mox.clicked.connect(lambda: station.toggle_mox())
        lay.addWidget(grid([self.atu, self.byp, self.tune, self.mox], 4))
        station.subscribe(self.refresh)
        self.refresh()

    def refresh(self) -> None:
        self.tune.setChecked(self.st.tune_on)
        self.mox.setChecked(self.st.mox_on)


class StationPanel(QScrollArea):
    def __init__(self, station: StationController, radio_for_slot: Callable[[str], RadioSession], parent=None):
        super().__init__(parent)
        self.st = station
        self._radio_for_slot = radio_for_slot
        self.setWidgetResizable(True)
        self.setHorizontalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        body = QWidget()
        body.setObjectName("body")
        self.setWidget(body)
        root = QVBoxLayout(body)
        root.setContentsMargins(8, 8, 8, 8)
        root.setSpacing(8)

        # target slot
        tcard, tl = card("Acts on")
        row = QHBoxLayout()
        self.slot_btns = {}
        for slot in ("A", "B"):
            b = button(f"RADIO {slot}", checkable=True)
            b.clicked.connect(lambda _=False, slot=slot: self._target(slot))
            self.slot_btns[slot] = b
            row.addWidget(b)
        self.target_info = label("", "dim")
        row.addWidget(self.target_info, 1)
        tl.addLayout(row)
        root.addWidget(tcard)

        qcard, ql = card("Quick radio controls")
        ql.addWidget(QuickControls(station, BANDS, MODES))
        self.status = label("", "dim")
        self.status.setWordWrap(True)
        ql.addWidget(self.status)
        root.addWidget(qcard)

        # amplifier
        acard, al = card("Amplifier")
        arow = QHBoxLayout()
        self.amp_info = label("No FLEX-reported amplifier", "dim")
        arow.addWidget(self.amp_info, 1)
        self.amp_btn = button("OPERATE")
        self.amp_btn.clicked.connect(station.toggle_amplifier)
        arow.addWidget(self.amp_btn)
        al.addLayout(arow)
        self.licence = label("", "dim")
        self.licence.setWordWrap(True)
        al.addWidget(self.licence)
        root.addWidget(acard)

        # profiles
        pcard, pl = card("Profiles")
        prow = QHBoxLayout()
        self.ptype = QComboBox()
        self.ptype.addItems(["global", "tx", "mic"])
        prow.addWidget(self.ptype)
        self.pname = QLineEdit()
        self.pname.setPlaceholderText("Profile name")
        prow.addWidget(self.pname, 1)
        load = button("LOAD", role="primary")
        load.clicked.connect(lambda: station.load_profile(self.ptype.currentText(), self.pname.text()))
        prow.addWidget(load)
        pl.addLayout(prow)
        root.addWidget(pcard)

        # macros
        mcard, ml = card("Macros")
        btns = []
        for mac in station.macros:
            b = button(mac["Label"])
            b.setToolTip(mac["Commands"])
            b.clicked.connect(lambda _=False, c=mac["Commands"]: station.run_macro(c))
            btns.append(b)
        ml.addWidget(grid(btns, 4))
        ml.addWidget(label("Edit macros in ~/.config/FlexCompanion/settings.json (shared with the .NET build). "
                           "One FLEX command per line; @mode X and @band N shortcuts.", "dim"))
        ml.itemAt(ml.count() - 1).widget().setWordWrap(True)
        root.addWidget(mcard)

        # raw command
        rcard, rl = card("Raw FLEX command")
        rrow = QHBoxLayout()
        self.raw = QLineEdit()
        self.raw.setPlaceholderText("e.g. slice set 0 agc_mode=fast")
        self.raw.returnPressed.connect(self._send_raw)
        rrow.addWidget(self.raw, 1)
        send = button("SEND", role="primary")
        send.clicked.connect(self._send_raw)
        rrow.addWidget(send)
        rl.addLayout(rrow)
        root.addWidget(rcard)
        root.addStretch(1)

        station.subscribe(self.refresh)
        for slot in ("A", "B"):
            radio_for_slot(slot).subscribe(lambda kind: kind in ("amplifier", "connection") and self.refresh())
        self.refresh()

    def _target(self, slot: str) -> None:
        self.st.set_target(slot)

    def _send_raw(self) -> None:
        text = self.raw.text().strip()
        if text:
            self.st.send(text)

    def refresh(self) -> None:
        st = self.st
        for slot, b in self.slot_btns.items():
            b.setChecked(slot == st.target_slot)
        r = st.radio
        self.target_info.setText(r.radio_title if r.connected else f"Radio {st.target_slot} is not connected")
        self.status.setText(st.status)
        if r.amp_handle:
            self.amp_info.setText(f"{r.amp_model or 'Amplifier'} · {r.amp_state}" + (f" · {r.amp_ip}" if r.amp_ip else ""))
        else:
            self.amp_info.setText("No FLEX-reported amplifier")
        self.amp_btn.setEnabled(bool(r.amp_handle))
        self.amp_btn.setText("STANDBY" if r.amp_operate else "OPERATE")
        self.licence.setText(r.license_summary if r.connected else "")
