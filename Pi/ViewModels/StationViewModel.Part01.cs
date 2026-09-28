using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using FlexCompanion.Services;
using FlexCompanion.Station;

namespace FlexCompanion.ViewModels;

public sealed partial class StationViewModel : ObservableObject, IDisposable
{
    readonly AppSettings _settings;
    readonly Func<string, RadioViewModel> _radioForSlot;
    readonly DxClusterClient _dx = new();
    readonly PgxlClient _pgxl = new();
    readonly DispatcherTimer _spotTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    string _status = "Native station tools ready";
    string _rawCommand = "";
    string _profileName = "";
    string _profileType = "global";
    string _pgxlStatus = "Not connected";
    string _dxStatus = "Not connected";
    string _ampAlert = "";
    double _ampPower, _ampSwr = 1, _ampCurrent = double.NaN, _ampTemp = double.NaN, _ampVdd = double.NaN, _ampVac = double.NaN;
    bool _tuneOn, _moxOn;
    int _memoryIndex;
    RadioViewModel? _subscribedRadio;

    static readonly Dictionary<string, double> BandCenters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["160"] = 1.900, ["80"] = 3.650, ["60"] = 5.360, ["40"] = 7.100,
        ["30"] = 10.120, ["20"] = 14.200, ["17"] = 18.130, ["15"] = 21.200,
        ["12"] = 24.950, ["10"] = 28.400, ["6"] = 50.200,
    };

    public StationViewModel(AppSettings settings, Func<string, RadioViewModel> radioForSlot)
    {
        _settings = settings;
        _radioForSlot = radioForSlot;
        Macros = new ObservableCollection<StationMacro>(_settings.StationMacros);

        BandCommand = new RelayCommand(async p => await SetBandAsync(p?.ToString() ?? ""));
        ModeCommand = new RelayCommand(async p => await SetModeAsync(p?.ToString() ?? ""));
        AtuCommand = new RelayCommand(async () => await SendAsync("atu start"));
        AtuBypassCommand = new RelayCommand(async () => await SendAsync("atu bypass"));
        TuneCommand = new RelayCommand(async () =>
        {
            bool want = !TuneOn;
            if (await SendAsync($"transmit tune {(want ? 1 : 0)}")) TuneOn = want;
        });
        MoxCommand = new RelayCommand(async () =>
        {
            bool want = !MoxOn;
            if (await SendAsync($"xmit {(want ? 1 : 0)}")) MoxOn = want;
        });
        SendRawCommand = new RelayCommand(async () => await SendAsync(RawCommand), () => RawCommand.Trim().Length > 0);
        LoadProfileCommand = new RelayCommand(async () => await LoadProfileAsync(), () => ProfileName.Trim().Length > 0);
        ApplyMemoryCommand = new RelayCommand(async () => await SendAsync($"memory apply {MemoryIndex}"), () => MemoryIndex >= 0);
        RunMacroCommand = new RelayCommand(async p => { if (p is StationMacro m) await RunMacroAsync(m); });
        DxConnectCommand = new RelayCommand(async () => await ConnectDxAsync());
        DxDisconnectCommand = new RelayCommand(_dx.Disconnect);
        TuneSpotCommand = new RelayCommand(async p => { if (p is SpotItem s) await TuneSpotAsync(s); });
        PgxlConnectCommand = new RelayCommand(async () => await ConnectPgxlAsync());
        PgxlDisconnectCommand = new RelayCommand(_pgxl.Disconnect);
        AmpOperateCommand = new RelayCommand(async () => await ToggleAmpAsync());

        _dx.AutoReconnect = _settings.DxClusterAutoReconnect;
        _dx.Connected += () => { DxStatus = $"Connected to {DxClusterHost}:{DxClusterPort}"; };
        _dx.Disconnected += () => { DxStatus = "Disconnected"; };
        _dx.Error += e => { DxStatus = e; Status = $"DX cluster: {e}"; };
        _dx.Spot += spot =>
        {
            Spots.Insert(0, new SpotItem { Spot = spot });
            while (Spots.Count > 250) Spots.RemoveAt(Spots.Count - 1);
            if (_settings.PublishSpotsToRadio) _ = PublishSpotToRadioAsync(spot);
        };

        _pgxl.AutoReconnect = _settings.PgxlAutoReconnect;
        _pgxl.Connected += () => { PgxlStatus = $"Connected · v{_pgxl.Version}"; };
        _pgxl.Disconnected += () => { PgxlStatus = "Disconnected"; };
        _pgxl.Error += e => { PgxlStatus = e; Status = $"Amplifier: {e}"; };
        _pgxl.Alert += a => AmpAlert = a;
        _pgxl.Status += ApplyPgxlStatus;

        _spotTimer.Tick += (_, _) => ExpireSpots();
        _spotTimer.Start();
        WireTargetRadio();
    }

    public ObservableCollection<SpotItem> Spots { get; } = new();
    public ObservableCollection<StationMacro> Macros { get; }

    public RelayCommand BandCommand { get; }
    public RelayCommand ModeCommand { get; }
    public RelayCommand AtuCommand { get; }
    public RelayCommand AtuBypassCommand { get; }
    public RelayCommand TuneCommand { get; }
    public RelayCommand MoxCommand { get; }
    public RelayCommand SendRawCommand { get; }
    public RelayCommand LoadProfileCommand { get; }
    public RelayCommand ApplyMemoryCommand { get; }
    public RelayCommand RunMacroCommand { get; }
    public RelayCommand DxConnectCommand { get; }
    public RelayCommand DxDisconnectCommand { get; }
    public RelayCommand TuneSpotCommand { get; }
    public RelayCommand PgxlConnectCommand { get; }
    public RelayCommand PgxlDisconnectCommand { get; }
    public RelayCommand AmpOperateCommand { get; }

    RadioViewModel TargetRadio => _radioForSlot(TargetSlot);

    public string TargetSlot
    {
        get => _settings.StationTargetSlot is "B" ? "B" : "A";
        set
        {
            var v = value == "B" ? "B" : "A";
            if (_settings.StationTargetSlot == v) return;
            _settings.StationTargetSlot = v;
            OnPropertyChanged();
            WireTargetRadio();
            RefreshRadioDerived();
        }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }
    public bool TuneOn { get => _tuneOn; private set => Set(ref _tuneOn, value); }
    public bool MoxOn { get => _moxOn; private set => Set(ref _moxOn, value); }

    public string RawCommand { get => _rawCommand; set => Set(ref _rawCommand, value); }
    public string ProfileName { get => _profileName; set => Set(ref _profileName, value); }
    public string ProfileType { get => _profileType; set => Set(ref _profileType, value); }
    public int MemoryIndex { get => _memoryIndex; set => Set(ref _memoryIndex, Math.Max(0, value)); }

    public string DxClusterHost { get => _settings.DxClusterHost; set { _settings.DxClusterHost = value.Trim(); OnPropertyChanged(); } }
    public int DxClusterPort { get => _settings.DxClusterPort; set { _settings.DxClusterPort = Math.Clamp(value, 1, 65535); OnPropertyChanged(); } }
    public string DxClusterCallsign { get => _settings.DxClusterCallsign; set { _settings.DxClusterCallsign = value.Trim().ToUpperInvariant(); OnPropertyChanged(); } }
    public string DxStatus { get => _dxStatus; private set => Set(ref _dxStatus, value); }

    public string PgxlHost
    {
        get => _settings.PgxlHost;
        set { _settings.PgxlHost = value.Trim(); OnPropertyChanged(); }
    }
    public int PgxlPort { get => _settings.PgxlPort; set { _settings.PgxlPort = Math.Clamp(value, 1, 65535); OnPropertyChanged(); } }
    public string PgxlStatus { get => _pgxlStatus; private set => Set(ref _pgxlStatus, value); }
    public string AmpAlert { get => _ampAlert; private set => Set(ref _ampAlert, value); }
    public double AmpPower { get => _ampPower; private set => Set(ref _ampPower, value); }
    public double AmpSwr { get => _ampSwr; private set => Set(ref _ampSwr, value); }
    public double AmpCurrent { get => _ampCurrent; private set => Set(ref _ampCurrent, value); }
    public double AmpTemp { get => _ampTemp; private set => Set(ref _ampTemp, value); }
    public double AmpVdd { get => _ampVdd; private set => Set(ref _ampVdd, value); }
    public double AmpVac { get => _ampVac; private set => Set(ref _ampVac, value); }

    public string AmpPowerText => double.IsFinite(AmpPower) ? $"{AmpPower:0} W" : "—";
    public string AmpSwrText => double.IsFinite(AmpSwr) ? $"{AmpSwr:0.0}:1" : "—";
    public string AmpCurrentText => double.IsFinite(AmpCurrent) ? $"{AmpCurrent:0.0} A" : "—";
    public string AmpTempText => double.IsFinite(AmpTemp) ? $"{AmpTemp:0.0} °C" : "—";
}
