using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using static SharpAgent.Tools.PathHelper;

namespace SharpAgent.Tools;

public static partial class FileTools
{
    private const int DefaultReadLines = 850, MaxReadLines = 3_000, MaxReadChars = 50_000, MaxLineChars = 2_000;
    private const int DefaultResults = 45, MaxResults = 1_000;
    private const int CloudOnlyAttributes = 0x00400000 | 0x00040000 | 0x00001000;

    public static IEnumerable<AgentTool> All() =>
    [
        new AgentTool
        {
            Name = "get_system_info",
            Description = "Сведения о компьютере: дата и время, пользователь, ОС, рабочая папка, пути к Рабочему столу, Документам, Загрузкам, список дисков со свободным местом.",
            Parameters = Schema.Object(),
            Handler = AgentTool.Sync(GetSystemInfo),
        },
        new AgentTool
        {
            Name = "list_directory",
            Description = "Показать содержимое папки: подпапки и файлы с размером и датой изменения.",
            Parameters = Schema.Object(
                new Prop("path", "string", "Путь к папке. По умолчанию — рабочая папка."),
                new Prop("recursive", "boolean", "Показать и содержимое вложенных папок (по умолчанию false)."),
                new Prop("show_hidden", "boolean", "Показывать скрытые и системные файлы (по умолчанию false)."),
                new Prop("max_entries", "integer", "Максимум элементов в ответе (по умолчанию 300).")),
            Handler = AgentTool.Sync(ListDirectory),
        },
        new AgentTool
        {
            Name = "read_file",
            Description = "Прочитать текстовый файл (кодировка UTF-8/UTF-16/Windows-1251 определяется автоматически) или извлечь текст " +
                          $"из документа .docx, .xlsx, .pptx. За раз — до {MaxReadChars} символов; строки длиннее {MaxLineChars} символов " +
                          "обрезаются с пометкой, кроме чтения одной строки (limit=1). Если показано не всё, в заголовке сказано, " +
                          "с какого offset читать дальше. Чтобы увидеть, например, функцию целиком, укажи offset и limit побольше.",
            Parameters = Schema.Object(
                new Prop("path", "string", "Путь к файлу.", Required: true),
                new Prop("offset", "integer", "С какой строки читать (нумерация с 1). По умолчанию 1."),
                new Prop("limit", "integer", $"Сколько строк прочитать (по умолчанию {DefaultReadLines}, максимум {MaxReadLines}).")),
            Handler = AgentTool.Sync(ReadFile),
        },
        new AgentTool
        {
            Name = "get_file_info",
            Description = "Подробности о файле или папке: существует ли, размер, даты создания/изменения, атрибуты.",
            Parameters = Schema.Object(new Prop("path", "string", "Путь к файлу или папке.", Required: true)),
            Handler = AgentTool.Sync(GetFileInfo),
        },
        new AgentTool
        {
            Name = "search_files",
            Description = "Найти файлы и папки по маске имени (например *.txt, отчет*.docx) в папке и её подпапках.",
            Parameters = Schema.Object(
                new Prop("pattern", "string", "Маска имени: * — любые символы, ? — один символ. Пример: *.pdf", Required: true),
                new Prop("directory", "string", "Где искать. По умолчанию — рабочая папка."),
                new Prop("recursive", "boolean", "Искать во вложенных папках (по умолчанию true)."),
                new Prop("max_results", "integer", $"Максимум результатов (по умолчанию {DefaultResults}).")),
            Handler = AgentTool.Sync(SearchFiles),
        },
        new AgentTool
        {
            Name = "search_text",
            Description = "Найти текст внутри текстовых файлов (как grep). Возвращает путь, номер строки и саму строку.",
            Parameters = Schema.Object(
                new Prop("query", "string", "Искомый текст.", Required: true),
                new Prop("directory", "string", "Где искать (рекурсивно). По умолчанию — рабочая папка."),
                new Prop("file_pattern", "string", "Маска файлов, например *.cs или *.txt. По умолчанию все файлы."),
                new Prop("case_sensitive", "boolean", "Учитывать регистр (по умолчанию false)."),
                new Prop("max_results", "integer", $"Максимум совпадений (по умолчанию {DefaultResults}).")),
            Handler = AgentTool.Sync(SearchText),
        },
        new AgentTool
        {
            Name = "write_file",
            Description = "Создать текстовый файл или полностью перезаписать существующий (UTF-8). Недостающие папки создаются автоматически. append=true — дописать в конец.",
            Parameters = Schema.Object(
                new Prop("path", "string", "Путь к файлу.", Required: true),
                new Prop("content", "string", "Полное содержимое файла (или добавляемый текст при append=true).", Required: true),
                new Prop("append", "boolean", "Дописать в конец, а не перезаписывать (по умолчанию false).")),
            Handler = AgentTool.Sync(WriteFile),
            Confirmation = ConfirmWrite,
        },
        new AgentTool
        {
            Name = "edit_file",
            Description = "Точечно изменить текстовый файл: заменить фрагмент old_text на new_text. old_text должен совпадать с текстом файла посимвольно (сначала прочитай файл). Кодировка файла сохраняется.",
            Parameters = Schema.Object(
                new Prop("path", "string", "Путь к файлу.", Required: true),
                new Prop("old_text", "string", "Точный фрагмент, который нужно заменить (должен встречаться в файле один раз).", Required: true),
                new Prop("new_text", "string", "Новый текст на место old_text (может быть пустым — тогда фрагмент удаляется).", Required: true),
                new Prop("replace_all", "boolean", "Заменить все вхождения (по умолчанию false).")),
            Handler = AgentTool.Sync(EditFile),
            Confirmation = ConfirmEdit,
        },
        new AgentTool
        {
            Name = "create_directory",
            Description = "Создать папку (вместе со всеми недостающими родительскими папками).",
            Parameters = Schema.Object(new Prop("path", "string", "Путь к новой папке.", Required: true)),
            Handler = AgentTool.Sync(CreateDirectory),
        },
        new AgentTool
        {
            Name = "move_path",
            Description = "Переместить или переименовать файл либо папку. Если destination — существующая папка, источник кладётся внутрь неё.",
            Parameters = Schema.Object(
                new Prop("source", "string", "Что перемещаем.", Required: true),
                new Prop("destination", "string", "Новый путь (или папка назначения).", Required: true),
                new Prop("overwrite", "boolean", "Заменить существующий файл назначения (по умолчанию false).")),
            Handler = AgentTool.Sync(MovePath),
            Confirmation = ConfirmMoveOrCopy,
        },
        new AgentTool
        {
            Name = "copy_path",
            Description = "Скопировать файл или папку (со всем содержимым). Если destination — существующая папка, копия кладётся внутрь неё.",
            Parameters = Schema.Object(
                new Prop("source", "string", "Что копируем.", Required: true),
                new Prop("destination", "string", "Куда копируем (новый путь или папка назначения).", Required: true),
                new Prop("overwrite", "boolean", "Заменять существующие файлы (по умолчанию false).")),
            Handler = AgentTool.Sync(CopyPath),
            Confirmation = ConfirmMoveOrCopy,
        },
        new AgentTool
        {
            Name = "delete_path",
            Description = "Удалить файл или папку. По умолчанию — в Корзину (можно восстановить). permanent=true — безвозвратно, только по явной просьбе пользователя.",
            Parameters = Schema.Object(
                new Prop("path", "string", "Что удалить.", Required: true),
                new Prop("permanent", "boolean", "Удалить безвозвратно, минуя Корзину (по умолчанию false).")),
            Handler = AgentTool.Sync(DeletePath),
            Confirmation = ConfirmDelete,
        },
        new AgentTool
        {
            Name = "open_path",
            Description = "Открыть файл, папку или ссылку (http/https) программой по умолчанию — как двойной щелчок в Проводнике.",
            Parameters = Schema.Object(new Prop("path", "string", "Путь к файлу/папке или URL.", Required: true)),
            Handler = AgentTool.Sync(OpenPath),
            Confirmation = ConfirmOpen,
        },
    ];

    private static string GetSystemInfo(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        using var identity = WindowsIdentity.GetCurrent();
        bool isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        var sb = new StringBuilder();
        sb.AppendLine($"Дата и время: {DateTime.Now:yyyy-MM-dd HH:mm:ss, dddd} ({TimeZoneInfo.Local.DisplayName})");
        sb.AppendLine($"Пользователь: {Environment.UserName}, компьютер: {Environment.MachineName}");
        sb.AppendLine($"ОС: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Права администратора: {(isAdmin ? "есть" : "нет (обычный пользователь)")}");
        sb.AppendLine($"Рабочая папка агента: {c.WorkingDirectory}");
        sb.AppendLine($"Папка пользователя: {Folder(Environment.SpecialFolder.UserProfile)}");
        sb.AppendLine($"Рабочий стол: {Folder(Environment.SpecialFolder.DesktopDirectory)}");
        sb.AppendLine($"Документы: {Folder(Environment.SpecialFolder.MyDocuments)}");
        sb.AppendLine($"Загрузки: {KnownFolders.Downloads}");
        sb.AppendLine($"Изображения: {Folder(Environment.SpecialFolder.MyPictures)}");
        sb.AppendLine($"Музыка: {Folder(Environment.SpecialFolder.MyMusic)}");
        sb.AppendLine($"Видео: {Folder(Environment.SpecialFolder.MyVideos)}");
        sb.AppendLine($"AppData: {Folder(Environment.SpecialFolder.ApplicationData)}");
        sb.AppendLine($"Временные файлы: {Path.GetTempPath()}");
        sb.AppendLine("Диски:");
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                sb.AppendLine($"  {drive.Name} [{drive.DriveType}] {drive.VolumeLabel} — свободно " +
                              $"{FormatSize(drive.AvailableFreeSpace)} из {FormatSize(drive.TotalSize)}");
            }
            catch { }
        }
        return sb.ToString();
    }

    private static string ListDirectory(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = c.Resolve(a.Optional("path"));
        if (File.Exists(path)) throw new ToolException($"Это файл, а не папка: {path}");
        if (!Directory.Exists(path)) throw new ToolException($"Папка не найдена: {path}");

        bool recursive = a.Bool("recursive");
        int max = Math.Clamp(a.Int("max_entries", 300), 1, 5000);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            AttributesToSkip = a.Bool("show_hidden") ? 0 : FileAttributes.Hidden | FileAttributes.System,
        };

        var root = new DirectoryInfo(path);
        IEnumerable<FileSystemInfo> entries = recursive
            ? root.EnumerateFileSystemInfos("*", options)
            : root.EnumerateDirectories("*", options).OrderBy(d => d.Name).Cast<FileSystemInfo>()
                .Concat(root.EnumerateFiles("*", options).OrderBy(f => f.Name));

        var sb = new StringBuilder($"Содержимое папки {root.FullName}:\n");
        int count = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (count == max)
            {
                sb.AppendLine($"... показаны первые {max} элементов (увеличьте max_entries или уточните путь)");
                break;
            }
            var rel = Path.GetRelativePath(root.FullName, entry.FullName);
            sb.AppendLine(entry is FileInfo f
                ? $"{rel}  ({FormatSize(f.Length)}, изменён {f.LastWriteTime:yyyy-MM-dd HH:mm})"
                : $"[папка] {rel}\\");
            count++;
        }
        if (count == 0) sb.AppendLine("(папка пуста)");
        return sb.ToString();
    }

    private static string ReadFile(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var path = c.Resolve(a.Required("path"));
        c.GuardSecrets(path);
        if (Directory.Exists(path)) throw new ToolException($"Это папка, а не файл: {path}. Используйте list_directory.");
        if (!File.Exists(path)) throw new ToolException($"Файл не найден: {path}");

        string text, source;
        if (OfficeText.CanRead(path))
        {
            text = OfficeText.Extract(path);
            source = "текст извлечён из документа";
        }
        else
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
            if (i >= offset && body.Length + line.Length + 1 > MaxReadChars) break;
            if (i >= offset) body.Append('\n');
            body.Append(line);
            if (line.Length != lines[i].Length) clipped++;
            end = i + 1;
        }

        var header = new StringBuilder($"Файл: {path} | {FormatSize(new FileInfo(path).Length)} | {source} | строк: {total}");
        if (offset > 1 || end < total) header.Append($" | показаны строки {offset}–{end}");
        if (clipped > 0) header.Append(single
            ? $" | обрезано: строка длиннее {MaxReadChars} символов — read_file покажет только её начало"
            : $" | обрезано длинных строк: {clipped} — строку целиком (до {MaxReadChars} символов) покажут её offset и limit=1");
        if (end < last) header.Append($" | показано не всё (за раз до {MaxReadChars} символов) — дальше читай с offset={end + 1}");
        else if (end < total) header.Append($" | показано не всё — дальше читай с offset={end + 1}");
        return header + "\n----------\n" + body;
    }

    internal static (int Offset, int Limit) ReadRange(ToolArgs args) =>
        (Math.Max(1, args.Int("offset", 1)), Math.Clamp(args.Int("limit", DefaultReadLines), 1, MaxReadLines));

    public static string ClipLine(string line, int number, bool single)
    {
        int max = single ? MaxReadChars : MaxLineChars;
        return line.Length <= max ? line : line[..max] + $" … [строка {number} обрезана: в ней {line.Length} символов]";
    }

    private static string GetFileInfo(ToolArgs a, ToolContext c, CancellationToken ct)
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
            return $"Папка: {d.FullName}\nСоздана: {d.CreationTime:yyyy-MM-dd HH:mm:ss}\n" +
                   $"Изменена: {d.LastWriteTime:yyyy-MM-dd HH:mm:ss}\nАтрибуты: {d.Attributes}\n" +
                   $"Внутри (без вложенных): папок {dirs}, файлов {files}";
        }
        if (File.Exists(path))
        {
            var f = new FileInfo(path);
            return $"Файл: {f.FullName}\nРазмер: {FormatSize(f.Length)} ({f.Length} байт)\n" +
                   $"Создан: {f.CreationTime:yyyy-MM-dd HH:mm:ss}\nИзменён: {f.LastWriteTime:yyyy-MM-dd HH:mm:ss}\n" +
                   $"Атрибуты: {f.Attributes}\nТолько для чтения: {(f.IsReadOnly ? "да" : "нет")}";
        }
        return $"Не существует: {path}";
    }

    private static string SearchFiles(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var dir = c.Resolve(a.Optional("directory"));
        if (!Directory.Exists(dir)) throw new ToolException($"Папка не найдена: {dir}");

        var pattern = a.Optional("pattern") ?? "*";
        bool recursive = a.Bool("recursive", true);
        if (pattern.StartsWith("**/") || pattern.StartsWith("**\\")) { pattern = pattern[3..]; recursive = true; }
        if (pattern.Contains('/') || pattern.Contains('\\'))
            throw new ToolException("pattern — это маска имени (например *.txt), без пути. Папку укажите в параметре directory.");

        int max = Math.Clamp(a.Int("max_results", DefaultResults), 1, MaxResults);
        var options = new EnumerationOptions { RecurseSubdirectories = recursive };

        var sb = new StringBuilder();
        int count = 0;
        foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!FileSystemName.MatchesSimpleExpression(pattern, entry.Name, ignoreCase: true)) continue;
            if (count == max)
            {
                sb.AppendLine($"... показаны первые {max} — есть ещё. Уточни маску или папку{MoreHint(max)}");
                break;
            }
            sb.AppendLine(entry is FileInfo f ? $"{f.FullName}  ({FormatSize(f.Length)})" : $"[папка] {entry.FullName}");
            count++;
        }
        return count == 0
            ? $"Ничего не найдено по маске '{pattern}' в {dir} (скрытые и системные папки не просматриваются)"
            : $"Найдено по маске '{pattern}' в {dir}:\n{sb}";
    }

    private static string SearchText(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var dir = c.Resolve(a.Optional("directory"));
        if (!Directory.Exists(dir)) throw new ToolException($"Папка не найдена: {dir}");

        var query = a.Required("query");
        var pattern = a.Optional("file_pattern") ?? "*";
        var comparison = a.Bool("case_sensitive") ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int max = Math.Clamp(a.Int("max_results", DefaultResults), 1, MaxResults);
        var options = new EnumerationOptions { RecurseSubdirectories = true };

        var sb = new StringBuilder();
        int hits = 0, scanned = 0, cloudSkipped = 0;
        bool more = false;
        const int scanLimit = 20_000;
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!FileSystemName.MatchesSimpleExpression(pattern, file.Name, ignoreCase: true)) continue;
            if (more || scanned >= scanLimit) break;
            if (file.Length == 0 || file.Length > 5 * 1024 * 1024 || c.IsSecret(file.FullName)) continue;
            if (((int)file.Attributes & CloudOnlyAttributes) != 0) { cloudSkipped++; continue; }

            string text;
            try { text = TextFileIO.Read(file.FullName).Text; }
            catch { continue; }
            scanned++;

            int lineNo = 0;
            foreach (var line in text.Split('\n'))
            {
                lineNo++;
                if (!line.Contains(query, comparison)) continue;
                if (hits == max)
                {
                    more = true;
                    break;
                }
                var shown = line.Trim();
                if (shown.Length > 200) shown = shown[..200] + "…";
                sb.AppendLine($"{file.FullName}:{lineNo}: {shown}");
                hits++;
            }
        }

        var summary = $"Просмотрено текстовых файлов: {scanned}, совпадений: {hits}" +
                      (more ? $" (показаны первые {max} — есть ещё; уточни запрос, маску или папку{MoreHint(max)})" : "") +
                      (scanned >= scanLimit ? $"; поиск остановлен на лимите {scanLimit} файлов — сузьте папку или маску" : "") +
                      (cloudSkipped > 0 ? $"; пропущено нескачанных файлов OneDrive: {cloudSkipped}" : "");
        return hits == 0 ? $"Текст «{query}» не найден в {dir}. {summary}" : sb + summary;
    }

    private static string MoreHint(int max) =>
        max < MaxResults ? " либо увеличь max_results" : $" — больше {MaxResults} за раз не выдаётся";
}
