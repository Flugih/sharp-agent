namespace SharpAgent.Agent;

sealed class ProjectIndexStore : IDisposable
{
    readonly LocalDatabase _db;
    internal ProjectIndexStore(string folder)
    {
        _db = new(Path.Combine(folder, "projects.db"));
        try { _db.Execute("CREATE TABLE IF NOT EXISTS source (project TEXT, path TEXT COLLATE NOCASE, payload TEXT, PRIMARY KEY(project,path)) WITHOUT ROWID;" +
            "CREATE TABLE IF NOT EXISTS last_access(project TEXT PRIMARY KEY, touched INTEGER) WITHOUT ROWID"); }
        catch
        {
            _db.Dispose();
            throw;
        }
    }

    internal Dictionary<string, IndexedSource> Read(string project)
    {
        Touch(project);
        var result = new Dictionary<string, IndexedSource>(StringComparer.OrdinalIgnoreCase);
        using var query = _db.Prepare("SELECT path,payload FROM source WHERE project=?1");
        query.Bind(1, project);
        while (query.Step())
        {
            var path = query.Text(0);
            if (JsonSerializer.Deserialize<IndexedSource>(query.Text(1)) is { } source) result[path] = source;
        }
        return result;
    }

    internal void Update(string project, Dictionary<string, IndexedSource> previous, Dictionary<string, IndexedSource> current,
        IReadOnlyCollection<string>? affected)
    {
        _db.Execute("BEGIN IMMEDIATE");
        try
        {
            Touch(project);
            using (var remove = _db.Prepare("DELETE FROM source WHERE project=?1 AND path=?2"))
            {
                remove.Bind(1, project);
                foreach (var path in (affected ?? previous.Keys).Where(path => !current.ContainsKey(path)))
                {
                    remove.Bind(2, path);
                    remove.Step();
                    remove.Reset();
                }
            }
            using (var upsert = _db.Prepare("INSERT OR REPLACE INTO source(project,path,payload) VALUES(?1,?2,?3)"))
            {
                upsert.Bind(1, project);
                foreach (var path in affected ?? current.Keys)
                {
                    if (!current.TryGetValue(path, out var entry)) continue;
                    if (previous.TryGetValue(path, out var old) && ReferenceEquals(old, entry)) continue;
                    upsert.Bind(2, path);
                    upsert.Bind(3, JsonSerializer.Serialize(entry));
                    upsert.Step();
                    upsert.Reset();
                }
            }
            _db.Execute("COMMIT");
        }
        catch
        {
            _db.Execute("ROLLBACK");
            throw;
        }
    }

    void Touch(string project)
    {
        using var command = _db.Prepare("INSERT OR REPLACE INTO last_access VALUES(?1, CAST(strftime('%s','now') AS INTEGER))");
        command.Bind(1, project);
        command.Step();
    }

    internal static void Trim(string folder, long limit)
    {
        try
        {
            var file = new FileInfo(Path.Combine(folder, "projects.db"));
            if (!file.Exists || file.Length < limit || !PathHelper.SamePath(file.FullName, PathHelper.FinalPath(file.FullName))) return;
            using var store = new ProjectIndexStore(folder);
            var db = store._db;
            var projects = new List<(string Key, long Bytes)>();
            using (var rows = db.Prepare("SELECT source.project, SUM(length(CAST(payload AS BLOB))) FROM source " +
                "LEFT JOIN last_access ON source.project=last_access.project GROUP BY source.project ORDER BY MAX(COALESCE(touched,0)) DESC"))
                while (rows.Step()) if (long.TryParse(rows.Text(1), out long size)) projects.Add((rows.Text(0), size));
            long total = projects.Sum(project => project.Bytes);
            bool trimmed = false;
            foreach (var project in projects.Skip(1).Reverse())
            {
                if (total <= limit * 3 / 4) break;
                using var delete = db.Prepare("DELETE FROM source WHERE project=?1");
                delete.Bind(1, project.Key);
                delete.Step();
                total -= project.Bytes;
                trimmed = true;
            }
            if (trimmed) db.Execute("VACUUM; PRAGMA wal_checkpoint(TRUNCATE)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { }
    }
    public void Dispose() => _db.Dispose();
}
