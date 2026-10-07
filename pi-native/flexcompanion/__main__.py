"""Entry point: ``python -m flexcompanion`` / ``flexcompanion-native``."""
from __future__ import annotations

import argparse
import os
import signal
import sys


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="flexcompanion-native",
                                 description="Flex Control Companion — native Raspberry Pi / Linux build")
    ap.add_argument("--touch", action="store_true", help="start with large touch controls")
    ap.add_argument("--kiosk", action="store_true", help="start full-screen")
    ap.add_argument("--demo", action="store_true", help="start a simulated radio and connect to it (no hardware needed)")
    ap.add_argument("--pure-python", action="store_true", help="use the numpy fallback instead of the C++ core")
    ap.add_argument("--settings", help="settings file (default ~/.config/FlexCompanion/settings.json)")
    ap.add_argument("--version", action="store_true")
    args = ap.parse_args(argv)

    if args.pure_python:
        os.environ["FLEXCOMPANION_PURE_PYTHON"] = "1"

    from . import __version__, _core
    if args.version:
        print(f"flexcompanion {__version__} ({_core.BACKEND_NAME})")
        return 0

    try:
        from PySide6.QtWidgets import QApplication
    except ImportError:
        print("PySide6 is not installed. On Raspberry Pi OS run the installer, or: "
              "sudo apt install python3-pyside6.qtwidgets", file=sys.stderr)
        return 2

    from .settings import Settings
    from .ui import theme
    from .ui.main_window import MainWindow

    app = QApplication(sys.argv[:1])
    app.setApplicationName("Flex Control Companion")
    app.setDesktopFileName("flexcompanion-native")
    theme.apply(app)
    signal.signal(signal.SIGINT, lambda *_: app.quit())

    settings = Settings(args.settings) if args.settings else Settings()
    sim = None
    if args.demo:
        from .sim import SimRadio
        sim = SimRadio(port=0).start()
        settings.remember_radio("A", "127.0.0.1", sim.port, "SimRadio (demo)", "")

    win = MainWindow(settings, touch=args.touch, kiosk=args.kiosk)
    if not args.kiosk:
        win.show()
        win.fit_to_screen()
    rc = app.exec()
    if sim is not None:
        settings.forget_radio("A")
        settings.save()
        sim.stop()
    return rc


if __name__ == "__main__":
    sys.exit(main())
