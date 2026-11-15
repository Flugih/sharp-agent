using System.Text.Json.Nodes;
using SharpAgent.Core;
using SharpAgent.Tools;

namespace SharpAgent.Agent;

public sealed partial class AgentRunner
{
    private sealed record ReadKey(string Identity, string? Path = null, long Size = 0, DateTime Written = default, DateTime Created = default);
    private sealed record ToolExecution(Task<ToolOutcome> Task, ReadKey? Key, string? PreviousId = null);

    private ToolExecution[] PrepareCalls(IReadOnlyList<ToolCallEvent> calls, CancellationToken ct)
    {
        var scheduled = new Dictionary<ReadKey, (string Id, Task<ToolOutcome> Task)>();
        var executions = new ToolExecution[calls.Count];
        for (int i = 0; i < calls.Count; i++)
        {
            var call = calls[i];
            var key = ReadKeyOf(call);
            if (CompletedWriteOf(call) is { } completed)
                executions[i] = new(Task.FromResult(new ToolOutcome(completed.Output, false)), null, completed.Id);
            else if (key != null && _reads.TryGetValue(key, out var earlier))
                executions[i] = new(Task.FromResult(earlier.Outcome), key, earlier.Id);
            else if (key != null && scheduled.TryGetValue(key, out var current))
                executions[i] = new(current.Task, key, current.Id);
            else
            {
                ToolCallStarted?.Invoke(call);
                var task = ExecuteCallAsync(call, ct);
                if (key != null) scheduled[key] = (call.Id, task);
                executions[i] = new(task, key);
            }
        }
        return executions;
    }

    private ReadKey? ReadKeyOf(ToolCallEvent call)
    {
        if (call.Name is not ("read_file" or "use_plugin") || JsonUtil.ParseObject(call.ArgumentsJson) is not { } parameters)
            return null;
        var args = new ToolArgs(parameters);
        try
        {
            if (call.Name == "use_plugin") return new ReadKey("use_plugin " + args.Required("name").ToUpperInvariant());
            var path = tools.Context.Resolve(args.Required("path"));
            tools.Context.GuardSecrets(path);
            var file = new FileInfo(PathHelper.FinalPath(path));
            if (!file.Exists) return null;
            var (offset, limit) = FileTools.ReadRange(args);
            return new ReadKey($"read_file {file.FullName}|{offset}|{limit}", file.FullName,
                file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private string ResultForModel(ToolExecution execution, ToolCallEvent call, ToolOutcome outcome,
        int index, ToolExecution[] batch)
    {
        if (execution.Key is not { } key || outcome.IsError) return outcome.Output;
        if (call.Name == "use_plugin" && PluginName(call) is { } name)
        {
            _activePlugins[name] = outcome.Output;
            if (execution.PreviousId != null)
                return $"Плагин «{name}» уже открыт. Сразу выполни действие по приложенной инструкции через run_plugin.";
        }
        if (execution.PreviousId is { } id &&
            (batch.Take(index).Any(previous => previous.Key == key) || ResultInContext(id, outcome.Output)))
            return "Полный результат этого действия уже передан. Используй его и переходи к следующему шагу.";
        if (_reads.Count < 128 || _reads.ContainsKey(key)) _reads[key] = (call.Id, outcome);
        return outcome.Output;
    }

    private bool ResultInContext(string id, string output) =>
        _sent.Any(node => node is JsonObject message && JsonUtil.Str(message["role"]) == "tool" &&
                          JsonUtil.Str(message["tool_call_id"]) == id &&
                          JsonUtil.Str(message["content"])?.StartsWith(output, StringComparison.Ordinal) == true);

    private void InvalidateReads(string tool)
    {
        foreach (var key in _reads.Keys.Where(key => key.Path != null).ToArray()) _reads.Remove(key);
        if (tool == "run_command")
        {
            _systemMessage = null;
            _plugins = null;
        }
    }
}
