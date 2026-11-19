namespace SharpAgent.Agent;

public sealed record ChatSummary(string Path, string Title, DateTime Updated);

public sealed record ChatFile(List<JsonNode> Messages, string? Title);

public sealed class ChatStore(string folder, string logsFolder)
{
    const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public string Folder => folder;
    string TrashFolder => Path.Combine(folder, "trash");

    public string NewPath() => Path.Combine(folder, $"chat_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.json");

    public ChatFile Load(string path)
    {
        var root = ReadRoot(path);
        if (root["messages"] is not JsonArray array) return new([], Str(root["title"]));
        var messages = array.Where(message => message is JsonObject).Cast<JsonNode>().ToList();
        array.Clear();
        return new(messages, Str(root["title"]));
    }

    public void Save(string path, IEnumerable<JsonNode> messages, string? model, string? title) => Write(path, writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("app", "SharpAgent");
        writer.WriteString("format", "openai-chat-messages");
        writer.WriteString("updated", DateTime.Now.ToString(TimeFormat, CultureInfo.InvariantCulture));
        writer.WriteString("title", title);
        writer.WriteString("model", model);
        writer.WriteStartArray("messages");
        foreach (var message in messages)
        {
            message.WriteTo(writer, Pretty);
            if (writer.BytesPending >= 64 * 1024) writer.Flush();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public void Rename(string path, string? title)
    {
        var root = ReadRoot(path);
        root["title"] = title;
        Write(path, writer => root.WriteTo(writer, Pretty), File.GetLastWriteTimeUtc(path));
    }

    public string Quarantine(string path) => Move(path, Unique(folder, "broken", "_" + Path.GetFileName(path)));

    public string? ImportLegacy() => Path.Combine(folder, "current.json") is var legacy && File.Exists(legacy) ? Move(legacy, NewPath()) : null;

    public string MoveToTrash(string path) => MoveTo(path, TrashFolder, DateTime.Now);

    public string Restore(string path)
    {
        DateTime? updated = null;
        try
        {
            if (DateTime.TryParseExact(Str(ReadRoot(path)["updated"]), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                updated = time;
        }
        catch { }
        var restored = MoveTo(path, folder, null);
        if (updated is { } previous)
            try { File.SetLastWriteTime(restored, previous); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return restored;
    }

    public void DeleteForever(string path)
    {
        foreach (var artifact in ArtifactsOf(path))
            try { File.Delete(artifact); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        File.Delete(path);
    }

    public int TrashCount() => Directory.Exists(TrashFolder) ? Directory.EnumerateFiles(TrashFolder, "chat_*.json").Count() : 0;

    internal FileInfo[] ListChatFiles() => ChatFiles(folder)
        .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public List<ChatSummary> ListTrash() => Summaries(ChatFiles(TrashFolder));

    static IEnumerable<FileInfo> ChatFiles(string directory) =>
        Directory.Exists(directory) ? new DirectoryInfo(directory).EnumerateFiles("chat_*.json") : [];

    internal static List<ChatSummary> Summaries(IEnumerable<FileInfo> files)
    {
        var result = new List<ChatSummary>();
        foreach (var file in files)
            try
            {
                if (Summarize(file) is { } chat) result.Add(chat);
            }
            catch { }
        return result;
    }

    public static string TitleOf(IEnumerable<JsonNode> messages, string? title) =>
        !string.IsNullOrWhiteSpace(title) ? title.Trim() : AgentRunner.TitleOf(messages) ?? "Без названия";

    static ChatSummary? Summarize(FileInfo file)
    {
        using var stream = File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var prefix = new byte[Math.Min(8192, stream.Length)];
        int filled = 0;
        while (true)
        {
            filled += stream.ReadAtLeast(prefix.AsSpan(filled), prefix.Length - filled, false);
            if (HeaderTitle(prefix.AsSpan(0, filled), out var legacy) is { } display)
                return new(file.FullName, display, file.LastWriteTime);
            if (legacy || filled >= stream.Length || filled < prefix.Length) break;
            Array.Resize(ref prefix, (int)Math.Min(stream.Length, Math.Min((long)prefix.Length * 2, Array.MaxLength)));
            if (prefix.Length == filled) break;
        }
        stream.Position = 0;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array) return null;
        var firstUser = messages.EnumerateArray()
            .Where(m => m.ValueKind == JsonValueKind.Object && m.TryGetProperty("role", out var role) && role.GetString() == "user")
            .Select(m => JsonNode.Parse(m.GetRawText()))
            .FirstOrDefault();
        if (firstUser == null) return null;
        var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        return new(file.FullName, TitleOf([firstUser], title), file.LastWriteTime);
    }

    static string? HeaderTitle(ReadOnlySpan<byte> bytes, out bool legacy)
    {
        legacy = false;
        if (bytes.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[Encoding.UTF8.Preamble.Length..];
        var reader = new Utf8JsonReader(bytes, false, default);
        bool sawTitle = false;
        while (reader.Read())
        {
            if (reader.CurrentDepth != 1 || reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.ValueTextEquals("title"u8))
            {
                if (!reader.Read()) return null;
                if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } name && !string.IsNullOrWhiteSpace(name)) return name.Trim();
                sawTitle = true;
            }
            else if (reader.ValueTextEquals("messages"u8))
            {
                if (!sawTitle) { legacy = true; return null; }
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return null;
                while (reader.Read() && reader.TokenType == JsonTokenType.StartObject && JsonDocument.TryParseValue(ref reader, out var item))
                {
                    using (item)
                        if (item.RootElement.TryGetProperty("role", out var role) && role.ValueEquals("user"u8))
                            return TitleOf([JsonNode.Parse(item.RootElement.GetRawText())!], null);
                }
                return null;
            }
        }
        return null;
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
                .OfType<string>().ToList();
        }
        catch { return []; }
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

    static JsonObject ReadRoot(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw new JsonException("В файле нет переписки.");
    }

    static void Write(string path, Action<Utf8JsonWriter> write, DateTime? modifiedUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var file = File.Create(temp))
        using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = Pretty.WriteIndented, Encoder = Pretty.Encoder }))
        {
            write(writer);
        }
        if (modifiedUtc is { } time) File.SetLastWriteTimeUtc(temp, time);
        try { File.Move(temp, path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
    }
}
