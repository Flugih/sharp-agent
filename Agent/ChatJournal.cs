using System.Buffers;

namespace SharpAgent.Agent;

static class ChatJournal
{
    static ReadOnlySpan<byte> Magic => "{\"format\":\"sharp-agent-chat-jsonl/1\"}\n"u8;
    [Flags]
    internal enum Flags { User = 1, Assistant = 2, Tool = 4, Summary = 8, Stopped = 16 }
    internal sealed record Usage(string? Provider, string? Model, long Prompt, long Completion, bool Response,
        long Cached = 0, long Reasoning = 0, long Requests = 0);
    internal readonly record struct Entry(long Offset, int Length, int EstimatedTokens, Flags Flags, Usage[]? Usage);
    internal sealed record Header(string? Title, string? FirstTitle, string? Model, bool WorkMode, DateTime Updated);
    internal sealed record Index(long End, long Length, long Timestamp, Header Header, Entry[] Entries);
    internal sealed record Change(int Position, JsonNode Message);

    internal static string IndexPath(string path) => path + ".idx";
    internal static string LegacyPath(string path) => path + ".legacy";

    internal static bool IsJournal(string path)
    {
        using var file = Open(path);
        Span<byte> header = stackalloc byte[Magic.Length];
        return file.ReadAtLeast(header, header.Length, false) == header.Length && header.SequenceEqual(Magic);
    }

    internal static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    internal static Entry Describe(JsonNode message, long offset = 0, int length = 0)
    {
        var flags = AgentRunner.Role(message) switch { "user" => Flags.User, "assistant" => Flags.Assistant, "tool" => Flags.Tool, _ => (Flags)0 };
        int estimate = length > 0 ? HistoryCompressor.Estimate(message) : 0;
        if (AgentRunner.MetaOf(message) is not { } meta) return new(offset, length, estimate, flags, null);
        if (Str(meta["summary"]) is { Length: > 0 }) flags |= Flags.Summary;
        if (meta["stopped"] != null && Bool(meta["stopped_by_program"]) != true) flags |= Flags.Stopped;
        List<Usage>? usage = null;
        void Record(JsonObject data, bool response)
        {
            if (data["prompt_tokens"] == null) return;
            var measured = UsageInfo.Read(data);
            (usage ??= []).Add(new(Str(data["provider"]), Str(data["model"]), measured.PromptTokens, measured.CompletionTokens,
                response, measured.CachedTokens, measured.ReasoningTokens, Long(data["requests"])));
        }
        Record(meta, flags.HasFlag(Flags.Assistant));
        if (meta["summary_usage"] is JsonArray extra)
            foreach (var entry in extra.OfType<JsonObject>()) Record(entry, false);
        return new(offset, length, estimate, flags, usage?.ToArray());
    }

    internal static Index ReadIndex(string path)
    {
        using var file = Open(path);
        long length = file.Length, stamp = File.GetLastWriteTimeUtc(path).Ticks;
        try
        {
            using var cache = Open(IndexPath(path));
            if (JsonSerializer.Deserialize<Index>(cache) is { } index && index.Length == length && index.Timestamp == stamp &&
                index.End >= Magic.Length && index.End <= length && index.Header != null && index.Entries != null &&
                index.Entries.All(entry => entry.Length > 0 && entry.Offset >= Magic.Length && entry.Offset < index.End - entry.Length))
                return index;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        Span<byte> magic = stackalloc byte[Magic.Length];
        if (file.ReadAtLeast(magic, magic.Length, false) != magic.Length || !magic.SequenceEqual(Magic)) throw InvalidJournal();
        List<Entry> entries = [];
        List<(int Position, Entry Entry)> pending = [];
        Header? header = null;
        long end = Magic.Length, offset = end;
        var line = new ArrayBufferWriter<byte>();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            for (int read; file.Position < length && (read = file.Read(buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - file.Position)))) > 0;)
            {
                var span = buffer.AsSpan(0, read);
                while (!span.IsEmpty)
                {
                    int newline = span.IndexOf((byte)'\n');
                    if (newline < 0)
                    {
                        line.Write(span);
                        break;
                    }
                    line.Write(span[..newline]);
                    using var json = JsonDocument.Parse(line.WrittenMemory);
                    var root = json.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) throw InvalidJournal();
                    int size = line.WrittenCount;
                    if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                        root.TryGetProperty("index", out var position) && position.ValueKind == JsonValueKind.Number && position.TryGetInt32(out int at) && at >= 0)
                        pending.Add((at, Describe(message.Deserialize<JsonObject>() ?? throw InvalidJournal(), offset, size)));
                    else if (root.TryGetProperty("commit", out var commit))
                    {
                        foreach (var (positionIndex, entry) in pending)
                        {
                            if (positionIndex > entries.Count) throw InvalidJournal();
                            if (positionIndex == entries.Count) entries.Add(entry);
                            else entries[positionIndex] = entry;
                        }
                        if (!root.TryGetProperty("count", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out int last) || last != entries.Count)
                            throw InvalidJournal();
                        pending.Clear();
                        header = commit.Deserialize<Header>() ?? throw InvalidJournal();
                        end = offset + size + 1;
                    }
                    else throw InvalidJournal();
                    offset += size + 1;
                    line.Clear();
                    span = span[(newline + 1)..];
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        var rebuilt = new Index(end, length, stamp, header ?? throw InvalidJournal(), entries.ToArray());
        if (new FileInfo(path).Length == length && File.GetLastWriteTimeUtc(path).Ticks == stamp) WriteIndex(path, rebuilt);
        return rebuilt;
    }

    internal static JsonNode Read(FileStream file, Entry entry)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(entry.Length);
        try
        {
            file.Position = entry.Offset;
            file.ReadExactly(bytes.AsSpan(0, entry.Length));
            return (JsonNode.Parse(bytes.AsSpan(0, entry.Length)) as JsonObject)?.Remove("message", out var message) == true && message != null
                ? message : throw InvalidJournal();
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); }
    }

    internal static Index Write(string path, Index? previous, Header header, IEnumerable<Change> changes, int count, bool migrate = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var target = previous == null ? path + ".writing" : path;
        try
        {
            var entries = new Entry[count];
            previous?.Entries.CopyTo(entries, 0);
            using (var file = new FileStream(target, previous == null ? FileMode.Create : FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete))
            {
                if (previous != null)
                {
                    if (file.Length < previous.End) throw new IOException("Файл истории был изменён.");
                    file.SetLength(previous.End);
                    file.Position = previous.End;
                }
                else file.Write(Magic);
                foreach (var change in changes)
                {
                    long start = file.Position;
                    using (var writer = new Utf8JsonWriter(file, new JsonWriterOptions { Encoder = Compact.Encoder }))
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("index", change.Position);
                        writer.WritePropertyName("message");
                        change.Message.WriteTo(writer, Compact);
                        writer.WriteEndObject();
                    }
                    entries[change.Position] = Describe(change.Message, start, checked((int)(file.Position - start)));
                    file.WriteByte((byte)'\n');
                }
                JsonSerializer.Serialize(file, new { commit = header, count }, Compact);
                file.WriteByte((byte)'\n');
                file.Flush(true);
            }
            if (migrate) File.Replace(target, path, LegacyPath(path));
            else if (previous == null) File.Move(target, path);
            try { File.SetLastWriteTime(path, header.Updated); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var info = new FileInfo(path);
            var index = new Index(info.Length, info.Length, info.LastWriteTimeUtc.Ticks, header, entries);
            WriteIndex(path, index);
            return index;
        }
        finally
        {
            if (previous == null && File.Exists(target)) File.Delete(target);
        }
    }

    static void WriteIndex(string path, Index index)
    {
        var cache = IndexPath(path);
        var temp = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temp)) JsonSerializer.Serialize(file, index, Compact);
            File.Move(temp, cache, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static JsonException InvalidJournal() => new("Файл истории JSON Lines повреждён.");
}
