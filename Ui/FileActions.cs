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
            Notify?.Invoke(Tr($"Не найдено: {path}. Возможно, файл переместили или удалили.", $"File missing: {path}. It may have been moved or deleted."), true);
            return;
        }
        if (File.Exists(path) && PathHelper.FinalPath(path) is var real && !(PathHelper.IsPassive(path) && PathHelper.IsPassive(real)))
        {
            bool run = PathHelper.IsExecutable(path) || PathHelper.IsExecutable(real);
            if (!DarkDialog.Ask(owner, run ? Tr("Запустить программу или скрипт?", "Run this program or script?") : Tr("Открыть файл этого типа?", "Open this file type?"),
                    run ? Tr("Открытие этого файла запустит его. Запускайте только то, чему доверяете.", "Opening this file will run it. Only run files you trust.")
                        : Tr("Windows откроет его связанной программой. Такие файлы могут выполнять команды.", "Windows will open this file in its associated app. This file type can run commands."),
                    run ? Tr("Запустить", "Run") : Tr("Открыть", "Open"), danger: run, code: path, delayed: true))
                return;
        }
        Start(new ProcessStartInfo(path) { UseShellExecute = true }, Tr($"Не удалось открыть {Path.GetFileName(path)}: ", $"Could not open {Path.GetFileName(path)}: "), () => OpenWith(path));
    }

    public static void Reveal(string path)
    {
        if (!Exists(path))
        {
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
            if (Directory.Exists(parent))
            {
                Notify?.Invoke(Tr($"Файла {Path.GetFileName(path)} уже нет — открыта папка, где он лежал.", $"{Path.GetFileName(path)} is missing. Opened its parent folder."), false);
                OpenFolder(parent);
            }
            else Notify?.Invoke(Tr($"Не найдено: {path}", $"File missing: {path}"), true);
            return;
        }
        nint pidl = 0;
        try
        {
            if (SHParseDisplayName(path, 0, out pidl, 0, out _) == 0 && SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0) return;
        }
        catch { }
        finally { Marshal.FreeCoTaskMem(pidl); }
        Start(new ProcessStartInfo(Explorer, $"/select,\"{path}\""), Tr("Не удалось открыть Проводник: ", "Could not open File Explorer: "));
    }

    public static void OpenFolder(string folder) => Start(new ProcessStartInfo(Explorer) { ArgumentList = { folder } }, Tr("Не удалось открыть папку: ", "Could not open folder: "));

    static void OpenWith(string path) => Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "rundll32.exe"),
        "shell32.dll,OpenAs_RunDLL " + path), Tr("Не удалось открыть выбор программы: ", "Could not open the app picker: "));

    public static void Copy(string text, string done)
    {
        bool copied = TextClipboard.SetText(text);
        Notify?.Invoke(copied ? done : Tr("Буфер обмена занят другой программой — попробуйте ещё раз", "The clipboard is busy. Try again."), !copied);
    }

    public static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            if (AppNetwork.CustomRoute && !_routeNoticeShown)
            {
                _routeNoticeShown = true;
                Notify?.Invoke(Tr("Ссылка откроется во внешней программе с её сетевыми настройками.", "This link will open in an external app using that app’s network settings."), false);
            }
            Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }, Tr("Не удалось открыть браузер: ", "Could not open browser: "));
        }
        else Notify?.Invoke(Tr("Эту ссылку открыть нельзя: ", "Could not open this link: ") + url, true);
    }

    public static void OpenWindowsSettings(string page) =>
        Start(new ProcessStartInfo("ms-settings:" + page) { UseShellExecute = true }, Tr("Не удалось открыть Параметры Windows: ", "Could not open Windows Settings: "));

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
        var open = Item(isFolder ? Tr("Открыть папку", "Open folder") : Tr("Открыть в программе по умолчанию", "Open in default app"), Glyphs.Open,
            () => Open(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? owner, path));
        if (isFolder) open.FontWeight = FontWeights.SemiBold;
        var menu = new ContextMenu { Items = { open, Item(Tr("Показать в папке", "Show in folder"), Glyphs.Reveal, () => Reveal(path)) } };
        if (!isFolder) menu.Items.Add(Item(Tr("Открыть с помощью…", "Open with…"), Glyphs.OpenWith, () => OpenWith(path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Tr("Копировать путь", "Copy path"), Glyphs.Copy, () => Copy(path, Tr("Путь скопирован", "Path copied"))));
        if (extra.Length > 0) menu.Items.Add(new Separator());
        foreach (var item in extra) menu.Items.Add(item);
        return menu;
    }

    public static MenuItem Item(string header, string? glyph, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (glyph != null) item.Icon = new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = 16 };
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
