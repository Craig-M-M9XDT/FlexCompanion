"""Telnet DX-cluster client (port of DxClusterClient.cs, after AetherSDR's GPLv3 design).

Works with DX Spider, AR-Cluster and CC Cluster: answers the login prompt with your callsign
(prompts often arrive without a line ending), refuses Telnet option negotiation, parses
``DX de`` spot lines and reconnects with back-off. Callbacks run on the reader thread; the
station layer marshals them onto its dispatcher.
"""
from __future__ import annotations

import re
import socket
import threading
import time
from dataclasses import dataclass, field
from typing import Callable, Optional

SPOT_RX = re.compile(r"^DX\s+de\s+(\S+?):\s+(\d+\.?\d*)\s+(\S+)\s+(.*?)\s+(\d{4})Z", re.IGNORECASE)
IAC, WILL, WONT, DO, DONT = 0xFF, 0xFB, 0xFC, 0xFD, 0xFE


@dataclass
class DxSpot:
    spotter: str
    frequency_mhz: float
    callsign: str
    comment: str
    utc: str
    received: float = field(default_factory=time.time)

    @property
    def frequency_text(self) -> str:
        return f"{self.frequency_mhz:.4f}"


def parse_spot(line: str) -> Optional[DxSpot]:
    m = SPOT_RX.match(line)
    if not m:
        return None
    try:
        mhz = float(m.group(2)) / 1000.0
    except ValueError:
        return None
    if mhz <= 0:
        return None
    return DxSpot(m.group(1), mhz, m.group(3), m.group(4).strip(), m.group(5) + "Z")


def is_login_prompt(line: str) -> bool:
    low = line.lower().strip()
    return (low.endswith(("login:", "call:", "callsign:")) or "enter your call" in low
            or "your call>" in low)


def clean(s: str) -> str:
    return "".join(c for c in s if c == "\t" or (c >= " " and not ("\x7f" <= c <= "\x9f"))).strip()


class DxClusterClient:
    def __init__(self) -> None:
        self.on_connected: Optional[Callable[[], None]] = None
        self.on_disconnected: Optional[Callable[[], None]] = None
        self.on_error: Optional[Callable[[str], None]] = None
        self.on_line: Optional[Callable[[str], None]] = None
        self.on_spot: Optional[Callable[[DxSpot], None]] = None
        self.auto_reconnect = True
        self.reconnect_base_s = 5.0
        self.connected = False
        self.host = ""
        self.port = 7300
        self.callsign = ""
        self._sock: Optional[socket.socket] = None
        self._gen = 0
        self._lock = threading.Lock()
        self._intentional = False
        self._attempt = 0

    def connect(self, host: str, port: int, callsign: str) -> None:
        """Starts connecting in the background; results arrive through the callbacks."""
        self.host = host.strip()
        self.port = port if port > 0 else 7300
        self.callsign = callsign.strip().upper()
        if not self.host:
            raise ValueError("Enter the DX cluster host.")
        if not self.callsign:
            raise ValueError("Enter your callsign for the DX cluster login.")
        self._intentional = False
        self._attempt = 0
        self._start()

    def _start(self) -> None:
        with self._lock:
            self._gen += 1
            gen = self._gen
        self._close_socket()
        threading.Thread(target=self._run, args=(gen,), name="dxcluster", daemon=True).start()

    def _emit(self, cb, *args) -> None:
        if cb:
            try:
                cb(*args)
            except Exception:  # noqa: BLE001
                pass

    def _run(self, gen: int) -> None:
        try:
            sock = socket.create_connection((self.host, self.port), timeout=10)
            sock.settimeout(None)
        except OSError as ex:
            if gen == self._gen:
                self._emit(self.on_error, f"DX cluster: {ex.strerror or ex}")
                self._schedule_reconnect(gen)
            return
        if gen != self._gen:
            sock.close()
            return
        self._sock = sock
        self.connected = True
        self._attempt = 0
        self._emit(self.on_connected)
        logged_in = False
        line = bytearray()
        try:
            while gen == self._gen:
                data = sock.recv(4096)
                if not data:
                    break
                i = 0
                while i < len(data):
                    b = data[i]
                    if b == IAC and i + 1 < len(data):
                        cmd = data[i + 1]
                        if cmd in (WILL, WONT, DO, DONT) and i + 2 < len(data):
                            reply = DONT if cmd in (WILL, WONT) else WONT
                            try:
                                sock.sendall(bytes([IAC, reply, data[i + 2]]))
                            except OSError:
                                pass
                            i += 3
                        else:
                            i += 2
                        continue
                    if b in (10, 13):
                        if line:
                            logged_in = self._handle(line.decode("latin-1"), logged_in, gen)
                            line.clear()
                    else:
                        line.append(b)
                    i += 1
                # an unterminated login prompt waiting for input
                if not logged_in and line and is_login_prompt(clean(line.decode("latin-1"))):
                    self._emit(self.on_line, clean(line.decode("latin-1")))
                    line.clear()
                    self.send(self.callsign)
                    logged_in = True
                if len(line) > 4096:
                    line.clear()
        except OSError as ex:
            if gen == self._gen:
                self._emit(self.on_error, f"DX cluster: {ex.strerror or ex}")
        if gen != self._gen:
            return
        self.connected = False
        self._close_socket()
        self._emit(self.on_disconnected)
        self._schedule_reconnect(gen)

    def _handle(self, raw: str, logged_in: bool, gen: int) -> bool:
        text = clean(raw)
        if not text or gen != self._gen:
            return logged_in
        self._emit(self.on_line, text)
        if not logged_in and is_login_prompt(text):
            self.send(self.callsign)
            return True
        spot = parse_spot(text)
        if spot is not None:
            self._emit(self.on_spot, spot)
        return logged_in

    def _schedule_reconnect(self, gen: int) -> None:
        if self._intentional or not self.auto_reconnect:
            return
        delay = min(self.reconnect_base_s * (1 << min(self._attempt, 4)), 60.0)
        self._attempt += 1

        def later():
            time.sleep(delay)
            if not self._intentional and gen == self._gen and not self.connected:
                self._start()
        threading.Thread(target=later, name="dxcluster-retry", daemon=True).start()

    def send(self, command: str) -> None:
        sock = self._sock
        if sock is None:
            return
        try:
            sock.sendall((command.rstrip() + "\r\n").encode("latin-1", "replace"))
        except OSError:
            pass

    def _close_socket(self) -> None:
        s, self._sock = self._sock, None
        if s is not None:
            try:
                s.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            s.close()

    def disconnect(self) -> None:
        self._intentional = True
        with self._lock:
            self._gen += 1
        if self.connected:
            self.send("bye")
        self._close_socket()
        was = self.connected
        self.connected = False
        if was:
            self._emit(self.on_disconnected)
