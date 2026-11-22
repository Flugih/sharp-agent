using static SharpAgent.Ui.UiLanguage;

namespace SharpAgent.Tools;

internal static class LocalContractEngine
{
    internal const string Name = "local_contract";
    internal enum State { Passed, Failed, Blocked, NotVerified }
    internal sealed record Check(string Kind, string Path, string Expected, string Key, bool Optional);
    internal sealed record Result(Check Check, State State, string Detail);

    internal static AgentTool Tool() => new()
    {
        Name = Name,
        Description = Tr("Локальная проверка результата. exec_command, сборка и тесты проверяются по фактическому результату текущей задачи.",
            "Verify outcomes locally. Commands, builds and tests require observed results from the current task."),
        Parameters = ParameterSchema(),
        Handler = AgentTool.Sync((arguments, context, ct) => Verify(arguments, context, ct)),
    };

    static JsonObject ParameterSchema()
    {
        var schema = Schema.Object(new Prop("checks", "array", Tr("Условия успеха.", "Acceptance checks."), true));
        var checks = (JsonObject)schema["properties"]!["checks"]!;
        checks["minItems"] = 1;
        checks["maxItems"] = 24;
        checks["items"] = Schema.Object(new Prop("kind", "string", Tr("Тип проверки.", "Check kind."), true,
            ["exists", "absent", "file", "directory", "sha256", "contains", "not_contains", "min_bytes", "max_bytes", "line_count",
             "csv_rows", "json_value", "build", "unit_tests", "ui_tests", "command_result"]),
            new Prop("path", "string", Tr("Путь к файлу или папке.", "File or directory path.")),
            new Prop("expected", "string", Tr("Ожидаемое значение или фрагмент.", "Expected value or excerpt.")),
            new Prop("key", "string", Tr("Путь свойства JSON, идентификатор UI-теста или точная выполненная команда.", "JSON property, UI test ID or exact command that ran.")),
            new Prop("optional", "boolean", Tr("Необязательное условие.", "Optional check.")));
        return schema;
    }

    internal static Check[] Parse(ToolArgs args)
    {
        var checks = args.Array("checks");
        if (checks.Count is < 1 or > 24 || checks.Any(check => check is not JsonObject))
            throw new ToolException(Tr("Нужен массив из 1–24 условий.", "Provide an array of 1–24 checks."));
        return checks.OfType<JsonObject>().Select(value =>
        {
            var item = new ToolArgs(value);
            return new Check(item.Required("kind"), item.Optional("path") ?? "", item.Optional("expected") ?? "",
                item.Optional("key") ?? "", item.Bool("optional"));
        }).ToArray();
    }

    internal static string Verify(ToolArgs args, ToolContext context, CancellationToken ct, Func<Check, Result?>? evidence = null)
    {
        var results = Assess(Parse(args), context, ct, evidence);
        var report = Report(results);
        if (results.Any(Unmet)) throw new ToolException(Tr("Контракт не выполнен. ", "The contract was not met. ") + report);
        return report;
    }

    internal static Result[] Assess(IEnumerable<Check> checks, ToolContext context, CancellationToken ct, Func<Check, Result?>? evidence = null)
    {
        var results = new List<Result>();
        foreach (var check in checks)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add(check.Kind is "build" or "unit_tests" or "ui_tests" or "command_result"
                    ? evidence?.Invoke(check) ?? new(check, State.NotVerified, Tr("Проверка ещё не выполнялась.", "There is no observed result."))
                    : FileCheck(check, context, ct));
            }
            catch (UnauthorizedAccessException) { results.Add(new(check, State.Blocked, Tr("Доступ запрещён.", "Access was denied."))); }
            catch (ToolException ex) { results.Add(new(check, State.Blocked, ex.Message)); }
            catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
            { results.Add(new(check, State.Failed, Tr("Файл повреждён или его нельзя прочитать.", "The file is invalid or unreadable."))); }
        }
        return [.. results];
    }

    internal static bool Unmet(Result result) => !result.Check.Optional && result.State != State.Passed;

    internal static string Report(IReadOnlyList<Result> results) =>
        Tr($"Контракт: {results.Count(result => result.State == State.Passed)}/{results.Count} условий пройдено.",
            $"Contract: {results.Count(result => result.State == State.Passed)}/{results.Count} checks passed.") + "\n" +
        string.Join("\n", results.Select(result =>
            $"{Status(result.State)} · {result.Check.Kind} {result.Check.Path} · {result.Detail}"));

    internal static string Status(State state) => state == State.NotVerified ? "not_verified" : state.ToString().ToLowerInvariant();

    static Result FileCheck(Check check, ToolContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(check.Path))
            return new(check, State.NotVerified, Tr("Укажите путь.", "Specify a path."));
        var path = PathHelper.FinalPath(context.Resolve(check.Path));
        context.GuardSecrets(path);
        bool file = File.Exists(path), directory = Directory.Exists(path);
        if (check.Kind is "exists" or "absent" or "file" or "directory")
        {
            bool exists = check.Kind switch { "file" => file, "directory" => directory, _ => file || directory };
            return new(check, exists != (check.Kind == "absent") ? State.Passed : State.Failed,
                exists ? Tr("существует", "exists") : Tr("отсутствует", "does not exist"));
        }
        if (!file) return new(check, State.Failed, Tr("Файл не найден.", "File not found."));
        var size = new FileInfo(path).Length;
        if (check.Kind is "min_bytes" or "max_bytes")
        {
            if (!long.TryParse(check.Expected, NumberStyles.None, CultureInfo.InvariantCulture, out long limit))
                return new(check, State.NotVerified, Tr("Укажите число байт.", "Specify a byte count."));
            return new(check, (check.Kind == "min_bytes" ? size >= limit : size <= limit) ? State.Passed : State.Failed,
                size.ToString(CultureInfo.InvariantCulture));
        }
        if (size > 20_000_000) return new(check, State.NotVerified, Tr("Файл превышает лимит анализа.", "The file exceeds the analysis limit."));
        if (check.Kind == "sha256")
        {
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return new(check, hash.Equals(check.Expected, StringComparison.OrdinalIgnoreCase) ? State.Passed : State.Failed, hash);
        }
        if (check.Kind is "contains" or "not_contains")
        {
            if (check.Expected.Length is < 1 or > 1_000)
                return new(check, State.NotVerified, Tr("Длина фрагмента должна быть от 1 до 1000 символов.", "The expected excerpt must be 1–1000 characters."));
            var document = LocalResearchEngine.Extract(path, ct);
            bool found = document.Text.Contains(check.Expected, StringComparison.Ordinal);
            if (!found && document.Status != "ready")
                return new(check, State.NotVerified, Tr("Содержимое извлечено частично.", "Only part of the content was extracted."));
            return new(check, found == (check.Kind == "contains") ? State.Passed : State.Failed,
                found ? Tr("фрагмент найден", "excerpt found") : Tr("фрагмент отсутствует", "excerpt not found"));
        }
        var text = TextFileIO.Read(path).Text;
        if (check.Kind == "json_value")
        {
            if (check.Key.Length is < 1 or > 200) return new(check, State.NotVerified, Tr("Укажите путь свойства JSON.", "Specify a JSON property."));
            using var doc = JsonDocument.Parse(text);
            var node = doc.RootElement;
            foreach (var part in check.Key.Split('.'))
            {
                if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(part, out var next)) node = next;
                else if (node.ValueKind == JsonValueKind.Array && int.TryParse(part, out int item) && item >= 0 && item < node.GetArrayLength()) node = node[item];
                else return new(check, State.Failed, Tr("Свойство JSON не найдено.", "The JSON property does not exist."));
            }
            var actual = node.ValueKind is JsonValueKind.String ? node.GetString() : node.GetRawText();
            return new(check, actual == check.Expected ? State.Passed : State.Failed, Tr("Значение сравнено.", "Value compared."));
        }
        if (check.Kind is "line_count" or "csv_rows")
        {
            if (!int.TryParse(check.Expected, NumberStyles.None, CultureInfo.InvariantCulture, out int expected))
                return new(check, State.NotVerified, Tr("Укажите количество строк.", "Specify the row count."));
            using var reader = new StringReader(text);
            int count = check.Kind == "csv_rows" ? LocalResearchEngine.CsvRows(text, Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase)
                ? "\t" : ",", ct).Skip(1).Count() : 0;
            if (check.Kind == "line_count")
                while (reader.ReadLine() != null) { ct.ThrowIfCancellationRequested(); count++; }
            return new(check, count == expected ? State.Passed : State.Failed, count.ToString(CultureInfo.InvariantCulture));
        }
        return new(check, State.NotVerified, Tr("Неизвестный тип проверки.", "Unknown check kind."));
    }
}
