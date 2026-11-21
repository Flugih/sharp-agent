using System.Windows.Interop;
using Microsoft.Win32;

namespace SharpAgent.Ui;

internal sealed class ComputerUseOverlay : IDisposable
{
    const int WsPopup = unchecked((int)0x80000000), WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000;
    const int KeyboardLowLevel = 13, KeyDown = 0x0100, Escape = 0x1B, Injected = 0x10;
    const uint ShowWithoutActivation = 0x0040 | 0x0010 | 0x0002 | 0x0001;
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
                ShowBand(new(screen.Left, screen.Top, screen.Right, screen.Top + band));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            CloseBands();
            App.LogError(ex);
        }
        if (_bands.Count == 0) RemoveKeyboardHook();
    }

    void ShowBand(PixelRect band)
    {
        var parameters = new HwndSourceParameters("Sharp Agent · Computer Use", band.Width, band.Height)
        {
            WindowStyle = WsPopup, ExtendedWindowStyle = WsExTransparent | WsExToolWindow | WsExNoActivate,
            UsesPerPixelOpacity = true, TreatAsInputRoot = false,
        };
        parameters.SetPosition(band.Left, band.Top);
        var source = new HwndSource(parameters);
        _bands.Add(source);
        var badge = new ComputerUseBadge { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 6, 0, 0) };
        if (_keyboard != 0) badge.StopHint.Visibility = Visibility.Visible;
        source.RootVisual = new Grid { ClipToBounds = true, IsHitTestVisible = false, Children = { badge } };
        WindowEffects.ExcludeFromCapture(source.Handle);
        if (!SetWindowPos(source.Handle, new nint(-1), 0, 0, 0, 0, ShowWithoutActivation))
            throw new Win32Exception(Marshal.GetLastWin32Error());
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
