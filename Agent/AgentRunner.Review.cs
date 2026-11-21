namespace SharpAgent.Agent;

partial class AgentRunner
{
    const string ReviewField = "review", AlreadySent = "Полный результат этого действия уже передан.";
    const int MaxReviews = 3;
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
    string? ReviewNote()
    {
        int start = _history.LastIndex(ChatJournal.Flags.User);
        if (delegated || start < 0) return null;
        int atReview = start + 1, since = start, reviews = 0;
        foreach (var message in _history.Range(atReview, _history.Count - atReview))
        {
            if (ReviewOf(message) != null)
            {
                if (++reviews >= MaxReviews) return null;
                since = atReview;
            }
            atReview++;
        }
        int lastChange = -1, lastCheck = -1;
        var changed = new List<string>();
        var failed = new Dictionary<string, ToolCallEvent>();
        foreach (var (call, result, at) in CurrentActions())
        {
            if (Str(MetaOf(result)?["written_path"]) is { } path && ProjectSourceIndex.IsSource(path) &&
                !Scripts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                lastChange = at;
                if (!changed.Contains(path, StringComparer.OrdinalIgnoreCase)) changed.Add(path);
            }
            else if (call.Name == "run_command" && VerificationCommand(call) || ComputerTools.IsControl(call.Name) && Str(ArgsOf(call)?["action"]) == "launch")
            {
                lastCheck = at;
                var key = call.Name == "run_command" ? Str(ArgsOf(call)?["command"])!.Trim() : call.ArgumentsJson;
                var output = Str(result["content"]) ?? "";
                if (at > since && (ToolRegistry.IsErrorOutput(output) || ToolRegistry.CommandFailed(output))) failed[key] = call;
                else foreach (var covered in failed.Keys.Where(earlier => Covers(key, earlier)).ToList()) failed.Remove(covered);
            }
        }
        var notes = new List<string>();
        if (CurrentPlan() is { } plan && (reviews == 0 || plan.At > since) && plan.Items.Where(item => !item.Done).Select(item => item.Step).ToList() is { Count: > 0 } open)
            notes.Add(PromptText("В плане остались невыполненные пункты: ", "The plan has unfinished steps: ") + string.Join("; ", open) +
                      PromptText(". Выполни их или обнови план, если они не нужны.", ". Complete them or update the plan if they are unnecessary."));
        if (lastChange > since && lastCheck < lastChange && tools.Context.SandboxRoot == null)
            notes.Add(PromptText("После последних изменений кода не было проверки: ", "The latest code changes were not verified: ") +
                      string.Join(", ", changed.TakeLast(6).Select(Path.GetFileName)) +
                      PromptText(". Проверь изменённое поведение на сценарии пользователя или целевым тестом. Если проверить нечем, скажи это в ответе.",
                          ". Verify the changed behavior in the user’s scenario or with a targeted test. If verification is unavailable, say so in the answer."));
        else if (lastCheck > since && lastCheck > lastChange && failed.Count > 0)
            notes.Add(PromptText("Остались ошибки проверки: ", "Verification failures remain: ") +
                      string.Join(", ", failed.Values.TakeLast(4).Select(call => HistoryCompressor.Describe(call.Name, call.ArgumentsJson))) +
                      PromptText(". Исправь причину и проверь снова либо объясни, что помешало.", ". Fix the cause and check again, or explain what blocked verification."));
        return notes.Count == 0 ? null
            : PromptText("[Программа: проверка перед завершением] ", "[Application: pre-completion check] ") + string.Join("\n", notes) +
              PromptText("\nСверь результат с запросом пользователя. Если всё уже сделано и проверено, повтори итоговый ответ полностью: пользователь увидит только его.",
                  "\nCompare the result with the user’s request. If everything is complete and verified, repeat the final answer in full. The user sees only that version.");
    }

    // A passing check also settles narrower ones (`dotnet build` after `dotnet build App.csproj`) and tests settle builds.
    static bool Covers(string check, string failed) => failed.StartsWith(check, StringComparison.OrdinalIgnoreCase) ||
        failed.Replace("build", "test", StringComparison.OrdinalIgnoreCase).StartsWith(check, StringComparison.OrdinalIgnoreCase);

    HashSet<string> SupersededResults(int currentUser)
    {
        var keys = new Dictionary<string, string>();
        var latest = new Dictionary<string, string>();
        var superseded = new HashSet<string>();
        foreach (var message in _history.Range(currentUser + 1, _history.Count - currentUser - 1))
        {
            foreach (var call in (message["tool_calls"] as JsonArray)?.OfType<JsonObject>().Select(ParseCall) ?? []) keys[call.Id] = CallKey(call);
            if (Role(message) != "tool" || Str(message["tool_call_id"]) is not { } id || !keys.TryGetValue(id, out var key) ||
                Str(message["content"]) is not { } content || content.StartsWith(AlreadySent, StringComparison.Ordinal)) continue;
            if (latest.TryGetValue(key, out var earlier)) superseded.Add(earlier);
            latest[key] = id;
        }
        return superseded;
    }
}
