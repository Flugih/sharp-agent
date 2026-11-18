using static SharpAgent.Tools.PathHelper;

namespace SharpAgent.Tools;

public static partial class FileTools
{
    const string ScriptNote = "При запуске он выполнит записанные в нём команды.";
    static readonly EnumerationOptions Everything = new() { RecurseSubdirectories = true, AttributesToSkip = 0 };

    static string GuardedPath(ToolArgs a, ToolContext c)
    {
        var path = c.Resolve(a.Required("path"));
        c.GuardAppFiles(path);
        return path;
    }

    static string WriteFile(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = GuardedPath(a, c);
        var content = a.Required("content", allowEmpty: true);
        if (Directory.Exists(path)) throw new ToolException($"По этому пути находится папка: {path}");
        bool existed = File.Exists(path);
        if (existed && a.Bool("append")) { TextFileIO.Append(path, content); return $"Текст добавлен в конец файла: {path} (+{content.Length} символов)"; }
        var (text, encoding, note) = TextFileIO.ForScript(path, content);
        TextFileIO.Write(path, text, encoding);
        return $"{(existed ? "Файл перезаписан" : "Файл создан")}: {path} ({FormatSize(new FileInfo(path).Length)}){note}";
    }

    static string EditFile(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = GuardedPath(a, c);
        var oldText = a.Required("old_text");
        var newText = a.Required("new_text", allowEmpty: true);
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
        if (count > 1 && !a.Bool("replace_all"))
            throw new ToolException($"Фрагмент old_text встречается {count} раз. Добавьте окружающий текст, чтобы он стал уникальным, или укажите replace_all=true.");
        TextFileIO.Write(path, text.Replace(oldText, newText, StringComparison.Ordinal), encoding);
        return $"Файл изменён: {path} (замен: {count})";
    }

    static string CreateDirectory(ToolArgs a, ToolContext c, CancellationToken ct)
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
        return (source, Path.EndsInDirectorySeparator(destination) || Directory.Exists(destination) && !SamePath(source, destination)
            ? Path.Combine(destination, Path.GetFileName(Path.TrimEndingDirectorySeparator(source))) : destination);
    }

    static void EnsureParent(string path) { if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is { Length: > 0 } parent) Directory.CreateDirectory(parent); }

    static ToolException AlreadyExists(string path) => new($"Файл уже существует: {path}. Чтобы заменить его, укажите overwrite=true.");

    static void GuardSecretCopy(ToolContext c, string from, string to) { if (c.IsSecret(from) && !c.IsSecret(to)) c.GuardSecrets(from); }

    static string MovePath(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var (source, target) = ResolveSourceAndTarget(a, c);
        if (IsProtected(source)) throw new ToolException($"Системную папку перемещать нельзя: {source}");
        c.GuardAppFiles(source);
        c.GuardAppFiles(target);
        GuardSecretCopy(c, source, target);
        if (Directory.Exists(source))
        {
            if (IsInside(FinalPath(target), FinalPath(source))) throw new ToolException("Нельзя переместить папку внутрь неё самой.");
            if (Path.Exists(target)) throw new ToolException($"Назначение уже существует: {target}");
            if (c.AppFolder != null && IsInside(FinalPath(source), c.AppFolder))
            {
                RejectLink(source);
                foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", Everything))
                {
                    ct.ThrowIfCancellationRequested();
                    RejectLink(entry.FullName);
                    GuardSecretCopy(c, entry.FullName, Path.Join(target, Path.GetRelativePath(source, entry.FullName)));
                }
            }
            EnsureParent(target);
            if (SameVolume(source, target)) Directory.Move(source, target);
            else MoveDirectoryAcrossVolumes(c, source, target, ct);
            return $"Папка перемещена: {source} → {target}";
        }
        if (!File.Exists(source)) throw new ToolException($"Источник не найден: {source}");
        bool overwrite = a.Bool("overwrite");
        if (File.Exists(target) && !overwrite && !SamePath(source, target)) throw AlreadyExists(target);
        EnsureParent(target);
        File.Move(source, target, overwrite);
        return $"Файл перемещён: {source} → {target}";
    }

    static string CopyPath(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var (source, target) = ResolveSourceAndTarget(a, c);
        bool overwrite = a.Bool("overwrite");
        if (SamePath(source, target)) throw new ToolException("Источник и назначение совпадают.");
        c.GuardSecrets(source);
        c.GuardAppFiles(target);
        if (Directory.Exists(source))
        {
            if (IsInside(FinalPath(target), FinalPath(source))) throw new ToolException("Нельзя копировать папку внутрь неё самой.");
            EnsureParent(target);
            return $"Папка скопирована: {source} → {target} (файлов: {CopyDirectory(c, source, target, overwrite, ct)})";
        }
        if (!File.Exists(source)) throw new ToolException($"Источник не найден: {source}");
        if (File.Exists(target) && !overwrite) throw AlreadyExists(target);
        EnsureParent(target);
        File.Copy(source, target, overwrite);
        return $"Файл скопирован: {source} → {target}";
    }

    static void MoveDirectoryAcrossVolumes(ToolContext context, string source, string target, CancellationToken ct)
    {
        var stage = Path.Combine(Path.GetDirectoryName(target)!, ".sharp-move-" + Guid.NewGuid().ToString("N"));
        List<string> directories = [];
        List<string> moved = [];
        try
        {
            Transfer(source);
            ct.ThrowIfCancellationRequested();
            Directory.Move(stage, target);
        }
        catch
        {
            bool restored = true;
            foreach (var file in moved.AsEnumerable().Reverse())
            {
                try { File.Move(Path.Combine(stage, Path.GetRelativePath(source, file)), file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { restored = false; }
            }
            foreach (var dir in directories.AsEnumerable().Reverse())
            {
                try { Directory.Delete(Path.Combine(stage, Path.GetRelativePath(source, dir))); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { restored = false; }
            }
            if (!restored) throw new ToolException($"Перенос прерван. Не удалось вернуть все файлы в {source}. Оставшиеся файлы сохранены в {stage}.");
            throw;
        }
        try
        {
            foreach (var dir in directories.AsEnumerable().Reverse()) Directory.Delete(dir, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ToolException($"Файлы перенесены в {target}. Исходную папку {source} не удалось убрать. Проверьте оставшиеся файлы.");
        }

        void Transfer(string dir)
        {
            ct.ThrowIfCancellationRequested();
            RejectLink(dir);
            var destination = Path.Combine(stage, Path.GetRelativePath(source, dir));
            Directory.CreateDirectory(destination);
            directories.Add(dir);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                ct.ThrowIfCancellationRequested();
                RejectLink(file);
                GuardSecretCopy(context, file, Path.Combine(target, Path.GetRelativePath(source, file)));
                File.Move(file, Path.Combine(destination, Path.GetFileName(file)));
                moved.Add(file);
            }
            foreach (var child in Directory.EnumerateDirectories(dir)) Transfer(child);
        }
    }

    static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ToolException($"Не удалось перенести или скопировать ссылку на файл или папку: {path}");
    }

    static int CopyDirectory(ToolContext c, string source, string target, bool overwrite, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RejectLink(source);
        if (Directory.Exists(target)) RejectLink(target);
        Directory.CreateDirectory(target);
        int count = 0;
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(target, Path.GetFileName(file));
            RejectLink(file);
            if (File.Exists(dest)) RejectLink(dest);
            GuardSecretCopy(c, file, dest);
            if (File.Exists(dest) && !overwrite)
                throw new ToolException($"Файл уже существует: {dest}. Чтобы заменять файлы, укажите overwrite=true.");
            File.Copy(file, dest, overwrite);
            count++;
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
            count += CopyDirectory(c, dir, Path.Combine(target, Path.GetFileName(dir)), overwrite, ct);
        return count;
    }

    static string DeletePath(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = Path.TrimEndingDirectorySeparator(c.Resolve(a.Required("path")));
        if (!Path.Exists(path)) throw new ToolException($"Не найдено: {path}");
        c.GuardAppFiles(path);
        if (IsProtected(path))
            throw new ToolException($"Отказано: {path} — корень диска или системная папка пользователя, удалять её целиком нельзя.");
        if (!a.Bool("permanent")) { RecycleBin.Send(path); return $"Перемещено в Корзину: {path}"; }
        if (Directory.Exists(path))
        {
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", Everything))
            {
                ct.ThrowIfCancellationRequested();
                if (file.IsReadOnly) file.IsReadOnly = false;
            }
            Directory.Delete(path, true);
        }
        else
        {
            new FileInfo(path).IsReadOnly = false;
            File.Delete(path);
        }
        return $"Удалено безвозвратно: {path}";
    }

    static string OpenPath(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var raw = a.Required("path");
        bool isUrl = IsWebUrl(raw);
        var target = isUrl ? raw.Trim() : c.Resolve(raw);
        if (!isUrl && !Path.Exists(target)) throw new ToolException($"Не найдено: {target}");
        if (c.SandboxRoot != null && (isUrl || File.Exists(target) && !OpensQuietly(target)))
            throw new ToolException("В режиме песочницы можно открывать только папки и документы (не программы, скрипты и ссылки).");
        using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return $"Открыто: {target}" + (isUrl ? " (во внешней программе с её сетевыми настройками)" : "");
    }

    static bool IsWebUrl(string value) =>
        Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";

    static bool OpensQuietly(string path) => IsPassive(path) && Path.GetExtension(path).ToLowerInvariant() is not (".html" or ".htm" or ".svg");

    static ConfirmRequest? ConfirmWrite(ToolArgs a, ToolContext c)
    {
        var path = GuardedPath(a, c);
        bool exists = File.Exists(path), script = IsExecutable(path);
        if (exists && !a.Bool("append"))
            return new("Перезаписать файл?", path, "Перезаписать", Danger: true,
                Note: $"Сейчас в нём {FormatSize(new FileInfo(path).Length)} — прежнее содержимое пропадёт." + (script ? " " + ScriptNote : ""));
        return !script ? null : exists ? new("Дописать в исполняемый файл или скрипт?", path, "Дописать", Note: ScriptNote) : CreateScript(path);
    }

    static ConfirmRequest CreateScript(string path) => new("Создать исполняемый файл или скрипт?", path, "Создать", Note: ScriptNote);

    static ConfirmRequest? ConfirmEdit(ToolArgs a, ToolContext c)
    {
        var path = GuardedPath(a, c);
        return IsExecutable(path) && File.Exists(path) ? new("Изменить исполняемый файл или скрипт?", path, "Изменить", Note: ScriptNote) : null;
    }

    static ConfirmRequest? ConfirmMoveOrCopy(ToolArgs a, ToolContext c)
    {
        var (source, target) = ResolveSourceAndTarget(a, c);
        bool script = !Path.Exists(target) && File.Exists(source) && IsExecutable(target);
        if (!script && !a.Bool("overwrite")) return null;
        c.GuardAppFiles(target);
        if (script) return CreateScript(target);
        return Path.Exists(target)
            ? new(Directory.Exists(target) ? "Заменить файлы в папке?" : "Заменить файл?", target, "Заменить", Danger: true, Note: "Источник: " + source)
            : null;
    }

    static ConfirmRequest? ConfirmDelete(ToolArgs a, ToolContext c)
    {
        var path = GuardedPath(a, c);
        if (!Path.Exists(path) || IsProtected(path)) return null;
        var (what, content) = Directory.Exists(path) ? ("папку", "Вместе со всем содержимым. ") : ("файл", "");
        return a.Bool("permanent")
            ? new($"Удалить {what} безвозвратно?", path, "Удалить навсегда", Danger: true, Note: content + "Корзина не используется — восстановить не получится.")
            : new($"Удалить {what} в Корзину?", path, "Удалить в Корзину", Note: content + "Восстановить можно из Корзины.");
    }

    static ConfirmRequest? ConfirmOpen(ToolArgs a, ToolContext c)
    {
        var raw = a.Required("path");
        if (IsWebUrl(raw)) return new("Открыть ссылку во внешнем браузере?", raw.Trim(), "Открыть", Note: "Браузер использует собственные сетевые настройки.");
        var path = c.Resolve(raw);
        return c.SandboxRoot != null || Directory.Exists(path) || OpensQuietly(path) ? null
            : IsExecutable(path) ? new("Запустить программу или скрипт?", path, "Запустить", Danger: true)
            : new("Открыть файл этого типа?", path, "Открыть", Note: "Windows откроет его связанной программой.");
    }
}
