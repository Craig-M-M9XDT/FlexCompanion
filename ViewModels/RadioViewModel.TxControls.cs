using System.Windows;
using System.Windows.Threading;
using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{
    public static readonly string[] ProcModeOptions = ["Normal", "DX", "DX+"];

    DispatcherTimer? _txControlMonitor;
    FlexClient? _txStatusClient;
    int _txRfPower;
    int _txMicLevel;
    int _txProcLevel;
    bool _txProcEnabled;
    bool _txPowerChangesAllowed = true;
    int _txRfPowerMaximum = 100;
    string _txControlStatus = "Waiting for radio transmit status.";
    bool _aetherTxRn2;
    string _aetherTxRn2Status = "Aether automation bridge not checked yet.";
    int _aetherRn2Generation;

    public int TxRfPower
    {
        get { EnsureTxControlMonitor(); return _txRfPower; }
        set
        {
            var next = Math.Clamp(value, 0, Math.Max(1, TxRfPowerMaximum));
            if (!Set(ref _txRfPower, next)) return;
            OnPropertyChanged(nameof(TxRfPowerText));
            if (!TxPowerChangesAllowed)
            {
                LastMessage = "RF power changes are disabled by the radio's current TX settings.";
                return;
            }
            _ = SendTransmitSettingAsync("rfpower", next.ToString());
        }
    }

    public string TxRfPowerText => $"{TxRfPower}%";

    public int TxRfPowerMaximum
    {
        get { EnsureTxControlMonitor(); return _txRfPowerMaximum; }
        private set
        {
            value = Math.Clamp(value, 1, 100);
            if (Set(ref _txRfPowerMaximum, value) && _txRfPower > value)
            {
                _txRfPower = value;
                OnPropertyChanged(nameof(TxRfPower));
                OnPropertyChanged(nameof(TxRfPowerText));
            }
        }
    }

    public bool TxPowerChangesAllowed
    {
        get { EnsureTxControlMonitor(); return _txPowerChangesAllowed; }
        private set => Set(ref _txPowerChangesAllowed, value);
    }

    public int TxMicLevel
    {
        get { EnsureTxControlMonitor(); return _txMicLevel; }
        set
        {
            var next = Math.Clamp(value, 0, 100);
            if (!Set(ref _txMicLevel, next)) return;
            OnPropertyChanged(nameof(TxMicLevelText));
            _ = SendMicLevelAsync(next);
        }
    }

    public string TxMicLevelText => $"{TxMicLevel}%";

    public bool TxProcEnabled
    {
        get { EnsureTxControlMonitor(); return _txProcEnabled; }
        set
        {
            if (!Set(ref _txProcEnabled, value)) return;
            _ = SendTransmitSettingAsync("speech_processor_enable", value ? "1" : "0");
        }
    }

    public string TxProcMode
    {
        get
        {
            EnsureTxControlMonitor();
            return ProcModeOptions[Math.Clamp(_txProcLevel, 0, ProcModeOptions.Length - 1)];
        }
        set
        {
            var next = Array.IndexOf(ProcModeOptions, value);
            if (next < 0) return;
            if (_txProcLevel == next) return;
            _txProcLevel = next;
            OnPropertyChanged();
            _ = SendTransmitSettingAsync("speech_processor_level", next.ToString());
        }
    }

    public string TxControlStatus
    {
        get { EnsureTxControlMonitor(); return _txControlStatus; }
        private set => Set(ref _txControlStatus, value);
    }

    public bool AetherTxRn2
    {
        get { EnsureTxControlMonitor(); return _aetherTxRn2; }
        set
        {
            if (!Set(ref _aetherTxRn2, value)) return;
            var generation = Interlocked.Increment(ref _aetherRn2Generation);
            AetherTxRn2Status = $"Setting Aether TX RN2 {(value ? "on" : "off")}…";
            _ = SetAetherTxRn2Async(value, generation);
        }
    }

    public string AetherTxRn2Status
    {
        get { EnsureTxControlMonitor(); return _aetherTxRn2Status; }
        private set => Set(ref _aetherTxRn2Status, value);
    }

    void EnsureTxControlMonitor()
    {
        if (_txControlMonitor != null) return;
        _txControlMonitor = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _txControlMonitor.Tick += (_, _) => RefreshTxStatusHook();
        _txControlMonitor.Start();
        RefreshTxStatusHook();
    }

    void RefreshTxStatusHook()
    {
        var current = _client;
        if (ReferenceEquals(current, _txStatusClient)) return;

        if (_txStatusClient != null)
            _txStatusClient.Status -= OnTransmitStatusForControls;
        _txStatusClient = current;

        if (current == null)
        {
            TxControlStatus = "Radio disconnected.";
            return;
        }

        current.Status += OnTransmitStatusForControls;
        TxControlStatus = "Reading transmit settings from radio…";
        _ = RequestTxSnapshotAsync(current);
    }

    async Task RequestTxSnapshotAsync(FlexClient client)
    {
        var r = await client.SendAsync("sub tx all");
        if (!ReferenceEquals(client, _client)) return;
        if (r.Code != 0)
            TxControlStatus = $"TX status subscription failed: {FlexClient.ErrorText(r.Code)}";
    }

    void OnTransmitStatusForControls(string body)
    {
        if (!body.StartsWith("transmit ", StringComparison.OrdinalIgnoreCase)) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ApplyTransmitStatusForControls(body));
            return;
        }
        ApplyTransmitStatusForControls(body);
    }

    void ApplyTransmitStatusForControls(string body)
    {
        var kv = Kv.Parse(Kv.Tokenize(body[9..]));
        if (kv.Count == 0) return;

        if (TryInt(kv, "rfpower", out var power))
        {
            power = Math.Clamp(power, 0, 100);
            if (_txRfPower != power)
            {
                _txRfPower = power;
                OnPropertyChanged(nameof(TxRfPower));
                OnPropertyChanged(nameof(TxRfPowerText));
            }
        }
        if (TryInt(kv, "mic_level", out var mic) || TryInt(kv, "miclevel", out mic))
        {
            mic = Math.Clamp(mic, 0, 100);
            if (_txMicLevel != mic)
            {
                _txMicLevel = mic;
                OnPropertyChanged(nameof(TxMicLevel));
                OnPropertyChanged(nameof(TxMicLevelText));
            }
        }
        if (TryInt(kv, "speech_processor_enable", out var procOn))
        {
            var enabled = procOn != 0;
            if (_txProcEnabled != enabled)
            {
                _txProcEnabled = enabled;
                OnPropertyChanged(nameof(TxProcEnabled));
            }
        }
        if (TryInt(kv, "speech_processor_level", out var procLevel))
        {
            procLevel = Math.Clamp(procLevel, 0, ProcModeOptions.Length - 1);
            if (_txProcLevel != procLevel)
            {
                _txProcLevel = procLevel;
                OnPropertyChanged(nameof(TxProcMode));
            }
        }
        if (TryInt(kv, "tx_rf_power_changes_allowed", out var allowed))
            TxPowerChangesAllowed = allowed != 0;
        if (TryInt(kv, "max_power_level", out var maxPower) && maxPower > 0)
            TxRfPowerMaximum = maxPower;

        TxControlStatus = "TX controls are synced to the radio.";
    }

    static bool TryInt(Dictionary<string, string> kv, string key, out int value)
    {
        value = 0;
        return kv.TryGetValue(key, out var text) && int.TryParse(text, out value);
    }

    async Task SendTransmitSettingAsync(string key, string value)
    {
        EnsureTxControlMonitor();
        var c = _client;
        if (c == null) return;
        await TryBindForSelectedSliceAsync();
        var r = await c.SendAsync($"transmit set {key}={value}");
        if (!ReferenceEquals(c, _client)) return;
        if (r.Code != 0)
            LastMessage = $"TX {key}: {FlexClient.ErrorText(r.Code)} [0x{r.Code:X8}]";
    }

    async Task SendMicLevelAsync(int value)
    {
        EnsureTxControlMonitor();
        var c = _client;
        if (c == null) return;
        await TryBindForSelectedSliceAsync();

        // Current API/status spelling is mic_level. Older firmware/client examples
        // have also used miclevel, so retry that spelling only when the first form
        // is explicitly rejected by the radio.
        var r = await c.SendAsync($"transmit set mic_level={value}");
        if (!ReferenceEquals(c, _client)) return;
        if (r.Code != 0)
            r = await c.SendAsync($"transmit set miclevel={value}");
        if (r.Code != 0)
            LastMessage = $"TX mic level: {FlexClient.ErrorText(r.Code)} [0x{r.Code:X8}]";
    }

    async Task SetAetherTxRn2Async(bool enabled, int generation)
    {
        var result = await AetherAutomationClient.SetTxRn2Async(enabled);
        if (generation != Volatile.Read(ref _aetherRn2Generation)) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            await dispatcher.InvokeAsync(() => ApplyAetherRn2Result(enabled, generation, result));
            return;
        }
        ApplyAetherRn2Result(enabled, generation, result);
    }

    void ApplyAetherRn2Result(bool requested, int generation, AetherAutomationClient.Result result)
    {
        if (generation != Volatile.Read(ref _aetherRn2Generation)) return;
        AetherTxRn2Status = result.Message;
        if (result.Ok) return;

        if (_aetherTxRn2 == requested)
        {
            _aetherTxRn2 = !requested;
            OnPropertyChanged(nameof(AetherTxRn2));
        }
    }
}
