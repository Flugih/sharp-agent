namespace SharpAgent.Ui;

public sealed class EffortSlider : Slider
{
    const double ThumbRadius = 14, RailRadius = 12;
    static readonly Color Peak = Color.FromRgb(0x9E, 0x6F, 0xFE);

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        double width = ActualWidth, center = ActualHeight / 2;
        double travel = width - ThumbRadius * 2, range = Maximum - Minimum;
        if (travel <= 0 || range <= 0) return;
        var rail = new Rect(0, center - RailRadius, width, RailRadius * 2);
        drawing.DrawRoundedRectangle(Background, null, rail, RailRadius, RailRadius);
        double fill = Value >= Maximum ? width : Value > Minimum ? ThumbRadius + travel * (Value - Minimum) / range : 0;
        if (fill > 0)
        {
            // Only the top level blends the accent into violet.
            var brush = Value >= Maximum && BorderBrush is SolidColorBrush accent ? new LinearGradientBrush(accent.Color, Peak, 0) : BorderBrush;
            drawing.PushClip(new RectangleGeometry(rail, RailRadius, RailRadius));
            drawing.DrawRectangle(brush, null, new(0, rail.Y, fill, rail.Height));
            drawing.Pop();
        }
        drawing.PushOpacity(0.5);
        for (int i = 0; i <= (int)range; i++)
        {
            double x = ThumbRadius + i * travel / range;
            drawing.DrawEllipse(x < fill ? Brushes.White : Foreground, null, new(x, center), 2, 2);
        }
        drawing.Pop();
    }
}
