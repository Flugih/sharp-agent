namespace SharpAgent.Agent;

partial class AgentRunner
{
    sealed record CompletedWrite(string Id, string Path, string Hash, string Output);
    readonly Dictionary<string, CompletedWrite> _completedWrites = new();

    void RecordChange(ToolCallEvent call, JsonObject message, FileChange? change)
    {
        if (change != null)
            EnsureMeta(message)["file_change"] = new JsonObject
            {
                ["path"] = change.Path, ["before"] = change.BeforeHash, ["after"] = change.AfterHash, ["backup"] = change.Backup,
            };
        if (call.Name is not ("write_file" or "edit_file" or "run_plugin" or AgentKnowledge.Name) || (change?.Path ?? WriteTarget(call)) is not { } path ||
            (change?.AfterHash ?? FileChanges.CurrentHash(path)) is not { } hash) return;
        var meta = EnsureMeta(message);
        meta["written_path"] = path;
        meta["written_hash"] = hash;
    }

    void RestoreCompletedWrites()
    {
        _completedWrites.Clear();
        foreach (var (call, result, _) in CurrentActions())
            if (MetaOf(result) is { } meta && Str(meta["written_path"]) is { } path && Str(meta["written_hash"]) is { } hash)
                _completedWrites[WriteKey(call)] = new(call.Id, AppPaths.Relocate(path), hash,
                    $"Действие уже успешно выполнено до остановки. Файл {path} сохранил записанное содержимое. " +
                    $"Переходи к оставшимся шагам. Предыдущий результат: {Str(result["content"])?.Split('\n', 2)[0]}");
    }

    CompletedWrite? CompletedWriteOf(ToolCallEvent call) =>
        _completedWrites.Count > 0 && _completedWrites.TryGetValue(WriteKey(call), out var completed) &&
        WriteTarget(call) is { } path && PathHelper.SamePath(path, completed.Path) && FileChanges.CurrentHash(path) == completed.Hash
            ? completed : null;

    static string WriteKey(ToolCallEvent call)
    {
        static JsonNode? Ordered(JsonNode? value) => value switch
        {
            JsonObject map => new JsonObject(map.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => KeyValuePair.Create(pair.Key, Ordered(pair.Value)))),
            JsonArray list => new JsonArray([.. list.Select(Ordered)]),
            _ => value?.DeepClone(),
        };
        return call.Name + " " + Ordered(ArgsOf(call))?.ToJsonString(Compact);
    }

    string? WriteTarget(ToolCallEvent call)
    {
        if (call.Name is not ("write_file" or "edit_file" or "run_plugin") || ArgsOf(call) is not { } args) return null;
        try
        {
            var raw = call.Name == "run_plugin" ? PluginTools.TargetOf(new ToolArgs(args), tools.Context) : Str(args["path"]);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var path = PathHelper.FinalPath(tools.Context.Resolve(raw));
            tools.Context.GuardSecrets(path);
            tools.Context.GuardAppFiles(path);
            return path;
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    static JsonObject? FileChangeMeta(JsonNode message) => MetaOf(message)?["file_change"] as JsonObject;

    internal static FileChange? FileChangeOf(JsonNode message) =>
        FileChangeMeta(message) is { } info && Str(info["path"]) is { } path && Str(info["after"]) is { } after
            ? new FileChange(AppPaths.Relocate(path), Str(info["before"]), after, Str(info["backup"]))
            : null;

    internal static bool FileChangeUndone(JsonNode message) => Bool(FileChangeMeta(message)?["undone"]) == true;

    internal async Task MarkFileChangeUndoneAsync(IEnumerable<(FileChange Change, int Steps)> changes)
    {
        foreach (var (change, steps) in changes)
        {
            string? after = change.AfterHash;
            for (int i = _history.Count - 1, left = steps; i >= 0 && left > 0; i--)
                if (FileChangeOf(_history[i]) is { } step && step.AfterHash == after && !FileChangeUndone(_history[i]) &&
                    PathHelper.SamePath(step.Path, change.Path))
                {
                    FileChangeMeta(_history.Edit(i))!["undone"] = true;
                    after = step.BeforeHash;
                    left--;
                }
        }
        InvalidateProjectContext();
        await SaveAsync();
    }
}
