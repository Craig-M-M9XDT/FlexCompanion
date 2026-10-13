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
    bool _apdMonitorActive;
    readonly ObservableCollection<string> _apdStatusHistory = new();
    public ObservableCollection<string> ApdStatusHistory => _apdStatusHistory;
    string _apdAntenna = "";
    double? _apdFreqMhz;
    int? _apdPowerSetting;
    int? _apdSampleIndex;
    double? _apdTxErrorMilliHz, _apdRxErrorMilliHz;
    string _apdModel = "";

    public string ApdCalibrationContext =>
        $"Antenna: {(_apdAntenna.Length > 0 ? _apdAntenna : "not reported")}  |  " +
        $"Frequency: {(_apdFreqMhz.HasValue ? _apdFreqMhz.Value.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture) + " MHz" : "not reported")}  |  " +
        $"RF drive: {(_apdPowerSetting.HasValue ? _apdPowerSetting.Value + "%" : "not reported")}  |  " +
        $"Sample index: {(_apdSampleIndex.HasValue ? _apdSampleIndex.ToString() : "not reported")}";

    public string ApdFrequencyErrorText =>
        $"TX frequency error: {(_apdTxErrorMilliHz.HasValue ? _apdTxErrorMilliHz.Value.ToString("0.###") + " mHz" : "not reported")}  |  " +
        $"RX frequency error: {(_apdRxErrorMilliHz.HasValue ? _apdRxErrorMilliHz.Value.ToString("0.###") + " mHz" : "not reported")}";

    // Null = the radio has not supplied enough metadata to verify this correction.
    public bool? ApdMatchesCurrentTx
    {
        get
        {
            if (!ApdEqualizerActive || !IsConnected) return null;
            if (_apdAntenna.Length == 0 || !_apdFreqMhz.HasValue || !_apdPowerSetting.HasValue) return null;
            var txSlice = Slices.FirstOrDefault(s => s.State.GetValueOrDefault("tx") == "1" && s.State.GetValueOrDefault("in_use") != "0");
            if (txSlice == null) return null;
            string ant = txSlice.State.GetValueOrDefault("txant") ?? txSlice.State.GetValueOrDefault("tx_ant") ?? "";
            var mhz = Kv.D(txSlice.State.GetValueOrDefault("RF_frequency"));
            if (ant.Length == 0 || !mhz.HasValue) return null;
            if (!_apdTxPowerReported) return null;
            return ant.Equals(_apdAntenna, StringComparison.OrdinalIgnoreCase)
                && Math.Abs(mhz.Value - _apdFreqMhz.Value) < 0.000001
                && _txRfPower == _apdPowerSetting.Value;
        }
    }
    bool _apdTxPowerReported;

    void NotifyApdContextChanged()
    {
        OnPropertyChanged(nameof(ApdMatchesCurrentTx));
        OnPropertyChanged(nameof(ApdStateText));
    }

    /// <summary>Creates a read-only, timestamped diagnostic snapshot; no radio file-download command is sent.</summary>
    public string CreateApdDiagnosticSnapshot()
    {
        var lines = new List<string>
        {
            "Flex Control Companion APD LAB — read-only diagnostics",
            $"Captured UTC: {DateTime.UtcNow:O}",
            $"Radio: {RadioTitle}; slot {Slot}; connected={IsConnected}",
            $"APD: {ApdStateText}",
            ApdCalibrationContext,
            ApdFrequencyErrorText,
            $"APD sampler ADC level: NOT EXPOSED BY THE VERIFIED API",
            $"IMD comparison: NOT MEASURED",
            $"Physical attenuation: NOT CONTROLLABLE BY COMPANION",
            "",
            "Radio-advertised APD samplers:"
        };
        foreach (var sampler in ApdSamplers)
            lines.Add($"{sampler.TxAntenna}: selected={sampler.SelectedPort}, valid={string.Join(",", sampler.AvailablePorts)}");
        lines.Add("");
        lines.Add("Radio meter inventory: source and raw sample are uncalibrated protocol values; no ADC assumption.");
        foreach (var m in _meterDefs.Values.OrderBy(m => m.Id))
        {
            var hasRaw = _client != null && _client.Meters.TryGetValue((ushort)m.Id, out _);
            string raw = hasRaw && _client != null && _client.Meters.TryGetValue((ushort)m.Id, out var value)
                ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "not subscribed / not sampled";
            lines.Add($"{m.Id}: {m.Source}#{m.Num} {m.Name} [{m.Unit}] raw={raw}");
        }
        lines.Add("");
        lines.Add("Recent APD status stream (most recent last):");
        lines.AddRange(ApdStatusHistory);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    void AppendApdTrace(string message)
    {
        if (!_apdMonitorActive) return;
        _apdStatusHistory.Add($"{DateTime.UtcNow:HH:mm:ss.fff}Z  {message}");
        while (_apdStatusHistory.Count > 120) _apdStatusHistory.RemoveAt(0);
    }
    double _apdAlcDb = double.NaN;
    public double ApdAlcDb { get => _apdAlcDb; private set { if (Set(ref _apdAlcDb, value)) OnPropertyChanged(nameof(ApdAlcText)); } }
    public string ApdAlcText => double.IsFinite(ApdAlcDb) ? $"{ApdAlcDb:0.0} dBFS" : "Not reported";

    /// <summary>Request microphone, processor and ALC meters while the APD window is open.</summary>
    public void SetApdMonitoring(bool active)
    {
        if (_apdMonitorActive == active) return;
        _apdMonitorActive = active;
        if (active) EnsureTxControlMonitor(); // subscribe to confirmed RF-power status for calibration identity
        ScheduleMeterRefresh();
    }

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
        : ApdEqualizerActive && ApdMatchesCurrentTx == true ? "ACTIVE · verified for current TX settings"
        : ApdEqualizerActive && ApdMatchesCurrentTx == false ? "ACTIVE · reported for different TX settings"
        : ApdEqualizerActive ? "ACTIVE · TX settings match not yet verified"
        : _apdReportedPhase.Length > 0 ? _apdReportedPhase.ToUpperInvariant()
        : "ENABLED · no active solution reported";

    void OnApdRadioInfo(string model)
    {
        // FLEX-6000 must never be offered APD writes. The status stream is still
        // authoritative if future models advertise APD without a familiar name.
        _apdModel = model;
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
        AppendApdTrace(body);

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
        if (!kv.Keys.Any(k => k is "enable" or "configurable" or "equalizer_active" or "state" or "status"
                          or "ant" or "freq" or "rfpower" or "sample_index" or "rx_error_mHz" or "tx_error_mHz")
            && !resetReported) return;
        _apdReported = true;
        if (!_apdSupported && !_apdModel.StartsWith("FLEX-6", StringComparison.OrdinalIgnoreCase))
            { _apdSupported = true; OnPropertyChanged(nameof(ApdSupported)); }
        if (kv.TryGetValue("enable", out var en)) _apdEnabled = en == "1" || en.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (kv.TryGetValue("configurable", out var conf)) _apdConfigurable = conf == "1" || conf.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (kv.TryGetValue("equalizer_active", out var active)) _apdEqualizerActive = active == "1" || active.Equals("true", StringComparison.OrdinalIgnoreCase);
        if (resetReported) _apdEqualizerActive = false;
        if (kv.TryGetValue("ant", out var ant)) _apdAntenna = ant;
        if (kv.TryGetValue("freq", out var frequency) && Kv.D(frequency) is { } mhz) _apdFreqMhz = mhz;
        if (kv.TryGetValue("rfpower", out var power) && int.TryParse(power, out var pct)) _apdPowerSetting = pct;
        if (kv.TryGetValue("sample_index", out var sample) && int.TryParse(sample, out var index)) _apdSampleIndex = index;
        if (kv.TryGetValue("tx_error_mHz", out var txerror) && Kv.D(txerror) is { } txerr) _apdTxErrorMilliHz = txerr;
        if (kv.TryGetValue("rx_error_mHz", out var rxerror) && Kv.D(rxerror) is { } rxerr) _apdRxErrorMilliHz = rxerr;
        OnPropertyChanged(nameof(ApdCalibrationContext));
        OnPropertyChanged(nameof(ApdFrequencyErrorText));
        OnPropertyChanged(nameof(ApdMatchesCurrentTx));
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
        _apdAntenna = _apdModel = "";
        _apdFreqMhz = null;
        _apdPowerSetting = _apdSampleIndex = null;
        _apdTxErrorMilliHz = _apdRxErrorMilliHz = null;
        _apdTxPowerReported = false;
        ApdStatusHistory.Clear();
        ApdAlcDb = double.NaN;
        OnPropertyChanged(nameof(ApdCalibrationContext));
        OnPropertyChanged(nameof(ApdFrequencyErrorText));
        OnPropertyChanged(nameof(ApdMatchesCurrentTx));
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
