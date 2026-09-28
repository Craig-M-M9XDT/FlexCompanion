using System.Globalization;
using FlexCompanion.Flex;
using FlexCompanion.Services;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{

    void OnFftTick()
    {
        if (!ShowFft) return;

        if (TryGetFreshAetherFrame(out var aether) && aether != null)
        {
            ApplyAetherPanFrame(aether);
            return;
        }

        if (_iqStreamId == 0)
        {
            if (_fftEnabledAt == DateTime.MinValue) _fftEnabledAt = DateTime.UtcNow;
            if ((DateTime.UtcNow - _fftEnabledAt).TotalMilliseconds >= AetherProbeMilliseconds)
                _ = EnsureIqStreamAsync();
            else
                SpectrumStatus = "Looking for Aether shared pan…";
            return;
        }

        if (!_iq.IsLive)
        {
            SpectrumStatus = $"DAX IQ {_iqChannel}: waiting for I/Q…";
            Spectrum = null;
            return;
        }

        SpectrumStatus = SpectrumStatusText(_iqChannel);
        int n = FftSize;
        if (!_iq.TryGetLatest(n, ref _lastIqSpectrumSequence, out var full) || full == null) return;

        var spec = CropToSpan(full);
        var prev = _specAvg;
        if (prev != null && prev.Length == spec.Length)
        {
            for (int i = 0; i < spec.Length; i++)
                spec[i] = spec[i] > prev[i] ? prev[i] + (spec[i] - prev[i]) * 0.65f
                                             : prev[i] + (spec[i] - prev[i]) * 0.22f;
        }
        _specAvg = spec;
        Spectrum = spec;
    }

    float[] CropToSpan(float[] full)
    {
        int n = full.Length;
        double binHz = (double)_iqRate / n;
        var window = DaxSpectrumWindow;
        int visible = Math.Clamp((int)Math.Round(window.SpanHz / binHz), 16, n);
        SpectrumMaxHz = visible * binHz * 0.5;

        double sliceOffsetHz = 0;
        bool haveSlice = false;
        if (SelectedSlice is { } s && _pans.TryGetValue(s.Pan, out var pan)
            && Kv.D(s.State.GetValueOrDefault("RF_frequency")) is double sf
            && Kv.D(pan.GetValueOrDefault("center")) is double pcMhz)
        {
            sliceOffsetHz = (sf - pcMhz) * 1e6;
            haveSlice = Math.Abs(sliceOffsetHz) < _iqRate / 2.0;
        }

        double wantedCentreHz = haveSlice ? sliceOffsetHz + window.CentreOffsetHz : 0;
        int centreBin = n / 2 + (int)Math.Round(wantedCentreHz / binHz);
        int start = Math.Clamp(centreBin - visible / 2, 0, n - visible);
        var outp = new float[visible];
        Array.Copy(full, start, outp, 0, visible);

        double viewCentreHz = (start + visible / 2.0 - n / 2.0) * binHz;
        SpectrumCenterOffsetHz = haveSlice ? viewCentreHz - sliceOffsetHz : viewCentreHz;
        if (haveSlice)
        {
            double frac = (sliceOffsetHz - (start - n / 2.0) * binHz) / (visible * binHz);
            SpectrumSliceFraction = frac is >= 0 and <= 1 ? frac : double.NaN;
        }
        else SpectrumSliceFraction = double.NaN;
        return outp;
    }

    string AgcMode => SelectedSlice?.State.GetValueOrDefault("agc_mode") ?? "";
    public bool AgcIsOff => AgcMode.Equals("off", StringComparison.OrdinalIgnoreCase);
    public string AgcModeText => SelectedSlice == null ? "No slice selected"
        : AgcIsOff ? $"Slice {SelectedSlice.Letter}: AGC off — the knob is a fixed gain; the sweep finds a comfortable noise level"
        : $"Slice {SelectedSlice.Letter}: AGC {AgcMode.ToUpperInvariant()} — the sweep finds the knee where noise just starts to drop";

    public int AgcValue
    {
        get
        {
            var key = AgcIsOff ? "agc_off_level" : "agc_threshold";
            return (int)Math.Round(Kv.D(SelectedSlice?.State.GetValueOrDefault(key)) ?? 0);
        }
        set
        {
            if (SelectedSlice is not { } s || _agc.IsRunning) return;
            value = Math.Clamp(value, 0, 100);
            var key = AgcIsOff ? "agc_off_level" : "agc_threshold";
            if (AgcValue == value) return;
            s.State[key] = value.ToString(CultureInfo.InvariantCulture);
            Send($"slice set {s.Index} {key}={value}");
            OnPropertyChanged();
            OnPropertyChanged(nameof(AgcValueText));
        }
    }

    public string AgcValueText => SelectedSlice == null ? "" : AgcValue.ToString(CultureInfo.CurrentCulture);

    public double AgcTargetDb
    {
        get => _prefs.AgcTargetDb;
        set
        {
            value = Math.Clamp(Math.Round(value), -60, -6);
            if (_prefs.AgcTargetDb == value) return;
            _prefs.AgcTargetDb = value;
            _agc.TargetDb = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<AgcTCalibrator.Point> AgcCurve { get; private set; } = Array.Empty<AgcTCalibrator.Point>();
    public int AgcRecommended => _agc.Recommended;
    public bool AgcRunning => _agc.IsRunning || _agcWaitingForAudio || _agcPreparingSweep;
    public int AgcPercent => _agc.Percent;

    public string AgcResultText
    {
        get
        {
            if (_agcWaitingForAudio) return "Waiting for slice audio…";
            if (_agcPreparingSweep) return "Temporarily disabling noise processing and allowing the DSP to settle…";
            if (_agc.IsRunning) return $"Sweeping… {_agc.Percent}%";
            if (_agc.Recommended >= 0)
                return $"Best AGC-T: {_agc.Recommended}  ({(_agc.RecommendedIsKnee ? "knee" : "target level")}). " +
                       $"It's applied now: Keep it, or Restore to go back to {_agc.OriginalValue}.";
            return "Tune to a clear spot with no signals, then press Find best AGC-T.";
        }
    }

    public string AgcWarning
    {
        get
        {
            if (SelectedSlice is not { } s) return "";
            var on = AgcFilterKeys
                .Where(k => IsOnValue(s.State.GetValueOrDefault(k.StatusKey)))
                .Select(k => k.StatusKey.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (_agc.IsRunning || _agcWaitingForAudio || _agcPreparingSweep)
                return "Noise reduction / blanker / auto-notch functions are temporarily disabled for the AGC-T scan and will be restored automatically.";
            return on.Count > 0
                ? $"ℹ Best AGC-T will temporarily disable {string.Join(", ", on)} and restore the previous settings when the scan finishes."
                : "";
        }
    }

    static bool IsOnValue(string? value) => value == "1"
        || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
