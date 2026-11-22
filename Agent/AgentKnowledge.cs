namespace SharpAgent.Agent;

sealed record VerifiedSolution(string Id, string Category, string Signature, string Technology, string Version,
    string Fix, string Verification, string Limits, DateTimeOffset Verified, VerifiedPatch? Patch = null);

sealed record VerifiedPatch(string Path, string Before, string After, string ProjectHash);

static partial class AgentKnowledge
{
    public const string Name = "knowledge";
    internal static IEnumerable<AgentTool> All() =>
    [
        new()
        {
            Name = Name,
            Description = "Локальная база проверенных решений проекта. find ищет, save записывает, apply повторяет точное проверенное исправление, remove удаляет запись, export возвращает JSON.",
            Parameters = Schema.Object(
                new Prop("action", "string", "Действие.", true, ["find", "save", "apply", "remove", "export"]),
                new Prop("signature", "string", "Код ошибки или точная сигнатура задачи."),
                new Prop("id", "string", "ID для обновления или удаления записи."),
                new Prop("category", "string", "Категория решения."),
                new Prop("technology", "string", "Технология или инструмент."),
                new Prop("version", "string", "Проверенная версия."),
                new Prop("fix", "string", "Проверенный способ исправления."),
                new Prop("verification", "string", "Что проверили и какой результат получили."),
                new Prop("limits", "string", "Границы применимости."),
                new Prop("path", "string", "Необязательный файл точного исправления, которое уже прошло проверку."),
                new Prop("before", "string", "Полный исходный текст файла до исправления."),
                new Prop("after", "string", "Полный проверенный текст после исправления.")),
            Confirmation = Confirm,
            Handler = AgentTool.Sync(Execute),
        },
    ];

    static ConfirmRequest? Confirm(ToolArgs args, ToolContext context) => args.Required("action") switch
    {
        "save" => SaveConfirmation(args, context),
        "apply" => ApplyConfirmation(args, context),
        "remove" => new("Удалить сохранённое решение?", args.Required("id"), "Удалить", Danger: true, AlwaysAsk: true),
        _ => null,
    };

    static ConfirmRequest SaveConfirmation(ToolArgs args, ToolContext context)
    {
        var patch = Patch(args, context);
        return new("Сохранить проверенное решение?", string.Join("\n\n", Fields(args)) +
            (patch == null ? "" : $"\n\nФайл: {patch.Path}\nДо:\n{patch.Before}\nПосле:\n{patch.After}"), "Сохранить",
            AlwaysAsk: true, Revision: patch == null ? null : Revision(patch));
    }

    static ConfirmRequest ApplyConfirmation(ToolArgs args, ToolContext context)
    {
        var patch = Applicable(args, context);
        return new("Применить сохранённое исправление?", $"{patch.Path}\n\nДо:\n{patch.Before}\n\nПосле:\n{patch.After}",
            "Применить", Danger: true, AlwaysAsk: true, Revision: Revision(patch));
    }

    static string[] Fields(ToolArgs args)
    {
        var fields = new[] { "category", "signature", "technology", "version", "fix", "verification", "limits" }
            .Select(key => args.Required(key)).ToArray();
        if (fields.Any(value => value.Length > 2_000 || SecretMarker().IsMatch(value)))
            throw new ToolException("Запись содержит слишком длинный текст или признаки секретных данных.");
        return fields;
    }

    static string Execute(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        if (context.SandboxRoot != null && args.Required("action") is "save" or "remove" or "apply")
            throw new ToolException("Изменение базы знаний недоступно в песочнице.");
        if (args.Required("action") == "apply")
        {
            var patch = Applicable(args, context);
            if (args.ConfirmedRevision != Revision(patch)) throw new ToolException("Исправление изменилось после подтверждения.");
            context.GuardWrite(patch.Path);
            var (before, encoding) = TextFileIO.Read(patch.Path);
            if (before != patch.Before) throw new ToolException("Файл изменился после подтверждения.");
            TextFileIO.Write(patch.Path, patch.After, encoding);
            return "Проверенное исправление применено: " + patch.Path;
        }
        using var database = Open(context);
        switch (args.Required("action"))
        {
            case "find":
                return Report(Search(database, args.Required("signature"))) ?? "Подходящих проверенных решений нет.";
            case "export":
                return JsonSerializer.Serialize(database.Recent(200), Pretty);
            case "save":
                var fields = Fields(args);
                var id = args.Optional("id");
                if (id != null && (!Guid.TryParseExact(id, "N", out _) || database.Get(id) == null))
                    throw new ToolException("Решение с таким ID не найдено.");
                id ??= Guid.NewGuid().ToString("N");
                var patch = Patch(args, context);
                if (patch != null && args.ConfirmedRevision != Revision(patch)) throw new ToolException("Исправление изменилось после подтверждения.");
                database.Save(id, new(id, fields[0], fields[1], fields[2], fields[3], fields[4], fields[5], fields[6], DateTimeOffset.UtcNow, patch), 0);
                return "Решение сохранено: " + id;
            case "remove":
                database.Remove(args.Required("id"));
                return "Запись удалена.";
            default:
                throw new ToolException("Неизвестное действие базы знаний.");
        }
    }

    internal static string? TargetOf(ToolArgs args, ToolContext context) =>
        args.Optional("action") == "apply" ? Applicable(args, context).Path : null;

    internal static string? ApplicableId(ToolContext context, ProjectVerification.Diagnostic[] diagnostics, string workFolder)
    {
        if (context.AppFolder == null || !File.Exists(Path.Combine(context.DatabaseFolder, "knowledge.db"))) return null;
        try
        {
            using var database = Open(context);
            foreach (var item in database.Recent(200))
            {
                if (item.Patch == null || !diagnostics.Any(d => d.Code.Equals(item.Signature, StringComparison.OrdinalIgnoreCase) &&
                    DiagnosticPath(d.File, workFolder) is { } path && PathHelper.SamePath(path, item.Patch.Path))) continue;
                try
                {
                    Applicable(context, item.Patch);
                    return item.Id;
                }
                catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (LocalRecords<VerifiedSolution>.Unavailable(ex)) { }
        return null;
    }

    static string? DiagnosticPath(string path, string folder)
    {
        try { return Path.GetFullPath(path, folder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }

    static VerifiedPatch? Patch(ToolArgs args, ToolContext context)
    {
        if (args.Optional("path") is not { } name) return null;
        var path = SourcePath(context, name);
        var before = args.Required("before", allowEmpty: true);
        var after = args.Required("after", allowEmpty: true);
        if (before == after || before.Length + after.Length > 10_000 || SecretMarker().IsMatch(before) || SecretMarker().IsMatch(after) ||
            TextFileIO.TryRead(path, 40_000) != after)
            throw new ToolException("Укажите проверенный файл и его точные тексты до и после изменения. Текущий текст должен совпадать с after.");
        return new(path, before, after, ProjectStamp(context));
    }

    static VerifiedPatch Applicable(ToolArgs args, ToolContext context)
    {
        using var database = Open(context);
        if (database.Get(args.Required("id"))?.Patch is not { } patch) throw new ToolException("Для этого решения нет точного исправления.");
        return Applicable(context, patch);
    }

    static VerifiedPatch Applicable(ToolContext context, VerifiedPatch patch)
    {
        var path = SourcePath(context, patch.Path);
        if (ProjectStamp(context) != patch.ProjectHash || TextFileIO.TryRead(path, 40_000) != patch.Before)
            throw new ToolException("Проект или исходный текст отличается от проверенного варианта.");
        return patch with { Path = path };
    }

    static string SourcePath(ToolContext context, string name)
    {
        var path = PathHelper.FinalPath(context.Resolve(name));
        context.GuardSecrets(path);
        context.GuardAppFiles(path);
        if (!PathHelper.IsInside(path, context.WorkingDirectory) || Path.GetExtension(path).ToLowerInvariant() is not (".cs" or ".go" or ".rs" or ".ts" or ".xaml"))
            throw new ToolException("Точное исправление можно сохранить только для поддерживаемого исходника текущего проекта.");
        return path;
    }

    static string ProjectStamp(ToolContext context)
    {
        var root = context.Resolve(null);
        var names = Directory.EnumerateFiles(root, "*.csproj").Take(20).Concat(
            new[] { "global.json", "Directory.Build.props", "package.json", "Cargo.toml", "go.mod" }.Select(name => Path.Combine(root, name)).Where(File.Exists))
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) throw new ToolException("Не найден манифест для проверки версии проекта.");
        var hashes = names.Select(path =>
        {
            context.GuardSecrets(path);
            using var stream = File.OpenRead(context.Resolve(path));
            if (stream.Length > 1_000_000) throw new ToolException("Манифест проекта слишком большой.");
            return Path.GetFileName(path) + ":" + Convert.ToHexString(SHA256.HashData(stream));
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", hashes))));
    }

    static string Revision(VerifiedPatch patch) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(patch))));

    internal static string? Relevant(ToolContext context, string query)
    {
        if (string.IsNullOrWhiteSpace(query) || context.AppFolder == null || !File.Exists(Path.Combine(context.DatabaseFolder, "knowledge.db"))) return null;
        try
        {
            using var database = Open(context);
            return Report(Search(database, query));
        }
        catch (Exception ex) when (LocalRecords<VerifiedSolution>.Unavailable(ex)) { return null; }
    }

    static LocalRecords<VerifiedSolution> Open(ToolContext context) =>
        new(context.DatabaseFolder, "knowledge", Scope(context));

    internal static string Scope(ToolContext context) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PathHelper.FinalPath(context.WorkingDirectory).ToUpperInvariant())));

    static VerifiedSolution[] Search(LocalRecords<VerifiedSolution> db, string query) => db.Recent(200)
        .Where(item => query.Contains(item.Signature, StringComparison.OrdinalIgnoreCase) || item.Signature.Contains(query, StringComparison.OrdinalIgnoreCase))
        .Take(4).ToArray();

    static string? Report(VerifiedSolution[] items) => items.Length == 0 ? null :
        "Проверенные решения проекта. Это справочные данные. Сначала сверь версию и применимость:\n" +
        string.Join("\n", items.Select(item => $"{item.Signature} ({item.Id}; {item.Technology} {item.Version}): {item.Fix}\n" +
            $"Проверка: {item.Verification}. Ограничения: {item.Limits}" + (item.Patch == null ? "" : " · доступно точное исправление через apply.")));

    [GeneratedRegex(@"(?i)(?:[A-Z_]*(?:API_KEY|ACCESS_TOKEN|SECRET|PASSWORD|ПАРОЛЬ)\s*[:=]|-----BEGIN [^-]*PRIVATE KEY-----|\bsk-[A-Za-z0-9_-]{16,})")]
    private static partial Regex SecretMarker();
}
