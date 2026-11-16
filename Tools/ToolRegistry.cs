namespace SharpAgent.Tools;

public sealed record ToolOutcome(string Output, bool IsError, bool IsWarning = false, FileDiff? Diff = null, UsageInfo? DelegatedUsage = null)
{
    internal CommandRestriction? Restriction { get; init; }
    internal FileChange? Change { get; init; }
}

public sealed class ToolRegistry
{
    const int MaxToolOutputChars = 100_000;
    static readonly HashSet<string> UnsafeInSandbox = ["run_command", "run_plugin"];
    static readonly HashSet<string> ReadOnly =
        ["read_file", "list_directory", "get_file_info", "search_files", "search_text", "get_system_info", "read_log", "use_plugin"];
    static readonly string[] ErrorPrefixes = ["Ошибка", "Пользователь отклонил", "Отменено", "Выполнение было прервано"];
    readonly Dictionary<string, AgentTool> _byName;
    readonly JsonArray _allDefinitions, _sandboxDefinitions;

    public ToolContext Context { get; }

    public JsonArray Definitions => Context.SandboxRoot != null ? _sandboxDefinitions : _allDefinitions;

    public ToolRegistry(ToolContext context, bool readOnly = false)
    {
        Context = context;
        var tools = FileTools.All().Concat(ShellTools.All()).Concat(CommandLog.All()).Concat(ImageTools.All())
            .Concat(PluginTools.All()).Where(t => !readOnly || IsReadOnly(t.Name)).ToList();
        _byName = tools.ToDictionary(t => t.Name);
        _allDefinitions = BuildDefinitions(tools);
        _sandboxDefinitions = BuildDefinitions(tools.Where(t => !UnsafeInSandbox.Contains(t.Name)));
    }

    static JsonArray BuildDefinitions(IEnumerable<AgentTool> tools) => new(tools.Select(t => (JsonNode?)new JsonObject
    {
        ["type"] = "function",
        ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() },
    }).ToArray());

    public static bool IsReadOnly(string name) => ReadOnly.Contains(name);

    public static bool IsErrorOutput(string output) => ErrorPrefixes.Any(prefix => output.StartsWith(prefix, StringComparison.Ordinal));

    public static bool CommandFailed(string output) => CommandLog.ResultOf(output) is { } result && result != "код 0";

    public async Task<ToolOutcome> ExecuteAsync(string name, string argumentsJson, Func<ConfirmRequest, Task<bool>>? confirm,
        CancellationToken ct)
    {
        if (!_byName.TryGetValue(name, out var tool)) return Error($"Ошибка: неизвестный инструмент '{name}'.");
        if (Context.SandboxRoot != null && UnsafeInSandbox.Contains(name))
            return Error($"Ошибка: инструмент '{name}' отключён в режиме песочницы.");
        JsonObject args;
        try { args = string.IsNullOrWhiteSpace(argumentsJson) ? [] : JsonNode.Parse(argumentsJson) as JsonObject ?? []; }
        catch (JsonException ex) { return Error($"Ошибка: аргументы не являются корректным JSON ({ex.Message})."); }
        var toolArgs = new ToolArgs(args);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (Context.SandboxRoot == null && ForeignShares(args) is { Length: > 0 } shares)
            {
                if (confirm == null) return Error("Ошибка: сетевые пути вне рабочей папки субагентам недоступны.");
                if (!await confirm(new("Обратиться к сетевому пути?", string.Join("\n", shares), "Разрешить", Danger: true,
                        Note: "Windows передаст этому компьютеру данные для входа в вашу учётную запись.")))
                    return Error("Пользователь отклонил это действие. Не повторяй его без явной просьбы пользователя.");
                ToolContext.AllowNetwork();
            }
            if (tool.Confirmation?.Invoke(toolArgs, Context) is { } request && confirm != null && !await confirm(request))
                return Error("Пользователь отклонил это действие. Не повторяй его без явной просьбы пользователя.");
            return await Task.Run(async () =>
            {
                var pending = FileChanges.Capture(Context, name, toolArgs);
                var output = await tool.Handler(toolArgs, Context, ct);
                bool warning = name is "run_command" or "run_plugin" && CommandFailed(output);
                var (change, diff) = warning ? (null, null) :
                    FileChanges.Complete(Context, pending, name == "generate_image" ? ImageTools.SavedPath(output) : null);
                if (output.Length > MaxToolOutputChars)
                    output = output[..MaxToolOutputChars] + $"\n... (результат обрезан до {MaxToolOutputChars} символов)";
                return new ToolOutcome(output, false, warning, diff) { Change = change };
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CommandRestrictedException ex) { return Error(ex.Message) with { Restriction = ex.Restriction }; }
        catch (ToolException ex) { return Error("Ошибка: " + ex.Message); }
        catch (UnauthorizedAccessException ex)
        {
            return Error($"Ошибка доступа: {ex.Message} Выберите файл или папку, доступные текущей учётной записи.");
        }
        catch (Exception ex) { return Error($"Ошибка ({ex.GetType().Name}): {ex.Message}"); }
    }

    string[] ForeignShares(JsonObject args) => args.Where(p => p.Key is "path" or "directory" or "source" or "destination" or "working_directory")
        .Concat(args["arguments"] as JsonObject ?? []).Select(p => SharpAgent.Core.JsonUtil.Str(p.Value)).OfType<string>().Select(value =>
        {
            try { return PathHelper.Resolve(value, Context.WorkingDirectory) is var full && Context.IsForeignShare(full) ? full : null; }
            catch (ToolException) { return null; }
        }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    static ToolOutcome Error(string message) => new(message, true);
}
