namespace SharpAgent.Agent;

public sealed record ChatSummary(string Path, string Title, DateTime Updated);

internal sealed record ChatFile(ChatHistory Messages, string? Title, string? Model, bool WorkMode);

public sealed class ChatStore(string folder, string logsFolder)
{
    const string TimeFormat = "yyyy-MM-dd HH:mm:ss";

    public string Folder => folder;
    string TrashFolder => Path.Combine(folder, "trash");

    public string NewPath() => Path.Combine(folder, $"chat_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.jsonl");

    internal ChatFile Load(string path)
    {
        var index = ChatJournal.IsJournal(path) ? ChatJournal.ReadIndex(path) : Migrate(path);
        return new(new(path, index), index.Header.Title, index.Header.Model, index.Header.WorkMode);
    }

    static ChatJournal.Index Migrate(string path)
    {
        var root = ReadRoot(path);
        var messages = LegacyMessages(root).ToList();
        AgentRunner.RepairHistory(messages);
        var header = new ChatJournal.Header(Str(root["title"]), FallbackTitle(messages), Str(root["model"]),
            Bool(root["work_mode"]) != false, File.GetLastWriteTime(path));
        return ChatJournal.Write(path, null, header, messages.Select((message, i) => new ChatJournal.Change(i, message)), messages.Count, migrate: true);
    }

    static IEnumerable<JsonNode> LegacyMessages(JsonObject root) => (root["messages"] as JsonArray)?.OfType<JsonObject>() ?? [];

    internal IEnumerable<(int Position, JsonNode Message)> ReadMessages(string path, bool textOnly = false)
    {
        if (!ChatJournal.IsJournal(path))
        {
            int position = 0;
            foreach (var message in LegacyMessages(ReadRoot(path))) yield return (position++, message);
            yield break;
        }
        var index = ChatJournal.ReadIndex(path);
        using var file = ChatJournal.Open(path);
        for (int i = 0; i < index.Entries.Length; i++)
        {
            var entry = index.Entries[i];
            if (!textOnly || (entry.Flags & (ChatJournal.Flags.User | ChatJournal.Flags.Assistant)) != 0)
                yield return (i, ChatJournal.Read(file, entry));
        }
    }

    public void Rename(string path, string? title)
    {
        if (ChatJournal.IsJournal(path))
        {
            var index = ChatJournal.ReadIndex(path);
            ChatJournal.Write(path, index, index.Header with { Title = title }, [], index.Entries.Length);
            return;
        }
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
            if (ChatJournal.IsJournal(path)) updated = ChatJournal.ReadIndex(path).Header.Updated;
            else if (DateTime.TryParseExact(Str(ReadRoot(path)["updated"]), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
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
        File.Delete(ChatJournal.IndexPath(path));
        File.Delete(ChatJournal.LegacyPath(path));
    }

    public int TrashCount() => ChatFiles(TrashFolder).Count();

    internal FileInfo[] ListChatFiles() => ChatFiles(folder)
        .OrderByDescending(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public List<ChatSummary> ListTrash() => Summaries(ChatFiles(TrashFolder));

    static IEnumerable<FileInfo> ChatFiles(string directory) => Directory.Exists(directory)
        ? new DirectoryInfo(directory).EnumerateFiles("chat_*.json*").Where(file => file.Extension is ".json" or ".jsonl") : [];

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
        !string.IsNullOrWhiteSpace(title) ? title.Trim() : FallbackTitle(messages) ?? "Без названия";

    internal static string? FallbackTitle(IEnumerable<JsonNode> messages) =>
        AgentRunner.TitleOf(messages) is { } title ? AgentRunner.Clip(title, 400) : null;

    static ChatSummary? Summarize(FileInfo file)
    {
        if (ChatJournal.IsJournal(file.FullName))
        {
            var header = ChatJournal.ReadIndex(file.FullName).Header;
            return new(file.FullName, header.Title ?? header.FirstTitle ?? "Без названия", file.LastWriteTime);
        }
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
            return ReadMessages(path).Select(item => item.Message)
                .Where(message => JsonUtil.Str(message["role"]) == "tool")
                .SelectMany(message => AgentRunner.ChangesOf(message).Select(change => FileChanges.BackupPath(logsFolder, change.Backup))
                    .Append(CommandLog.IdOf(JsonUtil.Str(message["content"]) ?? "") is { } id ? CommandLog.PathOf(logsFolder, id) : null))
                .OfType<string>().ToList();
        }
        catch { return []; }
    }

    static string MoveTo(string path, string directory, DateTime? touched)
    {
        Directory.CreateDirectory(directory);
        var target = Move(path, Unique(directory, Path.GetFileNameWithoutExtension(path), Path.GetExtension(path)));
        if (touched is { } time) File.SetLastWriteTime(target, time);
        return target;
    }

    static string Move(string path, string target)
    {
        var legacy = ChatJournal.LegacyPath(path);
        var targetLegacy = ChatJournal.LegacyPath(target);
        bool hasLegacy = File.Exists(legacy);
        if (hasLegacy) File.Move(legacy, targetLegacy);
        try { File.Move(path, target); }
        catch
        {
            if (hasLegacy) File.Move(targetLegacy, legacy);
            throw;
        }
        try
        {
            if (File.Exists(ChatJournal.IndexPath(path))) File.Move(ChatJournal.IndexPath(path), ChatJournal.IndexPath(target), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return target;
    }

    static string Unique(string directory, string stem, string tail)
    {
        var path = Path.Combine(directory, stem + tail);
        for (int n = 2; File.Exists(path) || File.Exists(ChatJournal.LegacyPath(path)); n++) path = Path.Combine(directory, $"{stem}_{n}{tail}");
        return path;
    }

    static JsonObject ReadRoot(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        Span<byte> prefix = stackalloc byte[4];
        int length = stream.ReadAtLeast(prefix, prefix.Length, false);
        var bytes = prefix[..length];
        if (bytes.StartsWith(Encoding.UTF8.Preamble)) stream.Position = Encoding.UTF8.Preamble.Length;
        else
        {
            stream.Position = 0;
            if (bytes.StartsWith(Encoding.Unicode.Preamble) || bytes.StartsWith(Encoding.BigEndianUnicode.Preamble) ||
                bytes.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? throw new JsonException("В файле нет переписки.");
            }
        }
        return JsonNode.Parse(stream) as JsonObject ?? throw new JsonException("В файле нет переписки.");
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
