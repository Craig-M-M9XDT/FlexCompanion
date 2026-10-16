using System.Collections.Specialized;
using System.ComponentModel;

namespace FlexCompanion.ViewModels;

/// <summary>
/// Read-only full-chain metering plus explicitly requested SmartSignal commands.
/// The feedback value is a power-budget calculation, never an RF sampler reading.
/// </summary>
public sealed class ApdDashboardViewModel : ObservableObject, IDisposable
{
    readonly RadioViewModel _radio;
    readonly StationViewModel _station;
    ApdSamplerEntry? _selectedAntenna;
    string? _desiredPort;
    double _couplerLossDb = 60;
    double _inlineLossDb;

    public ApdDashboardViewModel(RadioViewModel radio, StationViewModel station)
    {
        _radio = radio;
        _station = station;
        ToggleCommand = new RelayCommand(async () => await Radio.SetApdEnabledAsync(!Radio.ApdEnabled), () => Radio.ApdCanControl);
        ResetCommand = new RelayCommand(async () => await Radio.ResetApdEqualizerAsync(), () => Radio.ApdCanControl && !Radio.IsTransmitting);
        ApplySamplerCommand = new RelayCommand(async () =>
        {
            if (SelectedAntenna != null && DesiredPort != null)
                await Radio.ApplyApdSamplerAsync(SelectedAntenna.TxAntenna, DesiredPort);
        }, () => CanApplySampler);

        Radio.PropertyChanged += OnRadioChanged;
        Radio.ApdSamplers.CollectionChanged += OnSamplerListChanged;
        Station.PropertyChanged += OnStationChanged;
        Radio.SetApdMonitoring(true);
        RefreshSamplerSelection();
    }

    public RadioViewModel Radio => _radio;
    public StationViewModel Station => _station;
    public RelayCommand ToggleCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ApplySamplerCommand { get; }

    public ApdSamplerEntry? SelectedAntenna
    {
        get => _selectedAntenna;
        set
        {
            if (!Set(ref _selectedAntenna, value)) return;
            OnPropertyChanged(nameof(AvailablePorts));
            DesiredPort = value?.SelectedPort;
            RaiseDerived();
        }
    }

    public IReadOnlyList<string> AvailablePorts => SelectedAntenna?.AvailablePorts ?? Array.Empty<string>();

    public string? DesiredPort
    {
        get => _desiredPort;
        set { if (Set(ref _desiredPort, value)) RaiseDerived(); }
    }

    public bool CanApplySampler => Radio.ApdCanControl && Radio.ApdConfigurable && !Radio.IsTransmitting
        && SelectedAntenna != null && DesiredPort != null && AvailablePorts.Contains(DesiredPort);

    /// <summary>Coupler and inline attenuation in dB are manual physical-hardware values.</summary>
    public double CouplerLossDb
    {
        get => _couplerLossDb;
        set { if (double.IsFinite(value) && Set(ref _couplerLossDb, Math.Clamp(value, 0, 150))) RaiseDerived(); }
    }

    public double InlineLossDb
    {
        get => _inlineLossDb;
        set { if (double.IsFinite(value) && Set(ref _inlineLossDb, Math.Clamp(value, 0, 150))) RaiseDerived(); }
    }

    public string TotalLossText => $"{CouplerLossDb + InlineLossDb:0.0} dB (entered values)";
    public string AmpPowerText => AmpSourceReady ? Station.AmpPowerText : "—";
    public string AmpSwrText => AmpSourceReady ? Station.AmpSwrText : "—";
    public bool AmpSourceReady => Radio.IsConnected && Station.TargetSlot == Radio.Slot && Station.PgxlConnected;
    public string AmpSourceText => !Radio.IsConnected ? "Connect radio first."
        : Station.TargetSlot != Radio.Slot ? "PGXL is assigned to another Companion slot."
        : !Station.PgxlConnected ? "Connect the PGXL on the Station panel to show amplifier telemetry."
        : "Live PGXL output telemetry (station target matched).";

    public string FeedbackEstimateText
    {
        get
        {
            if (!AmpSourceReady) return "—";
            if (SelectedAntenna is null) return "— (awaiting sampler selection from radio)";
            if (SelectedAntenna.SelectedPort.Equals("INTERNAL", StringComparison.OrdinalIgnoreCase))
                return "— (radio uses internal sampler)";
            if (!double.IsFinite(Station.AmpPower) || Station.AmpPower <= 0)
                return "— (no external output power)";
            double dbm = 10.0 * Math.Log10(Station.AmpPower * 1000.0) - CouplerLossDb - InlineLossDb;
            return $"{dbm:+0.0;-0.0;0.0} dBm ESTIMATED";
        }
    }

    void OnSamplerListChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshSamplerSelection();

    void RefreshSamplerSelection()
    {
        string? ant = SelectedAntenna?.TxAntenna;
        SelectedAntenna = Radio.ApdSamplers.FirstOrDefault(x => x.TxAntenna == ant)
                       ?? Radio.ApdSamplers.FirstOrDefault();
        OnPropertyChanged(nameof(AvailablePorts));
        RaiseDerived();
    }

    void OnRadioChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RadioViewModel.IsConnected) or nameof(RadioViewModel.IsTransmitting)
            or nameof(RadioViewModel.ApdCanControl) or nameof(RadioViewModel.ApdConfigurable)
            or nameof(RadioViewModel.ApdEnabled) or nameof(RadioViewModel.FwdWatts)
            or nameof(RadioViewModel.MicDb) or nameof(RadioViewModel.CompDb))
            RaiseDerived();
    }

    void OnStationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StationViewModel.TargetSlot) or nameof(StationViewModel.PgxlConnected)
            or nameof(StationViewModel.PgxlStatus) or nameof(StationViewModel.AmpPower)
            or nameof(StationViewModel.AmpSwr))
            RaiseDerived();
    }

    void RaiseDerived()
    {
        OnPropertyChanged(nameof(CanApplySampler));
        OnPropertyChanged(nameof(AmpSourceReady));
        OnPropertyChanged(nameof(AmpPowerText));
        OnPropertyChanged(nameof(AmpSwrText));
        OnPropertyChanged(nameof(AmpSourceText));
        OnPropertyChanged(nameof(FeedbackEstimateText));
        OnPropertyChanged(nameof(TotalLossText));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    public void Dispose()
    {
        Radio.SetApdMonitoring(false);
        Radio.PropertyChanged -= OnRadioChanged;
        Radio.ApdSamplers.CollectionChanged -= OnSamplerListChanged;
        Station.PropertyChanged -= OnStationChanged;
    }
}
