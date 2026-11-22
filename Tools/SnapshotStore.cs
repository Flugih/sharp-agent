namespace SharpAgent.Tools;

sealed class SnapshotStore
{
    const int MaxBytes = 256 * 1024, MaxFiles = 24;
    sealed record Snapshot(long Size, DateTime Written, DateTime Created, byte[] Hash, string Text, Encoding Encoding);
    readonly Dictionary<string, Snapshot> _files = new(StringComparer.OrdinalIgnoreCase);

    public (string Text, Encoding Encoding)? Read(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes) return null;
        var file = new FileInfo(path);
        lock (_files)
        {
            _files.TryGetValue(path, out var previous);
            if (previous != null && previous.Size == stream.Length && previous.Written == file.LastWriteTimeUtc && previous.Created == file.CreationTimeUtc)
                return (previous.Text, previous.Encoding);
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var hash = SHA256.HashData(bytes);
            var (text, encoding) = previous != null && hash.AsSpan().SequenceEqual(previous.Hash)
                ? (previous.Text, previous.Encoding) : TextFileIO.Decode(bytes);
            _files.Remove(path);
            if (_files.Count >= MaxFiles) _files.Remove(_files.Keys.First());
            _files[path] = new(stream.Length, file.LastWriteTimeUtc, file.CreationTimeUtc, hash, text, encoding);
            return (text, encoding);
        }
    }

    public void Invalidate(string? path)
    {
        lock (_files)
        {
            if (path == null) _files.Clear();
            else foreach (var file in _files.Keys.Where(file => PathHelper.IsInside(file, path)).ToArray()) _files.Remove(file);
        }
    }
}
