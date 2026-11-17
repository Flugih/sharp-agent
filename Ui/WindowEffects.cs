using System.Windows.Interop;

namespace SharpAgent.Ui;

public static class WindowEffects
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;
    const uint FLASHW_TRAY = 0x2, FLASHW_TIMERNOFG = 0xC, WDA_EXCLUDEFROMCAPTURE = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags, Count, Timeout;
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("user32.dll")]
    static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    internal static void ExcludeFromCapture(Window window) =>
        SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, WDA_EXCLUDEFROMCAPTURE);

    public static void Flash(Window window)
    {
        if (window.IsActive) return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(), Hwnd = new WindowInteropHelper(window).Handle, Flags = FLASHW_TRAY | FLASHW_TIMERNOFG,
        };
        FlashWindowEx(ref info);
    }

    public static void ApplyFrame(Window window, Color border)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        Set(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsLight ? 0 : 1);
        Set(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        Set(hwnd, DWMWA_BORDER_COLOR, border.R | (border.G << 8) | (border.B << 16));
    }

    static void Set(IntPtr hwnd, int attribute, int value) => DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
}
