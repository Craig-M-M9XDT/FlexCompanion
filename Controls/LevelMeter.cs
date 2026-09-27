using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FlexCompanion.Controls;

/// <summary>
/// Segmented bar meter with peak hold and an optional scale ("value:label,value:label").
/// Colour runs BaseColor -> amber at WarnValue -> red at DangerValue.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    public static readonly DependencyProperty WarnValueProperty = DependencyProperty.Register(
        nameof(WarnValue), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    public static readonly DependencyProperty DangerValueProperty = DependencyProperty.Register(
        nameof(DangerValue), typeof(double), typeof(LevelMeter),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    public static readonly DependencyProperty BaseColorProperty = DependencyProperty.Register(
        nameof(BaseColor), typeof(Color), typeof(LevelMeter),
        new FrameworkPropertyMetadata(Color.FromRgb(0x4D, 0xD8, 0x7A), FrameworkPropertyMetadataOptions.AffectsRender, OnLookChanged));

    public static readonly DependencyProperty ScaleLabelsProperty = DependencyProperty.Register(
        nameof(ScaleLabels), typeof(string), typeof(LevelMeter),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnScaleChanged));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double WarnValue { get => (double)GetValue(WarnValueProperty); set => SetValue(WarnValueProperty, value); }
    public double DangerValue { get => (double)GetValue(DangerValueProperty); set => SetValue(DangerValueProperty, value); }
    public Color BaseColor { get => (Color)GetValue(BaseColorProperty); set => SetValue(BaseColorProperty, value); }
    public string ScaleLabels { get => (string)GetValue(ScaleLabelsProperty); set => SetValue(ScaleLabelsProperty, value); }

    static readonly Brush TrackBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x08, 0x0C, 0x14)));
    static readonly Brush GapBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x08, 0x0C, 0x14)));
    static readonly Brush PeakBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xF0, 0xFA)));
    static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0xA8, 0xC0)));
    static readonly Pen BorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0x40, 0x50)), 1));
    static readonly Pen TickPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x50, 0x60, 0x70)), 1));
    static readonly Typeface LabelFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Color Amber = Color.FromRgb(0xFF, 0xB8, 0x4D);
    static readonly Color Red = Color.FromRgb(0xFF, 0x4D, 0x4D);

    readonly List<(double Value, string Text)> _labels = new();
    Brush? _fill;
    double _peak = double.NaN;
    DateTime _peakTime;

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    static void OnLookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((LevelMeter)d)._fill = null;

    static void OnScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var m = (LevelMeter)d;
        m._labels.Clear();
        foreach (var part in ((string?)e.NewValue ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':', 2);
            if (kv.Length == 2 && double.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                m._labels.Add((v, kv[1].Trim()));
        }
    }

    static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var m = (LevelMeter)d;
        var v = (double)e.NewValue;
        var now = DateTime.UtcNow;
        if (double.IsNaN(v)) { m._peak = double.NaN; return; }
        if (double.IsNaN(m._peak) || v >= m._peak) { m._peak = v; m._peakTime = now; }
        else if ((now - m._peakTime).TotalMilliseconds > 1200)
            m._peak = Math.Max(v, m._peak - (m.Maximum - m.Minimum) * 0.015);
    }

    double Frac(double v)
    {
        if (double.IsNaN(v) || Maximum <= Minimum) return 0;
        return Math.Clamp((v - Minimum) / (Maximum - Minimum), 0, 1);
    }

    Brush GetFill()
    {
        if (_fill != null) return _fill;
        double warn = double.IsNaN(WarnValue) ? 0.75 : Frac(WarnValue);
        double danger = double.IsNaN(DangerValue) ? 0.92 : Frac(DangerValue);
        if (danger < warn) danger = warn;
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        b.GradientStops.Add(new GradientStop(BaseColor, 0));
        b.GradientStops.Add(new GradientStop(BaseColor, warn));
        b.GradientStops.Add(new GradientStop(Amber, Math.Min(1, warn + 0.02)));
        b.GradientStops.Add(new GradientStop(Red, danger));
        b.GradientStops.Add(new GradientStop(Red, 1));
        b.Freeze();
        return _fill = b;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(0, _labels.Count > 0 ? 28 : 12);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 6 || h < 4) return;

        bool hasScale = _labels.Count > 0;
        double barH = hasScale ? Math.Max(6, h - 13) : h;

        dc.DrawRoundedRectangle(TrackBrush, BorderPen, new Rect(0.5, 0.5, w - 1, barH - 1), 2, 2);

        var inner = new Rect(2, 2, Math.Max(0, w - 4), Math.Max(0, barH - 4));
        double f = Frac(Value);
        if (f > 0 && inner.Width > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(inner.X, inner.Y, inner.Width * f, inner.Height)));
            dc.DrawRectangle(GetFill(), null, inner);
            dc.Pop();
        }

        // LED segment gaps
        for (double x = inner.X + 3; x < inner.Right; x += 4)
            dc.DrawRectangle(GapBrush, null, new Rect(x, inner.Y, 1, inner.Height));

        if (!double.IsNaN(_peak) && Frac(_peak) > 0)
        {
            double px = inner.X + inner.Width * Frac(_peak);
            dc.DrawRectangle(PeakBrush, null, new Rect(Math.Min(px, inner.Right - 2), inner.Y, 2, inner.Height));
        }

        if (!hasScale) return;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var (value, text) in _labels)
        {
            double x = inner.X + inner.Width * Frac(value);
            dc.DrawLine(TickPen, new Point(x, barH), new Point(x, barH + 2));
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, 9.5, LabelBrush, dip);
            double tx = Math.Clamp(x - ft.Width / 2, 0, Math.Max(0, w - ft.Width));
            dc.DrawText(ft, new Point(tx, barH + 1.5));
        }
    }
}
