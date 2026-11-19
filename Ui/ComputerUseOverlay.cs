using System.Windows.Interop;
using Microsoft.Win32;
using ShapePath = System.Windows.Shapes.Path;

namespace SharpAgent.Ui;

internal sealed class ComputerUseOverlay : IDisposable
{
    const int WsPopup = unchecked((int)0x80000000), WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    const int KeyboardLowLevel = 13, KeyDown = 0x0100, Escape = 0x1B, Injected = 0x10;
    const uint ShowWithoutActivation = 0x0040 | 0x0010 | 0x0002 | 0x0001;
    const double Radius = 34;
    readonly Dispatcher _dispatcher;
    readonly Action _stop;
    readonly KeyboardCallback _keyboardCallback;
    readonly List<HwndSource> _bands = [];
    nint _keyboard;
    bool _disposed, _stopQueued;

    public ComputerUseOverlay(Dispatcher dispatcher, Action stop)
    {
        _dispatcher = dispatcher;
        _stop = stop;
        _keyboardCallback = OnKeyboard;
        Rebuild();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
    }

    void DisplayChanged(object? sender, EventArgs e) => _dispatcher.InvokeAsync(Rebuild);

    void Rebuild()
    {
        if (_disposed) return;
        CloseBands();
        if (_keyboard == 0) _keyboard = SetWindowsHookEx(KeyboardLowLevel, _keyboardCallback, GetModuleHandle(null), 0);
        List<PixelRect> monitors = [];
        EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref PixelRect bounds, nint data) =>
        {
            monitors.Add(bounds);
            return true;
        }, 0);
        try
        {
            foreach (var screen in monitors)
            {
                int size = Math.Min(screen.Width, screen.Height);
                int band = Math.Min(size / 3, Math.Clamp(size / 10, 88, 156));
                if (band == 0) continue;
                ShowBand(screen, new(screen.Left, screen.Top, screen.Right, screen.Top + band), 0);
                ShowBand(screen, new(screen.Right - band, screen.Top + band, screen.Right, screen.Bottom - band), 1);
                ShowBand(screen, new(screen.Left, screen.Bottom - band, screen.Right, screen.Bottom), 2);
                ShowBand(screen, new(screen.Left, screen.Top + band, screen.Left + band, screen.Bottom - band), 3);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            CloseBands();
            App.LogError(ex);
        }
        if (_bands.Count == 0) RemoveKeyboardHook();
    }

    void ShowBand(PixelRect screen, PixelRect band, int side)
    {
        var parameters = new HwndSourceParameters("Sharp Agent · Computer Use", band.Width, band.Height)
        {
            WindowStyle = WsPopup, ExtendedWindowStyle = WsExTransparent | WsExToolWindow | WsExNoActivate,
            UsesPerPixelOpacity = true, TreatAsInputRoot = false,
        };
        parameters.SetPosition(band.Left, band.Top);
        var source = new HwndSource(parameters);
        _bands.Add(source);
        var toDip = source.CompositionTarget!.TransformFromDevice;
        var frame = Rect.Transform(new Rect(screen.Left - band.Left, screen.Top - band.Top, screen.Width, screen.Height), toDip);
        frame.Inflate(-4, -4);
        if (frame.Width <= 0 || frame.Height <= 0) return;
        var size = toDip.Transform(new Vector(band.Width, band.Height));
        source.RootVisual = BuildBand(frame, size, side, _keyboard != 0);
        WindowEffects.ExcludeFromCapture(source.Handle);
        if (!SetWindowPos(source.Handle, new nint(-1), 0, 0, 0, 0, ShowWithoutActivation))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    static Grid BuildBand(Rect frame, Vector size, int side, bool hasStop)
    {
        var resources = Application.Current.Resources;
        var cyan = (Color)resources["ComputerCyan"];
        var blue = (Color)resources["ComputerBlue"];
        var violet = (Color)resources["ComputerViolet"];
        var outline = new RectangleGeometry(frame, Radius, Radius);
        var spectrum = new LinearGradientBrush(
            [new(cyan, 0), new(blue, .33), new(violet, .52), new(blue, .76), new(cyan, 1)],
            new Point(0, 0), new Point(1, 1));
        var glow = new DrawingGroup();
        using (var draw = glow.Open())
        {
            for (int width = 56; width >= 4; width -= 4)
            {
                var brush = spectrum.Clone();
                brush.Opacity = .012 + .16 * Math.Pow(1 - width / 56d, 3);
                draw.DrawGeometry(null, new Pen(brush, width), outline);
            }
            draw.DrawGeometry(null, new Pen(spectrum, 1.7), outline);
        }
        glow.Freeze();
        bool horizontal = side % 2 == 0;
        double length = horizontal ? frame.Width : size.Y;
        double depth = Math.Min(88, (horizontal ? size.Y : size.X) - 8);
        var origin = side switch
        {
            0 => frame.TopLeft, 1 => new Point(frame.Right, 0), 2 => frame.BottomRight, _ => new Point(frame.Left, size.Y),
        };
        var waves = new Canvas { Clip = outline };
        if (depth > 0)
        {
            waves.Children.Add(Wave(length, depth, origin, side * 90, side == 0 ? cyan : blue, (.40, .015, .58, 6.6)));
            waves.Children.Add(Wave(length, depth * .68, origin, side * 90, violet, (.31, .67, .015, 8.2)));
            waves.Children.Add(Wave(length, depth * .56, origin, side * 90, side == 0 ? blue : cyan, (.25, .32, .71, 5.4)));
        }
        var root = new Grid { ClipToBounds = true, IsHitTestVisible = false, Children = { new Glow(glow), waves } };
        if (side == 0)
        {
            var badge = new ComputerUseBadge { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 6, 0, 0) };
            if (hasStop) badge.StopHint.Visibility = Visibility.Visible;
            root.Children.Add(badge);
        }
        return root;
    }

    static ShapePath Wave(double length, double depth, Point origin, int angle, Color color,
        (double Width, double Start, double End, double Seconds) travel)
    {
        double width = length * travel.Width;
        var ribbon = new StreamGeometry();
        using (var path = ribbon.Open())
        {
            path.BeginFigure(new(0, 0), true, true);
            path.BezierTo(new(width * .18, 0), new(width * .27, depth * .94), new(width * .52, depth), true, false);
            path.BezierTo(new(width * .73, depth * 1.04), new(width * .86, depth * .04), new(width, 0), true, false);
        }
        ribbon.Freeze();
        var shift = new TranslateTransform(length * travel.Start, 0);
        if (Theme.AnimationsEnabled)
        {
            var motion = new DoubleAnimation(length * travel.Start, length * travel.End, TimeSpan.FromSeconds(travel.Seconds))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Timeline.SetDesiredFrameRate(motion, 20);
            shift.BeginAnimation(TranslateTransform.XProperty, motion);
        }
        return new ShapePath
        {
            Data = ribbon, Fill = new LinearGradientBrush(color with { A = 16 }, color with { A = 94 }, 90),
            Stroke = new SolidColorBrush(color with { A = 150 }), StrokeThickness = 1.25,
            RenderTransform = new TransformGroup { Children = { shift, new RotateTransform(angle), new TranslateTransform(origin.X, origin.Y) } },
        };
    }

    void CloseBands()
    {
        foreach (var band in _bands) band.Dispose();
        _bands.Clear();
    }

    nint OnKeyboard(int code, nint message, nint data)
    {
        // KBDLLHOOKSTRUCT starts with the virtual key, scan code, then flags.
        if (_disposed || code != 0 || message != KeyDown || Marshal.ReadInt32(data) != Escape || (Marshal.ReadInt32(data, 8) & Injected) != 0 ||
            GetAsyncKeyState(0x10) < 0 || GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0 ||
            GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0)
            return CallNextHookEx(0, code, message, data);
        if (!_stopQueued)
        {
            _stopQueued = true;
            _dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)(() => { if (!_disposed) _stop(); }));
        }
        return 1;
    }

    void RemoveKeyboardHook()
    {
        if (_keyboard == 0) return;
        UnhookWindowsHookEx(_keyboard);
        _keyboard = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        RemoveKeyboardHook();
        CloseBands();
    }

    sealed class Glow(Drawing drawing) : FrameworkElement
    {
        protected override void OnRender(DrawingContext context) => context.DrawDrawing(drawing);
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    delegate bool MonitorCallback(nint monitor, nint dc, ref PixelRect bounds, nint data);
    delegate nint KeyboardCallback(int code, nint message, nint data);

    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint SetWindowsHookEx(int kind, KeyboardCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? name);
}
