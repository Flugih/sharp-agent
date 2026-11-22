namespace SharpAgent.Tools;

static partial class ScriptRegistry
{
    public const string Name = "local_script";
    const int SourceLimit = 12_000;
    sealed record Script(string Id, string Title, string Extension, string Hash, string Version, string Permissions,
        string Result, string[] Parameters, int Timeout, DateTimeOffset Approved);

    internal static AgentTool Tool() => new()
    {
        Name = Name,
        Description = "Реестр локальных сценариев. list показывает сценарии, register сохраняет просмотренный пользователем файл, run запускает сценарий по ID после подтверждения. Поддерживаются PS1, CS, PY и JS.",
        Parameters = Schema.Object(
            new Prop("action", "string", "Действие.", true, ["list", "register", "run"]),
            new Prop("id", "string", "Уникальное имя из букв ASCII, цифр, - и _."),
            new Prop("source", "string", "Путь к исходному файлу при регистрации."),
            new Prop("title", "string", "Описание сценария."),
            new Prop("version", "string", "Версия сценария."),
            new Prop("permissions", "string", "Какие файлы, сеть и другие ресурсы использует."),
            new Prop("result", "string", "Формат ожидаемого результата."),
            new Prop("parameters", "string", "Параметры по порядку через запятую. Параметры с префиксом path_ проверяются как пути."),
            new Prop("arguments", "object", "Значения параметров при запуске."),
            new Prop("timeout_seconds", "integer", "Предел выполнения в секундах, максимум 120.")),
        Confirmation = Confirm,
        Handler = Execute,
    };

    static ConfirmRequest? Confirm(ToolArgs args, ToolContext context)
    {
        if (args.Required("action") == "list") return null;
        var (script, code) = args.Required("action") == "register" ? Candidate(args, context) : Open(context, Id(args));
        string action = args.Required("action") == "register" ? "Сохранить" : "Запустить";
        return new($"{action} локальный сценарий?", $"{script.Title} ({script.Extension}, {script.Version})\n" +
            $"Доступ: {script.Permissions}\nРезультат: {script.Result}\nДанные: {args.Object("arguments").ToJsonString()}\n\n{code}",
            action, Danger: true, Note: "Сценарий выполняется от имени текущего пользователя. Таймаут не ограничивает доступ к файлам или сети.", AlwaysAsk: true,
            Revision: Revision(script));
    }

    static async Task<string> Execute(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        if (context.SandboxRoot != null || context.AppFolder == null)
            throw new ToolException("Реестр сценариев недоступен в песочнице.");
        switch (args.Required("action"))
        {
            case "list":
                var folder = Path.Combine(context.AppFolder, "scripts", "registry");
                return !Directory.Exists(folder) ? "Сценариев пока нет." : string.Join("\n", Directory.EnumerateFiles(folder, "*.json").Take(100)
                    .Select(file => JsonSerializer.Deserialize<Script>(File.ReadAllText(file)))
                    .OfType<Script>().Select(script => $"{script.Id}: {script.Title} ({script.Extension}, {script.Version})"));
            case "register":
                var (script, code) = Candidate(args, context);
                Approved(args, script);
                var (manifest, source) = Paths(context, script.Id, script.Hash);
                Directory.CreateDirectory(Path.GetDirectoryName(source)!);
                if (!File.Exists(source)) await File.WriteAllTextAsync(source, code, ct);
                Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
                var temp = manifest + "." + Guid.NewGuid().ToString("N");
                try
                {
                    await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(script, Pretty), ct);
                    File.Move(temp, manifest, true);
                }
                finally { try { File.Delete(temp); } catch (IOException) { } }
                return $"Сценарий «{script.Id}» сохранён. Синтаксис проверяется перед каждым запуском.";
            case "run":
                var (approved, text) = Open(context, Id(args));
                Approved(args, approved);
                return await RunAsync(approved, text, args, context, ct);
            default:
                throw new ToolException("Неизвестное действие реестра сценариев.");
        }
    }

    static void Approved(ToolArgs args, Script script)
    {
        if (args.ConfirmedRevision != Revision(script)) throw new ToolException("Содержимое изменилось после предпросмотра. Подтвердите заново.");
    }

    static string Revision(Script script) => Hash(JsonSerializer.Serialize(script with { Approved = default }));

    static (Script Script, string Code) Candidate(ToolArgs args, ToolContext context)
    {
        var path = context.Resolve(args.Required("source"));
        context.GuardSecrets(path);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".ps1" or ".cs" or ".py" or ".js")) throw new ToolException("Неизвестный язык сценария.");
        var code = TextFileIO.TryRead(path, SourceLimit * 4);
        if (code is not { Length: > 0 and <= SourceLimit }) throw new ToolException($"Нужен текстовый сценарий до {SourceLimit} символов.");
        var parameters = (args.Optional("parameters") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parameters.Length > 16 || parameters.Distinct(StringComparer.OrdinalIgnoreCase).Count() != parameters.Length || parameters.Any(p => !Identifier().IsMatch(p)))
            throw new ToolException("Имена параметров должны быть уникальными и состоять из букв ASCII, цифр, - и _.");
        return (new(Id(args), args.Required("title"), extension, Hash(code), args.Required("version"), args.Required("permissions"),
            args.Required("result"), parameters, Math.Clamp(args.Int("timeout_seconds", 60), 1, 120), DateTimeOffset.UtcNow), code);
    }

    static (Script Script, string Code) Open(ToolContext context, string id)
    {
        var (manifest, _) = Paths(context, id, "");
        if (!File.Exists(manifest) || JsonSerializer.Deserialize<Script>(File.ReadAllText(manifest)) is not { } script ||
            script.Id != id || !Regex.IsMatch(script.Hash, "^[A-F0-9]{64}$") || script.Extension is not (".ps1" or ".cs" or ".py" or ".js"))
            throw new ToolException("Сценарий не найден.");
        var (_, source) = Paths(context, id, script.Hash);
        var code = TextFileIO.TryRead(source, SourceLimit * 4);
        if (code == null || Hash(code) != script.Hash) throw new ToolException("Контрольная сумма сценария изменилась. Зарегистрируйте его снова.");
        return (script, code);
    }

    static async Task<string> RunAsync(Script script, string code, ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var supplied = args.Object("arguments");
        if (supplied.Count != script.Parameters.Length) throw new ToolException("Передайте все объявленные параметры сценария.");
        var values = script.Parameters.Select(key =>
        {
            if (supplied[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || text.Length > 2_000 ||
                text.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new ToolException("Неверное значение параметра " + key + ".");
            if (!key.StartsWith("path_", StringComparison.OrdinalIgnoreCase)) return Quote(text);
            var path = context.Resolve(text);
            context.GuardSecrets(path);
            context.GuardAppFiles(path);
            return Quote(path);
        }).ToArray();
        var folder = Path.Combine(context.TempFolder, "script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "Program" + script.Extension);
            await File.WriteAllTextAsync(source, code, new UTF8Encoding(script.Extension == ".ps1"), ct);
            var file = Quote(source);
            var extra = string.Join(" ", values);
            var command = script.Extension switch
            {
                ".ps1" => "$tokens=$null; $errors=$null\n[void][System.Management.Automation.Language.Parser]::ParseFile(" +
                    file + ",[ref]$tokens,[ref]$errors)\nif($errors){$errors; exit 1}\n& " + file + " " + extra + "\nif (-not $?) { exit 1 }",
                ".py" => "& python -I -c 'import ast, sys, tokenize; ast.parse(tokenize.open(sys.argv[1]).read())' " + file +
                    "\nif($LASTEXITCODE -ne 0){exit $LASTEXITCODE}\n& python -I " + file + " " + extra,
                ".js" => "& node --check " + file + "\nif($LASTEXITCODE -ne 0){exit $LASTEXITCODE}\n& node " + file + " " + extra,
                _ => "& dotnet build " + Quote(Path.Combine(folder, "Script.csproj")) +
                    " -nologo -p:NuGetAudit=false -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -p:ManagePackageVersionsCentrally=false" +
                    "\nif($LASTEXITCODE -ne 0){exit $LASTEXITCODE}\n& dotnet " +
                    Quote(Path.Combine(folder, "bin", "Debug", "net8.0", "Script.dll")) + " " + extra,
            };
            if (script.Extension == ".cs")
                await File.WriteAllTextAsync(Path.Combine(folder, "Script.csproj"),
                    """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>""", ct);
            return await ShellTools.RunPowerShellAsync("$ErrorActionPreference='Stop'\n" + command, context, ct, script.Timeout);
        }
        finally { try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    static string Id(ToolArgs args) => args.Required("id").ToLowerInvariant() is var id && Identifier().IsMatch(id) ? id
        : throw new ToolException("Недопустимый идентификатор сценария.");

    static (string Manifest, string Source) Paths(ToolContext context, string id, string hash)
    {
        var root = Path.Combine(context.AppFolder!, "scripts");
        return (Path.Combine(root, "registry", id + ".json"), Path.Combine(root, "verified", hash + ".source"));
    }

    static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
    static string Quote(string value) => "'" + Regex.Replace(value, "['\u2018\u2019\u201a\u201b]", "$0$0") + "'";

    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex Identifier();
}
