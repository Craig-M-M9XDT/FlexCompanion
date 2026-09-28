using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace FlexCompanion.Controls;

/// <summary>Allocation-free linear meter for the Pi/touch build.</summary>
public sealed class MeterBar : Control
{
    public static readonly StyledProperty<double> ValueProperty = AvaloniaProperty.Register<MeterBar, double>(nameof(Value), double.NaN);
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<MeterBar, double>(nameof(Minimum), -127);
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<MeterBar, double>(nameof(Maximum), -13);
    public static readonly StyledProperty<double> RedStartProperty = AvaloniaProperty.Register<MeterBar, double>(nameof(RedStart), -73);

    static readonly IBrush Track = new SolidColorBrush(Color.Parse("#080C14"));
    static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#00B4D8"));
    static readonly IBrush Hot = new SolidColorBrush(Color.Parse("#FF4D4D"));
    static readonly Pen Border = new(new SolidColorBrush(Color.Parse("#304050")), 1);

    static MeterBar() => AffectsRender<MeterBar>(ValueProperty, MinimumProperty, MaximumProperty, RedStartProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double RedStart { get => GetValue(RedStartProperty); set => SetValue(RedStartProperty, value); }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 2 || h < 2) return;
        var rect = new Rect(0.5, 0.5, Math.Max(1, w - 1), Math.Max(1, h - 1));
        context.DrawRectangle(Track, Border, rect);

        double span = Maximum - Minimum;
        if (span <= 0 || !double.IsFinite(Value)) return;
        double frac = Math.Clamp((Value - Minimum) / span, 0, 1);
        double redFrac = Math.Clamp((RedStart - Minimum) / span, 0, 1);
        double fillW = Math.Max(0, (w - 2) * frac);
        double redX = 1 + (w - 2) * redFrac;

        if (fillW > 0)
        {
            double normalW = Math.Min(fillW, Math.Max(0, redX - 1));
            if (normalW > 0) context.FillRectangle(Fill, new Rect(1, 1, normalW, Math.Max(0, h - 2)), 0);
            if (fillW > normalW) context.FillRectangle(Hot, new Rect(1 + normalW, 1, fillW - normalW, Math.Max(0, h - 2)), 0);
        }
        if (redFrac is > 0 and < 1) context.DrawLine(new Pen(Hot, 1), new Point(redX, 1), new Point(redX, h - 1));
    }
}
