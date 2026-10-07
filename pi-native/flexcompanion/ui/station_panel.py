"""Station tab and the compact quick-controls block used in the sidebar."""
from __future__ import annotations

from typing import Callable, List

from PySide6.QtCore import Qt
from PySide6.QtWidgets import (QCheckBox, QComboBox, QFrame, QHBoxLayout, QLineEdit, QListWidget, QListWidgetItem,
                               QPushButton, QScrollArea, QSizePolicy, QVBoxLayout, QWidget)

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

        # amplifier: FLEX-reported OPERATE / STANDBY + direct Power Genius XL telemetry
        acard, al = card("Amplifier")
        arow = QHBoxLayout()
        self.amp_info = label("No FLEX-reported amplifier", "dim")
        self.amp_info.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        arow.addWidget(self.amp_info, 1)
        self.amp_btn = button("OPERATE")
        self.amp_btn.clicked.connect(station.toggle_amplifier)
        arow.addWidget(self.amp_btn)
        al.addLayout(arow)
        prow2 = QHBoxLayout()
        self.pg_host = QLineEdit(station.settings.pgxl_host if station.settings else "")
        self.pg_host.setPlaceholderText("PGXL IP (blank = the radio's amplifier)")
        prow2.addWidget(self.pg_host, 1)
        self.pg_port = QLineEdit(str(station.settings.pgxl_port if station.settings else 9008))
        self.pg_port.setMaximumWidth(110)
        prow2.addWidget(self.pg_port)
        self.pg_connect = button("CONNECT", role="primary")
        self.pg_connect.clicked.connect(self._pgxl_connect)
        self.pg_disconnect = button("DISCONNECT")
        self.pg_disconnect.clicked.connect(station.pgxl_disconnect)
        prow2.addWidget(self.pg_connect)
        prow2.addWidget(self.pg_disconnect)
        al.addLayout(prow2)
        self.amp_tiles = {}
        tiles = []
        for key, title in (("pwr", "POWER"), ("swr", "SWR"), ("id", "CURRENT"), ("temp", "PA TEMP"),
                           ("vdd", "VDD"), ("vac", "MAINS")):
            box = QFrame()
            bl = QVBoxLayout(box)
            bl.setContentsMargins(0, 0, 0, 0)
            bl.setSpacing(0)
            bl.addWidget(label(title, "section"))
            val = label("—", "title")
            bl.addWidget(val)
            self.amp_tiles[key] = val
            tiles.append(box)
        al.addWidget(grid(tiles, 3))
        self.pg_status = label("", "dim")
        self.pg_status.setWordWrap(True)
        al.addWidget(self.pg_status)
        self.pg_alert = label("", "message")
        self.pg_alert.setWordWrap(True)
        al.addWidget(self.pg_alert)
        self.licence = label("", "dim")
        self.licence.setWordWrap(True)
        al.addWidget(self.licence)
        root.addWidget(acard)

        # DX cluster
        dcard, dl = card("DX cluster")
        drow = QHBoxLayout()
        st_ = station.settings
        self.dx_host = QLineEdit(st_.dx_host if st_ else "")
        self.dx_host.setPlaceholderText("Cluster host, e.g. dxc.example.org")
        drow.addWidget(self.dx_host, 1)
        self.dx_port = QLineEdit(str(st_.dx_port if st_ else 7300))
        self.dx_port.setMaximumWidth(110)
        drow.addWidget(self.dx_port)
        self.dx_call = QLineEdit(st_.dx_callsign if st_ else "")
        self.dx_call.setPlaceholderText("Your call")
        self.dx_call.setMaximumWidth(130)
        drow.addWidget(self.dx_call)
        dl.addLayout(drow)
        drow2 = QHBoxLayout()
        self.dx_connect = button("CONNECT", role="primary")
        self.dx_connect.clicked.connect(self._dx_connect)
        self.dx_disconnect = button("DISCONNECT")
        self.dx_disconnect.clicked.connect(station.dx_disconnect)
        drow2.addWidget(self.dx_connect)
        drow2.addWidget(self.dx_disconnect)
        self.dx_status = label("", "dim")
        self.dx_status.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        drow2.addWidget(self.dx_status, 1)
        dl.addLayout(drow2)
        self.dx_publish = QCheckBox("Show spots on the radio's panadapters (spot add)")
        self.dx_publish.setChecked(st_.publish_spots_to_radio if st_ else True)
        self.dx_publish.toggled.connect(self._publish_toggled)
        dl.addWidget(self.dx_publish)
        self.spot_list = QListWidget()
        self.spot_list.setMinimumHeight(170)
        self.spot_list.itemClicked.connect(self._spot_tapped)
        self.spot_list.setToolTip("Tap a spot to tune the target radio to it")
        dl.addWidget(self.spot_list)
        root.addWidget(dcard)

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
        station.subscribe_kind(self._on_kind)
        for slot in ("A", "B"):
            radio_for_slot(slot).subscribe(lambda kind: kind in ("amplifier", "connection") and self.refresh())
        self.refresh()

    def _on_kind(self, kind: str) -> None:
        if kind == "spots":
            self._refresh_spots()
        elif kind == "dx":
            self._refresh_dx()
        elif kind == "amp":
            self._refresh_amp()

    @staticmethod
    def _port(edit: QLineEdit, default: int) -> int:
        try:
            return min(65535, max(1, int(edit.text().strip())))
        except ValueError:
            return default

    def _dx_connect(self) -> None:
        self.st.dx_connect(self.dx_host.text(), self._port(self.dx_port, 7300), self.dx_call.text())

    def _pgxl_connect(self) -> None:
        self.st.pgxl_connect(self.pg_host.text(), self._port(self.pg_port, 9008))

    def _publish_toggled(self, on: bool) -> None:
        if self.st.settings is not None:
            self.st.settings.publish_spots_to_radio = on

    def _spot_tapped(self, item) -> None:
        spot = item.data(Qt.UserRole)
        if spot is not None:
            self.st.tune_spot(spot)

    def _refresh_spots(self) -> None:
        self.spot_list.clear()
        for sp in self.st.spots:
            de = f"   de {sp.spotter}" if sp.spotter else ""
            it = QListWidgetItem(f"{sp.frequency_text}   {sp.callsign}   {sp.utc}\n{sp.comment}{de}")
            it.setData(Qt.UserRole, sp)
            self.spot_list.addItem(it)

    def _refresh_dx(self) -> None:
        connected = self.st.dx.connected
        self.dx_status.setText(self.st.dx_status)
        self.dx_disconnect.setEnabled(connected or self.st.dx_status not in ("Not connected", "Disconnected"))

    def _refresh_amp(self) -> None:
        a = self.st.amp
        fmt = a.fmt
        vals = {"pwr": fmt(a.power_w, "W", 0), "swr": "—" if a.swr != a.swr else f"{a.swr:.2f}:1",
                "id": fmt(a.current_a, "A"), "temp": fmt(a.temp_c, "°C"), "vdd": fmt(a.vdd, "V"),
                "vac": fmt(a.vac, "V", 0)}
        for k, v in vals.items():
            self.amp_tiles[k].setText(v)
        self.pg_status.setText(f"Power Genius XL: {self.st.pgxl_status}")
        self.pg_alert.setText(self.st.amp_alert)
        self.pg_disconnect.setEnabled(self.st.pgxl.connected or self.st.pgxl_status not in ("Not connected", "Disconnected"))

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
        self._refresh_dx()
        self._refresh_amp()
