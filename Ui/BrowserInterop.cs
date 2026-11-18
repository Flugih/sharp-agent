namespace SharpAgent.Ui;

[ComImport, Guid("b96d755e-0319-4e92-a296-23436f46a1fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEdgeEnvironment
{
    void CreateController(IntPtr parent, IControllerReady handler);
}

[ComImport, Guid("4d00c0d1-9434-4eb6-8078-8697a560334f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEdgeController
{
    void _VtblGap1_1();
    void SetVisible(int visible);
    void _VtblGap2_1();
    void SetBounds(BrowserRect bounds);
    void _VtblGap3_5();
    void MoveFocus(int reason);
    void _VtblGap4_11();
    void Close();
    IEdgePage GetPage();
}

[StructLayout(LayoutKind.Sequential)]
internal struct BrowserRect { public int Left, Top, Right, Bottom; }

[ComImport, Guid("76eceacb-0462-4d94-ac83-423a6793775e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEdgePage
{
    void _VtblGap1_1();
    [return: MarshalAs(UnmanagedType.LPWStr)] string Source();
    void Navigate([MarshalAs(UnmanagedType.LPWStr)] string url);
    void _VtblGap2_1();
    void OnStarting(INavigationStarting handler, out long token);
    void _VtblGap3_3();
    void OnSource(ISourceChanged handler, out long token);
    void _VtblGap4_1();
    void OnHistory(IHistoryChanged handler, out long token);
    void _VtblGap5_9();
    void OnPermission(IPermissionRequested handler, out long token);
    void _VtblGap6_7();
    void Reload();
    void _VtblGap7_5();
    uint BrowserPid();
    int CanGoBack();
    int CanGoForward();
    void GoBack();
    void GoForward();
    void _VtblGap8_2();
    void OnPopup(IPopupRequested handler, out long token);
    void _VtblGap9_1();
    void OnTitle(ITitleChanged handler, out long token);
    void _VtblGap10_1();
    [return: MarshalAs(UnmanagedType.LPWStr)] string Title();
}

[ComImport, Guid("20d02d59-6df2-42dc-bd06-f98a694b1302"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEdgePage4
{
    void _VtblGap1_72();
    void OnDownload(IDownloadStarting handler, out long token);
}

[ComImport, Guid("973ae2ef-ff18-4894-8fb2-3c758f046810"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPermissionArgs
{
    void _VtblGap1_4();
    void State(int state);
}

[ComImport, Guid("e99bbe21-43e9-4544-a732-282764eafa60"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDownloadArgs
{
    void _VtblGap1_2();
    void Cancel(int cancel);
    [return: MarshalAs(UnmanagedType.LPWStr)] string ResultFilePath();
    void SetResultFilePath([MarshalAs(UnmanagedType.LPWStr)] string path);
    void _VtblGap2_2();
    IDeferral GetDeferral();
}

[ComImport, Guid("c10e7f7b-b585-46f0-a623-8befbf3e4ee0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDeferral { void Complete(); }

[ComImport, Guid("5b495469-e119-438a-9b18-7604f25f2e49"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IStartingArgs
{
    [return: MarshalAs(UnmanagedType.LPWStr)] string Uri();
    void _VtblGap1_4();
    void Cancel(int cancel);
}

[ComImport, Guid("34acb11c-fc37-4418-9132-f9c21d1eafb9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPopupArgs
{
    [return: MarshalAs(UnmanagedType.LPWStr)] string Uri();
    void _VtblGap1_2();
    void Handled(int handled);
    void _VtblGap2_1();
    int UserInitiated();
}

[ComVisible(true), Guid("4e8a3389-c9d8-4bd2-b6b5-124fee6cc14d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnvironmentReady { void Invoke(int error, [MarshalAs(UnmanagedType.IUnknown)] object? environment); }

[ComVisible(true), Guid("6c4819f3-c9b7-4260-8127-81f94fb5f68c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IControllerReady { void Invoke(int error, [MarshalAs(UnmanagedType.IUnknown)] object? controller); }

[ComVisible(true), Guid("3c067f9f-5388-4772-8b48-79f7ef1ab37c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ISourceChanged { void Invoke(IntPtr sender, [MarshalAs(UnmanagedType.IUnknown)] object args); }

[ComVisible(true), Guid("f5f2b923-953e-4042-9f95-f3a118e1afd4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITitleChanged { void Invoke(IntPtr sender, [MarshalAs(UnmanagedType.IUnknown)] object args); }

[ComVisible(true), Guid("c79a420c-efd9-4058-9295-3e8b4bcab645"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IHistoryChanged { void Invoke(IntPtr sender, [MarshalAs(UnmanagedType.IUnknown)] object args); }

[ComVisible(true), Guid("9adbe429-f36d-432b-9ddc-f8881fbd76e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface INavigationStarting { void Invoke(IntPtr sender, [MarshalAs(UnmanagedType.IUnknown)] object args); }

[ComVisible(true), Guid("d4c185fe-c81c-4989-97af-2d3fa7ab5651"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPopupRequested { void Invoke(IntPtr sender, [MarshalAs(UnmanagedType.IUnknown)] object args); }

[ComVisible(true), Guid("15e1c6a3-c72a-4df3-91d7-d097fbec6bfd"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IPermissionRequested { void Invoke(IntPtr sender, [MarshalAs(UnmanagedType.IUnknown)] object args); }

[ComVisible(true), Guid("efedc989-c396-41ca-83f7-07f845a55724"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDownloadStarting { void Invoke(IntPtr sender, [MarshalAs(UnmanagedType.IUnknown)] object args); }

[ComVisible(true), Guid("2fde08a8-1e9a-4766-8c05-95a9ceb9d1c5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEdgeOptions
{
    string Arguments { [return: MarshalAs(UnmanagedType.LPWStr)] get; [param: MarshalAs(UnmanagedType.LPWStr)] set; }
    string? Language { [return: MarshalAs(UnmanagedType.LPWStr)] get; [param: MarshalAs(UnmanagedType.LPWStr)] set; }
    string Version { [return: MarshalAs(UnmanagedType.LPWStr)] get; [param: MarshalAs(UnmanagedType.LPWStr)] set; }
    int SingleSignOn { get; set; }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
internal sealed class EdgeOptions(string arguments) : IEdgeOptions
{
    public string Arguments { get; set; } = arguments;
    public string? Language { get; set; }
    // The runtime hides interfaces newer than this SDK version, and DownloadStarting needs 902.
    public string Version { get; set; } = "92.0.902.49";
    public int SingleSignOn { get; set; }
}
