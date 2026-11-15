using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpAgent.Core;
using SharpAgent.Tools;

namespace SharpAgent.Agent;

public sealed partial class AgentRunner
{
    private bool _projectContextReady;
    private string? _projectContext;
    private string? _changedSource;
    private List<Plugin>? _plugins;

    private List<Plugin> AvailablePlugins() => _plugins ??= PluginTools.Load(tools.Context.PluginsFolder);

    internal static string? LocalCommand(string text)
    {
        var command = text.Trim().ToLowerInvariant();
        return command is "git status" or "git diff" or "git diff --staged" or "dotnet build" or "dotnet test" ? command : null;
    }

    internal string? PendingLocalCommand =>
        CanContinue && _history.OfType<JsonObject>().LastOrDefault(m => JsonUtil.Str(m["role"]) == "user") is { } user &&
        Attachment.ListFrom(user).Count == 0 && ExtractText(user["content"]) is { } task ? LocalCommand(task) : null;

    private async Task RunLocalAsync(string command, CancellationToken ct)
    {
        _turnClock.Start();
        try
        {
            string text;
            if (command.StartsWith("git ", StringComparison.Ordinal))
            {
                text = await LocalProjectContext.RunGitRequestAsync(tools.Context, command, ct);
            }
            else
            {
                var call = new ToolCallEvent("call_" + Guid.NewGuid().ToString("N"), "run_command",
                    new JsonObject { ["command"] = command, ["timeout_seconds"] = 300 }.ToJsonString());
                ToolCallStarted?.Invoke(call);
                var result = await ExecuteCallAsync(call, ct);
                ToolCallFinished?.Invoke(new(call.Id, call.Name, result.Output, result.IsError, result.IsWarning));
                text = result.Restriction is { } restriction ? CommandRestriction.Prefix + restriction.Message :
                    CommandLog.ResultOf(result.Output) == null ? result.Output :
                    CommandLog.Brief(result.Output) + "\n\n" + CommandLog.Details(result.Output, 2200);
            }
            var elapsed = _turnClock.Elapsed;
            _history.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = text.Trim(),
                [MetaField] = new JsonObject { [ElapsedField] = (long)elapsed.TotalMilliseconds, ["local_command"] = command },
            });
            Save();
            AssistantText?.Invoke(text.Trim(), elapsed);
        }
        finally
        {
            _turnClock.Stop();
        }
    }

    private static string? PluginName(ToolCallEvent call) =>
        JsonUtil.Str(JsonUtil.ParseObject(call.ArgumentsJson)?["name"])?.Trim();

    private IEnumerable<(ToolCallEvent Call, JsonObject Result)> CurrentActions()
    {
        var messages = _history.OfType<JsonObject>().Reverse().TakeWhile(m => JsonUtil.Str(m["role"]) != "user").Reverse();
        var calls = new Dictionary<string, ToolCallEvent>();
        foreach (var message in messages)
        {
            if (message["tool_calls"] is JsonArray batch)
                foreach (var node in batch.OfType<JsonObject>())
                {
                    var call = ParseCall(node);
                    calls[call.Id] = call;
                }
            if (JsonUtil.Str(message["tool_call_id"]) is { } id && calls.TryGetValue(id, out var match))
                yield return (match, message);
        }
    }

    private void RestorePlugins()
    {
        foreach (var (call, result) in CurrentActions())
        {
            if (call.Name != "use_plugin" || PluginName(call) is not { } name ||
                JsonUtil.Str(result["content"]) is not { } output || !output.StartsWith("Плагин ", StringComparison.Ordinal))
                continue;
            output = output.Replace(RepeatReply, "");
            _activePlugins[name] = output;
            if (ReadKeyOf(call) is { } key)
                _reads.TryAdd(key, (call.Id, new ToolOutcome(output, false)));
        }
    }

    private string ResumeContext()
    {
        var steps = CurrentActions().Where(entry => entry.Call.Name != "use_plugin").TakeLast(18).Select(entry =>
        {
            var output = JsonUtil.Str(entry.Result["content"]) ?? "";
            var subject = HistoryCompressor.Describe(entry.Call.Name, entry.Call.ArgumentsJson);
            if (entry.Call.Name is "run_command" or "run_plugin")
                return "- " + subject + " — " + CommandLog.Brief(output);
            var firstLine = output.Split('\n', 2)[0];
            return "- " + subject + " — " + ClipContext(firstLine, 230);
        });
        return "Пользователь возобновил прерванную задачу. Продолжи текущий запрос с сохранённого места. " +
               "Успешные изменения уже внесены. Выполняй оставшиеся действия, а после отменённой команды сначала " +
               "проверь её результат на диске: она могла успеть внести изменения.\n" + string.Join("\n", steps);
    }

    private string? RequestTail(bool harder)
    {
        var notes = new List<string>();
        if (_resumeContext != null) notes.Add(ClipContext(_resumeContext, harder ? 1800 : 4200));
        foreach (var restriction in _blockedShells.Values)
            notes.Add(CommandRestriction.Prefix + restriction.Message);
        if (!_blockedShells.ContainsKey(CommandShell.PowerShell))
            foreach (var (name, instruction) in _activePlugins.TakeLast(harder ? 1 : 3))
                notes.Add($"Активный плагин «{name}». Инструкция уже загружена. Используй указанный ниже скрипт " +
                          "через run_plugin, когда действие ещё не выполнено.\n" + ClipContext(instruction, harder ? 4000 : 8500));
        return notes.Count > 0 ? "\n\n[Сведения программы о текущем шаге]\n" + string.Join("\n\n", notes) : null;
    }

    private static JsonObject WithContext(JsonObject message, string context)
    {
        var copy = (JsonObject)message.DeepClone();
        if (copy["content"] is JsonArray parts)
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = context });
        else
            copy["content"] = (ExtractText(copy["content"]) ?? "") + context;
        return copy;
    }

    private static string ClipContext(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private async Task<string?> ProjectContextAsync(int limit, CancellationToken ct)
    {
        if (limit < 300) return null;
        if (!_projectContextReady && !delegated && _history.OfType<JsonObject>()
            .LastOrDefault(m => JsonUtil.Str(m["role"]) == "user") is { } user)
        {
            var task = ExtractText(user["content"]) ?? "";
            var attachments = Attachment.ListFrom(user);
            _projectContext = await Task.Run(() => LocalProjectContext.CollectAsync(tools.Context, task, attachments, _changedSource, ct), ct);
            _projectContextReady = true;
        }
        return _projectContext != null ? ClipContext(_projectContext, limit) : null;
    }

    private void InvalidateProjectContext(ToolCallEvent? call = null)
    {
        _projectContextReady = false;
        _changedSource = null;
        if (call == null || JsonUtil.Str(JsonUtil.ParseObject(call.ArgumentsJson)?["path"]) is not { } path ||
            !ProjectSourceIndex.IsSource(path)) return;
        try { _changedSource = tools.Context.Resolve(path); }
        catch (ToolException) { }
    }

    private static bool VerificationCommand(ToolCallEvent call) =>
        JsonUtil.Str(JsonUtil.ParseObject(call.ArgumentsJson)?["command"]) is { } command && Verification().IsMatch(command);

    private static JsonNode WithVerificationSummary(JsonNode node)
    {
        if (node is not JsonObject message || message[MetaField] is not JsonObject meta) return node;
        if (JsonUtil.Str(meta["local_command"]) is { } local)
        {
            var summary = (JsonObject)message.DeepClone();
            summary["content"] = local.StartsWith("git ", StringComparison.Ordinal)
                ? $"Пользователю показан локальный вывод команды `{local}`."
                : $"Локально выполнена команда `{local}`. Сводка вывода:\n" +
                  ClipContext(ExtractText(message["content"]) ?? "", 1900);
            return summary;
        }
        if (JsonUtil.Bool(meta["verification"]) != true || JsonUtil.Bool(meta["command_restricted"]) == true ||
            JsonUtil.Str(message["content"]) is not { } content || CommandLog.IdOf(content) == null) return node;
        var diagnostics = CommandLog.Details(content, 2600);
        var copy = (JsonObject)message.DeepClone();
        copy["content"] = CommandLog.Brief(content) +
            (diagnostics.Length == 0 ? "" : "\n" + diagnostics) +
            "\nПолный доступный вывод открывается через read_log.";
        return copy;
    }

    [GeneratedRegex(@"^\s*(?:(?:dotnet\s+(?:build|test)|(?:npm|pnpm|yarn)\s+(?:run\s+)?(?:build|test)|(?:python\s+-m\s+)?pytest|cargo\s+(?:build|test)|go\s+test)\b)[^\r\n;|&]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Verification();
}
