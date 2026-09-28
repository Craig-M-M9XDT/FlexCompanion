using System.Globalization;
using FlexCompanion.Flex;
using FlexCompanion.Services;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{
    readonly AudioAnalyzer _audio = new();
    readonly IqAnalyzer _iq = new();
    readonly AgcTCalibrator _agc = new();
    volatile uint _daxStreamId, _iqStreamId;
    int _daxChannel, _iqChannel;
    string _iqPanId = "";
    bool _iqPanAssigned;
    bool _audioBusy, _audioRecheck, _iqBusy, _iqRecheck;
    bool _needsDax;
    string _audioStatus = "", _spectrumStatus = "";
    float[]? _spectrum, _specAvg;
    double _spectrumMaxHz = 24000, _spectrumCenterOffsetHz, _spectrumSliceFrac = 0.5;
    int _iqRate = 48000;
    bool _iqLost;
    long _lastIqSpectrumSequence;
    long _lastAetherPanSequence;
    DateTime _fftEnabledAt = DateTime.MinValue;
    bool _aetherTakeoverBusy;
    int _aetherUiPending;
    const int AetherProbeMilliseconds = 1200;
    int _agcSliceIndex = -1;
    string _agcKey = "agc_threshold";
    bool _agcWaitingForAudio;
    bool _agcPreparingSweep;
    int _agcRunEpoch;
    DateTime _agcFiltersChangedAt = DateTime.MinValue;
    readonly Dictionary<string, bool> _agcFilterSnapshot = new(StringComparer.OrdinalIgnoreCase);

    static readonly (string StatusKey, string SetKey)[] AgcFilterKeys =
    {
        ("nr", "nr"),
        ("nrf", "nrf"),
        ("nrl", "lms_nr"),
        ("nrs", "speex_nr"),
        ("rnn", "rnnoise"),
        ("nb", "nb"),
        ("anf", "anf"),
        ("anfl", "lms_anf"),
        ("anft", "anft"),
    };

    RelayCommand? _assignDax, _agcAuto, _agcKeep, _agcRestore;

    public RelayCommand AssignDaxCommand => _assignDax ??= new RelayCommand(AssignDax, () => IsConnected && SelectedSlice != null);
    public RelayCommand AgcAutoCommand => _agcAuto ??= new RelayCommand(StartAgcSweep, () => IsConnected && SelectedSlice != null && !_agc.IsRunning && !_agcPreparingSweep);
    public RelayCommand AgcKeepCommand => _agcKeep ??= new RelayCommand(() => { _agc.Keep(); RestoreAgcFilters(); AgcChanged(); }, () => _agc.Recommended >= 0 && !_agc.IsRunning);
    public RelayCommand AgcRestoreCommand => _agcRestore ??= new RelayCommand(() => { _agcRunEpoch++; _agcPreparingSweep = false; _agc.Stop(); _agcWaitingForAudio = false; RestoreAgcFilters(); AgcChanged(); ReleaseAudioIfUnused(); },
                                                                               () => _agc.IsRunning || _agc.Recommended >= 0 || _agcWaitingForAudio || _agcPreparingSweep);

    public bool ShowFft
    {
        get => _prefs.ShowFft;
        set
        {
            if (_prefs.ShowFft == value) return;
            _prefs.ShowFft = value;
            if (value) _fftEnabledAt = DateTime.UtcNow;
            OnPropertyChanged();
            ApplyFftPreference();
        }
    }

    public float[]? Spectrum { get => _spectrum; private set => Set(ref _spectrum, value); }
    public double SpectrumMaxHz { get => _spectrumMaxHz; private set => Set(ref _spectrumMaxHz, value); }
    public double SpectrumSampleRate => _iqRate;
    public bool SpectrumCentered => true;
    public double SpectrumCenterOffsetHz { get => _spectrumCenterOffsetHz; private set => Set(ref _spectrumCenterOffsetHz, value); }
    public double SpectrumSliceFraction { get => _spectrumSliceFrac; private set => Set(ref _spectrumSliceFrac, value); }

    public static IReadOnlyList<double> FftSpanOptionsKhz { get; } = new[] { 3.0, 6, 12, 24, 48, 96, 192 };
    public IReadOnlyList<double> FftSpans => FftSpanOptionsKhz;

    public double FftSpanKhz
    {
        get => FftSpanOptionsKhz.Contains(_prefs.FftSpanKhz) ? _prefs.FftSpanKhz : 48;
        set
        {
            if (!FftSpanOptionsKhz.Contains(value) || _prefs.FftSpanKhz == value) return;
            _prefs.FftSpanKhz = value;
            OnPropertyChanged();
            _specAvg = null;
            SpectrumMaxHz = DaxSpectrumWindow.SpanHz * 0.5;
            _iq.Configure(ShowFft ? FftSize : 0, LowBandwidthMode ? 20 : 30);
            _ = ApplyIqRateAsync();
        }
    }

    double EffectiveFftSpanKhz => LowBandwidthMode ? Math.Min(FftSpanKhz, 24.0) : FftSpanKhz;

    readonly record struct SpectrumWindow(double CentreOffsetHz, double SpanHz);

    static SpectrumWindow CalculateSpectrumWindow(string mode, string? filterLo, string? filterHi, double requestedSpanHz)
    {
        (double Lo, double Hi) fallback = mode.ToUpperInvariant() switch
        {
            "USB" or "DIGU" or "FDVU" => (100, 3000),
            "LSB" or "DIGL" or "RTTY" or "FDVL" => (-3000, -100),
            "AM" or "SAM" => (-3000, 3000),
            "FM" => (-8000, 8000),
            "NFM" => (-6000, 6000),
            "CW" or "CWL" or "CWU" or "CWR" => (-500, 500),
            _ => (0, 0),
        };

        double lo = double.TryParse(filterLo, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLo) ? parsedLo : fallback.Lo;
        double hi = double.TryParse(filterHi, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedHi) ? parsedHi : fallback.Hi;
        if (hi < lo) (lo, hi) = (hi, lo);

        double centre = (lo + hi) * 0.5;
        double filterWidth = Math.Max(0, hi - lo);
        double halfExtent = Math.Max(Math.Max(Math.Abs(lo - centre), Math.Abs(hi - centre)), Math.Abs(centre));
        double margin = Math.Max(400, filterWidth * 0.12);
        double span = Math.Max(Math.Max(requestedSpanHz, 1000), halfExtent * 2 + margin);
        return new SpectrumWindow(centre, span);
    }

    SpectrumWindow SpectrumWindowFor(double requestedSpanHz)
    {
        var slice = SelectedSlice;
        return CalculateSpectrumWindow(slice?.Mode ?? "",
            slice?.State.GetValueOrDefault("filter_lo"),
            slice?.State.GetValueOrDefault("filter_hi"), requestedSpanHz);
    }

    SpectrumWindow AetherSpectrumWindow => SpectrumWindowFor(FftSpanKhz * 1000.0);

    SpectrumWindow DaxSpectrumWindow
    {
        get
        {
            var window = SpectrumWindowFor(EffectiveFftSpanKhz * 1000.0);
            return LowBandwidthMode && window.SpanHz > 24000 ? window with { SpanHz = 24000 } : window;
        }
    }

    int RateForSpan
    {
        get
        {
            if (LowBandwidthMode) return 24000;
            return (DaxSpectrumWindow.SpanHz / 1000.0) switch
            {
                <= 24 => 24000,
                <= 48 => 48000,
                <= 96 => 96000,
                _ => 192000,
            };
        }
    }

    int FftSize
    {
        get
        {
            double want = _iqRate / DaxSpectrumWindow.SpanHz * 512;
            int n = 1024;
            while (n < want && n < 8192) n <<= 1;
            return n;
        }
    }
}
