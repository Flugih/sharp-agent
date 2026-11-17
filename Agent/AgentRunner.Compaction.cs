namespace SharpAgent.Agent;

partial class AgentRunner
{
    const string SummaryField = "summary";
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
        """;

    int SummaryStart() => _history.FindLastIndex(m => Str(MetaOf(m)?[SummaryField]) != null);

    // Folds older history into a model-written summary kept on the first message that stays in full.
    async Task<bool> CompactAsync(string model, int room, CancellationToken ct)
    {
        int from = Math.Max(0, SummaryStart()), cut = _history.FindLastIndex(m => Role(m) != "tool");
        for (int i = cut - 1, kept = HistoryCompressor.EstimateAll(_history.Skip(cut)); i > from; i--)
        {
            if ((kept += HistoryCompressor.Estimate(_history[i])) > room / 4) break;
            if (Role(_history[i]) != "tool") cut = i;
        }
        if (cut <= from + 1) return false;
        var transcript = new StringBuilder();
        if (from > 0) transcript.AppendLine("Прежняя сводка:\n" + Str(MetaOf(_history[from])?[SummaryField]));
        foreach (var message in _history.Skip(from).Take(cut - from))
            transcript.AppendLine(Role(message) switch
            {
                "user" => "Пользователь: " + ExtractText(message["content"]) +
                          string.Concat(Attachment.ListFrom((JsonObject)message).Select(a => "\n  вложение: " + a.Path)),
                "tool" => "Результат: " + Str(HistoryCompressor.Shorten(message)["content"]),
                _ => "Агент: " + ExtractText(message["content"]) + string.Concat((message["tool_calls"] as JsonArray)?.OfType<JsonObject>()
                    .Select(ParseCall).Select(call => $"\n  → {call.Name} {Clip(call.ArgumentsJson, TranscriptLineChars)}") ?? []),
            });
        int limit = Math.Max(4_000, (room - SummaryTokens) * 3);
        var text = transcript.Length <= limit ? transcript.ToString() : "…\n" + transcript.ToString(transcript.Length - limit, limit);
        Notice?.Invoke("Сжимаю контекст: модель составляет сводку давних шагов.");
        ChatResult result;
        try
        {
            result = await Api!.CompleteAsync(model, [new JsonObject { ["role"] = "system", ["content"] = CompactPrompt },
                new JsonObject { ["role"] = "user", ["content"] = text }], [], SummaryTokens, null, (_, _) => { }, _ => { }, ct);
        }
        catch (ApiException) { return false; }
        if (result.Usage != null) UsageReported?.Invoke(model, result.Usage);
        if (ExtractText(result.Message["content"])?.Trim() is not { Length: > 0 } summary) return false;
        EnsureMeta((JsonObject)_history[cut])[SummaryField] = summary;
        Save();
        return true;
    }

    JsonObject SummaryMessage(int start, int currentUser)
    {
        var text = "[Сводка программы: давние шаги этой переписки заменены сводкой, составленной моделью. Это контекст, а не новая просьба.]\n" +
                   Str(MetaOf(_history[start])?[SummaryField]);
        if (currentUser >= 0 && currentUser < start)
            text += "\n\n[Текущий запрос пользователя, на котором ты работаешь]\n" + Clip(ExtractText(_history[currentUser]["content"]) ?? "", 4_000);
        return new() { ["role"] = "user", ["content"] = text };
    }
}
