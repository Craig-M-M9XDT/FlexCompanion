"""Native station tools: band/mode buttons, ATU/TUNE/MOX, profiles, macros and raw
commands (port of StationViewModel.cs, core subset). Acts on radio slot A or B."""
from __future__ import annotations

import time
from typing import Callable, Dict, List, Optional

from .client import error_text
from .dispatch import Dispatcher
from .dxcluster import DxClusterClient, DxSpot
from .pgxl import PgxlClient, PgxlTelemetry
from .session import RadioSession

MAX_SPOTS = 250

BAND_CENTRES_MHZ: Dict[str, float] = {
    "160": 1.900, "80": 3.650, "60": 5.360, "40": 7.100, "30": 10.120, "20": 14.200,
    "17": 18.130, "15": 21.200, "12": 24.950, "10": 28.400, "6": 50.200,
}
BANDS = list(BAND_CENTRES_MHZ)
MODES = ["LSB", "USB", "CW", "AM", "FM", "DIGU", "DIGL", "RTTY"]


def quote_flex(s: str) -> str:
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


class StationController:
    def __init__(self, radio_for_slot: Callable[[str], RadioSession], target_slot: str = "A",
                 macros: List[Dict[str, str]] | None = None, dispatcher: Optional[Dispatcher] = None,
                 settings=None):
        self._radio_for_slot = radio_for_slot
        self.d = dispatcher
        self.settings = settings
        self._kind_listeners: List[Callable[[str], None]] = []
        self.spots: List[DxSpot] = []
        self.dx_status = "Not connected"
        self.dx = DxClusterClient()
        self.pgxl = PgxlClient()
        self.amp = PgxlTelemetry()
        self.pgxl_status = "Not connected"
        self.amp_alert = ""
        if settings is not None:
            self.dx.auto_reconnect = settings.dx_auto_reconnect
            self.pgxl.auto_reconnect = settings.pgxl_auto_reconnect
        self._wire_clients()
        self._expire_timer = None
        if dispatcher is not None:
            self._expire_timer = dispatcher.call_later(30, self._expire_spots)
        self.target_slot = "B" if target_slot == "B" else "A"
        self.macros = macros or []
        self.status = "Native station tools ready"
        self.tune_on = False
        self.mox_on = False
        self._listeners: List[Callable[[], None]] = []

    def subscribe(self, fn: Callable[[], None]) -> None:
        self._listeners.append(fn)

    def _changed(self) -> None:
        for fn in list(self._listeners):
            fn()

    def _set_status(self, text: str) -> None:
        self.status = text
        self._changed()

    @property
    def radio(self) -> RadioSession:
        return self._radio_for_slot(self.target_slot)

    def set_target(self, slot: str) -> None:
        self.target_slot = "B" if slot == "B" else "A"
        self.tune_on = self.mox_on = False
        self._changed()

    def set_band(self, band: str) -> None:
        mhz = BAND_CENTRES_MHZ.get(band)
        if mhz is None:
            self._set_status(f"Unknown band {band}")
            return
        self.radio.tune(mhz, lambda code, _m: self._set_status(
            f"{band} m · {mhz:.3f} MHz" if code == 0 else error_text(code)))

    def set_mode(self, mode: str) -> None:
        mode = mode.strip().upper()
        if not mode:
            return
        self.radio.set_mode(mode, lambda code, _m: self._set_status(
            f"Mode {mode}" if code == 0 else error_text(code)))

    def send(self, command: str, on_ok: Callable[[], None] | None = None) -> None:
        command = command.strip()
        if not command:
            return

        def done(code: int, _m: str) -> None:
            self._set_status(command if code == 0 else f"{command} · {error_text(code)}")
            if code == 0 and on_ok:
                on_ok()
        self.radio.execute(command, done, bind=True)

    def atu_start(self) -> None:
        self.send("atu start")

    def atu_bypass(self) -> None:
        self.send("atu bypass")

    def toggle_tune(self) -> None:
        want = not self.tune_on

        def ok():
            self.tune_on = want
            self._changed()
        self.send(f"transmit tune {1 if want else 0}", ok)

    def toggle_mox(self) -> None:
        want = not self.mox_on

        def ok():
            self.mox_on = want
            self._changed()
        self.send(f"xmit {1 if want else 0}", ok)

    def load_profile(self, kind: str, name: str) -> None:
        kind = {"tx": "tx", "transmit": "tx", "mic": "mic"}.get(kind.strip().lower(), "global")
        if name.strip():
            self.send(f"profile {kind} load {quote_flex(name.strip())}")

    def run_macro(self, commands: str) -> None:
        """One FLEX command per line; ``@mode X`` / ``@band N`` shortcuts; ``#`` comments."""
        for raw in commands.replace("\r", "").split("\n"):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            low = line.lower()
            if low.startswith("@mode "):
                self.set_mode(line[6:])
            elif low.startswith("@band "):
                self.set_band(line[6:].strip())
            else:
                self.send(line)

    def toggle_amplifier(self) -> None:
        r = self.radio
        want = not r.amp_operate
        r.set_amplifier_operate(want, lambda code, m: self._set_status(
            f"Amplifier {'OPERATE' if want else 'STANDBY'}" if code == 0 else f"Amplifier: {m or error_text(code)}"))

    # ───────────────────────── events ─────────────────────────

    def subscribe_kind(self, fn: Callable[[str], None]) -> None:
        """Fine-grained events: ``spots``, ``dx``, ``amp``."""
        self._kind_listeners.append(fn)

    def _emit(self, kind: str) -> None:
        for fn in list(self._kind_listeners):
            fn(kind)

    def _post(self, fn: Callable[[], None]) -> None:
        if self.d is not None:
            self.d.post(fn)
        else:
            fn()

    def _wire_clients(self) -> None:
        dx, pg = self.dx, self.pgxl

        def dx_status(text: str) -> None:
            self.dx_status = text
            self._emit("dx")
        dx.on_connected = lambda: self._post(lambda: dx_status(f"Connected to {dx.host}:{dx.port}"))
        dx.on_disconnected = lambda: self._post(lambda: dx_status("Disconnected"))
        dx.on_error = lambda e: self._post(lambda: dx_status(e))
        dx.on_spot = lambda spot: self._post(lambda: self._add_spot(spot))

        def pg_status(text: str) -> None:
            self.pgxl_status = text
            self._emit("amp")
        pg.on_connected = lambda: self._post(lambda: pg_status(f"Connected · v{pg.version}"))
        pg.on_disconnected = lambda: self._post(lambda: pg_status("Disconnected"))
        pg.on_error = lambda e: self._post(lambda: pg_status(e))

        def alert(a: str) -> None:
            self.amp_alert = a
            self._emit("amp")
        pg.on_alert = lambda a: self._post(lambda: alert(a))

        def status(d: Dict[str, str]) -> None:
            self.amp.apply(d)
            if self.amp.state:
                self.pgxl_status = f"{self.amp.state} · v{pg.version}"
            self._emit("amp")
        pg.on_status = lambda d: self._post(lambda: status(d))

    # ───────────────────────── DX cluster ─────────────────────────

    def dx_connect(self, host: str, port: int, callsign: str) -> None:
        if self.settings is not None:
            self.settings.dx_host, self.settings.dx_port, self.settings.dx_callsign = host, port, callsign
        try:
            self.dx.connect(host, port, callsign)
            self.dx_status = "Connecting…"
        except ValueError as ex:
            self.dx_status = str(ex)
        self._emit("dx")

    def dx_disconnect(self) -> None:
        self.dx.disconnect()
        self.dx_status = "Not connected"
        self._emit("dx")

    def _add_spot(self, spot: DxSpot) -> None:
        # newest first; a re-spot of the same call on (nearly) the same frequency replaces the old one
        self.spots = [s for s in self.spots
                      if not (s.callsign == spot.callsign and abs(s.frequency_mhz - spot.frequency_mhz) < 0.0015)]
        self.spots.insert(0, spot)
        del self.spots[MAX_SPOTS:]
        self._emit("spots")
        if self.settings is None or self.settings.publish_spots_to_radio:
            self._publish_spot(spot)

    def _publish_spot(self, spot: DxSpot) -> None:
        """Forward to the radio with ``spot add`` so SmartSDR / Aether show it on their panadapters."""
        r = self.radio
        if not r.connected:
            return
        call = "".join(c for c in spot.callsign if c.isalnum() or c in "/-")
        de = "".join(c for c in spot.spotter if c.isalnum() or c in "/-#")
        if not call:
            return
        f = f"{spot.frequency_mhz:.6f}"
        life = max(60, (self.settings.spot_max_age_minutes if self.settings else 30) * 60)
        cmd = f"spot add callsign={call} rx_freq={f} tx_freq={f} source=FlexCompanion lifetime_seconds={life}"
        if de:
            cmd += f" spotter_callsign={de}"
        r.execute(cmd)

    def tune_spot(self, spot: DxSpot) -> None:
        self.radio.tune(spot.frequency_mhz, lambda code, _m: self._set_status(
            f"Tuned {spot.callsign} · {spot.frequency_mhz:.4f} MHz" if code == 0 else error_text(code)))

    def _expire_spots(self) -> None:
        max_age = (self.settings.spot_max_age_minutes if self.settings else 30) * 60
        cutoff = time.time() - max_age
        before = len(self.spots)
        self.spots = [s for s in self.spots if s.received >= cutoff]
        if len(self.spots) != before:
            self._emit("spots")
        if self.d is not None:
            self._expire_timer = self.d.call_later(30, self._expire_spots)

    # ───────────────────────── Power Genius XL ─────────────────────────

    def pgxl_connect(self, host: str = "", port: int = 9008) -> None:
        host = host.strip() or self.radio.amp_ip
        if not host:
            self.pgxl_status = "Enter the PGXL IP, or connect a radio that reports one"
            self._emit("amp")
            return
        if self.settings is not None:
            self.settings.pgxl_host, self.settings.pgxl_port = host, port
        self.amp = PgxlTelemetry()
        self.pgxl_status = "Connecting…"
        self.pgxl.connect(host, port)
        self._emit("amp")

    def pgxl_disconnect(self) -> None:
        self.pgxl.disconnect()
        self.pgxl_status = "Not connected"
        self._emit("amp")

    def shutdown(self) -> None:
        if self._expire_timer is not None:
            self._expire_timer.cancel()
        self.dx.disconnect()
        self.pgxl.disconnect()
