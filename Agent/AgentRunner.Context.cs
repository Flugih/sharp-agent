namespace SharpAgent.Agent;

partial class AgentRunner
{
    string StoppedRequest => PromptText("[Программа: прошлое поручение остановлено. Возобновляй его только по явной просьбе пользователя.]",
        "[Application: the previous task was stopped. Resume it only if the user explicitly asks.]");

    HashSet<int> StoppedRequests(int currentUser)
    {
        var requests = new HashSet<int>();
        bool stopped = false;
        for (int i = currentUser - 1; i >= 0; i--)
        {
            var flags = _history.EntryAt(i).Flags;
            stopped |= flags.HasFlag(ChatJournal.Flags.Stopped);
            if (!flags.HasFlag(ChatJournal.Flags.User)) continue;
            if (stopped) requests.Add(i);
            stopped = false;
        }
        return requests;
    }

    bool _projectContextReady;
    string? _projectContext;
    List<Plugin>? _plugins;
    string[] _pendingLocalOperations = [];

    List<Plugin> AvailablePlugins() => _plugins ??= PluginTools.Load(tools.Context.PluginsFolder);

    internal string? PendingLocalCommand =>
        CanContinue && LastUser() is { } user && Attachment.ListFrom(user).Count == 0 && ExtractText(user["content"]) is { } task
            ? LocalActionRouter.Match(task) : null;

    JsonObject? LastUser() => _history.LastIndex(ChatJournal.Flags.User) is >= 0 and var at ? (JsonObject)_history[at] : null;

    Task RunLocalAsync(string command, CancellationToken ct) => Timed(async () =>
    {
        string text;
        if (command == "pwd") text = tools.Context.WorkingDirectory;
        else if (command == "time") text = DateTimeOffset.Now.ToString("HH:mm (zzz)", CultureInfo.InvariantCulture);
        else if (command.StartsWith("git ", StringComparison.Ordinal)) text = await LocalProjectContext.RunGitRequestAsync(tools.Context, command, ct);
        else if (command.Split(' ', 2) is [("read" or "inspect" or "research") and var kind, var value])
        {
            var args = kind switch
            {
                "inspect" => new JsonObject { ["action"] = "inspect", ["path"] = value },
                "research" => new JsonObject { ["action"] = "search", ["query"] = value },
                _ => new JsonObject { ["path"] = value },
            };
            text = (await RunLocalToolAsync(kind == "read" ? "read_file" : LocalResearchEngine.Name, args, ct)).Output;
        }
        else if (command.Split(' ', 3) is [("script" or "suite" or "knowledge") and var system, var action, .. var tail])
        {
            var name = system switch { "script" => ScriptRegistry.Name, "suite" => LocalTestTools.Name, _ => AgentKnowledge.Name };
            var result = await RunLocalToolAsync(name, new() { ["action"] = action, ["id"] = tail.FirstOrDefault() }, ct);
            text = result.Output;
            if (name == AgentKnowledge.Name && action == "apply" && result.Change is { } applied && ApplicableRecipe(applied.Path) is { } recipe)
                text += "\n\n" + (await VerifyAsync(recipe, ct)).Report;
        }
        else
        {
            var recipe = command.StartsWith("project ", StringComparison.Ordinal) ? ProjectVerification.Detect(tools.Context, testsOnly: command == "project test")
                : new ProjectVerification.Recipe(tools.Context.WorkingDirectory, [command]);
            text = recipe == null ? PromptText("Не удалось определить команды диагностики или тестов для этой папки.",
                "No diagnostic or test commands were found for this folder.") : (await VerifyAsync(recipe, ct)).Report;
        }
        await FinishLocalAsync(command, text);
    });

    async Task FinishLocalAsync(string command, string text)
    {
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
    }

    async Task<ToolOutcome> RunLocalToolAsync(string name, JsonObject args, CancellationToken ct)
    {
        var call = new ToolCallEvent("call_" + Guid.NewGuid().ToString("N"), name, args.ToJsonString());
        ToolCallStarted?.Invoke(call);
        await CheckpointAsync([name]);
        var result = await ExecuteCallAsync(call, ct);
        ToolCallFinished?.Invoke(new(call.Id, name, result.Output, result.IsError, result.IsWarning) { Change = result.Change });
        if (result.Change == null && name is not ("run_command" or LocalTestTools.Name))
        {
            await CheckpointAsync([]);
            return result;
        }
        _history.Add(new JsonObject
        {
            ["role"] = "assistant",
            ["tool_calls"] = new JsonArray(new JsonObject { ["type"] = "function", ["id"] = call.Id,
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = call.ArgumentsJson } }),
        });
        var tool = ToolMessage(call.Id, result.Output, result.Diff);
        if (name == "run_command" && VerificationCommand(call)) EnsureMeta(tool)["verification"] = true;
        if (result.Restriction != null) EnsureMeta(tool)["command_restricted"] = true;
        RecordChange(call, tool, result.Change);
        _history.Add(tool);
        InvalidateReads(call, result.Change?.Path);
        await SaveAsync();
        await CheckpointAsync([]);
        return result;
    }

    async Task<ProjectVerification.Result> VerifyAsync(ProjectVerification.Recipe recipe, CancellationToken ct)
    {
        if (tools.Context.SandboxRoot != null) return new(PromptText("Локальные команды отключены в песочнице.", "Local commands are disabled in the sandbox."), false, [], true);
        var reports = new List<string>();
        List<ProjectVerification.Diagnostic> diagnostics = [];
        bool success = false;
        bool blocked = false;
        var started = DateTime.UtcNow;
        foreach (var step in recipe.Commands)
        {
            var result = await RunLocalToolAsync("run_command",
                new() { ["command"] = step, ["working_directory"] = recipe.Directory, ["timeout_seconds"] = 120 }, ct);
            var parsed = ProjectVerification.Diagnostics(tools.Context, result.Output);
            diagnostics.AddRange(parsed);
            var report = result.Restriction is { } restriction ? CommandRestriction.Prefix + restriction.Message :
                CommandLog.ResultOf(result.Output) == null ? result.Output : ProjectVerification.Report(tools.Context, result.Output, parsed);
            reports.Add("**" + step + "**\n\n" + report);
            success = !result.IsError && !result.IsWarning;
            blocked = result.Restriction != null || result.Output.StartsWith("Пользователь отклонил", StringComparison.Ordinal);
            if (success && ProjectVerification.Tests(step) && !ProjectVerification.TestsExecuted(ProjectVerification.Transcript(tools.Context, result.Output)))
            {
                reports.Add(PromptText("Проверка не подтверждена: команда завершилась, но отчёта о выполненных тестах нет.",
                    "Verification is unconfirmed: the command exited without reporting executed tests."));
                if (recipe.Commands.All(ProjectVerification.Tests)) success = false;
            }
            if (!success) break;
        }
        var changes = CurrentActions().Select(entry => (Change: FileChangeOf(entry.Result), Diff: DiffOf(entry.Result))).ToArray();
        var edits = changes
            .Where(entry => entry.Change != null && entry.Diff != null && !entry.Diff.Unified.Contains(TextDiff.Truncated, StringComparison.Ordinal))
            .Select(entry => new ChangedCoverage.Edit(entry.Change!.Path, entry.Diff!)).ToArray();
        if (edits.Length > 0)
        {
            var coverage = await Task.Run(() => ChangedCoverage.Latest(tools.Context, recipe.Directory, started, edits), ct);
            if (coverage != null) reports.Add(coverage);
            else if (reports.Any(report => report.StartsWith("**dotnet test", StringComparison.OrdinalIgnoreCase)))
                reports.Add(PromptText("Для изменённых методов нет данных из свежего отчёта Cobertura.",
                    "No recent Cobertura results were found for the changed methods."));
        }
        if (success && !IsTemporary && !delegated)
        {
            var verification = string.Join(", ", recipe.Commands);
            var facts = changes.Select(entry => entry.Change).OfType<FileChange>().Reverse()
                .Where(change => PathHelper.IsInside(change.Path, recipe.Directory))
                .DistinctBy(change => change.Path, StringComparer.OrdinalIgnoreCase).Take(20)
                .Select(change => new TaskMemory.Fact(change.Path, change.AfterHash, DateTimeOffset.UtcNow, verification)).ToArray();
            if (facts.Length > 0) await Task.Run(() => TaskMemory.Remember(tools.Context, facts));
        }
        return new(string.Join("\n\n", reports), success, [.. diagnostics.Distinct()], blocked);
    }

    Task CheckpointAsync(string[] pending, bool remember = false)
    {
        _pendingLocalOperations = pending;
        if (IsTemporary || delegated || ChatPath is not { } chat) return Task.CompletedTask;
        var changes = CurrentActions().Select(entry => FileChangeOf(entry.Result)).OfType<FileChange>()
            .Where(change => PathHelper.IsInside(change.Path, tools.Context.WorkingDirectory)).ToArray();
        var checkpoint = new TaskCheckpoint(Str(MetaOf(LastUser())?[SentAtField]), _history.Count, PlanNote(),
            changes.Select(change => change.Path).Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(20).ToArray(), pending);
        return Task.Run(() =>
        {
            TaskMemory.Save(tools.Context, chat, checkpoint);
            if (remember)
                TaskMemory.Remember(tools.Context, changes.TakeLast(20)
                    .Select(change => new TaskMemory.Fact(change.Path, change.AfterHash, DateTimeOffset.UtcNow)).ToArray());
        });
    }

    static string? PluginName(ToolCallEvent call) => Str(ArgsOf(call)?["name"])?.Trim();

    IEnumerable<(ToolCallEvent Call, JsonObject Result, int At)> CurrentActions()
    {
        var calls = new Dictionary<string, ToolCallEvent>();
        int start = _history.LastIndex(ChatJournal.Flags.User) + 1, at = start;
        foreach (var message in _history.Range(start, _history.Count - start).OfType<JsonObject>())
        {
            foreach (var call in (message["tool_calls"] as JsonArray)?.OfType<JsonObject>().Select(ParseCall) ?? []) calls[call.Id] = call;
            if (Str(message["tool_call_id"]) is { } id && calls.TryGetValue(id, out var match)) yield return (match, message, at);
            at++;
        }
    }

    void RestorePlugins()
    {
        foreach (var (call, result, _) in CurrentActions())
        {
            if (call.Name != "use_plugin" || PluginName(call) is not { } name || Str(result["content"]) is not { } output ||
                !output.StartsWith("Плагин ", StringComparison.Ordinal)) continue;
            _activePlugins[name] = output = output.Replace(RepeatReply, "");
            if (ReadKeyOf(call) is { } key) _reads.TryAdd(key, (call.Id, new ToolOutcome(output, false)));
        }
    }

    string ResumeContext()
    {
        var checkpoint = TaskMemory.Load(tools.Context, ChatPath, Str(MetaOf(LastUser())?[SentAtField]), _history.Count);
        return PromptText(
        "Пользователь возобновил прерванную задачу. Продолжи текущий запрос с сохранённого места. " +
        "Успешные изменения уже внесены. Выполняй оставшиеся действия, а после отменённой команды сначала " +
        "проверь её результат на диске: она могла успеть внести изменения.\n",
        "The user resumed an interrupted task. Continue from where it stopped. Successful changes are already in place. " +
        "Complete the remaining work. If a command was cancelled, check its result on disk first because it may have made changes.\n") +
        (checkpoint is { ChangedFiles.Length: > 0 } ? "\nИзменённые файлы:\n" + string.Join("\n", checkpoint.ChangedFiles) + "\n" : "") +
        (checkpoint?.Plan is { Length: > 0 } plan ? "\n" + plan + "\n" : "") +
        (checkpoint?.Pending is { Length: > 0 } pending ? PromptText(
            "\nНеподтверждённый итог операций: ", "\nOperations with unconfirmed outcomes: ") + string.Join(", ", pending) + "\n" : "") +
        string.Join("\n", CurrentActions().Where(entry => entry.Call.Name != "use_plugin").TakeLast(18).Select(entry =>
        {
            var output = Str(entry.Result["content"]) ?? "";
            return "- " + HistoryCompressor.Describe(entry.Call.Name, entry.Call.ArgumentsJson) + " — " +
                   (entry.Call.Name is "run_command" or "run_plugin" ? CommandLog.Brief(output) : Clip(output.Split('\n', 2)[0], 230));
        }));
    }

    string? RequestTail(bool harder)
    {
        var notes = new List<string>();
        if (_resumeContext != null) notes.Add(Clip(_resumeContext, harder ? 1800 : 4200));
        foreach (var restriction in _blockedShells.Values) notes.Add(CommandRestriction.Prefix + restriction.Message);
        if (!_blockedShells.ContainsKey(CommandShell.PowerShell))
            foreach (var (name, instruction) in _activePlugins.TakeLast(harder ? 1 : 2))
            {
                var scripts = Regex.Matches(instruction, @"[\w.\/-]+\.ps1", RegexOptions.IgnoreCase)
                    .Select(m => Path.GetFileName(m.Value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(6);
                notes.Add(PromptText($"Активный плагин «{name}». Инструкция уже получена через use_plugin. ",
                    $"Active plugin: {name}. Instructions were loaded with use_plugin. ") +
                    PromptText("При необходимости вызови use_plugin ещё раз; доступные сценарии: ",
                    "Reload use_plugin if its instructions are missing; scripts: ") + string.Join(", ", scripts));
            }
        return notes.Count > 0 ? PromptText("\n\n[Сведения программы о текущем шаге]\n", "\n\n[Application context for this step]\n") + string.Join("\n\n", notes) : null;
    }

    static void AddContext(List<JsonNode> list, int at, string? context)
    {
        if (at < 0 || context == null) return;
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
            PreparingContext?.Invoke(PromptText("Ищу контекст в проекте…", "Finding project context…"));
            _projectContext = await Task.Run(async () =>
            {
                string? known = AgentKnowledge.Relevant(tools.Context, task);
                string? project = await LocalProjectContext.CollectAsync(tools.Context, task, attachments, ct);
                return string.Join("\n\n", new[] { Str(MetaOf(user)?["diagnostic_route"]), known, TaskMemory.Relevant(tools.Context, task), project }.OfType<string>());
            }, ct);
            _projectContextReady = true;
        }
        return _projectContext is { } context ? Clip(context, limit) : null;
    }

    void InvalidateProjectContext() => (_projectContextReady, _projectContext) = (false, null);

    static bool VerificationCommand(ToolCallEvent call) =>
        Str(ArgsOf(call)?["command"]) is { } command && ProjectVerification.Command(command);

    JsonNode WithVerificationSummary(JsonNode node)
    {
        if (MetaOf(node) is not { } meta) return node;
        string? summary = null;
        if (Str(meta["local_command"]) is { } local)
            summary = local.StartsWith("git ", StringComparison.Ordinal)
                ? PromptText($"Пользователю показан локальный вывод команды `{local}`.", $"The user was shown the local output of `{local}`.")
                : PromptText($"Локально выполнена команда `{local}`. Сводка вывода:\n", $"Command `{local}` ran locally. Output summary:\n") + Clip(ExtractText(node["content"]) ?? "", 1900);
        else if (Bool(meta["verification"]) == true && Bool(meta["command_restricted"]) != true &&
                 Str(node["content"]) is { } content && CommandLog.IdOf(content) != null)
            summary = CommandLog.Brief(content) + (CommandLog.Details(content, 2600) is { Length: > 0 } details ? "\n" + details : "") +
                      PromptText("\nПолный доступный вывод открывается через read_log.", "\nUse read_log to access the full available output.");
        if (summary == null) return node;
        var copy = (JsonObject)node.DeepClone();
        copy["content"] = summary;
        return copy;
    }

}
