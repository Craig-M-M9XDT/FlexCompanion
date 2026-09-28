using FlexCompanion.Flex;
using FlexCompanion.Services;

namespace FlexCompanion.ViewModels;

/// <summary>Scale definition for one TX meter choice.</summary>
public sealed record TxMeterSpec(string Key, string Title, double Min, double Max, double Red, string Scale, Func<double, string> Format);

public sealed partial class RadioViewModel
{
    int _rxId = -1, _fwdId = -1, _swrId = -1, _micId = -1, _compId = -1, _vddId = -1, _ampsId = -1, _tempId = -1;
    SlotPrefs _prefs = new();
    double _vdd = double.NaN, _amps = double.NaN, _temp = double.NaN;

    public static IReadOnlyList<string> TxMeterOptions { get; } = new[] { "Power", "SWR", "Proc", "Mic", "Vdd", "Current", "Temp" };

    public SlotPrefs Prefs
    {
        get => _prefs;
        set
        {
            _prefs = value ?? new SlotPrefs();
            OnPropertyChanged(string.Empty);   // refresh every binding that depends on prefs
            ApplyFftPreference();
        }
    }

    public bool AnalogueMeter
    {
        get => _prefs.AnalogueMeter;
        set
        {
            if (_prefs.AnalogueMeter == value) return;
            _prefs.AnalogueMeter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DigitalMeter));
        }
    }

    public bool DigitalMeter
    {
        get => !_prefs.AnalogueMeter;
        set => AnalogueMeter = !value;
    }

    public string TxMeter
    {
        get => _prefs.TxMeter;
        set
        {
            if (value == null || _prefs.TxMeter == value) return;
            _prefs.TxMeter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TxSpec));
            OnPropertyChanged(nameof(TxSelIsExtra));
            UpdateDial();
            ScheduleMeterRefresh();
        }
    }

    public TxMeterSpec TxSpec => SpecFor(TxMeter);

    /// <summary>True when the chosen TX meter isn't already shown as a fixed digital row.</summary>
    public bool TxSelIsExtra => TxMeter is not ("Power" or "SWR");

    TxMeterSpec SpecFor(string key)
    {
        switch (key)
        {
            case "SWR":
                return new("SWR", "SWR", 1, 3, 2, "1:1,1.5:1.5,2:2,2.5:2.5,3:3", v => $"{v:0.0}:1");
            case "Proc":
                return new("Proc", "PROC dB", 0, 25, 20, "0:0,5:5,10:10,15:15,20:20,25:25", v => $"{v:0} dB");
            case "Mic":
                return new("Mic", "MIC dB", -40, 0, -5, "-40:-40,-30:-30,-20:-20,-10:-10,0:0", v => v <= -59 ? "-" : $"{v:0} dB");
            case "Vdd":
                return new("Vdd", "Vdd", 10, 16, 15, "10:10,11:11,12:12,13:13,14:14,15:15,16:16", v => double.IsNaN(v) ? "-" : $"{v:0.0} V");
            case "Current":
                return new("Current", "Id  A", 0, 25, 22, "0:0,5:5,10:10,15:15,20:20,25:25", v => double.IsNaN(v) ? "-" : $"{v:0.0} A");
            case "Temp":
                return new("Temp", "PA °C", 20, 90, 75, "20:20,40:40,60:60,80:80", v => double.IsNaN(v) ? "-" : $"{v:0} °C");
            default:
                double max = PowerMax;
                double step = max > 200 ? 100 : 20;
                var labels = new List<string>();
                for (double x = 0; x <= max + 0.1; x += step) labels.Add($"{x:0}:{x:0}");
                return new("Power", "PWR  W", 0, max, max > 200 ? 550 : 105, string.Join(",", labels), v => $"{v:0} W");
        }
    }

    double TxRaw(string key) => key switch
    {
        "SWR" => Swr,
        "Proc" => CompDb,
        "Mic" => MicDb,
        "Vdd" => _vdd,
        "Current" => _amps,
        "Temp" => _temp,
        _ => FwdWatts,
    };

    // ── selected-TX-meter values (digital extra row) ──
    double _txSelValue = double.NaN;
    string _txSelText = "";
    public double TxSelValue { get => _txSelValue; private set => Set(ref _txSelValue, value); }
    public string TxSelText { get => _txSelText; private set => Set(ref _txSelText, value); }

    // ── dual RX/TX dial ──
    double _dialValue = double.NaN, _dialMin = -127, _dialMax = -13, _dialRed = -73;
    string _dialScale = RxScale, _dialTitle = "S", _dialText = "-", _dialSub = "";
    const string RxScale = "-121:1,-109:3,-97:5,-85:7,-73:9,-53:+20,-33:+40,-13:+60";

    public double DialValue { get => _dialValue; private set => Set(ref _dialValue, value); }
    public double DialMin { get => _dialMin; private set => Set(ref _dialMin, value); }
    public double DialMax { get => _dialMax; private set => Set(ref _dialMax, value); }
    public double DialRed { get => _dialRed; private set => Set(ref _dialRed, value); }
    public string DialScale { get => _dialScale; private set => Set(ref _dialScale, value); }
    public string DialTitle { get => _dialTitle; private set => Set(ref _dialTitle, value); }
    public string DialText { get => _dialText; private set => Set(ref _dialText, value); }
    public string DialSub { get => _dialSub; private set => Set(ref _dialSub, value); }

    string _vddText = "", _ampsText = "", _tempText = "";
    public string VddText { get => _vddText; private set => Set(ref _vddText, value); }
    public string AmpsText { get => _ampsText; private set => Set(ref _ampsText, value); }
    public string TempText { get => _tempText; private set => Set(ref _tempText, value); }

    void UpdateDial()
    {
        var spec = TxSpec;
        double sel = TxRaw(spec.Key);
        TxSelValue = sel;
        TxSelText = spec.Format(sel);

        if (IsTransmitting)
        {
            DialMin = spec.Min; DialMax = spec.Max; DialRed = spec.Red;
            DialScale = spec.Scale; DialTitle = spec.Title;
            DialValue = sel;
            DialText = spec.Format(sel);
            DialSub = spec.Key == "Power" ? SwrText : PowerText;
        }
        else
        {
            DialMin = -127; DialMax = -13; DialRed = -73;
            DialScale = RxScale; DialTitle = "S";
            DialValue = RxDbm;
            DialText = RxSText;
            DialSub = RxDbmText;
        }
    }

    void RebuildMeterMap()
    {
        _rxId = _fwdId = _swrId = _micId = _compId = _vddId = _ampsId = _tempId = -1;
        int slice = SelectedSlice?.Index ?? -1;
        foreach (var d in _meterDefs.Values)
        {
            bool cod = d.Source.StartsWith("COD", StringComparison.OrdinalIgnoreCase);
            bool tx = d.Source.StartsWith("TX", StringComparison.OrdinalIgnoreCase);
            bool amp = d.Source.Equals("AMP", StringComparison.OrdinalIgnoreCase);
            string n = d.Name;
            if (d.Source.Equals("SLC", StringComparison.OrdinalIgnoreCase) && d.Num == slice && n.Equals("LEVEL", StringComparison.OrdinalIgnoreCase))
                _rxId = d.Id;
            else if (tx && n.Equals("FWDPWR", StringComparison.OrdinalIgnoreCase)) _fwdId = d.Id;
            else if (tx && n.Equals("SWR", StringComparison.OrdinalIgnoreCase)) _swrId = d.Id;
            else if (n.Equals("MICPEAK", StringComparison.OrdinalIgnoreCase) && (_micId < 0 || cod)) _micId = d.Id;
            else if (n.Equals("COMPPEAK", StringComparison.OrdinalIgnoreCase) && (_compId < 0 || cod)) _compId = d.Id;
            else if (n.Equals("+13.8A", StringComparison.OrdinalIgnoreCase)) _vddId = d.Id;
            else if (!amp && n.Equals("PACURRENT", StringComparison.OrdinalIgnoreCase)) _ampsId = d.Id;
            else if (!amp && n.Equals("PATEMP", StringComparison.OrdinalIgnoreCase)) _tempId = d.Id;
        }
        _meterMapDirty = false;
    }

    double Read(int id)
    {
        if (id < 0 || _client == null || !_client.Meters.TryGetValue((ushort)id, out var raw)) return double.NaN;
        var unit = _meterDefs.TryGetValue(id, out var d) ? d.Unit.ToLowerInvariant() : "";
        return unit switch
        {
            "dbm" or "db" or "dbfs" or "swr" => raw / 128.0,
            "volts" or "amps" => raw / 256.0,
            "degc" => raw / 64.0,
            "degf" => (raw / 64.0 - 32) * 5 / 9,
            _ => raw,
        };
    }

    /// <summary>UDP-thread notification. Coalesce packet bursts into one UI update using the latest values.</summary>
    void OnMeterPacket()
    {
        _lastMeterPacketUtc = DateTime.UtcNow;
        if (Interlocked.Exchange(ref _meterUiPending, 1) != 0) return;
        Ui.Post(() =>
        {
            Interlocked.Exchange(ref _meterUiPending, 0);
            if (IsConnected && _client != null) OnMeterTick();
        });
    }

    void OnMeterTick()
    {
        if (_client == null) return;
        if (_meterMapDirty) RebuildMeterMap();

        double rx = Read(_rxId);
        if (!double.IsNaN(rx))
        {
            RxDbm = double.IsNaN(RxDbm) ? rx : RxDbm + (rx - RxDbm) * (rx > RxDbm ? 0.6 : 0.25);
            RxSText = SText(RxDbm);
            RxDbmText = $"{RxDbm:0} dBm";
        }

        double fwd = Read(_fwdId);
        double watts = double.IsNaN(fwd) ? 0 : Math.Pow(10, fwd / 10.0) / 1000.0;
        IsTransmitting = _interlockTx || watts > 1.0;

        if (IsTransmitting)
        {
            FwdWatts += (watts - FwdWatts) * (watts > FwdWatts ? 0.7 : 0.3);
            var swr = Read(_swrId);
            if (!double.IsNaN(swr)) Swr = Math.Max(1, swr);
            var comp = Read(_compId);
            if (!double.IsNaN(comp)) CompDb = Math.Clamp(comp, 0, 30);
        }
        else
        {
            FwdWatts *= 0.6;
            Swr = 1 + (Swr - 1) * 0.6;
            CompDb *= 0.6;
        }

        var mic = Read(_micId);
        if (!double.IsNaN(mic)) MicDb = mic;

        var v = Read(_vddId);
        if (!double.IsNaN(v)) _vdd = double.IsNaN(_vdd) ? v : _vdd + (v - _vdd) * 0.3;
        var a = Read(_ampsId);
        if (!double.IsNaN(a)) _amps = double.IsNaN(_amps) ? a : _amps + (a - _amps) * 0.4;
        var t = Read(_tempId);
        if (!double.IsNaN(t)) _temp = double.IsNaN(_temp) ? t : _temp + (t - _temp) * 0.1;

        PowerText = $"{FwdWatts:0} W";
        SwrText = $"{Swr:0.0}:1";
        MicText = MicDb <= -59 ? "" : $"{MicDb:0} dB";
        CompText = CompDb < 0.5 ? "" : $"{CompDb:0} dB";
        VddText = double.IsNaN(_vdd) ? "" : $"{_vdd:0.0} V";
        AmpsText = double.IsNaN(_amps) ? "" : $"{_amps:0.0} A";
        TempText = double.IsNaN(_temp) ? "" : $"{_temp:0} °C";

        UpdateDial();
        OnAudioTick();
    }

    void ResetExtraMeters()
    {
        _vdd = _amps = _temp = double.NaN;
        VddText = AmpsText = TempText = "";
        TxSelValue = double.NaN; TxSelText = "";
        UpdateDial();
    }

    static string SText(double dbm)
    {
        if (double.IsNaN(dbm)) return "-";
        if (dbm <= -73)
        {
            int s = Math.Clamp((int)Math.Round(9 + (dbm + 73) / 6.0), 0, 9);
            return $"S{s}";
        }
        return $"S9+{Math.Round(dbm + 73):0}";
    }
}
