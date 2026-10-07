import json
import math

import numpy as np

from flexcompanion import kv, meters, spectrum
from flexcompanion.discovery import parse_packet
from flexcompanion.params import ParamControl, build_controls
from flexcompanion.settings import Settings
from flexcompanion.sim import vita_packet


def test_tokenize_and_parse():
    tok = kv.tokenize('slice 0 mode=USB name="My Radio" RF_frequency=14.2')
    assert tok == ["slice", "0", "mode=USB", "name=My Radio", "RF_frequency=14.2"]
    d = kv.parse(["Nick=Shack\x7fRadio", "noequals", "=x"])
    assert d["nick"] == "Shack Radio" and d.get("NICK") == "Shack Radio" and "noequals" not in d


def test_handles_and_hex_ids():
    assert kv.same_handle("0x0000ABCD", "abcd")
    assert not kv.same_handle("0x1", "0x2")
    assert kv.parse_hex_id("0x20000001") == 0x20000001
    assert kv.parse_hex_id("40000000|extra") == 0x40000000
    assert kv.parse_hex_id("nonsense") == 0


def test_s_meter_text():
    assert meters.s_text(-73) == "S9"
    assert meters.s_text(-121) == "S1"
    assert meters.s_text(-53) == "S9+20"
    assert meters.s_text(math.nan) == "-"


def test_meter_metadata_and_mapping():
    defs = {}
    meters.parse_meter_metadata("1.src=SLC#1.num=0#1.nam=LEVEL#1.unit=dBm#2.src=TX-#2.nam=FWDPWR#2.unit=dBm#"
                                "3.src=SLC#3.num=1#3.nam=LEVEL#3.unit=dBm#9.src=AMP#9.nam=PATEMP#9.unit=degC#"
                                "10.src=RAD#10.nam=PATEMP#10.unit=degC", defs)
    ids = meters.map_meters(defs, 1)
    assert ids.rx == 3 and ids.fwd == 2 and ids.temp == 10      # amplifier PATEMP ignored
    assert meters.desired_meter_ids(ids, "Temp") == {3, 2, 10}
    assert meters.scale_raw(-97 * 128, "dBm") == -97
    assert meters.scale_raw(13 * 256, "Volts") == 13
    assert meters.scale_raw(40 * 64, "degC") == 40


def test_meter_ballistics():
    r = meters.MeterReadings()
    ids = meters.MeterIds(rx=1, fwd=2, swr=3)
    vals = {1: -80.0, 2: 50.0, 3: 1.4}
    r.update(lambda i: vals.get(i, math.nan), ids, False)
    assert r.rx_dbm == -80 and r.transmitting            # 50 dBm = 100 W
    assert 60 < r.fwd_watts < 80 and r.swr == 1.4
    vals[2] = -30.0
    r.update(lambda i: vals.get(i, math.nan), ids, False)
    assert not r.transmitting and r.fwd_watts < 50


def test_tx_spec_power_scales():
    assert meters.tx_spec("Power", 120).max == 120
    au = meters.tx_spec("Power", 600)
    assert au.max == 600 and au.red == 550
    assert meters.tx_spec("Mic").fmt(-60) == "-"


def test_spectrum_window_follows_passband():
    usb = spectrum.window_for("USB", "100", "2800", 3000)
    assert usb.centre_offset_hz == 1450
    # centred on the passband, wide enough for the filter and the carrier at its edge
    assert usb.span_hz >= 2700 + 400 and usb.span_hz / 2 >= usb.centre_offset_hz
    am = spectrum.window_for("AM", None, None, 24000)
    assert am.centre_offset_hz == 0 and am.span_hz == 24000
    saver = spectrum.effective_window("USB", "100", "2800", 96, saver=True)
    assert saver.span_hz == 24000
    assert spectrum.rate_for_span(48000, False) == 48000
    assert spectrum.rate_for_span(150000, False) == 192000
    assert spectrum.rate_for_span(150000, True) == 24000
    assert spectrum.fft_size_for(192000, 3000) == 8192
    assert spectrum.fft_size_for(48000, 48000) == 1024


def test_crop_centres_on_slice():
    full = np.full(4096, -120, dtype=np.float32)
    rate = 48000
    frame = spectrum.crop_to_span(full, rate, spectrum.SpectrumWindow(0, 12000), slice_offset_hz=6000)
    assert abs(frame.half_span_hz * 2 - 12000) < 20
    assert abs(frame.slice_fraction - 0.5) < 0.01
    off = spectrum.crop_to_span(full, rate, spectrum.SpectrumWindow(0, 12000), slice_offset_hz=None)
    assert math.isnan(off.slice_fraction)


def test_param_control_no_echo_and_capability():
    sent = []
    p = ParamControl("NR", toggle_set="nr", toggle_status="nr", level_set="nr_level", level_status="nr_level")
    p.sender = lambda scope, keyval: sent.append(keyval)
    assert not p.is_available                       # requires a report from the radio first
    p.set_on(True)
    assert sent == []
    p.apply_status({"NR": "1", "nr_level": "70"})
    assert p.is_on and p.level == 70 and p.is_available and sent == []
    p.set_on(False)
    p.set_level(40)
    assert sent == ["nr=0", "nr_level=40"]
    p.mark_unsupported()
    assert not p.is_available and "Unsupported" in p.unavailable_text
    p.reset()
    p.clear_unsupported()
    assert not p.is_on and p.level == 50


def test_esc_phase_round_trip():
    esc = next(c for c in build_controls()["ESC / Diversity"] if c.label == "ESC")
    sent = []
    esc.sender = lambda scope, kv_: sent.append(kv_)
    esc.set_level(90)
    assert sent == ["esc_phase_shift=1.570796"]
    esc.apply_status({"esc_phase_shift": "3.141593"})


def test_discovery_packet():
    pkt = vita_packet(0xFFFF, 0x800, b"model=FLEX-8600 serial=ABC nickname=Shack\x7fRig ip=10.0.0.5 port=4992")
    d = parse_packet(pkt)
    assert d["model"] == "FLEX-8600" and d["nickname"] == "Shack Rig" and d["ip"] == "10.0.0.5"
    assert parse_packet(b"garbage") is None


def test_settings_shared_with_dotnet(tmp_path):
    path = tmp_path / "settings.json"
    path.write_text(json.dumps({"ManualIp": "192.168.1.50", "WallpaperPath": "/x.png",
                                "Slots": {"A": {"TxMeter": "SWR", "ShowFft": True, "FftSpanKhz": 24}},
                                "StationMacros": [{"Label": "FT8", "Commands": "@band 20\n@mode DIGU"}]}))
    s = Settings(path)
    assert s.manual_ip == "192.168.1.50"
    assert s.slot("A").tx_meter == "SWR" and s.slot("A").show_fft and s.slot("A").fft_span_khz == 24
    assert s.slot("B").tx_meter == "Power"
    assert s.macros[0]["Label"] == "FT8"
    s.manual_ip = "10.0.0.9"
    s.remember_radio("A", "10.0.0.9", 4992, "Rig", "SER")
    s.save()
    again = json.loads(path.read_text())
    assert again["WallpaperPath"] == "/x.png"          # .NET-only keys are preserved
    assert again["ManualIp"] == "10.0.0.9"
    assert again["NativeLastRadio"]["A"]["Host"] == "10.0.0.9"


def test_settings_corrupt_file(tmp_path):
    p = tmp_path / "settings.json"
    p.write_text("{ not json")
    s = Settings(p)
    assert s.manual_ip == "" and s.macros
