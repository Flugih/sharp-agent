namespace SharpAgent.Agent;

internal static class ToolDefinitionSelection
{
    const string WebScheme = @"https?\\?:(?:\\?/){2}";
    const string DocumentRequest = @"(?i)\b(?:docx|xlsx|pptx|pdf|csv|json|word|documents?|spreadsheet)\b|документ|таблиц|отч[её]т";
    internal static readonly string[] Groups = ["all", "files", "code", "documents", "browser", "browser_capture", "computer", "media", "plugins", "git", "tests", "web"];

    internal static JsonObject EnableDefinition() => new()
    {
        ["type"] = "function", ["function"] = new JsonObject
        {
            ["name"] = "enable_tools",
            ["description"] = Tr("Подключи нужный инструмент или группу по смыслу текущей задачи. Укажи один аргумент. Для чтения сайтов используй web. Если пользователь просит поработать на странице или проверить её интерфейс, подключи browser, а если нужны снимки результата — browser_capture. Для действий в настольных приложениях используй computer.",
                "Enable the tool or group needed for the current task. Provide one argument. Use web for reading sites. When the user requests work on a page or a visible UI check, enable browser, or browser_capture if they need screenshots of the result. Use computer for desktop apps."),
            ["parameters"] = Schema.Object(new Prop("group", "string", Tr("Группа инструментов.", "Tool group."), Enum: Groups),
                new Prop("tool", "string", Tr("Точное имя инструмента.", "Exact tool name."))),
        },
    };

    internal static Func<string, bool> Initial(string request, bool wordAttachment = false)
    {
        var groups = new List<string>();
        bool styleChange = Regex.IsMatch(request, @"(?i)\b(?:font|colou?r|padding|margin)\b|шрифт|цвет|отступ") &&
            Regex.IsMatch(request, @"(?i)\b(?:change|edit|update|modify|replace|set)\b|измени|исправ|поменя|замени|установи|сделай");
        bool review = Regex.IsMatch(request, @"(?i)^\s*(?:review|inspect|изучи|посмотри)\s") &&
            !Regex.IsMatch(request, @"(?i)\b(?:fix|edit|update|change|implement|build|run|test|write|create|delete|move)\b|исправ|измен|реализ|сборк|запусти|тест|созда|удал|перенес");
        if (review && !styleChange) groups.Add("files");
        else if (styleChange || Regex.IsMatch(request, @"(?i)\b(?:refactor|edit|update|modify|implement|methods?|class|project|dotnet|compiler|typescript|python|csharp|endpoint|auth)\b|рефактор|исходник|метод|класс|исправ|измен|сборк|проект|авторизац"))
            groups.Add("edit");
        if (wordAttachment || Regex.IsMatch(request, DocumentRequest)) groups.Add("documents");
        if (WebRequest(request)) groups.Add("web");
        if (Regex.IsMatch(request, @"(?i)\b(?:image|audio|video)\b|картинк|изображен|видео|аудио")) groups.Add("media");
        if (Regex.IsMatch(request, @"(?i)\bplugins?\b|плагин")) groups.Add("plugins");
        if (Regex.IsMatch(request, @"(?i)\b(?:git|commit|branch)\b|коммит|ветк")) groups.Add("git");
        var checks = Regex.Replace(request, @"(?i)\bno tests\b|без тестов", "");
        if (Regex.IsMatch(checks, @"(?i)\b(?:tests?|testing|coverage)\b|тест|покрыти")) groups.Add("tests");
        if (Regex.IsMatch(checks, @"(?i)\b(?:build|compile|dotnet|powershell|cmd)\b|сборк|компиляц")) groups.Add("shell");
        if (groups.Count == 0) groups.AddRange(["edit", "web"]);
        bool styleEdit = styleChange && !groups.Any(group => group is "git" or "tests" or "shell");
        bool contract = Regex.IsMatch(request, @"(?i)acceptance criteria|requirements check|критери[ий]|услови[ея] приёмки");
        return name => (!styleEdit || name != "exec_command") &&
            (contract && name == LocalContractEngine.Name || name != LocalContractEngine.Name &&
             (Belongs(name, "files") || groups.Any(group => Belongs(name, group))));
    }

    internal static bool RepositoryOverview(string request) => WebRequest(request) &&
        Regex.IsMatch(request, @"(?i)\b(?:review|inspect|explain|summari[sz]e|look)\b|просмотри|посмотри|изучи|расскажи|объясни|про что|о ч[её]м") &&
        !Regex.IsMatch(request, @"(?i)\b(?:fix|edit|update|implement|build|clone|pull|test|compare)\b|исправ|измен|реализ|сборк|скачай|клонируй|сравни|запусти|тест");

    static bool WebRequest(string request) =>
        Regex.IsMatch(request, "(?i)" + WebScheme + @"|\b(?:github|repo(?:sitory)?|website|webpage|internet|online)\b|репозитор|\bреп[ауые]\b|интернет|сайт");

    static bool ScreenshotRequest(string request) =>
        Regex.IsMatch(request, @"(?i)\b(?:screenshots?|скриншот\w*|скрин[ыа]?|сним(?:и|ок|ки)|сфотка\w*|фотка(?:й|ть)\w*|сфотографир\w*)\b") &&
        !Regex.IsMatch(request, @"(?i)\b(?:без (?:скринов|скриншотов|снимков)|without screenshots?|не (?:фоткай|сфоткай|снимай|делай скриншот\w*))\b");

    internal static bool BrowserScreenshotDocument(string request) =>
        Regex.IsMatch(request, DocumentRequest) && ScreenshotRequest(request) && WebRequest(request);

    internal static bool BrowserProhibited(string request) => Regex.IsMatch(request,
        @"(?i)\b(?:без (?:браузера|webview|вкладок)|не (?:открыва\w*|запуска\w*|использу\w*)\s+(?:браузер|webview|вкладк\w*|ссылк\w*)|without (?:a |the )?(?:browser|tabs)|(?:do not|don't) (?:open|use)\s+(?:(?:a|the) )?(?:browser|tabs|links))\b");

    internal static bool Belongs(string name, string group) => group switch
    {
        "all" => name != BrowserTools.Name,
        "files" or "code" or "documents" or "git" or "tests" when name is ToolOutputStore.Name or "local_batch" => true,
        "browser" => name is BrowserTools.Name or "open_path",
        "browser_capture" => name is BrowserTools.Name or ComputerTools.Name,
        "web" => name == WebResearch.Name,
        "computer" => ComputerTools.IsControl(name),
        "media" => name is "generate_image" or "image_edit" or "use_plugin" or "run_plugin",
        "plugins" => name is "use_plugin" or "run_plugin",
        "files" => name is "exec_command" or "read_log" or "list_directory" or "get_file_info" or "search_files" or LocalContractEngine.Name,
        "edit" => name is PatchTool.Name,
        "code" => name is PatchTool.Name or "exec_command" or "read_log" or LocalTestTools.Name or AgentKnowledge.Name or "update_plan" or "delegate_tasks",
        "documents" => name is LocalResearchEngine.Name or PatchTool.Name or WordEdits.Name,
        "git" or "shell" => name is "exec_command" or "read_log",
        "tests" => name is "exec_command" or "read_log" or LocalTestTools.Name,
        _ => false,
    };
}

partial class AgentRunner
{
    JsonArray? _scopedDefinitions;
    bool BrowserEnabled => _scopedDefinitions?.Any(item => Str(item?["function"]?["name"]) == BrowserTools.Name) == true;

    ToolOutcome EnableToolGroup(JsonObject args)
    {
        var group = Str(args["group"]);
        var tool = Str(args["tool"]);
        if (string.IsNullOrWhiteSpace(tool) && !ToolDefinitionSelection.Groups.Contains(group, StringComparer.Ordinal) ||
            !string.IsNullOrWhiteSpace(tool) && group != null)
            return new(PromptText("Ошибка: неизвестная группа инструментов.", "Error: unknown tool group."), true);
        if (_scopedDefinitions == null || _toolCatalog == null)
            return new(PromptText("Ошибка: список инструментов недоступен.", "Error: the tool list is unavailable."), true);
        if ((group is "browser" or "browser_capture" || tool == BrowserTools.Name) && !_toolCatalog.ContainsKey(BrowserTools.Name))
            return new(PromptText("Ошибка: встроенный браузер недоступен в текущем запросе.", "Error: the built-in browser is unavailable for this request."), true);
        var selected = _toolCatalog.Where(item => tool != null ? item.Key == tool : ToolDefinitionSelection.Belongs(item.Key, group!)).ToArray();
        if (tool != null && selected.Length == 0)
            return new(PromptText("Ошибка: инструмент недоступен.", "Error: the tool is unavailable."), true);
        var present = _scopedDefinitions.Select(item => Str(item?["function"]?["name"])).ToHashSet();
        var added = selected.Where(item => !present.Contains(item.Key)).ToArray();
        if (added.Length > 0)
        {
            var expanded = (JsonArray)_scopedDefinitions.DeepClone();
            foreach (var item in added) expanded.Add(item.Value.DeepClone());
            _scopedDefinitions = expanded;
        }
        var result = PromptText($"Группа {tool ?? group}: добавлено инструментов {added.Length}.", $"Group {tool ?? group}: {added.Length} tools added.");
        if (group == "browser_capture" && !_toolCatalog.ContainsKey(ComputerTools.Name))
            result += PromptText(" Управление компьютером отключено, скриншоты недоступны.", " Computer Use is disabled, so screenshots are unavailable.");
        return new(result, false);
    }

    Dictionary<string, JsonNode>? _toolCatalog;
}
