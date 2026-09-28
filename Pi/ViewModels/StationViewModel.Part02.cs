using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using FlexCompanion.Services;
using FlexCompanion.Station;

namespace FlexCompanion.ViewModels;

public sealed partial class StationViewModel
{
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
