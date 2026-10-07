import pytest

from conftest import wait_for
from flexcompanion.discovery import Discovery


def test_discovery_receives_sim(sim):
    import socket
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    d = Discovery(port=port)
    d.start()
    try:
        sim.start_discovery(target="127.0.0.1", port=port, ip="127.0.0.1")
        assert wait_for(lambda: len(d.radios) == 1, 4)
        r = d.radios[0]
        assert r.model == "FLEX-6600" and r.port == sim.port and "SimRadio" in r.title
        assert "in use by SHACK" in r.detail
    finally:
        d.stop()


def test_main_window_smoke(sim, tmp_path):
    pytest.importorskip("PySide6")
    from PySide6.QtCore import QCoreApplication
    from PySide6.QtWidgets import QApplication
    from flexcompanion.settings import Settings
    from flexcompanion.ui import theme
    from flexcompanion.ui.main_window import MainWindow

    app = QApplication.instance() or QApplication([])
    theme.apply(app)
    st = Settings(tmp_path / "settings.json")
    st.remember_radio("A", "127.0.0.1", sim.port, "Sim", "")
    st.slot("A").show_fft = True
    w = MainWindow(st)
    w.resize(800, 480)
    w.show()

    def pump(cond, timeout=6):
        return wait_for(lambda: (QCoreApplication.processEvents(), cond())[1], timeout)

    try:
        assert pump(lambda: w.sessions["A"].connected)
        assert pump(lambda: w.panel_a.spec.bins is not None)
        assert w.tabs.tabText(w.tabs.currentIndex()).startswith("RADIO A")
        assert [w.tabs.tabText(i) for i in range(w.tabs.count())][0] == "RADIOS"   # compact layout
        nr_row = next(r for r in w.panel_a.rows if r.ctl.label == "NR")
        nr_row.btn.click()
        assert pump(lambda: sim.slices[0]["nr"] == "1")
        w.touch_box.setChecked(True)
        w.resize(1280, 860)
        assert pump(lambda: w.sidebar.isVisible())
    finally:
        w.close()
        QCoreApplication.processEvents()
    assert "NativeLastRadio" in (tmp_path / "settings.json").read_text()


PI_SCREENS = {"Touch Display (7in) 800x480": (800, 480),
              "Touch Display 2 landscape 1280x720": (1280, 720),
              "Touch Display 2 portrait 720x1280": (720, 1280)}


def test_fits_official_pi_touchscreens(tmp_path):
    """In touch mode the window must be able to shrink to every official Pi touchscreen."""
    pytest.importorskip("PySide6")
    from PySide6.QtCore import QCoreApplication
    from PySide6.QtWidgets import QApplication
    from flexcompanion.settings import Settings
    from flexcompanion.ui import theme
    from flexcompanion.ui.main_window import MainWindow

    app = QApplication.instance() or QApplication([])
    theme.apply(app)
    w = MainWindow(Settings(tmp_path / "settings.json"), touch=True)
    w.show()
    try:
        for name, (sw, sh) in PI_SCREENS.items():
            w.resize(sw, min(sh, 800))
            for _ in range(5):
                QCoreApplication.processEvents()
            m = w.minimumSizeHint()
            assert m.width() <= sw and m.height() <= sh, f"{name}: needs at least {m.width()}x{m.height()}"
            assert w._compact == (sw < 1000), name
            body = w.panel_a.widget()
            assert body.minimumSizeHint().width() <= w.panel_a.viewport().width(), f"{name}: radio page clipped"
    finally:
        w.close()
        QCoreApplication.processEvents()
