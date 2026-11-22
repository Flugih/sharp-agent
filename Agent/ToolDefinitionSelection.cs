namespace SharpAgent.Agent;

internal static class ToolDefinitionSelection
{
    internal static readonly string[] Groups = ["all", "files", "code", "documents", "browser", "computer", "media", "plugins", "git", "tests", "web"];

    internal static JsonObject EnableDefinition() => new()
    {
        ["type"] = "function", ["function"] = new JsonObject
        {
            ["name"] = "enable_tools",
            ["description"] = Tr("Открыть нужный инструмент или группу, когда текущих инструментов недостаточно. Укажи один из аргументов.",
                "Enable a needed tool or group when the current selection is insufficient. Provide one argument."),
            ["parameters"] = Schema.Object(new Prop("group", "string", Tr("Группа инструментов.", "Tool group."), Enum: Groups),
                new Prop("tool", "string", Tr("Точное имя инструмента.", "Exact tool name."))),
        },
    };

    internal static Func<string, bool>? Initial(string request)
    {
        var groups = new List<string>();
        bool review = Regex.IsMatch(request, @"(?i)^\s*(?:review|inspect|изучи|посмотри)\s") &&
            !Regex.IsMatch(request, @"(?i)\b(?:fix|edit|update|change|implement|build|run|test|write|create|delete|move)\b|исправ|измен|реализ|сборк|запусти|тест|созда|удал|перенес");
        if (review) groups.Add("files");
        else if (Regex.IsMatch(request, @"(?i)\b(?:refactor|edit|update|modify|implement|methods?|class|project|dotnet|compiler|typescript|python|csharp|endpoint|auth)\b|рефактор|исходник|метод|класс|исправ|измен|сборк|проект|авторизац"))
            groups.Add("edit");
        if (Regex.IsMatch(request, @"(?i)\b(?:docx|xlsx|pptx|pdf|csv|json|documents?|spreadsheet)\b|документ|таблиц|отч[её]т")) groups.Add("documents");
        if (BrowserInteraction(request)) groups.Add("browser");
        if (WebRequest(request)) groups.Add("web");
        if (Regex.IsMatch(request, @"(?i)\b(?:computer use|click|mouse|window|desktop|screenshot)\b|окн[оае]|мыш[ьюи]|нажми|скриншот")) groups.Add("computer");
        if (Regex.IsMatch(request, @"(?i)\b(?:image|audio|video)\b|картинк|изображен|видео|аудио")) groups.Add("media");
        if (Regex.IsMatch(request, @"(?i)\bplugins?\b|плагин")) groups.Add("plugins");
        if (Regex.IsMatch(request, @"(?i)\b(?:git|commit|branch)\b|коммит|ветк")) groups.Add("git");
        var checks = Regex.Replace(request, @"(?i)\bno tests\b|без тестов", "");
        if (Regex.IsMatch(checks, @"(?i)\b(?:tests?|testing|coverage)\b|тест|покрыти")) groups.Add("tests");
        if (Regex.IsMatch(checks, @"(?i)\b(?:build|compile|dotnet)\b|сборк|компиляц")) groups.Add("shell");
        if (groups.Count == 0) return null;
        bool contract = Regex.IsMatch(request, @"(?i)acceptance criteria|requirements check|критери[ий]|услови[ея] приёмки");
        return name => contract && name == LocalContractEngine.Name || name != LocalContractEngine.Name &&
            (Belongs(name, "files") || groups.Any(group => Belongs(name, group)));
    }

    internal static bool BackgroundResearch(string request) => WebRequest(request) && !BrowserInteraction(request) &&
        Regex.IsMatch(request, @"(?i)https?://|\b(?:github|internet|online|research|search|review|inspect)\b|интернет|изучи|посмотри|просмотри|поищи|найди|расскажи|объясни|про что|о ч[её]м");

    internal static bool RepositoryOverview(string request) => BackgroundResearch(request) &&
        Regex.IsMatch(request, @"(?i)\b(?:review|inspect|explain|summari[sz]e|look)\b|просмотри|посмотри|изучи|расскажи|объясни|про что|о ч[её]м") &&
        !Regex.IsMatch(request, @"(?i)\b(?:fix|edit|update|implement|build|clone|pull|test|compare)\b|исправ|измен|реализ|сборк|скачай|клонируй|сравни|запусти|тест");

    static bool WebRequest(string request) =>
        Regex.IsMatch(request, @"(?i)https?://|\b(?:github|repo(?:sitory)?|website|webpage|internet|online)\b|репозитор|\bреп[ауые]\b|интернет|сайт");

    internal static bool BrowserInteraction(string request) =>
        !Regex.IsMatch(request, @"(?i)без (?:браузера|webview|вкладок)|не\s+открыва\w*|without (?:a |the )?browser|(?:do not|don't) open") &&
        (Regex.IsMatch(request, @"(?i)\b(?:in (?:the )?browser|open (?:the )?browser)\b|в (?:браузере|webview)\b|открой\s+(?:браузер|сайт|страниц|вкладк|ссылк|https?://)|open\s+https?://") ||
         WebRequest(request) && Regex.IsMatch(request, @"(?i)\b(?:click|screenshot)\b|нажми|кликни|скриншот|(?:проверь|протестируй)\s+(?:интерфейс|кнопк|форму|в[её]рстку)"));

    internal static bool Belongs(string name, string group) => group switch
    {
        "all" => true,
        "files" or "code" or "documents" or "git" or "tests" when name is ToolOutputStore.Name or "local_batch" => true,
        "browser" => name is BrowserTools.Name or "open_path",
        "web" => name == WebResearch.Name,
        "computer" => ComputerTools.IsControl(name),
        "media" => name is "generate_image" or "image_edit" or "use_plugin" or "run_plugin",
        "plugins" => name is "use_plugin" or "run_plugin",
        "files" => name is "exec_command" or "read_log" or "list_directory" or "get_file_info" or "search_files" or LocalContractEngine.Name,
        "edit" => name is PatchTool.Name,
        "code" => name is PatchTool.Name or "exec_command" or "read_log" or LocalTestTools.Name or AgentKnowledge.Name or "update_plan" or "delegate_tasks",
        "documents" => name is LocalResearchEngine.Name or PatchTool.Name,
        "git" or "shell" => name is "exec_command" or "read_log",
        "tests" => name is "exec_command" or "read_log" or LocalTestTools.Name,
        _ => false,
    };
}

partial class AgentRunner
{
    JsonArray? _scopedDefinitions;

    ToolOutcome EnableToolGroup(JsonObject args)
    {
        var group = Str(args["group"]);
        var tool = Str(args["tool"]);
        if (string.IsNullOrWhiteSpace(tool) && !ToolDefinitionSelection.Groups.Contains(group, StringComparer.Ordinal) ||
            !string.IsNullOrWhiteSpace(tool) && group != null)
            return new(PromptText("Ошибка: неизвестная группа инструментов.", "Error: unknown tool group."), true);
        if (_scopedDefinitions == null || _toolCatalog == null)
            return new(PromptText("Ошибка: список инструментов недоступен.", "Error: the tool list is unavailable."), true);
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
        return new(PromptText($"Группа {tool ?? group}: добавлено инструментов {added.Length}.", $"Group {tool ?? group}: {added.Length} tools added."), false);
    }

    Dictionary<string, JsonNode>? _toolCatalog;
}
