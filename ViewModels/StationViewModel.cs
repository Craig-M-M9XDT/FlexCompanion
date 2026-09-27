using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Threading;
using FlexCompanion.Services;
using FlexCompanion.Station;

namespace FlexCompanion.ViewModels;

public sealed class SpotItem
{
    public required DxSpot Spot { get; init; }
    public DateTime ReceivedUtc { get; init; } = DateTime.UtcNow;
    public string Call => Spot.Callsign;
    public string Frequency => $"{Spot.FrequencyMhz:0.000000}";
    public string Comment => Spot.Comment;
    public string Source => Spot.Spotter.Length > 0 ? $"de {Spot.Spotter}" : "DX";
    public string Age => $"{Math.Max(0, (int)(DateTime.UtcNow - ReceivedUtc).TotalMinutes)}m";
}

/// <summary>
/// Native station-control surface. Replaces the former FRStack REST window with
/// direct FLEX API commands plus direct peripheral clients. Radio replies remain
/// authoritative; subscription labels are informational rather than a generic UI gate.
/// </summary>
public sealed class StationViewModel : ObservableObject, IDisposable
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
            if (await SendAsync($"transmit tune {(want ? 1 : 0)}")) TuneOn = want;   // only flip when the radio accepted it
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
    public string AmpVddText => double.IsFinite(AmpVdd) ? $"{AmpVdd:0.0} V" : "—";
    public string AmpVacText => double.IsFinite(AmpVac) ? $"{AmpVac:0} V" : "—";

    public string RadioAmpText
    {
        get
        {
            var r = TargetRadio;
            if (!r.HasAmplifier) return "No FLEX-reported amplifier";
            return $"{(r.AmplifierModel.Length > 0 ? r.AmplifierModel : "Amplifier")} · {r.AmplifierState}";
        }
    }
    public string RadioAmpIp => TargetRadio.AmplifierIp;
    public string AmpOperateLabel => TargetRadio.AmplifierOperate ? "STANDBY" : "OPERATE";
    public string LicenseSummary => TargetRadio.LicenseSummary;

    async Task SetBandAsync(string band)
    {
        if (!BandCenters.TryGetValue(band, out var mhz)) { Status = $"Unknown band {band}"; return; }
        var r = await TargetRadio.TuneAsync(mhz);
        Status = r.Code == 0 ? $"{band} m · {mhz:0.000} MHz" : Flex.FlexClient.ErrorText(r.Code);
    }

    async Task SetModeAsync(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return;
        var r = await TargetRadio.SetModeAsync(mode.Trim().ToUpperInvariant());
        Status = r.Code == 0 ? $"Mode {mode.ToUpperInvariant()}" : Flex.FlexClient.ErrorText(r.Code);
    }

    async Task<bool> SendAsync(string command)
    {
        command = command.Trim();
        if (command.Length == 0) return false;
        var r = await TargetRadio.ExecuteAsync(command, bindToSelectedStation: true);
        Status = r.Code == 0 ? command : $"{command} · {Flex.FlexClient.ErrorText(r.Code)}";
        return r.Code == 0;
    }

    static string QuoteFlex(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    async Task LoadProfileAsync()
    {
        var type = ProfileType.ToLowerInvariant() switch { "tx" or "transmit" => "tx", "mic" => "mic", _ => "global" };
        await SendAsync($"profile {type} load {QuoteFlex(ProfileName.Trim())}");
    }

    async Task RunMacroAsync(StationMacro macro)
    {
        foreach (var raw in macro.Commands.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("@mode ", StringComparison.OrdinalIgnoreCase))
                await SetModeAsync(line[6..].Trim());
            else if (line.StartsWith("@band ", StringComparison.OrdinalIgnoreCase))
                await SetBandAsync(line[6..].Trim());
            else
                await SendAsync(line);
        }
    }

    async Task ConnectDxAsync()
    {
        if (DxClusterHost.Length == 0 || DxClusterCallsign.Length == 0)
        {
            DxStatus = "Enter cluster host and callsign";
            return;
        }
        try { DxStatus = "Connecting…"; await _dx.ConnectAsync(DxClusterHost, DxClusterPort, DxClusterCallsign); }
        catch (Exception ex) { DxStatus = ex.Message; }
    }


    async Task PublishSpotToRadioAsync(DxSpot spot)
    {
        // FLEX spot service: publish the cluster hit so any GUI client attached to
        // the radio can render it too. Keep the payload conservative (token-safe
        // callsigns only); the local list still carries the full free-text comment.
        var call = new string(spot.Callsign.Where(c => char.IsLetterOrDigit(c) || c is '/' or '-').ToArray());
        var de = new string(spot.Spotter.Where(c => char.IsLetterOrDigit(c) || c is '/' or '-' or '#').ToArray());
        if (call.Length == 0) return;
        string f = spot.FrequencyMhz.ToString("0.000000", CultureInfo.InvariantCulture);
        var cmd = $"spot add callsign={call} rx_freq={f} tx_freq={f} source=FlexCompanion lifetime_seconds={Math.Max(60, _settings.SpotMaxAgeMinutes * 60)}";
        if (de.Length > 0) cmd += $" spotter_callsign={de}";
        await TargetRadio.ExecuteAsync(cmd);
    }

    async Task TuneSpotAsync(SpotItem s)
    {
        var r = await TargetRadio.TuneAsync(s.Spot.FrequencyMhz);
        Status = r.Code == 0 ? $"Tuned {s.Spot.Callsign} · {s.Spot.FrequencyMhz:0.000000}" : Flex.FlexClient.ErrorText(r.Code);
    }

    void ExpireSpots()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, _settings.SpotMaxAgeMinutes));
        for (int i = Spots.Count - 1; i >= 0; i--)
            if (Spots[i].ReceivedUtc < cutoff) Spots.RemoveAt(i);
        OnPropertyChanged(nameof(Spots));
    }

    async Task ConnectPgxlAsync()
    {
        var host = PgxlHost;
        if (host.Length == 0) host = TargetRadio.AmplifierIp;
        if (host.Length == 0) { PgxlStatus = "Enter PGXL IP or connect a radio that reports one"; return; }
        if (PgxlHost.Length == 0) { _settings.PgxlHost = host; OnPropertyChanged(nameof(PgxlHost)); }
        try { PgxlStatus = "Connecting…"; await _pgxl.ConnectAsync(host, PgxlPort); }
        catch (Exception ex) { PgxlStatus = ex.Message; }
    }

    async Task ToggleAmpAsync()
    {
        var radio = TargetRadio;
        var r = await radio.SetAmplifierOperateAsync(!radio.AmplifierOperate);
        Status = r.Code == 0 ? $"Amplifier {(radio.AmplifierOperate ? "standby" : "operate")} requested" : r.Message;
    }

    void ApplyPgxlStatus(IReadOnlyDictionary<string, string> kv)
    {
        if (kv.TryGetValue("fwd", out var fwd)) AmpPower = PgxlClient.DbmToWatts(fwd);
        if (kv.TryGetValue("swr", out var swr)) AmpSwr = PgxlClient.ReturnLossToSwr(swr);
        AmpCurrent = ReadFirst(kv, "id", "current", "idd");
        AmpTemp = ReadFirst(kv, "temp", "temperature", "patemp");
        AmpVdd = ReadFirst(kv, "vdd", "vpa", "voltage");
        AmpVac = ReadFirst(kv, "vac", "mains");
        if (kv.TryGetValue("state", out var state)) PgxlStatus = $"{state} · v{_pgxl.Version}";
        NotifyAmpReadouts();
    }

    static double ReadFirst(IReadOnlyDictionary<string, string> kv, params string[] keys)
    {
        foreach (var key in keys)
            if (kv.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return double.NaN;
    }

    void NotifyAmpReadouts()
    {
        OnPropertyChanged(nameof(AmpPowerText)); OnPropertyChanged(nameof(AmpSwrText));
        OnPropertyChanged(nameof(AmpCurrentText)); OnPropertyChanged(nameof(AmpTempText));
        OnPropertyChanged(nameof(AmpVddText)); OnPropertyChanged(nameof(AmpVacText));
    }

    void WireTargetRadio()
    {
        if (_subscribedRadio != null) _subscribedRadio.PropertyChanged -= OnRadioPropertyChanged;
        _subscribedRadio = TargetRadio;
        _subscribedRadio.PropertyChanged += OnRadioPropertyChanged;
    }

    void OnRadioPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RadioViewModel.AmplifierHandle) or nameof(RadioViewModel.AmplifierModel)
            or nameof(RadioViewModel.AmplifierIp) or nameof(RadioViewModel.AmplifierState)
            or nameof(RadioViewModel.AmplifierOperate) or nameof(RadioViewModel.LicenseSummary)
            or nameof(RadioViewModel.IsConnected)) RefreshRadioDerived();
    }

    void RefreshRadioDerived()
    {
        OnPropertyChanged(nameof(RadioAmpText)); OnPropertyChanged(nameof(RadioAmpIp));
        OnPropertyChanged(nameof(AmpOperateLabel)); OnPropertyChanged(nameof(LicenseSummary));
    }

    public void Dispose()
    {
        _spotTimer.Stop();
        if (_subscribedRadio != null) _subscribedRadio.PropertyChanged -= OnRadioPropertyChanged;
        _dx.Dispose();
        _pgxl.Dispose();
    }
}
