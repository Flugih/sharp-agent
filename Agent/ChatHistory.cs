using System.Collections;
using Flags = SharpAgent.Agent.ChatJournal.Flags;

namespace SharpAgent.Agent;

sealed class ChatHistory : IReadOnlyList<JsonNode>
{
    const int CacheBytes = 2 * 1024 * 1024, CacheCount = 64;
    readonly Dictionary<int, (JsonNode Message, long Version)> _pending = [];
    readonly Dictionary<int, (JsonNode Message, int Bytes)> _cache = [];
    ChatJournal.Index? _index;
    string? _path;
    int _cachedBytes;
    long _version;
    public int Count { get; private set; }

    internal ChatHistory() { }

    internal ChatHistory(string path, ChatJournal.Index index)
    {
        _path = path;
        _index = index;
        Count = index.Entries.Length;
    }

    public JsonNode this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            using var file = IsCached(index) ? null : ChatJournal.Open(_path!);
            return Read(index, file);
        }
    }

    bool IsCached(int index) => _pending.ContainsKey(index) || _cache.ContainsKey(index);

    JsonNode Read(int index, FileStream? file)
    {
        if (_pending.TryGetValue(index, out var pending)) return pending.Message;
        if (_cache.TryGetValue(index, out var cached)) return cached.Message;
        var entry = _index!.Entries[index];
        var message = ChatJournal.Read(file!, entry);
        Remember(index, message, entry.Length);
        return message;
    }

    void Remember(int index, JsonNode message, int length)
    {
        if (_cache.Remove(index, out var previous)) _cachedBytes -= previous.Bytes;
        if (length > CacheBytes) return;
        if (_cache.Count >= CacheCount || _cachedBytes + length > CacheBytes)
        {
            _cache.Clear();
            _cachedBytes = 0;
        }
        _cache[index] = (message, length);
        _cachedBytes += length;
    }

    internal IEnumerable<JsonNode> Range(int start, int count)
    {
        FileStream? file = null;
        try
        {
            for (int i = start; i < start + count; i++)
            {
                if (!IsCached(i)) file ??= ChatJournal.Open(_path!);
                yield return Read(i, file);
            }
        }
        finally { file?.Dispose(); }
    }

    internal ChatJournal.Entry EntryAt(int index) => _pending.TryGetValue(index, out var pending)
        ? ChatJournal.Describe(pending.Message) : _index!.Entries[index];

    internal int LastIndex(Flags flags)
    {
        for (int i = Count - 1; i >= 0; i--)
            if ((EntryAt(i).Flags & flags) != 0) return i;
        return -1;
    }

    internal int ContextStart(int start, int tokens)
    {
        int i = Count;
        while (i > start && tokens > 0)
        {
            var entry = EntryAt(--i);
            tokens -= entry.EstimatedTokens > 0 ? entry.EstimatedTokens : HistoryCompressor.Estimate(this[i]);
        }
        while (i > start && i < Count && EntryAt(i).Flags.HasFlag(Flags.Tool)) i--;
        return i;
    }

    internal JsonObject Edit(int index)
    {
        var message = (JsonObject)this[index];
        _pending[index] = (message, ++_version);
        return message;
    }

    internal void Add(JsonNode message) => _pending.Add(Count++, (message, ++_version));

    internal async Task SaveAsync(string path, string? model, string? title, bool workMode, DateTime? updated = null)
    {
        var firstTitle = _index?.Header.FirstTitle ?? ChatStore.FallbackTitle(Range(0, Count));
        var header = new ChatJournal.Header(title, firstTitle, model, workMode, updated ?? DateTime.Now);
        var pending = _pending.OrderBy(item => item.Key).Select(item => (item.Key, item.Value.Version,
            Change: new ChatJournal.Change(item.Key, item.Value.Message.DeepClone()))).ToArray();
        var baseline = _index;
        int count = Count;
        var index = await Task.Run(() => ChatJournal.Write(path, baseline, header, pending.Select(item => item.Change), count));
        _index = index;
        _path = path;
        foreach (var item in pending)
            if (_pending.TryGetValue(item.Key, out var value) && value.Version == item.Version)
            {
                _pending.Remove(item.Key);
                Remember(item.Key, value.Message, index.Entries[item.Key].Length);
            }
    }

    internal string DisplayTitle(string? title)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title;
        return _index?.Header.FirstTitle ?? ChatStore.TitleOf(Range(0, Count), null);
    }

    internal View Snapshot() => new(this, Count);
    public IEnumerator<JsonNode> GetEnumerator() => Range(0, Count).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal sealed class View(ChatHistory history, int count)
    {
        internal int Count => count;
        internal bool IsUser(int index) => history.EntryAt(index).Flags.HasFlag(Flags.User);
        internal IEnumerable<JsonNode> Range(int start, int end) => history.Range(start, end - start);
    }
}
