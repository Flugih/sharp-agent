namespace SharpAgent.Ui;

public sealed class VoiceWave : FrameworkElement
{
    const double BarWidth = 3, Step = 5;
    const int TalkBars = 6, HopMs = 180, PauseMs = 200;
    readonly List<float> _levels = [];
    DispatcherTimer? _hops;
    long _hopStart;
    bool _isThinking;

    public VoiceWave()
    {
        Loaded += (_, _) => UpdateHops();
        Unloaded += (_, _) => _hops?.Stop();
        IsVisibleChanged += (_, _) => UpdateHops();
    }
    public bool Centered { get; set; }
    public bool IsThinking
    {
        get => _isThinking;
        set
        {
            if (_isThinking == value) return;
            _isThinking = value;
            Clear();
            UpdateHops();
        }
    }

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

    void UpdateHops()
    {
        if (!Centered || !IsThinking || !IsLoaded || !IsVisible)
        {
            _hops?.Stop();
            return;
        }
        _hopStart = Environment.TickCount64;
        _hops ??= new DispatcherTimer(TimeSpan.FromMilliseconds(32), DispatcherPriority.Background, (_, _) => InvalidateVisual(), Dispatcher);
        _hops.Start();
    }

    protected override void OnRender(DrawingContext context)
    {
        int bars = Centered ? TalkBars : (int)((ActualWidth + Step - BarWidth) / Step);
        double step = Centered ? ActualWidth / bars : Step;
        double width = Centered ? Math.Clamp(step * 0.55, 3, 5) : BarWidth;
        double space = ActualWidth - (bars - 1) * step - width;
        double left = Centered ? space / 2 : space;
        long hop = _hops is { IsEnabled: true } && !(bool)GetValue(Shimmer.MotionSuspendedProperty)
            ? (Environment.TickCount64 - _hopStart) % (TalkBars * HopMs + PauseMs) : -1;
        var foreground = IsThinking ? Theme.Get("TextMuted") : Centered ? Theme.Get("VoiceOrbFg") : Theme.TextSoft;
        for (int i = 0; i < bars; i++)
        {
            int index = _levels.Count - bars + i;
            double level = index >= 0 ? _levels[index] : 0;
            double amplitude = Centered ? Math.Sqrt(Math.Clamp((level - 0.02) * 2, 0, 1)) : level;
            double height = Math.Max(width, amplitude * ActualHeight);
            double x = left + i * step;
            double y = (ActualHeight - height) / 2;
            long phase = hop - i * HopMs;
            if (phase is >= 0 and < HopMs) y -= Math.Sin(Math.PI * phase / HopMs) * Math.Min(10, ActualHeight * 0.19);
            context.DrawRoundedRectangle(Centered || index >= 0 ? foreground : Theme.Get("Track"), null,
                new(Centered ? x : Math.Round(x), y, width, height),
                width / 2, width / 2);
        }
    }
}
