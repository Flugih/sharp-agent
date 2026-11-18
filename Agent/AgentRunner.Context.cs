namespace SharpAgent.Agent;

partial class AgentRunner
{
    const string StoppedRequest = "[Программа: прошлое поручение остановлено. Возобновляй его только по явной просьбе пользователя.]";

    HashSet<int> StoppedRequests(int currentUser)
    {
        var requests = new HashSet<int>();
        bool stopped = false;
        for (int i = currentUser - 1; i >= 0; i--)
        {
            var message = _history[i];
            stopped |= MetaOf(message) is { } meta && meta[StopField] != null && Bool(meta[ProgramStopField]) != true;
            if (Role(message) != "user") continue;
            if (stopped) requests.Add(i);
            stopped = false;
        }
        return requests;
    }

    bool _projectContextReady;
    string? _projectContext, _changedSource;
    List<Plugin>? _plugins;

    List<Plugin> AvailablePlugins() => _plugins ??= PluginTools.Load(tools.Context.PluginsFolder);

    internal static string? LocalCommand(string text) =>
        text.Trim().ToLowerInvariant() is ("git status" or "git diff" or "git diff --staged" or "dotnet build" or "dotnet test") and var command
            ? command : null;

    internal string? PendingLocalCommand =>
        CanContinue && LastUser() is { } user && Attachment.ListFrom(user).Count == 0 && ExtractText(user["content"]) is { } task
            ? LocalCommand(task) : null;

    JsonObject? LastUser() => _history.OfType<JsonObject>().LastOrDefault(m => Role(m) == "user");

    Task RunLocalAsync(string command, CancellationToken ct) => Timed(async () =>
    {
        string text;
        if (command.StartsWith("git ", StringComparison.Ordinal)) text = await LocalProjectContext.RunGitRequestAsync(tools.Context, command, ct);
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
        var sentAt = DateTimeOffset.UtcNow;
        text = text.Trim();
        _history.Add(new JsonObject
        {
            ["role"] = "assistant", ["content"] = text,
            [MetaField] = new JsonObject { [ElapsedField] = (long)elapsed.TotalMilliseconds, ["local_command"] = command, [SentAtField] = FormatSentAt(sentAt) },
        });
        await SaveAsync();
        AssistantText?.Invoke(text, elapsed, sentAt);
    });

    static string? PluginName(ToolCallEvent call) => Str(ArgsOf(call)?["name"])?.Trim();

    IEnumerable<(ToolCallEvent Call, JsonObject Result)> CurrentActions()
    {
        var calls = new Dictionary<string, ToolCallEvent>();
        foreach (var message in _history.OfType<JsonObject>().Reverse().TakeWhile(m => Role(m) != "user").Reverse())
        {
            foreach (var call in (message["tool_calls"] as JsonArray)?.OfType<JsonObject>().Select(ParseCall) ?? []) calls[call.Id] = call;
            if (Str(message["tool_call_id"]) is { } id && calls.TryGetValue(id, out var match)) yield return (match, message);
        }
    }

    void RestorePlugins()
    {
        foreach (var (call, result) in CurrentActions())
        {
            if (call.Name != "use_plugin" || PluginName(call) is not { } name || Str(result["content"]) is not { } output ||
                !output.StartsWith("Плагин ", StringComparison.Ordinal)) continue;
            _activePlugins[name] = output = output.Replace(RepeatReply, "");
            if (ReadKeyOf(call) is { } key) _reads.TryAdd(key, (call.Id, new ToolOutcome(output, false)));
        }
    }

    string ResumeContext() =>
        "Пользователь возобновил прерванную задачу. Продолжи текущий запрос с сохранённого места. " +
        "Успешные изменения уже внесены. Выполняй оставшиеся действия, а после отменённой команды сначала " +
        "проверь её результат на диске: она могла успеть внести изменения.\n" +
        string.Join("\n", CurrentActions().Where(entry => entry.Call.Name != "use_plugin").TakeLast(18).Select(entry =>
        {
            var output = Str(entry.Result["content"]) ?? "";
            return "- " + HistoryCompressor.Describe(entry.Call.Name, entry.Call.ArgumentsJson) + " — " +
                   (entry.Call.Name is "run_command" or "run_plugin" ? CommandLog.Brief(output) : Clip(output.Split('\n', 2)[0], 230));
        }));

    string? RequestTail(bool harder)
    {
        var notes = new List<string>();
        if (_resumeContext != null) notes.Add(Clip(_resumeContext, harder ? 1800 : 4200));
        foreach (var restriction in _blockedShells.Values) notes.Add(CommandRestriction.Prefix + restriction.Message);
        if (!_blockedShells.ContainsKey(CommandShell.PowerShell))
            foreach (var (name, instruction) in _activePlugins.TakeLast(harder ? 1 : 3))
                notes.Add($"Активный плагин «{name}». Инструкция уже загружена. Используй указанный ниже скрипт " +
                          "через run_plugin, когда действие ещё не выполнено.\n" + Clip(instruction, harder ? 4000 : 8500));
        return notes.Count > 0 ? "\n\n[Сведения программы о текущем шаге]\n" + string.Join("\n\n", notes) : null;
    }

    static void AddContext(List<JsonNode> list, string? context, params string[] roles)
    {
        int at = context == null ? -1 : list.FindLastIndex(node => roles.Contains(Role(node)));
        if (at < 0) return;
        var copy = (JsonObject)list[at].DeepClone();
        if (copy["content"] is JsonArray parts) parts.Add(new JsonObject { ["type"] = "text", ["text"] = context });
        else copy["content"] = (ExtractText(copy["content"]) ?? "") + context;
        list[at] = copy;
    }

    async Task<string?> ProjectContextAsync(int limit, CancellationToken ct)
    {
        if (limit < 300) return null;
        if (!_projectContextReady && !delegated && LastUser() is { } user)
        {
            var task = ExtractText(user["content"]) ?? "";
            var attachments = Attachment.ListFrom(user);
            _projectContext = await Task.Run(() => LocalProjectContext.CollectAsync(tools.Context, task, attachments, _changedSource, ct), ct);
            _projectContextReady = true;
        }
        return _projectContext is { } context ? Clip(context, limit) : null;
    }

    void InvalidateProjectContext(ToolCallEvent? call = null)
    {
        _projectContextReady = false;
        _changedSource = null;
        if (call == null || Str(ArgsOf(call)?["path"]) is not { } path || !ProjectSourceIndex.IsSource(path)) return;
        try { _changedSource = tools.Context.Resolve(path); }
        catch (ToolException) { }
    }

    static bool VerificationCommand(ToolCallEvent call) =>
        Str(ArgsOf(call)?["command"]) is { } command && Verification().IsMatch(command);

    static JsonNode WithVerificationSummary(JsonNode node)
    {
        if (MetaOf(node) is not { } meta) return node;
        string? summary = null;
        if (Str(meta["local_command"]) is { } local)
            summary = local.StartsWith("git ", StringComparison.Ordinal)
                ? $"Пользователю показан локальный вывод команды `{local}`."
                : $"Локально выполнена команда `{local}`. Сводка вывода:\n" + Clip(ExtractText(node["content"]) ?? "", 1900);
        else if (Bool(meta["verification"]) == true && Bool(meta["command_restricted"]) != true &&
                 Str(node["content"]) is { } content && CommandLog.IdOf(content) != null)
            summary = CommandLog.Brief(content) + (CommandLog.Details(content, 2600) is { Length: > 0 } details ? "\n" + details : "") +
                      "\nПолный доступный вывод открывается через read_log.";
        if (summary == null) return node;
        var copy = (JsonObject)node.DeepClone();
        copy["content"] = summary;
        return copy;
    }

    [GeneratedRegex(@"^\s*(?:(?:dotnet\s+(?:build|test)|(?:npm|pnpm|yarn)\s+(?:run\s+)?(?:build|test)|(?:python\s+-m\s+)?pytest|cargo\s+(?:build|test)|go\s+test)\b)[^\r\n;|&]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Verification();
}
