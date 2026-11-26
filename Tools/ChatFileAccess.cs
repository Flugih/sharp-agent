namespace SharpAgent.Tools;

internal sealed class ChatFileAccess
{
    readonly object _gate = new();
    readonly AsyncLocal<Run?> _current = new();
    readonly HashSet<Run> _runs = [];
    Run? _unrestricted;

    public IDisposable Begin(bool allowConcurrentReads = false)
    {
        var run = new Run(this, _current.Value, allowConcurrentReads);
        lock (_gate) _runs.Add(run);
        _current.Value = run;
        return run;
    }

    public void Reserve(string path)
    {
        var owner = _current.Value;
        var full = PathHelper.FinalPath(path);
        lock (_gate)
        {
            if (_unrestricted != null && _unrestricted != owner)
                throw new ToolException("Другой чат выполняет задачу с неограниченным доступом к файлам. Дождитесь её завершения.");
            foreach (var run in _runs)
                if (run != owner && run.Paths.Any(other => PathHelper.IsInside(full, other) || PathHelper.IsInside(other, full)))
                    throw new ToolException($"Файл или папка «{full}» зарезервированы другой задачей. Дождитесь её завершения.");
            owner?.Paths.Add(full);
        }
    }

    public void RequireExclusiveAccess(bool reserve)
    {
        var owner = _current.Value;
        if (owner == null) return;
        lock (_gate)
        {
            if (_runs.Any(run => run != owner && (!run.AllowConcurrentReads || run.Paths.Count > 0 || run == _unrestricted)))
                throw new ToolException("Пока работают другие чаты, действия с неизвестным списком изменений недоступны. Используйте файловые инструменты.");
            if (reserve) _unrestricted = owner;
        }
    }

    sealed class Run(ChatFileAccess access, Run? previous, bool allowConcurrentReads) : IDisposable
    {
        public readonly bool AllowConcurrentReads = allowConcurrentReads;
        public readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase);

        public void Dispose()
        {
            lock (access._gate)
            {
                access._runs.Remove(this);
                if (access._unrestricted == this) access._unrestricted = null;
            }
            access._current.Value = previous;
        }
    }
}
