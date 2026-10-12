using System.Collections.ObjectModel;
using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

/// <summary>
/// A sampler map reported by the radio. No sampler/attenuator values are guessed:
/// only antenna/port pairs advertised by SmartSDR are offered as commands.
/// </summary>
public sealed record ApdSamplerEntry(string TxAntenna, string SelectedPort, IReadOnlyList<string> AvailablePorts);

public sealed partial class RadioViewModel
{
    bool _apdSupported;
    bool _apdReported;
    bool _apdEnabled;
    bool _apdConfigurable;
    bool _apdEqualizerActive;
    string _apdReportedPhase = "";
    string _apdLastAction = "APD controls have not been used.";

    public ObservableCollection<ApdSamplerEntry> ApdSamplers { get; } = new();

    public bool ApdSupported => _apdSupported;
    public bool ApdReported => _apdReported;
    public bool ApdEnabled => _apdEnabled;
    public bool ApdConfigurable => _apdConfigurable;
    public bool ApdEqualizerActive => _apdEqualizerActive;
    public bool ApdCanControl => IsConnected && ApdSupported && ApdReported;
    public string ApdLastAction { get => _apdLastAction; private set => Set(ref _apdLastAction, value); }

    public string ApdStateText => !ApdSupported ? "Not available on this radio"
        : !ApdReported ? "Awaiting APD status from firmware"
        : !ApdEnabled ? "OFF"
        : ApdEqualizerActive ? "ACTIVE · correction in use"
        : _apdReportedPhase.Length > 0 ? _apdReportedPhase.ToUpperInvariant()
        : "ENABLED · no active solution reported";

    void OnApdRadioInfo(string model)
    {
        // FLEX-6000 must never be offered APD writes. The status stream is still
        // authoritative if future models advertise APD without a familiar name.
        bool supported = model.StartsWith("FLEX-8", StringComparison.OrdinalIgnoreCase)
                      || model.StartsWith("AU-", StringComparison.OrdinalIgnoreCase)
                      || model.StartsWith("AURORA", StringComparison.OrdinalIgnoreCase);
        if (_apdSupported != supported)
        {
            _apdSupported = supported;
            OnPropertyChanged(nameof(ApdSupported));
            OnPropertyChanged(nameof(ApdStateText));
            OnPropertyChanged(nameof(ApdCanControl));
        }
    }

    void OnApdStatus(string body)
    {
        var tokens = Kv.Tokenize(body);
        if (tokens.Count < 2 || !tokens[0].Equals("apd", StringComparison.OrdinalIgnoreCase)) return;

        if (tokens[1].Equals("sampler", StringComparison.OrdinalIgnoreCase))
        {
            var sampler = Kv.Parse(tokens.Skip(2));
            string ant = sampler.GetValueOrDefault("tx_ant")?.ToUpperInvariant() ?? "";
            if (ant.Length == 0 || !sampler.ContainsKey("valid_samplers") && !sampler.ContainsKey("selected_sampler"))
                return;
            string selected = sampler.GetValueOrDefault("selected_sampler")?.ToUpperInvariant() ?? "";
            var available = new List<string> { "INTERNAL" };
            if (sampler.TryGetValue("valid_samplers", out var candidates))
            {
                foreach (string candidate in candidates.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string port = candidate.ToUpperInvariant();
                    if (port.Length > 0 && !available.Contains(port, StringComparer.OrdinalIgnoreCase))
                        available.Add(port);
                }
            }
            int existing = -1;
            for (int i = 0; i < ApdSamplers.Count; ++i)
                if (ApdSamplers[i].TxAntenna.Equals(ant, StringComparison.OrdinalIgnoreCase))
                    { existing = i; break; }
            if (existing >= 0 && !sampler.ContainsKey("valid_samplers"))
                available = ApdSamplers[existing].AvailablePorts.ToList();
            if (selected.Length == 0 && existing >= 0) selected = ApdSamplers[existing].SelectedPort;
            if (selected.Length == 0) selected = "INTERNAL";
            var entry = new ApdSamplerEntry(ant, selected, available);
            if (existing >= 0) ApdSamplers[existing] = entry;
            else ApdSamplers.Add(entry);
            return;
        }

        var kv = Kv.Parse(tokens.Skip(1));
        bool resetReported = tokens.Any(t => t.Equals("equalizer_reset", StringComparison.OrdinalIgnoreCase));
        if (!kv.Keys.Any(k => k is "enable" or "configurable" or "equalizer_active" or "state" or "status")
            && !resetReported) return;
        _apdReported = true;
        if (!_apdSupported) { _apdSupported = true; OnPropertyChanged(nameof(ApdSupported)); }
        if (kv.TryGetValue("enable", out var en)) _apdEnabled = en == "1" || en.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (kv.TryGetValue("configurable", out var conf)) _apdConfigurable = conf == "1" || conf.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (kv.TryGetValue("equalizer_active", out var active)) _apdEqualizerActive = active == "1" || active.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (resetReported) _apdEqualizerActive = false;
        if (kv.TryGetValue("state", out var state) || kv.TryGetValue("status", out state))
            _apdReportedPhase = state;
        OnPropertyChanged(nameof(ApdReported));
        OnPropertyChanged(nameof(ApdEnabled));
        OnPropertyChanged(nameof(ApdConfigurable));
        OnPropertyChanged(nameof(ApdEqualizerActive));
        OnPropertyChanged(nameof(ApdStateText));
        OnPropertyChanged(nameof(ApdCanControl));
        CommandManagerRefresh();
    }

    void ClearApdState()
    {
        _apdSupported = _apdReported = _apdEnabled = _apdConfigurable = _apdEqualizerActive = false;
        _apdReportedPhase = "";
        ApdSamplers.Clear();
        ApdLastAction = "APD controls have not been used.";
        OnPropertyChanged(nameof(ApdSupported));
        OnPropertyChanged(nameof(ApdReported));
        OnPropertyChanged(nameof(ApdEnabled));
        OnPropertyChanged(nameof(ApdConfigurable));
        OnPropertyChanged(nameof(ApdEqualizerActive));
        OnPropertyChanged(nameof(ApdStateText));
        OnPropertyChanged(nameof(ApdCanControl));
    }

    public async Task SetApdEnabledAsync(bool on)
    {
        if (!ApdCanControl) { ApdLastAction = "APD is unavailable or has not reported its status."; return; }
        var (code, _) = await ExecuteAsync($"apd enable={(on ? 1 : 0)}");
        ApdLastAction = code == 0 ? $"APD {(on ? "enable" : "disable")} accepted; awaiting radio status."
                                  : $"APD command rejected: {FlexClient.ErrorText(code)}";
        // Never optimistically set ApdEnabled. The status message is authoritative.
    }

    public async Task ResetApdEqualizerAsync()
    {
        if (!ApdCanControl || IsTransmitting)
        {
            ApdLastAction = "Reset only available while connected and not transmitting.";
            return;
        }
        var (code, _) = await ExecuteAsync("apd reset");
        ApdLastAction = code == 0 ? "APD reset accepted; await the next radio status."
                                  : $"APD reset rejected: {FlexClient.ErrorText(code)}";
    }

    public async Task ApplyApdSamplerAsync(string txAntenna, string samplePort)
    {
        var entry = ApdSamplers.FirstOrDefault(x => x.TxAntenna == txAntenna);
        if (!ApdCanControl || !ApdConfigurable || IsTransmitting || entry == null
            || !entry.AvailablePorts.Contains(samplePort, StringComparer.OrdinalIgnoreCase))
        {
            ApdLastAction = "Sampler change blocked: unsupported, transmitting, or port not advertised by radio.";
            return;
        }
        var (code, _) = await ExecuteAsync($"apd sampler tx_ant={txAntenna} sample_port={samplePort}");
        ApdLastAction = code == 0 ? $"{txAntenna} sampler command accepted; awaiting radio status."
                                  : $"Sampler change rejected: {FlexClient.ErrorText(code)}";
    }
}
