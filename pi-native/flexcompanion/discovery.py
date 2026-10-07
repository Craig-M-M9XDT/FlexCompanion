"""FLEX LAN discovery on UDP 4992 (port of Discovery.cs)."""
from __future__ import annotations

import socket
import threading
import time
from dataclasses import dataclass, field
from typing import Callable, Dict, List, Optional

from . import kv

PORT = 4992
PURGE_AFTER_S = 15.0


@dataclass
class RadioInfo:
    serial: str
    model: str = ""
    nickname: str = ""
    callsign: str = ""
    ip: str = ""
    port: int = 4992
    version: str = ""
    status: str = ""
    stations: str = ""
    last_seen: float = field(default_factory=time.monotonic)

    @property
    def title(self) -> str:
        name = self.model if not self.nickname.strip() else f"{self.nickname}  ({self.model})"
        return name if not self.callsign.strip() else f"{name}  {self.callsign}"

    @property
    def detail(self) -> str:
        s = f"{self.ip}   v{self.version}   {self.status}"
        return s if not self.stations.strip() else f"{s}   in use by {self.stations}"


def parse_packet(buf: bytes) -> Optional[kv.CIDict]:
    """Discovery payload is a VITA packet whose body is key=value text."""
    text = buf[28:].decode("ascii", "replace") if len(buf) > 28 else ""
    if "model=" not in text:
        text = buf.decode("ascii", "replace")
    if "model=" not in text:
        return None
    text = "".join(" " if ord(c) < 32 else c for c in text.replace("\x7f", "\xa0"))
    return kv.parse_line(text)


class Discovery:
    """Listens for radio broadcasts. ``on_change`` is called (on the listener thread or
    purge timer) whenever the radio list changes; read ``radios`` for a snapshot."""

    def __init__(self, on_change: Optional[Callable[[], None]] = None, port: int = PORT):
        self.on_change = on_change
        self.port = port
        self.error: Optional[str] = None
        self._radios: Dict[str, RadioInfo] = {}
        self._lock = threading.Lock()
        self._sock: Optional[socket.socket] = None
        self._stop = threading.Event()
        self._thread: Optional[threading.Thread] = None

    @property
    def radios(self) -> List[RadioInfo]:
        with self._lock:
            return sorted(self._radios.values(), key=lambda r: (r.title, r.serial))

    def start(self) -> None:
        self.stop()
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            if hasattr(socket, "SO_REUSEPORT"):
                try:
                    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEPORT, 1)
                except OSError:
                    pass
            s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            s.bind(("", self.port))
            s.settimeout(1.0)
            self._sock = s
            self.error = None
        except OSError as ex:
            self.error = f"Can't listen on UDP {self.port} ({ex.strerror or ex}). Use the manual IP box."
            self._notify()
            return
        self._stop.clear()
        self._thread = threading.Thread(target=self._loop, name="flex-discovery", daemon=True)
        self._thread.start()
        self._notify()

    def rescan(self) -> None:
        with self._lock:
            self._radios.clear()
        self.start()

    def _loop(self) -> None:
        sock = self._sock
        last_purge = time.monotonic()
        while not self._stop.is_set() and sock is not None:
            try:
                data, addr = sock.recvfrom(4096)
                d = parse_packet(data)
                if d is not None:
                    self._upsert(d, addr[0])
            except socket.timeout:
                pass
            except OSError:
                if self._stop.wait(0.5):
                    break
            now = time.monotonic()
            if now - last_purge >= 3:
                last_purge = now
                self._purge(now)

    def _upsert(self, d: kv.CIDict, sender: str) -> None:
        serial = d.get("serial") or sender
        with self._lock:
            r = self._radios.get(serial)
            is_new = r is None
            if r is None:
                r = self._radios[serial] = RadioInfo(serial=serial)
            before = (r.title, r.detail)
            r.model = d.get("model", r.model)
            r.nickname = d.get("nickname") or d.get("name") or r.nickname
            r.callsign = d.get("callsign", r.callsign)
            r.ip = d.get("ip") or sender
            try:
                p = int(d.get("port", "0"))
                if p > 0:
                    r.port = p
            except ValueError:
                pass
            r.version = d.get("version", r.version)
            r.status = d.get("status", r.status)
            r.stations = (d.get("gui_client_stations") or "").replace(",", " ").strip()
            r.last_seen = time.monotonic()
            changed = is_new or before != (r.title, r.detail)
        if changed:
            self._notify()

    def _purge(self, now: float) -> None:
        with self._lock:
            stale = [k for k, r in self._radios.items() if now - r.last_seen > PURGE_AFTER_S]
            for k in stale:
                del self._radios[k]
        if stale:
            self._notify()

    def _notify(self) -> None:
        cb = self.on_change
        if cb:
            try:
                cb()
            except Exception:  # noqa: BLE001
                pass

    def stop(self) -> None:
        self._stop.set()
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
        if self._thread is not None:
            self._thread.join(timeout=2)
        self._sock = None
        self._thread = None
