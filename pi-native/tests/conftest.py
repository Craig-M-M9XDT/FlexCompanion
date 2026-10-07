import os
import time

import pytest

import json
import tempfile

# Headless Qt with a realistic 1280x800 screen (the offscreen default is 800x600).
if os.environ.get("QT_QPA_PLATFORM", "offscreen") == "offscreen":
    _cfg = os.path.join(tempfile.gettempdir(), "flexcompanion-test-screen.json")
    with open(_cfg, "w") as f:
        json.dump({"screens": [{"name": "TEST", "x": 0, "y": 0, "width": 1280, "height": 800,
                                "logicalDpi": 96, "logicalBaseDpi": 96, "dpr": 1}]}, f)
    os.environ["QT_QPA_PLATFORM"] = f"offscreen:configfile={_cfg}"


def wait_for(cond, timeout=5.0, step=0.02):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if cond():
            return True
        time.sleep(step)
    return cond()


@pytest.fixture
def sim():
    from flexcompanion.sim import SimRadio
    r = SimRadio(port=0).start()
    yield r
    r.stop()


@pytest.fixture
def dispatcher():
    from flexcompanion.dispatch import ThreadDispatcher
    d = ThreadDispatcher()
    yield d
    d.stop()
