namespace SharpAgent.Ui;

// Status text in its Base colour; while active a lighter band sweeps across it instead of a spinner.
public sealed class Shimmer : TextBlock
{
    static readonly DependencyProperty BaseColorProperty = ColorSource("BaseColor"), BandColorProperty = ColorSource("BandColor");
    readonly GradientStop _start = new(), _band = new() { Offset = 0.5 }, _end = new() { Offset = 1 };
    readonly TranslateTransform _shift = new();
    readonly LinearGradientBrush _sweep;
    readonly Queue<string> _textQueue = new();
    DispatcherTimer? _textTimer;
    string _base = "";
    bool _active, _running, _revealing;

    public Shimmer()
    {
        _sweep = new([_start, _band, _end]) { StartPoint = new(0, 0), EndPoint = new(0.5, 0), RelativeTransform = _shift };
        Base = "TextMuted";
        this.Paint(BandColorProperty, "Text");
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Update();
    }

    // Brush key of the text: TextMuted, TextDim on raised surfaces, or a status colour while inactive.
    public string Base
    {
        get => _base;
        set
        {
            _base = value;
            this.Paint(BaseColorProperty, value);
            if (!_running) this.Paint(ForegroundProperty, value);
        }
    }

    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            Update();
        }
    }

    internal bool HasText(string text) => Equals(Text, text) && !_revealing && _textQueue.Count == 0;

    internal void SetAnimatedText(string text, bool animate)
    {
        if (!animate || !IsVisible || !Theme.AnimationsEnabled)
        {
            _textQueue.Clear();
            _textTimer?.Stop();
            _revealing = false;
            BeginAnimation(TextProperty, null);
            Text = text;
            return;
        }

        var target = GetAnimationBaseValue(TextProperty) as string;
        var lastQueued = _textQueue.Count == 0 ? null : _textQueue.Last();
        if (Equals(target, text) || Equals(lastQueued, text)) return;
        _textQueue.Enqueue(text);
        if (!_revealing) RevealNextText();
    }

    void RevealNextText()
    {
        if (_textQueue.Count == 0)
        {
            _revealing = false;
            return;
        }

        var text = _textQueue.Dequeue();
        BeginAnimation(TextProperty, null);
        bool changed = !Equals(Text, text);
        Text = text;
        if (!changed)
        {
            RevealNextText();
            return;
        }

        _revealing = true;
        var reveal = new StringAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        int frames = Math.Min(text.Length, 48);
        for (int i = 1; i <= frames; i++)
        {
            int end = (int)((long)i * text.Length / frames);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
            reveal.KeyFrames.Add(new DiscreteStringKeyFrame(text[..end], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds((i - 1) * 9))));
        }
        BeginAnimation(TextProperty, reveal);
        _textTimer ??= new DispatcherTimer();
        _textTimer.Stop();
        _textTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(40, (frames - 1) * 9 + 40));
        _textTimer.Tick -= FinishTextReveal;
        _textTimer.Tick += FinishTextReveal;
        _textTimer.Start();
    }

    void FinishTextReveal(object? sender, EventArgs e)
    {
        _textTimer?.Stop();
        BeginAnimation(TextProperty, null);
        _revealing = false;
        RevealNextText();
    }

    // Runs only while shown in a window, so finished or removed rows cost nothing.
    void Update()
    {
        bool run = _active && IsLoaded && Theme.AnimationsEnabled;
        if (run == _running) return;
        _running = run;
        if (run) Foreground = _sweep;
        else this.Paint(ForegroundProperty, _base);
        _shift.BeginAnimation(TranslateTransform.XProperty, run ? new DoubleAnimation(-0.5, 1, TimeSpan.FromSeconds(3.5)) { RepeatBehavior = RepeatBehavior.Forever } : null);
    }

    // Palette brushes arrive through resource references, so a theme switch recolours the sweep too.
    static DependencyProperty ColorSource(string name) => DependencyProperty.Register(name, typeof(Brush), typeof(Shimmer),
        new PropertyMetadata(null, (target, _) => ((Shimmer)target).Recolor()));

    void Recolor()
    {
        if (GetValue(BaseColorProperty) is SolidColorBrush baseBrush) _start.Color = _end.Color = baseBrush.Color;
        if (GetValue(BandColorProperty) is SolidColorBrush band) _band.Color = band.Color;
    }
}
