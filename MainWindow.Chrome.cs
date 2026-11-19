using System.Windows.Interop;
using System.Windows.Shell;

namespace SharpAgent;

public partial class MainWindow
{
    const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, HTMAXBUTTON = 9;

    readonly DispatcherTimer _maxHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    bool _wasNarrow;

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out System.Drawing.Point point);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.ApplyFrame(this, ((SolidColorBrush)Theme.Line).Color);
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WindowMessageHook);
        _maxHoverTimer.Tick += (_, _) =>
        {
            if (GetCursorPos(out var cursor) && IsOverMaxButton(cursor.X, cursor.Y)) return;
            MaxButton.Tag = null;
            _maxHoverTimer.Stop();
        };
    }

    nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        switch (message)
        {
            case WM_NCHITTEST when IsOverMaxButton((short)lParam, (short)(lParam >> 16)):
                MaxButton.Tag ??= "hover";
                _maxHoverTimer.Start();
                handled = true;
                return HTMAXBUTTON;
            case WM_NCLBUTTONDOWN when wParam == HTMAXBUTTON:
                MaxButton.Tag = "pressed";
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                bool pressed = Equals(MaxButton.Tag, "pressed");
                MaxButton.Tag = "hover";
                handled = true;
                if (pressed) ToggleMaximize();
                break;
        }
        return 0;
    }

    bool IsOverMaxButton(int x, int y)
    {
        try
        {
            return MaxButton.IsVisible && MaxButton.PointFromScreen(new(x, y)) is { X: >= 0, Y: >= 0 } point &&
                   point.X < MaxButton.ActualWidth && point.Y < MaxButton.ActualHeight;
        }
        catch (InvalidOperationException) { return false; }
    }

    void Minimize_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximize_Click(object s, RoutedEventArgs e) => ToggleMaximize();

    void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void Close_Click(object s, RoutedEventArgs e) => Close();

    void Window_Activated(object? s, EventArgs e)
    {
        if (Taskbar.ProgressState == TaskbarItemProgressState.Error) SetTaskbar(TaskbarItemProgressState.None);
    }

    void Window_StateChanged(object? s, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        RootBorder.Margin = new(maximized ? 8 : 0);
        RootBorder.BorderThickness = new(maximized ? 0 : 1);
        MaxButton.Content = maximized ? Glyphs.Restore : Glyphs.Maximize;
        AutomationProperties.SetName(MaxButton, maximized ? "Свернуть в окно" : "Развернуть");
    }

    void Window_SizeChanged(object s, SizeChangedEventArgs e)
    {
        if (Narrow != _wasNarrow)
        {
            _wasNarrow = Narrow;
            _overlayOpen = false;
            ApplySidebar();
        }
        else if (e.WidthChanged && SidebarExpanded) UpdateSidebarWidth();
    }

    void WorkArea_SizeChanged(object s, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) ApplyPreviewLayout();
    }

    void Composer_SizeChanged(object s, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateLayoutWidths();
    }

    void UpdateLayoutWidths()
    {
        double row = Composer.ActualWidth, folderRoom = row - 210;
        ConfirmLabel.Visibility = VisibleIf(row >= 558);
        FolderText.MaxWidth = Math.Clamp(folderRoom, 0, 240);
        FolderText.Visibility = VisibleIf(folderRoom >= 40);
        double room = RootGrid.ActualWidth - 250 - (SubagentsOpenButton?.Visibility == Visibility.Visible ? 140 : 0);
        ChatTitleText.MaxWidth = Math.Max(0, room);
        ChatTitleText.Visibility = VisibleIf(room >= 60);
    }

    void RestoreWindowPlacement()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 32);
        Height = Math.Min(Height, area.Height - 32);
        if (_state.Window is not { } saved || saved.Width < MinWidth || saved.Height < MinHeight) return;
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(screens, new(saved.Left, saved.Top, saved.Width, saved.Height));
        if (visible.IsEmpty || visible.Width < 240 || visible.Height < 120) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.Left;
        Top = saved.Top;
        Width = Math.Min(saved.Width, screens.Width);
        Height = Math.Min(saved.Height, screens.Height);
        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    WindowPlacement CapturePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        return bounds.IsEmpty || !double.IsFinite(bounds.Width) ? _state.Window ?? new() : new()
        {
            Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height, Maximized = WindowState == WindowState.Maximized,
        };
    }
}
