namespace SharpAgent.Agent;

partial class AgentRunner
{
    const string ReviewField = "review", AlreadySent = "Полный результат этого действия уже передан.";
    // Standalone scripts are not pushed toward a check: the only check is to run them, and that may change data.
    static readonly string[] Scripts = [".ps1", ".sql", ".py"];

    static readonly JsonNode PlanDefinition = JsonNode.Parse("""
        {"type":"function","function":{"name":"update_plan","description":"Записать или обновить план многошаговой задачи: весь список пунктов с их состоянием. Держи план актуальным: отмечай выполненное и добавляй найденную работу. Программа напомнит о невыполненных пунктах перед завершением.","parameters":{"type":"object","properties":{"items":{"type":"array","minItems":1,"maxItems":20,"items":{"type":"object","properties":{"step":{"type":"string"},"status":{"type":"string","enum":["pending","in_progress","done"]}},"required":["step","status"]}}},"required":["items"]}}}
        """)!;

    internal static string? ReviewOf(JsonNode? message) => Str(MetaOf(message)?[ReviewField]);

    static ToolOutcome UpdatePlan(JsonObject args)
    {
        var items = PlanItems(args);
        return items.Count == 0 ? new("Ошибка: укажи пункты плана со статусами pending, in_progress или done.", true)
            : new($"План обновлён: выполнено {items.Count(item => item.Done)} из {items.Count}.", false);
    }

    internal readonly record struct PlanItem(string Step, string? Status)
    {
        public bool Done => Status == "done";
    }

    internal static List<PlanItem> PlanItems(JsonObject? args) =>
        (args?["items"] as JsonArray)?.OfType<JsonObject>().Select(item => new PlanItem(TextArg(item["step"]) ?? "", Str(item["status"])))
        .Where(item => item.Step.Length > 0).Take(20).ToList() ?? [];

    (List<PlanItem> Items, int At)? CurrentPlan()
    {
        for (int i = _history.Count - 1; i >= 0 && Role(_history[i]) != "user"; i--)
            if ((_history[i]["tool_calls"] as JsonArray)?.OfType<JsonObject>().Select(ParseCall).LastOrDefault(call => call.Name == "update_plan") is { } plan)
                return (PlanItems(ArgsOf(plan)), i);
        return null;
    }

    string? PlanNote() => CurrentPlan() is { Items.Count: > 0 } plan
        ? PromptText("Текущий план:\n", "Current plan:\n") + string.Join("\n", plan.Items.Select(item => (item.Done ? "[x] " : "[ ] ") + item.Step)) : null;

    // A local check before the model finishes: one cheap extra step only when work is visibly unfinished or unverified.
    async Task<(string? Review, string? Verification, string? Incomplete)> ReviewAsync(ContextRun context, CancellationToken ct)
    {
        int start = _history.LastIndex(ChatJournal.Flags.User);
        if (delegated || start < 0) return (null, null, null);
        if (tools.PendingCommandOutput != null)
            return ContinueReview(PromptText("Команда ещё выполняется. Получи её итог по session_id.",
                "A command is still running. Poll its session_id before completing the task."), null, _history.LastIndex(ChatJournal.Flags.Tool));
        int lastChange = -1, lastCheck = -1;
        var changed = new List<string>();
        var failed = new Dictionary<string, ToolCallEvent>();
        var actions = CurrentActions().ToArray();
        var checks = actions.Where(action => action.Call.Name == LocalContractEngine.Name).SelectMany(action =>
        {
            try { return ArgsOf(action.Call) is { } arguments ? LocalContractEngine.Parse(new(arguments)) : []; }
            catch (ToolException) { return []; }
        }).Distinct().Take(48).ToArray();
        foreach (var (call, result, at) in actions)
        {
            var sourceChanges = ChangesOf(result).Select(change => change.Path)
                .Concat([Str(MetaOf(result)?["written_path"])]).OfType<string>()
                .Where(path => ProjectSourceIndex.IsSource(path) && !Scripts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
            if (sourceChanges.Length > 0)
            {
                lastChange = at;
                foreach (var path in sourceChanges)
                    if (!changed.Contains(path, StringComparer.OrdinalIgnoreCase)) changed.Add(path);
            }
            else if (call.Name == "exec_command" && VerificationCommand(call) || ComputerTools.IsControl(call.Name) && Str(ArgsOf(call)?["action"]) == "launch")
            {
                lastCheck = at;
                var key = call.Name == "exec_command" ? Str(ArgsOf(call)?["command"])!.Trim() : call.ArgumentsJson;
                var output = Str(result["content"]) ?? "";
                if (ToolRegistry.IsErrorOutput(output) || ToolRegistry.CommandFailed(output)) failed[key] = call;
                else foreach (var covered in failed.Keys.Where(earlier => Covers(key, earlier)).ToList()) failed.Remove(covered);
            }
        }
        var notes = new List<string>();
        string? verification = null;
        if (WorkMode && CurrentPlan() is { } plan && plan.Items.Where(item => !item.Done).Select(item => item.Step).ToList() is { Count: > 0 } open)
            notes.Add(PromptText("В плане остались невыполненные пункты: ", "The plan has unfinished steps: ") + string.Join("; ", open) +
                      PromptText(". Выполни их или обнови план, если они не нужны.", ". Complete them or update the plan if they are unnecessary."));
        var task = ExtractText(LastUser()?["content"]) ?? "";
        bool automatic = WorkMode && tools.Context.SandboxRoot == null &&
            !Regex.IsMatch(task, @"(?i)(?:\bбез (?:сборки|тестов|проверок)|\bне (?:запускай|выполняй|проверяй|собирай)|\bdo not (?:run|build|test)|\bno tests)\b");
        if (WorkMode && Regex.IsMatch(task, @"(?i)(?:запусти|выполни|прогони)\s+(?:(?:unit|авто|модульные)\s*)?тест[ыа]|\brun\s+(?:the\s+)?(?:unit\s+)?tests\b") &&
            !checks.Any(check => check.Kind == "unit_tests")) checks = [.. checks, new("unit_tests", "", "", "", false)];
        bool missingTest = !actions.Any(action => action.At > lastChange && action.Call.Name == "exec_command" &&
            Str(ArgsOf(action.Call)?["command"]) is { } command && ProjectVerification.Tests(command));
        if (automatic && lastChange >= 0 && (lastCheck < lastChange || missingTest))
        {
            if (lastChange > context.LastVerifiedChange &&
                changed.LastOrDefault() is { } changedPath && ApplicableRecipe(changedPath, testsOnly: lastCheck > lastChange) is { } recipe)
            {
                context.LastVerifiedChange = lastChange;
                var outcome = await VerifyAsync(recipe, ct);
                verification = Clip(outcome.Report, 3000);
                if (!outcome.Success)
                    notes.Add(PromptText("Локальная проверка завершилась без успеха. Учти результат и исправь причину, если она относится к задаче:\n",
                        "Local verification did not pass. Consider the result and fix causes relevant to the task:\n") + verification);
            }
            else if (context.LastVerifiedChange < lastChange)
                notes.Add(PromptText("После последних изменений кода не было проверки: ", "The latest code changes were not verified: ") +
                    string.Join(", ", changed.TakeLast(6).Select(Path.GetFileName)) +
                    PromptText(". Проверь изменения или укажи причину.", ". Verify the changes or explain why verification was unavailable."));
        }
        else if (WorkMode && lastCheck > lastChange && failed.Count > 0)
            notes.Add(PromptText("Остались ошибки проверки: ", "Verification failures remain: ") +
                      string.Join(", ", failed.Values.TakeLast(4).Select(call => HistoryCompressor.Describe(call.Name, call.ArgumentsJson))) +
                      PromptText(". Исправь причину и проверь снова либо объясни, что помешало.", ". Fix the cause and check again, or explain what blocked verification."));
        string? incomplete = null;
        if (checks.Length > 0)
        {
            var latest = CurrentActions().ToArray();
            if (automatic && context.LastContractVerification != lastChange &&
                checks.Any(check => check.Kind is "build" or "unit_tests" && ContractEvidence(check, latest) == null) &&
                ApplicableRecipe(changed.LastOrDefault(), testsOnly: checks.All(check => check.Kind != "build")) is { } project)
            {
                context.LastContractVerification = lastChange;
                var outcome = await VerifyAsync(project, ct);
                verification = Clip(outcome.Report, 3000);
                latest = CurrentActions().ToArray();
            }
            foreach (var check in checks.Where(check => check.Kind == "ui_tests" && !check.Optional && check.Key.Length > 0).Take(3))
                if (automatic && ContractEvidence(check, latest) == null)
                {
                    await RunLocalToolAsync(LocalTestTools.Name, new() { ["action"] = "run", ["id"] = check.Key }, ct);
                    latest = CurrentActions().ToArray();
                }
            var results = await Task.Run(() => LocalContractEngine.Assess(checks, tools.Context, ct,
                check => ContractEvidence(check, latest)), ct);
            var report = LocalContractEngine.Report(results);
            verification = verification == null ? report : verification + "\n\n" + report;
            if (results.Any(LocalContractEngine.Unmet))
            {
                if (results.Where(LocalContractEngine.Unmet).All(result => result.State == LocalContractEngine.State.Blocked))
                    incomplete = PromptText("Выполнение задачи не подтверждено.\n\n", "Task completion could not be verified.\n\n") + report;
                else notes.Add(report);
            }
        }
        if (incomplete != null) return (null, verification, incomplete);
        return notes.Count == 0 ? (null, verification, null) : ContinueReview(string.Join("\n", notes), verification, lastChange, checks.Length > 0);

        (string? Review, string? Verification, string? Incomplete) ContinueReview(string notes, string? verification, int revision, bool contract = false)
        {
            if (context.LastReview == (revision, notes))
                return (null, verification, (contract
                    ? PromptText("Выполнение задачи не подтверждено.\n\n", "Task completion could not be verified.\n\n")
                    : PromptText("Задача не завершена.\n\n", "The task is incomplete.\n\n")) + notes);
            context.LastReview = (revision, notes);
            return (PromptText("[Программа: проверка перед завершением] ", "[Application: pre-completion check] ") + notes +
                PromptText("\nСверь результат с запросом пользователя. Если всё уже сделано и проверено, повтори итоговый ответ полностью: пользователь увидит только его.",
                    "\nCompare the result with the user's request. If everything is complete and verified, repeat the final answer in full. The user sees only that version."), verification, null);
        }
    }

    LocalContractEngine.Result? ContractEvidence(LocalContractEngine.Check check,
        (ToolCallEvent Call, JsonObject Result, int At)[] actions)
    {
        var path = check.Path.Length == 0 ? null : tools.Context.Resolve(check.Path);
        if (path != null) tools.Context.GuardSecrets(path);
        int changed = actions.Where(action => ChangesOf(action.Result).Any() || Str(MetaOf(action.Result)?["written_path"]) != null)
            .Select(action => action.At).DefaultIfEmpty(-1).Max();
        foreach (var (call, result, at) in actions.Reverse())
        {
            if (at < changed || ArgsOf(call) is not { } args || Str(result["content"]) is not { } output) continue;
            var command = Str(args["command"]) ?? "";
            var id = Str(args["id"]) ?? "";
            bool matches = check.Kind switch
            {
                "build" => call.Name == "exec_command" && Regex.IsMatch(command, @"(?i)^\s*(?:dotnet|cargo|go)\s+build\b") &&
                    ProjectVerification.Command(command),
                "unit_tests" => call.Name == "exec_command" && ProjectVerification.Tests(command) ||
                    call.Name == LocalTestTools.Name && Str(args["action"]) == "run" && check.Key == id && !LocalTestTools.IsUiResult(output),
                "ui_tests" => call.Name == LocalTestTools.Name && Str(args["action"]) == "run" && check.Key == id,
                _ => call.Name == "exec_command" && check.Key.Length > 0 && command.Trim() == check.Key.Trim(),
            };
            if (!matches) continue;
            if (path != null && call.Name == "exec_command" &&
                !PathHelper.SamePath(path, tools.Context.Resolve(Str(args["working_directory"])))) continue;
            var state = MetaOf(result)?["command_restricted"] != null || output.StartsWith("Пользователь отклонил", StringComparison.Ordinal) ||
                result["content"]?.ToString().Contains(CommandRestriction.Prefix, StringComparison.Ordinal) == true
                ? LocalContractEngine.State.Blocked : ToolRegistry.IsErrorOutput(output) || ToolRegistry.CommandFailed(output)
                    ? LocalContractEngine.State.Failed : LocalContractEngine.State.Passed;
            if (state == LocalContractEngine.State.Passed)
            {
                var transcript = ProjectVerification.Transcript(tools.Context, output);
                state = check.Kind switch
                {
                    "unit_tests" when !ProjectVerification.TestsExecuted(transcript) && !Regex.IsMatch(transcript, @"(?m)^Passed [1-9]\d*/[1-9]\d*$") =>
                        LocalContractEngine.State.NotVerified,
                    "ui_tests" when !LocalTestTools.IsUiResult(output) => LocalContractEngine.State.NotVerified,
                    "command_result" when CommandLog.ResultOf(output) != "код 0" => LocalContractEngine.State.NotVerified,
                    "command_result" when check.Expected.Length > 0 && !transcript.Contains(check.Expected, StringComparison.Ordinal) =>
                        LocalContractEngine.State.Failed,
                    _ => state,
                };
            }
            return new(check, state, PromptText("Результат текущего запуска: ", "Result from this run: ") + LocalContractEngine.Status(state));
        }
        return null;
    }

    ProjectVerification.Recipe? ApplicableRecipe(string? path = null, bool testsOnly = false)
    {
        try { return ProjectVerification.Detect(tools.Context, path, testsOnly); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException) { return null; }
    }

    // A passing check also settles narrower ones (`dotnet build` after `dotnet build App.csproj`) and tests settle builds.
    static bool Covers(string check, string failed) => check.Equals(failed, StringComparison.OrdinalIgnoreCase) ||
        !check.Contains("--no-build", StringComparison.OrdinalIgnoreCase) && failed.StartsWith("dotnet build", StringComparison.OrdinalIgnoreCase) &&
        check.Equals("dotnet test" + failed["dotnet build".Length..], StringComparison.OrdinalIgnoreCase);

    HashSet<string> SupersededResults(int currentUser)
    {
        var keys = new Dictionary<string, string>();
        var latest = new Dictionary<string, (string Id, bool Failed)>();
        var superseded = new HashSet<string>();
        foreach (var message in _history.Range(currentUser + 1, _history.Count - currentUser - 1))
        {
            foreach (var call in (message["tool_calls"] as JsonArray)?.OfType<JsonObject>().Select(ParseCall) ?? [])
                if (call.Name is ("read_file" or "read_many" or "search_text" or "search_files" or "list_directory") ||
                    call.Name == "exec_command" && VerificationCommand(call)) keys[call.Id] = CallKey(call);
            if (Role(message) != "tool" || Str(message["tool_call_id"]) is not { } id || !keys.TryGetValue(id, out var key) ||
                Str(message["content"]) is not { } content || content.StartsWith(AlreadySent, StringComparison.Ordinal)) continue;
            bool failed = ToolRegistry.IsErrorOutput(content) || ToolRegistry.CommandFailed(content);
            if (!failed && latest.TryGetValue(key, out var earlier) && !earlier.Failed) superseded.Add(earlier.Id);
            latest[key] = (id, failed);
        }
        return superseded;
    }
}
