using System.Collections.ObjectModel;
using Avalonia.Threading;
using FlexCompanion.Flex;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{

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
            // 800x480 / Pi 3B should run the FFT redraw at a lower cadence; the desktop-rate
            // refresh is too aggressive for the constrained touch hardware.
            _fftTimer.Interval = TimeSpan.FromMilliseconds(50);
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
}
