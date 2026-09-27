using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FlexCompanion.Services;

namespace FlexCompanion.Controls;

/// <summary>Plots post-AGC audio level against AGC-T (0-100) and marks the recommendation.</summary>
public sealed class AgcCurveView : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IEnumerable), typeof(AgcCurveView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(
        nameof(Current), typeof(int), typeof(AgcCurveView), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RecommendedProperty = DependencyProperty.Register(
        nameof(Recommended), typeof(int), typeof(AgcCurveView), new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Points { get => (IEnumerable?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public int Current { get => (int)GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }
    public int Recommended { get => (int)GetValue(RecommendedProperty); set => SetValue(RecommendedProperty, value); }

    static readonly Brush BgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x08, 0x0C, 0x14)));
    static readonly Pen BorderPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x30, 0x40, 0x50))), 1));
    static readonly Pen GridPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x60, 0x30, 0x40, 0x50))), 1));
    static readonly Pen CurvePen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x00, 0xB4, 0xD8))), 1.6));
    static readonly Brush DotBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x00, 0xB4, 0xD8)));
    static readonly Pen RecPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x4D))), 1.6) { DashStyle = DashStyles.Dash });
    static readonly Pen CurPen = Frozen(new Pen(Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0xA8, 0xC0))), 1));
    static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x50, 0x60, 0x70)));
    static readonly Brush RecBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB8, 0x4D)));
    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    protected override Size MeasureOverride(Size availableSize) => new(0, 120);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 60 || h < 40) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var rect = new Rect(0.5, 0.5, w - 1, h - 1);
        dc.DrawRoundedRectangle(BgBrush, BorderPen, rect, 3, 3);

        const double padL = 34, padB = 16, padT = 8, padR = 8;
        var plot = new Rect(padL, padT, Math.Max(10, w - padL - padR), Math.Max(10, h - padT - padB));

        var pts = Points?.OfType<AgcTCalibrator.Point>().OrderBy(p => p.Value).ToList() ?? new();
        double lo = pts.Count > 0 ? pts.Min(p => p.RmsDb) : -60;
        double hi = pts.Count > 0 ? pts.Max(p => p.RmsDb) : -10;
        if (hi - lo < 6) { double mid = (hi + lo) / 2; lo = mid - 3; hi = mid + 3; }
        lo = Math.Floor(lo / 3) * 3; hi = Math.Ceiling(hi / 3) * 3;

        double X(double v) => plot.Left + v / 100.0 * plot.Width;
        double Y(double db) => plot.Bottom - (db - lo) / (hi - lo) * plot.Height;

        for (int v = 0; v <= 100; v += 25)
        {
            double x = Math.Round(X(v)) + 0.5;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = Text(v.ToString(CultureInfo.CurrentCulture), LabelBrush, dpi);
            dc.DrawText(ft, new Point(x - ft.Width / 2, plot.Bottom + 1));
        }
        foreach (var db in new[] { lo, (lo + hi) / 2, hi })
        {
            double y = Math.Round(Y(db)) + 0.5;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Text($"{db:0}", LabelBrush, dpi);
            dc.DrawText(ft, new Point(padL - ft.Width - 4, y - ft.Height / 2));
        }

        if (Current is >= 0 and <= 100)
            dc.DrawLine(CurPen, new Point(X(Current), plot.Top), new Point(X(Current), plot.Bottom));

        if (pts.Count > 1)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(X(pts[0].Value), Y(pts[0].RmsDb)), false, false);
                foreach (var p in pts.Skip(1)) ctx.LineTo(new Point(X(p.Value), Y(p.RmsDb)), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, CurvePen, g);
        }
        foreach (var p in pts) dc.DrawEllipse(DotBrush, null, new Point(X(p.Value), Y(p.RmsDb)), 2, 2);

        if (Recommended is >= 0 and <= 100)
        {
            double x = X(Recommended);
            dc.DrawLine(RecPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = Text($"best {Recommended}", RecBrush, dpi);
            dc.DrawText(ft, new Point(Math.Min(x + 4, plot.Right - ft.Width), plot.Top));
        }

        if (pts.Count == 0)
        {
            var ft = Text("audio level vs AGC-T appears here during the sweep", LabelBrush, dpi);
            dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2, plot.Top + (plot.Height - ft.Height) / 2));
        }
    }

    static FormattedText Text(string s, Brush b, double dpi) =>
        new(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 9.5, b, dpi);
}
