"""Native station tools: band/mode buttons, ATU/TUNE/MOX, profiles, macros and raw
commands (port of StationViewModel.cs, core subset). Acts on radio slot A or B."""
from __future__ import annotations

from typing import Callable, Dict, List

from .client import error_text
from .session import RadioSession

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
                 macros: List[Dict[str, str]] | None = None):
        self._radio_for_slot = radio_for_slot
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
