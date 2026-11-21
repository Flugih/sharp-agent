using System.Reflection;

namespace SharpAgent;

public partial class App
{
    readonly DispatcherTimer _themeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    Mutex? _instanceMutex;
    EventWaitHandle? _activateSignal;

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    public static string Version { get; } =
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
         ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "1.0").Split('+')[0];

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is ["--office-preview", var source, var destination])
        {
            Shutdown(OfficePreview.Export(source, destination));
            return;
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        DispatcherUnhandledException += OnUnhandledException;
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(500));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(500));
        base.OnStartup(e);
        var id = "SharpAgent_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.AppFolder.ToLowerInvariant())))[..16];
        var mutex = new Mutex(true, id, out bool isFirst);
        if (!isFirst)
        {
            try
            {
                AllowSetForegroundWindow(-1);
                using var signal = EventWaitHandle.OpenExisting(id + "_show");
                signal.Set();
            }
            catch { }
            mutex.Dispose();
            Shutdown();
            return;
        }
        _instanceMutex = mutex;
        _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, id + "_show");
        ThreadPool.RegisterWaitForSingleObject(_activateSignal, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is MainWindow window) window.ActivateChatWindow();
        }), null, Timeout.Infinite, false);
        _themeTimer.Tick += (_, _) =>
        {
            _themeTimer.Stop();
            (MainWindow as MainWindow)?.ApplyTheme();
        };
        void Refresh()
        {
            _themeTimer.Stop();
            _themeTimer.Start();
        }
        SystemParameters.StaticPropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SystemParameters.WindowGlassColor)) Refresh();
        };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == Microsoft.Win32.UserPreferenceCategory.General) Refresh();
        };
        new MainWindow().Show();
    }

    void MenuItem_RequestBringIntoView(object s, RequestBringIntoViewEventArgs e)
    {
        if (s is MenuItem { IsMouseOver: true } && InputManager.Current.MostRecentInputDevice is MouseDevice) e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppNetwork.Stop();
        _activateSignal?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    void OnUnhandledException(object s, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        bool logged = LogError(e.Exception);
        MessageBox.Show(
            Tr("В программе произошла ошибка. Переписка сохранена, работу можно продолжить.\n\n", "An application error occurred. Your chat was saved and you can continue.\n\n") + e.Exception.Message +
            (logged ? Tr("\n\nПодробности записаны в файл:\n", "\n\nDetails were saved to:\n") + AppPaths.ErrorLogPath : ""),
            "Sharp Agent", MessageBoxButton.OK, MessageBoxImage.Warning);
        if (MainWindow is not SharpAgent.MainWindow) Shutdown();
    }

    public static bool LogError(Exception exception)
    {
        try
        {
            var info = new FileInfo(AppPaths.ErrorLogPath);
            if (info.Exists && info.Length > 1024 * 1024) info.Delete();
            File.AppendAllText(AppPaths.ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Sharp Agent {Version}\n{exception}\n\n",
                new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }
}
