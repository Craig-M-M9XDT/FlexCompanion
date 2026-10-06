using System.Collections.ObjectModel;
using Avalonia.Threading;
using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel : ObservableObject
{
    FlexClient? _client;
    // Meter packets normally drive presentation directly. This timer is only a slow watchdog
    // for radios/firmware that temporarily stop emitting meter packets.
    readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _fftTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
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
}
