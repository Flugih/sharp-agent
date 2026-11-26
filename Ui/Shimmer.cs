namespace SharpAgent.Ui;

// Status text in its Base colour; while active a lighter band sweeps across it instead of a spinner.
public sealed class Shimmer : TextBlock
{
    const int FrameMs = 32, HoldMs = 150;
    static readonly DoubleAnimationUsingKeyFrames SweepMotion = CreateSweepMotion();
    internal static readonly DependencyProperty MotionSuspendedProperty = DependencyProperty.RegisterAttached(
        "MotionSuspended", typeof(bool), typeof(Shimmer), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits,
            (target, _) => { if (target is Shimmer shimmer) shimmer.Update(); }));
    static readonly DependencyProperty BaseColorProperty = ColorSource("BaseColor"), BandColorProperty = ColorSource("BandColor");
    readonly GradientStop _start = new(), _band = new() { Offset = 0.5 }, _end = new() { Offset = 1 };
    readonly TranslateTransform _shift = new();
    readonly ScaleTransform _width = new();
    readonly LinearGradientBrush _sweep;
    DispatcherTimer? _textTimer;
    string _base = "";
    string? _revealing, _nextText;
    long _holdUntil;
    int _revealFrame;
    bool _active, _running;

    public Shimmer()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        _sweep = new([_start, _band, _end])
        {
            StartPoint = new(0, 0), EndPoint = new(0.5, 0), MappingMode = BrushMappingMode.Absolute,
            Transform = new TransformGroup { Children = { _shift, _width } },
        };
        Base = "TextMuted";
        this.Paint(BandColorProperty, "Text");
        Loaded += (_, _) => Update();
        Unloaded += (_, _) =>
        {
            if (_revealing != null || _nextText != null) ShowImmediately(TargetText);
            Update();
        };
        IsVisibleChanged += (_, _) => Update();
        SizeChanged += (_, e) => _width.ScaleX = Math.Max(1, e.NewSize.Width);
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

    public bool AlwaysAnimate { get; set; }
    bool MotionEnabled => !(bool)GetValue(MotionSuspendedProperty) && (AlwaysAnimate || Theme.AnimationsEnabled);
    internal string TargetText => _nextText ?? _revealing ?? Text;

    internal void SetAnimatedText(string text, bool animate)
    {
        if (!animate || !MotionEnabled || text.Length == 0 || IsLoaded && !IsVisible)
        {
            ShowImmediately(text);
            return;
        }
        if (TargetText == text) return;
        _nextText = text;
        if (IsLoaded && IsVisible && _revealing == null) RevealNextText();
    }

    void ShowImmediately(string text)
    {
        _nextText = null;
        _textTimer?.Stop();
        _revealing = null;
        Text = text;
    }

    void RevealNextText()
    {
        if (_nextText is not { } text)
        {
            _textTimer?.Stop();
            return;
        }
        _nextText = null;
        _revealing = text;
        _revealFrame = 0;
        _holdUntil = 0;
        RevealText();
        _textTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(FrameMs), DispatcherPriority.Background, (_, _) => RevealText(), Dispatcher);
        _textTimer.Start();
    }

    void RevealText()
    {
        long now = Environment.TickCount64;
        if (_holdUntil > 0)
        {
            if (now < _holdUntil) return;
            _revealing = null;
            RevealNextText();
            return;
        }
        var text = _revealing!;
        int frames = Math.Min(text.Length, 16);
        int end = (int)((long)++_revealFrame * text.Length / frames);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1])) end--;
        if (Text.Length != end || !text.AsSpan(0, end).SequenceEqual(Text)) Text = text[..end];
        if (end == text.Length) _holdUntil = now + HoldMs;
    }

    // Runs only while shown in a window, so finished or removed rows cost nothing.
    void Update()
    {
        bool visible = IsLoaded && IsVisible;
        bool canAnimate = visible && MotionEnabled;
        if (!canAnimate && (_revealing != null || _nextText != null) && IsLoaded) ShowImmediately(TargetText);
        if (canAnimate && _revealing == null && _nextText != null) RevealNextText();
        bool run = _active && canAnimate;
        if (run == _running) return;
        _running = run;
        if (run) Foreground = _sweep;
        else this.Paint(ForegroundProperty, _base);
        _shift.BeginAnimation(TranslateTransform.XProperty, run ? SweepMotion : null);
    }

    static DoubleAnimationUsingKeyFrames CreateSweepMotion()
    {
        var sweep = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        sweep.KeyFrames.Add(new LinearDoubleKeyFrame(-0.5, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2))));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(7))));
        sweep.Freeze();
        return sweep;
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
