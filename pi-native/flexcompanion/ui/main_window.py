"""Main window: header, radio discovery, RADIO A / RADIO B / STATION tabs.

Responsive like the Avalonia build: on wide screens the radio list and quick controls
sit in a sidebar; below 1000 px wide (e.g. the 800x480 Pi touchscreen) the sidebar
folds into a RADIOS tab so each tab gets the full screen.
"""
from __future__ import annotations

from pathlib import Path
from typing import Dict, List

from PySide6.QtCore import QEvent, QObject, Qt
from PySide6.QtGui import QIcon, QPixmap
from PySide6.QtWidgets import (QApplication, QCheckBox, QSizePolicy, QHBoxLayout, QLineEdit, QListWidget, QListWidgetItem,
                               QMainWindow, QTabWidget, QVBoxLayout, QWidget)

from .. import __version__, _core
from ..discovery import Discovery, RadioInfo
from ..session import RadioSession
from ..settings import Settings
from ..station import StationController
from . import theme
from .qt_dispatch import QtDispatcher
from .radio_panel import RadioPanel
from .station_panel import QuickControls, StationPanel
from .widgets import button, card, label

ASSETS = Path(__file__).resolve().parent / "assets"
COMPACT_BELOW = 1000
TOUCH_SCALE = 1.35


class RadiosPanel(QWidget):
    """Discovered radios + manual IP, with Connect A / Connect B."""

    def __init__(self, win: "MainWindow", parent=None):
        super().__init__(parent)
        self.win = win
        lay = QVBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.setSpacing(6)
        lay.addWidget(label("RADIOS", "section"))
        self.state = label("", "dim")
        self.state.setWordWrap(True)
        lay.addWidget(self.state)
        self.list = QListWidget()
        self.list.setMinimumHeight(110)
        self.list.itemDoubleClicked.connect(lambda _i: self._connect("A"))
        lay.addWidget(self.list, 1)
        row = QHBoxLayout()
        self.ca = button("Connect A", role="primary")
        self.ca.clicked.connect(lambda: self._connect("A"))
        self.cb = button("Connect B")
        self.cb.clicked.connect(lambda: self._connect("B"))
        rescan = button("↻")
        rescan.setToolTip("Rescan the LAN")
        rescan.clicked.connect(win.discovery.rescan)
        row.addWidget(self.ca, 1)
        row.addWidget(self.cb, 1)
        row.addWidget(rescan)
        lay.addLayout(row)
        mrow = QHBoxLayout()
        self.ip = QLineEdit(win.settings.manual_ip)
        self.ip.setPlaceholderText("Manual IP or IP:port")
        self.ip.textChanged.connect(self._ip_changed)
        mrow.addWidget(self.ip, 1)
        for slot in ("A", "B"):
            b = button(slot)
            b.setToolTip(f"Connect the manual IP to radio slot {slot}")
            b.clicked.connect(lambda _=False, slot=slot: win.connect_manual(self.ip.text(), slot))
            mrow.addWidget(b)
        lay.addLayout(mrow)
        self.refresh()

    def _ip_changed(self, text: str) -> None:
        self.win.settings.manual_ip = text
        for p in self.win.radios_panels:
            if p is not self and p.ip.text() != text:
                p.ip.blockSignals(True)
                p.ip.setText(text)
                p.ip.blockSignals(False)

    def _connect(self, slot: str) -> None:
        it = self.list.currentItem()
        if it is None and self.list.count() == 1:
            it = self.list.item(0)
        if it is not None:
            self.win.connect_radio(it.data(Qt.UserRole), slot)

    def refresh(self) -> None:
        current = self.list.currentItem().data(Qt.UserRole).serial if self.list.currentItem() else None
        self.list.clear()
        for r in self.win.discovery.radios:
            it = QListWidgetItem(f"{r.title}\n{r.detail}")
            it.setData(Qt.UserRole, r)
            self.list.addItem(it)
            if r.serial == current:
                self.list.setCurrentItem(it)
        d = self.win.discovery
        self.state.setText(d.error or ("Listening for FLEX radios on UDP 4992…" if not self.list.count()
                                       else f"{self.list.count()} radio(s) found. Pick one, then Connect A or B."))
        has = self.list.count() > 0
        self.ca.setEnabled(has)
        self.cb.setEnabled(has)


class _TouchWatcher(QObject):
    def __init__(self, win: "MainWindow"):
        super().__init__(win)
        self.win = win

    def eventFilter(self, obj, ev) -> bool:
        if ev.type() == QEvent.TouchBegin and not self.win.touch_box.isChecked():
            self.win.touch_box.setChecked(True)     # first real touch switches to large targets
        return False


class MainWindow(QMainWindow):
    def __init__(self, settings: Settings, touch: bool = False, kiosk: bool = False):
        super().__init__()
        self.settings = settings
        self.dispatcher = QtDispatcher()
        self.setWindowTitle("Flex Control Companion")
        icon = ASSETS / "FlexCompanionIcon.png"
        if icon.exists():
            self.setWindowIcon(QIcon(str(icon)))
        self.resize(1180, 820)

        self.sessions: Dict[str, RadioSession] = {}
        for slot in ("A", "B"):
            p = settings.slot(slot)
            self.sessions[slot] = RadioSession(slot, self.dispatcher, saver=settings.network_saver,
                                               tx_meter=p.tx_meter, show_fft=p.show_fft, fft_span_khz=p.fft_span_khz)
            self.sessions[slot].subscribe(lambda kind, slot=slot: self._session_event(slot, kind))
        self.station = StationController(lambda s: self.sessions[s], settings.station_target_slot, settings.macros)
        self.discovery = Discovery(on_change=lambda: self.dispatcher.post(self._discovery_changed))

        central = QWidget()
        self.setCentralWidget(central)
        root = QVBoxLayout(central)
        root.setContentsMargins(8, 8, 8, 8)
        root.setSpacing(8)

        # ── header ──
        head = QHBoxLayout()
        if icon.exists():
            pic = label()
            pic.setPixmap(QPixmap(str(icon)).scaled(34, 34, Qt.KeepAspectRatio, Qt.SmoothTransformation))
            head.addWidget(pic)
        tbox = QVBoxLayout()
        tbox.setSpacing(0)
        tbox.addWidget(label("FLEX CONTROL COMPANION", "title"))
        self.subtitle = label(f"Native Pi / Linux build · v{__version__} · {_core.BACKEND_NAME}", "dim")
        tbox.addWidget(self.subtitle)
        head.addLayout(tbox)
        self.disc_text = label("", "dim")
        self.disc_text.setSizePolicy(QSizePolicy.Ignored, QSizePolicy.Preferred)
        head.addWidget(self.disc_text, 1, Qt.AlignRight | Qt.AlignVCenter)
        self.dual_box = QCheckBox("Dual")
        self.dual_box.setChecked(settings.dual_mode)
        self.dual_box.toggled.connect(self._dual)
        self.saver_box = QCheckBox("Saver")
        self.saver_box.setToolTip("Network saver: caps Companion's DAX IQ at 24 kHz for Wi-Fi / VPN / dual-radio use")
        self.saver_box.setChecked(settings.network_saver)
        self.saver_box.toggled.connect(self._saver)
        self.touch_box = QCheckBox("Touch")
        self.touch_box.toggled.connect(self._touch)
        self.kiosk_box = QCheckBox("Kiosk")
        self.kiosk_box.toggled.connect(self._kiosk)
        for b in (self.dual_box, self.saver_box, self.touch_box, self.kiosk_box):
            head.addWidget(b)
        root.addLayout(head)

        # ── body: sidebar + tabs ──
        body = QHBoxLayout()
        body.setSpacing(8)
        self.sidebar, sl = card()
        self.sidebar.setFixedWidth(330)
        self.radios_panels: List[RadiosPanel] = []
        side_radios = RadiosPanel(self)
        self.radios_panels.append(side_radios)
        sl.addWidget(side_radios, 1)
        sl.addWidget(label("QUICK RADIO", "section"))
        sl.addWidget(QuickControls(self.station, ["160", "80", "40", "20", "17", "15", "10", "6"], cols=4))
        self.side_status = label("", "dim")
        self.side_status.setWordWrap(True)
        sl.addWidget(self.side_status)
        self.station.subscribe(lambda: self.side_status.setText(self.station.status))
        body.addWidget(self.sidebar)

        self.tabs = QTabWidget()
        self.tabs.tabBar().setExpanding(False)
        self.panel_a = RadioPanel(self.sessions["A"], settings.slot("A"))
        self.panel_b = RadioPanel(self.sessions["B"], settings.slot("B"))
        self.station_panel = StationPanel(self.station, lambda s: self.sessions[s])
        self.radios_tab, rl = card()
        tab_radios = RadiosPanel(self)
        self.radios_panels.append(tab_radios)
        rl.addWidget(tab_radios)
        self._compact = None
        self._closing = False
        body.addWidget(self.tabs, 1)
        root.addLayout(body, 1)

        self._rebuild_tabs()
        self._discovery_changed()
        self._watcher = _TouchWatcher(self)
        QApplication.instance().installEventFilter(self._watcher)
        if touch:
            self.touch_box.setChecked(True)
        if kiosk:
            self.kiosk_box.setChecked(True)
        self.discovery.start()
        self._discovery_changed()
        self._auto_reconnect()

    # ───────────────────────── layout ─────────────────────────

    def _rebuild_tabs(self) -> None:
        compact = self.width() < COMPACT_BELOW
        current = self.tabs.currentWidget()
        self._compact = compact
        self.sidebar.setVisible(not compact)
        self.subtitle.setVisible(not compact)
        self.disc_text.setVisible(not compact)
        self.tabs.clear()
        if compact:
            self.tabs.addTab(self.radios_tab, "RADIOS")
        self.tabs.addTab(self.panel_a, "RADIO A")
        if self.dual_box.isChecked():
            self.tabs.addTab(self.panel_b, "RADIO B")
        self.tabs.addTab(self.station_panel, "STATION")
        i = self.tabs.indexOf(current) if current is not None else -1
        self.tabs.setCurrentIndex(i if i >= 0 else (1 if compact else 0))
        self._tab_titles()

    def _tab_titles(self) -> None:
        for slot, panel in (("A", self.panel_a), ("B", self.panel_b)):
            i = self.tabs.indexOf(panel)
            if i >= 0:
                s = self.sessions[slot]
                dot = " ●" if s.connected else ""
                self.tabs.setTabText(i, f"RADIO {slot}{dot}")

    def resizeEvent(self, ev) -> None:
        super().resizeEvent(ev)
        if (self.width() < COMPACT_BELOW) != self._compact:
            self._rebuild_tabs()

    # ───────────────────────── toggles ─────────────────────────

    def _dual(self, on: bool) -> None:
        self.settings.dual_mode = on
        self._rebuild_tabs()

    def _saver(self, on: bool) -> None:
        self.settings.network_saver = on
        for s in self.sessions.values():
            s.set_saver(on)

    def _touch(self, on: bool) -> None:
        theme.apply(QApplication.instance(), TOUCH_SCALE if on else 1.0)

    def _kiosk(self, on: bool) -> None:
        if on:
            self.showFullScreen()
        else:
            self.showNormal()

    # ───────────────────────── radios ─────────────────────────

    def _discovery_changed(self) -> None:
        radios = self.discovery.radios
        self.disc_text.setText(self.discovery.error or (
            "Listening for FLEX radios on UDP 4992" if not radios else f"{len(radios)} radio(s) found"))
        for p in self.radios_panels:
            p.refresh()

    def connect_radio(self, r: RadioInfo, slot: str) -> None:
        self._connect(slot, r.ip, r.port, r.title, r.serial)

    def connect_manual(self, text: str, slot: str) -> None:
        text = text.strip()
        if not text:
            return
        host, port = text, 4992
        if text.count(":") == 1:
            h, _, p = text.rpartition(":")
            if p.isdigit():
                host, port = h, int(p)
        self._connect(slot, host, port, host, "")

    def _connect(self, slot: str, host: str, port: int, title: str, serial: str) -> None:
        if slot == "B" and not self.dual_box.isChecked():
            self.dual_box.setChecked(True)
        self.settings.remember_radio(slot, host, port, title, serial)
        self.sessions[slot].connect(host, port, title, serial)
        self.tabs.setCurrentWidget(self.panel_a if slot == "A" else self.panel_b)

    def _auto_reconnect(self) -> None:
        for slot, r in self.settings.last_radio.items():
            if slot in self.sessions and isinstance(r, dict) and r.get("Host"):
                if slot == "B" and not self.settings.dual_mode:
                    continue
                self.sessions[slot].connect(str(r["Host"]), int(r.get("Port", 4992)),
                                            str(r.get("Title", "")), str(r.get("Serial", "")))

    def _session_event(self, slot: str, kind: str) -> None:
        if kind == "connection":
            self._tab_titles()
            s = self.sessions[slot]
            if not self._closing and not s.connected and not s.connecting and not s._want_connected and s.status_text == "Not connected":
                self.settings.forget_radio(slot)     # user pressed Disconnect: don't auto-connect next start

    def closeEvent(self, ev) -> None:
        self._closing = True
        self.settings.station_target_slot = self.station.target_slot
        self.settings.save()
        self.discovery.stop()
        for s in self.sessions.values():
            s.shutdown()
        super().closeEvent(ev)
