"""Settings, stored in the same ``~/.config/FlexCompanion/settings.json`` as the .NET build.

Keys use the .NET names (PascalCase) so both builds share manual IP, slot preferences,
macros and station settings. Unknown keys are preserved on save, so switching between
the two builds never loses the other's settings.
"""
from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path
from typing import Any, Dict, List

DEFAULT_MACROS = [
    {"Label": "USB", "Commands": "@mode USB"},
    {"Label": "LSB", "Commands": "@mode LSB"},
    {"Label": "CW", "Commands": "@mode CW"},
    {"Label": "DIGU", "Commands": "@mode DIGU"},
]

SLOT_DEFAULTS = {"AnalogueMeter": True, "TxMeter": "Power", "ShowFft": False, "FftSpanKhz": 48.0, "AgcTargetDb": -28.0}


def config_dir() -> Path:
    base = os.environ.get("XDG_CONFIG_HOME") or os.path.join(os.path.expanduser("~"), ".config")
    return Path(base) / "FlexCompanion"


class SlotPrefs:
    def __init__(self, data: Dict[str, Any]):
        self._d = data
        for k, v in SLOT_DEFAULTS.items():
            self._d.setdefault(k, v)

    def _get(self, key: str):
        # .NET writes PascalCase but reads case-insensitively; accept either.
        if key in self._d:
            return self._d[key]
        for k, v in self._d.items():
            if k.lower() == key.lower():
                return v
        return SLOT_DEFAULTS[key]

    @property
    def tx_meter(self) -> str:
        v = str(self._get("TxMeter"))
        return v if v in ("Power", "SWR", "Proc", "Mic", "Vdd", "Current", "Temp") else "Power"

    @tx_meter.setter
    def tx_meter(self, v: str) -> None:
        self._d["TxMeter"] = v

    @property
    def show_fft(self) -> bool:
        return bool(self._get("ShowFft"))

    @show_fft.setter
    def show_fft(self, v: bool) -> None:
        self._d["ShowFft"] = bool(v)

    @property
    def fft_span_khz(self) -> float:
        v = float(self._get("FftSpanKhz"))
        return v if v in (3, 6, 12, 24, 48, 96, 192) else 48.0

    @fft_span_khz.setter
    def fft_span_khz(self, v: float) -> None:
        self._d["FftSpanKhz"] = float(v)

    @property
    def agc_target_db(self) -> float:
        try:
            return min(-6.0, max(-60.0, float(self._get("AgcTargetDb"))))
        except (TypeError, ValueError):
            return -28.0

    @agc_target_db.setter
    def agc_target_db(self, v: float) -> None:
        self._d["AgcTargetDb"] = float(v)


class Settings:
    def __init__(self, path: Path | None = None):
        self.path = Path(path) if path else config_dir() / "settings.json"
        self._d: Dict[str, Any] = {}
        self.load()

    def load(self) -> None:
        try:
            with open(self.path, "r", encoding="utf-8-sig") as f:
                data = json.load(f)
            self._d = data if isinstance(data, dict) else {}
        except (OSError, ValueError):
            self._d = {}

    def save(self) -> None:
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            fd, tmp = tempfile.mkstemp(dir=str(self.path.parent), prefix=".settings-", suffix=".json")
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                json.dump(self._d, f, indent=2)
            os.replace(tmp, self.path)
        except OSError:
            pass  # settings are best effort

    def _get(self, key: str, default):
        if key in self._d:
            return self._d[key]
        for k, v in self._d.items():
            if k.lower() == key.lower():
                return v
        return default

    def _set(self, key: str, value) -> None:
        for k in list(self._d):
            if k.lower() == key.lower() and k != key:
                del self._d[k]
        self._d[key] = value

    # ── general ──
    @property
    def manual_ip(self) -> str:
        return str(self._get("ManualIp", "") or "")

    @manual_ip.setter
    def manual_ip(self, v: str) -> None:
        self._set("ManualIp", v)

    @property
    def dual_mode(self) -> bool:
        return bool(self._get("DualMode", False))

    @dual_mode.setter
    def dual_mode(self, v: bool) -> None:
        self._set("DualMode", bool(v))

    @property
    def network_saver(self) -> bool:
        return bool(self._get("NetworkSaver", False))

    @network_saver.setter
    def network_saver(self, v: bool) -> None:
        self._set("NetworkSaver", bool(v))

    @property
    def station_target_slot(self) -> str:
        return "B" if self._get("StationTargetSlot", "A") == "B" else "A"

    @station_target_slot.setter
    def station_target_slot(self, v: str) -> None:
        self._set("StationTargetSlot", "B" if v == "B" else "A")

    @property
    def last_radio(self) -> Dict[str, Any]:
        """Native-build addition: last radio per slot, for auto-reconnect at start-up."""
        v = self._get("NativeLastRadio", {})
        return v if isinstance(v, dict) else {}

    def remember_radio(self, slot: str, host: str, port: int, title: str, serial: str) -> None:
        d = dict(self.last_radio)
        d[slot] = {"Host": host, "Port": port, "Title": title, "Serial": serial}
        self._set("NativeLastRadio", d)

    def forget_radio(self, slot: str) -> None:
        d = dict(self.last_radio)
        d.pop(slot, None)
        self._set("NativeLastRadio", d)

    def slot(self, slot: str) -> SlotPrefs:
        slots = self._get("Slots", None)
        if not isinstance(slots, dict):
            slots = {}
            self._set("Slots", slots)
        s = slots.get(slot)
        if not isinstance(s, dict):
            s = slots[slot] = {}
        return SlotPrefs(s)

    # ── station tools (same keys as the .NET build) ──
    def _str(self, key: str, default: str = "") -> str:
        v = self._get(key, default)
        return default if v is None else str(v)

    def _int(self, key: str, default: int, lo: int, hi: int) -> int:
        try:
            return min(hi, max(lo, int(self._get(key, default))))
        except (TypeError, ValueError):
            return default

    @property
    def dx_host(self) -> str:
        return self._str("DxClusterHost")

    @dx_host.setter
    def dx_host(self, v: str) -> None:
        self._set("DxClusterHost", v.strip())

    @property
    def dx_port(self) -> int:
        return self._int("DxClusterPort", 7300, 1, 65535)

    @dx_port.setter
    def dx_port(self, v: int) -> None:
        self._set("DxClusterPort", int(v))

    @property
    def dx_callsign(self) -> str:
        return self._str("DxClusterCallsign").upper()

    @dx_callsign.setter
    def dx_callsign(self, v: str) -> None:
        self._set("DxClusterCallsign", v.strip().upper())

    @property
    def dx_auto_reconnect(self) -> bool:
        return bool(self._get("DxClusterAutoReconnect", True))

    @property
    def spot_max_age_minutes(self) -> int:
        return self._int("SpotMaxAgeMinutes", 30, 1, 24 * 60)

    @property
    def publish_spots_to_radio(self) -> bool:
        return bool(self._get("PublishSpotsToRadio", True))

    @publish_spots_to_radio.setter
    def publish_spots_to_radio(self, v: bool) -> None:
        self._set("PublishSpotsToRadio", bool(v))

    @property
    def pgxl_host(self) -> str:
        return self._str("PgxlHost")

    @pgxl_host.setter
    def pgxl_host(self, v: str) -> None:
        self._set("PgxlHost", v.strip())

    @property
    def pgxl_port(self) -> int:
        return self._int("PgxlPort", 9008, 1, 65535)

    @pgxl_port.setter
    def pgxl_port(self, v: int) -> None:
        self._set("PgxlPort", int(v))

    @property
    def pgxl_auto_reconnect(self) -> bool:
        return bool(self._get("PgxlAutoReconnect", True))

    @property
    def macros(self) -> List[Dict[str, str]]:
        m = self._get("StationMacros", None)
        if not isinstance(m, list) or not m:
            return [dict(x) for x in DEFAULT_MACROS]
        out = []
        for x in m:
            if isinstance(x, dict):
                label = x.get("Label") or x.get("label") or "Macro"
                cmds = x.get("Commands") or x.get("commands") or ""
                out.append({"Label": str(label), "Commands": str(cmds)})
        return out
