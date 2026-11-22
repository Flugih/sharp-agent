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
    const int SubagentInputTokens = 12_000, SubagentOutputTokens = 1_200, SubagentReasoningTokens = 4_000, SubagentAttachmentLimit = 3, MaxSubagentTasks = 4, MaxSubagentSteps = 4;
    const string SubagentStepStop = "Достигнут лимит шагов субагента.";
    const string SubagentPrompt = """
        Ты — субагент Sharp Agent. Выполни порученную часть задачи. Пользуйся только доступными инструментами чтения и поиска.
        Не меняй файлы. Отвечай основному агенту на языке пользователя. Кратко сообщи результаты, основания и полные пути
        проверенных файлов. Если данных недостаточно, укажи, каких именно.
        """;
    const string EnglishSubagentPrompt = """
        You are a Sharp Agent subagent. Complete your assignment using only the available read and search tools.
        Do not change files. Reply in the user's language and briefly give your findings, evidence, and full paths
        to files you checked. If evidence is missing, say what you need.
        """;

    static readonly JsonNode AskDefinition = JsonNode.Parse("""{"type":"function","function":{"name":"ask_user","description":"Уточнить существенную неоднозначность прямо у пользователя и дождаться ответа.","parameters":{"type":"object","properties":{"question":{"type":"string"},"choices":{"type":"array","items":{"type":"string"},"minItems":2,"maxItems":5}},"required":["question","choices"]}}}""")!;

    static readonly JsonNode DelegateDefinition = JsonNode.Parse("""
        {"type":"function","function":{"name":"delegate_tasks","description":"Параллельно поручить независимые проверки субагентам с инструментами чтения. Возвращает результат каждого.","parameters":{"type":"object","properties":{"tasks":{"type":"array","minItems":1,"items":{"type":"object","properties":{"title":{"type":"string"},"task":{"type":"string","description":"Самодостаточное поручение с нужными требованиями пользователя."},"attachments":{"type":"array","description":"Только точные пути нужных вложений из текущего сообщения пользователя. По умолчанию вложения не передаются.","items":{"type":"string"},"maxItems":3},"steps":{"type":"integer","minimum":2,"maximum":8,"description":"Сколько шагов модели дать субагенту по сложности поручения: точечная проверка — 2–3, широкий анализ — 6–8."}},"required":["title","task","steps"]}}},"required":["tasks"]}}}
        """)!;

    public bool UseSubagents { get; set; }
    public bool FastMode { get; set; }
    int SubagentTaskLimit => FastMode ? MaxSubagentTasks : 2;
    int SubagentStepsPerTask => FastMode ? MaxSubagentSteps : 3;
    int SubagentStepLimit { get; init; } = MaxSubagentSteps;
    public Func<string, string?>? SubagentEffort { get; set; }
    public Func<Clarification, CancellationToken, Task<string>>? ClarifyAsync { get; set; }
    public event Action<SubagentUpdate>? SubagentChanged;
    internal event Action? ComputerUseStarted;

    JsonArray Definitions()
    {
        if (delegated) return tools.Definitions;
        var definitions = (JsonArray)tools.Definitions.DeepClone();
        bool readOnly = CurrentDiagnosis() is { Repair: false };
        bool simple = !WorkMode && LastUser() is { } current && Attachment.ListFrom(current).Count == 0 &&
            AdaptiveModelRouter.IsSimple(ExtractText(current["content"]) ?? "");
        bool plugins = !simple && AvailablePlugins().Count > 0;
        foreach (var tool in definitions.Where(tool => simple || Str(tool?["function"]?["name"]) switch
        {
            "use_plugin" or "run_plugin" => readOnly || !plugins,
            BrowserTools.Name => tools.Context.ReadBrowser == null,
            { } name => readOnly && !ToolRegistry.IsReadOnly(name),
            null => readOnly,
        }).ToArray()) definitions.Remove(tool);
        if (!WorkMode || readOnly)
        {
            if (ClarifyAsync != null) definitions.Add(AskDefinition.DeepClone());
            return definitions;
        }
        if (_activePlugins.Count > 0 &&
            definitions.OfType<JsonObject>().FirstOrDefault(tool => JsonUtil.Str(tool["function"]?["name"]) == "use_plugin") is { } activate)
        {
            var available = AvailablePlugins().Select(plugin => plugin.Name)
                .Where(name => !_activePlugins.ContainsKey(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (available.Length == 0) definitions.Remove(activate);
            else if (activate["function"]?["parameters"]?["properties"]?["name"] is JsonObject name)
                name["enum"] = new JsonArray([.. available.Select(value => JsonValue.Create(value))]);
        }
        definitions.Add(PlanDefinition.DeepClone());
        if (ClarifyAsync != null) definitions.Add(AskDefinition.DeepClone());
        if (UseSubagents && LastUser() is { } user && RemainingSubagentTasks(user) is > 0 and var remaining)
        {
            var definition = DelegateDefinition.DeepClone();
            var tasks = definition["function"]!["parameters"]!["properties"]!["tasks"]!;
            tasks["maxItems"] = remaining;
            var steps = tasks["items"]!["properties"]!["steps"]!;
            steps["maximum"] = SubagentStepsPerTask;
            if (!FastMode) steps["description"] = "Дай 2 шага для точечной проверки и 3, если нужно больше чтения.";
            definitions.Add(definition);
        }
        return definitions;
    }

    string InteractionPrompt() => !RussianLanguage ? EnglishInteractionPrompt() : delegated ? $"\nРабочая папка: {tools.Context.WorkingDirectory}" :
        (ClarifyAsync == null ? "" :
            "\nВызывай ask_user только при существенной неоднозначности, которую нельзя разрешить по контексту " +
            "и без которой нельзя правильно продолжить. Давай короткий вопрос и различимые варианты. " +
            "Не спрашивай о мелочах и не используй этот инструмент для подтверждения действий.") +
        (UseSubagents
            ? (FastMode
                ? "\nВ быстром режиме делегируй независимые проверки одним параллельным вызовом, если они заметно ускорят задачу. " +
                  "Переиспользуй результаты субагентов и не повторяй уже выполненный поиск. Простые задачи делай сам. "
                : "\nКогда доступен delegate_tasks, используй субагентов для отдельных проверок, которые заметно помогают задаче. " +
                  "Обычно поручай одну точечную проверку за 2–3 шага. Не дублируй собственный поиск. ") +
              "Формулируй самодостаточные поручения, включая нужные требования, " +
              "и выбирай только относящиеся к поручению вложения по точным путям через attachments. " +
              $"За запрос можно дать до {SubagentTaskLimit} поручений, до {SubagentStepsPerTask} шагов на каждое. " +
              "Субагенты читают файлы, а изменения выполняешь ты с обычными подтверждениями."
            : "") +
        (FastMode
            ? "\nВ быстром режиме объединяй независимые поиски и чтения в один пакет вызовов. Зависимые шаги выполняй по результатам предыдущих. " +
              "Применяй обычные критерии проверки и завершения задачи."
            : "");

    string EnglishInteractionPrompt()
    {
        if (delegated) return $"\nWorking directory: {tools.Context.WorkingDirectory}";
        var clarification = ClarifyAsync == null ? "" :
            "\nUse ask_user only to resolve ambiguity that blocks the task. Give a short question and distinct options. Do not use it for action confirmations.";
        var delegation = !UseSubagents ? "" :
            (FastMode
                ? "\nIn fast mode, batch independent checks in parallel when that will substantially accelerate the task. Reuse the findings and avoid repeating searches. Handle simple tasks yourself. "
                : "\nWhen delegate_tasks is available, delegate focused checks that materially help. Usually assign one check in 2–3 steps and avoid duplicating your own search. ") +
            $"Give each worker a self-contained brief and only relevant attachment paths. Up to {SubagentTaskLimit} assignments per request, with up to {SubagentStepsPerTask} steps each. Subagents are read-only. Apply changes yourself with the usual confirmations.";
        return clarification + delegation + (FastMode
            ? "\nBatch independent searches and reads. Run dependent steps after their prerequisites, and verify the result as usual." : "");
    }

    async Task<ToolOutcome> ExecuteCallAsync(ToolCallEvent call, CancellationToken ct)
    {
        if (delegated || call.Name is not ("ask_user" or "delegate_tasks" or "update_plan"))
        {
            var shell = call.Name switch
            {
                "run_plugin" => CommandShell.PowerShell,
                "run_command" when ArgsOf(call) is { } command => ShellTools.ShellOf(new ToolArgs(command)),
                _ => (CommandShell?)null,
            };
            if (shell is { } known && _blockedShells.TryGetValue(known, out var restriction))
                return new("Ошибка запуска. " + CommandRestriction.Prefix + restriction.Message, true) { Restriction = restriction };
            var outcome = await tools.ExecuteAsync(call.Name, call.ArgumentsJson, ConfirmAsync, ct,
                call.Name == ComputerTools.Name ? ComputerUseStarted : null);
            if (outcome.Restriction is { UnavailableShell: { } blocked } denied) _blockedShells[blocked] = denied;
            return outcome;
        }
        if (ArgsOf(call) is not { } args) return new("Ошибка: проверь параметры вызова.", true);
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
        var briefs = new List<(string Title, string Task, List<Attachment> Files, int Steps)>();
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
            briefs.Add((title, text, files, Math.Clamp(new ToolArgs(task).Int("steps", FastMode ? 4 : 2), 2, SubagentStepsPerTask)));
        }
        EnsureMeta(_history.Edit(_history.LastIndex(ChatJournal.Flags.User)))["delegated_tasks"] = (long)SubagentTaskLimit - remaining + briefs.Count;
        await SaveAsync();
        var registry = new ToolRegistry(tools.Context, readOnly: true);
        using var slots = new SemaphoreSlim(4);
        var results = await Task.WhenAll(briefs.Select(brief =>
            RunSubagentAsync(registry, slots, model, brief.Title, brief.Task, brief.Files, brief.Steps, ct)));
        var usage = results.Aggregate(new UsageInfo(0, 0, Requests: 0), (total, result) => total.Add(result.Usage));
        if (ct.IsCancellationRequested) return new("Отменено пользователем.", true, DelegatedUsage: usage);
        bool failed = results.All(result => result.Failed);
        return new((failed ? "Ошибка: субагенты не завершили анализ.\n\n" : "") + string.Join("\n\n", results.Select(result => result.Text)),
            failed, DelegatedUsage: usage);
    }

    int RemainingSubagentTasks(JsonObject user) => SubagentTaskLimit - (int)Math.Clamp(Long(MetaOf(user)?["delegated_tasks"]), 0, SubagentTaskLimit);

    async Task<(string Text, bool Failed, UsageInfo Usage)> RunSubagentAsync(ToolRegistry registry, SemaphoreSlim slots, string model,
        string title, string task, List<Attachment> files, int steps, CancellationToken ct)
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
                ReasoningEffort = effort, SubagentStepLimit = steps, CanSpendAsync = CanSpendAsync,
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
        var status = warning == SubagentStepStop ? "Лимит шагов" : failed ? "Ошибка" : "Готово";
        SubagentChanged?.Invoke(update with { Status = status, Elapsed = clock.Elapsed, Result = answer, Failed = failed, Action = null });
        return (title + "\n" + answer, failed, total);
    }

    static string? TextArg(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
