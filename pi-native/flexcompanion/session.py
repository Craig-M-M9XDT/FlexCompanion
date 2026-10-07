"""One radio slot: connection, slices, stations, DSP controls, meters and spectrum.

Port of the .NET ``RadioViewModel`` (Core / Meters / Audio parts) without the UI.
All state lives on the dispatcher thread. Network threads post into it; blocking
command sequences (connect handshake, meter subscriptions, DAX-IQ setup) run on a
small worker pool and post their results back. Subscribers receive ``notify(kind)``
calls on the dispatcher thread, where kind is one of::

    connection  slices  stations  selection  controls  meters  spectrum  message  amplifier
"""
from __future__ import annotations

import math
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Callable, Dict, List, Optional, Tuple

import numpy as np

from . import _core, kv, meters, spectrum
from .client import NO_REPLY, FlexClient, error_text
from .dispatch import Dispatcher, TimerHandle
from .params import ParamControl, build_controls
from .session_aether import AETHER_STATUS, AetherMixin
from .session_agc import AUDIO_PCCS, AgcMixin

UNKNOWN_PARAMETER = 0x5000002D
COMMAND_REFUSED = 0x50004001
INVALID_MODE_OR_STATE = 0xE2000000
INVALID_DSP_FOR_MODE = 0x50000061
INVALID_COMMAND_FOR_MODE = 0x50000085

DIGITAL_RESTRICTED = {"DIGU", "DIGL", "RTTY", "FDV", "FDVL", "FDVU"}
CW_MODES = {"CW", "CWL", "CWR"}
LICENSED_DSP = {"NRF", "NRL", "NRS", "RNN", "ANFL", "ANFT"}

RECONNECT_S = 5.0
METER_REFRESH_S = 0.45
METER_WATCHDOG_S = 0.25


class Slice:
    def __init__(self, index: int):
        self.index = index
        self.state = kv.CIDict()
        self.station = ""

    @property
    def letter(self) -> str:
        return self.state.get("index_letter") or chr(ord("A") + self.index)

    @property
    def mode(self) -> str:
        return self.state.get("mode", "")

    @property
    def pan(self) -> str:
        return self.state.get("pan", "")

    @property
    def client_handle(self) -> str:
        return self.state.get("client_handle", "")

    @property
    def active(self) -> bool:
        return self.state.get("active") == "1"

    @property
    def is_diversity_child(self) -> bool:
        return self.state.get("diversity_child") == "1"

    @property
    def freq_mhz(self) -> Optional[float]:
        return kv.to_float(self.state.get("RF_frequency"))

    @property
    def freq_text(self) -> str:
        f = self.freq_mhz
        if f is None:
            return "-"
        hz = int(round(f * 1_000_000))
        return f"{hz // 1_000_000}.{hz // 1000 % 1000:03d}.{hz % 1000:03d}"

    def __str__(self) -> str:
        return f"{self.letter}  {self.freq_text}  {self.mode}"


class StationItem:
    def __init__(self, handle: str = "", client_id: str = "", name: str = "All stations"):
        self.handle = handle
        self.client_id = client_id
        self.name = name


ALL_STATIONS = StationItem()


class RadioSession(AgcMixin, AetherMixin):
    def __init__(self, slot: str, dispatcher: Dispatcher, saver: bool = False,
                 tx_meter: str = "Power", show_fft: bool = False, fft_span_khz: float = 48.0,
                 agc_target_db: float = -28.0, aether: bool = True):
        self.slot = slot
        self.d = dispatcher
        self._listeners: List[Callable[[str], None]] = []
        self._pool = ThreadPoolExecutor(max_workers=3, thread_name_prefix=f"flex-{slot}")

        self.client: Optional[FlexClient] = None
        self.connected = False
        self.connecting = False
        self.status_text = "Not connected"
        self.radio_title = "No radio"
        self.last_message = ""
        self.model = ""
        self.power_max = 120.0
        self._want_connected = False
        self._retrying = False
        self._reconnect: Optional[TimerHandle] = None
        self._last = ("", 4992, "", "")   # host, port, title, serial
        self._epoch = 0                    # bumps on every connect/cleanup; stale work is ignored

        self.slices: Dict[int, Slice] = {}
        self.selected_index: Optional[int] = None
        self.follow_active = True
        self.pans: Dict[str, kv.CIDict] = {}
        self.stations: List[StationItem] = [ALL_STATIONS]
        self.selected_station: StationItem = ALL_STATIONS
        self._client_station: Dict[str, str] = {}
        self._bound_client_id = ""
        self._interlock_tx = False

        self.amp_handle = ""
        self.amp_model = ""
        self.amp_ip = ""
        self.amp_state = ""
        self.amp_operate = False
        self.license: Dict[str, Tuple[bool, str]] = {}

        self.groups = build_controls()
        for c in self.all_controls:
            c.sender = self._send_scoped
            c.dispatcher = dispatcher
            c.on_change = lambda _c: self._notify("controls")

        # meters
        self.meter_defs: Dict[int, meters.MeterDef] = {}
        self.meter_ids = meters.MeterIds()
        self._meter_map_dirty = True
        self._meter_subs: set = set()
        self._meter_fallback_all = False
        self._meter_refresh_timer: Optional[TimerHandle] = None
        self._meter_refresh_busy = False
        self._meter_pending = False
        self._last_meter_packet = 0.0
        self._watchdog: Optional[TimerHandle] = None
        self.readings = meters.MeterReadings()
        self.tx_meter = tx_meter if tx_meter in meters.TX_METER_OPTIONS else "Power"

        # spectrum
        self.saver = saver
        self.show_fft = show_fft
        self.fft_span_khz = fft_span_khz if fft_span_khz in spectrum.SPAN_OPTIONS_KHZ else 48.0
        self.engine = _core.SpectrumEngine(16384)
        self.spectrum_frame: Optional[spectrum.SpectrumFrame] = None
        self.spectrum_status = ""
        self._spec_avg: Optional[np.ndarray] = None
        self._iq_stream_id = 0
        self._iq_channel = 0
        self._iq_pan = ""
        self._iq_pan_assigned = False
        self._iq_lost = False
        self._iq_rate = 48000
        self._iq_busy = False
        self._iq_recheck = False
        self._fft_pending = False
        self._fft_stop = threading.Event()
        self._fft_thread: Optional[threading.Thread] = None
        self._init_agc(agc_target_db)
        self._init_aether(aether)

    # ───────────────────────── plumbing ─────────────────────────

    def subscribe(self, fn: Callable[[str], None]) -> None:
        self._listeners.append(fn)

    def _notify(self, kind: str) -> None:
        for fn in list(self._listeners):
            try:
                fn(kind)
            except Exception:  # noqa: BLE001
                import traceback
                traceback.print_exc()

    def _set_message(self, text: str) -> None:
        self.last_message = text
        self._notify("message")

    def _post_if_current(self, epoch: int, fn: Callable[[], None]) -> None:
        def run():
            if epoch == self._epoch:
                fn()
        self.d.post(run)

    def _send(self, cmd: str, then: Optional[Callable[[int, str], None]] = None) -> None:
        """Non-blocking send; ``then(code, message)`` runs on the dispatcher."""
        c = self.client
        if c is None:
            if then:
                then(NO_REPLY, "not connected")
            return
        epoch = self._epoch
        fut = c.send_async(cmd)
        if then is not None:
            fut.add_done_callback(lambda f: self._post_if_current(epoch, lambda: then(*f.result())))

    def send(self, cmd: str) -> None:
        """Fire a command and report any error in the status line."""
        def done(code: int, _msg: str) -> None:
            if code != 0:
                self._set_message(f"{cmd}   {error_text(code)}")
        self._send(cmd, done)

    @property
    def all_controls(self) -> List[ParamControl]:
        return [c for group in self.groups.values() for c in group]

    @property
    def selected_slice(self) -> Optional[Slice]:
        return self.slices.get(self.selected_index) if self.selected_index is not None else None

    @property
    def ordered_slices(self) -> List[Slice]:
        return [self.slices[k] for k in sorted(self.slices)]

    # ───────────────────────── connection ─────────────────────────

    def connect(self, host: str, port: int = 4992, title: str = "", serial: str = "") -> None:
        self._retrying = False
        self._cancel_reconnect()
        self._connect_core(host, port, title, serial)

    def _connect_core(self, host: str, port: int, title: str, serial: str) -> None:
        self._cleanup()
        self._want_connected = True
        self._last = (host, port, title, serial or "")
        self.radio_serial = (serial or "").strip().strip('"')
        self.connecting = True
        self.status_text = f"Connecting to {host}:{port}"
        self.radio_title = title.strip() or host
        self.last_message = ""
        epoch = self._epoch
        self._notify("connection")
        self._pool.submit(self._connect_worker, epoch, host, port)

    def _connect_worker(self, epoch: int, host: str, port: int) -> None:
        c = FlexClient()
        c.on_status = lambda body: self._post_if_current(epoch, lambda: self._on_status(body))
        c.on_message = lambda m: self._post_if_current(epoch, lambda: self._set_message(m))
        c.on_disconnected = lambda why: self._post_if_current(epoch, lambda: self._on_disconnected(why))
        c.on_meter_packet = lambda: self._on_meter_packet(epoch)
        c.on_stream_packet = self._on_stream_packet
        try:
            c.connect(host, port, timeout=5.0)
        except Exception as ex:  # noqa: BLE001
            c.close()
            msg = f"No answer from {host}:{port}" if isinstance(ex, (TimeoutError, OSError)) and "timed out" in str(ex).lower() \
                else f"Connect failed: {ex}"
            self._post_if_current(epoch, lambda: self._connect_failed(msg))
            return

        # Handshake on the worker (blocking sends), then hand the client to the dispatcher.
        c.send("client program FlexCompanion")
        info = c.send("info")
        udp = c.send(f"client udpport {c.udp_port}")
        self._post_if_current(epoch, lambda: self._connected(c, info, udp))
        if epoch != self._epoch:
            c.close()
            return
        for sub in ("sub client all", "sub slice all", "sub pan all", "sub tx all", "sub amplifier all", "sub license all"):
            c.send(sub)
        self._refresh_meters_worker(epoch, c)
        self._post_if_current(epoch, self._after_subscriptions)

    def _connected(self, c: FlexClient, info: Tuple[int, str], udp: Tuple[int, str]) -> None:
        self.client = c
        self.connected = True
        self.connecting = False
        self._retrying = False
        if info[0] == 0:
            self._apply_info(info[1])
        if udp[0] != 0:
            self._set_message(f"Meter stream registration failed ({error_text(udp[0])}). Controls still work.")
        self.status_text = f"Connected to {c.host}   handle {c.handle}"
        self._start_watchdog()
        self._start_fft_worker()
        self._start_aether()
        self._notify("connection")

    def _after_subscriptions(self) -> None:
        self.ensure_iq_stream()

    def _connect_failed(self, msg: str) -> None:
        self.connecting = False
        self.connected = False
        self.status_text = msg
        if self._retrying:
            self.status_text += f"   retrying in {RECONNECT_S:.0f} s"
            self._schedule_reconnect()
        else:
            self._want_connected = False
        self._notify("connection")

    def _schedule_reconnect(self) -> None:
        self._cancel_reconnect()
        self._reconnect = self.d.call_later(RECONNECT_S, self._reconnect_tick)

    def _cancel_reconnect(self) -> None:
        if self._reconnect is not None:
            self._reconnect.cancel()
            self._reconnect = None

    def _reconnect_tick(self) -> None:
        self._reconnect = None
        if self._want_connected and not self.connected and not self.connecting:
            host, port, title, serial = self._last
            self._connect_core(host, port, title, serial)

    def reconnect(self) -> None:
        host, port, title, serial = self._last
        if host:
            self.connect(host, port, title, serial)

    def disconnect(self) -> None:
        self._want_connected = False
        self._retrying = False
        self._cancel_reconnect()
        self._cleanup()
        self.status_text = "Not connected"
        self._notify("connection")

    def _on_disconnected(self, reason: str) -> None:
        if self.client is None:
            return
        self._cleanup()
        if self._want_connected:
            self._retrying = True
            self.status_text = f"Lost connection ({reason})   retrying in {RECONNECT_S:.0f} s"
            self._schedule_reconnect()
        else:
            self.status_text = f"Disconnected ({reason})"
        self._notify("connection")

    def _cleanup(self) -> None:
        self._cleanup_agc()
        self._stop_aether()
        self._epoch += 1
        self._stop_watchdog()
        self._stop_fft_worker()
        if self._meter_refresh_timer is not None:
            self._meter_refresh_timer.cancel()
            self._meter_refresh_timer = None
        c = self.client
        self.client = None
        if c is not None:
            # Release anything Companion created on the radio, without waiting for replies.
            if self._iq_stream_id:
                c.send_no_reply(f"stream remove 0x{self._iq_stream_id:08X}")
            if self._iq_pan_assigned and self._iq_pan:
                c.send_no_reply(f"display pan set {self._iq_pan} daxiq_channel=0")
            c.close()
        self.connected = False
        self.connecting = False
        self.slices.clear()
        self.selected_index = None
        self.pans.clear()
        self.stations = [ALL_STATIONS]
        self.selected_station = ALL_STATIONS
        self._client_station.clear()
        self._bound_client_id = ""
        self._interlock_tx = False
        self.meter_defs.clear()
        self._meter_subs.clear()
        self._meter_fallback_all = False
        self._meter_map_dirty = True
        self._meter_pending = False
        self._last_meter_packet = 0.0
        self.readings = meters.MeterReadings()
        self.license.clear()
        self.amp_handle = self.amp_model = self.amp_ip = self.amp_state = ""
        self.amp_operate = False
        self._iq_stream_id = self._iq_channel = 0
        self._iq_pan = ""
        self._iq_pan_assigned = self._iq_lost = False
        self._iq_busy = self._iq_recheck = False
        self.engine.reset()
        self.spectrum_frame = None
        self._spec_avg = None
        self.spectrum_status = ""
        for p in self.all_controls:
            p.reset()
            p.clear_unsupported()
        self._notify("slices")
        self._notify("stations")
        self._notify("meters")
        self._notify("spectrum")
        self._notify("amplifier")

    def shutdown(self) -> None:
        self.disconnect()
        self._pool.shutdown(wait=False)

    def _apply_info(self, msg: str) -> None:
        d = kv.parse_line(msg.replace(",", " "))
        model = d.get("model", "")
        name = d.get("nickname") or d.get("name") or ""
        self.model = model
        chassis = (d.get("chassis_serial") or d.get("serial") or "").strip().strip('"')
        if chassis:
            self.radio_serial = chassis
        if model:
            self.radio_title = f"{name}  ({model})" if name and name != model else model
        self.power_max = 600.0 if model.upper().startswith("AU") else 120.0

    # ───────────────────────── selection ─────────────────────────

    def station_matches(self, s: Slice) -> bool:
        st = self.selected_station
        return st is None or not st.handle or kv.same_handle(s.client_handle, st.handle)

    def select_slice(self, index: Optional[int]) -> None:
        if index == self.selected_index:
            return
        self.selected_index = index
        self._meter_map_dirty = True
        self.readings.rx_dbm = math.nan
        self._reapply_all()
        self.apply_mode_availability()
        self.schedule_meter_refresh()
        self._spec_avg = None
        self.engine.configure(self._fft_size() if self.show_fft else 0)
        self._mark_fft_wanted()
        self._notify("selection")
        self._agc_on_slice_changed()
        self.ensure_iq_stream()

    def set_follow_active(self, on: bool) -> None:
        self.follow_active = on
        if on:
            act = next((s for s in self.ordered_slices if s.active and self.station_matches(s)), None)
            if act is not None:
                self.select_slice(act.index)
        self._notify("selection")

    def select_station(self, item: StationItem) -> None:
        self.selected_station = item
        if item.client_id:
            self._ensure_bound(lambda ok: None, announce=True)
        if self.follow_active:
            act = next((s for s in self.ordered_slices if s.active and self.station_matches(s)), None)
            if act is not None:
                self.select_slice(act.index)
        self._notify("stations")

    def _reapply_all(self) -> None:
        for c in self.all_controls:
            c.reset()
        s = self.selected_slice
        if s is None:
            return
        for c in self.all_controls:
            if c.scope == "slice":
                c.apply_status(s.state)
        pan = self.pans.get(s.pan)
        if pan is not None:
            for c in self.all_controls:
                if c.scope == "pan":
                    c.apply_status(pan)

    # ───────────────────────── status parsing ─────────────────────────

    def _on_status(self, body: str) -> None:
        if body.startswith("meter "):
            self._handle_meter_status(body[6:])
            return
        tok = kv.tokenize(body)
        if not tok:
            return
        head = tok[0]
        if head == "slice":
            self._handle_slice(tok)
        elif head == "display" and len(tok) > 2 and tok[1] == "pan":
            self._handle_pan(tok)
        elif head == "client" and len(tok) > 1:
            self._handle_client(tok)
        elif head == "interlock":
            st = kv.parse(tok[1:]).get("state")
            if st is not None:
                self._interlock_tx = st.upper() == "TRANSMITTING"
        elif head == "amplifier":
            self._handle_amplifier(tok)
        elif head == "license":
            self._handle_license(tok)

    def _handle_slice(self, tok: List[str]) -> None:
        if len(tok) < 2:
            return
        try:
            idx = int(tok[1])
        except ValueError:
            return
        d = kv.parse(tok[2:])
        s = self.slices.get(idx)
        if d.get("in_use") == "0" or "removed" in tok:
            if s is None:
                return
            del self.slices[idx]
            if self.selected_index == idx:
                nxt = next((x for x in self.ordered_slices if x.active and self.station_matches(x)), None) \
                    or next(iter(self.ordered_slices), None)
                self.selected_index = None
                self._notify("slices")
                self.select_slice(nxt.index if nxt else None)
            else:
                self._notify("slices")
            return

        if s is None:
            s = self.slices[idx] = Slice(idx)
        s.state.update(d)
        st = self._client_station.get(s.client_handle.lower())
        if st:
            s.station = st
        self._notify("slices")

        if self.follow_active and d.get("active") == "1" and self.station_matches(s) and idx != self.selected_index:
            self.select_slice(idx)
            return
        if self.selected_index is None:
            self.select_slice(idx)
            return
        if idx == self.selected_index:
            for c in self.all_controls:
                if c.scope == "slice":
                    c.apply_status(d)
            if "pan" in d and s.pan in self.pans:
                for c in self.all_controls:
                    if c.scope == "pan":
                        c.apply_status(self.pans[s.pan])
            if "mode" in d:
                self.apply_mode_availability(announce=True)
            if "mode" in d or "pan" in d or "RF_frequency" in d:
                self.schedule_meter_refresh()
            if "pan" in d or "filter_lo" in d or "filter_hi" in d or "mode" in d:
                self._spec_avg = None
                self._apply_iq_rate()
            if "pan" in d:
                self.ensure_iq_stream()
            if "dax" in d:
                self.ensure_audio_stream()
            if any(k.startswith("agc") or k in ("nr", "nrl", "nrs", "rnn", "nrf", "nb", "anf", "anfl", "anft") for k in d):
                self._notify("agc")
            self._notify("selection")

    def _handle_pan(self, tok: List[str]) -> None:
        pid = tok[2]
        if "removed" in tok:
            self.pans.pop(pid, None)
            return
        d = kv.parse(tok[3:])
        state = self.pans.setdefault(pid, kv.CIDict())
        state.update(d)
        s = self.selected_slice
        if s is not None and s.pan.lower() == pid.lower():
            for c in self.all_controls:
                if c.scope == "pan":
                    c.apply_status(d)
            if "daxiq_channel" in d:
                self.ensure_iq_stream()

    def _handle_client(self, tok: List[str]) -> None:
        handle = tok[1]
        d = kv.parse(tok[2:])
        if "disconnected" in tok:
            self._client_station.pop(handle.lower(), None)
            gone = next((x for x in self.stations if x.handle and kv.same_handle(x.handle, handle)), None)
            if gone is not None:
                if gone is self.selected_station:
                    self.selected_station = ALL_STATIONS
                if gone.client_id == self._bound_client_id:
                    self._bound_client_id = ""
                self.stations.remove(gone)
                self._notify("stations")
            return
        station = d.get("station")
        program = d.get("program")
        if station is None and program is None:
            return
        label = station or program or ""
        self._client_station[handle.lower()] = label
        for s in self.slices.values():
            if kv.same_handle(s.client_handle, handle):
                s.station = label
        client_id = d.get("client_id")
        if not client_id:
            return
        name = f"{station}  ({program})" if program and station else label
        existing = next((x for x in self.stations if x.handle and kv.same_handle(x.handle, handle)), None)
        if existing is not None and existing.name == name:
            return
        item = StationItem(handle, client_id, name)
        if existing is not None:
            i = self.stations.index(existing)
            self.stations[i] = item
            if existing is self.selected_station:
                self.selected_station = item
        else:
            self.stations.append(item)
        self._notify("stations")

    def _handle_amplifier(self, tok: List[str]) -> None:
        if len(tok) < 2:
            return
        handle = tok[1]
        if "removed" in tok:
            if kv.same_handle(handle, self.amp_handle):
                self.amp_handle = self.amp_model = self.amp_ip = self.amp_state = ""
                self.amp_operate = False
                self._notify("amplifier")
            return
        d = kv.parse(tok[2:])
        model = d.get("model", "")
        if model.lower() == "tunergeniusxl":
            return
        if handle.lower() == "0x00000000":
            handle = ""
        if handle:
            self.amp_handle = handle
        if model:
            self.amp_model = model
        if "ip" in d:
            self.amp_ip = d["ip"]
        state = d.get("state", "")
        if state:
            self.amp_state = state
            self.amp_operate = state.upper() != "STANDBY"
        self._notify("amplifier")

    def _handle_license(self, tok: List[str]) -> None:
        if len(tok) < 2 or tok[1].lower() != "feature":
            return
        d = kv.parse(tok[2:])
        name = d.get("name", "")
        if not name:
            return
        self.license[name] = (d.get("enabled", "0") == "1", d.get("reason", ""))
        self._notify("amplifier")

    @property
    def license_summary(self) -> str:
        if not self.license:
            return "No feature-entitlement status received — controls fail open; the radio remains authoritative."
        return "  •  ".join(f"{k}:{'on' if v[0] else 'off'}" for k, v in sorted(self.license.items()))

    # ───────────────────────── DSP controls ─────────────────────────

    def control(self, label: str) -> Optional[ParamControl]:
        return next((c for c in self.all_controls if c.label.upper() == label.upper()), None)

    def apply_mode_availability(self, announce: bool = False) -> None:
        """AetherSDR's rule: ANF/ANFL/ANFT are hidden in DIG/RTTY/FDV; CW also excludes RNN."""
        for c in self.all_controls:
            c.set_temporary_unavailable(False)
        s = self.selected_slice
        mode = (s.mode if s else "").strip().upper()
        if not mode:
            return
        if mode in DIGITAL_RESTRICTED:
            blocked = ["ANF", "ANFL", "ANFT"]
        elif mode in CW_MODES:
            blocked = ["ANF", "RNN", "ANFL", "ANFT"]
        else:
            blocked = []
        for label in blocked:
            c = self.control(label)
            if c is not None:
                c.set_temporary_unavailable(True, f"{label} is unavailable in {mode} mode")
        if announce and blocked:
            self._set_message(f"{' / '.join(blocked)} unavailable in {mode} mode. They will be restored "
                              f"automatically when you return to a compatible mode.")
        elif announce and "unavailable in" in self.last_message and "mode" in self.last_message:
            self._set_message(f"DSP mode restriction cleared — filters are available again in {mode}.")

    def _license_reason(self, ctl: Optional[ParamControl]) -> Optional[str]:
        if ctl is None or ctl.label.upper() not in LICENSED_DSP:
            return None
        state = self.license.get("NOISE_REDUCTION")
        if state is None or state[0]:
            return None
        return f"radio reports NOISE_REDUCTION disabled ({state[1] or 'radio feature status'})"

    def describe_control_error(self, ctl: Optional[ParamControl], key: str, code: int) -> str:
        label = ctl.label if ctl else key.upper()
        s = self.selected_slice
        mode = (s.mode if s else "").strip().upper()
        hx = f"0x{code:08X}"
        lic = self._license_reason(ctl)
        if code == UNKNOWN_PARAMETER:
            return f"{label} unavailable — {lic} [{hx}]" if lic else \
                f"{label} unavailable — this radio / firmware does not report support for {key} [{hx}]"
        if code in (COMMAND_REFUSED, INVALID_MODE_OR_STATE, INVALID_DSP_FOR_MODE, INVALID_COMMAND_FOR_MODE):
            if ctl is not None and ctl.is_temporarily_unavailable and ctl.temporary_reason:
                return f"{ctl.temporary_reason} [{hx}]"
            if lic:
                return f"{label} unavailable — {lic} [{hx}]"
            if mode:
                return f"{label} unavailable in {mode} mode or the radio's current state [{hx}]"
            return f"{label} unavailable in the radio's current state [{hx}]"
        return f"{label}: {error_text(code)} [{hx}]"

    def _send_scoped(self, scope: str, keyval: str) -> None:
        s = self.selected_slice
        if s is None or self.client is None:
            return
        if scope == "pan":
            if not s.pan:
                return
            cmd = f"display pan set {s.pan} {keyval}"
        else:
            cmd = f"slice set {s.index} {keyval}"
        key = keyval.split("=", 1)[0]
        ctl = next((c for c in self.all_controls if c.scope == scope and c.uses(key)), None)
        if ctl is not None and ctl.is_temporarily_unavailable:
            self._set_message(ctl.temporary_reason or f"{ctl.label} is temporarily unavailable in the current mode.")
            return

        def done(code: int, _msg: str) -> None:
            if code == 0:
                return
            if code == UNKNOWN_PARAMETER and ctl is not None:
                ctl.mark_unsupported()     # genuine capability result; context errors never poison it
            self._set_message(self.describe_control_error(ctl, key, code))

        # Bind to the owning GUI station first so SmartSDR/Aether and Companion share a
        # MultiFLEX context. A failed bind does not block ordinary slice commands.
        self._ensure_bound(lambda _ok: self._send(cmd, done), announce=False)

    # ───────────────────────── station binding ─────────────────────────

    def station_for_commands(self) -> Optional[StationItem]:
        if self.selected_station.client_id:
            return self.selected_station
        s = self.selected_slice
        handle = s.client_handle if s else ""
        if handle:
            owner = next((x for x in self.stations if x.client_id and kv.same_handle(x.handle, handle)), None)
            if owner is not None:
                return owner
        gui = [x for x in self.stations if x.client_id]
        return gui[0] if len(gui) == 1 else None

    def _ensure_bound(self, then: Callable[[bool], None], announce: bool) -> None:
        st = self.station_for_commands()
        if st is None:
            if announce:
                self._set_message("No SmartSDR / AetherSDR station found to act for. Open the GUI on this radio, "
                                  "or pick it in the Station box.")
            then(False)
            return
        if st.client_id == self._bound_client_id:
            then(True)
            return

        def done(code: int, _msg: str) -> None:
            if code == 0:
                self._bound_client_id = st.client_id
            elif announce:
                self._set_message(f"Could not attach to station {st.name}: {error_text(code)}")
            then(code == 0)

        self._send(f"client bind client_id={st.client_id}", done)

    def execute(self, cmd: str, then: Optional[Callable[[int, str], None]] = None, bind: bool = False) -> None:
        """Run a raw FLEX command; errors also go to the status line."""
        def done(code: int, msg: str) -> None:
            if code != 0:
                self._set_message(f"{cmd}   {error_text(code)}")
            if then:
                then(code, msg)
        if self.client is None:
            done(NO_REPLY, "not connected")
            return
        if bind:
            self._ensure_bound(lambda _ok: self._send(cmd, done), announce=False)
        else:
            self._send(cmd, done)

    def tune(self, mhz: float, then: Optional[Callable[[int, str], None]] = None) -> None:
        s = self.selected_slice
        if s is None:
            if then:
                then(NO_REPLY, "no slice selected")
            return
        # No autopan=0: band jumps must let the panadapter follow the slice.
        self.execute(f"slice tune {s.index} {mhz:.6f}", then, bind=True)

    def set_mode(self, mode: str, then: Optional[Callable[[int, str], None]] = None) -> None:
        s = self.selected_slice
        if s is None:
            if then:
                then(NO_REPLY, "no slice selected")
            return
        self.execute(f"slice set {s.index} mode={mode}", then, bind=True)

    def set_amplifier_operate(self, on: bool, then: Optional[Callable[[int, str], None]] = None) -> None:
        if not self.amp_handle:
            if then:
                then(NO_REPLY, "no amplifier reported by radio")
            return
        self.execute(f"amplifier set {self.amp_handle} operate={1 if on else 0}", then)

    def auto_tune_once(self) -> None:
        s = self.selected_slice
        if s is not None:
            self._ensure_bound(lambda ok: ok and self.send(f"slice auto_tune {s.index}"), announce=True)

    # ───────────────────────── meters ─────────────────────────

    def set_tx_meter(self, key: str) -> None:
        if key in meters.TX_METER_OPTIONS and key != self.tx_meter:
            self.tx_meter = key
            self.schedule_meter_refresh()
            self._notify("meters")

    def schedule_meter_refresh(self) -> None:
        if self.client is None:
            return
        if self._meter_refresh_timer is not None:
            self._meter_refresh_timer.cancel()
        self._meter_refresh_timer = self.d.call_later(METER_REFRESH_S, self._meter_refresh_due)

    def _meter_refresh_due(self) -> None:
        self._meter_refresh_timer = None
        c = self.client
        if c is None:
            return
        if self._meter_refresh_busy:
            self.schedule_meter_refresh()
            return
        self._meter_refresh_busy = True
        epoch = self._epoch
        self._pool.submit(self._refresh_meters_worker, epoch, c)

    def _refresh_meters_worker(self, epoch: int, c: FlexClient) -> None:
        """Subscribe only to the meters the UI shows (slice LEVEL, FWD/SWR, chosen TX meter)."""
        try:
            code, body = c.send("meter list")
            if epoch != self._epoch:
                return
            defs: Dict[int, meters.MeterDef] = {}
            if code == 0 and body.strip():
                b = body.strip()
                if b.lower().startswith("meter "):
                    b = b[6:]
                meters.parse_meter_metadata(b, defs)
            if not defs:
                if not self._meter_fallback_all:
                    r = c.send("sub meter all")
                    if r[0] == 0:
                        def fb():
                            self._meter_fallback_all = True
                            self._meter_subs.clear()
                            self._set_message("Selective meter subscription wasn't available, so Companion fell back to all meters.")
                        self._post_if_current(epoch, fb)
                return

            # Compute the wanted set on the dispatcher (it owns slice selection), then apply it here.
            box: dict = {}
            ready = threading.Event()

            def plan():
                self.meter_defs = defs
                sel = self.selected_slice
                self.meter_ids = meters.map_meters(defs, sel.index if sel else -1)
                self._meter_map_dirty = False
                box["want"] = meters.desired_meter_ids(self.meter_ids, self.tx_meter)
                box["have"] = set(self._meter_subs)
                box["all"] = self._meter_fallback_all
                ready.set()
            self._post_if_current(epoch, plan)
            if not ready.wait(3) or epoch != self._epoch:
                return
            want, have = box["want"], box["have"]
            if box["all"]:
                c.send("unsub meter all")
                have = set()
            for mid in sorted(have - want):
                c.send(f"unsub meter {mid}")
                have.discard(mid)
            for mid in sorted(want - have):
                if c.send(f"sub meter {mid}")[0] == 0:
                    have.add(mid)

            def commit():
                self._meter_subs = have
                self._meter_fallback_all = False
            self._post_if_current(epoch, commit)
        finally:
            def clear():
                self._meter_refresh_busy = False
            self.d.post(clear)

    def _handle_meter_status(self, rest: str) -> None:
        rest = rest.strip()
        if rest.endswith("removed"):
            for part in rest.split():
                if part.isdigit():
                    mid = int(part)
                    self.meter_defs.pop(mid, None)
                    self._meter_subs.discard(mid)
            self._meter_map_dirty = True
            self.schedule_meter_refresh()
            return
        meters.parse_meter_metadata(rest, self.meter_defs)
        self._meter_map_dirty = True

    def _on_meter_packet(self, epoch: int) -> None:
        """UDP thread: coalesce packet bursts into one dispatcher update using the latest values."""
        self._last_meter_packet = time.monotonic()
        if self._meter_pending:
            return
        self._meter_pending = True

        def tick():
            self._meter_pending = False
            if epoch == self._epoch and self.connected:
                self.meter_tick()
        self.d.post(tick)

    def meter_tick(self) -> None:
        c = self.client
        if c is None:
            return
        if self._meter_map_dirty:
            sel = self.selected_slice
            self.meter_ids = meters.map_meters(self.meter_defs, sel.index if sel else -1)
            self._meter_map_dirty = False
        snapshot = dict(c.meters)
        defs = self.meter_defs
        self.readings.update(lambda mid: meters.read_meter(snapshot, defs, mid), self.meter_ids, self._interlock_tx)
        self._notify("meters")
        self._audio_tick()

    def _start_watchdog(self) -> None:
        self._stop_watchdog()
        epoch = self._epoch

        def wd():
            if epoch != self._epoch or not self.connected:
                return
            if time.monotonic() - self._last_meter_packet >= 0.2:
                self.meter_tick()
            self._watchdog = self.d.call_later(METER_WATCHDOG_S, wd)
        self._watchdog = self.d.call_later(METER_WATCHDOG_S, wd)

    def _stop_watchdog(self) -> None:
        if self._watchdog is not None:
            self._watchdog.cancel()
            self._watchdog = None

    @property
    def tx_spec(self) -> meters.TxMeterSpec:
        return meters.tx_spec(self.tx_meter, self.power_max)

    # ───────────────────────── spectrum (DAX IQ) ─────────────────────────

    def set_show_fft(self, on: bool) -> None:
        if on == self.show_fft:
            return
        self.show_fft = on
        self._iq_lost = False
        if on:
            self._mark_fft_wanted()
        if not on:
            self.spectrum_frame = None
            self._spec_avg = None
        self.engine.configure(self._fft_size() if on else 0)
        self._notify("spectrum")
        self.ensure_iq_stream()

    def set_fft_span(self, khz: float) -> None:
        if khz not in spectrum.SPAN_OPTIONS_KHZ or khz == self.fft_span_khz:
            return
        self.fft_span_khz = khz
        self._spec_avg = None
        self._apply_iq_rate()
        self._notify("spectrum")

    def set_saver(self, on: bool) -> None:
        if on == self.saver:
            return
        self.saver = on
        self._spec_avg = None
        self._apply_iq_rate()
        if self._iq_channel:
            self.spectrum_status = self._spectrum_status_text(self._iq_channel)
        self._notify("spectrum")

    def _window(self) -> spectrum.SpectrumWindow:
        s = self.selected_slice
        st = s.state if s else kv.CIDict()
        return spectrum.effective_window(s.mode if s else "", st.get("filter_lo"), st.get("filter_hi"),
                                         self.fft_span_khz, self.saver)

    def _fft_size(self) -> int:
        return spectrum.fft_size_for(self._iq_rate, self._window().span_hz)

    def _spectrum_status_text(self, ch: int) -> str:
        eff = min(self.fft_span_khz, 24.0) if self.saver else self.fft_span_khz
        if not self.saver or self.fft_span_khz <= 24:
            return f"DAX IQ {ch} · {eff:.0f} kHz span"
        return f"DAX IQ {ch} · Network saver 24 kHz (requested {self.fft_span_khz:.0f} kHz)"

    def _apply_iq_rate(self) -> None:
        want = spectrum.rate_for_span(self._window().span_hz, self.saver)
        if want == self._iq_rate:
            self.engine.configure(self._fft_size() if self.show_fft else 0)
            return
        sid = self._iq_stream_id
        if self.client is None or sid == 0:
            self._iq_rate = want
            self.engine.configure(self._fft_size() if self.show_fft else 0)
            return

        def done(code: int, _m: str) -> None:
            if code != 0:
                self.spectrum_status = f"DAX IQ rate {want // 1000} kHz was refused ({error_text(code)})."
                self._notify("spectrum")
                return
            self._iq_rate = want
            self.engine.reset()
            self._spec_avg = None
            self.engine.configure(self._fft_size() if self.show_fft else 0)
        self._send(f"stream set 0x{sid:08X} daxiq_rate={want}", done)

    def ensure_iq_stream(self) -> None:
        """Create / move / release the Companion-owned DAX-IQ stream to match the UI state."""
        if self._iq_busy:
            self._iq_recheck = True
            return
        self._iq_busy = True
        self._iq_recheck = False
        epoch = self._epoch
        s = self.selected_slice
        plan = {
            "want": self.connected and self.show_fft and s is not None,
            "pan": s.pan if s else "",
            "pans": {k: v.get("daxiq_channel") for k, v in self.pans.items()},
            "cur_id": self._iq_stream_id, "cur_ch": self._iq_channel, "cur_pan": self._iq_pan,
            "assigned": self._iq_pan_assigned, "lost": self._iq_lost,
            "rate": spectrum.rate_for_span(self._window().span_hz, self.saver),
            "station": self.station_for_commands(), "bound": self._bound_client_id,
            "aether": self.aether_active, "probe": self._aether_probing,
        }
        c = self.client
        if c is None:
            self._iq_busy = False
            return
        self._pool.submit(self._iq_worker, epoch, c, plan)

    def _iq_worker(self, epoch: int, c: FlexClient, p: dict) -> None:
        result: dict = {"status": None}
        try:
            self._iq_plan(c, p, result)
        finally:
            def finish():
                self._iq_busy = False
                if epoch != self._epoch:
                    return
                if "set" in result:
                    sid, ch, pan, assigned, rate = result["set"]
                    self._iq_stream_id, self._iq_channel, self._iq_pan = sid, ch, pan
                    self._iq_pan_assigned, self._iq_rate = assigned, rate
                    self.engine.reset()
                    self._spec_avg = None
                    self.engine.configure(self._fft_size() if self.show_fft else 0)
                if "lost" in result:
                    self._iq_lost = result["lost"]
                if result.get("bound"):
                    self._bound_client_id = result["bound"]
                if result["status"] is not None:
                    self.spectrum_status = result["status"]
                if result.get("clear"):
                    self.spectrum_frame = None
                    self._spec_avg = None
                self._notify("spectrum")
                if self._iq_recheck:
                    self.ensure_iq_stream()
            self.d.post(finish)

    def _iq_plan(self, c: FlexClient, p: dict, r: dict) -> None:
        def remove_current():
            if p["cur_id"]:
                c.send(f"stream remove 0x{p['cur_id']:08X}")
            if p["assigned"] and p["cur_ch"] > 0 and p["cur_pan"]:
                c.send(f"display pan set {p['cur_pan']} daxiq_channel=0")
            r["set"] = (0, 0, p["cur_pan"] if r.get("lost") else "", False, p["rate"])

        if not p["want"]:
            if p["cur_id"]:
                remove_current()
            r["status"] = ""
            r["clear"] = True
            return
        pan = p["pan"]
        if not pan:
            remove_current()
            r["status"] = "Waiting for the selected slice's panadapter…"
            r["clear"] = True
            return
        if p.get("aether"):
            if p["cur_id"]:
                remove_current()                 # Aether is serving this pan: no DAX IQ needed
            r["status"] = AETHER_STATUS
            return
        if p.get("probe") and not p["cur_id"]:
            r["status"] = "Looking for Aether shared pan…"
            return

        def chan(v) -> int:
            try:
                return int(v)
            except (TypeError, ValueError):
                return -1

        pan_ch = chan(p["pans"].get(pan))
        if p["cur_id"] and p["cur_pan"].lower() == pan.lower():
            if pan_ch < 0 or pan_ch == p["cur_ch"]:
                return                       # already streaming the right pan
            if pan_ch == 0:
                r["lost"] = True
                p["assigned"] = False
                remove_current()
                r["status"] = "Another program removed this pan's DAX IQ channel. Toggle the spectrum off/on to take it again."
                r["clear"] = True
                return
            p["assigned"] = False
        if p["lost"] and p["cur_pan"].lower() == pan.lower():
            return
        r["lost"] = False
        remove_current()

        if 1 <= pan_ch <= 4:
            ch, assigned = pan_ch, False
        else:
            used = {chan(v) for v in p["pans"].values()}
            ch = next((x for x in range(1, 5) if x not in used), 0)
            if ch == 0:
                r["status"] = "All four DAX IQ channels are already assigned."
                return
            st = p["station"]
            if st is not None and st.client_id and st.client_id != p["bound"]:
                if c.send(f"client bind client_id={st.client_id}")[0] == 0:
                    r["bound"] = st.client_id
            code, _ = c.send(f"display pan set {pan} daxiq_channel={ch}")
            if code != 0:
                r["status"] = f"Couldn't attach DAX IQ {ch} to this pan ({error_text(code)})."
                return
            assigned = True

        code, body = c.send(f"stream create type=dax_iq daxiq_channel={ch}")
        if code != 0:
            r["status"] = f"Couldn't open DAX IQ {ch} ({error_text(code)})."
            if assigned:
                c.send(f"display pan set {pan} daxiq_channel=0")
            return
        sid = kv.parse_hex_id(body)
        if sid == 0:
            r["status"] = f'Radio returned an unexpected DAX IQ stream id "{body}".'
            if assigned:
                c.send(f"display pan set {pan} daxiq_channel=0")
            return
        rate = p["rate"]
        code, _ = c.send(f"stream set 0x{sid:08X} daxiq_rate={rate}")
        if code != 0:
            c.send(f"stream remove 0x{sid:08X}")
            if assigned:
                c.send(f"display pan set {pan} daxiq_channel=0")
            r["status"] = f"DAX IQ rate was refused ({error_text(code)})."
            return
        r["set"] = (sid, ch, pan, assigned, rate)
        r["status"] = self._spectrum_status_text(ch)

    def _on_stream_packet(self, pcc: int, sid: int, data: bytes) -> None:
        """UDP thread: IQ for our stream goes straight into the native ring buffer."""
        if sid and sid == self._iq_stream_id and _core.is_iq_pcc(pcc):
            self.engine.push_packet(data, sid)
        elif pcc in AUDIO_PCCS:
            self._on_audio_packet(sid, data)

    def _start_fft_worker(self) -> None:
        self._stop_fft_worker()
        self.engine.configure(self._fft_size() if self.show_fft else 0)
        stop = threading.Event()
        self._fft_stop = stop
        epoch = self._epoch

        def loop():
            while not stop.is_set():
                fps = 20 if self.saver else 30
                if stop.wait(1.0 / fps):
                    break
                if not self.show_fft or self._iq_stream_id == 0 or self._fft_pending:
                    continue
                spec = self.engine.compute()
                if spec is None:
                    continue
                self._fft_pending = True
                self._post_if_current(epoch, lambda s=spec: self._on_fft(s))
        t = threading.Thread(target=loop, name=f"flex-fft-{self.slot}", daemon=True)
        self._fft_thread = t
        t.start()

    def _stop_fft_worker(self) -> None:
        self._fft_stop.set()
        self._fft_thread = None
        self._fft_pending = False

    def _on_fft(self, full: np.ndarray) -> None:
        self._fft_pending = False
        if not self.show_fft or self.aether_active:
            return
        s = self.selected_slice
        offset = None
        if s is not None:
            pan = self.pans.get(s.pan)
            sf = s.freq_mhz
            pc = kv.to_float(pan.get("center")) if pan is not None else None
            if sf is not None and pc is not None:
                offset = (sf - pc) * 1e6
        frame = spectrum.crop_to_span(full, self._iq_rate, self._window(), offset)
        frame.bins = spectrum.smooth(self._spec_avg, frame.bins)
        self._spec_avg = frame.bins
        if s is not None:
            spectrum.filter_fractions(frame, s.state.get("filter_lo"), s.state.get("filter_hi"))
        self.spectrum_frame = frame
        if self._iq_channel:
            live = self.engine.is_live(800)
            self.spectrum_status = self._spectrum_status_text(self._iq_channel) if live \
                else f"DAX IQ {self._iq_channel}: waiting for I/Q…"
        self._notify("spectrum")
