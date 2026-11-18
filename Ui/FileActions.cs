namespace SharpAgent.Ui;

public static class FileActions
{
    public const string DragMarker = "SharpAgent.InternalDrag";

    static readonly string Explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public static event Action<string, bool>? Notify;

    static bool _routeNoticeShown;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, nint bindContext, out nint pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    static extern int SHOpenFolderAndSelectItems(nint pidlFolder, uint count, nint[]? items, uint flags);

    public static void RaiseNotify(string message, bool isError) => Notify?.Invoke(message, isError);

    public static bool Exists(string path) => Path.Exists(path);

    public static void Open(Window owner, string path)
    {
        if (!Exists(path))
        {
            Notify?.Invoke($"Не найдено: {path}. Возможно, файл переместили или удалили.", true);
            return;
        }
        if (File.Exists(path) && PathHelper.FinalPath(path) is var real && !(PathHelper.IsPassive(path) && PathHelper.IsPassive(real)))
        {
            bool run = PathHelper.IsExecutable(path) || PathHelper.IsExecutable(real);
            if (!DarkDialog.Ask(owner, run ? "Запустить программу или скрипт?" : "Открыть файл этого типа?",
                    run ? "Открытие этого файла запустит его. Запускайте только то, чему доверяете."
                        : "Windows откроет его связанной программой. Такие файлы могут выполнять команды.",
                    run ? "Запустить" : "Открыть", danger: run, code: path, delayed: true))
                return;
        }
        Start(new ProcessStartInfo(path) { UseShellExecute = true }, $"Не удалось открыть {Path.GetFileName(path)}: ", () => OpenWith(path));
    }

    public static void Reveal(string path)
    {
        if (!Exists(path))
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
            if (Directory.Exists(parent))
            {
                Notify?.Invoke($"Файла {Path.GetFileName(path)} уже нет — открыта папка, где он лежал.", false);
                OpenFolder(parent);
            }
            else Notify?.Invoke($"Не найдено: {path}", true);
            return;
        }
        nint pidl = 0;
        try
        {
            if (SHParseDisplayName(path, 0, out pidl, 0, out _) == 0 && SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0) return;
        }
        catch { }
        finally { Marshal.FreeCoTaskMem(pidl); }
        Start(new ProcessStartInfo(Explorer, $"/select,\"{path}\""), "Не удалось открыть Проводник: ");
    }

    public static void OpenFolder(string folder) => Start(new ProcessStartInfo(Explorer) { ArgumentList = { folder } }, "Не удалось открыть папку: ");

    static void OpenWith(string path) => Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
        "shell32.dll,OpenAs_RunDLL " + path), "Не удалось открыть выбор программы: ");

    public static void Copy(string text, string done)
    {
        bool copied = TextClipboard.SetText(text);
        Notify?.Invoke(copied ? done : "Буфер обмена занят другой программой — попробуйте ещё раз", !copied);
    }

    public static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            if (AppNetwork.CustomRoute && !_routeNoticeShown)
            {
                _routeNoticeShown = true;
                Notify?.Invoke("Ссылка откроется во внешней программе с её сетевыми настройками.", false);
            }
            Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }, "Не удалось открыть браузер: ");
        }
        else Notify?.Invoke("Эту ссылку открыть нельзя: " + url, true);
    }

    public static void OpenWindowsSettings(string page) =>
        Start(new ProcessStartInfo("ms-settings:" + page) { UseShellExecute = true }, "Не удалось открыть Параметры Windows: ");

    static void Start(ProcessStartInfo info, string failure, Action? noAssociation = null)
    {
        try { Process.Start(info)?.Dispose(); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1155 && noAssociation != null) { noAssociation(); }
        catch (Exception ex) { Notify?.Invoke(failure + ex.Message, true); }
    }

    public static ContextMenu BuildMenu(Window owner, string path, params MenuItem[] extra)
    {
        bool isFolder = Directory.Exists(path);
        // Links and cards also appear in the mini window, so the dialog goes over the window the menu was opened in.
        var open = Item(isFolder ? "Открыть папку" : "Открыть в программе по умолчанию", Glyphs.Open,
            () => Open(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? owner, path));
        if (isFolder) open.FontWeight = FontWeights.SemiBold;
        var menu = new ContextMenu { Items = { open, Item("Показать в папке", Glyphs.Reveal, () => Reveal(path)) } };
        if (!isFolder) menu.Items.Add(Item("Открыть с помощью…", Glyphs.OpenWith, () => OpenWith(path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Копировать путь", Glyphs.Copy, () => Copy(path, "Путь скопирован")));
        if (extra.Length > 0) menu.Items.Add(new Separator());
        foreach (var item in extra) menu.Items.Add(item);
        return menu;
    }

    public static MenuItem Item(string header, string? glyph, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (glyph != null) item.Icon = new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = 14 };
        item.Click += (_, _) => action();
        return item;
    }

    public static void EnableDragOut(IInputElement element, string path)
    {
        Point? start = null;
        element.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(null);
        element.PreviewMouseLeftButtonUp += (_, _) => start = null;
        element.MouseMove += (sender, e) =>
        {
            if (start is not { } origin || e.LeftButton != MouseButtonState.Pressed) return;
            var moved = e.GetPosition(null) - origin;
            if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            start = null;
            if (!Exists(path) || sender is not DependencyObject source) return;
            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            data.SetData(DragMarker, true);
            try { DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Link); }
            catch { }
        };
    }
}

public static class TextClipboard
{
    public static bool SetText(string text)
    {
        try { Clipboard.SetDataObject(text, copy: true); }
        catch (ExternalException) { return false; }
        return true;
    }
}
