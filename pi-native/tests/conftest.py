import os
import time

import pytest

os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")


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
