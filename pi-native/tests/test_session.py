"""End-to-end: RadioSession against the simulated radio over real TCP/UDP sockets."""
import math
import time

from conftest import wait_for
from flexcompanion.session import RadioSession
from flexcompanion.station import StationController


def connected_session(sim, dispatcher, **kw):
    s = RadioSession("A", dispatcher, **kw)
    dispatcher.post(lambda: s.connect("127.0.0.1", sim.port, "Sim"))
    assert wait_for(lambda: s.connected and s.selected_slice is not None and len(s._meter_subs) == 3, 6)
    return s


def call(dispatcher, fn):
    return dispatcher.run_sync(fn)


def test_connect_handshake_and_state(sim, dispatcher):
    s = connected_session(sim, dispatcher)
    assert s.radio_title == "SimRadio  (FLEX-6600)" and s.power_max == 120
    assert [st.name for st in s.stations] == ["All stations", "SHACK  (SmartSDR-Win)"]
    assert s.selected_slice.mode == "USB" and s.selected_slice.freq_text == "14.200.000"
    assert "client program FlexCompanion" in sim.log
    assert any(c.startswith("client udpport ") for c in sim.log)
    # only the meters the UI shows: slice LEVEL, FWDPWR, SWR
    assert sorted(s._meter_subs) == [1, 2, 3]
    assert wait_for(lambda: not math.isnan(s.readings.rx_dbm), 3)
    s.shutdown()


def test_tx_meter_choice_changes_subscriptions(sim, dispatcher):
    s = connected_session(sim, dispatcher)
    call(dispatcher, lambda: s.set_tx_meter("Temp"))
    assert wait_for(lambda: sorted(s._meter_subs) == [1, 2, 3, 7], 3)
    call(dispatcher, lambda: s.set_tx_meter("Power"))
    assert wait_for(lambda: sorted(s._meter_subs) == [1, 2, 3], 3)
    assert "unsub meter 7" in sim.log
    s.shutdown()


def test_dsp_controls_bind_and_capabilities(sim, dispatcher):
    s = connected_session(sim, dispatcher)
    nr, rnn = s.control("NR"), s.control("RNN")
    assert nr.is_available
    call(dispatcher, lambda: nr.set_on(True))
    call(dispatcher, lambda: rnn.set_on(True))
    assert wait_for(lambda: "slice set 0 nr=1" in sim.log and not rnn.is_available, 3)
    assert sim.log.index("client bind client_id=8B1D9A6C-0000-4C3E-9C4B-111122223333") < sim.log.index("slice set 0 nr=1")
    assert "does not report support for rnnoise" in s.last_message
    assert wait_for(lambda: sim.slices[0]["nr"] == "1")
    s.shutdown()


def test_mode_gating_restores(sim, dispatcher):
    s = connected_session(sim, dispatcher)
    st = StationController(lambda _slot: s)
    call(dispatcher, lambda: st.set_mode("CW"))
    assert wait_for(lambda: not s.control("ANF").is_available and not s.control("RNN").is_available, 3)
    assert s.control("NR").is_available
    call(dispatcher, lambda: st.set_mode("USB"))
    assert wait_for(lambda: s.control("ANF").is_available, 3)
    assert "restriction cleared" in s.last_message
    s.shutdown()


def test_station_band_tune_and_mox(sim, dispatcher):
    s = connected_session(sim, dispatcher)
    st = StationController(lambda _slot: s)
    call(dispatcher, lambda: st.set_band("40"))
    assert wait_for(lambda: s.selected_slice.freq_text == "7.100.000", 3)
    assert wait_for(lambda: st.status.startswith("40 m"), 3)
    call(dispatcher, lambda: st.toggle_mox())
    assert wait_for(lambda: st.mox_on and s.readings.transmitting, 3)
    assert wait_for(lambda: s.readings.fwd_watts > 50, 3)
    call(dispatcher, lambda: st.toggle_mox())
    assert wait_for(lambda: not st.mox_on and not s.readings.transmitting, 3)
    call(dispatcher, lambda: st.run_macro("# comment\n@band 20\nslice set 0 agc_mode=fast"))
    assert wait_for(lambda: "slice set 0 agc_mode=fast" in sim.log, 3)
    s.shutdown()


def test_dax_iq_spectrum_lifecycle(sim, dispatcher):
    s = connected_session(sim, dispatcher, show_fft=True, fft_span_khz=24)
    assert wait_for(lambda: s.spectrum_frame is not None, 5)
    f = s.spectrum_frame
    assert abs(f.slice_fraction - 0.5) < 0.1
    peak = f.slice_fraction + 1500 / (f.half_span_hz * 2)       # the sim's +1.5 kHz tone
    i = int(round(peak * (len(f.bins) - 1)))
    assert f.bins[max(0, i - 3):i + 4].max() > float(f.bins.mean()) + 30
    assert "display pan set 0x40000000 daxiq_channel=1" in sim.log
    assert "stream set 0x20000001 daxiq_rate=24000" in sim.log
    # turning the spectrum off releases everything Companion created
    call(dispatcher, lambda: s.set_show_fft(False))
    assert wait_for(lambda: "stream remove 0x20000001" in sim.log, 3)
    assert wait_for(lambda: sim.pans["0x40000000"]["daxiq_channel"] == "0", 3)
    s.shutdown()


def test_disconnect_releases_and_reconnects(sim, dispatcher):
    s = connected_session(sim, dispatcher, show_fft=True)
    assert wait_for(lambda: s._iq_stream_id != 0, 5)
    call(dispatcher, s.disconnect)
    assert wait_for(lambda: "stream remove 0x20000001" in sim.log, 3)
    assert not s.connected and s.status_text == "Not connected" and not s.slices
    call(dispatcher, s.reconnect)
    assert wait_for(lambda: s.connected and s.selected_slice is not None, 5)
    s.shutdown()


def test_lost_connection_retries(dispatcher):
    from flexcompanion import session as session_mod
    from flexcompanion.sim import SimRadio
    session_mod.RECONNECT_S = 0.3
    try:
        sim = SimRadio(port=0).start()
        port = sim.port
        s = connected_session(sim, dispatcher)
        sim.stop()
        assert wait_for(lambda: "retrying" in s.status_text, 3)
        sim2 = None
        for _ in range(60):                     # the port may linger briefly after the old sim closes
            try:
                sim2 = SimRadio(port=port).start()
                break
            except OSError:
                time.sleep(0.1)
        assert sim2 is not None
        assert wait_for(lambda: s.connected, 6)
        s.shutdown()
        sim2.stop()
    finally:
        session_mod.RECONNECT_S = 5.0


def test_connect_failure_reports(dispatcher):
    s = RadioSession("A", dispatcher)
    dispatcher.post(lambda: s.connect("127.0.0.1", 1, "Nothing"))
    assert wait_for(lambda: not s.connecting and s.status_text.startswith(("Connect failed", "No answer")), 6)
    assert not s.connected
    s.shutdown()
