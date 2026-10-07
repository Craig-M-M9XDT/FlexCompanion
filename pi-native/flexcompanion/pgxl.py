"""Direct 4O3A Power Genius XL telemetry over TCP 9008 (port of PgxlClient.cs, after AetherSDR's
GPLv3 PgxlConnection). The direct link is read-only telemetry; OPERATE / STANDBY still goes
through the FLEX radio's ``amplifier set`` command, as in the .NET build.
"""
from __future__ import annotations

import itertools
import math
import socket
import threading
import time
from typing import Callable, Dict, Optional

POLL_S = 0.25
RETRY_S = 5.0


def dbm_to_watts(dbm: Optional[str]) -> float:
    try:
        return math.pow(10, float(dbm) / 10.0) / 1000.0
    except (TypeError, ValueError):
        return math.nan


def return_loss_to_swr(rl: Optional[str]) -> float:
    try:
        rho = math.pow(10, -abs(float(rl)) / 20.0)
    except (TypeError, ValueError):
        return math.nan
    return (1 + rho) / (1 - rho) if rho < 0.999 else 99.9


def read_first(d: Dict[str, str], *keys: str) -> float:
    for k in keys:
        if k in d:
            try:
                return float(d[k])
            except ValueError:
                pass
    return math.nan


def parse_kv(body: str) -> Dict[str, str]:
    out: Dict[str, str] = {}
    for part in body.split():
        eq = part.find("=")
        if eq > 0:
            out[part[:eq].lower()] = part[eq + 1:]
    return out


class PgxlTelemetry:
    """Latest readings, same field mapping as the .NET StationViewModel.ApplyPgxlStatus."""

    def __init__(self) -> None:
        self.power_w = math.nan
        self.swr = math.nan
        self.current_a = math.nan
        self.temp_c = math.nan
        self.vdd = math.nan
        self.vac = math.nan
        self.state = ""

    def apply(self, d: Dict[str, str]) -> None:
        if "fwd" in d:
            self.power_w = dbm_to_watts(d["fwd"])
        if "swr" in d:
            self.swr = return_loss_to_swr(d["swr"])
        for attr, keys in (("current_a", ("id", "current", "idd")), ("temp_c", ("temp", "temperature", "patemp")),
                           ("vdd", ("vdd", "vpa", "voltage")), ("vac", ("vac", "mains"))):
            v = read_first(d, *keys)
            if not math.isnan(v):
                setattr(self, attr, v)
        if "state" in d:
            self.state = d["state"]

    @staticmethod
    def fmt(v: float, unit: str, digits: int = 1) -> str:
        return "—" if math.isnan(v) else f"{v:.{digits}f} {unit}"


class PgxlClient:
    def __init__(self) -> None:
        self.on_connected: Optional[Callable[[], None]] = None
        self.on_disconnected: Optional[Callable[[], None]] = None
        self.on_error: Optional[Callable[[str], None]] = None
        self.on_status: Optional[Callable[[Dict[str, str]], None]] = None
        self.on_alert: Optional[Callable[[str], None]] = None
        self.auto_reconnect = True
        self.retry_s = RETRY_S
        self.connected = False
        self.version = ""
        self.host = ""
        self.port = 9008
        self._sock: Optional[socket.socket] = None
        self._wlock = threading.Lock()
        self._seq = itertools.count(1)
        self._gen = 0
        self._intentional = False

    def connect(self, host: str, port: int = 9008) -> None:
        self.host = host.strip()
        self.port = port if port > 0 else 9008
        if not self.host:
            raise ValueError("Enter the Power Genius XL IP address.")
        self._intentional = False
        self._start()

    def _start(self) -> None:
        self._gen += 1
        gen = self._gen
        self._close_socket()
        threading.Thread(target=self._run, args=(gen,), name="pgxl", daemon=True).start()

    def _emit(self, cb, *args) -> None:
        if cb:
            try:
                cb(*args)
            except Exception:  # noqa: BLE001
                pass

    def send(self, command: str) -> None:
        s = self._sock
        if s is None:
            return
        try:
            with self._wlock:                 # poll loop and commands must not interleave bytes
                s.sendall(f"C{next(self._seq)}|{command}\n".encode())
        except OSError:
            pass

    def _run(self, gen: int) -> None:
        try:
            sock = socket.create_connection((self.host, self.port), timeout=5)
            sock.settimeout(None)
        except OSError as ex:
            if gen == self._gen:
                self._emit(self.on_error, f"Amplifier: {ex.strerror or ex}")
                self._retry(gen)
            return
        if gen != self._gen:
            sock.close()
            return
        self._sock = sock
        self.version = ""
        buf = b""
        try:
            while gen == self._gen:
                data = sock.recv(4096)
                if not data:
                    break
                buf += data
                while b"\n" in buf:
                    raw, buf = buf.split(b"\n", 1)
                    line = raw.decode("utf-8", "replace").strip()
                    if line:
                        self._process(line, gen)
        except OSError as ex:
            if gen == self._gen:
                self._emit(self.on_error, f"Amplifier: {ex.strerror or ex}")
        if gen != self._gen:
            return
        self.connected = False
        self._close_socket()
        self._emit(self.on_disconnected)
        self._retry(gen)

    def _process(self, line: str, gen: int) -> None:
        if not self.connected and line.startswith("V"):
            self.version = line[1:]
            self.connected = True
            self._emit(self.on_connected)
            self.send("info")
            self.send("status")
            threading.Thread(target=self._poll, args=(gen,), name="pgxl-poll", daemon=True).start()
            return
        if line.startswith("M|"):
            self._emit(self.on_alert, line[2:].strip())
            return
        body = ""
        if line.startswith("R"):
            parts = line.split("|", 2)
            if len(parts) < 3:
                return
            code = parts[1].strip()
            if code and code != "0":
                self._emit(self.on_error, f"PGXL refused command: {code}")
                return
            body = parts[2].strip()
        elif line.startswith("S"):
            p = line.find("|")
            if p < 0:
                return
            body = line[p + 1:].strip()
            first_eq = body.find("=")
            if first_eq > 0:                 # drop the object name before the first key=value
                sp = body.rfind(" ", 0, first_eq)
                if sp >= 0:
                    body = body[sp + 1:]
        else:
            return
        d = parse_kv(body)
        if d:
            self._emit(self.on_status, d)

    def _poll(self, gen: int) -> None:
        while not self._intentional and self.connected and gen == self._gen:
            self.send("status")
            time.sleep(POLL_S)

    def _retry(self, gen: int) -> None:
        if self._intentional or not self.auto_reconnect:
            return

        def later():
            time.sleep(self.retry_s)
            if not self._intentional and gen == self._gen and not self.connected:
                self._start()
        threading.Thread(target=later, name="pgxl-retry", daemon=True).start()

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
        self._gen += 1
        self._close_socket()
        was = self.connected
        self.connected = False
        if was:
            self._emit(self.on_disconnected)
