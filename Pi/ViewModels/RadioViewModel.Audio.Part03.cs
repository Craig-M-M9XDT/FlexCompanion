using System.Globalization;
using FlexCompanion.Flex;
using FlexCompanion.Services;

namespace FlexCompanion.ViewModels;

public sealed partial class RadioViewModel
{

    float[] ResliceAetherPan(AetherPanBridge.PanFrame frame)
    {
        const int outputBins = 320;
        var source = frame.Bins;
        if (source.Length < 2) return source;

        if (SelectedSlice is not { } slice
            || !_pans.TryGetValue(slice.Pan, out var pan)
            || Kv.D(pan.GetValueOrDefault("center")) is not double panCenterMhz
            || Kv.D(pan.GetValueOrDefault("bandwidth")) is not double panBandwidthMhz
            || panBandwidthMhz <= 0
            || Kv.D(slice.State.GetValueOrDefault("RF_frequency")) is not double sliceMhz)
        {
            SpectrumMaxHz = FftSpanKhz * 500.0;
            SpectrumCenterOffsetHz = 0;
            SpectrumSliceFraction = double.NaN;
            return ResampleLinear(source, outputBins, 0, source.Length - 1, -160f);
        }

        var window = AetherSpectrumWindow;
        double wantedBandwidthMhz = window.SpanHz / 1e6;
        double panLowMhz = panCenterMhz - panBandwidthMhz * 0.5;
        double wantLowMhz = sliceMhz + window.CentreOffsetHz / 1e6 - wantedBandwidthMhz * 0.5;

        float floor = float.PositiveInfinity;
        for (int i = 0; i < source.Length; i++)
            if (float.IsFinite(source[i])) floor = MathF.Min(floor, source[i]);
        if (!float.IsFinite(floor)) floor = -160f;
        floor = MathF.Max(-180f, floor);

        var output = new float[outputBins];
        int n = source.Length;
        for (int i = 0; i < outputBins; i++)
        {
            double mhz = wantLowMhz + wantedBandwidthMhz * i / (outputBins - 1.0);
            double pos = (mhz - panLowMhz) / panBandwidthMhz * (n - 1.0);
            if (pos < 0 || pos > n - 1)
            {
                output[i] = floor;
                continue;
            }
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, n - 1);
            double f = pos - i0;
            output[i] = (float)(source[i0] * (1.0 - f) + source[i1] * f);
        }

        SpectrumMaxHz = window.SpanHz * 0.5;
        SpectrumCenterOffsetHz = window.CentreOffsetHz;
        double marker = 0.5 - window.CentreOffsetHz / window.SpanHz;
        SpectrumSliceFraction = marker is >= 0 and <= 1 ? marker : double.NaN;
        return output;
    }

    static float[] ResampleLinear(float[] source, int count, double start, double end, float floor)
    {
        if (source.Length == 0 || count <= 0) return Array.Empty<float>();
        var output = new float[count];
        if (source.Length == 1)
        {
            Array.Fill(output, source[0]);
            return output;
        }
        for (int i = 0; i < count; i++)
        {
            double pos = start + (end - start) * i / Math.Max(1, count - 1.0);
            if (pos < 0 || pos > source.Length - 1) { output[i] = floor; continue; }
            int i0 = (int)pos;
            int i1 = Math.Min(i0 + 1, source.Length - 1);
            double f = pos - i0;
            output[i] = (float)(source[i0] * (1.0 - f) + source[i1] * f);
        }
        return output;
    }

    async Task StopDaxIqForAetherAsync()
    {
        if (_aetherTakeoverBusy) return;
        _aetherTakeoverBusy = true;
        try { await RemoveIqStreamAsync(clearSpectrum: false); }
        finally { _aetherTakeoverBusy = false; }
    }

    async Task EnsureIqStreamAsync()
    {
        if (_iqBusy) { _iqRecheck = true; return; }
        _iqBusy = true;
        try
        {
            do
            {
                _iqRecheck = false;
                await EnsureIqStreamCoreAsync();
            } while (_iqRecheck);
        }
        finally { _iqBusy = false; }
    }
}
