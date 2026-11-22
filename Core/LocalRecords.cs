namespace SharpAgent.Core;

sealed class LocalRecords<T> : IDisposable where T : class
{
    readonly LocalDatabase _database;
    readonly string _scope;

    internal LocalRecords(string folder, string database, string scope)
    {
        _scope = scope;
        _database = new(Path.Combine(folder, database + ".db"));
        try { _database.Execute("CREATE TABLE IF NOT EXISTS records(scope TEXT, id TEXT, updated INTEGER, payload TEXT, PRIMARY KEY(scope,id))"); }
        catch
        {
            _database.Dispose();
            throw;
        }
    }

    internal T? Get(string id)
    {
        using var query = _database.Prepare("SELECT payload FROM records WHERE scope=?1 AND id=?2");
        query.Bind(1, _scope);
        query.Bind(2, id);
        return query.Step() ? JsonSerializer.Deserialize<T>(query.Text(0)) : null;
    }

    internal List<T> Recent(int limit)
    {
        using var query = _database.Prepare("SELECT payload FROM records WHERE scope=?1 ORDER BY updated DESC,rowid DESC LIMIT ?2");
        query.Bind(1, _scope);
        query.Bind(2, limit.ToString(CultureInfo.InvariantCulture));
        var results = new List<T>();
        while (query.Step())
            if (JsonSerializer.Deserialize<T>(query.Text(0)) is { } item) results.Add(item);
        return results;
    }

    internal void Save(string id, T item, int limit)
    {
        using (var write = _database.Prepare("INSERT OR REPLACE INTO records VALUES(?1,?2,CAST(strftime('%s','now') AS INTEGER),?3)"))
        {
            write.Bind(1, _scope);
            write.Bind(2, id);
            write.Bind(3, JsonSerializer.Serialize(item));
            write.Step();
        }
        if (limit <= 0) return;
        using var trim = _database.Prepare("DELETE FROM records WHERE rowid IN (SELECT rowid FROM records WHERE scope=?1 ORDER BY updated DESC,rowid DESC LIMIT -1 OFFSET ?2)");
        trim.Bind(1, _scope);
        trim.Bind(2, limit.ToString(CultureInfo.InvariantCulture));
        trim.Step();
    }

    internal void Remove(string id)
    {
        using var delete = _database.Prepare("DELETE FROM records WHERE scope=?1 AND id=?2");
        delete.Bind(1, _scope);
        delete.Bind(2, id);
        delete.Step();
    }

    public void Dispose() => _database.Dispose();

    internal static bool Unavailable(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or DllNotFoundException or EntryPointNotFoundException;
}
