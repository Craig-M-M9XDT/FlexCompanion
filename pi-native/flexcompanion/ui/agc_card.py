"""Best AGC-T card: value slider, sweep button, Keep / Restore, live curve."""
from __future__ import annotations

import math
from typing import Optional

from PySide6.QtCore import QPointF, QRectF, QSize, Qt
from PySide6.QtGui import QColor, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import QFrame, QHBoxLayout, QSizePolicy, QSlider, QVBoxLayout, QWidget

from ..session import RadioSession
from ..settings import SlotPrefs
from . import theme
from .widgets import button, label, scaled_font


class AgcCurveView(QWidget):
    """RMS (dB) against AGC-T value, with the recommended point marked."""

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.setMinimumHeight(90)
        self.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        self.points = []
        self.recommended = -1
        self.target_db = math.nan

    def sizeHint(self) -> QSize:
        return QSize(300, 100)

    def set_data(self, points, recommended: int, target_db: float) -> None:
        self.points = [(p.value, p.rms_db) for p in points]
        self.recommended = recommended
        self.target_db = target_db
        self.update()

    def paintEvent(self, _ev) -> None:
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        w, h = self.width(), self.height()
        p.fillRect(self.rect(), QColor(theme.BG2))
        pad_l, pad_b = 34, 16
        plot = QRectF(pad_l, 6, w - pad_l - 8, h - 6 - pad_b)
        small = scaled_font(self.font(), 0.75)
        p.setFont(small)
        p.setPen(QColor(theme.LABEL))
        for v, align in ((0, Qt.AlignLeft), (50, Qt.AlignHCenter), (100, Qt.AlignRight)):
            x = plot.left() + plot.width() * v / 100
            rx = x if v == 0 else x - 40 if v == 100 else x - 20
            p.drawText(QRectF(rx, plot.bottom() + 1, 40, pad_b), align | Qt.AlignTop, str(v))
        if len(self.points) < 2:
            p.setPen(QColor(theme.DIM))
            p.drawText(plot, Qt.AlignCenter, "The sweep curve appears here")
            p.end()
            return
        dbs = [d for _, d in self.points]
        if not math.isnan(self.target_db):
            dbs.append(self.target_db)
        lo, hi = min(dbs) - 3, max(dbs) + 3
        rng = max(6.0, hi - lo)

        def pt(v, d):
            return QPointF(plot.left() + plot.width() * v / 100, plot.bottom() - (d - lo) / rng * plot.height())

        p.setPen(QColor(theme.LABEL))
        p.drawText(QRectF(0, plot.top() - 2, pad_l - 4, 14), Qt.AlignRight, f"{hi:.0f}")
        p.drawText(QRectF(0, plot.bottom() - 12, pad_l - 4, 14), Qt.AlignRight, f"{lo:.0f}")
        if not math.isnan(self.target_db):
            y = pt(0, self.target_db).y()
            p.setPen(QPen(QColor(theme.DIM), 1, Qt.DashLine))
            p.drawLine(QPointF(plot.left(), y), QPointF(plot.right(), y))
        path = QPainterPath(pt(*self.points[0]))
        for v, d in self.points[1:]:
            path.lineTo(pt(v, d))
        p.setPen(QPen(QColor(theme.ACCENT), 1.8))
        p.drawPath(path)
        p.setBrush(QColor(theme.ACCENT))
        for v, d in self.points:
            p.drawEllipse(pt(v, d), 2.2, 2.2)
        if self.recommended >= 0:
            x = pt(self.recommended, lo).x()
            p.setPen(QPen(QColor(theme.AMBER), 2))
            p.drawLine(QPointF(x, plot.top()), QPointF(x, plot.bottom()))
        p.end()


class AgcCard(QFrame):
    def __init__(self, session: RadioSession, prefs: SlotPrefs, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.setProperty("role", "card")
        lay = QVBoxLayout(self)
        lay.setContentsMargins(12, 10, 12, 12)
        lay.setSpacing(8)
        lay.addWidget(label("BEST AGC-T", "section"))
        self.s = session
        self.prefs = prefs
        self._updating = False

        self.mode_text = label("", "dim")
        self.mode_text.setWordWrap(True)
        lay.addWidget(self.mode_text)

        row = QHBoxLayout()
        row.addWidget(label("AGC-T", "dim"))
        self.slider = QSlider(Qt.Horizontal)
        self.slider.setRange(0, 100)
        self.slider.valueChanged.connect(self._slid)
        row.addWidget(self.slider, 1)
        self.value = label("", "dim")
        self.value.setMinimumWidth(34)
        self.value.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        row.addWidget(self.value)
        lay.addLayout(row)

        self.target_row = QWidget()
        tr = QHBoxLayout(self.target_row)
        tr.setContentsMargins(0, 0, 0, 0)
        tr.addWidget(label("Target", "dim"))
        self.target = QSlider(Qt.Horizontal)
        self.target.setRange(-60, -6)
        self.target.setValue(int(session.agc.target_db))
        self.target.valueChanged.connect(self._target)
        tr.addWidget(self.target, 1)
        self.target_val = label(f"{session.agc.target_db:.0f} dB", "dim")
        self.target_val.setMinimumWidth(48)
        tr.addWidget(self.target_val)
        lay.addWidget(self.target_row)

        btns = QHBoxLayout()
        self.find = button("FIND BEST AGC-T", role="primary")
        self.find.clicked.connect(session.start_agc_sweep)
        self.keep = button("KEEP")
        self.keep.clicked.connect(session.agc_keep)
        self.restore = button("RESTORE")
        self.restore.clicked.connect(session.agc_restore)
        self.dax = button("ASSIGN DAX")
        self.dax.setToolTip("Give this slice a free DAX RX channel so AGC-T has audio to analyse")
        self.dax.clicked.connect(session.assign_dax)
        for b in (self.find, self.keep, self.restore, self.dax):
            btns.addWidget(b)
        btns.addStretch(1)
        lay.addLayout(btns)

        self.result = label("")
        self.result.setWordWrap(True)
        lay.addWidget(self.result)
        self.curve = AgcCurveView()
        lay.addWidget(self.curve)
        self.warning = label("", "message")
        self.warning.setWordWrap(True)
        lay.addWidget(self.warning)
        self.audio = label("", "dim")
        self.audio.setWordWrap(True)
        lay.addWidget(self.audio)

        session.subscribe(self._on_event)
        self.refresh()

    def _on_event(self, kind: str) -> None:
        if kind in ("agc", "selection", "connection", "slices"):
            self.refresh()

    def _slid(self, v: int) -> None:
        self.value.setText(str(v))
        if not self._updating:
            self.s.set_agc_value(v)

    def _target(self, v: int) -> None:
        self.target_val.setText(f"{v} dB")
        if not self._updating:
            self.s.set_agc_target(v)
            self.prefs.agc_target_db = float(v)

    def refresh(self) -> None:
        s = self.s
        a = s.agc
        self._updating = True
        try:
            self.mode_text.setText(s.agc_mode_text)
            self.slider.setValue(s.agc_value)
            self.value.setText(str(s.agc_value) if s.selected_slice else "—")
            self.slider.setEnabled(s.connected and not a.running and s.selected_slice is not None)
            self.target_row.setVisible(s.agc_is_off)
            self.target.setValue(int(a.target_db))
        finally:
            self._updating = False
        busy = s.agc_busy
        self.find.setEnabled(s.connected and s.selected_slice is not None and not busy)
        self.find.setText(f"SWEEPING {a.percent}%" if a.running else "FIND BEST AGC-T")
        self.keep.setEnabled(s.agc_can_decide)
        self.restore.setEnabled(busy or s.agc_can_decide)
        self.dax.setVisible(s.needs_dax)
        self.result.setText(s.agc_result_text)
        self.warning.setText(s.agc_warning)
        self.audio.setText(s.audio_status)
        self.curve.set_data(a.curve, a.recommended, a.target_db if s.agc_is_off else math.nan)
