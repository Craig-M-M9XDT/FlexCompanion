using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlexCompanion.Controls;

/// <summary>
/// Compact spectrum renderer. The default Aether-style mini-pan mode is trace-only;
/// an optional waterfall remains available for callers that want it. Spectrum values
/// may be radio pan dBm bins or Companion's fallback DAX-IQ dB bins.
/// </summary>
public sealed class SpectrumView : FrameworkElement
{
    public static readonly DependencyProperty SpectrumProperty = DependencyProperty.Register(
        nameof(Spectrum), typeof(float[]), typeof(SpectrumView), new FrameworkPropertyMetadata(null, OnSpectrum));
    public static readonly DependencyProperty MaxHzProperty = DependencyProperty.Register(
        nameof(MaxHz), typeof(double), typeof(SpectrumView), new FrameworkPropertyMetadata(3000.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SampleRateProperty = DependencyProperty.Register(
        nameof(SampleRate), typeof(double), typeof(SpectrumView), new FrameworkPropertyMetadata(24000.0));
    public static readonly DependencyProperty CenteredProperty = DependencyProperty.Register(
        nameof(Centered), typeof(bool), typeof(SpectrumView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowWaterfallProperty = DependencyProperty.Register(
        nameof(ShowWaterfall), typeof(bool), typeof(SpectrumView), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenterOffsetHzProperty = DependencyProperty.Register(
        nameof(CenterOffsetHz), typeof(double), typeof(SpectrumView), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MarkerFractionProperty = DependencyProperty.Register(
        nameof(MarkerFraction), typeof(double), typeof(SpectrumView), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Centred mode: frequency of the display centre relative to the slice (labels are slice-relative).</summary>
    public double CenterOffsetHz { get => (double)GetValue(CenterOffsetHzProperty); set => SetValue(CenterOffsetHzProperty, value); }
    /// <summary>Centred mode: slice position across the width, 0..1, or NaN to hide the marker.</summary>
    public double MarkerFraction { get => (double)GetValue(MarkerFractionProperty); set => SetValue(MarkerFractionProperty, value); }

    public float[]? Spectrum { get => (float[]?)GetValue(SpectrumProperty); set => SetValue(SpectrumProperty, value); }
    public double MaxHz { get => (double)GetValue(MaxHzProperty); set => SetValue(MaxHzProperty, value); }
    public double SampleRate { get => (double)GetValue(SampleRateProperty); set => SetValue(SampleRateProperty, value); }
    public bool Centered { get => (bool)GetValue(CenteredProperty); set => SetValue(CenteredProperty, value); }
    /// <summary>Aether-style compact mode defaults to trace-only; waterfall is optional.</summary>
    public bool ShowWaterfall { get => (bool)GetValue(ShowWaterfallProperty); set => SetValue(ShowWaterfallProperty, value); }

    const int WfCols = 320, WfRows = 48;
    const double RangeDb = 70;

    static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x08, 0x0C, 0x14)));
    static readonly Pen BorderPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0x40, 0x50))), 1));
    static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x30, 0x40, 0x50))), 1));
    static readonly Pen LinePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x00, 0xB4, 0xD8))), 1.2));
    static readonly Brush FillBrush = Frozen(new LinearGradientBrush(Color.FromArgb(0x90, 0x00, 0xB4, 0xD8), Color.FromArgb(0x08, 0x00, 0xB4, 0xD8), 90));
    static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x50, 0x60, 0x70)));
    static readonly Pen MarkerPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xB8, 0x4D))), 1) { DashStyle = DashStyles.Dash });
    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    readonly WriteableBitmap _wf = new(WfCols, WfRows, 96, 96, PixelFormats.Bgr32, null);
    readonly int[] _wfPixels = new int[WfCols * WfRows];
    static readonly int[] Palette = BuildPalette();
    double _floor = double.NaN;

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    static int[] BuildPalette()
    {
        // dark navy -> accent cyan -> amber -> white
        (double p, Color c)[] stops =
        {
            (0.00, Color.FromRgb(0x08, 0x0C, 0x14)),
            (0.35, Color.FromRgb(0x10, 0x30, 0x60)),
            (0.60, Color.FromRgb(0x00, 0xB4, 0xD8)),
            (0.82, Color.FromRgb(0xFF, 0xB8, 0x4D)),
            (1.00, Color.FromRgb(0xFF, 0xFF, 0xFF)),
        };
        var pal = new int[256];
        for (int i = 0; i < 256; i++)
        {
            double t = i / 255.0;
            int k = 0;
            while (k < stops.Length - 2 && t > stops[k + 1].p) k++;
            var (p0, c0) = stops[k];
            var (p1, c1) = stops[k + 1];
            double u = Math.Clamp((t - p0) / (p1 - p0), 0, 1);
            byte r = (byte)(c0.R + (c1.R - c0.R) * u), g = (byte)(c0.G + (c1.G - c0.G) * u), b = (byte)(c0.B + (c1.B - c0.B) * u);
            pal[i] = (r << 16) | (g << 8) | b;
        }
        return pal;
    }

    static void OnSpectrum(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (SpectrumView)d;
        if (v.Spectrum is { Length: >= 16 } s)
            v.UpdateFloor(s, v.VisibleBins(s));
        if (v.ShowWaterfall) v.PushWaterfall();
        v.InvalidateVisual();
    }

    int VisibleBins(float[] s)
    {
        if (Centered) return s.Length;
        int n = (s.Length - 1) * 2;
        double binHz = SampleRate / n;
        return Math.Clamp((int)(MaxHz / binHz), 8, s.Length);
    }

    void UpdateFloor(float[] s, int bins)
    {
        // Approximate the 20th percentile with a fixed histogram. This avoids allocating
        // and sorting a temporary float array every rendered frame (twice in dual-radio).
        const float lo = -180f, hi = 20f;
        Span<int> hist = stackalloc int[128];
        int count = Math.Min(bins, s.Length);
        if (count <= 0) return;
        for (int i = 0; i < count; i++)
        {
            float v = float.IsFinite(s[i]) ? Math.Clamp(s[i], lo, hi) : lo;
            int b = Math.Clamp((int)((v - lo) * (hist.Length - 1) / (hi - lo)), 0, hist.Length - 1);
            hist[b]++;
        }
        int target = Math.Max(1, count / 5);
        int accum = 0, bucket = 0;
        for (; bucket < hist.Length; bucket++)
        {
            accum += hist[bucket];
            if (accum >= target) break;
        }
        double f = lo + (bucket + 0.5) * (hi - lo) / hist.Length;
        _floor = double.IsNaN(_floor) ? f : _floor + (f - _floor) * 0.05;
    }

    double Bottom => (double.IsNaN(_floor) ? -110 : _floor) - 10;

    void PushWaterfall()
    {
        var s = Spectrum;
        if (s == null || s.Length < 16) return;
        int bins = VisibleBins(s);
        UpdateFloor(s, bins);
        Array.Copy(_wfPixels, 0, _wfPixels, WfCols, WfCols * (WfRows - 1));   // scroll down one row
        double bottom = Bottom;
        for (int x = 0; x < WfCols; x++)
        {
            int b0 = x * bins / WfCols, b1 = Math.Max(b0 + 1, (x + 1) * bins / WfCols);
            float m = float.MinValue;
            for (int b = b0; b < b1 && b < s.Length; b++) m = Math.Max(m, s[b]);
            int idx = (int)Math.Clamp((m - bottom) / RangeDb * 255, 0, 255);
            _wfPixels[x] = Palette[idx];
        }
        _wf.WritePixels(new Int32Rect(0, 0, WfCols, WfRows), _wfPixels, WfCols * 4, 0);
    }

    static string FormatOffset(double hz)
    {
        if (Math.Abs(hz) < 0.5) return "0";
        double k = hz / 1000.0;
        string num = Math.Abs(k) >= 10 || Math.Abs(k - Math.Round(k)) < 0.05 ? k.ToString("+0;-0", CultureInfo.InvariantCulture)
                                                                             : k.ToString("+0.0#;-0.0#", CultureInfo.InvariantCulture);
        return num + "k";
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(0, double.IsInfinity(availableSize.Height) ? 110 : Math.Min(availableSize.Height, 110));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 30) return;
        var rect = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.DrawRoundedRectangle(BgBrush, BorderPen, rect, 3, 3);
        dc.PushClip(new RectangleGeometry(rect, 3, 3));

        double specH = ShowWaterfall ? Math.Round(h * 0.58) : h - 1;
        double wfTop = specH + 1;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Frequency grid. DAX IQ is displayed centred around the selected pan/slice.
        if (Centered)
        {
            // Labels are relative to the selected slice; the span is 2 x MaxHz.
            for (int k = -2; k <= 2; k++)
            {
                double frac = (k + 2) / 4.0;
                double x = Math.Round(frac * w) + 0.5;
                dc.DrawLine(GridPen, new Point(x, 0), new Point(x, specH));
                double hz = k * MaxHz / 2.0 + CenterOffsetHz;
                var ft = new FormattedText(FormatOffset(hz), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 9, LabelBrush, dpi);
                double tx = k == 2 ? x - ft.Width - 2 : x + 2;
                dc.DrawText(ft, new Point(Math.Clamp(tx, 1, Math.Max(1, w - ft.Width - 1)), 1));
            }
        }
        else
        {
            for (double hz = 500; hz < MaxHz; hz += 500)
            {
                double x = Math.Round(hz / MaxHz * w) + 0.5;
                dc.DrawLine(GridPen, new Point(x, 0), new Point(x, specH));
                if (hz % 1000 == 0)
                {
                    var ft = new FormattedText($"{hz / 1000:0}k", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 9, LabelBrush, dpi);
                    dc.DrawText(ft, new Point(x + 2, 1));
                }
            }
        }
        for (int k = 1; k < 4; k++)
        {
            double y = Math.Round(specH * k / 4) + 0.5;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }

        var s = Spectrum;
        if (s != null && s.Length >= 16)
        {
            int bins = VisibleBins(s);
            double bottom = Bottom;
            double binHz = Centered ? SampleRate / s.Length : SampleRate / ((s.Length - 1) * 2);
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(0, specH), true, true);
                int step = Math.Max(1, bins / (int)Math.Max(1, w));
                for (int b = 0; b < bins; b += step)
                {
                    float m = s[b];
                    for (int j = 1; j < step && b + j < bins; j++) m = Math.Max(m, s[b + j]);
                    double x = Centered ? b / (double)Math.Max(1, bins - 1) * w
                                        : b * binHz / MaxHz * w;
                    double y = specH - Math.Clamp((m - bottom) / RangeDb, 0, 1) * (specH - 4);
                    ctx.LineTo(new Point(x, y), true, true);
                }
                ctx.LineTo(new Point(w, specH), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(FillBrush, LinePen, g);
        }
        else
        {
            var ft = new FormattedText("no spectrum", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 10, LabelBrush, dpi);
            dc.DrawText(ft, new Point((w - ft.Width) / 2, (specH - ft.Height) / 2));
        }

        if (Centered && !double.IsNaN(MarkerFraction))
        {
            double mx = Math.Round(MarkerFraction * w) + 0.5;
            dc.DrawLine(MarkerPen, new Point(mx, 0), new Point(mx, h));
        }

        if (ShowWaterfall)
        {
            dc.DrawLine(BorderPen, new Point(0, specH + 0.5), new Point(w, specH + 0.5));
            dc.DrawImage(_wf, new Rect(0, wfTop, w, h - wfTop));
        }
        dc.Pop();
    }
}
