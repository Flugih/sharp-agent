using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SharpAgent.Tools;

namespace SharpAgent.Ui;

public static class FileActions
{
    public const string DragMarker = "SharpAgent.InternalDrag";
    private const int ErrorNoAssociation = 1155;

    public static event Action<string, bool>? Notify;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindContext, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint count, IntPtr[]? items, uint flags);

    public static void RaiseNotify(string message, bool isError) => Notify?.Invoke(message, isError);

    public static bool Exists(string path) => Path.Exists(path);

    public static void Open(Window owner, string path)
    {
        if (!Exists(path))
        {
            Notify?.Invoke($"Не найдено: {path}. Возможно, файл переместили или удалили.", true);
            return;
        }

        if (File.Exists(path) && !PathHelper.IsPassive(path) && !ConfirmOpen(owner, path)) return;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoAssociation)
        {
            OpenWith(path);
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"Не удалось открыть {Path.GetFileName(path)}: {ex.Message}", true);
        }
    }

    private static bool ConfirmOpen(Window owner, string path) => PathHelper.IsExecutable(path)
        ? DarkDialog.Ask(owner, "Запустить программу или скрипт?",
            "Открытие этого файла запустит его. Запускайте только то, чему доверяете.",
            "Запустить", danger: true, code: path, delayed: true)
        : DarkDialog.Ask(owner, "Открыть файл этого типа?",
            "Windows откроет его связанной программой. Такие файлы могут выполнять команды.",
            "Открыть", code: path, delayed: true);

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
            else
            {
                Notify?.Invoke($"Не найдено: {path}", true);
            }
            return;
        }

        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) == 0 &&
                SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0)
                return;
        }
        catch
        {
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }

        Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{path}\"" }, "Не удалось открыть Проводник: ");
    }

    public static void OpenFolder(string folder) =>
        Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } }, "Не удалось открыть папку: ");

    public static void OpenWith(string path) =>
        Start(new ProcessStartInfo("rundll32.exe") { Arguments = "shell32.dll,OpenAs_RunDLL " + path },
            "Не удалось открыть выбор программы: ");

    public static void CopyPath(string path) => Copy(path, "Путь скопирован");

    public static void Copy(string text, string done)
    {
        if (TextClipboard.SetText(text)) Notify?.Invoke(done, false);
        else Notify?.Invoke("Буфер обмена занят другой программой — попробуйте ещё раз", true);
    }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
            Notify?.Invoke("Эту ссылку открыть нельзя: " + url, true);
        else
            Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }, "Не удалось открыть браузер: ");
    }

    public static void OpenWindowsSettings(string page) =>
        Start(new ProcessStartInfo("ms-settings:" + page) { UseShellExecute = true }, "Не удалось открыть Параметры Windows: ");

    private static void Start(ProcessStartInfo info, string failure)
    {
        try
        {
            using var process = Process.Start(info);
        }
        catch (Exception ex)
        {
            Notify?.Invoke(failure + ex.Message, true);
        }
    }

    public static ContextMenu BuildMenu(Window owner, string path, params MenuItem[] extra)
    {
        bool isFolder = Directory.Exists(path);
        var menu = new ContextMenu();

        var open = Item(isFolder ? "Открыть папку" : "Открыть в программе по умолчанию", Glyphs.Open, () => Open(owner, path));
        if (isFolder) open.FontWeight = FontWeights.SemiBold;
        menu.Items.Add(open);
        menu.Items.Add(Item("Показать в папке", Glyphs.Reveal, () => Reveal(path)));
        if (!isFolder) menu.Items.Add(Item("Открыть с помощью…", Glyphs.OpenWith, () => OpenWith(path)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Копировать путь", Glyphs.Copy, () => CopyPath(path)));
        if (extra.Length > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var item in extra) menu.Items.Add(item);
        }
        return menu;
    }

    public static MenuItem Item(string header, string? glyph, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
        if (glyph != null)
            item.Icon = new TextBlock { Text = glyph, FontFamily = Theme.Icons, FontSize = 14 };
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
            var now = e.GetPosition(null);
            if (Math.Abs(now.X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(now.Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

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
        try
        {
            Clipboard.SetDataObject(text, copy: true);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }
}
