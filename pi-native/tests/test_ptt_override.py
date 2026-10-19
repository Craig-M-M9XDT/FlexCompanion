from flexcompanion.ptt_override import PttOverrideState


def test_older_firmware_never_writes():
    state = PttOverrideState()
    assert state.observe("transmit", {"mic_selection": "PC"}) is None
    assert state.observe("interlock", {"source": "RCA", "state": "READY"}) is None
    assert state.override is None
    assert "cannot" in state.summary.lower()


def test_advertised_transmit_route_automatic():
    state = PttOverrideState()
    state.observe("transmit", {"mic_selection": "PC", "ptt_override": "1"})
    cmd = state.observe("interlock", {"state": "READY"})
    assert cmd == "transmit set ptt_override=0"
    assert state.maybe_command() is None
    state.complete(0)
    assert "awaiting" in state.summary
    state.observe("transmit", {"ptt_override": "0"})
    assert "preserved" in state.summary


def test_avoid_updates_during_tx_and_when_disabled():
    state = PttOverrideState(auto=False)
    state.observe("transmit", {"mic_selection": "PC", "ptt_override": "1"})
    assert state.observe("interlock", {"source": "RCA", "state": "TRANSMITTING"}) is None
    state.auto = True
    assert state.maybe_command() is None
    assert state.observe("interlock", {"state": "RECEIVE"}) == "transmit set ptt_override=0"


def test_no_retry_after_radio_rejects():
    state = PttOverrideState()
    state.observe("interlock", {"state": "READY", "ptt_override": "1"})
    assert state.observe("transmit", {"mic_selection": "PC"}) == "interlock ptt_override=0"
    state.complete(0x50001000)
    assert state.observe("interlock", {"state": "READY", "ptt_override": "1"}) is None
    assert "rejected" in state.summary
    state.reset()
    assert state.override is None and state.auto is True


def test_invalid_status_never_enables_writes():
    state = PttOverrideState()
    state.observe("interlock", {"state": "READY", "ptt_override": "TRUE"})
    assert state.observe("transmit", {"mic_selection": "PC"}) is None
    assert state.maybe_command() is None
