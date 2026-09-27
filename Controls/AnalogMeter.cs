using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FlexCompanion.Controls;

/// <summary>
/// Classic moving-needle meter: arc scale with major/minor ticks, red zone and needle
/// ballistics (fast attack, slower fall) plus a delayed-release peak marker.
/// ScaleLabels uses the same "value:label,value:label" format as LevelMeter.
/// </summary>
public sealed class AnalogMeter : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(AnalogMeter), new FrameworkPropertyMetadata(double.NaN, OnTargetChanged));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(AnalogMeter), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender, OnScaleChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(AnalogMeter), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender, OnScaleChanged));
    public static readonly DependencyProperty RedFromProperty = DependencyProperty.Register(
        nameof(RedFrom), typeof(double), typeof(AnalogMeter), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender, OnFaceChanged));
    public static readonly DependencyProperty ScaleLabelsProperty = DependencyProperty.Register(
        nameof(ScaleLabels), typeof(string), typeof(AnalogMeter), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnScaleChanged));
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AnalogMeter), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender, OnFaceChanged));
    public static readonly DependencyProperty IsTransmitProperty = DependencyProperty.Register(
        nameof(IsTransmit), typeof(bool), typeof(AnalogMeter), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnFaceChanged));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double RedFrom { get => (double)GetValue(RedFromProperty); set => SetValue(RedFromProperty, value); }
    public string ScaleLabels { get => (string)GetValue(ScaleLabelsProperty); set => SetValue(ScaleLabelsProperty, value); }
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public bool IsTransmit { get => (bool)GetValue(IsTransmitProperty); set => SetValue(IsTransmitProperty, value); }

    const double HalfSweepDeg = 48;     // needle travel either side of vertical
    const double MaximumHorizontalStretch = 1.9;
    const double PreferredWidth = 440;
    const double PeakHoldSeconds = 0.85;
    const double PeakReleaseTauSeconds = 0.65;

    static readonly Brush FaceBrush = Frozen(new LinearGradientBrush(Color.FromRgb(0x16, 0x22, 0x33), Color.FromRgb(0x0A, 0x10, 0x1A), 90));
    static readonly Pen BezelPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0x40, 0x50))), 1));
    static readonly Pen ArcPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0xA8, 0xC0))), 1.4));
    static readonly Pen RedArcPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D))), 3.2));
    static readonly Pen MajorPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xD8, 0xE8))), 1.4));
    static readonly Pen MinorPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x50, 0x60, 0x70))), 1));
    static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xD8, 0xE8)));
    static readonly Brush RedLabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x7A)));
    static readonly Brush TitleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0xA8, 0xC0)));
    static readonly Brush TxTitleBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x4D)));
    static readonly Pen NeedlePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x4D))), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen NeedleShadowPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0))), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Pen PeakPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x8A))), 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    static readonly Brush HubBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x20, 0x30, 0x40)));
    static readonly Typeface Face = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface TitleFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    readonly List<(double Value, string Text)> _labels = new();
    double _shown = double.NaN;    // animated needle position (value units)
    double _peakShown = double.NaN;
    DateTime _peakHoldUntilUtc = DateTime.MinValue;
    DateTime _lastFrame = DateTime.UtcNow;
    bool _animating;
    DrawingGroup? _faceCache;
    double _faceCacheW = -1, _faceCacheH = -1;
    bool _faceDirty = true;

    public AnalogMeter()
    {
        Loaded += (_, _) => StartAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    static void OnScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var m = (AnalogMeter)d;
        m._labels.Clear();
        foreach (var part in (m.ScaleLabels ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':', 2);
            if (kv.Length == 2 && double.TryParse(kv[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                m._labels.Add((v, kv[1].Trim()));
        }
        m._labels.Sort((a, b) => a.Value.CompareTo(b.Value));
        m._faceDirty = true;
        // Scale changed (RX <-> TX): jump the needle to the bottom so it swings up naturally.
        m._shown = m.Minimum;
        m._peakShown = m.Minimum;
        m._peakHoldUntilUtc = DateTime.MinValue;
        m.StartAnimation();
    }

    static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AnalogMeter)d).StartAnimation();
    static void OnFaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var m = (AnalogMeter)d;
        m._faceDirty = true;
        m.InvalidateVisual();
    }

    void StartAnimation()
    {
        if (_animating || !IsLoaded) return;
        _animating = true;
        _lastFrame = DateTime.UtcNow;
        CompositionTarget.Rendering += OnFrame;
    }

    void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.1);
        _lastFrame = now;

        double lo = Math.Min(Minimum, Maximum), hi = Math.Max(Minimum, Maximum);   // min/max can cross while RX <-> TX swaps
        double target = double.IsNaN(Value) ? Minimum : Math.Max(lo, Math.Min(hi, Value));
        if (double.IsNaN(_shown)) _shown = Minimum;
        double diff = target - _shown;
        // Fast rise (~60 ms), slower fall (~250 ms) like a real meter movement.
        double tau = diff > 0 ? 0.06 : 0.25;
        _shown += diff * (1 - Math.Exp(-dt / tau));

        double span = Math.Max(1e-9, hi - lo);
        bool needleMoving = Math.Abs(target - _shown) >= span * 0.001;
        if (!needleMoving) _shown = target;
        bool peakMoving = UpdatePeak(target, span, now, dt);

        InvalidateVisual();
        if (!needleMoving && !peakMoving) StopAnimation();
    }

    bool UpdatePeak(double target, double span, DateTime now, double dt)
    {
        double tolerance = span * 0.001;
        if (double.IsNaN(_peakShown) || target > _peakShown + tolerance)
        {
            _peakShown = target;
            _peakHoldUntilUtc = now.AddSeconds(PeakHoldSeconds);
            return false;
        }

        if (_peakShown <= target + tolerance)
        {
            _peakShown = target;
            return false;
        }

        if (now < _peakHoldUntilUtc) return true;
        _peakShown += (target - _peakShown) * (1 - Math.Exp(-dt / PeakReleaseTauSeconds));
        if (Math.Abs(_peakShown - target) < tolerance)
        {
            _peakShown = target;
            return false;
        }
        return true;
    }

    double Frac(double v)
    {
        double span = Maximum - Minimum;
        if (double.IsNaN(v) || span <= 0) return 0;
        return Math.Clamp((v - Minimum) / span, 0, 1);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? PreferredWidth : availableSize.Width;
        return new Size(Math.Min(w, PreferredWidth), 128);
    }

    static (double Radius, double HorizontalStretch, Point Pivot) MeterGeometry(double w, double h)
    {
        double sinSweep = Math.Sin(HalfSweepDeg * Math.PI / 180);
        double widthLimitedRadius = Math.Max(1, (w / 2 - 14) / sinSweep);
        double radius = Math.Min(h * 1.05, widthLimitedRadius);
        double availableStretch = (w / 2 - 14) / Math.Max(1, radius * sinSweep);
        double horizontalStretch = Math.Clamp(availableStretch, 1, MaximumHorizontalStretch);
        return (radius, horizontalStretch, new Point(w / 2, 14 + radius));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 40) return;

        if (_faceDirty || _faceCache == null || Math.Abs(_faceCacheW - w) > 0.5 || Math.Abs(_faceCacheH - h) > 0.5)
        {
            _faceCache = BuildFace(w, h);
            _faceCacheW = w;
            _faceCacheH = h;
            _faceDirty = false;
        }
        dc.DrawDrawing(_faceCache);

        // Only the needle moves at display refresh rate. The bezel, gradients, ticks,
        // labels and title above are cached, avoiding dozens of text/geometry objects
        // per frame per radio.
        var faceRect = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.PushClip(new RectangleGeometry(faceRect, 5, 5));
        var (radius, horizontalStretch, pivot) = MeterGeometry(w, h);
        Point At(double frac, double r)
        {
            double a = (-HalfSweepDeg + frac * 2 * HalfSweepDeg) * Math.PI / 180;
            return new Point(pivot.X + r * horizontalStretch * Math.Sin(a), pivot.Y - r * Math.Cos(a));
        }
        double arcR = radius - 20;
        double nf = Frac(double.IsNaN(_shown) ? Minimum : _shown);
        var tip = At(nf, arcR + 8);
        double ang = (-HalfSweepDeg + nf * 2 * HalfSweepDeg) * Math.PI / 180;
        double baseR = (pivot.Y - (h - 4)) / Math.Max(0.2, Math.Cos(ang));
        var baseP = At(nf, baseR);
        var shadowOffset = new Vector(1.5, 1.5);
        dc.DrawLine(NeedleShadowPen, baseP + shadowOffset, tip + shadowOffset);
        dc.DrawLine(NeedlePen, baseP, tip);
        double peakFraction = Frac(double.IsNaN(_peakShown) ? _shown : _peakShown);
        dc.DrawLine(PeakPen, At(peakFraction, arcR - 5), At(peakFraction, arcR + 10));
        dc.Pop();
    }

    DrawingGroup BuildFace(double w, double h)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var faceRect = new Rect(0.5, 0.5, w - 1, h - 1);
            dc.DrawRoundedRectangle(FaceBrush, BezelPen, faceRect, 5, 5);
            dc.PushClip(new RectangleGeometry(faceRect, 5, 5));

            var (radius, horizontalStretch, pivot) = MeterGeometry(w, h);
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Point At(double frac, double r)
            {
                double a = (-HalfSweepDeg + frac * 2 * HalfSweepDeg) * Math.PI / 180;
                return new Point(pivot.X + r * horizontalStretch * Math.Sin(a), pivot.Y - r * Math.Cos(a));
            }
            double arcR = radius - 20;
            double redFrac = double.IsNaN(RedFrom) ? 1 : Frac(RedFrom);
            DrawArc(dc, ArcPen, arcR, horizontalStretch, 0, redFrac, At);
            if (redFrac < 1) DrawArc(dc, RedArcPen, arcR + 1.5, horizontalStretch, redFrac, 1, At);

            for (int i = 0; i < _labels.Count; i++)
            {
                var (v, text) = _labels[i];
                double f = Frac(v);
                dc.DrawLine(MajorPen, At(f, arcR), At(f, arcR + 9));
                if (i + 1 < _labels.Count)
                {
                    double f2 = Frac(_labels[i + 1].Value);
                    for (int k = 1; k < 4; k++)
                    {
                        double fm = f + (f2 - f) * k / 4;
                        dc.DrawLine(MinorPen, At(fm, arcR), At(fm, arcR + (k == 2 ? 6 : 4)));
                    }
                }
                var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 10.5,
                                           f >= redFrac - 1e-6 && redFrac < 1 ? RedLabelBrush : LabelBrush, dpi);
                var pt = At(f, arcR + 17);
                dc.DrawText(ft, new Point(pt.X - ft.Width / 2, pt.Y - ft.Height / 2));
            }

            if (!string.IsNullOrEmpty(Title))
            {
                var tt = new FormattedText(Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TitleFace, 11,
                                           IsTransmit ? TxTitleBrush : TitleBrush, dpi);
                dc.DrawText(tt, new Point(10, h - tt.Height - 6));
            }
            dc.DrawRoundedRectangle(HubBrush, BezelPen, new Rect(w / 2 - 22, h - 9, 44, 16), 6, 6);
            dc.Pop();
        }
        group.Freeze();
        return group;
    }

    static void DrawArc(DrawingContext dc, Pen pen, double r, double horizontalStretch,
                        double f0, double f1, Func<double, double, Point> at)
    {
        if (f1 <= f0) return;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(at(f0, r), false, false);
            ctx.ArcTo(at(f1, r), new Size(r * horizontalStretch, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}
