using System.Windows.Interop;

namespace SharpAgent.Ui;

internal sealed class BrowserView(Uri initial, NetworkMode mode, BrowserProxy? proxy, string downloads) : HwndHost
{
    static readonly Lazy<CreateEnvironment> Loader = new(FindLoader, LazyThreadSafetyMode.PublicationOnly);
    Callbacks? _callbacks;
    IEdgeController? _controller;
    IEdgePage? _page;
    IntPtr _host;
    public Uri Address { get; private set; } = initial;
    public string Title { get; private set; } = initial.Host;
    public IntPtr NativeHandle => _host;
    public bool CanBack => _page is { } page && page.CanGoBack() != 0;
    public bool CanForward => _page is { } page && page.CanGoForward() != 0;
    public event Action? Changed;
    public event Action<string>? Failed;
    public event Action<Uri>? Popup;

    public void Navigate(Uri uri)
    {
        Address = uri;
        _page?.Navigate(uri.AbsoluteUri);
    }

    public void Back() { if (CanBack) _page!.GoBack(); }
    public void Forward() { if (CanForward) _page!.GoForward(); }
    public void Reload() => _page?.Reload();

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        _host = CreateWindowEx(0, "STATIC", "", 0x56000000, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_host == IntPtr.Zero) throw new Win32Exception();
        Dispatcher.InvokeAsync(Start);
        return new(this, _host);
    }

    void Start()
    {
        if (_host == IntPtr.Zero) return;
        try
        {
            var switches = proxy != null
                ? $"--proxy-server=socks5://127.0.0.1:{proxy.Port} --proxy-bypass-list=<-loopback> --force-webrtc-ip-handling-policy=disable_non_proxied_udp --disable-quic --host-resolver-rules=\"MAP * ~NOTFOUND, EXCLUDE 127.0.0.1\""
                : mode == NetworkMode.Direct ? "--no-proxy-server" : "";
            var profile = Path.Combine(AppPaths.AppFolder, "auth", "browser", mode.ToString());
            Directory.CreateDirectory(profile);
            _callbacks = new(this, proxy);
            Marshal.ThrowExceptionForHR(Loader.Value(1, 0, profile, new EdgeOptions(switches), _callbacks));
        }
        catch (Exception ex) { Failed?.Invoke(ErrorText(ex)); }
    }

    static string ErrorText(Exception ex) => ex is FileNotFoundException or EntryPointNotFoundException or DllNotFoundException
        ? "Для браузера требуется установленный Microsoft Edge WebView2 Runtime."
        : $"Не удалось открыть браузер (0x{ex.HResult:X8}).";

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    sealed class Callbacks(BrowserView view, BrowserProxy? relay)
        : IEnvironmentReady, IControllerReady, ISourceChanged, ITitleChanged, IHistoryChanged, INavigationStarting, IPopupRequested, IPermissionRequested, IDownloadStarting
    {
        int _popups;
        long _lastPopup;

        void IEnvironmentReady.Invoke(int error, object? environment)
        {
            if (view._callbacks != this) return;
            try
            {
                Marshal.ThrowExceptionForHR(error);
                ((IEdgeEnvironment)environment!).CreateController(view._host, this);
            }
            catch (Exception ex) { view.Failed?.Invoke(ErrorText(ex)); }
        }

        void IControllerReady.Invoke(int error, object? controller)
        {
            try
            {
                Marshal.ThrowExceptionForHR(error);
                var control = (IEdgeController)controller!;
                if (view._callbacks != this) { control.Close(); return; }
                view._controller = control;
                var page = view._page = control.GetPage();
                relay?.Allow(page.BrowserPid());
                page.OnStarting(this, out _);
                page.OnPopup(this, out _);
                page.OnPermission(this, out _);
                ((IEdgePage4)page).OnDownload(this, out _);
                page.OnSource(this, out _);
                page.OnHistory(this, out _);
                page.OnTitle(this, out _);
                view.Resize();
                control.SetVisible(1);
                page.Navigate(view.Address.AbsoluteUri);
            }
            catch (Exception ex) { view.Failed?.Invoke(ErrorText(ex)); }
        }

        void INavigationStarting.Invoke(IntPtr sender, object args)
        {
            _popups = 0;
            var request = (IStartingArgs)args;
            if (!WebUri(request.Uri(), out _)) request.Cancel(1);
        }

        void IPopupRequested.Invoke(IntPtr sender, object args)
        {
            var request = (IPopupArgs)args;
            request.Handled(1);
            var now = Environment.TickCount64;
            // User gestures are cheap to fake, so one page still gets a limited number of tabs.
            if (request.UserInitiated() == 0 || !WebUri(request.Uri(), out var uri) || now - _lastPopup < 1000 || _popups >= 20) return;
            _popups++;
            _lastPopup = now;
            view.Dispatcher.InvokeAsync(() => view.Popup?.Invoke(uri!));
        }

        // Pages get no camera, microphone, location, clipboard or other permissions.
        void IPermissionRequested.Invoke(IntPtr sender, object args) => ((IPermissionArgs)args).State(2);

        void IDownloadStarting.Invoke(IntPtr sender, object args)
        {
            var request = (IDownloadArgs)args;
            var deferral = request.GetDeferral();
            view.Dispatcher.InvokeAsync(() =>
            {
                try { view.AskDownloadPath(request); }
                finally { deferral.Complete(); }
            });
        }

        public void Invoke(IntPtr sender, object args)
        {
            if (view._callbacks != this || view._page is not { } page) return;
            if (WebUri(page.Source(), out var uri)) view.Address = uri!;
            var title = page.Title();
            view.Title = string.IsNullOrWhiteSpace(title) ? view.Address.Host : title;
            view.Changed?.Invoke();
        }
    }

    void AskDownloadPath(IDownloadArgs request)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Сохранить загрузку", FileName = Path.GetFileName(request.ResultFilePath()) };
        if (Directory.Exists(downloads)) dialog.InitialDirectory = downloads;
        if (_host != IntPtr.Zero && dialog.ShowDialog(Window.GetWindow(this)) == true) request.SetResultFilePath(dialog.FileName);
        else request.Cancel(1);
    }

    static bool WebUri(string text, out Uri? uri) => Uri.TryCreate(text, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0;

    protected override void OnWindowPositionChanged(Rect bounds)
    {
        base.OnWindowPositionChanged(bounds);
        Resize();
    }

    void Resize()
    {
        if (_controller != null && GetClientRect(_host, out var bounds)) _controller.SetBounds(bounds);
    }

    protected override bool TabIntoCore(TraversalRequest request)
    {
        if (_controller == null) return false;
        _controller.MoveFocus(request.FocusNavigationDirection == FocusNavigationDirection.Previous ? 2 : 1);
        return true;
    }

    protected override void DestroyWindowCore(HandleRef window)
    {
        _host = IntPtr.Zero;
        _callbacks = null;
        _controller?.Close();
        _controller = null;
        _page = null;
        DestroyWindow(window.Handle);
    }

    static CreateEnvironment FindLoader()
    {
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData })
        {
            var root = Path.Combine(Environment.GetFolderPath(folder), "Microsoft", "EdgeWebView", "Application");
            if (!Directory.Exists(root)) continue;
            foreach (var dir in new DirectoryInfo(root).EnumerateDirectories().Where(d => Version.TryParse(d.Name, out _)).OrderByDescending(d => Version.Parse(d.Name)))
            {
                var path = Path.Combine(dir.FullName, "EBWebView", arch, "EmbeddedBrowserWebView.dll");
                if (!File.Exists(path)) continue;
                var module = NativeLibrary.Load(path, typeof(BrowserView).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
                return Marshal.GetDelegateForFunctionPointer<CreateEnvironment>(NativeLibrary.GetExport(module, "CreateWebViewEnvironmentWithOptionsInternal"));
            }
        }
        throw new FileNotFoundException();
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    delegate int CreateEnvironment(byte installed, int type, string profile, IEdgeOptions options, IEnvironmentReady handler);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int exStyle, string cls, string title, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr handle);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr handle, out BrowserRect rect);
}
