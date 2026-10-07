"""SmartSDR API client: TCP 4992 command/status channel plus a UDP socket that receives
FLEX VITA-49 meter / audio / IQ packets (port of FlexClient.cs).

Connects as a non-GUI companion client, so it runs alongside SmartSDR / AetherSDR.
Two background threads are used: one reads TCP lines, one receives UDP datagrams.
Callbacks are invoked on those threads; the session layer marshals them onto its
own dispatcher, so nothing here touches UI state.
"""
from __future__ import annotations

import itertools
import socket
import threading
from concurrent.futures import Future
from typing import Callable, Dict, Optional, Tuple

from . import _core

Reply = Tuple[int, str]
NO_REPLY = 0xFFFFFFFF
BAD_REPLY = 0xFFFFFFFE

ERROR_TEXT = {
    0x50000003: "radio rejected this feature / entitlement check",
    0x5000002D: "bad / unsupported field on this radio / firmware",
    0x5000002F: "mode not implemented by this radio",
    0x50000032: "invalid mode",
    0x50000061: "DSP algorithm is invalid for the current mode",
    0x50000085: "command is invalid for the current mode",
    0x50004001: "command refused by radio in the current context",
    0xE2000000: "general radio error / operation rejected in the current state",
    0x50000004: "parameter error",
    0x50000005: "wrong number or type of parameters",
    0x50000016: "malformed command",
    0x5000002C: "wrong number of parameters",
    NO_REPLY: "no reply from radio",
}


def error_text(code: int) -> str:
    return ERROR_TEXT.get(code, f"error 0x{code:08X}")


class FlexClient:
    def __init__(self) -> None:
        self.on_status: Optional[Callable[[str], None]] = None
        self.on_message: Optional[Callable[[str], None]] = None
        self.on_disconnected: Optional[Callable[[str], None]] = None
        self.on_meter_packet: Optional[Callable[[], None]] = None
        # (packet_class_code, stream_id, datagram) for non-meter VITA packets (audio / IQ).
        self.on_stream_packet: Optional[Callable[[int, int, bytes], None]] = None

        self.handle = ""
        self.protocol_version = ""
        self.host = ""
        self.udp_port = 0
        self.meter_packets = 0
        # Latest raw value per meter id, written by the UDP thread.
        self.meters: Dict[int, int] = {}

        self._tcp: Optional[socket.socket] = None
        self._udp: Optional[socket.socket] = None
        self._pending: Dict[int, Future] = {}
        self._pending_lock = threading.Lock()
        self._write_lock = threading.Lock()
        self._seq = itertools.count(1)
        self._handle_ready = threading.Event()
        self._closed = False
        self._threads: list = []

    # ───────────────────────── connection ─────────────────────────

    def connect(self, host: str, port: int = 4992, timeout: float = 5.0) -> None:
        self.host = host
        tcp = socket.create_connection((host, port), timeout=timeout)
        if tcp.getsockname() == tcp.getpeername():
            # Linux "TCP self-connect": dialling a closed local port can connect a socket to itself.
            tcp.close()
            raise ConnectionRefusedError(f"nothing listening on {host}:{port}")
        tcp.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        tcp.settimeout(None)
        self._tcp = tcp

        udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        try:
            udp.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4 * 1024 * 1024)
        except OSError:
            pass
        udp.bind(("0.0.0.0", 0))
        udp.settimeout(0.5)
        self._udp = udp
        self.udp_port = udp.getsockname()[1]

        for target, name in ((self._read_loop, "flex-tcp"), (self._udp_loop, "flex-udp")):
            t = threading.Thread(target=target, name=f"{name}-{host}", daemon=True)
            t.start()
            self._threads.append(t)

        if not self._handle_ready.wait(timeout):
            self.close()
            raise TimeoutError(f"no handle from {host}:{port}")

    @property
    def connected(self) -> bool:
        return self._tcp is not None and not self._closed

    # ───────────────────────── commands ─────────────────────────

    def _write(self, seq: int, command: str) -> None:
        tcp = self._tcp
        if tcp is None or self._closed:
            raise ConnectionError("not connected")
        data = f"C{seq}|{command}\n".encode("ascii", "replace")
        with self._write_lock:
            tcp.sendall(data)

    def send_async(self, command: str) -> "Future[Reply]":
        fut: Future = Future()
        if self._tcp is None or self._closed:
            fut.set_result((NO_REPLY, "not connected"))
            return fut
        seq = next(self._seq)
        with self._pending_lock:
            self._pending[seq] = fut
        try:
            self._write(seq, command)
        except Exception as ex:  # noqa: BLE001
            with self._pending_lock:
                self._pending.pop(seq, None)
            if not fut.done():
                fut.set_result((NO_REPLY, str(ex)))
        return fut

    def send(self, command: str, timeout: float = 5.0) -> Reply:
        """Blocking send. Never call from the TCP reader thread."""
        fut = self.send_async(command)
        try:
            return fut.result(timeout)
        except Exception:  # noqa: BLE001 - timeout
            return (NO_REPLY, "no reply")

    def send_no_reply(self, command: str) -> bool:
        """Best-effort write used during shutdown; the reply is intentionally unmatched."""
        if not command.strip():
            return False
        try:
            self._write(next(self._seq), command)
            return True
        except Exception:  # noqa: BLE001
            return False

    # ───────────────────────── reader threads ─────────────────────────

    def _read_loop(self) -> None:
        reason = "connection closed by radio"
        tcp = self._tcp
        buf = b""
        try:
            while not self._closed and tcp is not None:
                chunk = tcp.recv(65536)
                if not chunk:
                    break
                buf += chunk
                while True:
                    nl = buf.find(b"\n")
                    if nl < 0:
                        break
                    line = buf[:nl].rstrip(b"\r").decode("ascii", "replace")
                    buf = buf[nl + 1:]
                    if line:
                        self._handle_line(line)
        except OSError as ex:
            reason = str(ex) or reason
        self._fail_pending("disconnected")
        if not self._closed:
            self._closed = True
            cb = self.on_disconnected
            if cb:
                cb(reason)

    def _handle_line(self, line: str) -> None:
        kind = line[0]
        if kind == "V":
            self.protocol_version = line[1:]
        elif kind == "H":
            self.handle = line[1:]
            self._handle_ready.set()
        elif kind == "R":
            parts = line[1:].split("|", 2)
            if len(parts) >= 2:
                try:
                    seq = int(parts[0])
                except ValueError:
                    return
                with self._pending_lock:
                    fut = self._pending.pop(seq, None)
                if fut is not None and not fut.done():
                    try:
                        code = int(parts[1], 16) if parts[1] else 0
                    except ValueError:
                        code = BAD_REPLY
                    fut.set_result((code, parts[2] if len(parts) > 2 else ""))
        elif kind == "S":
            bar = line.find("|")
            if bar > 0 and self.on_status:
                self.on_status(line[bar + 1:])
        elif kind == "M":
            bar = line.find("|")
            if self.on_message:
                self.on_message(line[bar + 1:] if bar > 0 else line[1:])

    def _udp_loop(self) -> None:
        udp = self._udp
        while not self._closed and udp is not None:
            try:
                data = udp.recv(65536)
            except socket.timeout:
                continue
            except OSError:
                break
            try:
                self._dispatch_udp(data)
            except Exception:  # noqa: BLE001 - never stop the UDP loop
                pass

    def _dispatch_udp(self, data: bytes) -> None:
        pcc, sid = _core.peek(data)
        if pcc == 0:
            return
        if pcc == _core.PCC_METER:
            meters = self.meters
            for mid, val in _core.parse_meters(data):
                meters[mid] = val
            self.meter_packets += 1
            cb = self.on_meter_packet
            if cb:
                cb()
            return
        cb = self.on_stream_packet
        if cb:
            cb(pcc, sid, data)

    def _fail_pending(self, why: str) -> None:
        with self._pending_lock:
            pending = list(self._pending.values())
            self._pending.clear()
        for f in pending:
            if not f.done():
                f.set_result((NO_REPLY, why))

    def close(self) -> None:
        if self._closed and self._tcp is None:
            return
        self._closed = True
        for s in (self._tcp, self._udp):
            if s is not None:
                try:
                    s.shutdown(socket.SHUT_RDWR)
                except OSError:
                    pass
                try:
                    s.close()
                except OSError:
                    pass
        self._tcp = None
        self._udp = None
        self._fail_pending("disposed")
