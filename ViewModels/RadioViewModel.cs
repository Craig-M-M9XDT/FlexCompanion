using System.Collections.ObjectModel;
using System.Windows.Threading;
using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

/// <summary>Everything for one connected radio ("slot A" or "slot B").</summary>
public sealed partial class RadioViewModel : ObservableObject
{
    FlexClient? _client;
    // Meter packets normally drive presentation directly. This timer is only a slow watchdog
    // for radios/firmware that temporarily stop emitting meter packets.
    readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _fftTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _meterRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly DispatcherTimer _reconnectTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    readonly Dictionary<int, MeterDef> _meterDefs = new();
    readonly Dictionary<string, Dictionary<string, string>> _pans = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _clientStation = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<int> _meterSubscriptions = new();
    readonly SemaphoreSlim _meterSubscriptionGate = new(1, 1);

    int _meterUiPending;
    DateTime _lastMeterPacketUtc = DateTime.MinValue;

    bool _meterMapDirty = true;
    bool _meterFallbackAll;
    bool _lowBandwidthMode;
    bool _interlockTx;
    bool _wantConnected, _retrying;
    string _lastHost = "", _lastTitle = "", _lastSerial = "", _radioSerial = "";
    int _lastPort = 4992;

    // Radio-reported amplifier and license state. License state is informational: controls
    // fail open and the radio remains authoritative when a command is attempted.
    readonly Dictionary<string, (bool Enabled, string Reason)> _licenseFeatures = new(StringComparer.OrdinalIgnoreCase);
    string _amplifierHandle = "", _amplifierModel = "", _amplifierIp = "", _amplifierState = "";
    bool _amplifierOperate;

    public RadioViewModel(string slot)
    {
        Slot = slot;

        NoiseReduction = new()
        {
            SliceParam("NR", "Legacy noise reduction", "nr", "nr", "nr_level", "nr_level"),
            SliceParam("NRF", "Spectral subtraction filtering (FLEX-8000 / Aurora). Sent optimistically; the radio is authoritative.", "nrf", "nrf", "nrf_level", "nrf_level", requiresReport: false),
            SliceParam("NRL", "LMS noise reduction. Sent optimistically; the radio is authoritative.", "lms_nr", "nrl", "lms_nr_level", "lms_nr_level", requiresReport: false),
            SliceParam("NRS", "Spectral subtraction with voice detection (FLEX-8000 / Aurora). Sent optimistically; the radio is authoritative.", "speex_nr", "nrs", "speex_nr_level", "speex_nr_level", requiresReport: false),
            SliceParam("RNN", "AI noise reduction (FLEX-8000 / Aurora). The radio has no level for RNN.", "rnnoise", "rnn", null, null, requiresReport: false),
        };
        Blanker = new()
        {
            SliceParam("NB", "Noise blanker", "nb", "nb", "nb_level", "nb_level"),
        };
        Notch = new()
        {
            SliceParam("ANF", "Legacy automatic notch filter", "anf", "anf", "anf_level", "anf_level"),
            SliceParam("ANFL", "LMS automatic notch filter. Sent optimistically; the radio is authoritative.", "lms_anf", "anfl", "lms_anf_level", "lms_anf_level", requiresReport: false),
            SliceParam("ANFT", "FFT automatic notch filter. The radio has no level for ANFT.", "anft", "anft", null, null, requiresReport: false),
        };
        Display = new()
        {
            new ParamControl
            {
                Label = "FLOOR", Scope = "pan", RequiresReport = false, DefaultLevel = 75,
                Tooltip = "Relative noise floor scaling for this slice's panadapter. Sent optimistically; the radio is authoritative.",
                ToggleSetKey = "noise_floor_position_enable", ToggleStatusKey = "noise_floor_position_enable",
                LevelSetKey = "noise_floor_position", LevelStatusKey = "noise_floor_position",
            },
        };
        Esc = new()
        {
            new ParamControl
            {
                Label = "DIV", Tooltip = "Diversity: adds a child slice on the second SCU (dual-SCU radios only)",
                ToggleSetKey = "diversity", ToggleStatusKey = "diversity",
            },
            new ParamControl
            {
                Label = "ESC", Tooltip = "Enhanced Signal Clarity beam steering. Slider = phase in degrees. Sent optimistically; the radio is authoritative.",
                RequiresReport = false,
                ToggleSetKey = "esc", ToggleStatusKey = "esc", ToggleFormat = b => b ? "on" : "off",
                LevelSetKey = "esc_phase_shift", LevelStatusKey = "esc_phase_shift",
                Min = 0, Max = 360, Step = 5, DefaultLevel = 0,
                LevelFormat = deg => Kv.F(deg * Math.PI / 180.0, "0.000000"),
                LevelParse = s => Kv.D(s) is double rad ? rad * 180.0 / Math.PI : (double?)null,
                LevelDisplay = deg => $"{deg:0}°",
            },
            new ParamControl
            {
                Label = "GAIN", Tooltip = "ESC antenna balance. 1.00 = equal, below favours the main antenna",
                LevelSetKey = "esc_gain", LevelStatusKey = "esc_gain",
                Min = 0, Max = 2, Step = 0.05, DefaultLevel = 1,
                LevelFormat = v => Kv.F(v, "0.000"),
                LevelDisplay = v => v.ToString("0.00"),
            },
        };

        foreach (var c in AllControls)
        {
            c.Sender = SendScoped;
            c.Reset();
        }
        Stations.Add(StationItem.All);
        _selectedStation = StationItem.All;

        _meterTimer.Tick += (_, _) =>
        {
            if ((DateTime.UtcNow - _lastMeterPacketUtc).TotalMilliseconds >= 200)
                OnMeterTick();
        };
        _fftTimer.Tick += (_, _) => OnFftTick();
        _meterRefreshTimer.Tick += async (_, _) =>
        {
            _meterRefreshTimer.Stop();
            await RefreshMeterSubscriptionsAsync();
        };
        _reconnectTimer.Tick += async (_, _) =>
        {
            _reconnectTimer.Stop();
            if (_wantConnected && !IsConnected && !IsConnecting)
                await ConnectCoreAsync(_lastHost, _lastPort, _lastTitle, _lastSerial);
        };

        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected || _retrying);
        ReconnectCommand = new RelayCommand(async () => await ConnectAsync(_lastHost, _lastPort, _lastTitle, _lastSerial),
                                            () => _lastHost.Length > 0 && !IsConnecting);
        AutoTuneOnceCommand = new RelayCommand(() => { if (SelectedSlice is { } s) _ = SendAsStationAsync($"slice auto_tune {s.Index}"); },
                                               () => IsConnected && SelectedSlice != null);
    }

    ParamControl SliceParam(string label, string tip, string tSet, string tStatus, string? lSet, string? lStatus, bool requiresReport = true) => new()
    {
        Label = label, Tooltip = tip, RequiresReport = requiresReport,
        ToggleSetKey = tSet, ToggleStatusKey = tStatus,
        LevelSetKey = lSet, LevelStatusKey = lStatus,
    };

    // ───────────────────────── bindable state ─────────────────────────

    public string Slot { get; }
    public string IdleHint => $"Slot {Slot} is free.\nPick a radio on the left (or type its IP) and press \"Connect to {Slot}\".";

    public ObservableCollection<ParamControl> NoiseReduction { get; }
    public ObservableCollection<ParamControl> Blanker { get; }
    public ObservableCollection<ParamControl> Notch { get; }
    public ObservableCollection<ParamControl> Display { get; }
    public ObservableCollection<ParamControl> Esc { get; }
    IEnumerable<ParamControl> AllControls => NoiseReduction.Concat(Blanker).Concat(Notch).Concat(Display).Concat(Esc);

    public ObservableCollection<SliceItem> Slices { get; } = new();
    public ObservableCollection<StationItem> Stations { get; } = new();

    public RelayCommand DisconnectCommand { get; }
    public RelayCommand ReconnectCommand { get; }
    public RelayCommand AutoTuneOnceCommand { get; }

    bool _isConnected, _isConnecting, _followActive = true, _autoTuneInt, _isTx;
    string _status = "Not connected", _title = "No radio", _lastMessage = "";
    SliceItem? _selectedSlice;
    StationItem? _selectedStation;
    double _rxDbm = double.NaN, _fwdW, _swr = 1, _micDb = -60, _compDb = 0, _powerMax = 120;
    string _rxS = "-", _rxDbmText = "", _powerText = "0 W", _swrText = "1.0:1", _micText = "", _compText = "";

    public bool IsConnected
    {
        get => _isConnected;
        private set { if (Set(ref _isConnected, value)) OnPropertyChanged(nameof(IsDisconnected)); }
    }
    public bool IsDisconnected => !_isConnected;
    public bool IsConnecting { get => _isConnecting; private set => Set(ref _isConnecting, value); }
    public bool LowBandwidthMode
    {
        get => _lowBandwidthMode;
        set
        {
            if (!Set(ref _lowBandwidthMode, value)) return;
            // Meter presentation is source-paced by incoming VITA meter packets, so Network
            // Saver no longer changes its cadence. Only the Companion-owned FFT fallback is
            // rate-limited; Aether shared-pan frames remain source-paced too.
            _fftTimer.Interval = value ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromMilliseconds(33);
            OnBandwidthModeChanged();
        }
    }
    public string StatusText { get => _status; private set => Set(ref _status, value); }
    public string RadioTitle { get => _title; private set => Set(ref _title, value); }
    public string LastMessage { get => _lastMessage; private set => Set(ref _lastMessage, value); }

    public bool FollowActiveSlice
    {
        get => _followActive;
        set
        {
            if (!Set(ref _followActive, value) || !value) return;
            var act = Slices.FirstOrDefault(s => s.Active && StationMatches(s));
            if (act != null) SelectedSlice = act;
        }
    }

    public SliceItem? SelectedSlice
    {
        get => _selectedSlice;
        set
        {
            if (!Set(ref _selectedSlice, value)) return;
            _meterMapDirty = true;
            RxDbm = double.NaN;
            ReapplyAll();
            ApplyModeAvailability();
            OnPropertyChanged(nameof(SliceModeText));
            OnPropertyChanged(nameof(EscNote));
            ScheduleMeterRefresh();
            OnSliceChangedExtras();
        }
    }

    public StationItem? SelectedStation
    {
        get => _selectedStation;
        set
        {
            if (!Set(ref _selectedStation, value) || value == null) return;
            // Binding makes station-scoped commands such as CW auto tune act for that GUI client.
            if (value.ClientId.Length > 0) _ = EnsureBoundAsync();
            if (FollowActiveSlice)
            {
                var act = Slices.FirstOrDefault(s => s.Active && StationMatches(s));
                if (act != null) SelectedSlice = act;
            }
        }
    }

    public string SliceModeText => SelectedSlice == null ? "No slice selected" : $"Slice {SelectedSlice.Letter} is in {SelectedSlice.Mode}";

    public string EscNote => SelectedSlice?.IsDiversityChild == true
        ? "This is a diversity child slice. Select the parent slice to adjust ESC."
        : "ESC needs a dual-SCU radio and DIV on. Companion does not pre-gate on a SmartSDR+ label; the radio decides whether the command is accepted.";

    public string AmplifierHandle { get => _amplifierHandle; private set => Set(ref _amplifierHandle, value); }
    public string AmplifierModel { get => _amplifierModel; private set => Set(ref _amplifierModel, value); }
    public string AmplifierIp { get => _amplifierIp; private set => Set(ref _amplifierIp, value); }
    public string AmplifierState { get => _amplifierState; private set => Set(ref _amplifierState, value); }
    public bool AmplifierOperate { get => _amplifierOperate; private set => Set(ref _amplifierOperate, value); }
    public bool HasAmplifier => AmplifierHandle.Length > 0;
    public string LicenseSummary => _licenseFeatures.Count == 0
        ? "No feature-entitlement status received — controls fail open; the radio remains authoritative."
        : string.Join("  •  ", _licenseFeatures.OrderBy(x => x.Key).Select(x => $"{x.Key}:{(x.Value.Enabled ? "on" : "off")}"));

    public bool AutoTuneIntermittent
    {
        get => _autoTuneInt;
        set
        {
            if (!Set(ref _autoTuneInt, value)) return;
            if (SelectedSlice is { } s) _ = SendAsStationAsync($"slice auto_tune {s.Index} int={(value ? 1 : 0)}");
        }
    }


    public double RxDbm { get => _rxDbm; private set => Set(ref _rxDbm, value); }
    public string RxSText { get => _rxS; private set => Set(ref _rxS, value); }
    public string RxDbmText { get => _rxDbmText; private set => Set(ref _rxDbmText, value); }
    public double FwdWatts { get => _fwdW; private set => Set(ref _fwdW, value); }
    public string PowerText { get => _powerText; private set => Set(ref _powerText, value); }
    public double PowerMax { get => _powerMax; private set => Set(ref _powerMax, value); }
    public double Swr { get => _swr; private set => Set(ref _swr, value); }
    public string SwrText { get => _swrText; private set => Set(ref _swrText, value); }
    public double MicDb { get => _micDb; private set => Set(ref _micDb, value); }
    public string MicText { get => _micText; private set => Set(ref _micText, value); }
    public double CompDb { get => _compDb; private set => Set(ref _compDb, value); }
    public string CompText { get => _compText; private set => Set(ref _compText, value); }
    public bool IsTransmitting { get => _isTx; private set => Set(ref _isTx, value); }

    // ───────────────────────── connection ─────────────────────────

    public Task ConnectAsync(string host, int port, string title, string serial = "")
    {
        _retrying = false;
        _reconnectTimer.Stop();
        _lastSerial = serial ?? "";
        return ConnectCoreAsync(host, port, title, _lastSerial);
    }

    async Task ConnectCoreAsync(string host, int port, string title, string serial)
    {
        Cleanup();
        _wantConnected = true;
        _lastHost = host; _lastPort = port; _lastTitle = title; _lastSerial = serial ?? ""; _radioSerial = _lastSerial.Trim().Trim('"');
        IsConnecting = true;
        StatusText = $"Connecting to {host}:{port}";
        RadioTitle = string.IsNullOrWhiteSpace(title) ? host : title;
        LastMessage = "";

        var c = new FlexClient();
        c.Status += OnStatus;
        c.Message += OnRadioMessage;
        c.Disconnected += OnClientDisconnected;
        c.Audio += OnAudioPacket;
        c.Iq += OnIqPacket;
        c.MeterPacket += OnMeterPacket;
        try
        {
            await c.ConnectAsync(host, port);
            _client = c;
            IsConnected = true;
            _retrying = false;
            AetherPanBridge.Instance.FrameReceived += OnAetherPanFrame;

            await c.SendAsync("client program FlexCompanion");
            var info = await c.SendAsync("info");
            if (info.Code == 0) ApplyInfo(info.Message);

            var udp = await c.SendAsync($"client udpport {c.UdpPort}");
            if (udp.Code != 0)
                LastMessage = $"Meter stream registration failed ({FlexClient.ErrorText(udp.Code)}). Controls still work.";

            foreach (var sub in new[] { "sub client all", "sub slice all", "sub pan all", "sub tx all", "sub amplifier all", "sub license all" })
                await c.SendAsync(sub);

            // Do not subscribe to every meter. A typical FLEX session exposes dozens of meters,
            // most of which Companion never renders. Build metadata with `meter list` and subscribe
            // only to the selected slice LEVEL, PWR/SWR and the currently selected extra TX meter.
            await RefreshMeterSubscriptionsAsync();

            StatusText = $"Connected to {host}   handle {c.Handle}";
            _meterTimer.Start();
            _fftTimer.Start();
            OnConnectedExtras();
        }
        catch (Exception ex)
        {
            c.Status -= OnStatus;
            c.Message -= OnRadioMessage;
            c.Disconnected -= OnClientDisconnected;
            c.Audio -= OnAudioPacket;
            c.Iq -= OnIqPacket;
            c.MeterPacket -= OnMeterPacket;
            AetherPanBridge.Instance.FrameReceived -= OnAetherPanFrame;
            c.Dispose();
            _client = null;
            IsConnected = false;
            StatusText = ex is OperationCanceledException or TimeoutException
                ? $"No answer from {host}:{port}"
                : $"Connect failed: {ex.Message}";
            if (_retrying)
            {
                StatusText += "   retrying in 5 s";
                _reconnectTimer.Start();
            }
            else _wantConnected = false;
        }
        finally
        {
            IsConnecting = false;
            CommandManagerRefresh();
        }
    }

    public void Disconnect()
    {
        _wantConnected = false;
        _retrying = false;
        _reconnectTimer.Stop();
        Cleanup();
        StatusText = "Not connected";
        CommandManagerRefresh();
    }

    void OnClientDisconnected(string reason)
    {
        if (_client == null) return;
        Cleanup();
        if (_wantConnected)
        {
            _retrying = true;
            StatusText = $"Lost connection ({reason})   retrying in 5 s";
            _reconnectTimer.Start();
        }
        else StatusText = $"Disconnected ({reason})";
        CommandManagerRefresh();
    }

    void OnRadioMessage(string m) => LastMessage = m;

    void Cleanup()
    {
        _meterTimer.Stop();
        _fftTimer.Stop();
        _meterRefreshTimer.Stop();
        OnCleanupExtras();
        var c = _client;
        _client = null;
        if (c != null)
        {
            c.Status -= OnStatus;
            c.Message -= OnRadioMessage;
            c.Disconnected -= OnClientDisconnected;
            c.Audio -= OnAudioPacket;
            c.Iq -= OnIqPacket;
            c.MeterPacket -= OnMeterPacket;
            AetherPanBridge.Instance.FrameReceived -= OnAetherPanFrame;
            c.Dispose();
        }
        IsConnected = false;
        SelectedSlice = null;
        Slices.Clear();
        Stations.Clear();
        Stations.Add(StationItem.All);
        _selectedStation = StationItem.All;
        _boundClientId = "";
        OnPropertyChanged(nameof(SelectedStation));
        _clientStation.Clear();
        _meterDefs.Clear();
        _meterSubscriptions.Clear();
        _meterFallbackAll = false;
        _pans.Clear();
        _meterMapDirty = true;
        _lastMeterPacketUtc = DateTime.MinValue;
        Interlocked.Exchange(ref _meterUiPending, 0);
        _interlockTx = false;
        _licenseFeatures.Clear();
        AmplifierHandle = AmplifierModel = AmplifierIp = AmplifierState = "";
        AmplifierOperate = false;
        OnPropertyChanged(nameof(LicenseSummary));
        IsTransmitting = false;
        RxDbm = double.NaN; RxSText = "-"; RxDbmText = "";
        FwdWatts = 0; PowerText = "0 W"; Swr = 1; SwrText = "1.0:1";
        MicDb = -60; MicText = ""; CompDb = 0; CompText = "";
        foreach (var p in AllControls) { p.Reset(); p.ClearUnsupported(); }
        _autoTuneInt = false; OnPropertyChanged(nameof(AutoTuneIntermittent));
    }

    static void CommandManagerRefresh() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    void ApplyInfo(string msg)
    {
        var kv = Kv.Parse(Kv.Tokenize(msg.Replace(',', ' ')));
        var model = kv.GetValueOrDefault("model") ?? "";
        var name = kv.GetValueOrDefault("nickname") ?? kv.GetValueOrDefault("name") ?? "";
        var serial = kv.GetValueOrDefault("chassis_serial") ?? kv.GetValueOrDefault("serial") ?? "";
        serial = serial.Trim().Trim('"');
        if (serial.Length > 0) _radioSerial = serial;
        if (model.Length > 0)
            RadioTitle = name.Length > 0 && name != model ? $"{name}  ({model})" : model;
        PowerMax = model.StartsWith("AU", StringComparison.OrdinalIgnoreCase) ? 600 : 120;
    }

    // ───────────────────────── sending ─────────────────────────

    public async void Send(string cmd)
    {
        var c = _client;
        if (c == null) return;
        var (code, _) = await c.SendAsync(cmd);
        if (code != 0) LastMessage = $"{cmd}   {FlexClient.ErrorText(code)}";
    }

    const uint UnknownParameter = 0x5000002D;
    const uint CommandRefused = 0x50004001;
    const uint InvalidModeOrState = 0xE2000000;
    const uint InvalidDspForMode = 0x50000061;
    const uint InvalidCommandForMode = 0x50000085;

    static bool IsDigitalDspRestrictedMode(string mode)
    {
        mode = mode.Trim().ToUpperInvariant();
        return mode is "DIGU" or "DIGL" or "RTTY" or "FDV" or "FDVL" or "FDVU";
    }

    static bool IsCwMode(string mode)
    {
        mode = mode.Trim().ToUpperInvariant();
        return mode is "CW" or "CWL" or "CWR";
    }

    static string? LicenseFeatureForControl(string label) => label.ToUpperInvariant() switch
    {
        "NRF" or "NRL" or "NRS" or "RNN" or "ANFL" or "ANFT" => "NOISE_REDUCTION",
        _ => null,
    };

    string? ExplicitLicenseReason(ParamControl? ctl)
    {
        if (ctl == null) return null;
        var feature = LicenseFeatureForControl(ctl.Label);
        if (feature == null || !_licenseFeatures.TryGetValue(feature, out var state) || state.Enabled) return null;
        var source = string.IsNullOrWhiteSpace(state.Reason) ? "radio feature status" : state.Reason;
        return $"radio reports {feature} disabled ({source})";
    }

    void ApplyModeAvailability(bool announce = false)
    {
        // Temporary mode gating is deliberately separate from permanent capability detection.
        // AetherSDR follows the same UI rule: ANF/ANFL/ANFT are hidden in DIG/RTTY/FDV;
        // CW also excludes those notch filters and RNN. Returning to a compatible mode restores them.
        foreach (var ctl in AllControls) ctl.SetTemporaryUnavailable(false);

        var mode = SelectedSlice?.Mode.Trim().ToUpperInvariant() ?? "";
        if (mode.Length == 0) return;

        string[] blocked = IsDigitalDspRestrictedMode(mode)
            ? ["ANF", "ANFL", "ANFT"]
            : IsCwMode(mode) ? ["ANF", "RNN", "ANFL", "ANFT"] : [];

        foreach (var label in blocked)
        {
            var ctl = AllControls.FirstOrDefault(x => x.Label.Equals(label, StringComparison.OrdinalIgnoreCase));
            ctl?.SetTemporaryUnavailable(true, $"{label} is unavailable in {mode} mode");
        }

        if (announce && blocked.Length > 0)
            LastMessage = $"{string.Join(" / ", blocked)} unavailable in {mode} mode. They will be restored automatically when you return to a compatible mode.";
        else if (announce && blocked.Length == 0
                 && LastMessage.Contains("unavailable in", StringComparison.OrdinalIgnoreCase)
                 && LastMessage.Contains("mode", StringComparison.OrdinalIgnoreCase))
            LastMessage = $"DSP mode restriction cleared — filters are available again in {mode}.";
    }

    string DescribeControlError(ParamControl? ctl, string key, uint code)
    {
        var label = ctl?.Label ?? key.ToUpperInvariant();
        var mode = SelectedSlice?.Mode.Trim().ToUpperInvariant() ?? "";
        var hex = $"0x{code:X8}";
        var licence = ExplicitLicenseReason(ctl);

        if (code == UnknownParameter)
            return licence != null
                ? $"{label} unavailable — {licence} [{hex}]"
                : $"{label} unavailable — this radio / firmware does not report support for {key} [{hex}]";

        if (code == CommandRefused || code == InvalidModeOrState || code == InvalidDspForMode || code == InvalidCommandForMode)
        {
            if (ctl?.IsTemporarilyUnavailable == true && ctl.TemporaryReason.Length > 0)
                return $"{ctl.TemporaryReason} [{hex}]";
            if (licence != null)
                return $"{label} unavailable — {licence} [{hex}]";
            if (mode.Length > 0)
                return $"{label} unavailable in {mode} mode or the radio's current state [{hex}]";
            return $"{label} unavailable in the radio's current state [{hex}]";
        }

        return $"{label}: {FlexClient.ErrorText(code)} [{hex}]";
    }

    async void SendScoped(string scope, string kv)
    {
        var s = SelectedSlice;
        var c = _client;
        if (s == null || c == null) return;
        string cmd;
        if (scope == "pan")
        {
            if (s.Pan.Length == 0) return;
            cmd = $"display pan set {s.Pan} {kv}";
        }
        else cmd = $"slice set {s.Index} {kv}";

        // Normal DSP writes are legal from a non-GUI client, but when the slice belongs
        // to a GUI station we bind first so SmartSDR/Aether and this companion share the
        // same MultiFLEX context. Failure to bind does not block ordinary slice commands.
        await TryBindForSelectedSliceAsync();
        var key = kv.Split('=')[0];
        var ctl = AllControls.FirstOrDefault(x => x.Scope == scope && x.Uses(key));
        if (ctl?.IsTemporarilyUnavailable == true)
        {
            LastMessage = ctl.TemporaryReason.Length > 0
                ? ctl.TemporaryReason
                : $"{ctl.Label} is temporarily unavailable in the current mode.";
            return;
        }

        var (code, _) = await c.SendAsync(cmd);
        if (code == 0) return;
        if (code == UnknownParameter && ctl != null)
        {
            // Unknown parameter is a genuine capability result and may be cached for this connection.
            // Context/state errors below must never poison capability state.
            ctl.MarkUnsupported();
        }
        LastMessage = DescribeControlError(ctl, key, code);
    }

    void ReapplyAll()
    {
        foreach (var c in AllControls) c.Reset();
        var s = SelectedSlice;
        if (s == null) return;
        foreach (var c in AllControls.Where(x => x.Scope == "slice")) c.ApplyStatus(s.State);
        if (_pans.TryGetValue(s.Pan, out var pan))
            foreach (var c in AllControls.Where(x => x.Scope == "pan")) c.ApplyStatus(pan);
        _autoTuneInt = false;
        OnPropertyChanged(nameof(AutoTuneIntermittent));
    }

    // ───────────────────────── status parsing ─────────────────────────


    /// <summary>Execute a raw FLEX command against this radio and return the radio response.</summary>
    public async Task<(uint Code, string Message)> ExecuteAsync(string cmd, bool bindToSelectedStation = false)
    {
        var c = _client;
        if (c == null) return (0xFFFFFFFF, "not connected");
        if (bindToSelectedStation) await TryBindForSelectedSliceAsync();
        var result = await c.SendAsync(cmd);
        if (result.Code != 0) LastMessage = $"{cmd}   {FlexClient.ErrorText(result.Code)}";
        return result;
    }

    public Task<(uint Code, string Message)> TuneAsync(double mhz)
        => SelectedSlice == null ? Task.FromResult((0xFFFFFFFFu, "no slice selected"))
                                 // No autopan=0: band jumps and spot clicks must let the panadapter follow the slice.
                                 : ExecuteAsync($"slice tune {SelectedSlice.Index} {mhz.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}", true);

    public Task<(uint Code, string Message)> SetModeAsync(string mode)
        => SelectedSlice == null ? Task.FromResult((0xFFFFFFFFu, "no slice selected"))
                                 : ExecuteAsync($"slice set {SelectedSlice.Index} mode={mode}", true);

    public Task<(uint Code, string Message)> SetAmplifierOperateAsync(bool on)
        => AmplifierHandle.Length == 0 ? Task.FromResult((0xFFFFFFFFu, "no amplifier reported by radio"))
                                       : ExecuteAsync($"amplifier set {AmplifierHandle} operate={(on ? 1 : 0)}");

    async Task<bool> TryBindForSelectedSliceAsync()
    {
        var c = _client;
        if (c == null) return false;
        var st = StationForCommands();
        if (st == null || st.ClientId.Length == 0) return false;
        if (st.ClientId == _boundClientId) return true;
        var (code, _) = await c.SendAsync($"client bind client_id={st.ClientId}");
        if (code != 0) return false;
        _boundClientId = st.ClientId;
        return true;
    }

    void OnStatus(string body)
    {
        if (body.StartsWith("meter ", StringComparison.Ordinal)) { HandleMeterStatus(body[6..]); return; }

        var tok = Kv.Tokenize(body);
        if (tok.Count == 0) return;
        switch (tok[0])
        {
            case "slice": HandleSlice(tok); break;
            case "display" when tok.Count > 2 && tok[1] == "pan": HandlePan(tok); break;
            case "client" when tok.Count > 1: HandleClient(tok); break;
            case "interlock": HandleInterlock(tok); break;
            case "amplifier": HandleAmplifier(tok); break;
            case "license": HandleLicense(tok); break;
        }
    }

    bool StationMatches(SliceItem s) =>
        SelectedStation == null || SelectedStation.Handle.Length == 0 ||
        string.Equals(s.ClientHandle, SelectedStation.Handle, StringComparison.OrdinalIgnoreCase);

    void HandleSlice(List<string> tok)
    {
        if (tok.Count < 2 || !int.TryParse(tok[1], out var idx)) return;
        var kv = Kv.Parse(tok.Skip(2));
        var s = Slices.FirstOrDefault(x => x.Index == idx);

        bool removed = (kv.TryGetValue("in_use", out var iu) && iu == "0") || tok.Contains("removed");
        if (removed)
        {
            if (s == null) return;
            bool wasSelected = s == SelectedSlice;
            Slices.Remove(s);
            if (wasSelected) SelectedSlice = Slices.FirstOrDefault(x => x.Active && StationMatches(x)) ?? Slices.FirstOrDefault();
            return;
        }

        if (s == null)
        {
            s = new SliceItem(idx);
            int pos = 0;
            while (pos < Slices.Count && Slices[pos].Index < idx) pos++;
            Slices.Insert(pos, s);
        }
        s.Merge(kv);
        if (_clientStation.TryGetValue(s.ClientHandle, out var st)) s.Station = st;

        if (FollowActiveSlice && kv.TryGetValue("active", out var a) && a == "1" && StationMatches(s) && s != SelectedSlice)
        {
            SelectedSlice = s;   // re-applies everything
            return;
        }
        if (SelectedSlice == null) { SelectedSlice = s; return; }

        if (s == SelectedSlice)
        {
            foreach (var c in AllControls.Where(x => x.Scope == "slice")) c.ApplyStatus(kv);
            if (kv.ContainsKey("pan") && _pans.TryGetValue(s.Pan, out var pan))
                foreach (var c in AllControls.Where(x => x.Scope == "pan")) c.ApplyStatus(pan);
            if (kv.ContainsKey("mode"))
            {
                OnPropertyChanged(nameof(SliceModeText));
                ApplyModeAvailability(announce: true);
            }
            if (kv.ContainsKey("mode") || kv.ContainsKey("pan") || kv.ContainsKey("RF_frequency"))
                ScheduleMeterRefresh();
            if (kv.ContainsKey("diversity_child")) OnPropertyChanged(nameof(EscNote));
            OnSelectedSliceStatus(kv);
        }
    }

    void HandlePan(List<string> tok)
    {
        var id = tok[2];
        if (tok.Contains("removed")) { _pans.Remove(id); return; }
        var kv = Kv.Parse(tok.Skip(3));
        if (!_pans.TryGetValue(id, out var state)) _pans[id] = state = new(StringComparer.OrdinalIgnoreCase);
        foreach (var p in kv) state[p.Key] = p.Value;
        if (SelectedSlice != null && string.Equals(SelectedSlice.Pan, id, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var c in AllControls.Where(x => x.Scope == "pan")) c.ApplyStatus(kv);
            OnSelectedPanStatus(kv);
        }
    }

    void HandleClient(List<string> tok)
    {
        var handle = tok[1];
        var kv = Kv.Parse(tok.Skip(2));

        if (tok.Contains("disconnected"))
        {
            _clientStation.Remove(handle);
            var gone = Stations.FirstOrDefault(x => x.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
            if (gone != null)
            {
                if (gone == SelectedStation) SelectedStation = StationItem.All;
                if (gone.ClientId == _boundClientId) _boundClientId = "";
                Stations.Remove(gone);
            }
            return;
        }

        var station = kv.GetValueOrDefault("station");
        var program = kv.GetValueOrDefault("program");
        if (station == null && program == null) return;
        var label = station ?? program!;
        _clientStation[handle] = label;
        foreach (var s in Slices.Where(x => x.ClientHandle.Equals(handle, StringComparison.OrdinalIgnoreCase)))
            s.Station = label;

        // Only GUI clients (SmartSDR, AetherSDR, Maestro...) carry a client_id and can be bound to.
        var clientId = kv.GetValueOrDefault("client_id");
        if (string.IsNullOrEmpty(clientId)) return;
        var name = program != null && station != null ? $"{station}  ({program})" : label;
        var existing = Stations.FirstOrDefault(x => x.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
        if (existing != null && existing.Name == name) return;
        var item = new StationItem { Handle = handle, ClientId = clientId, Name = name };
        if (existing != null)
        {
            bool sel = existing == SelectedStation;
            Stations[Stations.IndexOf(existing)] = item;
            if (sel) { _selectedStation = item; OnPropertyChanged(nameof(SelectedStation)); }
        }
        else Stations.Add(item);
    }

    void HandleAmplifier(List<string> tok)
    {
        if (tok.Count < 2) return;
        var handle = tok[1];
        if (tok.Contains("removed"))
        {
            if (SameHandle(handle, AmplifierHandle))
            {
                AmplifierHandle = AmplifierModel = AmplifierIp = AmplifierState = "";
                AmplifierOperate = false;
                OnPropertyChanged(nameof(HasAmplifier));
            }
            return;
        }
        var kv = Kv.Parse(tok.Skip(2));
        var model = kv.GetValueOrDefault("model") ?? "";
        if (model.Equals("TunerGeniusXL", StringComparison.OrdinalIgnoreCase)) return;
        if (handle.Equals("0x00000000", StringComparison.OrdinalIgnoreCase)) handle = "";
        if (handle.Length > 0) AmplifierHandle = handle;
        if (model.Length > 0) AmplifierModel = model;
        if (kv.TryGetValue("ip", out var ip)) AmplifierIp = ip;
        if (kv.TryGetValue("state", out var state) && state.Length > 0)
        {
            AmplifierState = state;
            AmplifierOperate = !state.Equals("STANDBY", StringComparison.OrdinalIgnoreCase);
        }
        OnPropertyChanged(nameof(HasAmplifier));
    }

    void HandleLicense(List<string> tok)
    {
        // Aether policy: do not infer a block from a subscription label alone.
        // Store explicit feature reports for information only; commands are still
        // attempted and the radio's response is authoritative.
        if (tok.Count < 2 || !tok[1].Equals("feature", StringComparison.OrdinalIgnoreCase)) return;
        var kv = Kv.Parse(tok.Skip(2));
        var name = kv.GetValueOrDefault("name") ?? "";
        if (name.Length == 0) return;
        bool enabled = (kv.GetValueOrDefault("enabled") ?? "0") == "1";
        _licenseFeatures[name] = (enabled, kv.GetValueOrDefault("reason") ?? "");
        OnPropertyChanged(nameof(LicenseSummary));
    }

    void HandleInterlock(List<string> tok)
    {
        var kv = Kv.Parse(tok.Skip(1));
        if (kv.TryGetValue("state", out var st))
            _interlockTx = st.Equals("TRANSMITTING", StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────── acting for a GUI station ─────────────────────
    // Some station-scoped controls, such as CW auto tune, belong to a GUI client
    // (SmartSDR / AetherSDR / Maestro). A non-GUI API client like this one must
    // bind to that station first. Native radio DVK is intentionally not exposed:
    // current radios may refuse DVK commands from a bound non-GUI companion client.

    string _boundClientId = "";

    StationItem? StationForCommands()
    {
        if (SelectedStation is { ClientId.Length: > 0 } chosen) return chosen;
        var handle = SelectedSlice?.ClientHandle ?? "";
        if (handle.Length > 0)
        {
            var owner = Stations.FirstOrDefault(x => x.ClientId.Length > 0 && SameHandle(x.Handle, handle));
            if (owner != null) return owner;
        }
        var gui = Stations.Where(x => x.ClientId.Length > 0).ToList();
        return gui.Count == 1 ? gui[0] : null;
    }

    static bool SameHandle(string a, string b)
    {
        static string N(string h) => (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? h[2..] : h).TrimStart('0').ToUpperInvariant();
        return N(a) == N(b);
    }

    async Task<bool> EnsureBoundAsync()
    {
        var c = _client;
        if (c == null) return false;
        var st = StationForCommands();
        if (st == null)
        {
            LastMessage = "No SmartSDR / AetherSDR station found to act for. Open the GUI on this radio, or pick it in the Station box.";
            return false;
        }
        if (st.ClientId == _boundClientId) return true;
        var (code, _) = await c.SendAsync($"client bind client_id={st.ClientId}");
        if (code != 0)
        {
            LastMessage = $"Could not attach to station {st.Name}: {FlexClient.ErrorText(code)}";
            return false;
        }
        _boundClientId = st.ClientId;
        return true;
    }

    async Task SendAsStationAsync(string cmd)
    {
        var c = _client;
        if (c == null || !await EnsureBoundAsync()) return;
        var (code, _) = await c.SendAsync(cmd);
        if (code != 0) LastMessage = $"{cmd}   {FlexClient.ErrorText(code)}";
    }

    // ───────────────────────── meters ─────────────────────────

    void ScheduleMeterRefresh()
    {
        if (_client == null) return;
        _meterRefreshTimer.Stop();
        _meterRefreshTimer.Start();
    }

    async Task RefreshMeterSubscriptionsAsync()
    {
        var c = _client;
        if (c == null) return;
        if (!await _meterSubscriptionGate.WaitAsync(0))
        {
            ScheduleMeterRefresh();
            return;
        }
        try
        {
            c = _client;
            if (c == null) return;

            var list = await c.SendAsync("meter list");
            if (_client != c) return;
            if (list.Code != 0 || string.IsNullOrWhiteSpace(list.Message))
            {
                await EnableMeterFallbackAsync(c);
                return;
            }

            var body = list.Message.Trim();
            if (body.StartsWith("meter ", StringComparison.OrdinalIgnoreCase)) body = body[6..];
            _meterDefs.Clear();
            ParseMeterMetadata(body);
            if (_meterDefs.Count == 0)
            {
                await EnableMeterFallbackAsync(c);
                return;
            }

            _meterMapDirty = true;
            RebuildMeterMap();
            var desired = DesiredMeterIds();

            // If we previously had to fall back to all meters, switch back to selective mode now.
            if (_meterFallbackAll)
            {
                await c.SendAsync("unsub meter all");
                _meterFallbackAll = false;
                _meterSubscriptions.Clear();
            }

            foreach (var id in _meterSubscriptions.Where(x => !desired.Contains(x)).ToArray())
            {
                await c.SendAsync($"unsub meter {id}");
                _meterSubscriptions.Remove(id);
            }
            foreach (var id in desired.Where(x => !_meterSubscriptions.Contains(x)))
            {
                var r = await c.SendAsync($"sub meter {id}");
                if (r.Code == 0) _meterSubscriptions.Add(id);
            }
        }
        finally
        {
            _meterSubscriptionGate.Release();
        }
    }

    HashSet<int> DesiredMeterIds()
    {
        var ids = new HashSet<int>();
        void Add(int id) { if (id >= 0) ids.Add(id); }

        // Always needed by the main meter.
        Add(_rxId);
        Add(_fwdId);
        Add(_swrId);

        void AddOptional(string key)
        {
            switch (key)
            {
                case "Mic": Add(_micId); break;
                case "Proc": Add(_compId); break;
                case "Vdd": Add(_vddId); break;
                case "Current": Add(_ampsId); break;
                case "Temp": Add(_tempId); break;
            }
        }

        // Multi-display modes subscribe only to the extra telemetry they need.
        if (IsDigitalMultimeter)
            foreach (var key in TxMeterOptions) AddOptional(key);
        else if (IsMultiAnalogue)
        {
            AddOptional(MultiNeedle1);
            AddOptional(MultiNeedle2);
            AddOptional(MultiNeedle3);
        }
        else AddOptional(TxMeter);
        return ids;
    }

    async Task EnableMeterFallbackAsync(FlexClient c)
    {
        if (_meterFallbackAll || _client != c) return;
        var r = await c.SendAsync("sub meter all");
        if (r.Code == 0)
        {
            _meterFallbackAll = true;
            _meterSubscriptions.Clear();
            LastMessage = "Selective meter subscription wasn't available, so Companion fell back to all meters.";
        }
    }

    void HandleMeterStatus(string rest)
    {
        rest = rest.Trim();
        if (rest.EndsWith("removed", StringComparison.Ordinal))
        {
            foreach (var part in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out var rid))
                {
                    _meterDefs.Remove(rid);
                    _meterSubscriptions.Remove(rid);
                }
            _meterMapDirty = true;
            ScheduleMeterRefresh();
            return;
        }

        ParseMeterMetadata(rest);
        _meterMapDirty = true;
    }

    void ParseMeterMetadata(string rest)
    {
        // "7.src=SLC#7.num=0#7.nam=LEVEL#7.unit=dBm#..."
        foreach (var part in rest.Split('#', StringSplitOptions.RemoveEmptyEntries))
        {
            int dot = part.IndexOf('.');
            int eq = part.IndexOf('=');
            if (dot <= 0 || eq < dot) continue;
            if (!int.TryParse(part[..dot], out var id)) continue;
            var key = part[(dot + 1)..eq];
            var val = part[(eq + 1)..];
            if (!_meterDefs.TryGetValue(id, out var d)) _meterDefs[id] = d = new MeterDef { Id = id };
            switch (key)
            {
                case "src": d.Source = val; break;
                case "num": int.TryParse(val, out d.Num); break;
                case "nam": d.Name = val; break;
                case "unit": d.Unit = val; break;
            }
        }
    }

}
