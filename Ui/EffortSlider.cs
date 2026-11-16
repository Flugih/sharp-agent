namespace SharpAgent.Ui;

public sealed class EffortSlider : Slider
{
    const double ThumbRadius = 14, RailRadius = 12;

    static readonly LinearGradientBrush Fill = new()
    {
        StartPoint = new(0, .65), EndPoint = new(1, .40),
        GradientStops =
        [
            new(Color.FromRgb(29, 65, 204), 0),
            new(Color.FromRgb(100, 108, 247), .28),
            new(Color.FromRgb(173, 130, 250), .52),
            new(Color.FromRgb(133, 73, 237), .76),
            new(Color.FromRgb(66, 66, 213), 1),
        ],
    };
    static readonly (double X, double Y, double Size, double Opacity)[] Highlights =
    [
        (.07, .42, 1.0, .30), (.11, .65, 1.2, .20), (.17, .36, 1.6, .25),
        (.23, .66, 1.0, .34), (.30, .53, 1.8, .20), (.38, .31, 1.2, .35),
        (.48, .64, 1.7, .24), (.55, .39, 1.2, .33), (.62, .68, 2.2, .19),
        (.68, .23, 1.4, .18), (.74, .55, 2.0, .27), (.83, .30, 1.1, .28),
        (.90, .63, 1.7, .23), (.96, .43, 1.3, .32),
    ];

    static EffortSlider() => Fill.Freeze();

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
        if (Value > Minimum)
        {
            double fill = Value >= Maximum ? width : ThumbRadius + travel * (Value - Minimum) / range;
            drawing.PushClip(new RectangleGeometry(rail, RailRadius, RailRadius));
            drawing.PushClip(new RectangleGeometry(new(0, rail.Y, fill, rail.Height)));
            drawing.DrawRectangle(Fill, null, rail);
            foreach (var (x, y, size, opacity) in Highlights)
            {
                drawing.PushOpacity(opacity);
                drawing.DrawEllipse(Brushes.White, null, new(width * x, rail.Y + rail.Height * y), size, size);
                drawing.Pop();
            }
            drawing.Pop();
            drawing.Pop();
        }
        drawing.PushOpacity(0.34);
        for (int i = 0; i <= (int)range; i++)
            drawing.DrawEllipse(Brushes.White, null, new(ThumbRadius + i * travel / range, center), 2, 2);
        drawing.Pop();
    }
}
