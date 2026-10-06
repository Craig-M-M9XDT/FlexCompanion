using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FlexCompanion.Controls;

/// <summary>
/// Lightweight touch-build spectrum. It intentionally draws only the trace/grid; there is no
/// waterfall bitmap or extra animation. Aether shared-pan frames are already radio FFT bins;
/// DAX-IQ fallback frames are generated on the background worker in IqAnalyzer.
/// </summary>
public sealed class SpectrumView : Control
{
    public static readonly StyledProperty<float[]?> SpectrumProperty =
        AvaloniaProperty.Register<SpectrumView, float[]?>(nameof(Spectrum));
    public static readonly StyledProperty<double> MaxHzProperty =
        AvaloniaProperty.Register<SpectrumView, double>(nameof(MaxHz), 24000.0);
    public static readonly StyledProperty<double> SampleRateProperty =
        AvaloniaProperty.Register<SpectrumView, double>(nameof(SampleRate), 48000.0);
    public static readonly StyledProperty<bool> CenteredProperty =
        AvaloniaProperty.Register<SpectrumView, bool>(nameof(Centered), true);
    public static readonly StyledProperty<double> MarkerFractionProperty =
        AvaloniaProperty.Register<SpectrumView, double>(nameof(MarkerFraction), double.NaN);

    static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#080C14"));
    static readonly IBrush GridBrush = new SolidColorBrush(Color.Parse("#243445"));
    static readonly IBrush TraceBrush = new SolidColorBrush(Color.Parse("#00B4D8"));
    static readonly IBrush MarkerBrush = new SolidColorBrush(Color.Parse("#FFB84D"));
    static readonly Pen BorderPen = new(new SolidColorBrush(Color.Parse("#304050")), 1);
    static readonly Pen GridPen = new(GridBrush, 1);
    static readonly Pen TracePen = new(TraceBrush, 1.35);
    static readonly Pen MarkerPen = new(MarkerBrush, 1);

    double _floor = double.NaN;

    static SpectrumView()
    {
        AffectsRender<SpectrumView>(SpectrumProperty, MaxHzProperty, SampleRateProperty,
            CenteredProperty, MarkerFractionProperty);
    }

    public float[]? Spectrum
    {
        get => GetValue(SpectrumProperty);
        set => SetValue(SpectrumProperty, value);
    }

    public double MaxHz
    {
        get => GetValue(MaxHzProperty);
        set => SetValue(MaxHzProperty, value);
    }

    public double SampleRate
    {
        get => GetValue(SampleRateProperty);
        set => SetValue(SampleRateProperty, value);
    }

    public bool Centered
    {
        get => GetValue(CenteredProperty);
        set => SetValue(CenteredProperty, value);
    }

    public double MarkerFraction
    {
        get => GetValue(MarkerFractionProperty);
        set => SetValue(MarkerFractionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SpectrumProperty && Spectrum is { Length: >= 16 } s)
            UpdateFloor(s);
    }

    void UpdateFloor(float[] spectrum)
    {
        const float lo = -180f, hi = 20f;
        Span<int> hist = stackalloc int[96];
        foreach (float raw in spectrum)
        {
            float v = float.IsFinite(raw) ? Math.Clamp(raw, lo, hi) : lo;
            int b = Math.Clamp((int)((v - lo) * (hist.Length - 1) / (hi - lo)), 0, hist.Length - 1);
            hist[b]++;
        }
        int target = Math.Max(1, spectrum.Length / 5);
        int sum = 0, bucket = 0;
        for (; bucket < hist.Length; bucket++)
        {
            sum += hist[bucket];
            if (sum >= target) break;
        }
        double f = lo + (bucket + 0.5) * (hi - lo) / hist.Length;
        _floor = double.IsNaN(_floor) ? f : _floor + (f - _floor) * 0.08;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 24 || h < 24) return;

        var rect = new Rect(0.5, 0.5, Math.Max(1, w - 1), Math.Max(1, h - 1));
        context.DrawRectangle(BackgroundBrush, BorderPen, rect);

        for (int i = 1; i < 4; i++)
        {
            double x = Math.Round(w * i / 4.0) + 0.5;
            double y = Math.Round(h * i / 4.0) + 0.5;
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, h));
            context.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }

        var s = Spectrum;
        if (s is { Length: >= 2 })
        {
            double bottom = (double.IsNaN(_floor) ? -110 : _floor) - 10;
            const double rangeDb = 70;
            int n = s.Length;
            int pixels = Math.Min(Math.Max(2, (int)Math.Round(w)), 360);
            int stride = Math.Max(2, n / pixels);

            Point? previous = null;
            for (int i = 0; i < n; i += stride)
            {
                float peak = s[i];
                for (int j = 1; j < stride && i + j < n; j++) peak = Math.Max(peak, s[i + j]);
                double x = i / (double)Math.Max(1, n - 1) * w;
                double y = h - Math.Clamp((peak - bottom) / rangeDb, 0, 1) * Math.Max(1, h - 3);
                var p = new Point(x, y);
                if (previous is Point p0) context.DrawLine(TracePen, p0, p);
                previous = p;
            }
        }

        if (Centered && double.IsFinite(MarkerFraction) && MarkerFraction is >= 0 and <= 1)
        {
            double x = Math.Round(MarkerFraction * w) + 0.5;
            context.DrawLine(MarkerPen, new Point(x, 0), new Point(x, h));
        }
    }
}
