"""Single-threaded dispatchers for the model layer.

Every bit of radio/session state is mutated on exactly one thread (like the .NET
build's ``Ui.Post``). Network threads hand work over with ``post``; blocking command
sequences run on a per-session worker and post their results back.

``ThreadDispatcher`` runs its own thread and is used headless and in tests. The Qt UI
supplies ``QtDispatcher`` (see ``ui/qt_dispatch.py``) so the model runs on the GUI thread.
"""
from __future__ import annotations

import heapq
import itertools
import queue
import threading
import time
from typing import Callable, List, Tuple


class TimerHandle:
    __slots__ = ("cancelled",)

    def __init__(self) -> None:
        self.cancelled = False

    def cancel(self) -> None:
        self.cancelled = True


class Dispatcher:
    def post(self, fn: Callable[[], None]) -> None:
        raise NotImplementedError

    def call_later(self, seconds: float, fn: Callable[[], None]) -> TimerHandle:
        raise NotImplementedError

    def on_thread(self) -> bool:
        raise NotImplementedError


class ThreadDispatcher(Dispatcher):
    def __init__(self, name: str = "flex-model") -> None:
        self._q: "queue.Queue[Callable[[], None]]" = queue.Queue()
        self._timers: List[Tuple[float, int, TimerHandle, Callable[[], None]]] = []
        self._tlock = threading.Lock()
        self._ids = itertools.count()
        self._stop = False
        self._thread = threading.Thread(target=self._run, name=name, daemon=True)
        self._thread.start()

    def post(self, fn: Callable[[], None]) -> None:
        self._q.put(fn)

    def call_later(self, seconds: float, fn: Callable[[], None]) -> TimerHandle:
        h = TimerHandle()
        with self._tlock:
            heapq.heappush(self._timers, (time.monotonic() + max(0.0, seconds), next(self._ids), h, fn))
        self._q.put(lambda: None)  # wake the loop so it recomputes its timeout
        return h

    def on_thread(self) -> bool:
        return threading.current_thread() is self._thread

    def _run(self) -> None:
        while not self._stop:
            with self._tlock:
                timeout = None
                if self._timers:
                    timeout = max(0.0, self._timers[0][0] - time.monotonic())
            try:
                fn = self._q.get(timeout=timeout)
                self._safe(fn)
            except queue.Empty:
                pass
            now = time.monotonic()
            due = []
            with self._tlock:
                while self._timers and self._timers[0][0] <= now:
                    due.append(heapq.heappop(self._timers))
            for _, _, h, fn in due:
                if not h.cancelled:
                    self._safe(fn)

    @staticmethod
    def _safe(fn: Callable[[], None]) -> None:
        try:
            fn()
        except Exception:  # noqa: BLE001
            import traceback
            traceback.print_exc()

    def run_sync(self, fn: Callable[[], object], timeout: float = 5.0):
        """Run ``fn`` on the dispatcher thread and wait for its result (tests / shutdown)."""
        if self.on_thread():
            return fn()
        done = threading.Event()
        box: list = [None, None]

        def wrapper():
            try:
                box[0] = fn()
            except Exception as ex:  # noqa: BLE001
                box[1] = ex
            finally:
                done.set()

        self.post(wrapper)
        if not done.wait(timeout):
            raise TimeoutError("dispatcher did not run the call in time")
        if box[1] is not None:
            raise box[1]
        return box[0]

    def stop(self) -> None:
        self._stop = True
        self._q.put(lambda: None)
        self._thread.join(timeout=2)
