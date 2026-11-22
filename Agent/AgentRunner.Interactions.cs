namespace SharpAgent.Agent;

public sealed record Clarification(string Question, IReadOnlyList<string> Choices);
public sealed record SubagentUpdate(string Id, string Title, string Status, DateTimeOffset? Started = null,
    TimeSpan? Elapsed = null, string? Result = null, bool Failed = false, ToolCallEvent? Action = null)
{
    internal string? Model { get; init; }
    internal AgentProvider Provider { get; init; }
    internal string? Effort { get; init; }
    internal bool NewStep { get; init; }
    internal string? Commentary { get; init; }
    internal ToolResultEvent? CompletedAction { get; init; }
}

partial class AgentRunner
{
    const int SubagentOutputTokens = 1_200, SubagentReasoningTokens = 4_000, SubagentAttachmentLimit = 3, MaxSubagentTasks = 4;
    static readonly JsonNode AskDefinition = JsonNode.Parse("""{"type":"function","function":{"name":"ask_user","description":"Уточнить существенную неоднозначность прямо у пользователя и дождаться ответа.","parameters":{"type":"object","properties":{"question":{"type":"string"},"choices":{"type":"array","items":{"type":"string"},"minItems":2,"maxItems":5}},"required":["question","choices"]}}}""")!;

    static readonly JsonNode DelegateDefinition = JsonNode.Parse("""
        {"type":"function","function":{"name":"delegate_tasks","description":"Параллельно поручить независимые проверки субагентам с инструментами чтения. Возвращает результат каждого.","parameters":{"type":"object","properties":{"tasks":{"type":"array","minItems":1,"items":{"type":"object","properties":{"title":{"type":"string"},"task":{"type":"string","description":"Самодостаточное поручение с нужными требованиями пользователя."},"attachments":{"type":"array","description":"Только точные пути нужных вложений из текущего сообщения пользователя. По умолчанию вложения не передаются.","items":{"type":"string"},"maxItems":3}},"required":["title","task"]}}},"required":["tasks"]}}}
        """)!;

    public bool UseSubagents { get; set; }
    public bool FastMode { get; set; }
    int SubagentTaskLimit => FastMode ? MaxSubagentTasks : 2;
    public Func<string, string?>? SubagentEffort { get; set; }
    public Func<Clarification, CancellationToken, Task<string>>? ClarifyAsync { get; set; }
    public event Action<SubagentUpdate>? SubagentChanged;
    internal event Action? ComputerUseStarted;

    JsonArray Definitions()
    {
        if (delegated) return tools.Definitions;
        bool readOnly = CurrentDiagnosis() is { Repair: false };
        var request = ExtractText(LastUser()?["content"]) ?? "";
        bool simple = !WorkMode && LastUser() is { } current && Attachment.ListFrom(current).Count == 0 &&
            AdaptiveModelRouter.IsSimple(request);
        var initial = !readOnly && !simple ? ToolDefinitionSelection.Initial(request) : null;
        bool plugins = !simple && AvailablePlugins().Count > 0;
        bool allowBrowser = ToolDefinitionSelection.BrowserInteraction(request);
        bool background = ToolDefinitionSelection.BackgroundResearch(request);
        var catalog = _toolCatalog = tools.Definitions.OfType<JsonObject>()
            .Where(tool => !simple && (Str(tool["function"]?["name"]) switch
        {
            "use_plugin" or "run_plugin" => !readOnly && plugins,
            "open_path" or ComputerTools.Name or ComputerTools.AppName when background => false,
            BrowserTools.Name => allowBrowser && tools.Context.ReadBrowser != null,
            { } name => !readOnly || ToolRegistry.IsReadOnly(name),
            null => false,
        })).ToDictionary(tool => Str(tool["function"]?["name"])!, tool => (JsonNode)tool, StringComparer.Ordinal);
        if (_activePlugins.Count > 0 &&
            catalog.TryGetValue("use_plugin", out var activate))
        {
            var available = AvailablePlugins().Select(plugin => plugin.Name)
                .Where(name => !_activePlugins.ContainsKey(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            activate = (JsonObject)activate.DeepClone();
            if (available.Length == 0) catalog.Remove("use_plugin");
            else if (activate["function"]?["parameters"]?["properties"]?["name"] is JsonObject name)
            {
                name["enum"] = new JsonArray([.. available.Select(value => JsonValue.Create(value))]);
                catalog["use_plugin"] = activate;
            }
        }
        if (WorkMode && !readOnly) catalog.Add("update_plan", PlanDefinition);
        if (ClarifyAsync != null) catalog.Add("ask_user", AskDefinition);
        if (WorkMode && !readOnly && UseSubagents && LastUser() is { } user && RemainingSubagentTasks(user) is > 0 and var remaining)
        {
            var definition = DelegateDefinition.DeepClone();
            var tasks = definition["function"]!["parameters"]!["properties"]!["tasks"]!;
            tasks["maxItems"] = remaining;
            catalog.Add("delegate_tasks", definition);
        }
        var selected = catalog.Where(item => initial == null || initial(item.Key) || item.Key == "ask_user" ||
            (item.Key is "update_plan" or "delegate_tasks") && Regex.IsMatch(request, @"(?i)\b(?:plan|subagents?)\b|план|субагент")).ToArray();
        var definitions = _scopedDefinitions = new JsonArray([.. selected.Select(item => item.Value.DeepClone())]);
        if (selected.Length == catalog.Count) return definitions;
        definitions.Add(ToolDefinitionSelection.EnableDefinition());
        foreach (var (call, result, _) in CurrentActions())
            if (call.Name == "enable_tools" && !ToolRegistry.IsErrorOutput(Str(result["content"]) ?? "") && ArgsOf(call) is { } args)
                EnableToolGroup(args);
        return _scopedDefinitions;
    }

    async Task<ToolOutcome> ExecuteCallAsync(ToolCallEvent call, CancellationToken ct)
    {
        if (call.Name == LocalContractEngine.Name && ArgsOf(call) is { } contract)
        {
            var evidence = CurrentActions().ToArray();
            try
            {
                var result = await Task.Run(() => LocalContractEngine.Verify(new(contract), tools.Context, ct,
                    check => ContractEvidence(check, evidence)), ct);
                return new(result, false);
            }
            catch (ToolException ex) { return new("Ошибка: " + ex.Message, true); }
        }
        if (delegated || call.Name is not ("ask_user" or "delegate_tasks" or "update_plan" or "enable_tools"))
        {
            var shell = call.Name switch
            {
                "run_plugin" => CommandShell.PowerShell,
                "exec_command" when ArgsOf(call) is { } command && string.IsNullOrWhiteSpace(Str(command["session_id"])) =>
                    ShellTools.ShellOf(new ToolArgs(command)),
                _ => (CommandShell?)null,
            };
            if (shell is { } known && _blockedShells.TryGetValue(known, out var restriction))
                return new("Ошибка запуска. " + CommandRestriction.Prefix + restriction.Message, true) { Restriction = restriction };
            var request = ExtractText(LastUser()?["content"]) ?? "";
            bool allowBrowser = !delegated && (ToolDefinitionSelection.BrowserInteraction(request) ||
                call.Name == LocalTestTools.Name && LocalActionRouter.Match(request)?.StartsWith("suite run ", StringComparison.Ordinal) == true);
            if (ComputerTools.IsControl(call.Name) && !allowBrowser && ToolDefinitionSelection.BackgroundResearch(request))
                return new("Ошибка: для фонового изучения используй web_search или читай рабочий репозиторий локально.", true);
            var outcome = await tools.ExecuteAsync(call.Name, call.ArgumentsJson, ConfirmAsync, ct,
                call.Name == ComputerTools.Name ? ComputerUseStarted : null, allowBrowser);
            if (outcome.Restriction is { UnavailableShell: { } blocked } denied) _blockedShells[blocked] = denied;
            return outcome;
        }
        if (ArgsOf(call) is not { } args) return new("Ошибка: проверь параметры вызова.", true);
        if (call.Name == "enable_tools") return EnableToolGroup(args);
        if (call.Name == "update_plan") return UpdatePlan(args);
        if (call.Name == "delegate_tasks" && UseSubagents) return await DelegateAsync(args["tasks"] as JsonArray, ct);
        if (call.Name == "ask_user" && ClarifyAsync is { } ask &&
            TextArg(args["question"]) is { Length: <= 600 } question && args["choices"] is JsonArray { Count: >= 2 and <= 5 } choices)
        {
            var options = choices.Select(TextArg).OfType<string>().Where(choice => choice.Length <= 180).Distinct().ToArray();
            if (options.Length >= 2) return new("Ответ пользователя: " + await ask(new(question, options), ct), false);
        }
        return new("Ошибка: инструмент недоступен или его параметры неполны.", true);
    }

    async Task<ToolOutcome> DelegateAsync(JsonArray? tasks, CancellationToken ct)
    {
        if (tasks is not { Count: > 0 } || _model is not { } model || LastUser() is not { } user)
            return new("Ошибка: укажи задачи для субагентов.", true);
        int remaining = RemainingSubagentTasks(user);
        if (tasks.Count > remaining) return new($"Ошибка: в этом запросе осталось поручений для субагентов: {remaining}.", true);
        var available = Attachment.ListFrom(user);
        var briefs = new List<(string Title, string Task, List<Attachment> Files)>();
        foreach (var node in tasks)
        {
            if (node is not JsonObject task || TextArg(task["title"]) is not { Length: <= 100 } title ||
                TextArg(task["task"]) is not { Length: <= 6000 } text)
                return new("Ошибка: каждой задаче нужны короткое название и описание.", true);
            List<Attachment> files = [];
            if (task["attachments"] is { } selection)
            {
                if (selection is not JsonArray { Count: <= SubagentAttachmentLimit } paths)
                    return new($"Ошибка: укажи до {SubagentAttachmentLimit} вложений на поручение.", true);
                foreach (var entry in paths)
                {
                    var path = TextArg(entry)?.Replace('/', '\\');
                    var file = available.Find(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (file == null) return new("Ошибка: выбирай вложения только из текущего сообщения пользователя.", true);
                    if (!files.Contains(file)) files.Add(file);
                }
            }
            briefs.Add((title, text, files));
        }
        EnsureMeta(_history.Edit(_history.LastIndex(ChatJournal.Flags.User)))["delegated_tasks"] = (long)SubagentTaskLimit - remaining + briefs.Count;
        await SaveAsync();
        var registry = new ToolRegistry(tools.Context, readOnly: true);
        using var slots = new SemaphoreSlim(4);
        var results = await Task.WhenAll(briefs.Select(brief =>
            RunSubagentAsync(registry, slots, model, brief.Title, brief.Task, brief.Files, ct)));
        var usage = results.Aggregate(new UsageInfo(0, 0, Requests: 0), (total, result) => total.Add(result.Usage));
        if (ct.IsCancellationRequested) return new("Отменено пользователем.", true, DelegatedUsage: usage);
        bool failed = results.All(result => result.Failed);
        return new((failed ? "Ошибка: субагенты не завершили анализ.\n\n" : "") + string.Join("\n\n", results.Select(result => result.Text)),
            failed, DelegatedUsage: usage);
    }

    int RemainingSubagentTasks(JsonObject user) => SubagentTaskLimit - (int)Math.Clamp(Long(MetaOf(user)?["delegated_tasks"]), 0, SubagentTaskLimit);

    async Task<(string Text, bool Failed, UsageInfo Usage)> RunSubagentAsync(ToolRegistry registry, SemaphoreSlim slots, string model,
        string title, string task, List<Attachment> files, CancellationToken ct)
    {
        var effort = _noEffortModels.Contains(model) ? null : SubagentEffort?.Invoke(model);
        var update = new SubagentUpdate(Guid.NewGuid().ToString("N"), title, "В очереди") { Model = model, Provider = Provider, Effort = effort };
        SubagentChanged?.Invoke(update);
        bool entered = false, failed = false;
        string? answer = null, warning = null;
        var total = new UsageInfo(0, 0, Requests: 0);
        var clock = new Stopwatch();
        void Report(string status, ToolCallEvent? action = null, ToolResultEvent? completed = null, bool newStep = false)
        {
            update = update with { Status = status, Action = action };
            SubagentChanged?.Invoke(update with { CompletedAction = completed, NewStep = newStep });
        }
        void Comment(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text)) SubagentChanged?.Invoke(update with { Commentary = text });
        }
        try
        {
            await slots.WaitAsync(ct);
            entered = true;
            clock.Start();
            update = update with { Started = DateTimeOffset.Now };
            Report("Начинает работу");
            var worker = new AgentRunner(registry, store, true)
            {
                // Providers count reasoning into the output limit, so the short answer gets room for thinking on top.
                Api = Api, Provider = Provider, RussianLanguage = RussianLanguage, MaxTokens = Math.Min(MaxTokens ?? SubagentOutputTokens, SubagentOutputTokens) +
                    (FastMode ? SubagentReasoningTokens : SubagentReasoningTokens / 2),
                ReasoningEffort = effort, CanSpendAsync = CanSpendAsync,
                ModelSupportsVision = ModelSupportsVision, ModelContextTokens = ModelContextTokens,
                _timeline = _timeline, _timelineParent = update.Id,
            };
            var actions = new Dictionary<string, ToolCallEvent>();
            worker.ThinkingStarted += () =>
            {
                if (worker._noEffortModels.Contains(model)) update = update with { Effort = null };
                Report("Модель отвечает", newStep: true);
            };
            worker.ProgressReported += progress => Comment(progress.Commentary);
            worker.ToolCallStarted += call =>
            {
                actions[call.Id] = call;
                Report("В работе", call);
            };
            worker.ToolCallFinished += result =>
            {
                actions.Remove(result.Id);
                Report(actions.Count == 0 ? "Обрабатывает результат" : "В работе", actions.Values.FirstOrDefault(), result);
            };
            worker.AssistantText += (text, elapsed, _) =>
            {
                if (elapsed != null) answer = text;
                else Comment(text);
            };
            worker.Warning += text => warning = text;
            worker.Stopped += text => warning = text;
            worker.UsageReported += (id, usage) =>
            {
                total = total.Add(usage);
                UsageReported?.Invoke(id, usage);
            };
            await worker.RunAsync(PromptText("Поручение основного агента:\n", "Assignment from the main agent:\n") + task, files, model, ct);
            failed = string.IsNullOrWhiteSpace(answer);
            if (failed) answer = warning ?? "Модель не вернула результат. Уточни поручение или выполни его самостоятельно.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SubagentChanged?.Invoke(update with { Status = "Остановлен", Elapsed = clock.Elapsed, Failed = true, Action = null });
            return ("Отменено пользователем.", true, total);
        }
        catch (Exception ex)
        {
            failed = true;
            answer = ex is ApiException ? ex.Message : "Не удалось выполнить поручение. Попробуй упростить задачу.";
        }
        finally
        {
            clock.Stop();
            if (entered) slots.Release();
        }
        var status = failed ? "Ошибка" : "Готово";
        SubagentChanged?.Invoke(update with { Status = status, Elapsed = clock.Elapsed, Result = answer, Failed = failed, Action = null });
        return (title + "\n" + answer, failed, total);
    }

    static string? TextArg(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
