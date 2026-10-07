"""AetherSDR shared-pan bridge (port of AetherPanBridge.cs).

A patched AetherSDR (see ``AetherBridgePatch/`` in the repo) mirrors the panadapter FFT
frames it already receives from the radio to ``127.0.0.1:7331`` as FCSP v1 datagrams.
When those frames are fresh, Companion re-slices them for its compact spectrum and creates
no DAX IQ stream and no FFT of its own.
"""
from __future__ import annotations

import socket
import threading
import time
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional, Tuple

import numpy as np

from . import _core

PORT = 7331
FRESH_S = 0.9


@dataclass
class PanFrame:
    serial: str
    stream_id: int
    bins: np.ndarray
    sequence: int
    received: float          # time.monotonic()
    source_ns: int


def normalize_serial(serial: Optional[str]) -> str:
    return (serial or "").strip().strip('"').upper()


class AetherPanBridge:
    """Process-wide localhost listener; sessions subscribe to frames for their radio."""

    _instance: Optional["AetherPanBridge"] = None
    _instance_lock = threading.Lock()

    @classmethod
    def instance(cls, port: int = PORT) -> "AetherPanBridge":
        with cls._instance_lock:
            if cls._instance is None:
                cls._instance = AetherPanBridge(port)
            return cls._instance

    def __init__(self, port: int = PORT):
        self.port = port
        self.error: Optional[str] = None
        self._frames: Dict[Tuple[str, int], PanFrame] = {}
        self._lock = threading.Lock()
        self._listeners: List[Callable[[PanFrame], None]] = []
        self._seq = 0
        self._stop = threading.Event()
        self._sock: Optional[socket.socket] = None
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.bind(("127.0.0.1", port))
            s.settimeout(0.5)
            self._sock = s
            self.port = s.getsockname()[1]
            threading.Thread(target=self._loop, name="aether-bridge", daemon=True).start()
        except OSError as ex:
            self.error = str(ex)      # another Companion already owns the port: fall back to DAX IQ

    def subscribe(self, fn: Callable[[PanFrame], None]) -> None:
        with self._lock:
            if fn not in self._listeners:
                self._listeners.append(fn)

    def unsubscribe(self, fn: Callable[[PanFrame], None]) -> None:
        with self._lock:
            if fn in self._listeners:
                self._listeners.remove(fn)

    def latest(self, serial: str, stream_id: int, max_age_s: float = FRESH_S) -> Optional[PanFrame]:
        serial = normalize_serial(serial)
        if not serial or not stream_id:
            return None
        with self._lock:
            f = self._frames.get((serial, stream_id))
        if f is None or time.monotonic() - f.received > max_age_s:
            return None
        return f

    def _loop(self) -> None:
        sock = self._sock
        while not self._stop.is_set() and sock is not None:
            try:
                data = sock.recv(70000)
            except socket.timeout:
                continue
            except OSError:
                break
            try:
                self._handle(data)
            except Exception:  # noqa: BLE001 - a bad localhost datagram must not stop the loop
                pass

    def _handle(self, data: bytes) -> None:
        parsed = _core.parse_fcsp(data)
        if parsed is None:
            return
        serial, sid, bins, ns = parsed
        serial = normalize_serial(serial)
        if not serial:
            return
        with self._lock:
            self._seq += 1
            frame = PanFrame(serial, sid, bins, self._seq, time.monotonic(), ns)
            self._frames[(serial, sid)] = frame          # latest frame wins; nothing queues
            listeners = list(self._listeners)
        for fn in listeners:
            try:
                fn(frame)
            except Exception:  # noqa: BLE001
                pass

    def close(self) -> None:
        self._stop.set()
        if self._sock is not None:
            self._sock.close()


def fcsp_packet(serial: str, stream_id: int, bins: np.ndarray, source_ns: int = 0) -> bytes:
    """Build an FCSP v1 datagram (used by the simulator and tests; mirrors the Aether patch)."""
    import struct
    s = serial.encode()
    b = np.asarray(bins, dtype="<f4")
    return (b"FCSP" + struct.pack("<BBHIHHq", 1, 0, len(s), stream_id, len(b), 0, source_ns) + s + b.tobytes())
