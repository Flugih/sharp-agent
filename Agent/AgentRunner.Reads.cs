namespace SharpAgent.Agent;

partial class AgentRunner
{
    sealed record ReadKey(string Identity, string? Path = null, long Size = 0, DateTime Written = default, DateTime Created = default, string? RelatedPaths = null);
    sealed record ToolExecution(Task<ToolOutcome> Task, ReadKey? Key, string? PreviousId = null);

    List<ToolExecution> PrepareCalls(List<ToolCallEvent> calls, CancellationToken ct)
    {
        var scheduled = new Dictionary<ReadKey, (string Id, Task<ToolOutcome> Task)>();
        var executions = new List<ToolExecution>();
        bool readOnly = CurrentDiagnosis() is { Repair: false };
        foreach (var call in calls)
        {
            var key = ReadKeyOf(call);
            if (CompletedWriteOf(call) is { } completed)
                executions.Add(new(Task.FromResult(new ToolOutcome(completed.Output, false)), null, completed.Id));
            else if (key != null && _reads.TryGetValue(key, out var earlier)) executions.Add(new(Task.FromResult(earlier.Outcome), key, earlier.Id));
            else if (key != null && scheduled.TryGetValue(key, out var current)) executions.Add(new(current.Task, key, current.Id));
            else
            {
                ToolCallStarted?.Invoke(call);
                var task = readOnly && (!ToolRegistry.IsReadOnly(call.Name) || call.Name == "use_plugin") &&
                    call.Name != "ask_user" && call.Name != BrowserTools.Name
                    ? Task.FromResult(new ToolOutcome("Ошибка: диагностика доступна только для чтения.", true)) : ExecuteCallAsync(call, ct);
                if (key != null) scheduled[key] = (call.Id, task);
                executions.Add(new(task, key));
            }
        }
        return executions;
    }

    ReadKey? ReadKeyOf(ToolCallEvent call)
    {
        if (call.Name is not ("read_file" or "read_many" or "use_plugin") || ArgsOf(call) is not { } parameters) return null;
        var args = new ToolArgs(parameters);
        try
        {
            if (call.Name == "use_plugin") return new("use_plugin " + args.Required("name").ToUpperInvariant());
            if (call.Name == "read_many")
            {
                var requests = args.Array("files");
                if (requests.Count is < 1 or > 8 || requests.Any(request => request is not JsonObject)) return null;
                var entries = new List<string>();
                var paths = new List<string>();
                foreach (var request in requests.OfType<JsonObject>())
                {
                    var part = new ToolArgs(request);
                    var resolved = tools.Context.Resolve(part.Required("path"));
                    tools.Context.GuardSecrets(resolved);
                    var info = new FileInfo(PathHelper.FinalPath(resolved));
                    if (!info.Exists) return null;
                    var (start, count) = FileTools.ReadRange(part);
                    entries.Add($"{info.FullName}|{start}|{count}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{info.CreationTimeUtc.Ticks}");
                    paths.Add(info.FullName);
                }
                return new("read_many " + string.Join("\u001f", entries), RelatedPaths: string.Join("\n", paths));
            }
            var path = tools.Context.Resolve(args.Required("path"));
            tools.Context.GuardSecrets(path);
            var file = new FileInfo(PathHelper.FinalPath(path));
            if (!file.Exists) return null;
            var (offset, limit) = FileTools.ReadRange(args);
            return new($"read_file {file.FullName}|{offset}|{limit}", file.FullName, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    string ResultForModel(ToolCallEvent call, ToolOutcome outcome, int index, List<ToolExecution> batch)
    {
        var execution = batch[index];
        if (execution.Key is not { } key || outcome.IsError) return outcome.Output;
        if (call.Name == "use_plugin" && PluginName(call) is { } name)
        {
            _activePlugins[name] = outcome.Output;
            if (execution.PreviousId != null)
                return $"Плагин «{name}» уже открыт. Сразу выполни действие по приложенной инструкции через run_plugin.";
        }
        if (execution.PreviousId is { } id && (batch.Take(index).Any(previous => previous.Key == key) || ResultInContext(id, outcome.Output)))
            return AlreadySent + " Используй его и переходи к следующему шагу.";
        if (_reads.Count < 128 || _reads.ContainsKey(key)) _reads[key] = (call.Id, outcome);
        return outcome.Output;
    }

    bool ResultInContext(string id, string output) => _sent.Any(node => Role(node) == "tool" && Str(node["tool_call_id"]) == id &&
        Str(node["content"])?.StartsWith(output, StringComparison.Ordinal) == true);

    void InvalidateReads(ToolCallEvent call, string? changedPath)
    {
        bool unknown = changedPath == null && call.Name is ("run_command" or "run_plugin" or "move_path" or "copy_path" or "delete_path");
        if (unknown || changedPath != null)
        {
            var path = unknown ? null : PathHelper.FinalPath(changedPath!);
            tools.Context.Snapshots.Invalidate(path);
            foreach (var key in _reads.Keys.Where(key =>
                key.Path != null && (path == null || PathHelper.IsInside(key.Path, path)) ||
                key.RelatedPaths != null && (path == null ||
                    key.RelatedPaths.Split('\n').Any(file => PathHelper.IsInside(file, path)))).ToArray())
                _reads.Remove(key);
        }
        if (call.Name == "run_command") (_systemMessage, _plugins) = (null, null);
    }
}
