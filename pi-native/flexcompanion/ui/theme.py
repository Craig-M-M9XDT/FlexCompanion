"""Dark Aether-inspired theme, matching the .NET Companion's palette."""
from __future__ import annotations

from PySide6.QtGui import QColor, QFont, QPalette
from PySide6.QtWidgets import QApplication

BG = "#080C14"
BG2 = "#0A101A"
PANEL = "#111923"
PANEL2 = "#162130"
BORDER = "#304050"
LABEL = "#8EA8C0"
TEXT = "#E6F0FA"
DIM = "#6F8296"
ACCENT = "#00B4D8"
AMBER = "#FFB84D"
RED = "#FF4D5E"
GREEN = "#3DDC84"


def stylesheet(scale: float = 1.0) -> str:
    """Touch mode raises ``scale`` so every control becomes a comfortable finger target."""
    fs = round(13 * scale)
    pad_v = round(6 * scale)
    pad_h = round(10 * scale)
    min_h = round(30 * scale)
    radius = round(6 * scale)
    handle = round(18 * scale)
    groove = round(6 * scale)
    return f"""
    QWidget {{ color: {TEXT}; font-size: {fs}px; }}
    QMainWindow, QScrollArea, QScrollArea > QWidget > QWidget#body {{ background: {BG}; }}
    QLabel {{ background: transparent; }}
    QLabel[role="section"] {{ color: {LABEL}; font-weight: bold; letter-spacing: 1px; font-size: {round(11 * scale)}px; }}
    QLabel[role="dim"] {{ color: {DIM}; }}
    QLabel[role="title"] {{ font-weight: bold; font-size: {round(16 * scale)}px; }}
    QLabel[role="message"] {{ color: {AMBER}; }}
    QFrame[role="card"] {{ background: {PANEL}; border: 1px solid {BORDER}; border-radius: {radius + 2}px; }}
    QPushButton {{
        background: {PANEL2}; border: 1px solid {BORDER}; border-radius: {radius}px;
        padding: {pad_v}px {pad_h}px; min-height: {min_h}px; color: {TEXT};
    }}
    QPushButton:hover {{ border-color: {ACCENT}; }}
    QPushButton:pressed {{ background: {BORDER}; }}
    QPushButton:checked {{ background: {ACCENT}; color: {BG}; border-color: {ACCENT}; font-weight: bold; }}
    QPushButton:disabled {{ color: {DIM}; border-color: #1E2A38; background: {BG2}; }}
    QPushButton[role="danger"]:checked {{ background: {RED}; border-color: {RED}; color: white; }}
    QPushButton[role="primary"] {{ border-color: {ACCENT}; color: {ACCENT}; font-weight: bold; }}
    QLineEdit, QComboBox {{
        background: {BG2}; border: 1px solid {BORDER}; border-radius: {radius}px;
        padding: {pad_v}px {pad_h}px; min-height: {min_h - 2 * pad_v}px; selection-background-color: {ACCENT};
    }}
    QComboBox QAbstractItemView {{ background: {PANEL}; border: 1px solid {BORDER}; selection-background-color: {ACCENT}; }}
    QComboBox::drop-down {{ border: none; width: {round(22 * scale)}px; }}
    QCheckBox {{ spacing: {round(8 * scale)}px; background: transparent; }}
    QCheckBox::indicator {{ width: {round(18 * scale)}px; height: {round(18 * scale)}px; border-radius: 4px;
        border: 1px solid {BORDER}; background: {BG2}; }}
    QCheckBox::indicator:checked {{ background: {ACCENT}; border-color: {ACCENT}; }}
    QSlider::groove:horizontal {{ height: {groove}px; background: {BORDER}; border-radius: {groove // 2}px; }}
    QSlider::sub-page:horizontal {{ background: {ACCENT}; border-radius: {groove // 2}px; }}
    QSlider::handle:horizontal {{ background: {TEXT}; width: {handle}px; height: {handle}px;
        margin: -{(handle - groove) // 2}px 0; border-radius: {handle // 2}px; }}
    QSlider:disabled::sub-page:horizontal {{ background: #24323F; }}
    QSlider::handle:horizontal:disabled {{ background: {DIM}; }}
    QTabWidget::pane {{ border: none; }}
    QTabBar::tab {{ background: {PANEL}; color: {LABEL}; padding: {pad_v + 2}px {pad_h + 8}px;
        min-height: {min_h - 6}px; border: 1px solid {BORDER}; border-bottom: none;
        border-top-left-radius: {radius}px; border-top-right-radius: {radius}px; margin-right: 3px; font-weight: bold; }}
    QTabBar::tab:selected {{ background: {PANEL2}; color: {TEXT}; border-color: {ACCENT}; }}
    QListWidget {{ background: {BG2}; border: 1px solid {BORDER}; border-radius: {radius}px; }}
    QListWidget::item {{ padding: {pad_v}px; border-bottom: 1px solid #18222E; }}
    QListWidget::item:selected {{ background: {PANEL2}; color: {TEXT}; border-left: 3px solid {ACCENT}; }}
    QScrollArea {{ border: none; }}
    QScrollBar:vertical {{ background: {BG}; width: {round(10 * scale)}px; }}
    QScrollBar::handle:vertical {{ background: {BORDER}; border-radius: 4px; min-height: 30px; }}
    QScrollBar::add-line, QScrollBar::sub-line {{ height: 0; }}
    QToolTip {{ background: {PANEL}; color: {TEXT}; border: 1px solid {BORDER}; padding: 4px; }}
    """


def apply(app: QApplication, scale: float = 1.0) -> None:
    app.setStyle("Fusion")
    pal = QPalette()
    for role, col in ((QPalette.Window, BG), (QPalette.Base, BG2), (QPalette.AlternateBase, PANEL),
                      (QPalette.Text, TEXT), (QPalette.WindowText, TEXT), (QPalette.Button, PANEL2),
                      (QPalette.ButtonText, TEXT), (QPalette.Highlight, ACCENT), (QPalette.HighlightedText, BG),
                      (QPalette.ToolTipBase, PANEL), (QPalette.ToolTipText, TEXT)):
        pal.setColor(role, QColor(col))
    app.setPalette(pal)
    f = QFont(app.font())
    f.setFamilies(["Inter", "DejaVu Sans", "Noto Sans", "Sans Serif"])
    app.setFont(f)
    app.setStyleSheet(stylesheet(scale))
