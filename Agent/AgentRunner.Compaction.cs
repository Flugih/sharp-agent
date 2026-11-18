namespace SharpAgent.Agent;

partial class AgentRunner
{
    const string SummaryField = "summary", SummaryUsageField = "summary_usage";
    const int DefaultContextTokens = 128_000, SummaryTokens = 6_000, TranscriptLineChars = 300;
    const double CompactAt = 0.8;
    const string CompactPrompt = """
        Ты сжимаешь рабочую переписку ИИ-агента Sharp Agent, чтобы он продолжил задачу без потери нити.
        Составь подробную сводку на языке пользователя по разделам:
        1. Запросы пользователя — дословно важные формулировки, требования и ограничения.
        2. Ключевые решения и найденные факты о проекте: архитектура, соглашения, причины выбора.
        3. Файлы: полные пути, что в них важно, что изменено и зачем; короткие фрагменты кода, которые нужны дальше.
        4. Ошибки и проверки: что падало, как исправлено, результаты сборок и тестов.
        5. Текущее состояние: что сделано, что осталось, какой следующий шаг.
        Не выдумывай. Пропускай подробности, которые больше не понадобятся, но не теряй требования пользователя и незавершённую работу.
        Остановленные поручения помечай как остановленные в разделе запросов. Их незавершённые действия не включай в следующие шаги.
        """;

    int SummaryStart(int currentUser) => currentUser < 0 ? 0 :
        Math.Max(0, _history.FindLastIndex(currentUser, m => Str(MetaOf(m)?[SummaryField]) != null));

    async Task<int> CompactAsync(string model, int room, int from, int currentUser, HashSet<int> stoppedRequests, CancellationToken ct)
    {
        if (currentUser <= from + 1) return from;
        int cut = currentUser;
        for (int i = cut - 1, kept = HistoryCompressor.EstimateAll(_history.Skip(cut)); i > from; i--)
        {
            if ((kept += HistoryCompressor.Estimate(_history[i])) > room / 4) break;
            if (Role(_history[i]) == "user") cut = i;
        }
        if (cut <= from + 1) return from;
        var transcript = new StringBuilder();
        var previous = from > 0 ? "Прежняя сводка:\n" + Str(MetaOf(_history[from])?[SummaryField]) + "\n" : "";
        for (int i = from; i < cut; i++)
        {
            var message = _history[i];
            transcript.AppendLine(Role(message) switch
            {
                "user" => "Пользователь: " + ExtractText(message["content"]) +
                          string.Concat(Attachment.ListFrom((JsonObject)message).Select(a => "\n  вложение: " + a.Path)),
                "tool" => "Результат: " + Str(HistoryCompressor.Shorten(message)["content"]),
                _ => "Агент: " + ExtractText(message["content"]) + string.Concat((message["tool_calls"] as JsonArray)?.OfType<JsonObject>()
                    .Select(ParseCall).Select(call => $"\n  → {call.Name} {Clip(call.ArgumentsJson, TranscriptLineChars)}") ?? []),
            });
            if (stoppedRequests.Contains(i)) transcript.AppendLine(StoppedRequest);
        }
        int limit = Math.Max(4_000, (room - SummaryTokens) * 3);
        limit -= previous.Length;
        if (limit <= 0) return from;
        var text = previous + (transcript.Length <= limit ? transcript.ToString() : "…\n" + transcript.ToString(transcript.Length - limit, limit));
        Notice?.Invoke("Сжимаю контекст: модель составляет сводку давних шагов.");
        ChatResult result;
        try
        {
            result = await Api!.CompleteAsync(model, [new JsonObject { ["role"] = "system", ["content"] = CompactPrompt },
                new JsonObject { ["role"] = "user", ["content"] = text }], [], SummaryTokens, null, (_, _) => { }, _ => { }, ct);
        }
        catch (ApiException) { return from; }
        var summary = ExtractText(result.Message["content"])?.Trim();
        bool summarized = !string.IsNullOrEmpty(summary);
        if (!summarized && result.Usage == null) return from;
        var meta = EnsureMeta((JsonObject)_history[cut]);
        if (summarized) meta[SummaryField] = summary;
        if (result.Usage is { } usage) RecordAdditionalUsage(meta, model, usage);
        await SaveAsync();
        if (result.Usage != null) UsageReported?.Invoke(model, result.Usage);
        return summarized ? cut : from;
    }

    void RecordAdditionalUsage(JsonObject meta, string model, UsageInfo usage)
    {
        if (meta[SummaryUsageField] is not JsonArray entries) meta[SummaryUsageField] = entries = [];
        var provider = Provider.ToString().ToLowerInvariant();
        var entry = entries.OfType<JsonObject>().FirstOrDefault(item => Str(item["provider"]) == provider && Str(item["model"]) == model);
        if (entry == null)
        {
            entry = new() { ["provider"] = provider, ["model"] = model };
            entries.Add(entry);
        }
        entry["prompt_tokens"] = Long(entry["prompt_tokens"]) + usage.PromptTokens;
        entry["completion_tokens"] = Long(entry["completion_tokens"]) + usage.CompletionTokens;
    }

    JsonObject SummaryMessage(int start) => new()
    {
        ["role"] = "user",
        ["content"] = "[Сводка программы: давние шаги этой переписки заменены сводкой, составленной моделью. Это контекст, а не новая просьба.]\n" +
                      Str(MetaOf(_history[start])?[SummaryField]),
    };
}
