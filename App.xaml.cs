using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SharpAgent.Core;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class App : Application
{
    private readonly DispatcherTimer _accentTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateSignal;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    public static string Version { get; } =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
         ?? "1.0").Split('+')[0];

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--office-preview", var source, var destination])
        {
            Shutdown(OfficePreview.Export(source, destination));
            return;
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);

        var id = "SharpAgent_" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.AppFolder.ToLowerInvariant())))[..16];
        _instanceMutex = new Mutex(initiallyOwned: true, id, out bool isFirst);
        if (!isFirst)
        {
            try
            {
                AllowSetForegroundWindow(-1);
                using var signal = EventWaitHandle.OpenExisting(id + "_show");
                signal.Set();
            }
            catch
            {
            }
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, id + "_show");
        ThreadPool.RegisterWaitForSingleObject(_activateSignal, (_, _) => Dispatcher.BeginInvoke(BringToFront),
            null, Timeout.Infinite, executeOnlyOnce: false);

        Theme.ApplySystemAccent(Resources);
        _accentTimer.Tick += OnAccentTimer;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
        new MainWindow().Show();
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SystemParameters.WindowGlassColor)) return;
        _accentTimer.Stop();
        _accentTimer.Start();
    }

    private void MenuItem_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        if (sender is MenuItem { IsMouseOver: true } && InputManager.Current.MostRecentInputDevice is MouseDevice)
            e.Handled = true;
    }

    private void OnAccentTimer(object? sender, EventArgs e)
    {
        _accentTimer.Stop();
        Theme.ApplySystemAccent(Resources);
        (MainWindow as MainWindow)?.OnAccentChanged();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activateSignal?.Dispose();
        if (_instanceMutex != null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private void BringToFront()
    {
        if (MainWindow is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        bool logged = LogError(e.Exception);
        MessageBox.Show(
            "В программе произошла ошибка. Переписка сохранена, работу можно продолжить.\n\n" +
            e.Exception.Message +
            (logged ? $"\n\nПодробности записаны в файл:\n{AppPaths.ErrorLogPath}" : ""),
            "Sharp Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public static bool LogError(Exception exception)
    {
        try
        {
            var info = new FileInfo(AppPaths.ErrorLogPath);
            if (info.Exists && info.Length > 1024 * 1024) info.Delete();
            File.AppendAllText(AppPaths.ErrorLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Sharp Agent {Version}\n{exception}\n\n", new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
