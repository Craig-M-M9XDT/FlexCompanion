"""A simulated FLEX radio for testing and demos (no hardware needed).

Implements enough of the SmartSDR TCP API, VITA-49 meters, DAX IQ and LAN discovery
to drive Flex Control Companion end to end::

    flexcompanion-sim                       # listen on 127.0.0.1:4992
    flexcompanion-sim --host 0.0.0.0 --discovery   # visible on the LAN
    flexcompanion-native --demo             # start the app with a built-in simulator
"""
from __future__ import annotations

import argparse
import math
import random
import socket
import struct
import threading
import time
from typing import Dict, List, Optional, Set, Tuple

import numpy as np

from . import kv

PAN_ID = "0x40000000"
GUI_HANDLE = "0x2A3B4C5D"


def vita_packet(pcc: int, stream_id: int, payload: bytes) -> bytes:
    words = (28 + len(payload) + 3) // 4
    header = bytearray(28)
    header[0] = 0x38                       # extension data with stream id, class id present
    header[1] = 0x00
    struct.pack_into(">H", header, 2, words)
    struct.pack_into(">I", header, 4, stream_id)
    struct.pack_into(">I", header, 8, 0x00001C2D)   # FlexRadio OUI
    struct.pack_into(">HH", header, 12, 0x534C, pcc)
    pad = b"\x00" * (words * 4 - 28 - len(payload))
    return bytes(header) + payload + pad


class SimRadio:
    def __init__(self, host: str = "127.0.0.1", port: int = 4992, model: str = "FLEX-6600",
                 serial: str = "1234-5678-9ABC-DEF0", nickname: str = "SimRadio",
                 unsupported: Optional[Set[str]] = None, with_gui_station: bool = True):
        self.host = host
        self.port = port
        self.model = model
        self.serial = serial
        self.nickname = nickname
        # Keys rejected with 0x5000002D, like a FLEX-6000 without the 8000-series DSP.
        self.unsupported = set(unsupported if unsupported is not None else {"nrf", "speex_nr", "rnnoise"})
        self.with_gui_station = with_gui_station
        self.log: List[str] = []
        self.transmitting = False
        self.slices: Dict[int, kv.CIDict] = {
            0: kv.CIDict(in_use="1", RF_frequency="14.200000", mode="USB", pan=PAN_ID, active="1",
                         index_letter="A", client_handle=GUI_HANDLE, filter_lo="100", filter_hi="2800",
                         dax="1", nr="0", nr_level="50", nb="0", nb_level="50", anf="0", anf_level="50",
                         nrl="0", lms_nr_level="50", anfl="0", lms_anf_level="50", anft="0",
                         diversity="0", agc_mode="med", agc_threshold="65"),
        }
        self.pans: Dict[str, kv.CIDict] = {
            PAN_ID: kv.CIDict(center="14.200000", bandwidth="0.192000", daxiq_channel="0",
                              noise_floor_position="75", noise_floor_position_enable="0"),
        }
        self.meter_defs = [
            (1, "SLC", 0, "LEVEL", "dBm"), (2, "TX-", -1, "FWDPWR", "dBm"), (3, "TX-", -1, "SWR", "SWR"),
            (4, "COD-", -1, "MICPEAK", "dBFS"), (5, "COD-", -1, "COMPPEAK", "dB"),
            (6, "RAD", -1, "+13.8A", "Volts"), (7, "RAD", -1, "PATEMP", "degC"), (8, "RAD", -1, "PACURRENT", "Amps"),
        ]
        self._clients: List["_Conn"] = []
        self._lock = threading.Lock()
        self._stop = threading.Event()
        self._server: Optional[socket.socket] = None
        self._threads: List[threading.Thread] = []
        self.iq_streams: Dict[int, dict] = {}
        self._next_stream = 0x20000001

    # ───────────────────────── lifecycle ─────────────────────────

    def start(self) -> "SimRadio":
        s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        s.bind((self.host, self.port))
        s.listen(4)
        s.settimeout(0.5)
        self.port = s.getsockname()[1]
        self._server = s
        for fn in (self._accept_loop, self._meter_loop, self._iq_loop):
            t = threading.Thread(target=fn, daemon=True, name=f"sim-{fn.__name__}")
            t.start()
            self._threads.append(t)
        return self

    def start_discovery(self, target: str = "255.255.255.255", port: int = 4992, ip: Optional[str] = None) -> None:
        def loop():
            u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            u.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
            adv_ip = ip or (self.host if self.host not in ("0.0.0.0", "") else _local_ip())
            body = (f"discovery_protocol_version=3.0.0.2 model={self.model} serial={self.serial} "
                    f"version=3.8.23.35640 nickname={self.nickname} callsign=M9XDT ip={adv_ip} port={self.port} "
                    f"status=Available gui_client_stations=SHACK").encode()
            pkt = vita_packet(0xFFFF, 0x800, body)
            while not self._stop.is_set():
                try:
                    u.sendto(pkt, (target, port))
                except OSError:
                    pass
                self._stop.wait(1.0)
            u.close()
        t = threading.Thread(target=loop, daemon=True, name="sim-discovery")
        t.start()
        self._threads.append(t)

    def stop(self) -> None:
        self._stop.set()
        if self._server:
            self._server.close()
        with self._lock:
            for c in list(self._clients):
                c.close()

    # ───────────────────────── TCP ─────────────────────────

    def _accept_loop(self) -> None:
        handle = 0x10000001
        while not self._stop.is_set():
            try:
                sock, addr = self._server.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            conn = _Conn(self, sock, addr[0], f"0x{handle:08X}")
            handle += 1
            with self._lock:
                self._clients.append(conn)
            threading.Thread(target=conn.run, daemon=True, name="sim-conn").start()

    def broadcast(self, status: str, subscription: str) -> None:
        with self._lock:
            clients = list(self._clients)
        for c in clients:
            if subscription in c.subs:
                c.status(status)

    def slice_status(self, idx: int, d: Dict[str, str]) -> str:
        return f"slice {idx} " + " ".join(f"{k}={v}" for k, v in d.items())

    def handle_command(self, conn: "_Conn", cmd: str) -> Tuple[int, str]:
        self.log.append(cmd)
        tok = kv.tokenize(cmd)
        if not tok:
            return 0x50000016, ""
        head = tok[0]
        if head == "info":
            return 0, (f'model="{self.model}",chassis_serial="{self.serial}",name="{self.nickname}",'
                       f'nickname="{self.nickname}",callsign="M9XDT"')
        if head == "client":
            if len(tok) >= 3 and tok[1] == "udpport":
                conn.udp_port = int(tok[2])
            return 0, ""
        if head == "sub" and len(tok) >= 2:
            what = tok[1]
            if what == "meter":
                if tok[2] == "all":
                    conn.meter_subs = {m[0] for m in self.meter_defs}
                else:
                    conn.meter_subs.add(int(tok[2]))
                return 0, ""
            conn.subs.add(what)
            conn.after_reply.append(lambda: self._initial_status(conn, what))
            return 0, ""
        if head == "unsub" and len(tok) >= 3 and tok[1] == "meter":
            if tok[2] == "all":
                conn.meter_subs.clear()
            else:
                conn.meter_subs.discard(int(tok[2]))
            return 0, ""
        if head == "meter" and len(tok) >= 2 and tok[1] == "list":
            parts = []
            for mid, src, num, nam, unit in self.meter_defs:
                parts += [f"{mid}.src={src}", f"{mid}.num={num}", f"{mid}.nam={nam}", f"{mid}.unit={unit}"]
            return 0, "#".join(parts) + "#"
        if head == "slice" and len(tok) >= 3:
            return self._slice_cmd(tok)
        if head == "display" and len(tok) >= 4 and tok[1] == "pan" and tok[2] == "set":
            pan = self.pans.get(tok[3])
            if pan is None:
                return 0x50000004, ""
            d = kv.parse(tok[4:])
            pan.update(d)
            self.broadcast(f"display pan {tok[3]} " + " ".join(f"{k}={v}" for k, v in d.items()), "pan")
            return 0, ""
        if head == "stream":
            return self._stream_cmd(conn, tok)
        if head in ("xmit", "transmit"):
            on = tok[-1] == "1"
            self.transmitting = on
            self.broadcast(f"interlock state={'TRANSMITTING' if on else 'READY'}", "tx")
            return 0, ""
        if head == "atu":
            return 0, ""
        if head == "profile":
            return 0, ""
        return 0, ""

    def _slice_cmd(self, tok: List[str]) -> Tuple[int, str]:
        action = tok[1]
        try:
            idx = int(tok[2])
        except ValueError:
            return 0x50000004, ""
        s = self.slices.get(idx)
        if s is None:
            return 0x50000004, ""
        if action == "tune" and len(tok) >= 4:
            f = f"{float(tok[3]):.6f}"
            s["RF_frequency"] = f
            self.pans[s["pan"]]["center"] = f
            self.broadcast(self.slice_status(idx, {"RF_frequency": f}), "slice")
            self.broadcast(f"display pan {s['pan']} center={f}", "pan")
            return 0, ""
        if action == "set":
            d = kv.parse(tok[3:])
            for k in d:
                if k in self.unsupported:
                    return 0x5000002D, ""
            if "mode" in d and d["mode"].upper() == "DIGU" and any(k in ("anf", "lms_anf", "anft") for k in d):
                return 0x50000061, ""
            out = {}
            status_key = {"lms_nr": "nrl", "lms_anf": "anfl", "speex_nr": "nrs", "rnnoise": "rnn"}
            for k, v in d.items():
                sk = status_key.get(k, k)
                s[sk] = v
                out[sk] = v
            if "mode" in d:
                lo, hi = {"USB": ("100", "2800"), "LSB": ("-2800", "-100"), "CW": ("-250", "250"),
                          "DIGU": ("0", "3000")}.get(d["mode"].upper(), ("-3000", "3000"))
                s["filter_lo"], s["filter_hi"] = lo, hi
                out["filter_lo"], out["filter_hi"] = lo, hi
            self.broadcast(self.slice_status(idx, out), "slice")
            return 0, ""
        if action == "auto_tune":
            return 0, ""
        return 0, ""

    def _stream_cmd(self, conn: "_Conn", tok: List[str]) -> Tuple[int, str]:
        if len(tok) >= 2 and tok[1] == "create":
            d = kv.parse(tok[2:])
            if d.get("type") == "dax_iq":
                sid = self._next_stream
                self._next_stream += 1
                self.iq_streams[sid] = {"conn": conn, "rate": 48000, "ch": int(d.get("daxiq_channel", "1")), "phase": 0.0}
                return 0, f"0x{sid:08X}"
            return 0, "0x04000008"
        if len(tok) >= 3 and tok[1] == "set":
            sid = kv.parse_hex_id(tok[2])
            d = kv.parse(tok[3:])
            if sid in self.iq_streams and "daxiq_rate" in d:
                self.iq_streams[sid]["rate"] = int(d["daxiq_rate"])
            return 0, ""
        if len(tok) >= 3 and tok[1] == "remove":
            self.iq_streams.pop(kv.parse_hex_id(tok[2]), None)
            return 0, ""
        return 0x50000016, ""

    def _initial_status(self, conn: "_Conn", what: str) -> None:
        if what == "client" and self.with_gui_station:
            conn.status(f"client {GUI_HANDLE} connected local_ptt=1 client_id=8B1D9A6C-0000-4C3E-9C4B-111122223333 "
                        f"program=SmartSDR-Win station=SHACK")
        elif what == "slice":
            for idx, s in self.slices.items():
                conn.status(self.slice_status(idx, s))
        elif what == "pan":
            for pid, p in self.pans.items():
                conn.status(f"display pan {pid} " + " ".join(f"{k}={v}" for k, v in p.items()))
        elif what == "tx":
            conn.status("interlock state=READY")
        elif what == "license":
            conn.status("license feature name=NOISE_REDUCTION enabled=1 reason=license")

    # ───────────────────────── UDP ─────────────────────────

    def _meter_loop(self) -> None:
        u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        t0 = time.monotonic()
        while not self._stop.wait(0.05):
            t = time.monotonic() - t0
            rx = -97 + 18 * max(0.0, math.sin(t * 0.7)) + random.uniform(-2, 2)
            fwd_w = 100.0 if self.transmitting else 0.0
            vals = {
                1: rx * 128, 2: (10 * math.log10(max(1e-3, fwd_w * 1000)) if fwd_w else -30) * 128,
                3: (1.3 if self.transmitting else 1.0) * 128, 4: (-12 if self.transmitting else -60) * 128,
                5: (8 if self.transmitting else 0) * 128, 6: 13.8 * 256, 7: 38.5 * 64,
                8: (18.0 if self.transmitting else 1.2) * 256,
            }
            with self._lock:
                clients = list(self._clients)
            for c in clients:
                if not c.udp_port or not c.meter_subs:
                    continue
                payload = b"".join(struct.pack(">Hh", mid, int(max(-32768, min(32767, vals[mid]))))
                                   for mid in sorted(c.meter_subs) if mid in vals)
                try:
                    u.sendto(vita_packet(0x8002, 0x00000700, payload), (c.addr, c.udp_port))
                except OSError:
                    pass
        u.close()

    def _iq_loop(self) -> None:
        u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        pairs = 512
        rng = np.random.default_rng(7)
        next_t = time.monotonic()
        while not self._stop.is_set():
            streams = list(self.iq_streams.items())
            if not streams:
                self._stop.wait(0.05)
                next_t = time.monotonic()
                continue
            rate = max(s["rate"] for _, s in streams)
            for sid, st in streams:
                c = st["conn"]
                if not c.udp_port:
                    continue
                r = st["rate"]
                # Two tones: one 1.5 kHz above the slice (in a USB passband), one at -9 kHz.
                s0 = self.slices[0]
                off = (float(s0["RF_frequency"]) - float(self.pans[s0["pan"]]["center"])) * 1e6
                n = np.arange(pairs) + st["phase"]
                st["phase"] += pairs
                sig = 0.05 * np.exp(2j * np.pi * (off + 1500) * n / r) + 0.01 * np.exp(2j * np.pi * (off - 9000) * n / r)
                noise = (rng.standard_normal(pairs) + 1j * rng.standard_normal(pairs)) * 0.0004
                x = (sig + noise).astype(np.complex64)
                inter = np.empty(pairs * 2, dtype="<f4")
                inter[0::2] = x.real
                inter[1::2] = x.imag
                try:
                    u.sendto(vita_packet(0x02E3, sid, inter.tobytes()), (c.addr, c.udp_port))
                except OSError:
                    pass
            next_t += pairs / rate
            delay = next_t - time.monotonic()
            if delay > 0:
                self._stop.wait(delay)
            elif delay < -0.5:
                next_t = time.monotonic()
        u.close()


class _Conn:
    def __init__(self, radio: SimRadio, sock: socket.socket, addr: str, handle: str):
        self.radio = radio
        self.sock = sock
        self.addr = addr
        self.handle = handle
        self.subs: Set[str] = set()
        self.meter_subs: Set[int] = set()
        self.udp_port = 0
        self.after_reply: list = []
        self._wlock = threading.Lock()
        self._closed = False

    def write(self, line: str) -> None:
        if self._closed:
            return
        try:
            with self._wlock:
                self.sock.sendall((line + "\n").encode())
        except OSError:
            self._closed = True

    def status(self, body: str) -> None:
        self.write(f"S{self.handle[2:]}|{body}")

    def run(self) -> None:
        self.write("V1.4.0.0")
        self.write(f"H{self.handle[2:]}")
        buf = b""
        try:
            while not self._closed:
                chunk = self.sock.recv(65536)
                if not chunk:
                    break
                buf += chunk
                while b"\n" in buf:
                    raw, buf = buf.split(b"\n", 1)
                    line = raw.decode(errors="replace").strip()
                    if not line.startswith("C") or "|" not in line:
                        continue
                    seq, cmd = line[1:].split("|", 1)
                    code, msg = self.radio.handle_command(self, cmd)
                    self.write(f"R{seq}|{code:X}|{msg}" if code else f"R{seq}|0|{msg}")
                    pending, self.after_reply = self.after_reply, []
                    for fn in pending:
                        fn()
        except OSError:
            pass
        self.close()

    def close(self) -> None:
        self._closed = True
        try:
            self.sock.close()
        except OSError:
            pass
        with self.radio._lock:
            if self in self.radio._clients:
                self.radio._clients.remove(self)


def _local_ip() -> str:
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("10.255.255.255", 1))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except OSError:
        return "127.0.0.1"


def main(argv=None) -> None:
    ap = argparse.ArgumentParser(description="Simulated FLEX radio for Flex Control Companion")
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=4992)
    ap.add_argument("--model", default="FLEX-6600")
    ap.add_argument("--discovery", action="store_true", help="broadcast LAN discovery packets")
    args = ap.parse_args(argv)
    r = SimRadio(args.host, args.port, model=args.model).start()
    if args.discovery:
        r.start_discovery()
    print(f"Simulated {args.model} on {args.host}:{r.port}  (Ctrl+C to stop)")
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        r.stop()


if __name__ == "__main__":
    main()
