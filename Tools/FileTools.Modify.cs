using System.Diagnostics;
using static SharpAgent.Tools.PathHelper;

namespace SharpAgent.Tools;

public static partial class FileTools
{
    private static string WriteFile(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = c.Resolve(a.Required("path"));
        var content = a.Required("content", allowEmpty: true);
        bool append = a.Bool("append");
        c.GuardAppFiles(path);
        if (Directory.Exists(path)) throw new ToolException($"По этому пути находится папка: {path}");

        bool existed = File.Exists(path);
        if (append && existed)
        {
            File.AppendAllText(path, content, TextFileIO.DetectEncodingOrUtf8(path));
            return $"Текст добавлен в конец файла: {path} (+{content.Length} символов)";
        }

        var (text, encoding, note) = TextFileIO.ForScript(path, content);
        TextFileIO.Write(path, text, encoding);
        return $"{(existed ? "Файл перезаписан" : "Файл создан")}: {path} ({FormatSize(new FileInfo(path).Length)}){note}";
    }

    private static string EditFile(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = c.Resolve(a.Required("path"));
        var oldText = a.Required("old_text");
        var newText = a.Required("new_text", allowEmpty: true);
        bool replaceAll = a.Bool("replace_all");
        c.GuardAppFiles(path);
        if (!File.Exists(path)) throw new ToolException($"Файл не найден: {path}");

        var (text, encoding) = TextFileIO.Read(path);
        int count = text.AsSpan().Count(oldText);
        if (count == 0 && text.Contains("\r\n") && !oldText.Contains("\r\n"))
        {
            oldText = oldText.Replace("\n", "\r\n");
            newText = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            count = text.AsSpan().Count(oldText);
        }

        if (count == 0)
            throw new ToolException("Фрагмент old_text не найден в файле. Прочитайте файл (read_file) и скопируйте фрагмент точно, со всеми пробелами и отступами.");
        if (count > 1 && !replaceAll)
            throw new ToolException($"Фрагмент old_text встречается {count} раз. Добавьте окружающий текст, чтобы он стал уникальным, или укажите replace_all=true.");

        TextFileIO.Write(path, text.Replace(oldText, newText, StringComparison.Ordinal), encoding);
        return $"Файл изменён: {path} (замен: {count})";
    }

    private static string CreateDirectory(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = c.Resolve(a.Required("path"));
        if (File.Exists(path)) throw new ToolException($"По этому пути уже есть файл: {path}");
        if (Directory.Exists(path)) return $"Папка уже существует: {path}";
        Directory.CreateDirectory(path);
        return $"Папка создана: {path}";
    }

    internal static (string Source, string Target) ResolveSourceAndTarget(ToolArgs a, ToolContext c)
    {
        var source = c.Resolve(a.Required("source"));
        var destination = c.Resolve(a.Required("destination"));
        if (Path.EndsInDirectorySeparator(destination) || (Directory.Exists(destination) && !SamePath(source, destination)))
            destination = Path.Combine(destination, Path.GetFileName(Path.TrimEndingDirectorySeparator(source)));
        return (source, destination);
    }

    private static void EnsureParent(string path)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

    private static string MovePath(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var (source, target) = ResolveSourceAndTarget(a, c);
        bool overwrite = a.Bool("overwrite");
        if (IsProtected(source)) throw new ToolException($"Системную папку перемещать нельзя: {source}");
        c.GuardAppFiles(source);
        c.GuardAppFiles(target);

        if (Directory.Exists(source))
        {
            if (IsInside(target, source)) throw new ToolException("Нельзя переместить папку внутрь неё самой.");
            if (Path.Exists(target)) throw new ToolException($"Назначение уже существует: {target}");
            EnsureParent(target);
            if (SameVolume(source, target))
            {
                Directory.Move(source, target);
            }
            else
            {
                CopyDirectory(source, target, overwrite: false, ct);
                Directory.Delete(source, recursive: true);
            }
            return $"Папка перемещена: {source} → {target}";
        }

        if (!File.Exists(source)) throw new ToolException($"Источник не найден: {source}");
        if (File.Exists(target) && !overwrite && !SamePath(source, target))
            throw new ToolException($"Файл уже существует: {target}. Чтобы заменить его, укажите overwrite=true.");
        EnsureParent(target);
        File.Move(source, target, overwrite);
        return $"Файл перемещён: {source} → {target}";
    }

    private static string CopyPath(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var (source, target) = ResolveSourceAndTarget(a, c);
        bool overwrite = a.Bool("overwrite");
        if (SamePath(source, target)) throw new ToolException("Источник и назначение совпадают.");
        c.GuardSecrets(source);
        c.GuardAppFiles(target);

        if (Directory.Exists(source))
        {
            if (IsInside(target, source)) throw new ToolException("Нельзя копировать папку внутрь неё самой.");
            EnsureParent(target);
            int files = CopyDirectory(source, target, overwrite, ct);
            return $"Папка скопирована: {source} → {target} (файлов: {files})";
        }

        if (!File.Exists(source)) throw new ToolException($"Источник не найден: {source}");
        if (File.Exists(target) && !overwrite)
            throw new ToolException($"Файл уже существует: {target}. Чтобы заменить его, укажите overwrite=true.");
        EnsureParent(target);
        File.Copy(source, target, overwrite);
        return $"Файл скопирован: {source} → {target}";
    }

    private static int CopyDirectory(string source, string target, bool overwrite, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(dest) && !overwrite)
                throw new ToolException($"Файл уже существует: {dest}. Чтобы заменять файлы, укажите overwrite=true.");
            File.Copy(file, dest, overwrite);
            count++;
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
            count += CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), overwrite, ct);
        return count;
    }

    private static string DeletePath(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = Path.TrimEndingDirectorySeparator(c.Resolve(a.Required("path")));
        bool permanent = a.Bool("permanent");
        if (!Path.Exists(path)) throw new ToolException($"Не найдено: {path}");
        c.GuardAppFiles(path);
        if (IsProtected(path))
            throw new ToolException($"Отказано: {path} — корень диска или системная папка пользователя, удалять её целиком нельзя.");

        if (!permanent)
        {
            RecycleBin.Send(path);
            return $"Перемещено в Корзину: {path}";
        }

        if (Directory.Exists(path))
        {
            var everything = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", everything))
            {
                ct.ThrowIfCancellationRequested();
                if (file.IsReadOnly) file.IsReadOnly = false;
            }
            Directory.Delete(path, recursive: true);
        }
        else
        {
            new FileInfo(path).IsReadOnly = false;
            File.Delete(path);
        }
        return $"Удалено безвозвратно: {path}";
    }

    private static string OpenPath(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var raw = a.Required("path");
        bool isUrl = IsWebUrl(raw);
        var target = isUrl ? raw.Trim() : c.Resolve(raw);
        if (!isUrl && !Path.Exists(target)) throw new ToolException($"Не найдено: {target}");
        if (c.SandboxRoot != null && (isUrl || (File.Exists(target) && !IsPassive(target))))
            throw new ToolException("В режиме песочницы можно открывать только папки и документы (не программы, скрипты и ссылки).");

        using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return $"Открыто: {target}";
    }

    private static bool IsWebUrl(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";

    private const string ScriptNote = "При запуске он выполнит записанные в нём команды.";

    private static ConfirmRequest? ConfirmWrite(ToolArgs a, ToolContext c)
    {
        var path = c.Resolve(a.Required("path"));
        c.GuardAppFiles(path);
        bool exists = File.Exists(path);
        if (exists && !a.Bool("append"))
            return new ConfirmRequest("Перезаписать файл?", path, "Перезаписать", Danger: true,
                Note: $"Сейчас в нём {FormatSize(new FileInfo(path).Length)} — прежнее содержимое пропадёт." +
                      (IsExecutable(path) ? " " + ScriptNote : ""));
        if (!IsExecutable(path)) return null;
        return exists
            ? new ConfirmRequest("Дописать в исполняемый файл или скрипт?", path, "Дописать", Note: ScriptNote)
            : new ConfirmRequest("Создать исполняемый файл или скрипт?", path, "Создать", Note: ScriptNote);
    }

    private static ConfirmRequest? ConfirmEdit(ToolArgs a, ToolContext c)
    {
        var path = c.Resolve(a.Required("path"));
        c.GuardAppFiles(path);
        return IsExecutable(path) && File.Exists(path)
            ? new ConfirmRequest("Изменить исполняемый файл или скрипт?", path, "Изменить", Note: ScriptNote)
            : null;
    }

    private static ConfirmRequest? ConfirmMoveOrCopy(ToolArgs a, ToolContext c)
    {
        if (!a.Bool("overwrite")) return null;
        var (source, target) = ResolveSourceAndTarget(a, c);
        c.GuardAppFiles(target);
        if (!Path.Exists(target)) return null;
        return new ConfirmRequest(Directory.Exists(target) ? "Заменить файлы в папке?" : "Заменить файл?", target, "Заменить",
            Danger: true, Note: "Источник: " + source);
    }

    private static ConfirmRequest? ConfirmDelete(ToolArgs a, ToolContext c)
    {
        var path = c.Resolve(a.Required("path"));
        c.GuardAppFiles(path);
        if (!Path.Exists(path) || IsProtected(path)) return null;
        bool folder = Directory.Exists(path);
        var what = folder ? "папку" : "файл";
        var content = folder ? "Вместе со всем содержимым. " : "";
        return a.Bool("permanent")
            ? new ConfirmRequest($"Удалить {what} безвозвратно?", path, "Удалить навсегда", Danger: true,
                Note: content + "Корзина не используется — восстановить не получится.")
            : new ConfirmRequest($"Удалить {what} в Корзину?", path, "Удалить в Корзину",
                Note: content + "Восстановить можно из Корзины.");
    }

    private static ConfirmRequest? ConfirmOpen(ToolArgs a, ToolContext c)
    {
        var raw = a.Required("path");
        if (IsWebUrl(raw)) return new ConfirmRequest("Открыть ссылку в браузере?", raw.Trim(), "Открыть");
        var path = c.Resolve(raw);
        if (c.SandboxRoot != null || Directory.Exists(path) || IsPassive(path)) return null;
        return IsExecutable(path)
            ? new ConfirmRequest("Запустить программу или скрипт?", path, "Запустить", Danger: true)
            : new ConfirmRequest("Открыть файл этого типа?", path, "Открыть", Note: "Windows откроет его связанной программой.");
    }
}
