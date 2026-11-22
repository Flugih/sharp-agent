namespace SharpAgent.Agent;

internal static class ToolDefinitionSelection
{
    internal static readonly string[] Groups = ["all", "files", "code", "documents", "browser", "computer", "media", "plugins", "git", "tests"];

    internal static JsonObject EnableDefinition() => new()
    {
        ["type"] = "function", ["function"] = new JsonObject
        {
            ["name"] = "enable_tools",
            ["description"] = Tr("Локально открыть недоступную группу инструментов для текущей задачи.", "Enable another available group of tools for the current task."),
            ["parameters"] = Schema.Object(new Prop("group", "string", Tr("Группа инструментов.", "Tool group."), true, Groups)),
        },
    };

    internal static bool Reduce(JsonArray definitions, string request)
    {
        var groups = new List<string>();
        if (Regex.IsMatch(request, @"(?i)\b(?:refactor|method|class|dotnet|compiler|typescript|python|csharp|endpoint|auth)\b|рефактор|исходник|метод|класс|исправь код|сборк|проект|авторизац")) groups.Add("code");
        if (Regex.IsMatch(request, @"(?i)\b(?:docx|xlsx|pptx|pdf|csv|json|documents?|spreadsheet)\b|документ|таблиц|отч[её]т")) groups.Add("documents");
        if (Regex.IsMatch(request, @"(?i)https?://|\b(?:browser|website|webpage)\b|браузер|сайт")) groups.Add("browser");
        if (Regex.IsMatch(request, @"(?i)\b(?:computer use|click|mouse|window|desktop|screenshot)\b|окн[оае]|мыш[ьюи]|нажми|скриншот")) groups.Add("computer");
        if (Regex.IsMatch(request, @"(?i)\b(?:image|audio|video)\b|картинк|изображен|видео|аудио")) groups.Add("media");
        if (Regex.IsMatch(request, @"(?i)\bplugins?\b|плагин")) groups.Add("plugins");
        if (Regex.IsMatch(request, @"(?i)\b(?:git|commit|branch)\b|коммит|ветк")) groups.Add("git");
        if (Regex.IsMatch(request, @"(?i)\b(?:test|tests|testing|coverage)\b|тест|покрыти")) groups.Add("tests");
        if (groups.Count == 0) return false;
        groups.Add("files");
        var removed = 0;
        foreach (var item in definitions.ToArray())
        {
            var name = JsonUtil.Str(item?["function"]?["name"]);
            if (name != null && !groups.Any(group => Belongs(name, group)))
            {
                definitions.Remove(item);
                removed++;
            }
        }
        return removed > 0;
    }

    internal static bool Belongs(string name, string group) => group switch
    {
        "all" => true,
        "browser" => name is "browser_page" or "open_path",
        "computer" => ComputerTools.IsControl(name),
        "media" => name is "generate_image" or "image_edit" or "use_plugin" or "run_plugin",
        "plugins" => name is "use_plugin" or "run_plugin",
        "files" => name is "read_file" or "read_many" or "list_directory" or "get_file_info" or "search_files" or "search_text" or LocalContractEngine.Name,
        "code" => name is "write_file" or "edit_file" or "run_command" or "read_log" or LocalTestTools.Name or AgentKnowledge.Name or "create_directory",
        "documents" => name is LocalResearchEngine.Name or "write_file" or "edit_file" or "create_directory",
        "git" => name is "run_command" or "read_log",
        "tests" => name is "run_command" or "read_log" or LocalTestTools.Name,
        _ => false,
    };
}

partial class AgentRunner
{
    JsonArray? _scopedDefinitions;

    ToolOutcome EnableToolGroup(JsonObject args)
    {
        var group = Str(args["group"]) ?? "";
        if (!ToolDefinitionSelection.Groups.Contains(group, StringComparer.Ordinal))
            return new(PromptText("Ошибка: неизвестная группа инструментов.", "Error: unknown tool group."), true);
        if (_scopedDefinitions == null)
            return new(PromptText("Ошибка: список инструментов недоступен.", "Error: the tool list is unavailable."), true);
        var present = _scopedDefinitions.OfType<JsonObject>().Select(x => Str(x["function"]?["name"])).ToHashSet();
        int added = 0;
        foreach (var item in tools.Definitions.OfType<JsonObject>())
        {
            var name = Str(item["function"]?["name"]);
            if (name != null && ToolDefinitionSelection.Belongs(name, group) &&
                (name != BrowserTools.Name || tools.Context.ReadBrowser != null) &&
                (name is not ("use_plugin" or "run_plugin") || AvailablePlugins().Count > 0) && present.Add(name))
            {
                _scopedDefinitions.Add(item.DeepClone());
                added++;
            }
        }
        if (group == "all")
        {
            var enable = _scopedDefinitions.OfType<JsonObject>().FirstOrDefault(x => Str(x["function"]?["name"]) == "enable_tools");
            if (enable != null) _scopedDefinitions.Remove(enable);
        }
        return new(PromptText($"Группа {group}: добавлено инструментов {added}.", $"Group {group}: {added} tools added."), false);
    }
}
