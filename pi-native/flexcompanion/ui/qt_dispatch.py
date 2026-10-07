"""Runs the model layer on the Qt GUI thread."""
from __future__ import annotations

import threading
from typing import Callable

from PySide6.QtCore import QObject, QTimer, Signal, Slot

from ..dispatch import Dispatcher, TimerHandle


class QtDispatcher(QObject, Dispatcher):
    _posted = Signal(object)

    def __init__(self) -> None:
        QObject.__init__(self)
        self._gui_thread = threading.current_thread()
        # Emitting from any thread queues the call onto this object's (GUI) thread.
        self._posted.connect(self._run)

    def post(self, fn: Callable[[], None]) -> None:
        self._posted.emit(fn)

    def call_later(self, seconds: float, fn: Callable[[], None]) -> TimerHandle:
        h = TimerHandle()

        def fire():
            if not h.cancelled:
                self._run(fn)
        # Timers must be created on the GUI thread.
        self.post(lambda: QTimer.singleShot(max(0, int(seconds * 1000)), fire))
        return h

    def on_thread(self) -> bool:
        return threading.current_thread() is self._gui_thread

    @Slot(object)
    def _run(self, fn: Callable[[], None]) -> None:
        try:
            fn()
        except Exception:  # noqa: BLE001
            import traceback
            traceback.print_exc()
