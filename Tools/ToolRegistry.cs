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
    static readonly HashSet<string> UnsafeInSandbox = ["exec_command", "run_plugin", ComputerTools.Name, ComputerTools.AppName, ScriptRegistry.Name, BrowserTools.Name, WebResearch.Name,
        ComputerInventory.FindFolders, ComputerInventory.ListApps];
    static readonly HashSet<string> ReadOnly =
        ["list_directory", "get_file_info", "search_files", "get_system_info", "read_log", "use_plugin", LocalContractEngine.Name, LocalResearchEngine.Name, ToolOutputStore.Name, "local_batch", WebResearch.Name,
            ComputerInventory.FindFolders, ComputerInventory.ListApps];
    static readonly string[] ErrorPrefixes = ["Ошибка", "Пользователь отклонил", "Отменено", "Выполнение было прервано", "Error:"];
    readonly Dictionary<string, AgentTool> _byName;
    readonly List<AgentTool> _tools;
    readonly ShellTools.Sessions _sessions = new();
    readonly SemaphoreSlim _documentRetryDialog = new(1);
    internal Task CancelCommandsAsync() => _sessions.CancelAsync();
    internal string? PendingCommandOutput => _sessions.PendingOutput;
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
        _tools = FileTools.All().Concat(ComputerInventory.All()).Concat(ShellTools.All(_sessions)).Concat(CommandLog.All()).Concat(ImageTools.All())
            .Concat(PluginTools.All()).Concat(ComputerTools.All()).Concat(AgentKnowledge.All()).Append(ScriptRegistry.Tool()).Append(BrowserTools.Tool()).Append(LocalTestTools.Tool())
            .Append(LocalResearchEngine.Tool()).Append(LocalContractEngine.Tool()).Append(ToolOutputStore.Tool()).Append(BatchTool()).Append(PatchTool.Tool())
            .Append(WebResearch.Tool()).Append(WordEdits.Tool())
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
        CancellationToken ct, Action? executionStarted = null, bool allowBrowser = false)
    {
        if (!_byName.TryGetValue(name, out var tool) || ComputerTools.IsControl(name) && !Context.ComputerUse)
            return Error($"Ошибка: неизвестный инструмент '{name}'.");
        if (Context.SandboxRoot != null && UnsafeInSandbox.Contains(name))
            return Error($"Ошибка: инструмент '{name}' отключён в режиме песочницы.");
        if (PendingCommandOutput != null && name != "exec_command" && !IsReadOnly(name))
            return Error("Ошибка: сначала получите итог выполняющейся команды через exec_command.");
        JsonObject args;
        try { args = string.IsNullOrWhiteSpace(argumentsJson) ? [] : JsonNode.Parse(argumentsJson) as JsonObject ?? []; }
        catch (JsonException ex) { return Error($"Ошибка: аргументы не являются корректным JSON ({ex.Message})."); }
        var toolArgs = new ToolArgs(args) { BrowserAllowed = allowBrowser, RetryBusyDocument = confirm != null ? (_documentRetryDialog, AskToRetryAsync) : null };
        if (!allowBrowser && (name == BrowserTools.Name || name == "open_path" && FileTools.IsWebUrl(toolArgs.Optional("path") ?? "")))
            return Error("Ошибка: доступ к браузеру для этого запроса не подключён. Используй web_search для чтения сайтов или enable_tools с группой browser, если пользователь просит выполнить действие на странице.");
        bool opaque = name is "exec_command" or "run_plugin" or "open_path" or ComputerTools.Name or ComputerTools.AppName or ScriptRegistry.Name ||
            name == LocalTestTools.Name && toolArgs.Optional("action") == "run";
        try
        {
            ct.ThrowIfCancellationRequested();
            if (opaque) Context.FileAccess.RequireExclusiveAccess(reserve: false);
            if (await ConfirmExternalPathsAsync(name, args, confirm)) ToolContext.AllowNetwork();
            var request = tool.Confirmation?.Invoke(toolArgs, Context);
            if (confirm != null && name == "exec_command" && request != null)
                request = request with { AlwaysAsk = true, Note = request.Note + "\n" + Tr("Команда может обращаться к файлам вне проекта.",
                    "The command can access files outside the project.") };
            if (request != null)
            {
                if (confirm == null && request.AlwaysAsk) return Error("Ошибка: для этого действия нужно подтверждение пользователя.");
                if (confirm != null && !await confirm(request)) return Error(Declined);
                toolArgs.ConfirmedRevision = request.Revision;
            }
            ct.ThrowIfCancellationRequested();
            if (opaque) Context.FileAccess.RequireExclusiveAccess(reserve: true);
            executionStarted?.Invoke();
            return await Task.Run(async () =>
            {
                FileChanges.Pending? pending = null;
                var output = await toolArgs.WithDocumentRetryAsync(() =>
                {
                    pending = FileChanges.Capture(Context, name, toolArgs);
                    return tool.Handler(toolArgs, Context, ct);
                }, ct);
                bool error = name == "local_batch" && IsErrorOutput(output);
                bool warning = name == "local_batch" && IsPartialBatch(output) ||
                    name is "exec_command" or "run_plugin" or ScriptRegistry.Name or LocalTestTools.Name && CommandFailed(output);
                var (change, diff) = error || warning ? (null, null) :
                    FileChanges.Complete(Context, pending, name == "generate_image" ? ImageTools.SavedPath(output) : null);
                var saved = (output.Length > 2400 && ToolOutputStore.Applies(name) || output.Length > MaxToolOutputChars)
                    ? ToolOutputStore.Save(Context, output) : null;
                output = ToolOutputStore.Preview(output, saved);
                if (output.Length > MaxToolOutputChars)
                    output = output[..MaxToolOutputChars] + $"\n... (результат обрезан до {MaxToolOutputChars} символов)";
                return new ToolOutcome(output, error, warning, diff) { Change = change, SavedOutput = saved,
                    Changes = toolArgs.PatchChanges?.Select(item => FileChanges.RecordPatch(Context, item)).ToArray(),
                    CompletedCommand = toolArgs.CompletedCommand };
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CommandRestrictedException ex) { return Error(ex.Message) with { Restriction = ex.Restriction }; }
        catch (DocumentInUseException ex) { return Error("Ошибка: " + ex.Message); }
        catch (ToolException ex) { return Error("Ошибка: " + ex.Message); }
        catch (UnauthorizedAccessException ex)
        {
            return Error($"Ошибка доступа: {ex.Message} Выберите файл или папку, доступные текущей учётной записи.");
        }
        catch (Exception ex) { return Error($"Ошибка ({ex.GetType().Name}): {ex.Message}"); }

        Task<bool> AskToRetryAsync(string path) => confirm!(new(Tr("Документ открыт в другой программе", "Document is open in another app"), path,
            Tr("Повторить", "Retry"), Note: Tr("Закройте документ в другой программе и нажмите «Повторить».",
                "Close the document in the other app, then select Retry."), AlwaysAsk: true, CancelAction: Tr("Отмена", "Cancel")));
    }

    const string Declined = "Пользователь отклонил это действие. Не повторяй его без явной просьбы пользователя.";

    internal async Task<bool> ConfirmExternalPathsAsync(string name, JsonObject args, Func<ConfirmRequest, Task<bool>>? confirm)
    {
        if (Context.SandboxRoot != null || ExternalPaths(name, args) is not { Length: > 0 } paths) return false;
        if (confirm == null) throw new ToolException("Для доступа к пути вне проекта нужно подтверждение пользователя.");
        bool network = paths.Any(Context.IsForeignShare);
        if (name == "exec_command" && !network) return false;
        var note = network ? Tr("При обращении к сетевому пути Windows может передать данные для входа в вашу учётную запись.",
            "Windows may send your sign-in credentials when accessing a network path.") : Tr("Действие затронет пути за пределами текущей рабочей папки.",
            "This action accesses paths outside the current working folder.");
        if (!await confirm(new(Tr("Разрешить доступ вне проекта?", "Allow access outside the project?"), string.Join("\n", paths),
                Tr("Разрешить", "Allow"), Danger: network || !IsReadOnly(name), Note: note, AlwaysAsk: true))) throw new ToolException(Declined);
        return network;
    }

    internal string[] ExternalPaths(string name, JsonObject args) => Paths(args)
        .Concat(name == PatchTool.Name && Str(args["patch"]) is { } patch ? PatchTool.Paths(patch) : [])
        .Concat(name == "run_plugin" ? PluginTools.PathArguments(new(args)) : [])
        .Where(value => !string.IsNullOrWhiteSpace(value) && !FileTools.IsWebUrl(value)).Select(value =>
        {
            try
            {
                var full = PathHelper.Resolve(value, Context.WorkingDirectory);
                return !PathHelper.IsInside(full, Context.WorkingDirectory) && !(name == "local_batch" && Context.IsSecretPath(full)) ? full : null;
            }
            catch (ToolException) { return null; }
        }).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    static IEnumerable<string> Paths(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(pair => pair.Key is "path" or "directory" or "source" or "destination" or "working_directory" && Str(pair.Value) is { } value
            ? [value] : Paths(pair.Value)),
        JsonArray array => array.SelectMany(Paths),
        _ => [],
    };

    static ToolOutcome Error(string message) => new(message, true);
}
