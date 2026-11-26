namespace SharpAgent.Agent;

partial class AgentRunner
{
    sealed record ReadKey(string Identity, string? Path = null, string? RelatedPaths = null);
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
        if (ArgsOf(call) is not { } parameters) return null;
        if (call.Name is "exec_command" or "local_batch" && tools.ExternalPaths(call.Name, parameters).Length > 0) return null;
        if (call.Name == "use_plugin" && PluginName(call) is { } plugin) return new("use_plugin " + plugin.ToUpperInvariant());
        try
        {
            if (call.Name == "exec_command" && tools.Context.SandboxRoot == null)
            {
                var shell = new ToolArgs(parameters);
                if (tools.PendingCommandOutput != null || CurrentDiagnosis() is { Repair: false }) return null;
                var folder = PathHelper.FinalPath(tools.Context.Resolve(shell.Optional("working_directory")));
                if (ShellTools.FileInspectionOf(shell) is { } fileName)
                {
                    var candidate = tools.Context.Resolve(Path.Combine(folder, fileName));
                    if (!PathHelper.IsInside(candidate, folder)) return null;
                    var path = PathHelper.FinalPath(candidate);
                    if (!PathHelper.IsInside(path, folder) || tools.Context.IsForeignShare(path) || !File.Exists(path)) return null;
                    tools.Context.GuardSecrets(path);
                    tools.Context.GuardAppFiles(path);
                    long revision = PathHelper.Revision(path);
                    return revision == 0 ? null : new($"shell-read {folder}|{path}|{revision}|{shell.Required("command")}", Path: path);
                }
                return ShellTools.GitInspectionOf(shell) is { } command && Directory.Exists(Path.Combine(folder, ".git")) &&
                    tools.Context.ProjectWatcher.InspectionStamp(folder) is { } gitStamp
                    ? new($"git {folder}|{gitStamp}|{ShellTools.ShellOf(shell)}|{command}", Path: folder) : null;
            }
            if (call.Name != "local_batch" || parameters["operations"] is not JsonArray { Count: > 0 } operations) return null;
            List<string> identity = [], paths = [];
            string? workspace = null;
            foreach (var operation in operations.OfType<JsonObject>())
            {
                var name = Str(operation["name"]);
                if (name is "git_status" or "git_diff" or "grep" or "search_files")
                {
                    var root = PathHelper.FinalPath(tools.Context.Resolve(null));
                    var options = operation["arguments"] as JsonObject ?? [];
                    var folder = PathHelper.FinalPath(tools.Context.Resolve(new ToolArgs(options).Optional("directory")));
                    if (tools.Context.ProjectWatcher.InspectionStamp(root) is not { } stamp || !PathHelper.IsInside(folder, root) ||
                        name is ("git_status" or "git_diff") && !Directory.Exists(Path.Combine(root, ".git"))) return null;
                    workspace = root;
                    identity.Add($"{name}|{root}|{stamp}|{options.ToJsonString()}");
                    continue;
                }
                if (operation["arguments"] is not JsonObject args) return null;
                var requests = name switch
                {
                    "read" => new[] { args },
                    "ranges" => (args["files"] as JsonArray)?.OfType<JsonObject>().ToArray() ?? [],
                    _ => [],
                };
                if (requests.Length == 0) return null;
                foreach (var request in requests)
                {
                    var input = new ToolArgs(request);
                    var path = PathHelper.FinalPath(tools.Context.Resolve(input.Required("path")));
                    tools.Context.GuardSecrets(path);
                    var file = new FileInfo(path);
                    long revision = PathHelper.Revision(path);
                    if (!file.Exists || revision == 0) return null;
                    var (offset, limit) = FileTools.ReadRange(input, name == "ranges" ? 60 : 250);
                    identity.Add($"{path}|{offset}|{limit}|{file.Length}|{revision}");
                    paths.Add(path);
                }
            }
            return identity.Count == 0 ? null : new("local_batch " + string.Join("\u001f", identity),
                Path: workspace, RelatedPaths: paths.Count == 0 ? null : string.Join("\n", paths));
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    string ResultForModel(ToolCallEvent call, ToolOutcome outcome, int index, List<ToolExecution> batch)
    {
        var execution = batch[index];
        if (outcome.IsError || outcome.IsWarning || call.Name == "exec_command" && CommandLog.ResultOf(outcome.Output) != "код 0")
            return outcome.Output;
        if (ToolOutputStore.Applies(call.Name) && (ResultAlreadySent(outcome.Output) ||
            batch.Take(index).Any(previous => previous.Task.IsCompletedSuccessfully &&
                !previous.Task.Result.IsError && previous.Task.Result.Output == outcome.Output)))
            return AlreadySent + " " + (outcome.Output.StartsWith(ToolOutputStore.ReferencePrefix, StringComparison.Ordinal)
                ? outcome.Output.Split('\n', 2)[0] : "Используй недавний результат.");
        if (execution.Key is not { } key) return outcome.Output;
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

    bool ResultAlreadySent(string output) => _sent.Any(node => Role(node) == "tool" &&
        Str(node["content"]) is { } sent && (sent == output || sent == output + RepeatReply));

    HashSet<string> StaleReads()
    {
        var stale = new HashSet<string>(StringComparer.Ordinal);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (call, result, _) in CurrentActions().Reverse())
        {
            foreach (var edit in ChangesOf(result)) changed.Add(edit.Path);
            if (Str(MetaOf(result)?["read_identity"]) is { } identity && ReadKeyOf(call)?.Identity != identity)
            {
                stale.Add(call.Id);
                continue;
            }
            if (changed.Count == 0 || ArgsOf(call) is not { } args) continue;
            var paths = call.Name switch
            {
                "read_file" => new[] { Str(args["path"]) },
                "read_many" => (args["files"] as JsonArray)?.OfType<JsonObject>().Select(item => Str(item["path"])) ?? [],
                _ => [],
            };
            foreach (var path in paths.OfType<string>())
                try
                {
                    if (changed.Contains(PathHelper.FinalPath(tools.Context.Resolve(path)))) { stale.Add(call.Id); break; }
                }
                catch (ToolException) { }
        }
        return stale;
    }

    void InvalidateReads(ToolCallEvent call, string? changedPath)
    {
        bool unknown = changedPath == null && call.Name is ("exec_command" or "run_plugin" or "move_path" or "copy_path" or "delete_path");
        if (unknown || changedPath != null)
        {
            var path = unknown ? null : PathHelper.FinalPath(changedPath!);
            tools.Context.Snapshots.Invalidate(path);
            foreach (var key in _reads.Keys.Where(key =>
                key.Path != null && (path == null || PathHelper.IsInside(key.Path, path) || PathHelper.IsInside(path, key.Path)) ||
                key.RelatedPaths != null && (path == null ||
                    key.RelatedPaths.Split('\n').Any(file => PathHelper.IsInside(file, path)))).ToArray())
                _reads.Remove(key);
        }
        if (call.Name == "exec_command") (_systemMessage, _plugins) = (null, null);
    }
}
