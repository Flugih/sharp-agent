namespace SharpAgent.Agent;

public sealed record ChatSummary(string Path, string Title, DateTime Updated);

public sealed record ChatFile(List<JsonNode> Messages, string? Title);

public sealed class ChatStore(string folder, string logsFolder)
{
    const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public string Folder => folder;
    string TrashFolder => Path.Combine(folder, "trash");

    public string NewPath() => Unique(folder, $"chat_{DateTime.Now:yyyyMMdd_HHmmss}", ".json");

    public ChatFile Load(string path)
    {
        var root = ReadRoot(path);
        return new(root["messages"] is JsonArray array ? array.OfType<JsonObject>().Select(m => (JsonNode)m.DeepClone()).ToList() : [],
            JsonUtil.Str(root["title"]));
    }

    public void Save(string path, IEnumerable<JsonNode> messages, string? model, string? title) => Write(path, new JsonObject
    {
        ["app"] = "SharpAgent",
        ["format"] = "openai-chat-messages",
        ["updated"] = DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture),
        ["title"] = title,
        ["model"] = model,
        ["messages"] = new JsonArray(messages.Select(m => m.DeepClone()).ToArray()),
    });

    public void Rename(string path, string? title)
    {
        var root = ReadRoot(path);
        root["title"] = title;
        Write(path, root);
    }

    public string Quarantine(string path) => Move(path, Unique(folder, "broken", "_" + Path.GetFileName(path)));

    public string? ImportLegacy() => Path.Combine(folder, "current.json") is var legacy && File.Exists(legacy) ? Move(legacy, NewPath()) : null;

    public string MoveToTrash(string path) => MoveTo(path, TrashFolder, DateTime.Now);

    public string Restore(string path) => MoveTo(path, folder, null);

    public void DeleteForever(string path)
    {
        foreach (var artifact in ArtifactsOf(path))
            try { File.Delete(artifact); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        File.Delete(path);
    }

    public int TrashCount() => Directory.Exists(TrashFolder) ? Directory.EnumerateFiles(TrashFolder, "chat_*.json").Count() : 0;

    public List<ChatSummary> List() => Summaries(folder, deleted: false);

    public List<ChatSummary> ListTrash() => Summaries(TrashFolder, deleted: true);

    static List<ChatSummary> Summaries(string directory, bool deleted)
    {
        var result = new List<ChatSummary>();
        if (!Directory.Exists(directory)) return result;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("chat_*.json"))
            try
            {
                if (Summarize(file) is { } chat) result.Add(deleted ? chat with { Updated = file.LastWriteTime } : chat);
            }
            catch { }
        return result;
    }

    public static string TitleOf(IEnumerable<JsonNode> messages, string? title) =>
        !string.IsNullOrWhiteSpace(title) ? title.Trim() : AgentRunner.TitleOf(messages) ?? "Без названия";

    static ChatSummary? Summarize(FileInfo file)
    {
        using var stream = file.OpenRead();
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return null;
        var firstUser = messages.EnumerateArray()
            .Where(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("role", out var role) && role.GetString() == "user")
            .Select(m => JsonNode.Parse(m.GetRawText()))
            .FirstOrDefault();
        if (firstUser == null) return null;
        var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var updated = root.TryGetProperty("updated", out var u) && u.ValueKind == JsonValueKind.String &&
                      DateTime.TryParseExact(u.GetString(), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : file.LastWriteTime;
        return new(file.FullName, TitleOf([firstUser], title), updated);
    }

    IEnumerable<string> ArtifactsOf(string path)
    {
        try
        {
            return Load(path).Messages
                .Where(message => JsonUtil.Str(message["role"]) == "tool")
                .SelectMany(message => new[]
                {
                    CommandLog.IdOf(JsonUtil.Str(message["content"]) ?? "") is { } id ? CommandLog.PathOf(logsFolder, id) : null,
                    FileChanges.BackupPath(logsFolder, AgentRunner.FileChangeOf(message)?.Backup),
                })
                .OfType<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    static string MoveTo(string path, string directory, DateTime? touched)
    {
        Directory.CreateDirectory(directory);
        var target = Move(path, Unique(directory, Path.GetFileNameWithoutExtension(path), ".json"));
        if (touched is { } time) File.SetLastWriteTime(target, time);
        return target;
    }

    static string Move(string path, string target)
    {
        File.Move(path, target);
        return target;
    }

    static string Unique(string directory, string stem, string tail)
    {
        var path = Path.Combine(directory, stem + tail);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(directory, $"{stem}_{n}{tail}");
        return path;
    }

    static JsonObject ReadRoot(string path) =>
        JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject ?? throw new JsonException("В файле нет переписки.");

    static void Write(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(JsonUtil.Pretty), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
}
