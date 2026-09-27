using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FlexCompanion.Controls;

/// <summary>
/// Three independently scaled needles on one cached analogue face. Each input is
/// normalised against its own range, allowing unlike TX measurements to share a dial.
/// </summary>
public sealed class MultiAnalogMeter : FrameworkElement
{
    public static readonly DependencyProperty Value1Property = ValueProperty(nameof(Value1));
    public static readonly DependencyProperty Value2Property = ValueProperty(nameof(Value2));
    public static readonly DependencyProperty Value3Property = ValueProperty(nameof(Value3));
    public static readonly DependencyProperty Minimum1Property = FaceProperty(nameof(Minimum1), 0.0);
    public static readonly DependencyProperty Minimum2Property = FaceProperty(nameof(Minimum2), 0.0);
    public static readonly DependencyProperty Minimum3Property = FaceProperty(nameof(Minimum3), 0.0);
    public static readonly DependencyProperty Maximum1Property = FaceProperty(nameof(Maximum1), 100.0);
    public static readonly DependencyProperty Maximum2Property = FaceProperty(nameof(Maximum2), 100.0);
    public static readonly DependencyProperty Maximum3Property = FaceProperty(nameof(Maximum3), 100.0);
    public static readonly DependencyProperty RedFrom1Property = FaceProperty(nameof(RedFrom1), double.NaN);
    public static readonly DependencyProperty RedFrom2Property = FaceProperty(nameof(RedFrom2), double.NaN);
    public static readonly DependencyProperty RedFrom3Property = FaceProperty(nameof(RedFrom3), double.NaN);
    public static readonly DependencyProperty Label1Property = FaceProperty(nameof(Label1), "A");
    public static readonly DependencyProperty Label2Property = FaceProperty(nameof(Label2), "B");
    public static readonly DependencyProperty Label3Property = FaceProperty(nameof(Label3), "C");

    public double Value1 { get => (double)GetValue(Value1Property); set => SetValue(Value1Property, value); }
    public double Value2 { get => (double)GetValue(Value2Property); set => SetValue(Value2Property, value); }
    public double Value3 { get => (double)GetValue(Value3Property); set => SetValue(Value3Property, value); }
    public double Minimum1 { get => (double)GetValue(Minimum1Property); set => SetValue(Minimum1Property, value); }
    public double Minimum2 { get => (double)GetValue(Minimum2Property); set => SetValue(Minimum2Property, value); }
    public double Minimum3 { get => (double)GetValue(Minimum3Property); set => SetValue(Minimum3Property, value); }
    public double Maximum1 { get => (double)GetValue(Maximum1Property); set => SetValue(Maximum1Property, value); }
    public double Maximum2 { get => (double)GetValue(Maximum2Property); set => SetValue(Maximum2Property, value); }
    public double Maximum3 { get => (double)GetValue(Maximum3Property); set => SetValue(Maximum3Property, value); }
    public double RedFrom1 { get => (double)GetValue(RedFrom1Property); set => SetValue(RedFrom1Property, value); }
    public double RedFrom2 { get => (double)GetValue(RedFrom2Property); set => SetValue(RedFrom2Property, value); }
    public double RedFrom3 { get => (double)GetValue(RedFrom3Property); set => SetValue(RedFrom3Property, value); }
    public string Label1 { get => (string)GetValue(Label1Property); set => SetValue(Label1Property, value); }
    public string Label2 { get => (string)GetValue(Label2Property); set => SetValue(Label2Property, value); }
    public string Label3 { get => (string)GetValue(Label3Property); set => SetValue(Label3Property, value); }

    const double HalfSweepDeg = 48;
    static readonly Brush FaceBrush = Frozen(new LinearGradientBrush(Color.FromRgb(0x16, 0x22, 0x33), Color.FromRgb(0x0A, 0x10, 0x1A), 90));
    static readonly Pen BezelPen = MakePen(Color.FromRgb(0x30, 0x40, 0x50), 1);
    static readonly Pen TrackPen = MakePen(Color.FromRgb(0x45, 0x56, 0x68), 1.2);
    static readonly Pen OrangePen = MakePen(Color.FromRgb(0xFF, 0xB8, 0x4D), 2.2);
    static readonly Pen GreenPen = MakePen(Color.FromRgb(0x4D, 0xD8, 0x7A), 2.2);
    static readonly Pen CyanPen = MakePen(Color.FromRgb(0x00, 0xB4, 0xD8), 2.2);
    static readonly Pen RedPen = MakePen(Color.FromRgb(0xFF, 0x4D, 0x4D), 2.2);
    static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC8, 0xD8, 0xE8)));
    static readonly Brush HubBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x20, 0x30, 0x40)));
    static readonly Typeface Face = new(new FontFamily("Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    double _shown1 = double.NaN, _shown2 = double.NaN, _shown3 = double.NaN;
    DateTime _lastFrame = DateTime.UtcNow;
    bool _animating, _faceDirty = true;
    DrawingGroup? _faceCache;
    double _faceCacheW = -1, _faceCacheH = -1;

    public MultiAnalogMeter()
    {
        Loaded += (_, _) => StartAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    static DependencyProperty ValueProperty(string name) => DependencyProperty.Register(
        name, typeof(double), typeof(MultiAnalogMeter), new FrameworkPropertyMetadata(double.NaN, OnValueChanged));

    static DependencyProperty FaceProperty(string name, object defaultValue) => DependencyProperty.Register(
        name, defaultValue.GetType(), typeof(MultiAnalogMeter),
        new FrameworkPropertyMetadata(defaultValue, FrameworkPropertyMetadataOptions.AffectsRender, OnFaceChanged));

    static T Frozen<T>(T value) where T : Freezable { value.Freeze(); return value; }

    static Pen MakePen(Color color, double width) => Frozen(new Pen(Frozen(new SolidColorBrush(color)), width)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    });

    static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MultiAnalogMeter)d).StartAnimation();

    static void OnFaceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var meter = (MultiAnalogMeter)d;
        meter._faceDirty = true;
        meter.StartAnimation();
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
        bool moving = Animate(ref _shown1, Value1, Minimum1, Maximum1, dt)
                   | Animate(ref _shown2, Value2, Minimum2, Maximum2, dt)
                   | Animate(ref _shown3, Value3, Minimum3, Maximum3, dt);
        InvalidateVisual();
        if (!moving) StopAnimation();
    }

    static bool Animate(ref double shown, double value, double minimum, double maximum, double dt)
    {
        double lo = Math.Min(minimum, maximum), hi = Math.Max(minimum, maximum);
        double target = double.IsNaN(value) ? lo : Math.Clamp(value, lo, hi);
        if (double.IsNaN(shown)) shown = lo;
        double diff = target - shown;
        double span = Math.Max(1e-9, hi - lo);
        if (Math.Abs(diff) < span * 0.001) { shown = target; return false; }
        shown += diff * (1 - Math.Exp(-dt / (diff > 0 ? 0.06 : 0.25)));
        return true;
    }

    static double Frac(double value, double minimum, double maximum)
    {
        double span = maximum - minimum;
        return double.IsNaN(value) || span <= 0 ? 0 : Math.Clamp((value - minimum) / span, 0, 1);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width;
        return new Size(Math.Min(width, 320), 126);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 60) return;
        if (_faceDirty || _faceCache == null || Math.Abs(w - _faceCacheW) > 0.5 || Math.Abs(h - _faceCacheH) > 0.5)
        {
            _faceCache = BuildFace(w, h);
            _faceCacheW = w; _faceCacheH = h; _faceDirty = false;
        }
        dc.DrawDrawing(_faceCache);

        double radius = Math.Min(h * 1.05, (w / 2 - 18) / Math.Sin(HalfSweepDeg * Math.PI / 180));
        var pivot = new Point(w / 2, 16 + radius);
        DrawNeedle(dc, pivot, radius - 13, Frac(_shown1, Minimum1, Maximum1), OrangePen, -3);
        DrawNeedle(dc, pivot, radius - 27, Frac(_shown2, Minimum2, Maximum2), GreenPen, 0);
        DrawNeedle(dc, pivot, radius - 41, Frac(_shown3, Minimum3, Maximum3), CyanPen, 3);
        dc.DrawRoundedRectangle(HubBrush, BezelPen, new Rect(w / 2 - 25, h - 10, 50, 17), 6, 6);
    }

    void DrawNeedle(DrawingContext dc, Point pivot, double length, double fraction, Pen pen, double baseOffset)
    {
        double angle = (-HalfSweepDeg + fraction * 2 * HalfSweepDeg) * Math.PI / 180;
        var tip = new Point(pivot.X + length * Math.Sin(angle), pivot.Y - length * Math.Cos(angle));
        var start = new Point(pivot.X + baseOffset, ActualHeight - 5);
        dc.DrawLine(pen, start, tip);
    }

    DrawingGroup BuildFace(double w, double h)
    {
        var group = new DrawingGroup();
        // Close the DrawingContext before freezing the cache. Freezing while Open()
        // is still active throws on every render and can create an error-dialog loop.
        using (var dc = group.Open())
        {
            var rect = new Rect(0.5, 0.5, w - 1, h - 1);
            dc.DrawRoundedRectangle(FaceBrush, BezelPen, rect, 5, 5);
            dc.PushClip(new RectangleGeometry(rect, 5, 5));
            double radius = Math.Min(h * 1.05, (w / 2 - 18) / Math.Sin(HalfSweepDeg * Math.PI / 180));
            var pivot = new Point(w / 2, 16 + radius);
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            DrawTrack(dc, pivot, radius - 13, Minimum1, Maximum1, RedFrom1, Label1, OrangePen, dpi);
            DrawTrack(dc, pivot, radius - 27, Minimum2, Maximum2, RedFrom2, Label2, GreenPen, dpi);
            DrawTrack(dc, pivot, radius - 41, Minimum3, Maximum3, RedFrom3, Label3, CyanPen, dpi);
            dc.Pop();
        }
        group.Freeze();
        return group;
    }

    void DrawTrack(DrawingContext dc, Point pivot, double radius, double min, double max, double redFrom,
                   string label, Pen colorPen, double dpi)
    {
        Point At(double fraction)
        {
            double angle = (-HalfSweepDeg + fraction * 2 * HalfSweepDeg) * Math.PI / 180;
            return new Point(pivot.X + radius * Math.Sin(angle), pivot.Y - radius * Math.Cos(angle));
        }
        DrawArc(dc, TrackPen, radius, 0, 1, At);
        double red = double.IsNaN(redFrom) ? 1 : Frac(redFrom, min, max);
        if (red < 1) DrawArc(dc, RedPen, radius, red, 1, At);
        dc.DrawLine(colorPen, At(0), At(0.035));
        dc.DrawLine(colorPen, At(0.965), At(1));
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            Face, 9.5, LabelBrush, dpi);
        var at = At(0.5);
        dc.DrawText(text, new Point(at.X - text.Width / 2, at.Y - text.Height / 2 - 2));
    }

    static void DrawArc(DrawingContext dc, Pen pen, double radius, double f0, double f1, Func<double, Point> at)
    {
        if (f1 <= f0 || radius <= 0) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(at(f0), false, false);
            ctx.ArcTo(at(f1), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }
}
