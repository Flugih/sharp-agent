namespace SharpAgent.Agent;

partial class AgentRunner
{
    sealed record ReadKey(string Identity, string? Path = null, long Size = 0, DateTime Written = default, DateTime Created = default);
    sealed record ToolExecution(Task<ToolOutcome> Task, ReadKey? Key, string? PreviousId = null);

    List<ToolExecution> PrepareCalls(List<ToolCallEvent> calls, CancellationToken ct)
    {
        var scheduled = new Dictionary<ReadKey, (string Id, Task<ToolOutcome> Task)>();
        var executions = new List<ToolExecution>();
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
                var task = ExecuteCallAsync(call, ct);
                if (key != null) scheduled[key] = (call.Id, task);
                executions.Add(new(task, key));
            }
        }
        return executions;
    }

    ReadKey? ReadKeyOf(ToolCallEvent call)
    {
        if (call.Name is not ("read_file" or "use_plugin") || ArgsOf(call) is not { } parameters) return null;
        var args = new ToolArgs(parameters);
        try
        {
            if (call.Name == "use_plugin") return new("use_plugin " + args.Required("name").ToUpperInvariant());
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
            return "Полный результат этого действия уже передан. Используй его и переходи к следующему шагу.";
        if (_reads.Count < 128 || _reads.ContainsKey(key)) _reads[key] = (call.Id, outcome);
        return outcome.Output;
    }

    bool ResultInContext(string id, string output) => _sent.Any(node => Role(node) == "tool" && Str(node["tool_call_id"]) == id &&
        Str(node["content"])?.StartsWith(output, StringComparison.Ordinal) == true);

    void InvalidateReads(ToolCallEvent call)
    {
        foreach (var key in _reads.Keys.Where(key => key.Path != null).ToArray()) _reads.Remove(key);
        if (call.Name == "run_command") (_systemMessage, _plugins) = (null, null);
        InvalidateProjectContext(call);
    }
}
