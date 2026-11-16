using System.IO.Enumeration;
using System.Security.Principal;
using static SharpAgent.Tools.PathHelper;
using Folder = System.Environment.SpecialFolder;

namespace SharpAgent.Tools;

public static partial class FileTools
{
    const int DefaultReadLines = 250, MaxReadLines = 3_000, MaxReadChars = 50_000, MaxLineChars = 2_000, DefaultResults = 45, MaxResults = 1_000;
    static readonly Prop FilePath = Text("path", "Путь к файлу.", true);

    public static IEnumerable<AgentTool> All() =>
    [
        Tool("get_system_info", "Сведения о компьютере: дата и время, пользователь, ОС, рабочая папка, пути к Рабочему столу, Документам, Загрузкам, список дисков со свободным местом.", GetSystemInfo, []),
        Tool("list_directory", "Показать содержимое папки: подпапки и файлы с размером и датой изменения.", ListDirectory, [
            Text("path", "Путь к папке. По умолчанию — рабочая папка."),
            Flag("recursive", "Показать и содержимое вложенных папок (по умолчанию false)."),
            Flag("show_hidden", "Показывать скрытые и системные файлы (по умолчанию false)."),
            Number("max_entries", "Максимум элементов в ответе (по умолчанию 300).")]),
        Tool("read_file", $"Прочитать текстовый файл (кодировка UTF-8/UTF-16/Windows-1251 определяется автоматически) или извлечь текст из документа .docx, .xlsx, .pptx. За раз — до {MaxReadChars} символов; строки длиннее {MaxLineChars} символов обрезаются с пометкой, кроме чтения одной строки (limit=1). Если показано не всё, в заголовке сказано, с какого offset читать дальше. Чтобы увидеть, например, функцию целиком, укажи offset и limit побольше.", ReadFile, [
            FilePath,
            Number("offset", "С какой строки читать (нумерация с 1). По умолчанию 1."),
            Number("limit", $"Сколько строк прочитать (по умолчанию {DefaultReadLines}, максимум {MaxReadLines}).")]),
        Tool("get_file_info", "Подробности о файле или папке: существует ли, размер, даты создания/изменения, атрибуты.", GetFileInfo, [Text("path", "Путь к файлу или папке.", true)]),
        Tool("search_files", "Найти файлы и папки по маске имени (например *.txt, отчет*.docx) в папке и её подпапках.", SearchFiles, [
            Text("pattern", "Маска имени: * — любые символы, ? — один символ. Пример: *.pdf", true),
            Text("directory", "Где искать. По умолчанию — рабочая папка."),
            Flag("recursive", "Искать во вложенных папках (по умолчанию true)."),
            Number("max_results", $"Максимум результатов (по умолчанию {DefaultResults}).")]),
        Tool("search_text", "Найти текст внутри текстовых файлов (как grep). Возвращает путь, номер строки и саму строку.", SearchText, [
            Text("query", "Искомый текст.", true),
            Text("directory", "Где искать (рекурсивно). По умолчанию — рабочая папка."),
            Text("file_pattern", "Маска файлов, например *.cs или *.txt. По умолчанию все файлы."),
            Flag("case_sensitive", "Учитывать регистр (по умолчанию false)."),
            Number("max_results", $"Максимум совпадений (по умолчанию {DefaultResults}).")]),
        Tool("write_file", "Создать текстовый файл или полностью перезаписать существующий (UTF-8). Недостающие папки создаются автоматически. append=true — дописать в конец.", WriteFile, [
            FilePath,
            Text("content", "Полное содержимое файла (или добавляемый текст при append=true).", true),
            Flag("append", "Дописать в конец, а не перезаписывать (по умолчанию false).")], ConfirmWrite),
        Tool("edit_file", "Точечно изменить текстовый файл: заменить фрагмент old_text на new_text. old_text должен совпадать с текстом файла посимвольно (сначала прочитай файл). Кодировка файла сохраняется.", EditFile, [
            FilePath,
            Text("old_text", "Точный фрагмент, который нужно заменить (должен встречаться в файле один раз).", true),
            Text("new_text", "Новый текст на место old_text (может быть пустым — тогда фрагмент удаляется).", true),
            Flag("replace_all", "Заменить все вхождения (по умолчанию false).")], ConfirmEdit),
        Tool("create_directory", "Создать папку (вместе со всеми недостающими родительскими папками).", CreateDirectory, [Text("path", "Путь к новой папке.", true)]),
        Tool("move_path", "Переместить или переименовать файл либо папку. Если destination — существующая папка, источник кладётся внутрь неё.", MovePath, [
            Text("source", "Что перемещаем.", true),
            Text("destination", "Новый путь (или папка назначения).", true),
            Flag("overwrite", "Заменить существующий файл назначения (по умолчанию false).")], ConfirmMoveOrCopy),
        Tool("copy_path", "Скопировать файл или папку (со всем содержимым). Если destination — существующая папка, копия кладётся внутрь неё.", CopyPath, [
            Text("source", "Что копируем.", true),
            Text("destination", "Куда копируем (новый путь или папка назначения).", true),
            Flag("overwrite", "Заменять существующие файлы (по умолчанию false).")], ConfirmMoveOrCopy),
        Tool("delete_path", "Удалить файл или папку. По умолчанию — в Корзину (можно восстановить). permanent=true — безвозвратно, только по явной просьбе пользователя.", DeletePath, [
            Text("path", "Что удалить.", true),
            Flag("permanent", "Удалить безвозвратно, минуя Корзину (по умолчанию false).")], ConfirmDelete),
        Tool("open_path", "Открыть файл, папку или ссылку (http/https) программой по умолчанию — как двойной щелчок в Проводнике.", OpenPath, [Text("path", "Путь к файлу/папке или URL.", true)], ConfirmOpen),
    ];

    static AgentTool Tool(string name, string description, Func<ToolArgs, ToolContext, CancellationToken, string> handler, Prop[] props,
        Func<ToolArgs, ToolContext, ConfirmRequest?>? confirm = null) =>
        new() { Name = name, Description = description, Parameters = Schema.Object(props), Handler = AgentTool.Sync(handler), Confirmation = confirm };

    static Prop Text(string name, string description, bool required = false) => new(name, "string", description, required);
    static Prop Flag(string name, string description) => new(name, "boolean", description);
    static Prop Number(string name, string description) => new(name, "integer", description);

    static string GetSystemInfo(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        static string Get(Folder folder) => Environment.GetFolderPath(folder);
        using var identity = WindowsIdentity.GetCurrent();
        var sb = new StringBuilder();
        foreach (var line in new[]
        {
            $"Дата и время: {DateTime.Now:yyyy-MM-dd HH:mm:ss, dddd} ({TimeZoneInfo.Local.DisplayName})",
            $"Пользователь: {Environment.UserName}, компьютер: {Environment.MachineName}",
            $"ОС: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            $"Права администратора: {(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ? "есть" : "нет (обычный пользователь)")}",
            $"Рабочая папка агента: {c.WorkingDirectory}",
            $"Папка пользователя: {Get(Folder.UserProfile)}",
            $"Рабочий стол: {Get(Folder.DesktopDirectory)}",
            $"Документы: {Get(Folder.MyDocuments)}",
            $"Загрузки: {KnownFolders.Downloads}",
            $"Изображения: {Get(Folder.MyPictures)}",
            $"Музыка: {Get(Folder.MyMusic)}",
            $"Видео: {Get(Folder.MyVideos)}",
            $"AppData: {Get(Folder.ApplicationData)}",
            $"Временные файлы: {Path.GetTempPath()}",
            "Диски:",
        }) sb.AppendLine(line);
        foreach (var drive in DriveInfo.GetDrives())
            try
            {
                if (drive.IsReady)
                    sb.AppendLine($"  {drive.Name} [{drive.DriveType}] {drive.VolumeLabel} — свободно {FormatSize(drive.AvailableFreeSpace)} из {FormatSize(drive.TotalSize)}");
            }
            catch { }
        return sb.ToString();
    }

    static string ListDirectory(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = c.Resolve(a.Optional("path"));
        if (File.Exists(path)) throw new ToolException($"Это файл, а не папка: {path}");
        if (!Directory.Exists(path)) throw new ToolException($"Папка не найдена: {path}");
        bool recursive = a.Bool("recursive");
        int max = Math.Clamp(a.Int("max_entries", 300), 1, 5000), count = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, AttributesToSkip = a.Bool("show_hidden") ? 0 : FileAttributes.Hidden | FileAttributes.System };
        var root = new DirectoryInfo(path);
        var entries = recursive ? root.EnumerateFileSystemInfos("*", options)
            : root.EnumerateDirectories("*", options).OrderBy(d => d.Name).Concat<FileSystemInfo>(root.EnumerateFiles("*", options).OrderBy(f => f.Name));
        var sb = new StringBuilder($"Содержимое папки {root.FullName}:\n");
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (count++ == max) { sb.AppendLine($"... показаны первые {max} элементов (увеличьте max_entries или уточните путь)"); break; }
            var rel = Path.GetRelativePath(root.FullName, entry.FullName);
            sb.AppendLine(entry is FileInfo f ? $"{rel}  ({FormatSize(f.Length)}, изменён {f.LastWriteTime:yyyy-MM-dd HH:mm})" : $"[папка] {rel}\\");
        }
        if (count == 0) sb.AppendLine("(папка пуста)");
        return sb.ToString();
    }

    static string ReadFile(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = c.Resolve(a.Required("path"));
        c.GuardSecrets(path);
        if (Directory.Exists(path)) throw new ToolException($"Это папка, а не файл: {path}. Используйте list_directory.");
        if (!File.Exists(path)) throw new ToolException($"Файл не найден: {path}");
        string? text = OfficeText.Extract(path), source = "текст извлечён из документа";
        if (text == null)
        {
            (text, var encoding) = TextFileIO.Read(path);
            source = "кодировка: " + encoding.WebName;
        }
        var lines = text.Replace("\r\n", "\n").Split('\n');
        int total = lines.Length;
        if (total > 1 && lines[^1].Length == 0) total--;
        if (total == 1 && lines[0].Length == 0) return $"Файл: {path}\n(файл пуст)";
        var (offset, limit) = ReadRange(a);
        if (offset > total) throw new ToolException($"offset={offset} больше числа строк в файле ({total}).");
        var body = new StringBuilder();
        int last = Math.Min(total, offset + limit - 1), end = offset - 1, clipped = 0;
        bool single = last == offset;
        for (int i = offset - 1; i < last; i++)
        {
            var line = ClipLine(lines[i], i + 1, single);
            if (i >= offset)
            {
                if (body.Length + line.Length + 1 > MaxReadChars) break;
                body.Append('\n');
            }
            body.Append(line);
            if (line.Length != lines[i].Length) clipped++;
            end = i + 1;
        }
        return $"Файл: {path} | {FormatSize(new FileInfo(path).Length)} | {source} | строк: {total}" +
            (offset > 1 || end < total ? $" | показаны строки {offset}–{end}" : "") +
            (clipped == 0 ? "" : single ? $" | обрезано: строка длиннее {MaxReadChars} символов — read_file покажет только её начало"
                : $" | обрезано длинных строк: {clipped} — строку целиком (до {MaxReadChars} символов) покажут её offset и limit=1") +
            (end < last ? $" | показано не всё (за раз до {MaxReadChars} символов) — дальше читай с offset={end + 1}"
                : end < total ? $" | показано не всё — дальше читай с offset={end + 1}" : "") + "\n----------\n" + body;
    }

    internal static (int Offset, int Limit) ReadRange(ToolArgs args) =>
        (Math.Max(1, args.Int("offset", 1)), Math.Clamp(args.Int("limit", DefaultReadLines), 1, MaxReadLines));

    public static string ClipLine(string line, int number, bool single)
    {
        int max = single ? MaxReadChars : MaxLineChars;
        return line.Length <= max ? line : line[..max] + $" … [строка {number} обрезана: в ней {line.Length} символов]";
    }

    static string GetFileInfo(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = c.Resolve(a.Required("path"));
        if (Directory.Exists(path))
        {
            var d = new DirectoryInfo(path);
            int files = 0, dirs = 0;
            try
            {
                foreach (var e in d.EnumerateFileSystemInfos())
                    if (e is DirectoryInfo) dirs++; else files++;
            }
            catch { }
            return $"Папка: {d.FullName}\nСоздана: {d.CreationTime:yyyy-MM-dd HH:mm:ss}\nИзменена: {d.LastWriteTime:yyyy-MM-dd HH:mm:ss}\n" +
                   $"Атрибуты: {d.Attributes}\nВнутри (без вложенных): папок {dirs}, файлов {files}";
        }
        if (!File.Exists(path)) return $"Не существует: {path}";
        var f = new FileInfo(path);
        return $"Файл: {f.FullName}\nРазмер: {FormatSize(f.Length)} ({f.Length} байт)\nСоздан: {f.CreationTime:yyyy-MM-dd HH:mm:ss}\n" +
               $"Изменён: {f.LastWriteTime:yyyy-MM-dd HH:mm:ss}\nАтрибуты: {f.Attributes}\nТолько для чтения: {(f.IsReadOnly ? "да" : "нет")}";
    }

    static (string Dir, int Max) SearchScope(ToolArgs a, ToolContext c)
    {
        var dir = c.Resolve(a.Optional("directory"));
        if (!Directory.Exists(dir)) throw new ToolException($"Папка не найдена: {dir}");
        return (dir, Math.Clamp(a.Int("max_results", DefaultResults), 1, MaxResults));
    }

    static string SearchFiles(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var (dir, max) = SearchScope(a, c);
        var pattern = a.Optional("pattern") ?? "*";
        bool recursive = a.Bool("recursive", true);
        if (pattern.StartsWith("**/") || pattern.StartsWith("**\\")) (pattern, recursive) = (pattern[3..], true);
        if (pattern.Contains('/') || pattern.Contains('\\'))
            throw new ToolException("pattern — это маска имени (например *.txt), без пути. Папку укажите в параметре directory.");
        var sb = new StringBuilder();
        int count = 0;
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = recursive }))
        {
            ct.ThrowIfCancellationRequested();
            if (!FileSystemName.MatchesSimpleExpression(pattern, entry.Name, ignoreCase: true)) continue;
            if (count++ == max) { sb.AppendLine($"... показаны первые {max} — есть ещё. Уточни маску или папку{MoreHint(max)}"); break; }
            sb.AppendLine(entry is FileInfo f ? $"{f.FullName}  ({FormatSize(f.Length)})" : $"[папка] {entry.FullName}");
        }
        return count == 0
            ? $"Ничего не найдено по маске '{pattern}' в {dir} (скрытые и системные папки не просматриваются)"
            : $"Найдено по маске '{pattern}' в {dir}:\n{sb}";
    }

    static string SearchText(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        const int scanLimit = 20_000, cloudOnly = 0x00400000 | 0x00040000 | 0x00001000;
        var (dir, max) = SearchScope(a, c);
        var query = a.Required("query");
        var pattern = a.Optional("file_pattern") ?? "*";
        var comparison = a.Bool("case_sensitive") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var sb = new StringBuilder();
        int hits = 0, scanned = 0, cloudSkipped = 0;
        bool more = false;
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true }))
        {
            ct.ThrowIfCancellationRequested();
            if (!FileSystemName.MatchesSimpleExpression(pattern, file.Name, ignoreCase: true)) continue;
            if (more || scanned >= scanLimit) break;
            if (file.Length == 0 || file.Length > 5 * 1024 * 1024 || c.IsSecret(file.FullName)) continue;
            if (((int)file.Attributes & cloudOnly) != 0) { cloudSkipped++; continue; }
            string text;
            try { text = TextFileIO.Read(file.FullName).Text; }
            catch { continue; }
            scanned++;
            int lineNo = 0;
            foreach (var line in text.Split('\n'))
            {
                lineNo++;
                if (!line.Contains(query, comparison)) continue;
                if (hits == max) { more = true; break; }
                var shown = line.Trim();
                sb.AppendLine($"{file.FullName}:{lineNo}: {(shown.Length > 200 ? shown[..200] + "…" : shown)}");
                hits++;
            }
        }
        var summary = $"Просмотрено текстовых файлов: {scanned}, совпадений: {hits}" +
                      (more ? $" (показаны первые {max} — есть ещё; уточни запрос, маску или папку{MoreHint(max)})" : "") +
                      (scanned >= scanLimit ? $"; поиск остановлен на лимите {scanLimit} файлов — сузьте папку или маску" : "") +
                      (cloudSkipped > 0 ? $"; пропущено нескачанных файлов OneDrive: {cloudSkipped}" : "");
        return hits == 0 ? $"Текст «{query}» не найден в {dir}. {summary}" : sb + summary;
    }

    static string MoreHint(int max) => max < MaxResults ? " либо увеличь max_results" : $" — больше {MaxResults} за раз не выдаётся";
}
