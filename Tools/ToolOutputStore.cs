namespace SharpAgent.Tools;

static class ToolOutputStore
{
    internal const string Name = "read_tool_output", ReferencePrefix = "Результат: ";
    const int PreviewChars = 6_000, MaxPageChars = 32_000;
    const long Capacity = 96L * 1024 * 1024;
    static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    static readonly object Gate = new();

    internal static bool Applies(string name) => name is "search_files" or
        "list_directory" or LocalResearchEngine.Name or LocalTestTools.Name or LocalContractEngine.Name or "local_batch";

    internal static AgentTool Tool() => new()
    {
        Name = Name,
        Description = "Прочитать сохранённый результат инструмента по идентификатору из строки «Результат: …» без повторного запуска.",
        Parameters = Schema.Object(new Prop("id", "string", "Идентификатор результата.", Required: true),
            new Prop("offset", "integer", "Смещение в символах, начиная с 0."),
            new Prop("limit", "integer", $"Число символов (максимум {MaxPageChars}).")),
        Handler = AgentTool.Sync(Read),
    };

    internal static string? Save(ToolContext context, string output)
    {
        if (output.Length <= 2400 || context.AppFolder == null) return null;
        var data = Encoding.UTF8.GetBytes(output);
        if (data.LongLength > Capacity) return null;
        var id = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        try
        {
            var path = Resolve(context, id);
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path))
                {
                    var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllBytes(staging, data);
                        File.Move(staging, path);
                    }
                    finally { if (File.Exists(staging)) File.Delete(staging); }
                }
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                Trim(Path.GetDirectoryName(path)!);
            }
            return $"{ReferencePrefix}{id}, {output.Length} символов — подробности: {Name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException) { return null; }
    }

    internal static string Preview(string output, string? reference)
    {
        if (reference == null || output.Length <= PreviewChars) return output;
        var important = CommandLog.Details(output, 1700);
        return reference + "\n" + HistoryCompressor.Excerpt(output, 3300) + (important.Length > 0 ? "\nВажные строки:\n" + important : "");
    }

    static string Read(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var id = args.Required("id");
        var path = Resolve(context, id);
        if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > Retention)
            throw new ToolException("Сохранённый результат не найден или срок его хранения истёк.");
        var output = File.ReadAllText(path, Encoding.UTF8);
        ct.ThrowIfCancellationRequested();
        if (!Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).Equals(id, StringComparison.OrdinalIgnoreCase))
            throw new ToolException("Содержимое сохранённого результата изменилось.");
        int offset = Math.Max(0, args.Int("offset", 0));
        if (offset >= output.Length) throw new ToolException($"Смещение за концом результата ({output.Length} символов).");
        int end = offset + Math.Min(output.Length - offset, Math.Clamp(args.Int("limit", 12_000), 1, MaxPageChars));
        return $"{ReferencePrefix}{id}, символы {offset}–{end - 1} из {output.Length}\n" + output[offset..end] +
               (end < output.Length ? $"\nДальше: offset={end}" : "");
    }

    static string Resolve(ToolContext context, string id)
    {
        if (context.AppFolder == null || id.Length != 64 || !id.All(Uri.IsHexDigit)) throw new ToolException("Недопустимый идентификатор результата.");
        var folder = context.Resolve(null);
        var scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            folder.ToUpperInvariant() + "\n" + context.SandboxRoot?.ToUpperInvariant())));
        var path = context.Resolve(Path.Combine(context.AppFolder, "cache", "tool-results", scope, id.ToLowerInvariant() + ".txt"));
        context.GuardSecrets(path);
        if (!PathHelper.SamePath(path, PathHelper.FinalPath(path))) throw new ToolException("Недопустимый путь результата.");
        return path;
    }

    static void Trim(string folder)
    {
        var root = Path.GetDirectoryName(folder)!;
        var files = new DirectoryInfo(root).EnumerateDirectories().Where(directory =>
                (directory.Attributes & FileAttributes.ReparsePoint) == 0 && PathHelper.SamePath(directory.FullName, PathHelper.FinalPath(directory.FullName)))
            .SelectMany(directory => directory.EnumerateFiles("*.txt")).Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(file => file.LastWriteTimeUtc).ToArray();
        long size = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (size <= Capacity && DateTime.UtcNow - file.LastWriteTimeUtc <= Retention) break;
            long length = file.Length;
            file.Delete();
            size -= length;
        }
    }
}
