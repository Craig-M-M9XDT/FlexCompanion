using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

/// <summary>
/// Observes the radio-side hardware-PTT / transmit-audio policy.  This does not
/// synthesize MOX or intercept the physical PTT input. The radio remains the
/// authority for TX audio routing, ownership and all safety interlocks.
/// </summary>
public sealed partial class RadioViewModel
{
    string _pttMicSource = "";
    string _pttHardwareSource = "";
    string _pttInterlockState = "";
    bool? _radioPttOverride;
    string _pttOverrideStatusPlane = "";
    bool _pttOverrideWriteAttempted;
    bool _pttOverrideWriteInFlight;
    bool _pttOverrideWriteFailed;
    bool _pttOverrideWriteAccepted;

    /// <summary>
    /// Slot preference, persisted by AppSettings with the other radio settings.
    /// If an explicit radio API property is observed, keep override OFF while
    /// PC microphone is selected. Never probe unsupported command names.
    /// </summary>
    public bool AutoPreservePcPttAudio
    {
        get => _prefs.AutoPreservePcPttAudio;
        set
        {
            if (_prefs.AutoPreservePcPttAudio == value) return;
            _prefs.AutoPreservePcPttAudio = value;
            OnPropertyChanged(nameof(AutoPreservePcPttAudio));
            OnPropertyChanged(nameof(PttOverrideStatus));
            MaybeDisableRadioPttOverride();
        }
    }

    public string PttHardwareStatus => _pttHardwareSource.Length == 0
        ? "Hardware PTT: not observed"
        : $"Hardware PTT source: {_pttHardwareSource}   •   {_pttInterlockState}";

    public string PttMicSourceStatus => _pttMicSource.Length == 0
        ? "Microphone source: waiting for transmit status"
        : $"Selected microphone: {_pttMicSource}";

    public string PttOverrideStatus
    {
        get
        {
            if (!IsConnected) return "PTT audio policy: disconnected.";
            if (!AutoPreservePcPttAudio)
                return "Automatic PC audio preservation disabled (radio setting unchanged).";
            if (_radioPttOverride is null)
                return "PTT Override API not advertised. Older firmware, including 4.2.20, cannot be corrected by Companion alone.";
            if (!string.Equals(_pttMicSource, "PC", StringComparison.OrdinalIgnoreCase))
                return "Monitoring only: select PC as the transmit microphone to enable automatic preservation.";
            if (_radioPttOverride == false)
                return "Radio confirms PTT Override OFF: hardware PTT preserves PC audio.";
            if (_pttOverrideWriteFailed)
                return "Radio rejected the PTT Override command. Firmware API route must be verified.";
            if (_pttOverrideWriteAccepted)
                return "Disable command accepted; waiting for radio to report PTT Override OFF.";
            if (_pttOverrideWriteInFlight)
                return "Disabling hardware microphone override…";
            if (_pttInterlockState is not ("READY" or "RECEIVE"))
                return "Radio reports PTT Override ON; will disable after TX/interlock returns to idle.";
            return "Radio reports PTT Override ON; preparing to preserve PC audio.";
        }
    }

    void OnPttRadioStatus(string body)
    {
        string scope;
        if (body.StartsWith("transmit ", StringComparison.OrdinalIgnoreCase))
            scope = "transmit";
        else if (body.StartsWith("interlock ", StringComparison.OrdinalIgnoreCase))
            scope = "interlock";
        else
            return;

        var kv = Kv.Parse(Kv.Tokenize(body[(scope.Length + 1)..]));
        bool changed = false;

        if (scope == "transmit" && kv.TryGetValue("mic_selection", out var mic))
        {
            var normalized = mic.Trim().ToUpperInvariant();
            if (_pttMicSource != normalized)
            {
                _pttMicSource = normalized;
                OnPropertyChanged(nameof(PttMicSourceStatus));
                changed = true;
            }
        }
        if (scope == "interlock")
        {
            if (kv.TryGetValue("source", out var source))
            {
                var normalized = source.Trim().ToUpperInvariant();
                if (_pttHardwareSource != normalized)
                {
                    _pttHardwareSource = normalized;
                    OnPropertyChanged(nameof(PttHardwareStatus));
                }
            }
            if (kv.TryGetValue("state", out var state))
            {
                var normalized = state.Trim().ToUpperInvariant();
                if (_pttInterlockState != normalized)
                {
                    _pttInterlockState = normalized;
                    OnPropertyChanged(nameof(PttHardwareStatus));
                    changed = true;
                }
            }
        }

        // Capability detection is deliberately status-driven. v4.2.20 has no
        // verified PTT Override API setter; never send an invented command to it.
        // Future firmware/extensions can expose ptt_override=0|1 on one of the
        // standard transmit or interlock status planes.
        if (kv.TryGetValue("ptt_override", out var reported)
            && (reported == "0" || reported == "1"))
        {
            bool enabled = reported == "1";
            if (_radioPttOverride != enabled || _pttOverrideStatusPlane != scope)
            {
                _radioPttOverride = enabled;
                _pttOverrideStatusPlane = scope;
                _pttOverrideWriteAttempted = false;
                _pttOverrideWriteFailed = false;
                _pttOverrideWriteAccepted = false;
                changed = true;
            }
        }

        if (changed)
        {
            OnPropertyChanged(nameof(PttOverrideStatus));
            MaybeDisableRadioPttOverride();
        }
    }

    void MaybeDisableRadioPttOverride()
    {
        var c = _client;
        if (c == null || !AutoPreservePcPttAudio
            || !string.Equals(_pttMicSource, "PC", StringComparison.OrdinalIgnoreCase)
            || _radioPttOverride != true
            || _pttOverrideWriteAttempted || _pttOverrideWriteInFlight
            || _pttInterlockState is not ("READY" or "RECEIVE"))
            return;

        // The only write routes we will attempt are on a firmware build that
        // explicitly advertised the parameter and the corresponding status plane.
        // Each radio/session gets at most one attempt until status changes.
        string cmd = _pttOverrideStatusPlane == "transmit"
            ? "transmit set ptt_override=0"
            : _pttOverrideStatusPlane == "interlock"
                ? "interlock ptt_override=0" : "";
        if (cmd.Length == 0) return;
        _pttOverrideWriteAttempted = true;
        _pttOverrideWriteInFlight = true;
        OnPropertyChanged(nameof(PttOverrideStatus));
        _ = DisablePttOverrideAsync(c, cmd);
    }

    async Task DisablePttOverrideAsync(FlexClient connection, string command)
    {
        (uint Code, string Message) response;
        try { response = await connection.SendAsync(command); }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_client, connection)) return;
            _pttOverrideWriteInFlight = false;
            _pttOverrideWriteFailed = true;
            LastMessage = $"PTT Override API error: {ex.Message}";
            OnPropertyChanged(nameof(PttOverrideStatus));
            return;
        }
        if (!ReferenceEquals(_client, connection)) return;
        _pttOverrideWriteInFlight = false;
        _pttOverrideWriteFailed = response.Code != 0;
        _pttOverrideWriteAccepted = response.Code == 0;
        if (response.Code != 0)
            LastMessage = $"PTT Override unsupported/rejected: {FlexClient.ErrorText(response.Code)} [0x{response.Code:X8}]";
        OnPropertyChanged(nameof(PttOverrideStatus));
    }

    void ResetPttOverrideMonitor()
    {
        _pttMicSource = "";
        _pttHardwareSource = "";
        _pttInterlockState = "";
        _radioPttOverride = null;
        _pttOverrideStatusPlane = "";
        _pttOverrideWriteAttempted = false;
        _pttOverrideWriteInFlight = false;
        _pttOverrideWriteFailed = false;
        _pttOverrideWriteAccepted = false;
        OnPropertyChanged(nameof(PttMicSourceStatus));
        OnPropertyChanged(nameof(PttHardwareStatus));
        OnPropertyChanged(nameof(PttOverrideStatus));
    }
}
