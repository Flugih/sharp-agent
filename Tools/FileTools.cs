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
        Tool("get_file_info", "Подробности о файле или папке: существует ли, размер, даты создания/изменения, атрибуты.", GetFileInfo, [Text("path", "Путь к файлу или папке.", true)]),
        Tool("search_files", "Найти файлы и папки по маске имени (например *.txt, отчет*.docx) в папке и её подпапках.", SearchFiles, [
            Text("pattern", "Маска имени: * — любые символы, ? — один символ. Пример: *.pdf", true),
            Text("directory", "Где искать. По умолчанию — рабочая папка."),
            Flag("recursive", "Искать во вложенных папках (по умолчанию true)."),
            Number("max_results", $"Максимум результатов (по умолчанию {DefaultResults}).")]),
        Tool("write_file", "Создать текстовый файл или полностью перезаписать существующий (UTF-8). Недостающие папки создаются автоматически. append=true — дописать в конец.", WriteFile, [
            FilePath,
            Text("content", "Полное содержимое файла (или добавляемый текст при append=true).", true),
            Flag("append", "Дописать в конец, а не перезаписывать (по умолчанию false).")], ConfirmWrite),
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
        Tool("open_path", "Открыть файл или папку программой по умолчанию. Ссылки http/https открываются во встроенном браузере Sharp Agent.", OpenPath, [Text("path", "Путь к файлу, папке или URL.", true)], ConfirmOpen),
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

    internal static ToolOutcome ReadMany(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var files = args.Array("files");
        if (files.Count is < 1 or > 8 || files.Any(file => file is not JsonObject))
            throw new ToolException("Укажите от одного до восьми диапазонов файлов.");
        var pages = new List<ToolOutcome>();
        var ranges = new Dictionary<string, List<(int Start, int Count)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files.OfType<JsonObject>())
        {
            ct.ThrowIfCancellationRequested();
            var request = new ToolArgs(file);
            try
            {
                var path = context.Resolve(request.Required("path"));
                context.GuardSecrets(path);
                if (!ranges.TryGetValue(path, out var requested)) ranges[path] = requested = [];
                requested.Add(ReadRange(request, 60));
            }
            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException) { pages.Add(new("Ошибка: " + ex.Message, true)); }
        }
        foreach (var (path, requested) in ranges)
        {
            var merged = new List<(int Start, int Count)>();
            foreach (var (start, count) in requested.OrderBy(range => range.Start))
            {
                if (merged.Count > 0 && merged[^1] is var earlier && start <= (long)earlier.Start + earlier.Count &&
                    Math.Max((long)start + count - earlier.Start, earlier.Count) is <= MaxReadLines and var combined)
                    merged[^1] = (earlier.Start, (int)combined);
                else merged.Add((start, count));
            }
            foreach (var (start, count) in merged)
            {
                var request = new ToolArgs(new JsonObject { ["path"] = path, ["offset"] = start, ["limit"] = count });
                try { pages.Add(new(ReadFile(request, context, ct, Math.Max(6_000, MaxReadChars / files.Count), 60), false)); }
                catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException) { pages.Add(new("Ошибка: " + ex.Message, true)); }
            }
        }
        int failed = pages.Count(page => page.IsError);
        return new(string.Join("\n\n", pages.Select(page => page.Output)), failed == pages.Count, failed > 0 && failed < pages.Count);
    }

    internal static string ReadFile(ToolArgs a, ToolContext c, CancellationToken ct) => ReadFile(a, c, ct, MaxReadChars, DefaultReadLines);

    internal static string ReadFile(ToolArgs a, ToolContext c, CancellationToken ct, int maxChars, int defaultLines)
    {
        var path = c.Resolve(a.Required("path"));
        var sourcePath = PathHelper.FinalPath(path);
        c.GuardSecrets(sourcePath);
        if (Directory.Exists(path)) throw new ToolException($"Это папка, а не файл: {path}. Используйте list_directory.");
        if (!File.Exists(path)) throw new ToolException($"Файл не найден: {path}");
        var (offset, limit) = ReadRange(a, defaultLines);
        if (OfficeText.Extract(sourcePath) is { } document)
        {
            using var reader = new StringReader(document);
            return Page(reader, "текст извлечён из документа");
        }
        if (c.Snapshots.Read(sourcePath) is { } snapshot)
        {
            using var reader = new StringReader(snapshot.Text);
            return Page(reader, "кодировка: " + snapshot.Encoding.WebName);
        }
        using var stream = File.OpenRead(sourcePath);
        if (stream.Length > 20 * 1024 * 1024)
            throw new ToolException($"Файл слишком большой для чтения как текст ({FormatSize(stream.Length)}).");
        var prefix = new byte[Math.Min(stream.Length, 8000)];
        stream.ReadExactly(prefix);
        var encoding = TextFileIO.DetectBom(prefix);
        if (encoding == null && prefix.Contains((byte)0)) throw new ToolException("Это двоичный файл (не текст) — прочитать или изменить его как текст нельзя.");
        try { return ReadPage(encoding ?? new UTF8Encoding(false, true)); }
        catch (DecoderFallbackException) when (encoding == null) { return ReadPage(Encoding.GetEncoding(1251)); }

        string ReadPage(Encoding chosen)
        {
            stream.Position = chosen.GetPreamble().Length;
            using var reader = new StreamReader(stream, chosen, false, 4096, true);
            return Page(reader, "кодировка: " + chosen.WebName);
        }

        string Page(TextReader reader, string source)
        {
            int number = 0;
            while (number < offset - 1 && ReadLine(reader) != null)
            {
                ct.ThrowIfCancellationRequested();
                number++;
            }
            var current = ReadLine(reader);
            if (current == null)
            {
                if (number == 0) return $"Файл: {path}\n(файл пуст)";
                throw new ToolException($"offset={offset} больше числа строк в файле ({number}).");
            }
            int clipped = 0;
            var body = new StringBuilder();
            bool single = limit == 1 || reader.Peek() == -1;
            while (current != null && number - offset + 1 < limit)
            {
                ct.ThrowIfCancellationRequested();
                var line = ClipLine(current, number + 1, single);
                if (number >= offset)
                {
                    if (body.Length + line.Length + 1 > maxChars) break;
                    body.Append('\n');
                }
                if (line.Length > maxChars) line = line[..maxChars] + " … [строка обрезана]";
                body.Append(line);
                if (line.Length != current.Length) clipped++;
                number++;
                if (number - offset + 1 == limit) break;
                current = ReadLine(reader);
            }
            bool more = current != null && (number - offset + 1 < limit || reader.Peek() != -1);
            return $"Файл: {path} | {FormatSize(new FileInfo(path).Length)} | {source}" +
                (more ? $" | показаны строки {offset}–{number}" : $" | строк: {number}") +
                (clipped > 0 ? $" | обрезано длинных строк: {clipped}" : "") +
                (more ? $" | показано не всё — дальше читай с offset={number + 1}" : "") + "\n----------\n" + body;
        }
    }

    // Lines end only at \n, as in search_text and diffs: a lone \r stays inside the line.
    static string? ReadLine(TextReader reader)
    {
        var line = new StringBuilder();
        int ch;
        while ((ch = reader.Read()) is not (-1 or '\n')) line.Append((char)ch);
        if (ch == -1 && line.Length == 0) return null;
        return line.Length > 0 && line[^1] == '\r' ? line.ToString(0, line.Length - 1) : line.ToString();
    }

    internal static (int Offset, int Limit) ReadRange(ToolArgs args, int defaultLines = DefaultReadLines) =>
        (Math.Max(1, args.Int("offset", 1)), Math.Clamp(args.Int("limit", defaultLines), 1, MaxReadLines));

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

    internal static string SearchText(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        const int scanLimit = 20_000, outputLimit = 8_000_000, cloudOnly = 0x00400000 | 0x00040000 | 0x00001000;
        var (dir, max) = SearchScope(a, c);
        var query = a.Required("query");
        var pattern = a.Optional("file_pattern") ?? "*";
        var comparison = a.Bool("case_sensitive") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var sb = new StringBuilder();
        int hits = 0, scanned = 0, cloudSkipped = 0;
        bool more = false, outputFull = false;
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
                if (sb.Length + shown.Length + file.FullName.Length + 20 > outputLimit) { more = outputFull = true; break; }
                sb.AppendLine($"{file.FullName}:{lineNo}: {shown}");
                hits++;
            }
        }
        var summary = $"Просмотрено текстовых файлов: {scanned}, совпадений: {hits}" +
                      (outputFull ? " (достигнут предел размера результата — уточни запрос, маску или папку)" :
                        more ? $" (показаны первые {max} — есть ещё; уточни запрос, маску или папку{MoreHint(max)})" : "") +
                      (scanned >= scanLimit ? $"; поиск остановлен на лимите {scanLimit} файлов — сузьте папку или маску" : "") +
                      (cloudSkipped > 0 ? $"; пропущено нескачанных файлов OneDrive: {cloudSkipped}" : "");
        return hits == 0 ? $"Текст «{query}» не найден в {dir}. {summary}" : sb + summary;
    }

    static string MoreHint(int max) => max < MaxResults ? " либо увеличь max_results" : $" — больше {MaxResults} за раз не выдаётся";
}
