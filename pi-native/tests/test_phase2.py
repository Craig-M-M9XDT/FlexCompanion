"""Best AGC-T, DX cluster, Power Genius XL and the Aether shared-pan bridge."""
import math
import threading
import time

import numpy as np
import pytest

from conftest import wait_for
from flexcompanion import _fallback, aether, spectrum
from flexcompanion.agc import AgcTCalibrator, CurvePoint
from flexcompanion.dxcluster import is_login_prompt, parse_spot
from flexcompanion.pgxl import PgxlTelemetry, dbm_to_watts, parse_kv, return_loss_to_swr
from flexcompanion.session import RadioSession
from flexcompanion.sim import SimDxCluster, SimPgxl, send_aether_frame
from flexcompanion.station import StationController

try:
    from flexcompanion import flexcore
except ImportError:
    flexcore = None

SERIAL = "1234-5678-9ABC-DEF0"


def connect(sim, dispatcher, **kw):
    s = RadioSession("A", dispatcher, **kw)
    dispatcher.post(lambda: s.connect("127.0.0.1", sim.port, "Sim", SERIAL))
    assert wait_for(lambda: s.connected and s.selected_slice is not None, 6)
    s.agc.settle_s = 0.08          # the app uses 0.28 s; enough margin for a busy CI runner
    return s


# ───────────────────────── AGC-T ─────────────────────────

def _calibrator(points, off=False, target=-28.0):
    c = AgcTCalibrator(dispatcher=None)
    c.curve = [CurvePoint(v, db) for v, db in points]
    c.is_off_mode = lambda: off
    c.target_db = target
    c._recompute()
    return c


def test_calibrator_finds_knee():
    pts = [(v, -20.0 if v >= 40 else -20.0 - (40 - v) * 0.6) for v in range(0, 101, 4)]
    c = _calibrator(pts)
    assert c.recommended_is_knee and abs(c.recommended - 40) <= 4


def test_calibrator_agc_off_target():
    pts = [(v, -60 + 0.6 * v) for v in range(0, 101, 4)]
    c = _calibrator(pts, off=True, target=-30)
    assert not c.recommended_is_knee and c.recommended == 50


def test_agc_sweep_end_to_end(sim, dispatcher):
    s = connect(sim, dispatcher)
    dispatcher.run_sync(lambda: s.control("NR").set_on(True))
    assert wait_for(lambda: sim.slices[0]["nr"] == "1")
    assert "temporarily disable NR" in dispatcher.run_sync(lambda: s.agc_warning)
    dispatcher.run_sync(s.start_agc_sweep)
    assert wait_for(lambda: sim.slices[0]["nr"] == "0", 3)            # filters off for the scan
    assert wait_for(lambda: s.agc.recommended >= 0 and not s.agc_busy, 15)
    assert s.agc.recommended == 60 and s.agc.recommended_is_knee      # the simulator's knee
    assert sim.slices[0]["agc_threshold"] == "60"                     # applied
    assert wait_for(lambda: sim.slices[0]["nr"] == "1", 3)            # and NR restored
    assert wait_for(lambda: any(l.startswith("stream remove 0x04") for l in sim.log), 3)   # audio released
    dispatcher.run_sync(s.agc_restore)
    assert wait_for(lambda: sim.slices[0]["agc_threshold"] == "65", 3)
    s.shutdown()


def test_agc_off_mode_hits_target(sim, dispatcher):
    sim.slices[0]["agc_mode"] = "off"
    s = connect(sim, dispatcher, agc_target_db=-28)
    assert dispatcher.run_sync(lambda: s.agc_is_off)
    dispatcher.run_sync(s.start_agc_sweep)
    assert wait_for(lambda: s.agc.recommended >= 0 and not s.agc_busy, 15)
    assert 52 <= s.agc.recommended <= 54 and not s.agc.recommended_is_knee   # -60 + 0.6 x = -28
    dispatcher.run_sync(s.agc_keep)
    assert sim.slices[0]["agc_off_level"] == str(s.agc.recommended)
    s.shutdown()


def test_agc_needs_dax_channel(sim, dispatcher):
    sim.slices[0]["dax"] = "0"
    s = connect(sim, dispatcher)
    dispatcher.run_sync(s.start_agc_sweep)
    assert wait_for(lambda: s.needs_dax, 3)
    dispatcher.run_sync(s.assign_dax)
    assert wait_for(lambda: sim.slices[0]["dax"] == "1", 3)
    assert wait_for(lambda: s.agc.recommended >= 0, 15)                # the waiting sweep then runs
    s.shutdown()


def test_disconnect_during_sweep_restores(sim, dispatcher):
    s = connect(sim, dispatcher)
    s.agc.settle_s = 0.2
    dispatcher.run_sync(s.start_agc_sweep)
    assert wait_for(lambda: s.agc.running, 5)
    dispatcher.run_sync(s.disconnect)
    assert wait_for(lambda: sim.slices[0]["agc_threshold"] == "65", 3)
    s.shutdown()


# ───────────────────────── DX cluster ─────────────────────────

def test_spot_parsing():
    sp = parse_spot("DX de G4ABC:      14074.0  JA1XYZ       FT8 -12dB                      1712Z")
    assert sp.callsign == "JA1XYZ" and sp.frequency_mhz == pytest.approx(14.074) and sp.spotter == "G4ABC"
    assert sp.comment == "FT8 -12dB" and sp.utc == "1712Z"
    assert parse_spot("WWV de VE7CC <18>:   SFI=150") is None
    assert is_login_prompt("Please enter your call:") and is_login_prompt("login:")


def test_dx_cluster_end_to_end(sim, dispatcher):
    s = connect(sim, dispatcher)
    dxs = SimDxCluster()
    st = StationController(lambda _slot: s, dispatcher=dispatcher)
    try:
        dispatcher.run_sync(lambda: st.dx_connect("127.0.0.1", dxs.port, "m9xdt"))
        assert wait_for(lambda: dxs.received[:1] == ["M9XDT"], 3)        # logged in at the bare prompt
        dxs.spot("G4ABC", 14074.0, "JA1XYZ", "FT8")
        dxs.spot("EA1AA", 7012.5, "VK2DEF", "CW")
        dxs.spot("G4ABC", 14074.2, "JA1XYZ", "FT8 again")             # re-spot replaces the old one
        assert wait_for(lambda: len(st.spots) == 2 and st.spots[0].comment == "FT8 again", 3)
        assert wait_for(lambda: any(l.startswith("spot add callsign=VK2DEF rx_freq=7.012500") for l in sim.log), 3)
        dispatcher.run_sync(lambda: st.tune_spot(st.spots[-1]))
        assert wait_for(lambda: sim.slices[0]["RF_frequency"] == "7.012500", 3)
    finally:
        st.shutdown()
        dxs.stop()
        s.shutdown()


def test_dx_cluster_reconnects(dispatcher):
    dxs = SimDxCluster()
    st = StationController(lambda _slot: None, dispatcher=dispatcher, settings=None)
    st.dx.reconnect_base_s = 0.2
    try:
        dispatcher.run_sync(lambda: st.dx_connect("127.0.0.1", dxs.port, "M9XDT"))
        assert wait_for(lambda: st.dx.connected and dxs.received[:1] == ["M9XDT"], 3)
        dxs.drop_clients()
        assert wait_for(lambda: not st.dx.connected or len(dxs.received) >= 2, 3)
        assert wait_for(lambda: dxs.received.count("M9XDT") >= 2, 5)     # logged in again
    finally:
        st.shutdown()
        dxs.stop()


# ───────────────────────── Power Genius XL ─────────────────────────

def test_pgxl_conversions():
    assert dbm_to_watts("60") == pytest.approx(1000)
    assert return_loss_to_swr("-20") == pytest.approx(1.222, abs=0.01)
    assert math.isnan(dbm_to_watts("x"))
    t = PgxlTelemetry()
    t.apply(parse_kv("state=OPERATE fwd=57 swr=-26 id=18.2 temp=40.5 vpa=50 mains=230"))
    assert round(t.power_w) == 501 and t.current_a == 18.2 and t.vdd == 50 and t.vac == 230


def test_pgxl_end_to_end(dispatcher):
    pg = SimPgxl()
    st = StationController(lambda _slot: None, dispatcher=dispatcher)
    st.pgxl.retry_s = 0.2
    try:
        dispatcher.run_sync(lambda: st.pgxl_connect("127.0.0.1", pg.port))
        assert wait_for(lambda: st.amp.state == "OPERATE", 3)
        assert round(st.amp.power_w) == 1000 and st.amp.temp_c == 41.0 and "v3.8.11" in st.pgxl_status
        pg.fwd_dbm = 57.0
        assert wait_for(lambda: round(st.amp.power_w) == 501, 3)         # polled every 250 ms
        pg.alert("HIGH SWR")
        assert wait_for(lambda: st.amp_alert == "HIGH SWR", 3)
        pg.drop_clients()
        assert wait_for(lambda: len([l for l in pg.received if l.endswith("|info")]) >= 2, 5)   # reconnected
    finally:
        st.shutdown()
        pg.stop()


# ───────────────────────── Aether bridge ─────────────────────────

BACKENDS = [pytest.param(_fallback, id="numpy"),
            pytest.param(flexcore, id="cpp", marks=pytest.mark.skipif(flexcore is None, reason="C++ core not built"))]


@pytest.mark.parametrize("core", BACKENDS)
def test_fcsp_parsing(core):
    bins = np.linspace(-130, -40, 512).astype(np.float32)
    bins[3] = np.nan
    pkt = aether.fcsp_packet(SERIAL, 0x40000000, bins, 123456789)
    serial, sid, out, ns = core.parse_fcsp(pkt)
    assert serial == SERIAL and sid == 0x40000000 and ns == 123456789
    assert out[3] == -160 and out[10] == pytest.approx(bins[10])
    assert core.parse_fcsp(pkt[:30]) is None
    assert core.parse_fcsp(b"XXXX" + pkt[4:]) is None


def test_reslice_centres_slice():
    bins = np.full(2048, -120, np.float32)
    bins[1024 + 64] = -30                                   # a signal 1.5 kHz above the pan centre
    w = spectrum.SpectrumWindow(1450, 12000)
    f = spectrum.reslice_pan(bins, 14.2, 0.048, 14.2, w)   # 48 kHz pan, slice at pan centre
    assert len(f.bins) == spectrum.RESLICE_BINS
    peak = int(np.argmax(f.bins)) / (len(f.bins) - 1)
    expected = f.slice_fraction + 1500 / 12000
    assert abs(peak - expected) < 0.02


def test_aether_takes_over_and_falls_back(sim, dispatcher):
    s = connect(sim, dispatcher, show_fft=True)
    assert wait_for(lambda: s._iq_stream_id != 0, 5)                   # no Aether: DAX IQ after the probe
    bins = np.full(1024, -120, np.float32)
    bins[600] = -40
    stop = threading.Event()
    port = aether.AetherPanBridge.instance().port

    def feed():
        while not stop.is_set():
            send_aether_frame(SERIAL, 0x40000000, bins, port)
            time.sleep(0.04)
    t = threading.Thread(target=feed, daemon=True)
    t.start()
    try:
        assert wait_for(lambda: s.spectrum_status.startswith("Aether") and s._iq_stream_id == 0, 4)
        assert "stream remove 0x20000001" in sim.log                    # our DAX IQ stream was released
        assert len(s.spectrum_frame.bins) == spectrum.RESLICE_BINS
    finally:
        stop.set()
        t.join()
    assert wait_for(lambda: s._iq_stream_id != 0 and s.spectrum_status.startswith("DAX"), 6)
    s.shutdown()


def test_aether_frames_for_other_radio_ignored(sim, dispatcher):
    s = connect(sim, dispatcher, show_fft=True)
    assert wait_for(lambda: s._iq_stream_id != 0, 5)
    port = aether.AetherPanBridge.instance().port
    for _ in range(10):
        send_aether_frame("OTHER-SERIAL", 0x40000000, np.full(256, -100, np.float32), port)
        time.sleep(0.03)
    time.sleep(0.6)
    assert s._iq_stream_id != 0 and not s.spectrum_status.startswith("Aether")
    s.shutdown()
