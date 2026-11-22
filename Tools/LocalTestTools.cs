namespace SharpAgent.Tools;

static partial class LocalTestTools
{
    public const string Name = "local_tests";
    internal static bool IsUiResult(string output) => output.StartsWith("Проверок пройдено:", StringComparison.Ordinal) ||
        output.StartsWith("UI checks passed:", StringComparison.Ordinal) || output.StartsWith("Ошибка: UI-тест не пройден.", StringComparison.Ordinal) ||
        output.StartsWith("Ошибка: UI test failed.", StringComparison.Ordinal);
    sealed record Check(string Target, string? Selector, string? Text, string? State);
    sealed record Suite(string Mode, Check[] Checks);

    internal static AgentTool Tool() => new()
    {
        Name = Name,
        Description = Tr("Локальные тесты: cases предлагает граничные входы, generate сохраняет исполняемый C#-тест, coverage читает Cobertura, save сохраняет UI-проверки, run запускает сохранённый тест.",
            "Local testing: cases suggests edge cases, generate creates a runnable C# contract test, coverage reads Cobertura, save stores UI checks and run executes the saved test."),
        Parameters = Schema.Object(
            new Prop("action", "string", Tr("Действие.", "Action."), true, ["cases", "generate", "coverage", "save", "run"]),
            new Prop("signature", "string", Tr("Сигнатура вида Method(int n, string text, bool enabled).", "Signature such as Method(int n, string text, bool enabled).")),
            new Prop("path", "string", Tr("Путь к Cobertura XML.", "Path to Cobertura XML.")),
            new Prop("method", "string", Tr("Метод для отчёта покрытия.", "Method to report coverage for.")),
            new Prop("id", "string", Tr("ID сценария из букв ASCII, цифр, - и _.", "Scenario ID using ASCII letters, digits, - or _.")),
            new Prop("mode", "string", Tr("Сайт или окно.", "Website or window."), Enum: ["browser", "window"]),
            new Prop("suite", "object", Tr("Набор checks с полями target, selector, text, state.", "A checks array with target, selector, text and state.")),
            new Prop("contract", "object", Tr("Контракт с assembly, type, method, parameters, returns и cases (args и expected либо throws).",
                "Contract with assembly, type, method, parameters, returns and cases (args and expected or throws)."))),
        Confirmation = Preview,
        Handler = Execute,
    };

    static async Task<string> Execute(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        switch (args.Required("action"))
        {
            case "cases":
                return Cases(args.Required("signature"));
            case "coverage":
                return Coverage(args, context);
            case "generate":
                var contract = ContractTestSource.Generate(args.Object("contract"), context);
                var destination = SuitePath(args, context);
                if (File.Exists(destination)) throw new ToolException("Это имя уже занято проверками интерфейса.");
                await SaveAsync(destination + ".contract", contract, ct);
                return "Тест сгенерирован: " + args.Required("id") + "\n" + contract.Source;
            case "save":
                var suite = ParseSuite(args);
                var path = SuitePath(args, context);
                if (File.Exists(path + ".contract")) throw new ToolException("Это имя уже занято тестом по контракту.");
                await SaveAsync(path, suite, ct);
                return "Набор проверок сохранён: " + args.Required("id");
            case "run":
                var saved = SuitePath(args, context);
                if (ReadContract(saved) is { } generated)
                {
                    CheckContractExecution(context);
                    if (args.ConfirmedRevision != Revision(generated, context)) throw new ToolException("Тест или сборка изменились после подтверждения.");
                    return await RunContractAsync(generated, context, ct);
                }
                if (!File.Exists(saved) || JsonSerializer.Deserialize<Suite>(await File.ReadAllTextAsync(saved, ct)) is not { } test)
                    throw new ToolException("Такой набор проверок не найден.");
                Validate(test);
                return await Replay(test, args.BrowserAllowed, context, ct);
            default:
                throw new ToolException("Неизвестное действие тестов.");
        }
    }

    static async Task SaveAsync<T>(string path, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, Pretty);
        if (value is ContractTestSource.Contract && Encoding.UTF8.GetByteCount(json) > 240_000)
            throw new ToolException("Исполняемый тест превышает предел размера. Разделите случаи на несколько тестов.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, json, ct);
            File.Move(temp, path, true);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    static ContractTestSource.Contract? ReadContract(string path) =>
        TextFileIO.TryRead(path + ".contract", 240_000) is { } json ? JsonSerializer.Deserialize<ContractTestSource.Contract>(json) : null;

    static ConfirmRequest? Preview(ToolArgs args, ToolContext context)
    {
        if (args.Optional("action") != "run" || ReadContract(SuitePath(args, context)) is not { } contract) return null;
        CheckContractExecution(context);
        return new("Запустить тест по контракту?", contract.Assembly + "\n\n" + contract.Source, "Запустить",
            Danger: true, Note: "Тест загружает сборку проекта и выполняет метод от имени текущего пользователя.",
            AlwaysAsk: true, Revision: Revision(contract, context));
    }

    static void CheckContractExecution(ToolContext context)
    {
        if (context.SandboxRoot != null) throw new ToolException("Запуск сборки по контракту отключён в режиме песочницы.");
    }

    static string Revision(ContractTestSource.Contract contract, ToolContext context)
    {
        var assembly = ContractTestSource.Library(contract.Assembly, context);
        using var input = File.OpenRead(assembly);
        return Convert.ToHexString(SHA256.HashData(input)) + ":" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(contract.Source + "\n" + assembly)));
    }

    static async Task<string> RunContractAsync(ContractTestSource.Contract contract, ToolContext context, CancellationToken ct)
    {
        var root = Path.Combine(context.TempFolder, "contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = Path.Combine(root, "Contract.csproj");
            await File.WriteAllTextAsync(project,
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""", ct);
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), contract.Source, ct);
            static string Quote(string value) => "'" + Regex.Replace(value, "['\u2018\u2019\u201a\u201b]", "$0$0") + "'";
            return await ShellTools.RunPowerShellAsync("& dotnet run --project " + Quote(project) +
                " -p:NuGetAudit=false -p:RestoreSources= -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false -- " +
                Quote(contract.Assembly), context, ct, 90);
        }
        finally { try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    internal static string Cases(string signature)
    {
        int opening = signature.IndexOf('('), closing = signature.LastIndexOf(')');
        if (opening < 1 || closing <= opening || closing - opening > 300)
            throw new ToolException("Ожидается простая сигнатура, например Method(int count, string text).");
        var parameters = signature[(opening + 1)..closing].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Parameter().Match(value)).ToArray();
        if (parameters.Length > 10 || parameters.Any(match => !match.Success))
            throw new ToolException("Поддерживаются простые параметры типов int, long, double, bool и string с необязательным ?.");
        static object?[] Values(string type)
        {
            var list = new List<object?>(type.TrimEnd('?') switch
            {
                "int" => new object?[] { 1, 0, -1, int.MinValue, int.MaxValue },
                "long" => new object?[] { 1L, 0L, -1L, long.MinValue, long.MaxValue },
                "double" => new object?[] { 1d, 0d, -1d, double.Epsilon, double.MaxValue },
                "bool" => new object?[] { true, false },
                _ => new object?[] { "текст", "", " ", new string('x', 256) },
            });
            if (type.EndsWith('?')) list.Add(null);
            return [.. list];
        }
        var data = parameters.Select(p => (Name: p.Groups["name"].Value, Values: Values(p.Groups["type"].Value))).ToArray();
        if (data.Select(p => p.Name).Distinct().Count() != data.Length) throw new ToolException("Имена параметров должны быть уникальными.");
        var normal = data.ToDictionary(p => p.Name, p => p.Values[0]);
        var cases = new List<Dictionary<string, object?>>() { normal };
        foreach (var parameter in data)
            foreach (var value in parameter.Values.Skip(1))
                cases.Add(new(normal) { [parameter.Name] = value });
        return "Входы для тестов. Ожидаемые результаты нужно задать по контракту метода.\n" +
            JsonSerializer.Serialize(new { Signature = signature[..(closing + 1)], Cases = cases }, Pretty);
    }

    static string Coverage(ToolArgs args, ToolContext context)
    {
        var path = context.Resolve(args.Required("path"));
        context.GuardSecrets(path);
        var xml = ChangedCoverage.Load(path);
        var method = args.Optional("method");
        var records = xml.Descendants("class").SelectMany(item => item.Descendants("method").Select(member =>
            (File: (string?)item.Attribute("filename"), Method: (string?)member.Attribute("name"), Lines: member.Descendants("line").ToArray())))
            .Where(item => method == null || item.Method?.Contains(method, StringComparison.OrdinalIgnoreCase) == true)
            .Take(40).Select(item => $"{item.File} · {item.Method}: {item.Lines.Count(line => (int?)line.Attribute("hits") > 0)}/{item.Lines.Length} строк");
        return "Покрытие по отчёту Cobertura:\n" + string.Join("\n", records);
    }

    static Suite ParseSuite(ToolArgs args)
    {
        var checks = args.Object("suite")["checks"] as JsonArray ?? [];
        if (checks.Any(check => check is not JsonObject)) throw new ToolException("Некорректная проверка.");
        var suite = new Suite(args.Required("mode"), checks.OfType<JsonObject>().Select(check =>
            new Check(Str(check["target"]) ?? "", Str(check["selector"]), Str(check["text"]), Str(check["state"]))).ToArray());
        Validate(suite);
        return suite;
    }

    static void Validate(Suite suite)
    {
        if (suite.Mode is not ("browser" or "window") || suite.Checks.Length is < 1 or > 8 ||
            suite.Checks.Any(check => string.IsNullOrWhiteSpace(check.Target) || check.Target.Length > 500 ||
                check.Selector?.Length > 200 || check.Text?.Length > 200 ||
                (suite.Mode == "browser" ? check.Selector == null && check.Text == null ||
                    !Uri.TryCreate(check.Target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0
                    : check.State is not (null or "visible" or "gone" or "enabled" or "disabled" or "checked" or "unchecked" or "selected") ||
                      check.State is not (null or "visible" or "gone") && check.Selector == null)))
            throw new ToolException("Проверьте поля набора. Допускается от одной до восьми проверок окна или страницы.");
    }

    static async Task<string> Replay(Suite suite, bool allowBrowser, ToolContext context, CancellationToken ct)
    {
        if (suite.Mode == "window" && (!context.ComputerUse || context.SandboxRoot != null))
            throw new ToolException("Проверка окон требует разрешённого Computer Use.");
        if (suite.Mode == "browser" && (context.ReadBrowser == null || context.SandboxRoot != null))
            throw new ToolException("Встроенный браузер недоступен.");
        if (suite.Mode == "browser" && !allowBrowser)
            throw new ToolException("Для запуска браузерного теста попросите агента проверить страницу или выполнить этот набор проверок.");
        var windows = new ComputerTools(automationOnly: true);
        List<string> reports = [];
        string? loaded = null;
        int passed = 0;
        foreach (var check in suite.Checks)
        {
            try
            {
                if (suite.Mode == "browser")
                {
                    var result = await context.ReadBrowser!(loaded == check.Target ? null : check.Target, check.Selector, check.Text, ct);
                    loaded = check.Target;
                    if (!result.Matches) throw new ToolException("видимый элемент или текст не найден");
                }
                else
                {
                    var request = new JsonObject
                    {
                        ["action"] = "wait", ["window"] = check.Target, ["element"] = check.Selector,
                        ["state"] = check.State ?? "visible", ["seconds"] = 4,
                    };
                    await windows.RunAsync(new(request) { BrowserAllowed = allowBrowser }, context, ct);
                }
                passed++;
                reports.Add($"✓ {check.Target} · {check.Selector ?? check.Text ?? check.State}");
            }
            catch (ToolException ex) { reports.Add($"✕ {check.Target} · {check.Selector ?? check.Text ?? check.State} · {ex.Message}"); }
        }
        var report = Tr($"Проверок пройдено: {passed}/{suite.Checks.Length}\n", $"UI checks passed: {passed}/{suite.Checks.Length}\n") + string.Join("\n", reports);
        if (passed != suite.Checks.Length)
            throw new ToolException(Tr("UI-тест не пройден. ", "UI test failed. ") + report);
        return report;
    }

    static string SuitePath(ToolArgs args, ToolContext context)
    {
        var id = args.Required("id").ToLowerInvariant();
        if (!Regex.IsMatch(id, "^[a-z0-9_-]{1,64}$") || context.AppFolder == null || context.SandboxRoot != null)
            throw new ToolException("Недопустимый ID или хранилище сценариев недоступно.");
        return Path.Combine(context.AppFolder, "scripts", "tests", id + ".json");
    }

    [GeneratedRegex(@"^(?<type>(?:int|long|double|bool|string)\??)\s+(?<name>[a-zA-Z_]\w*)$")]
    private static partial Regex Parameter();
}
