"""Meter metadata, unit scaling, S-units and TX meter scales (port of RadioViewModel.Meters.cs)."""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Callable, Dict, List, Optional, Tuple

TX_METER_OPTIONS = ["Power", "SWR", "Proc", "Mic", "Vdd", "Current", "Temp"]

# S-meter scale: dBm -> label
RX_SCALE: List[Tuple[float, str]] = [(-121, "1"), (-109, "3"), (-97, "5"), (-85, "7"), (-73, "9"),
                                     (-53, "+20"), (-33, "+40"), (-13, "+60")]
RX_MIN, RX_MAX, RX_RED = -127.0, -13.0, -73.0


@dataclass
class MeterDef:
    id: int
    source: str = ""
    num: int = -1
    name: str = ""
    unit: str = ""


def parse_meter_metadata(rest: str, defs: Dict[int, MeterDef]) -> None:
    """``meter list`` / ``S|meter`` body: ``7.src=SLC#7.num=0#7.nam=LEVEL#7.unit=dBm#...``"""
    for part in rest.split("#"):
        if not part:
            continue
        dot = part.find(".")
        eq = part.find("=")
        if dot <= 0 or eq < dot:
            continue
        try:
            mid = int(part[:dot])
        except ValueError:
            continue
        key = part[dot + 1:eq]
        val = part[eq + 1:]
        d = defs.get(mid)
        if d is None:
            d = defs[mid] = MeterDef(mid)
        if key == "src":
            d.source = val
        elif key == "num":
            try:
                d.num = int(val)
            except ValueError:
                pass
        elif key == "nam":
            d.name = val
        elif key == "unit":
            d.unit = val


def scale_raw(raw: int, unit: str) -> float:
    u = unit.lower()
    if u in ("dbm", "db", "dbfs", "swr"):
        return raw / 128.0
    if u in ("volts", "amps"):
        return raw / 256.0
    if u == "degc":
        return raw / 64.0
    if u == "degf":
        return (raw / 64.0 - 32) * 5 / 9
    return float(raw)


def s_text(dbm: float) -> str:
    if math.isnan(dbm):
        return "-"
    if dbm <= -73:
        s = min(9, max(0, int(round(9 + (dbm + 73) / 6.0))))
        return f"S{s}"
    return f"S9+{round(dbm + 73):.0f}"


@dataclass
class MeterIds:
    rx: int = -1
    fwd: int = -1
    swr: int = -1
    mic: int = -1
    comp: int = -1
    vdd: int = -1
    amps: int = -1
    temp: int = -1


def map_meters(defs: Dict[int, MeterDef], slice_index: int) -> MeterIds:
    ids = MeterIds()
    for d in defs.values():
        src = d.source.upper()
        cod = src.startswith("COD")
        tx = src.startswith("TX")
        amp = src == "AMP"
        n = d.name.upper()
        if src == "SLC" and d.num == slice_index and n == "LEVEL":
            ids.rx = d.id
        elif tx and n == "FWDPWR":
            ids.fwd = d.id
        elif tx and n == "SWR":
            ids.swr = d.id
        elif n == "MICPEAK" and (ids.mic < 0 or cod):
            ids.mic = d.id
        elif n == "COMPPEAK" and (ids.comp < 0 or cod):
            ids.comp = d.id
        elif n == "+13.8A":
            ids.vdd = d.id
        elif not amp and n == "PACURRENT":
            ids.amps = d.id
        elif not amp and n == "PATEMP":
            ids.temp = d.id
    return ids


def desired_meter_ids(ids: MeterIds, tx_meter: str) -> set:
    want = {ids.rx, ids.fwd, ids.swr}
    extra = {"Mic": ids.mic, "Proc": ids.comp, "Vdd": ids.vdd, "Current": ids.amps, "Temp": ids.temp}.get(tx_meter)
    if extra is not None:
        want.add(extra)
    return {i for i in want if i >= 0}


@dataclass
class TxMeterSpec:
    key: str
    title: str
    min: float
    max: float
    red: float
    scale: List[Tuple[float, str]]
    fmt: Callable[[float], str]


def _nan(fmt: Callable[[float], str]) -> Callable[[float], str]:
    return lambda v: "-" if math.isnan(v) else fmt(v)


def tx_spec(key: str, power_max: float = 120.0) -> TxMeterSpec:
    def ticks(vals):
        return [(float(v), f"{v:g}") for v in vals]
    if key == "SWR":
        return TxMeterSpec("SWR", "SWR", 1, 3, 2, ticks([1, 1.5, 2, 2.5, 3]), _nan(lambda v: f"{v:.1f}:1"))
    if key == "Proc":
        return TxMeterSpec("Proc", "PROC dB", 0, 25, 20, ticks([0, 5, 10, 15, 20, 25]), _nan(lambda v: f"{v:.0f} dB"))
    if key == "Mic":
        return TxMeterSpec("Mic", "MIC dB", -40, 0, -5, ticks([-40, -30, -20, -10, 0]),
                           _nan(lambda v: "-" if v <= -59 else f"{v:.0f} dB"))
    if key == "Vdd":
        return TxMeterSpec("Vdd", "Vdd", 10, 16, 15, ticks(range(10, 17)), _nan(lambda v: f"{v:.1f} V"))
    if key == "Current":
        return TxMeterSpec("Current", "Id  A", 0, 25, 22, ticks([0, 5, 10, 15, 20, 25]), _nan(lambda v: f"{v:.1f} A"))
    if key == "Temp":
        return TxMeterSpec("Temp", "PA °C", 20, 90, 75, ticks([20, 40, 60, 80]), _nan(lambda v: f"{v:.0f} °C"))
    step = 100 if power_max > 200 else 20
    vals = []
    x = 0.0
    while x <= power_max + 0.1:
        vals.append(x)
        x += step
    return TxMeterSpec("Power", "PWR  W", 0, power_max, 550 if power_max > 200 else 105, ticks(vals),
                       _nan(lambda v: f"{v:.0f} W"))


@dataclass
class MeterReadings:
    """Smoothed meter state, updated once per meter packet burst."""
    rx_dbm: float = math.nan
    fwd_watts: float = 0.0
    swr: float = 1.0
    mic_db: float = -60.0
    comp_db: float = 0.0
    vdd: float = math.nan
    amps: float = math.nan
    temp: float = math.nan
    transmitting: bool = False

    def tx_value(self, key: str) -> float:
        return {"SWR": self.swr, "Proc": self.comp_db, "Mic": self.mic_db, "Vdd": self.vdd,
                "Current": self.amps, "Temp": self.temp}.get(key, self.fwd_watts)

    def update(self, read: Callable[[int], float], ids: MeterIds, interlock_tx: bool) -> None:
        """Same ballistics as the .NET build: fast attack, slower release."""
        rx = read(ids.rx)
        if not math.isnan(rx):
            if math.isnan(self.rx_dbm):
                self.rx_dbm = rx
            else:
                self.rx_dbm += (rx - self.rx_dbm) * (0.6 if rx > self.rx_dbm else 0.25)

        fwd = read(ids.fwd)
        watts = 0.0 if math.isnan(fwd) else math.pow(10, fwd / 10.0) / 1000.0
        self.transmitting = interlock_tx or watts > 1.0
        if self.transmitting:
            self.fwd_watts += (watts - self.fwd_watts) * (0.7 if watts > self.fwd_watts else 0.3)
            swr = read(ids.swr)
            if not math.isnan(swr):
                self.swr = max(1.0, swr)
            comp = read(ids.comp)
            if not math.isnan(comp):
                self.comp_db = min(30.0, max(0.0, comp))
        else:
            self.fwd_watts *= 0.6
            self.swr = 1 + (self.swr - 1) * 0.6
            self.comp_db *= 0.6

        mic = read(ids.mic)
        if not math.isnan(mic):
            self.mic_db = mic

        def smooth(old: float, new: float, k: float) -> float:
            if math.isnan(new):
                return old
            return new if math.isnan(old) else old + (new - old) * k

        self.vdd = smooth(self.vdd, read(ids.vdd), 0.3)
        self.amps = smooth(self.amps, read(ids.amps), 0.4)
        self.temp = smooth(self.temp, read(ids.temp), 0.1)


def read_meter(meters: Dict[int, int], defs: Dict[int, MeterDef], mid: int) -> float:
    if mid < 0:
        return math.nan
    raw = meters.get(mid)
    if raw is None:
        return math.nan
    d: Optional[MeterDef] = defs.get(mid)
    return scale_raw(raw, d.unit if d else "")
