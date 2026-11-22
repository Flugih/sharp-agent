using System.Text.Json.Nodes;

namespace SharpAgent.Tools;

/// <summary>Deterministic, read-only postconditions. A failed mandatory check is a tool failure, never success text.</summary>
internal static class LocalContractEngine
{
    internal const string Name = "local_contract";

    internal static AgentTool Tool() => new()
    {
        Name = Name,
        Description = "Локальная проверка результата без AI: exists, absent, sha256, contains, not_contains, min_bytes, max_bytes, line_count, json_value и csv_rows. Невыполненное обязательное условие возвращает ошибку.",
        Parameters = ParameterSchema(),
        Handler = AgentTool.Sync(Verify),
    };

    static JsonObject ParameterSchema()
    {
        var schema = Schema.Object(new Prop("checks", "array", "Массив проверок условий успеха.", true));
        var checks = (JsonObject)((JsonObject)schema["properties"]!)["checks"]!;
        checks["minItems"] = 1;
        checks["maxItems"] = 24;
        checks["items"] = Schema.Object(new Prop("kind", "string", "Тип условия.", true,
            ["exists", "absent", "sha256", "contains", "not_contains", "min_bytes", "max_bytes", "line_count", "csv_rows", "json_value"]),
            new Prop("path", "string", "Полный или относительный путь к файлу.", true),
            new Prop("expected", "string", "Ожидаемая строка/хеш/число (не нужна для exists и absent)."),
            new Prop("key", "string", "Путь к свойству через точку для json_value."));
        return schema;
    }

    internal static string Verify(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var checks = args.Array("checks");
        if (checks.Count is < 1 or > 24 || checks.Any(check => check is not JsonObject))
            throw new ToolException("Нужен массив из 1–24 корректных условий проверки.");
        var reports = new List<string>();
        int passed = 0;
        foreach (var check in checks.OfType<JsonObject>())
        {
            ct.ThrowIfCancellationRequested();
            var item = new ToolArgs(check);
            var kind = item.Required("kind");
            var full = PathHelper.FinalPath(context.Resolve(item.Required("path")));
            context.GuardSecrets(full);
            bool present = File.Exists(full), directory = Directory.Exists(full);
            bool result;
            string actual;
            if (kind == "exists") { result = present || directory; actual = result ? "существует" : "отсутствует"; }
            else if (kind == "absent") { result = !present && !directory; actual = result ? "отсутствует" : "существует"; }
            else
            {
                if (!present) { reports.Add($"FAIL {kind}: {full} (файл не найден)"); continue; }
                var info = new FileInfo(full);
                if (info.Length > 8_000_000 && kind is not ("sha256" or "min_bytes" or "max_bytes"))
                {
                    reports.Add($"UNKNOWN {kind}: {full} (файл слишком большой для проверки)");
                    continue;
                }
                var expected = item.Required("expected", allowEmpty: true);
                switch (kind)
                {
                    case "sha256":
                        using (var stream = File.OpenRead(full)) actual = Convert.ToHexString(SHA256.HashData(stream));
                        result = string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
                        break;
                    case "min_bytes": case "max_bytes":
                        if (!long.TryParse(expected, NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) || bytes < 0)
                            throw new ToolException("Для ограничения размера укажите неотрицательное число байт.");
                        actual = info.Length.ToString(CultureInfo.InvariantCulture);
                        result = kind == "min_bytes" ? info.Length >= bytes : info.Length <= bytes;
                        break;
                    case "contains": case "not_contains":
                        if (expected.Length is < 1 or > 1_000) throw new ToolException("Ожидаемая подстрока должна занимать от 1 до 1000 символов.");
                        var text = File.ReadAllText(full);
                        var match = text.Contains(expected, StringComparison.Ordinal);
                        actual = match ? "найдено" : "не найдено";
                        result = kind == "contains" ? match : !match;
                        break;
                    case "line_count": case "csv_rows":
                        if (!int.TryParse(expected, NumberStyles.None, CultureInfo.InvariantCulture, out int lines) || lines < 0)
                            throw new ToolException("Для подсчёта строк укажите неотрицательное целое число.");
                        int count = 0;
                        if (kind == "line_count")
                        {
                            foreach (var _ in File.ReadLines(full)) { ct.ThrowIfCancellationRequested(); count++; }
                        }
                        else
                        {
                            // Count actual CSV records including quoted multiline fields; exclude the header.
                            bool quoted = false, any = false, first = true;
                            using var reader = File.OpenText(full);
                            int ch;
                            while ((ch = reader.Read()) >= 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                if (ch == '"')
                                {
                                    if (quoted && reader.Peek() == '"') { reader.Read(); any = true; }
                                    else quoted = !quoted;
                                }
                                else if (!quoted && ch == '\n')
                                {
                                    if (!first && any) count++;
                                    first = false; any = false;
                                }
                                else if (ch != '\r') any = true;
                            }
                            if (quoted) throw new ToolException("Некорректные CSV-кавычки.");
                            if (!first && any) count++;
                        }
                        actual = count.ToString(CultureInfo.InvariantCulture);
                        result = count == lines;
                        break;
                    case "json_value":
                        var key = item.Required("key");
                        if (key.Length > 200 || key.Split('.').Any(part => !Regex.IsMatch(part, "^[A-Za-z_][A-Za-z0-9_-]*$")))
                            throw new ToolException("Путь JSON должен состоять из простых имён полей, разделённых точкой.");
                        JsonNode? node = JsonNode.Parse(File.ReadAllText(full));
                        foreach (var part in key.Split('.')) node = node is JsonObject obj ? obj[part] : null;
                        actual = node?.ToString() ?? "null";
                        result = node != null && string.Equals(actual, expected, StringComparison.Ordinal);
                        break;
                    default: throw new ToolException("Неизвестный вид проверки: " + kind);
                }
            }
            if (result) passed++;
            reports.Add($"{(result ? "PASS" : "FAIL")} {kind}: {full} (получено: {actual})");
        }
        string report = $"Контракт: {passed}/{checks.Count} обязательных условий пройдено.\n" + string.Join("\n", reports);
        if (passed != checks.Count) throw new ToolException("Контракт не выполнен. " + report);
        return report;
    }
}
