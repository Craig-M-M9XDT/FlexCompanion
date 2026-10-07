"""Custom widgets: linear meter with peak hold, passband spectrum, DSP rows, flow layout."""
from __future__ import annotations

import math
import time
from typing import List, Optional, Sequence, Tuple

import numpy as np
from PySide6.QtCore import QPointF, QRect, QRectF, QSize, Qt, Signal
from PySide6.QtGui import QColor, QFont, QLinearGradient, QPainter, QPainterPath, QPen
from PySide6.QtWidgets import (QFrame, QHBoxLayout, QLabel, QLayout, QPushButton, QSizePolicy, QSlider,
                               QVBoxLayout, QWidget)

from . import theme


def scaled_font(base: QFont, factor: float, bold: bool = False, minimum_px: int = 9) -> QFont:
    """Scale a font whether it was sized in points or (via stylesheet) in pixels."""
    f = QFont(base)
    if base.pixelSize() > 0:
        f.setPixelSize(max(minimum_px, round(base.pixelSize() * factor)))
    else:
        f.setPointSizeF(max(minimum_px * 0.75, base.pointSizeF() * factor))
    f.setBold(bold)
    return f


def card(title: str = "") -> Tuple[QFrame, QVBoxLayout]:
    f = QFrame()
    f.setProperty("role", "card")
    lay = QVBoxLayout(f)
    lay.setContentsMargins(12, 10, 12, 12)
    lay.setSpacing(8)
    if title:
        lab = QLabel(title.upper())
        lab.setProperty("role", "section")
        lay.addWidget(lab)
    return f, lay


def label(text: str = "", role: str = "") -> QLabel:
    lab = QLabel(text)
    if role:
        lab.setProperty("role", role)
    return lab


def button(text: str, checkable: bool = False, role: str = "") -> QPushButton:
    b = QPushButton(text)
    b.setCheckable(checkable)
    b.setFocusPolicy(Qt.NoFocus)
    if role:
        b.setProperty("role", role)
    return b


# ───────────────────────────── meter ─────────────────────────────

class MeterBar(QWidget):
    """Horizontal meter: scale ticks, red zone, moving bar and a delayed-release peak marker."""

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.setMinimumHeight(78)
        self.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        self.lo, self.hi, self.red = -127.0, -13.0, -73.0
        self.ticks: Sequence[Tuple[float, str]] = []
        self.value = math.nan
        self.title = "S"
        self.text = "-"
        self.sub = ""
        self.transmitting = False
        self._peak = math.nan
        self._peak_t = 0.0

    def sizeHint(self) -> QSize:
        return QSize(420, 84)

    def set_state(self, value: float, lo: float, hi: float, red: float, ticks, title: str, text: str,
                  sub: str, transmitting: bool) -> None:
        if (lo, hi) != (self.lo, self.hi):
            self._peak = math.nan
        self.value, self.lo, self.hi, self.red = value, lo, hi, red
        self.ticks, self.title, self.text, self.sub, self.transmitting = ticks, title, text, sub, transmitting
        now = time.monotonic()
        if not math.isnan(value):
            if math.isnan(self._peak) or value >= self._peak:
                self._peak, self._peak_t = value, now
            elif now - self._peak_t > 1.2:                       # hold, then fall back gently
                self._peak = max(value, self._peak - (hi - lo) * 0.02)
        self.update()

    def _frac(self, v: float) -> float:
        if math.isnan(v) or self.hi <= self.lo:
            return 0.0
        return min(1.0, max(0.0, (v - self.lo) / (self.hi - self.lo)))

    def paintEvent(self, _ev) -> None:
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        w, h = self.width(), self.height()
        scale = max(1.0, h / 84)
        big = scaled_font(self.font(), 1.9, bold=True)
        small = scaled_font(self.font(), 0.8)

        # readout on the left
        read_w = int(118 * scale)
        accent = QColor(theme.RED if self.transmitting else theme.ACCENT)
        p.setPen(QColor(theme.LABEL))
        p.setFont(small)
        p.drawText(QRect(0, 2, read_w, int(16 * scale)), Qt.AlignLeft | Qt.AlignVCenter,
                   ("TX  " if self.transmitting else "RX  ") + self.title)
        p.setPen(accent)
        p.setFont(big)
        p.drawText(QRect(0, int(16 * scale), read_w, int(36 * scale)), Qt.AlignLeft | Qt.AlignVCenter, self.text)
        p.setPen(QColor(theme.DIM))
        p.setFont(small)
        p.drawText(QRect(0, int(52 * scale), read_w, int(20 * scale)), Qt.AlignLeft | Qt.AlignVCenter, self.sub)

        # bar
        x0 = read_w + 8
        bw = max(40, w - x0 - int(18 * scale))
        by, bh = int(14 * scale), int(22 * scale)
        rect = QRectF(x0, by, bw, bh)
        p.setPen(Qt.NoPen)
        p.setBrush(QColor(theme.BG2))
        p.drawRoundedRect(rect, 4, 4)
        red_x = x0 + bw * self._frac(self.red)
        p.setBrush(QColor(90, 30, 38))
        p.drawRoundedRect(QRectF(red_x, by, x0 + bw - red_x, bh), 4, 4)

        fx = bw * self._frac(self.value)
        if fx > 1:
            g = QLinearGradient(x0, 0, x0 + bw, 0)
            g.setColorAt(0.0, QColor("#0B5E78"))
            g.setColorAt(max(0.01, self._frac(self.red) - 0.001), accent)
            g.setColorAt(min(0.999, self._frac(self.red) + 0.001), QColor(theme.AMBER))
            g.setColorAt(1.0, QColor(theme.RED))
            p.setBrush(g)
            p.drawRoundedRect(QRectF(x0, by + 2, fx, bh - 4), 3, 3)
        if not math.isnan(self._peak):
            px = x0 + bw * self._frac(self._peak)
            p.setPen(QPen(QColor(theme.TEXT), 2))
            p.drawLine(QPointF(px, by - 2), QPointF(px, by + bh + 2))

        # ticks
        p.setFont(small)
        ty = by + bh + 4
        for v, lab in self.ticks:
            tx = x0 + bw * self._frac(v)
            p.setPen(QPen(QColor(theme.BORDER), 1))
            p.drawLine(QPointF(tx, ty), QPointF(tx, ty + 5 * scale))
            p.setPen(QColor(theme.RED) if v >= self.red else QColor(theme.LABEL))
            p.drawText(QRectF(tx - 22, ty + 5 * scale, 44, 16 * scale), Qt.AlignHCenter | Qt.AlignTop, lab)
        p.end()


# ───────────────────────────── spectrum ─────────────────────────────

class SpectrumView(QWidget):
    """Trace-only passband spectrum with filter shading and carrier marker.

    Emits ``clicked(offset_hz)``: the clicked frequency relative to the slice carrier,
    so a tap can retune the slice.
    """
    clicked = Signal(float)

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.setMinimumHeight(120)
        self.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Expanding)
        self.bins: Optional[np.ndarray] = None
        self.half_span = 24000.0
        self.centre_off = 0.0
        self.slice_frac = math.nan
        self.filt = (math.nan, math.nan)
        self.message = "Spectrum off"
        self._floor = -110.0
        self._top = -40.0

    def sizeHint(self) -> QSize:
        return QSize(420, 170)

    def set_frame(self, frame) -> None:
        if frame is None:
            self.bins = None
        else:
            self.bins = frame.bins
            self.half_span = frame.half_span_hz
            self.centre_off = frame.centre_offset_hz
            self.slice_frac = frame.slice_fraction
            self.filt = (frame.filter_lo_frac, frame.filter_hi_frac)
            # auto range: slow-moving noise floor estimate and a peak headroom
            floor = float(np.percentile(frame.bins, 20)) - 8
            top = max(floor + 50, float(frame.bins.max()) + 6)
            self._floor += (floor - self._floor) * 0.15
            self._top += (top - self._top) * 0.15
        self.update()

    def set_message(self, text: str) -> None:
        self.message = text
        if self.bins is None:
            self.update()

    def mouseReleaseEvent(self, ev) -> None:
        if self.bins is None or math.isnan(self.slice_frac):
            return
        frac = ev.position().x() / max(1, self.width())
        self.clicked.emit((frac - self.slice_frac) * self.half_span * 2)

    def paintEvent(self, _ev) -> None:
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        w, h = self.width(), self.height()
        p.fillRect(self.rect(), QColor(theme.BG2))
        label_h = 16
        ph = h - label_h

        # grid
        p.setPen(QPen(QColor("#18232F"), 1))
        for k in range(1, 4):
            y = ph * k / 4
            p.drawLine(QPointF(0, y), QPointF(w, y))
        for k in range(1, 8):
            x = w * k / 8
            p.drawLine(QPointF(x, 0), QPointF(x, ph))

        if self.bins is None or len(self.bins) < 2:
            p.setPen(QColor(theme.DIM))
            p.drawText(self.rect(), Qt.AlignCenter | Qt.TextWordWrap, self.message)
            p.end()
            return

        # filter passband
        lo, hi = self.filt
        if not math.isnan(lo) and not math.isnan(hi):
            x1, x2 = sorted((lo * w, hi * w))
            p.fillRect(QRectF(x1, 0, x2 - x1, ph), QColor(0, 180, 216, 34))
            p.setPen(QPen(QColor(0, 180, 216, 90), 1))
            p.drawLine(QPointF(x1, 0), QPointF(x1, ph))
            p.drawLine(QPointF(x2, 0), QPointF(x2, ph))

        # trace
        b = self.bins
        n = len(b)
        rng = max(10.0, self._top - self._floor)
        ys = ph - (np.clip(b, self._floor, self._top) - self._floor) / rng * (ph - 4)
        xs = np.arange(n) * (w / (n - 1))
        path = QPainterPath(QPointF(0, ph))
        for x, y in zip(xs.tolist(), ys.tolist()):
            path.lineTo(x, y)
        path.lineTo(w, ph)
        g = QLinearGradient(0, 0, 0, ph)
        g.setColorAt(0, QColor(0, 180, 216, 110))
        g.setColorAt(1, QColor(0, 180, 216, 8))
        p.fillPath(path, g)
        trace = QPainterPath(QPointF(xs[0], ys[0]))
        for x, y in zip(xs[1:].tolist(), ys[1:].tolist()):
            trace.lineTo(x, y)
        p.setPen(QPen(QColor(theme.ACCENT), 1.4))
        p.drawPath(trace)

        # carrier marker
        if not math.isnan(self.slice_frac):
            cx = self.slice_frac * w
            p.setPen(QPen(QColor(theme.AMBER), 1.5, Qt.DashLine))
            p.drawLine(QPointF(cx, 0), QPointF(cx, ph))

        # frequency labels relative to the carrier
        p.setFont(scaled_font(self.font(), 0.78))
        p.setPen(QColor(theme.LABEL))
        base = 0.0 if math.isnan(self.slice_frac) else (0.5 - self.slice_frac) * self.half_span * 2
        for k in (0, 2, 4, 6, 8):
            fx = w * k / 8
            off = base + (k / 8 - 0.5) * self.half_span * 2
            txt = f"{off / 1000:+.1f}k" if abs(off) >= 50 else "0"
            align = Qt.AlignLeft if k == 0 else Qt.AlignRight if k == 8 else Qt.AlignHCenter
            rx = fx if k == 0 else fx - 60 if k == 8 else fx - 30
            p.drawText(QRectF(rx, ph, 60, label_h), align | Qt.AlignVCenter, txt)
        p.end()


# ───────────────────────────── DSP row ─────────────────────────────

class ParamRow(QWidget):
    """Toggle button + level slider + value, bound to a ``ParamControl``."""

    def __init__(self, ctl, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.ctl = ctl
        lay = QHBoxLayout(self)
        lay.setContentsMargins(0, 0, 0, 0)
        lay.setSpacing(8)
        self.btn = button(ctl.label, checkable=True)
        self.btn.setMinimumWidth(68)
        self.btn.setToolTip(ctl.tooltip)
        self.btn.clicked.connect(lambda on: ctl.set_on(bool(on)))
        if not ctl.has_toggle:
            self.btn.setCheckable(False)
            self.btn.setEnabled(False)
        lay.addWidget(self.btn)
        self.slider: Optional[QSlider] = None
        self.value = label("", "dim")
        self.value.setMinimumWidth(44)
        self.value.setAlignment(Qt.AlignRight | Qt.AlignVCenter)
        if ctl.has_level:
            self._steps = int(round((ctl.max - ctl.min) / ctl.step))
            s = QSlider(Qt.Horizontal)
            s.setRange(0, self._steps)
            s.setPageStep(max(1, self._steps // 10))
            s.valueChanged.connect(self._slid)
            self.slider = s
            lay.addWidget(s, 1)
            lay.addWidget(self.value)
        else:
            note = label("on / off only", "dim")
            lay.addWidget(note, 1)
        self._updating = False
        self.refresh()

    def _slid(self, pos: int) -> None:
        if self._updating:
            return
        self.ctl.set_level(self.ctl.min + pos * self.ctl.step)

    def refresh(self) -> None:
        c = self.ctl
        self._updating = True
        try:
            avail = c.is_available
            self.btn.setEnabled(avail and c.has_toggle)
            if c.has_toggle:
                self.btn.setChecked(c.is_on)
            tip = c.tooltip if avail else f"{c.tooltip}\n\n{c.unavailable_text}"
            self.btn.setToolTip(tip)
            if self.slider is not None:
                self.slider.setEnabled(avail)
                self.slider.setValue(int(round((c.level - c.min) / c.step)))
                self.slider.setToolTip(tip)
                self.value.setText(c.level_text if avail else "—")
        finally:
            self._updating = False


# ───────────────────────────── flow layout ─────────────────────────────

class FlowLayout(QLayout):
    """Wraps buttons onto new lines as the panel narrows (band / mode rows on 800x480)."""

    def __init__(self, parent: Optional[QWidget] = None, spacing: int = 6):
        super().__init__(parent)
        self._items: List = []
        self._sp = spacing
        self.setContentsMargins(0, 0, 0, 0)

    def addItem(self, item) -> None:
        self._items.append(item)

    def count(self) -> int:
        return len(self._items)

    def itemAt(self, i: int):
        return self._items[i] if 0 <= i < len(self._items) else None

    def takeAt(self, i: int):
        return self._items.pop(i) if 0 <= i < len(self._items) else None

    def expandingDirections(self):
        return Qt.Orientations(0)

    def hasHeightForWidth(self) -> bool:
        return True

    def heightForWidth(self, width: int) -> int:
        return self._do(QRect(0, 0, width, 0), True)

    def setGeometry(self, rect: QRect) -> None:
        super().setGeometry(rect)
        self._do(rect, False)

    def sizeHint(self) -> QSize:
        return self.minimumSize()

    def minimumSize(self) -> QSize:
        s = QSize()
        for it in self._items:
            s = s.expandedTo(it.minimumSize())
        return s

    def _do(self, rect: QRect, test: bool) -> int:
        x, y, line_h = rect.x(), rect.y(), 0
        for it in self._items:
            hint = it.sizeHint()
            nx = x + hint.width() + self._sp
            if nx - self._sp > rect.right() and line_h > 0:
                x, y = rect.x(), y + line_h + self._sp
                nx = x + hint.width() + self._sp
                line_h = 0
            if not test:
                it.setGeometry(QRect(x, y, hint.width(), hint.height()))
            x = nx
            line_h = max(line_h, hint.height())
        return y + line_h - rect.y()


def grid(widgets: Sequence[QWidget], cols: int, spacing: int = 6) -> QWidget:
    """Fixed-column button grid; predictable on an 800x480 panel."""
    from PySide6.QtWidgets import QGridLayout
    holder = QWidget()
    g = QGridLayout(holder)
    g.setContentsMargins(0, 0, 0, 0)
    g.setSpacing(spacing)
    for i, wdg in enumerate(widgets):
        wdg.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Fixed)
        wdg.setMinimumWidth(0)
        g.addWidget(wdg, i // cols, i % cols)
    return holder


def flow(widgets: Sequence[QWidget], spacing: int = 6) -> QWidget:
    holder = QWidget()
    fl = FlowLayout(holder, spacing)
    for wdg in widgets:
        fl.addWidget(wdg)
    return holder
