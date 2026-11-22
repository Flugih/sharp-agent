namespace SharpAgent.Tools;

public sealed record ToolOutcome(string Output, bool IsError, bool IsWarning = false, FileDiff? Diff = null, UsageInfo? DelegatedUsage = null)
{
    internal CommandRestriction? Restriction { get; init; }
    internal FileChange? Change { get; init; }
    internal FileChange[]? Changes { get; init; }
    internal string? CompletedCommand { get; init; }
    internal string? SavedOutput { get; init; }
}

public sealed partial class ToolRegistry
{
    const int MaxToolOutputChars = 100_000;
    static readonly HashSet<string> UnsafeInSandbox = ["exec_command", "run_plugin", ComputerTools.Name, ComputerTools.AppName, ScriptRegistry.Name, BrowserTools.Name];
    static readonly HashSet<string> ReadOnly =
        ["list_directory", "get_file_info", "search_files", "get_system_info", "read_log", "use_plugin", LocalContractEngine.Name, LocalResearchEngine.Name, ToolOutputStore.Name, "local_batch"];
    static readonly string[] ErrorPrefixes = ["Ошибка", "Пользователь отклонил", "Отменено", "Выполнение было прервано", "Error:"];
    readonly Dictionary<string, AgentTool> _byName;
    readonly List<AgentTool> _tools;
    readonly ShellTools.Sessions _sessions = new();
    internal Task CancelCommandsAsync() => _sessions.CancelAsync();
    internal bool CommandsPending => _sessions.Pending;
    readonly Dictionary<(bool Sandbox, bool Computer, bool Russian), JsonArray> _definitions = [];

    public ToolContext Context { get; }

    public JsonArray Definitions
    {
        get
        {
            var key = (Sandbox: Context.SandboxRoot != null, Computer: Context.ComputerUse, Russian: UiLanguage.Current.Value.IsRussian);
            if (!_definitions.TryGetValue(key, out var definitions))
                _definitions[key] = definitions = BuildDefinitions(_tools.Where(t =>
                    !(key.Sandbox && UnsafeInSandbox.Contains(t.Name) || ComputerTools.IsControl(t.Name) && !key.Computer)).Select(tool => tool.Name switch
                    {
                        LocalResearchEngine.Name => LocalResearchEngine.Tool(),
                        LocalContractEngine.Name => LocalContractEngine.Tool(),
                        LocalTestTools.Name => LocalTestTools.Tool(),
                        _ => tool,
                    }));
            return definitions;
        }
    }

    public ToolRegistry(ToolContext context, bool readOnly = false)
    {
        Context = context;
        _tools = FileTools.All().Concat(ShellTools.All(_sessions)).Concat(CommandLog.All()).Concat(ImageTools.All())
            .Concat(PluginTools.All()).Concat(ComputerTools.All()).Concat(AgentKnowledge.All()).Append(ScriptRegistry.Tool()).Append(BrowserTools.Tool()).Append(LocalTestTools.Tool())
            .Append(LocalResearchEngine.Tool()).Append(LocalContractEngine.Tool()).Append(ToolOutputStore.Tool()).Append(BatchTool()).Append(PatchTool.Tool())
            .Where(t => !readOnly || IsReadOnly(t.Name)).ToList();
        _byName = _tools.ToDictionary(t => t.Name);
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
        CancellationToken ct, Action? executionStarted = null)
    {
        if (!_byName.TryGetValue(name, out var tool) || ComputerTools.IsControl(name) && !Context.ComputerUse)
            return Error($"Ошибка: неизвестный инструмент '{name}'.");
        if (Context.SandboxRoot != null && UnsafeInSandbox.Contains(name))
            return Error($"Ошибка: инструмент '{name}' отключён в режиме песочницы.");
        if (_sessions.Pending && name != "exec_command" && !IsReadOnly(name))
            return Error("Ошибка: сначала получите итог выполняющейся команды через exec_command.");
        JsonObject args;
        try { args = string.IsNullOrWhiteSpace(argumentsJson) ? [] : JsonNode.Parse(argumentsJson) as JsonObject ?? []; }
        catch (JsonException ex) { return Error($"Ошибка: аргументы не являются корректным JSON ({ex.Message})."); }
        var toolArgs = new ToolArgs(args);
        bool opaque = name is "exec_command" or "run_plugin" or "open_path" or ComputerTools.Name or ComputerTools.AppName or ScriptRegistry.Name ||
            name == LocalTestTools.Name && toolArgs.Optional("action") == "run";
        try
        {
            ct.ThrowIfCancellationRequested();
            if (opaque) Context.FileAccess.RequireExclusiveAccess(reserve: false);
            if (Context.SandboxRoot == null && ForeignShares(args, name) is { Length: > 0 } shares)
            {
                if (confirm == null) return Error("Ошибка: сетевые пути вне рабочей папки субагентам недоступны.");
                if (!await confirm(new("Обратиться к сетевому пути?", string.Join("\n", shares), "Разрешить", Danger: true,
                        Note: "Windows передаст этому компьютеру данные для входа в вашу учётную запись.")))
                    return Error("Пользователь отклонил это действие. Не повторяй его без явной просьбы пользователя.");
                ToolContext.AllowNetwork();
            }
            if (tool.Confirmation?.Invoke(toolArgs, Context) is { } request)
            {
                if (confirm == null ? request.AlwaysAsk : !await confirm(request))
                    return Error("Пользователь отклонил это действие. Не повторяй его без явной просьбы пользователя.");
                toolArgs.ConfirmedRevision = request.Revision;
            }
            ct.ThrowIfCancellationRequested();
            if (opaque) Context.FileAccess.RequireExclusiveAccess(reserve: true);
            executionStarted?.Invoke();
            return await Task.Run(async () =>
            {
                var pending = FileChanges.Capture(Context, name, toolArgs);
                var output = await tool.Handler(toolArgs, Context, ct);
                bool warning = name is "exec_command" or "run_plugin" or ScriptRegistry.Name or LocalTestTools.Name && CommandFailed(output);
                var (change, diff) = warning ? (null, null) :
                    FileChanges.Complete(Context, pending, name == "generate_image" ? ImageTools.SavedPath(output) : null);
                var saved = ToolOutputStore.Applies(name) ? ToolOutputStore.Save(Context, output) : null;
                output = ToolOutputStore.Preview(output, saved);
                if (output.Length > MaxToolOutputChars)
                    output = output[..MaxToolOutputChars] + $"\n... (результат обрезан до {MaxToolOutputChars} символов)";
                return new ToolOutcome(output, false, warning, diff) { Change = change, SavedOutput = saved,
                    Changes = toolArgs.PatchChanges?.Select(item => FileChanges.RecordPatch(Context, item)).ToArray(),
                    CompletedCommand = toolArgs.CompletedCommand };
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

    string[] ForeignShares(JsonObject args, string name) => args.Where(p => p.Key is "path" or "directory" or "source" or "destination" or "working_directory")
        .Concat(args["arguments"] as JsonObject ?? [])
        .Concat((args["files"] as JsonArray ?? []).OfType<JsonObject>().SelectMany(file => file.Where(p => p.Key == "path")))
        .Select(p => SharpAgent.Core.JsonUtil.Str(p.Value)).OfType<string>()
        .Concat(name == PatchTool.Name && Str(args["patch"]) is { } patch ? PatchTool.Paths(patch) : []).Select(value =>
        {
            try { return PathHelper.Resolve(value, Context.WorkingDirectory) is var full && Context.IsForeignShare(full) ? full : null; }
            catch (ToolException) { return null; }
        }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    static ToolOutcome Error(string message) => new(message, true);
}
