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
        ? "Текущий план:\n" + string.Join("\n", plan.Items.Select(item => (item.Done ? "[x] " : "[ ] ") + item.Step)) : null;

    // A local check before the model finishes: one cheap extra step only when work is visibly unfinished or unverified.
    string? ReviewNote()
    {
        int start = _history.FindLastIndex(m => Role(m) == "user");
        var reviews = _history.Skip(start + 1).Where(m => ReviewOf(m) != null).ToList();
        if (delegated || start < 0 || reviews.Count >= MaxReviews) return null;
        int since = reviews.Count > 0 ? _history.LastIndexOf(reviews[^1]) : start;
        int lastChange = -1, lastCheck = -1;
        var changed = new List<string>();
        var failed = new Dictionary<string, ToolCallEvent>();
        foreach (var (call, result) in CurrentActions())
        {
            int at = _history.IndexOf(result);
            if (Str(MetaOf(result)?["written_path"]) is { } path && ProjectSourceIndex.IsSource(path) &&
                !Scripts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                lastChange = at;
                if (!changed.Contains(path, StringComparer.OrdinalIgnoreCase)) changed.Add(path);
            }
            else if (call.Name == "run_command" && VerificationCommand(call) || call.Name == ComputerTools.Name && Str(ArgsOf(call)?["action"]) == "launch")
            {
                lastCheck = at;
                var key = call.Name == "run_command" ? Str(ArgsOf(call)?["command"])!.Trim() : call.ArgumentsJson;
                var output = Str(result["content"]) ?? "";
                if (at > since && (ToolRegistry.IsErrorOutput(output) || ToolRegistry.CommandFailed(output))) failed[key] = call;
                else foreach (var covered in failed.Keys.Where(earlier => Covers(key, earlier)).ToList()) failed.Remove(covered);
            }
        }
        var notes = new List<string>();
        if (CurrentPlan() is { } plan && (reviews.Count == 0 || plan.At > since) && plan.Items.Where(item => !item.Done).Select(item => item.Step).ToList() is { Count: > 0 } open)
            notes.Add("В плане остались невыполненные пункты: " + string.Join("; ", open) + ". Выполни их или обнови план, если они не нужны.");
        if (lastChange > since && lastCheck < lastChange && tools.Context.SandboxRoot == null)
            notes.Add($"Ты изменил код ({string.Join(", ", changed.TakeLast(6).Select(Path.GetFileName))}), но после этого не проверил результат. " +
                      "Проверь изменённое поведение на сценарии пользователя или целевым тестом. Если проверить нечем, скажи это в ответе.");
        else if (lastCheck > since && lastCheck > lastChange && failed.Count > 0)
            notes.Add("Остались ошибки проверки: " + string.Join(", ", failed.Values.TakeLast(4).Select(call => HistoryCompressor.Describe(call.Name, call.ArgumentsJson))) +
                      ". Исправь причину и проверь снова либо объясни, что помешало.");
        return notes.Count == 0 ? null
            : "[Программа: проверка перед завершением] " + string.Join("\n", notes) +
              "\nСверь результат с запросом пользователя. Если всё уже сделано и проверено, повтори итоговый ответ полностью: пользователь увидит только его.";
    }

    // A passing check also settles narrower ones (`dotnet build` after `dotnet build App.csproj`) and tests settle builds.
    static bool Covers(string check, string failed) => failed.StartsWith(check, StringComparison.OrdinalIgnoreCase) ||
        failed.Replace("build", "test", StringComparison.OrdinalIgnoreCase).StartsWith(check, StringComparison.OrdinalIgnoreCase);

    HashSet<string> SupersededResults(int currentUser)
    {
        var keys = new Dictionary<string, string>();
        var latest = new Dictionary<string, string>();
        var superseded = new HashSet<string>();
        foreach (var message in _history.Skip(currentUser + 1))
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
