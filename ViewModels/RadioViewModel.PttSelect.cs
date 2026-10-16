using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

/// <summary>
/// PTT audio-source preference is independent of the selected microphone.
/// Never asserts PTT, changes TXREQ, or changes mic_selection.
/// </summary>
public sealed partial class RadioViewModel
{
    public sealed record PttModeOption(string Title, bool OverrideEnabled);

    public IReadOnlyList<PttModeOption> PttModeOptions { get; } = new[]
    {
        new PttModeOption("Keep selected audio (PC/DAX)", false),
        new PttModeOption("Use last analogue microphone", true),
    };

    PttModeOption? _selectedPttMode;
    bool? _pttOverrideReported;
    string _pttStatusPlane = "";
    string _pttMicSelection = "Not reported";
    string _pttSource = "Not reported";
    string _pttInterlock = "Not reported";
    string _pttActionStatus = "Waiting for radio PTT status; changing the selection alone does not change the radio.";
    bool _pttApplyInFlight;

    public PttModeOption SelectedPttMode
    {
        get => _selectedPttMode ??= PttModeOptions[0];
        set
        {
            if (value == null || ReferenceEquals(_selectedPttMode, value)) return;
            _selectedPttMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanApplyPttMode));
            PttActionStatus = "PTT selection pending. Apply is only available after the radio advertises the API field.";
        }
    }

    public string PttMicSelection { get => _pttMicSelection; private set => Set(ref _pttMicSelection, value); }
    public string PttSource { get => _pttSource; private set => Set(ref _pttSource, value); }
    public string PttInterlock { get => _pttInterlock; private set => Set(ref _pttInterlock, value); }
    public string PttActionStatus { get => _pttActionStatus; private set => Set(ref _pttActionStatus, value); }

    public string PttReportedMode => _pttOverrideReported is null
        ? "Not reported by radio; use SmartSDR > Settings > Radio Setup > Phone/CW > PTT Override."
        : _pttOverrideReported == true ? "PTT Override ON: hardware PTT uses analogue mic"
        : "PTT Override OFF: hardware PTT keeps selected PC/DAX/mic audio";

    public bool CanApplyPttMode =>
        IsConnected && _pttOverrideReported is not null && !_pttApplyInFlight
        && _pttInterlock is ("READY" or "RECEIVE")
        && !IsTransmitting
        && _pttStatusPlane is ("transmit" or "interlock")
        && _pttOverrideReported != SelectedPttMode.OverrideEnabled;

    public RelayCommand ApplyPttSelectionCommand => _applyPttSelectionCommand ??=
        new RelayCommand(async () => await ApplyPttSelectionAsync(), () => CanApplyPttMode);
    RelayCommand? _applyPttSelectionCommand;

    void OnPttStatus(string body)
    {
        string plane;
        if (body.StartsWith("interlock ", StringComparison.OrdinalIgnoreCase))
            plane = "interlock";
        else if (body.StartsWith("transmit ", StringComparison.OrdinalIgnoreCase))
            plane = "transmit";
        else return;
        var kv = Kv.Parse(Kv.Tokenize(body[(plane.Length + 1)..]));
        if (kv.Count == 0) return;

        if (plane == "transmit" && kv.TryGetValue("mic_selection", out var mic))
            PttMicSelection = mic;
        if (plane == "interlock")
        {
            if (kv.TryGetValue("source", out var source)) PttSource = source;
            if (kv.TryGetValue("state", out var state)) PttInterlock = state.ToUpperInvariant();
        }
        if (kv.TryGetValue("ptt_override", out var overrideText)
            && (overrideText == "0" || overrideText == "1"))
        {
            _pttOverrideReported = overrideText == "1";
            _pttStatusPlane = plane;
            OnPropertyChanged(nameof(PttReportedMode));
            if (_pttOverrideReported == SelectedPttMode.OverrideEnabled)
                PttActionStatus = "Requested setting confirmed by radio status.";
        }
        OnPropertyChanged(nameof(CanApplyPttMode));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    async Task ApplyPttSelectionAsync()
    {
        var client = _client;
        if (client == null || !CanApplyPttMode) return;

        // Experimental setter paths inherited from an earlier prototype:
        // attempt only on a radio that explicitly advertised ptt_override
        // on the corresponding status plane. Never probe blindly.
        string value = SelectedPttMode.OverrideEnabled ? "1" : "0";
        string command = _pttStatusPlane == "transmit"
            ? $"transmit set ptt_override={value}"
            : $"interlock ptt_override={value}";
        _pttApplyInFlight = true;
        PttActionStatus = "Sending requested PTT audio policy; awaiting radio response.";
        OnPropertyChanged(nameof(CanApplyPttMode));
        try
        {
            var result = await client.SendAsync(command);
            if (!ReferenceEquals(_client, client)) return;
            PttActionStatus = result.Code == 0
                ? "API command accepted. Waiting for explicit radio status confirmation; test routing into a dummy load."
                : $"Radio rejected PTT setting (0x{result.Code:X8}). Configure PTT Override in SmartSDR Phone/CW.";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_client, client))
                PttActionStatus = $"PTT setting error: {ex.Message}";
        }
        finally
        {
            _pttApplyInFlight = false;
            OnPropertyChanged(nameof(CanApplyPttMode));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    void ResetPttSelect()
    {
        _pttOverrideReported = null;
        _pttStatusPlane = "";
        _pttApplyInFlight = false;
        PttMicSelection = "Not reported";
        PttSource = "Not reported";
        PttInterlock = "Not reported";
        PttActionStatus = "Waiting for radio PTT status; selection alone does not change the radio.";
        OnPropertyChanged(nameof(PttReportedMode));
        OnPropertyChanged(nameof(CanApplyPttMode));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }
}
