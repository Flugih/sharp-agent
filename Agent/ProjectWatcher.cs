namespace SharpAgent.Agent;

sealed class ProjectWatcher : IDisposable
{
    internal enum IndexPhase { Project, Documents, Scans }
    internal sealed record IndexProgress(IndexPhase Phase, int Done = 0, int Total = 0);
    Session? _active;
    internal IndexProgress? Progress => Volatile.Read(ref _active)?.Progress;
    internal event Action? ProgressChanged;

    internal void Watch(ToolContext context)
    {
        string? root = null;
        try
        {
            if (context.AppFolder != null && context.Resolve(null) is { } folder && Directory.Exists(folder))
                root = PathHelper.SamePath(folder, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ||
                    PathHelper.SamePath(folder, Path.GetPathRoot(folder)!) ? null : PathHelper.FinalPath(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException) { }
        if (_active is { } current && root != null && PathHelper.SamePath(current.Root, root) &&
            current.UseSdk == (context.SandboxRoot == null)) return;
        Dispose();
        if (root == null) return;
        try { _active = new(context, root, () => ProgressChanged?.Invoke()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    internal async Task<Dictionary<string, IndexedSource>?> SnapshotAsync(string root, CancellationToken ct)
    {
        var current = Volatile.Read(ref _active);
        return current != null && PathHelper.SamePath(current.Root, root) ? await current.SnapshotAsync(ct) : null;
    }

    internal bool DocumentsReady(string root) => Volatile.Read(ref _active) is { } current &&
        PathHelper.SamePath(current.Root, root) && current.DocumentsReady;

    internal (Guid Session, long Revision)? InspectionStamp(string root) =>
        Volatile.Read(ref _active) is { } current && PathHelper.SamePath(current.Root, root) ? current.InspectionStamp : null;

    internal void Reconcile(string root)
    {
        if (Volatile.Read(ref _active) is { } current && PathHelper.SamePath(current.Root, root)) current.Reconcile();
    }

    public void Dispose() => Interlocked.Exchange(ref _active, null)?.Dispose();

    internal static IEnumerable<string> ReportFiles(IEnumerable<string> files, Action<int, int>? report)
    {
        if (report == null) return files;
        return Track(files.ToArray(), report);

        static IEnumerable<string> Track(string[] batch, Action<int, int> report)
        {
            int interval = Math.Max(1, batch.Length / 100);
            for (int i = 0; i < batch.Length; i++)
            {
                if (i % interval == 0) report(i, batch.Length);
                yield return batch[i];
            }
            report(batch.Length, batch.Length);
        }
    }

    sealed class Session : IDisposable
    {
        static readonly IndexProgress Scanning = new(IndexPhase.Scans);
        readonly ToolContext _context;
        readonly Action _progressChanged;
        readonly FileSystemWatcher _watcher;
        readonly CancellationTokenSource _stop = new();
        readonly SemaphoreSlim _signal = new(0, 1);
        readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
        readonly Guid _inspectionSession = Guid.NewGuid();
        long _inspectionRevision;
        readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource _updated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dictionary<string, IndexedSource>? _snapshot;
        IndexProgress? _progress = new(IndexPhase.Project);
        Task? _scanOcr;
        bool _rescan;
        volatile bool _documentsReady;
        internal string Root { get; }
        internal bool UseSdk => _context.SandboxRoot == null;
        internal bool DocumentsReady => _documentsReady;
        internal (Guid Session, long Revision) InspectionStamp => (_inspectionSession, Interlocked.Read(ref _inspectionRevision));
        internal IndexProgress? Progress => Volatile.Read(ref _progress) ??
            (Volatile.Read(ref _scanOcr) is { IsCompleted: false } ? Scanning : null);

        internal Session(ToolContext context, string root, Action progressChanged)
        {
            Root = root;
            _progressChanged = progressChanged;
            _context = new() { AppFolder = context.AppFolder, WorkingDirectory = context.WorkingDirectory, SandboxRoot = context.SandboxRoot };
            _watcher = new(root) { IncludeSubdirectories = true, InternalBufferSize = 32_768,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            _watcher.Changed += (_, args) =>
            {
                TrackInspectionChange(args.FullPath);
                if (LocalResearchEngine.Supports(args.FullPath)) Enqueue(args.FullPath);
            };
            _watcher.Created += (_, args) => TrackEntry(args.FullPath);
            _watcher.Deleted += (_, args) => TrackEntry(args.FullPath);
            _watcher.Renamed += (_, args) => { TrackEntry(args.OldFullPath); TrackEntry(args.FullPath); };
            _watcher.Error += (_, _) => Reconcile();
            try { _watcher.EnableRaisingEvents = true; }
            catch { _watcher.Dispose(); throw; }
            _ = Task.Run(RunAsync);
        }

        internal void Reconcile()
        {
            Interlocked.Increment(ref _inspectionRevision);
            lock (_pending) { _rescan = true; Invalidate(); }
            Signal();
        }

        void TrackEntry(string path)
        {
            TrackInspectionChange(path);
            Enqueue(path);
        }

        void TrackInspectionChange(string path)
        {
            if (_context.AppFolder is { } data && !PathHelper.IsInside(Root, data) && PathHelper.IsInside(path, data)) return;
            Interlocked.Increment(ref _inspectionRevision);
        }

        void Enqueue(string path)
        {
            var relative = Path.GetRelativePath(Root, path);
            if (!ProjectSourceIndex.Trackable(relative)) return;
            lock (_pending)
            {
                if (_pending.Count < 6000) _pending.Add(relative);
                else _rescan = true;
                Invalidate();
            }
            Signal();
        }

        void Invalidate()
        {
            if (_updated.Task.IsCompleted) _updated = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _snapshot, null);
            _documentsReady = false;
        }

        void Signal()
        {
            try { _signal.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        void SetProgress(IndexProgress? progress)
        {
            if (_progress == progress) return;
            Volatile.Write(ref _progress, progress);
            _progressChanged();
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
                            SetProgress(null);
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
                            SetProgress(new(IndexPhase.Project));
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
                            if (files.Count > 0)
                                current = await ProjectSourceIndex.UpdateAsync(_context, Root, folder, files, UseSdk, ct, current, related,
                                    (done, total) => SetProgress(new(IndexPhase.Project, done, total)));
                            Publish(current);
                            await RefreshDocumentsAsync(changes.SelectMany(ExpandDocuments), false, ct,
                                changes.Select(name => Path.Combine(Root, name)).Where(path => !Path.Exists(path)).ToArray());
                        }
                        Publish(current);
                        SetProgress(null);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException or ArgumentException or InvalidOperationException)
                    {
                        // Retry after transient errors instead of losing the watcher forever.
                        current = null;
                        Volatile.Write(ref _snapshot, null);
                        _ready.TrySetResult();
                        SetProgress(null);
                        await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally { SetProgress(null); _ready.TrySetResult(); }
        }

        void Publish(Dictionary<string, IndexedSource> current)
        {
            lock (_pending)
                if (_pending.Count == 0 && !_rescan)
                {
                    Volatile.Write(ref _snapshot, current);
                    _updated.TrySetResult();
                }
        }

        async Task<Dictionary<string, IndexedSource>> ScanAsync(string folder, CancellationToken ct)
        {
            SetProgress(new(IndexPhase.Project));
            var files = ProjectSourceIndex.Walk(Root, ct, includeDocuments: true).ToArray();
            var source = await ProjectSourceIndex.UpdateAsync(_context, Root, folder, files, UseSdk, ct,
                report: (done, total) => SetProgress(new(IndexPhase.Project, done, total)));
            Publish(source);
            _ready.TrySetResult();
            await RefreshDocumentsAsync(files, true, ct);
            return source;
        }

        IEnumerable<string> ExpandDocuments(string relative)
        {
            var path = Path.Combine(Root, relative);
            if (Directory.Exists(path) && PathHelper.SamePath(path, PathHelper.FinalPath(path)))
                return ProjectSourceIndex.Walk(path, _stop.Token, includeDocuments: true).Select(child => Path.Combine(relative, child));
            return [relative];
        }

        async Task RefreshDocumentsAsync(IEnumerable<string> files, bool reconcile, CancellationToken ct, string[]? removed = null)
        {
            SetProgress(new(IndexPhase.Documents));
            try
            {
                bool ready = await LocalResearchEngine.RefreshAsync(_context, Root, files, reconcile, ct, removed,
                    (done, total) => SetProgress(new(IndexPhase.Documents, done, total)));
                lock (_pending) _documentsReady = ready && _pending.Count == 0 && !_rescan;
                if (_scanOcr is not { IsCompleted: false })
                {
                    var scan = LocalResearchEngine.IndexScanAsync(_context, Root, ct);
                    Volatile.Write(ref _scanOcr, scan);
                    if (!scan.IsCompleted) _ = scan.ContinueWith(_ => _progressChanged(), TaskScheduler.Default);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException) { _documentsReady = false; }
        }

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
            Task pending;
            lock (_pending) pending = _updated.Task;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(2), ct); }
            catch (TimeoutException) { }
            return Volatile.Read(ref _snapshot);
        }

        public void Dispose()
        {
            _watcher.Dispose();
            _stop.Cancel();
            _ready.TrySetResult();
            lock (_pending) _updated.TrySetResult();
        }
    }
}
