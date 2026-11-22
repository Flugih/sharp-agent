namespace SharpAgent.Agent;

sealed class AgentProgressManager
{
    internal sealed record Operation(string Id, string Name, string Executor, string State, DateTimeOffset Started, DateTimeOffset? Ended = null,
        int Step = 0, string? Parent = null, string? Model = null, UsageInfo? Usage = null, int EstimatedInput = 0, int Images = 0,
        int Retries = 0, string? Error = null);
    internal sealed record Run(string Id, string? Chat, string Provider, string State, DateTimeOffset Started, DateTimeOffset Ended,
        int Retries, Dictionary<string, UsageInfo> Usage, Operation[] Operations);

    readonly string _id = Guid.NewGuid().ToString("N");
    readonly DateTimeOffset _started = DateTimeOffset.UtcNow;
    readonly Dictionary<string, Operation> _operations = [];
    readonly Dictionary<string, UsageInfo> _usage = new(StringComparer.OrdinalIgnoreCase);
    readonly object _gate = new();
    string? _phase;
    int _retries, _step;

    internal void Phase(string name, string executor)
    {
        lock (_gate)
        {
            var id = Guid.NewGuid().ToString("N");
            Begin(id, name, executor);
            _phase = id;
        }
    }

    internal void Begin(string id, string name, string executor, string? parent = null, string? model = null, int estimated = 0, int images = 0)
    {
        lock (_gate)
        {
            if (_phase is { } phase && id != phase)
            {
                End(phase, "completed");
                _phase = null;
            }
            _operations[id] = new(id, name, executor, "running", DateTimeOffset.UtcNow, Step: ++_step,
                Parent: parent, Model: model, EstimatedInput: estimated, Images: images);
        }
    }

    internal void End(string id, string state)
    {
        lock (_gate)
            if (_operations.TryGetValue(id, out var value)) _operations[id] = value with { State = state, Ended = DateTimeOffset.UtcNow };
    }

    internal void ModelResult(string id, string state, UsageInfo? usage, string? error = null)
    {
        lock (_gate)
            if (_operations.TryGetValue(id, out var operation))
                _operations[id] = operation with { Usage = usage, Error = error, State = state, Ended = DateTimeOffset.UtcNow };
    }

    internal void Retry(string id)
    {
        lock (_gate)
        {
            _retries++;
            if (_operations.TryGetValue(id, out var operation)) _operations[id] = operation with { Retries = operation.Retries + 1 };
        }
    }

    internal static Run? Last(ToolContext context, string? chat)
    {
        if (context.AppFolder == null || chat == null || !File.Exists(Path.Combine(context.DatabaseFolder, "usage.db"))) return null;
        try
        {
            using var usage = new LocalRecords<Run>(context.DatabaseFolder, "usage", Path.GetFileName(chat));
            return usage.Recent(1).FirstOrDefault();
        }
        catch (Exception ex) when (LocalRecords<Run>.Unavailable(ex)) { return null; }
    }

    internal void Usage(string model, UsageInfo usage)
    {
        lock (_gate) _usage[model] = _usage.TryGetValue(model, out var current) ? current.Add(usage) : usage;
    }

    internal void Save(ToolContext context, string? chat, AgentProvider provider, string state)
    {
        if (context.AppFolder == null) return;
        Run run;
        lock (_gate)
        {
            foreach (var active in _operations.Values.Where(op => op.Ended == null).ToArray()) End(active.Id, state);
            run = new(_id, Path.GetFileName(chat), provider.ToString(), state, _started, DateTimeOffset.UtcNow, _retries, new(_usage), [.. _operations.Values]);
        }
        try
        {
            using var usage = new LocalRecords<Run>(context.DatabaseFolder, "usage", run.Chat ?? "unsaved");
            usage.Save(_id, run, 80);
        }
        catch (Exception ex) when (LocalRecords<Run>.Unavailable(ex)) { }
    }
}
