using System.Text.Json.Nodes;
using SharpAgent.Core;
using SharpAgent.Tools;

namespace SharpAgent.Agent;

public sealed partial class AgentRunner
{
    private sealed record CompletedWrite(string Id, string Path, string Hash, string Output);
    private readonly Dictionary<string, CompletedWrite> _completedWrites = new(StringComparer.Ordinal);

    private void RecordCompletedWrite(ToolCallEvent call, JsonObject result, FileChange? change)
    {
        if (call.Name is not ("write_file" or "edit_file" or "run_plugin")) return;
        var path = change?.Path ?? WriteTarget(call);
        if (path == null || (change?.AfterHash ?? FileChanges.CurrentHash(path)) is not { } hash) return;
        if (result[MetaField] is not JsonObject meta) result[MetaField] = meta = new JsonObject();
        meta["written_path"] = path;
        meta["written_hash"] = hash;
    }

    private void RestoreCompletedWrites()
    {
        _completedWrites.Clear();
        foreach (var (call, result) in CurrentActions())
        {
            if (result[MetaField] is not JsonObject meta || JsonUtil.Str(meta["written_path"]) is not { } path ||
                JsonUtil.Str(meta["written_hash"]) is not { } hash) continue;
            var previous = JsonUtil.Str(result["content"])?.Split('\n', 2)[0] ?? "";
            var note = $"Действие уже успешно выполнено до остановки. Файл {path} сохранил записанное содержимое. " +
                       "Переходи к оставшимся шагам. Предыдущий результат: " + previous;
            _completedWrites[WriteKey(call)] = new CompletedWrite(call.Id, path, hash, note);
        }
    }

    private CompletedWrite? CompletedWriteOf(ToolCallEvent call) =>
        _completedWrites.Count > 0 && _completedWrites.TryGetValue(WriteKey(call), out var completed) &&
        WriteTarget(call) is { } path && PathHelper.SamePath(path, completed.Path) && FileChanges.CurrentHash(path) == completed.Hash
            ? completed : null;

    private static string WriteKey(ToolCallEvent call)
    {
        JsonNode? Ordered(JsonNode? value) => value switch
        {
            JsonObject map => new JsonObject(map.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Ordered(pair.Value)))),
            JsonArray list => new JsonArray(list.Select(Ordered).ToArray()),
            _ => value?.DeepClone(),
        };
        return call.Name + " " + Ordered(JsonUtil.ParseObject(call.ArgumentsJson))?.ToJsonString(JsonUtil.Compact);
    }

    private string? WriteTarget(ToolCallEvent call)
    {
        if (call.Name is not ("write_file" or "edit_file" or "run_plugin") ||
            JsonUtil.ParseObject(call.ArgumentsJson) is not { } args) return null;
        try
        {
            var raw = call.Name == "run_plugin" ? PluginTools.TargetOf(new ToolArgs(args), tools.Context) : JsonUtil.Str(args["path"]);
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

    private static void RecordFileChange(JsonObject message, FileChange? change)
    {
        if (change == null) return;
        if (message[MetaField] is not JsonObject meta) message[MetaField] = meta = new JsonObject();
        meta["file_change"] = new JsonObject
        {
            ["path"] = change.Path, ["before"] = change.BeforeHash, ["after"] = change.AfterHash, ["backup"] = change.Backup,
        };
    }

    private static JsonObject? FileChangeMeta(JsonNode message) =>
        ((message as JsonObject)?[MetaField] as JsonObject)?["file_change"] as JsonObject;

    internal static FileChange? FileChangeOf(JsonNode message) =>
        FileChangeMeta(message) is { } info && JsonUtil.Str(info["path"]) is { } path &&
        JsonUtil.Str(info["after"]) is { } after
            ? new FileChange(path, JsonUtil.Str(info["before"]), after, JsonUtil.Str(info["backup"]))
            : null;

    internal static bool FileChangeUndone(JsonNode message) =>
        JsonUtil.Bool(FileChangeMeta(message)?["undone"]) == true;

    internal void MarkFileChangeUndone(IReadOnlyCollection<string> ids)
    {
        foreach (var result in _history.Where(node => JsonUtil.Str(node["tool_call_id"]) is { } id && ids.Contains(id)))
            if (FileChangeMeta(result) is { } info) info["undone"] = true;
        InvalidateProjectContext();
        Save();
    }
}
