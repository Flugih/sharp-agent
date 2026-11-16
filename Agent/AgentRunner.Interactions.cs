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
    const string SubagentPrompt = """
        Ты — субагент Sharp Agent. Выполни порученную часть задачи. Пользуйся только доступными инструментами чтения и поиска.
        Не меняй файлы. Отвечай основному агенту на языке пользователя. Кратко сообщи результаты, основания и полные пути
        проверенных файлов. Если данных недостаточно, укажи, каких именно.
        """;

    static readonly JsonNode AskDefinition = JsonNode.Parse("""{"type":"function","function":{"name":"ask_user","description":"Уточнить существенную неоднозначность прямо у пользователя и дождаться ответа.","parameters":{"type":"object","properties":{"question":{"type":"string"},"choices":{"type":"array","items":{"type":"string"},"minItems":2,"maxItems":5}},"required":["question","choices"]}}}""")!;

    static readonly JsonNode DelegateDefinition = JsonNode.Parse("""{"type":"function","function":{"name":"delegate_tasks","description":"Параллельно поручить независимые проверки или анализ субагентам с инструментами чтения. Возвращает результат каждого.","parameters":{"type":"object","properties":{"tasks":{"type":"array","minItems":1,"items":{"type":"object","properties":{"title":{"type":"string"},"task":{"type":"string"}},"required":["title","task"]}}},"required":["tasks"]}}}""")!;

    public bool UseSubagents { get; set; }
    public Func<Clarification, CancellationToken, Task<string>>? ClarifyAsync { get; set; }
    public event Action<SubagentUpdate>? SubagentChanged;

    JsonArray Definitions()
    {
        if (delegated || ClarifyAsync == null && !UseSubagents && _activePlugins.Count == 0) return tools.Definitions;
        var definitions = (JsonArray)tools.Definitions.DeepClone();
        if (_activePlugins.Count > 0 &&
            definitions.OfType<JsonObject>().FirstOrDefault(tool => JsonUtil.Str(tool["function"]?["name"]) == "use_plugin") is { } activate)
        {
            var available = AvailablePlugins().Select(plugin => plugin.Name)
                .Where(name => !_activePlugins.ContainsKey(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (available.Length == 0) definitions.Remove(activate);
            else if (activate["function"]?["parameters"]?["properties"]?["name"] is JsonObject name)
                name["enum"] = new JsonArray([.. available.Select(value => JsonValue.Create(value))]);
        }
        if (ClarifyAsync != null) definitions.Add(AskDefinition.DeepClone());
        if (UseSubagents) definitions.Add(DelegateDefinition.DeepClone());
        return definitions;
    }

    string InteractionPrompt() => delegated ? $"\nРабочая папка: {tools.Context.WorkingDirectory}" :
        (ClarifyAsync == null ? "" :
            "\nВызывай ask_user только при существенной неоднозначности, которую нельзя разрешить по контексту " +
            "и без которой нельзя правильно продолжить. Давай короткий вопрос и различимые варианты. " +
            "Не спрашивай о мелочах и не используй этот инструмент для подтверждения действий.") +
        (UseSubagents
            ? "\nСам решай, нужны ли субагенты, сколько независимых частей выделить и как объединить результаты. " +
              "Поручай им проверки и анализ через delegate_tasks, передавай каждой задаче нужные пути и контекст. " +
              "Субагенты читают файлы, а изменения выполняешь ты с обычными подтверждениями."
            : "");

    async Task<ToolOutcome> ExecuteCallAsync(ToolCallEvent call, CancellationToken ct)
    {
        if (delegated || call.Name is not ("ask_user" or "delegate_tasks"))
        {
            var shell = call.Name switch
            {
                "run_plugin" => CommandShell.PowerShell,
                "run_command" when ArgsOf(call) is { } command => ShellTools.ShellOf(new ToolArgs(command)),
                _ => (CommandShell?)null,
            };
            if (shell is { } known && _blockedShells.TryGetValue(known, out var restriction))
                return new("Ошибка запуска. " + CommandRestriction.Prefix + restriction.Message, true) { Restriction = restriction };
            var outcome = await tools.ExecuteAsync(call.Name, call.ArgumentsJson, ConfirmAsync, ct);
            if (outcome.Restriction is { UnavailableShell: { } blocked } denied) _blockedShells[blocked] = denied;
            return outcome;
        }
        if (ArgsOf(call) is not { } args) return new("Ошибка: проверь параметры вызова.", true);
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
        if (tasks is not { Count: > 0 } || _model is not { } model) return new("Ошибка: укажи задачи для субагентов.", true);
        var briefs = tasks.OfType<JsonObject>().Select(task => (Title: TextArg(task["title"]), Task: TextArg(task["task"]))).ToArray();
        if (briefs.Length != tasks.Count || briefs.Any(brief => brief.Title is not { Length: <= 100 } || brief.Task is not { Length: <= 8000 }))
            return new("Ошибка: каждой задаче нужны короткое название и описание.", true);
        var user = _history.OfType<JsonObject>().Last(message => Role(message) == "user");
        var registry = new ToolRegistry(tools.Context, readOnly: true);
        using var slots = new SemaphoreSlim(4);
        int limit = Math.Clamp(2 * (MaxInputTokens / briefs.Length), 800, 10_000);
        var results = await Task.WhenAll(briefs.Select(brief =>
            RunSubagentAsync(registry, slots, model, brief.Title!, brief.Task!, user, limit, ct)));
        bool failed = results.All(result => result.Failed);
        return new((failed ? "Ошибка: субагенты не завершили анализ.\n\n" : "") + string.Join("\n\n", results.Select(result => result.Text)),
            failed, DelegatedUsage: new(results.Sum(r => r.Usage.PromptTokens), results.Sum(r => r.Usage.CompletionTokens)));
    }

    async Task<(string Text, bool Failed, UsageInfo Usage)> RunSubagentAsync(ToolRegistry registry, SemaphoreSlim slots, string model,
        string title, string task, JsonObject user, int limit, CancellationToken ct)
    {
        var update = new SubagentUpdate(Guid.NewGuid().ToString("N"), title, "В очереди") { Model = model, Provider = Provider, Effort = ReasoningEffort };
        SubagentChanged?.Invoke(update);
        bool entered = false, failed = false;
        string? answer = null, warning = null;
        long promptTokens = 0, completionTokens = 0;
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
                Api = Api, Provider = Provider, MaxTokens = MaxTokens, ReasoningEffort = ReasoningEffort, CanSpendAsync = CanSpendAsync,
                ModelSupportsVision = ModelSupportsVision, ModelContextTokens = ModelContextTokens,
            };
            var actions = new Dictionary<string, ToolCallEvent>();
            worker.ThinkingStarted += (_, _) =>
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
            worker.AssistantText += (text, elapsed) =>
            {
                if (elapsed != null) answer = text;
                else Comment(text);
            };
            worker.Warning += text => warning = text;
            worker.Stopped += text => warning = text;
            worker.UsageReported += (id, usage) =>
            {
                promptTokens += usage.PromptTokens;
                completionTokens += usage.CompletionTokens;
                UsageReported?.Invoke(id, usage);
            };
            await worker.RunAsync($"Поручение основного агента:\n{task}\n\nИсходный запрос пользователя:\n{ExtractText(user["content"]) ?? ""}",
                Attachment.ListFrom(user), model, ct);
            failed = string.IsNullOrWhiteSpace(answer);
            if (failed) answer = warning ?? "Модель не вернула результат. Уточни поручение или выполни его самостоятельно.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SubagentChanged?.Invoke(update with { Status = "Остановлен", Elapsed = clock.Elapsed, Failed = true, Action = null });
            throw;
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
        if (answer!.Length > limit) answer = answer[..limit] + "\n… результат сокращён";
        SubagentChanged?.Invoke(update with { Status = failed ? "Ошибка" : "Готово", Elapsed = clock.Elapsed, Result = answer, Failed = failed, Action = null });
        return (title + "\n" + answer, failed, new(promptTokens, completionTokens));
    }

    static string? TextArg(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
}
