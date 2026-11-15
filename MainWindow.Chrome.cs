using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using SharpAgent.Core;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;
    private const int HTMAXBUTTON = 9;

    private DispatcherTimer? _maxHoverTimer;
    private bool _wasNarrow;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowEffects.ApplyDarkFrame(this, ((SolidColorBrush)Theme.LineStrong).Color);
        (PresentationSource.FromVisual(this) as HwndSource)?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WM_NCHITTEST:
                long packed = lParam.ToInt64();
                if (IsOverMaxButton((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF)))
                {
                    if (MaxButton.Tag == null) MaxButton.Tag = "hover";
                    WatchMaxButtonHover();
                    handled = true;
                    return HTMAXBUTTON;
                }
                break;

            case WM_NCLBUTTONDOWN when wParam.ToInt64() == HTMAXBUTTON:
                MaxButton.Tag = "pressed";
                handled = true;
                break;

            case WM_NCLBUTTONUP when wParam.ToInt64() == HTMAXBUTTON:
                bool wasPressed = Equals(MaxButton.Tag, "pressed");
                MaxButton.Tag = "hover";
                handled = true;
                if (wasPressed) ToggleMaximize();
                break;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaxButton(int screenX, int screenY)
    {
        try
        {
            if (!MaxButton.IsVisible) return false;
            var point = MaxButton.PointFromScreen(new Point(screenX, screenY));
            return point.X >= 0 && point.Y >= 0 && point.X < MaxButton.ActualWidth && point.Y < MaxButton.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void WatchMaxButtonHover()
    {
        if (_maxHoverTimer == null)
        {
            _maxHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _maxHoverTimer.Tick += (_, _) =>
            {
                if (GetCursorPos(out var cursor) && IsOverMaxButton(cursor.X, cursor.Y)) return;
                MaxButton.Tag = null;
                _maxHoverTimer!.Stop();
            };
        }
        if (!_maxHoverTimer.IsEnabled) _maxHoverTimer.Start();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Activated(object? sender, EventArgs e)
    {
        if (Taskbar.ProgressState == TaskbarItemProgressState.Error) SetTaskbar(TaskbarItemProgressState.None);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        bool maximized = WindowState == WindowState.Maximized;
        RootBorder.Margin = maximized ? new Thickness(8) : new Thickness(0);
        RootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
        MaxButton.Content = maximized ? Glyphs.Restore : Glyphs.Maximize;
        var label = maximized ? "Свернуть в окно" : "Развернуть";
        MaxButton.ToolTip = label;
        AutomationProperties.SetName(MaxButton, label);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Narrow != _wasNarrow)
        {
            _wasNarrow = Narrow;
            _overlayOpen = false;
            ApplySidebar();
        }
        else if (e.WidthChanged && SidebarExpanded) UpdateSidebarWidth();
    }

    private void WorkArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) ApplyPreviewLayout();
    }

    private void Composer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateLayoutWidths();
    }

    private void UpdateLayoutWidths()
    {
        double row = Composer.ActualWidth;
        ConfirmLabel.Visibility = row < 558 ? Visibility.Collapsed : Visibility.Visible;
        double folderRoom = row - (ConfirmLabel.Visibility == Visibility.Visible ? 492 : 397);
        FolderText.MaxWidth = Math.Clamp(folderRoom, 0, 150);
        FolderText.Visibility = folderRoom < 40 ? Visibility.Collapsed : Visibility.Visible;
        double room = MainWidth - 300 - (SubagentsOpenButton?.Visibility == Visibility.Visible ? 120 : 0);
        ChatTitleText.MaxWidth = Math.Max(0, room);
        ChatTitleText.Visibility = room < 60 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RestoreWindowPlacement()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 32);
        Height = Math.Min(Height, area.Height - 32);

        var saved = _state.Window;
        if (saved == null || saved.Width < MinWidth || saved.Height < MinHeight) return;

        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var visible = Rect.Intersect(screens, new Rect(saved.Left, saved.Top, saved.Width, saved.Height));
        if (visible.IsEmpty || visible.Width < 240 || visible.Height < 120) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.Left;
        Top = saved.Top;
        Width = Math.Min(saved.Width, screens.Width);
        Height = Math.Min(saved.Height, screens.Height);
        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    private WindowPlacement CapturePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.Width) || double.IsInfinity(bounds.Width))
            return _state.Window ?? new WindowPlacement();
        return new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
    }
}
