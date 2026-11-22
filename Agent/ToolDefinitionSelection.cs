namespace SharpAgent.Agent;

/// <summary>Conservative tool-schema scoping: unfamiliar tasks keep all tools; missing groups can be enabled in one local call.</summary>
internal static class ToolDefinitionSelection
{
    static readonly HashSet<string> Essentials = new(StringComparer.Ordinal)
    {
        "read_file", "read_many", "list_directory", "get_file_info", "search_files", "search_text",
        "write_file", "edit_file", "run_command", "read_log", "local_contract", "local_research", "local_tests", "knowledge",
        "create_directory", "copy_path", "move_path", "delete_path",
    };

    internal static readonly JsonNode EnableDefinition = JsonNode.Parse("""
        {"type":"function","function":{"name":"enable_tools","description":"Локально открыть дополнительную группу инструментов для текущей задачи. Не делает API-запрос сам по себе; используй только когда нужного инструмента нет в доступном наборе.","parameters":{"type":"object","properties":{"group":{"type":"string","enum":["all","browser","computer","media","plugins","files"]}},"required":["group"]}}}
        """)!;

    internal static bool Reduce(JsonArray definitions, string request)
    {
        // Strictly explicit code/document intents only; mixed or ambiguous tasks keep all capabilities.
        bool code = Regex.IsMatch(request, @"(?i)(?:\b(?:refactor|method|class|dotnet|compiler|typescript|python|csharp)\b|(?:рефактор|исходник|метод[а-я]*|класс[а-я]*|исправь код|сборк[а-я]*|проект[а-я]*))");
        bool documents = Regex.IsMatch(request, @"(?i)(?:\b(?:docx|xlsx|pdf|csv|json|document|spreadsheet)\b|(?:документ[а-я]*|таблиц[а-я]*|отч[её]т[а-я]*))");
        bool other = Regex.IsMatch(request, @"(?i)(?:\b(?:browser|website|image|screenshot|audio|video|computer|click|openrouter|whatsapp)\b|(?:браузер[а-я]*|сайт[а-я]*|скриншот[а-я]*|картинк[а-я]*|изображен[а-я]*|видео|аудио|мыш[ьюи]|нажми|окно|плагин[а-я]*))");
        if ((!code && !documents) || other) return false;
        var removed = 0;
        foreach (var item in definitions.ToArray())
        {
            var name = JsonUtil.Str(item?["function"]?["name"]);
            if (name != null && !Essentials.Contains(name))
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
        "computer" => name.StartsWith("computer", StringComparison.Ordinal) || name is "app_control",
        "media" => name is "generate_image" or "image_edit" or "use_plugin" or "run_plugin",
        "plugins" => name is "use_plugin" or "run_plugin",
        "files" => Essentials.Contains(name),
        _ => false,
    };
}

partial class AgentRunner
{
    JsonArray? _scopedDefinitions;

    ToolOutcome EnableToolGroup(JsonObject args)
    {
        var group = Str(args["group"]) ?? "";
        if (group is not ("all" or "browser" or "computer" or "media" or "plugins" or "files"))
            return new("Ошибка: неизвестная группа инструментов.", true);
        if (_scopedDefinitions == null)
            return new("Ошибка: список инструментов не инициализирован.", true);
        var present = _scopedDefinitions.OfType<JsonObject>().Select(x => Str(x["function"]?["name"])).ToHashSet();
        int added = 0;
        foreach (var item in tools.Definitions.OfType<JsonObject>())
        {
            var name = Str(item["function"]?["name"]);
            if (name != null && ToolDefinitionSelection.Belongs(name, group) && present.Add(name))
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
        return new($"Группа {group}: добавлено инструментов {added}. Используй их на следующем шаге.", false);
    }
}
