namespace SharpAgent.Ui;

public sealed class VoiceWave : FrameworkElement
{
    const double BarWidth = 3, Step = 5;
    readonly List<float> _levels = [];

    public void Push(float level)
    {
        _levels.Add(level);
        if (_levels.Count > 200) _levels.RemoveAt(0);
        InvalidateVisual();
    }

    public void Clear()
    {
        _levels.Clear();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        int bars = (int)((ActualWidth + Step - BarWidth) / Step);
        for (int i = 0; i < bars; i++)
        {
            int index = _levels.Count - bars + i;
            double height = Math.Max(BarWidth, (index >= 0 ? _levels[index] : 0) * ActualHeight);
            context.DrawRoundedRectangle(index >= 0 ? Theme.TextSoft : Theme.Get("Track"), null,
                new(Math.Round(ActualWidth - (bars - i) * Step + Step - BarWidth), (ActualHeight - height) / 2, BarWidth, height),
                BarWidth / 2, BarWidth / 2);
        }
    }
}
