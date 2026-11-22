namespace SharpAgent.Agent;

sealed class ProjectWatcher : IDisposable
{
    Session? _active;

    internal void Watch(ToolContext context)
    {
        string? root = null;
        try
        {
            if (context.AppFolder != null) root = LocalProjectContext.ProjectRoot(context, context.Resolve(null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException) { }
        if (_active is { } current && root != null && PathHelper.SamePath(current.Root, root) &&
            current.UseSdk == (context.SandboxRoot == null)) return;
        Dispose();
        if (root == null) return;
        try { _active = new(context, root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    internal async Task<Dictionary<string, IndexedSource>?> SnapshotAsync(string root, CancellationToken ct)
    {
        var current = Volatile.Read(ref _active);
        return current != null && PathHelper.SamePath(current.Root, root) ? await current.SnapshotAsync(ct) : null;
    }

    public void Dispose() => Interlocked.Exchange(ref _active, null)?.Dispose();

    sealed class Session : IDisposable
    {
        readonly ToolContext _context;
        readonly FileSystemWatcher _watcher;
        readonly CancellationTokenSource _stop = new();
        readonly SemaphoreSlim _signal = new(0, 1);
        readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
        readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dictionary<string, IndexedSource>? _snapshot;
        bool _rescan;
        internal string Root { get; }
        internal bool UseSdk => _context.SandboxRoot == null;

        internal Session(ToolContext context, string root)
        {
            Root = root;
            _context = new() { AppFolder = context.AppFolder, WorkingDirectory = context.WorkingDirectory, SandboxRoot = context.SandboxRoot };
            _watcher = new(root) { IncludeSubdirectories = true, InternalBufferSize = 32_768,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Changed += (_, args) =>
            {
                if (ProjectSourceIndex.IsSource(args.FullPath) || SourceProjects.Manifest(args.FullPath)) Enqueue(args.FullPath);
            };
            _watcher.Created += (_, args) => Enqueue(args.FullPath);
            _watcher.Deleted += (_, args) => Enqueue(args.FullPath);
            _watcher.Renamed += (_, args) => { Enqueue(args.OldFullPath); Enqueue(args.FullPath); };
            _watcher.Error += (_, _) =>
            {
                lock (_pending) _rescan = true;
                Volatile.Write(ref _snapshot, null);
                Signal();
            };
            try { _watcher.EnableRaisingEvents = true; }
            catch { _watcher.Dispose(); throw; }
            _ = Task.Run(RunAsync);
        }

        void Enqueue(string path)
        {
            var relative = Path.GetRelativePath(Root, path);
            if (!ProjectSourceIndex.Trackable(relative)) return;
            lock (_pending)
            {
                if (_pending.Count < 6000) _pending.Add(relative);
                else _rescan = true;
            }
            // Pending events invalidate the published snapshot until reindexing finishes.
            Volatile.Write(ref _snapshot, null);
            Signal();
        }

        void Signal()
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        async Task RunAsync()
        {
            var ct = _stop.Token;
            var folder = _context.CodeIndexFolder;
            Dictionary<string, IndexedSource>? current = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        if (current == null)
                        {
                            Directory.CreateDirectory(folder);
                            current = await ScanAsync(folder, ct);
                            Publish(current);
                            _ready.TrySetResult();
                        }
                        // Reconciliation still runs if the OS watcher silently drops events.
                        bool signalled = await _signal.WaitAsync(TimeSpan.FromSeconds(45), ct);
                        if (signalled) await Task.Delay(280, ct);
                        string[] changes;
                        bool rescan;
                        lock (_pending)
                        {
                            changes = [.. _pending];
                            rescan = _rescan || !signalled;
                            _pending.Clear();
                            _rescan = false;
                        }
                        if (rescan) current = await ScanAsync(folder, ct);
                        else if (changes.Length > 0)
                        {
                            var files = ChangedSources(current, changes, ct);
                            var related = Related(current, files);
                            foreach (var manifest in changes.Where(SourceProjects.Manifest))
                            {
                                var path = Path.Combine(Root, manifest);
                                var parent = Path.GetDirectoryName(path)!;
                                var id = SourceProjects.Id(path) + "|";
                                related.UnionWith(current.Where(entry => PathHelper.IsInside(Path.Combine(Root, entry.Key), parent) ||
                                    entry.Value.Syntax.Symbols.Any(symbol => symbol.References?.Any(reference => reference.StartsWith(id, StringComparison.Ordinal)) == true))
                                    .Select(entry => entry.Key));
                            }
                            files.UnionWith(related);
                            if (files.Count == 0) { Publish(current); continue; }
                            current = await ProjectSourceIndex.UpdateAsync(_context, Root, folder, files, UseSdk, ct, current, related);
                        }
                        Publish(current);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException or ArgumentException or InvalidOperationException)
                    {
                        // Retry after transient errors instead of losing the watcher forever.
                        current = null;
                        Volatile.Write(ref _snapshot, null);
                        _ready.TrySetResult();
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally { _ready.TrySetResult(); }
        }

        void Publish(Dictionary<string, IndexedSource> current)
        {
            lock (_pending)
                Volatile.Write(ref _snapshot, _pending.Count == 0 && !_rescan ? current : null);
        }

        async Task<Dictionary<string, IndexedSource>> ScanAsync(string folder, CancellationToken ct) =>
            await ProjectSourceIndex.UpdateAsync(_context, Root, folder,
                await LocalProjectContext.IndexFilesAsync(_context, Root, ct), UseSdk, ct);

        HashSet<string> ChangedSources(Dictionary<string, IndexedSource> current, string[] changes, CancellationToken ct)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in changes)
            {
                var path = Path.Combine(Root, relative);
                if (ProjectSourceIndex.IsSource(path)) files.Add(relative);
                else if (Directory.Exists(path) && PathHelper.SamePath(path, PathHelper.FinalPath(path)))
                    foreach (var child in ProjectSourceIndex.Walk(path, ct)) files.Add(Path.Combine(relative, child));
                else if (!File.Exists(path))
                {
                    var prefix = relative + Path.DirectorySeparatorChar;
                    files.UnionWith(current.Keys.Where(file => file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
                }
            }
            return files;
        }

        static HashSet<string> Related(Dictionary<string, IndexedSource> current, HashSet<string> changes)
        {
            var changed = changes.Select(path => current.GetValueOrDefault(path)).OfType<IndexedSource>()
                .SelectMany(source => source.Syntax.Symbols).ToArray();
            var names = changed.Select(symbol => symbol.Name).ToHashSet(StringComparer.Ordinal);
            var references = changed.SelectMany(symbol => symbol.Dependencies).ToHashSet(StringComparer.Ordinal);
            var ids = changed.Select(symbol => symbol.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var targets = changed.SelectMany(symbol => symbol.References ?? []).ToHashSet(StringComparer.Ordinal);
            var related = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, source) in current)
            {
                if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || changes.Contains(path)) continue;
                if (source.Syntax.Symbols.Any(symbol => symbol.Id != null && targets.Contains(symbol.Id) || symbol.References?.Any(ids.Contains) == true ||
                    references.Contains(symbol.Name) || symbol.Dependencies.Any(names.Contains))) related.Add(path);
            }
            return related;
        }

        internal async Task<Dictionary<string, IndexedSource>?> SnapshotAsync(CancellationToken ct)
        {
            await _ready.Task.WaitAsync(ct);
            return Volatile.Read(ref _snapshot);
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _stop.Cancel();
            _ready.TrySetResult();
        }
    }
}
