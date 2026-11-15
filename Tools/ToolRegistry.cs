using System.Text.Json;
using System.Text.Json.Nodes;
using SharpAgent.Api;

namespace SharpAgent.Tools;

public sealed record ToolOutcome(string Output, bool IsError, bool IsWarning = false, FileDiff? Diff = null, UsageInfo? DelegatedUsage = null)
{
    internal CommandRestriction? Restriction { get; init; }
    internal FileChange? Change { get; init; }
}

public sealed class ToolRegistry
{
    private const int MaxToolOutputChars = 100_000;
    private static readonly HashSet<string> UnsafeInSandbox = ["run_command", "run_plugin"];
    private static readonly HashSet<string> ReadOnly =
        ["read_file", "list_directory", "get_file_info", "search_files", "search_text", "get_system_info", "read_log", "use_plugin"];

    private static readonly string[] ErrorPrefixes = ["Ошибка", "Пользователь отклонил", "Отменено", "Выполнение было прервано"];

    private readonly Dictionary<string, AgentTool> _byName;
    private readonly JsonArray _allDefinitions;
    private readonly JsonArray _sandboxDefinitions;

    public ToolContext Context { get; }

    public JsonArray Definitions => Context.SandboxRoot != null ? _sandboxDefinitions : _allDefinitions;

    public ToolRegistry(ToolContext context, bool readOnly = false)
    {
        Context = context;
        var tools = FileTools.All().Concat(ShellTools.All()).Concat(CommandLog.All()).Concat(ImageTools.All())
            .Concat(PluginTools.All()).ToList();
        if (readOnly) tools.RemoveAll(tool => !IsReadOnly(tool.Name));
        _byName = tools.ToDictionary(t => t.Name);
        _allDefinitions = BuildDefinitions(tools);
        _sandboxDefinitions = BuildDefinitions(tools.Where(t => !UnsafeInSandbox.Contains(t.Name)));
    }

    private static JsonArray BuildDefinitions(IEnumerable<AgentTool> tools) =>
        new(tools.Select(t => (JsonNode?)new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.Parameters.DeepClone(),
            },
        }).ToArray());

    public static bool IsReadOnly(string name) => ReadOnly.Contains(name);

    public static bool IsErrorOutput(string output) =>
        ErrorPrefixes.Any(prefix => output.StartsWith(prefix, StringComparison.Ordinal));

    public static bool CommandFailed(string output)
    {
        var firstLine = output.Split('\n', 2)[0].Trim();
        if (firstLine.StartsWith(CommandLog.TimeoutPrefix, StringComparison.Ordinal)) return true;
        return firstLine.StartsWith(CommandLog.ExitPrefix, StringComparison.Ordinal) && firstLine[CommandLog.ExitPrefix.Length..] != "0";
    }

    public async Task<ToolOutcome> ExecuteAsync(string name, string argumentsJson,
        Func<ConfirmRequest, Task<bool>>? confirm, CancellationToken ct)
    {
        if (!_byName.TryGetValue(name, out var tool))
            return Error($"Ошибка: неизвестный инструмент '{name}'.");
        if (Context.SandboxRoot != null && UnsafeInSandbox.Contains(name))
            return Error($"Ошибка: инструмент '{name}' отключён в режиме песочницы.");

        JsonObject args;
        try
        {
            args = string.IsNullOrWhiteSpace(argumentsJson)
                ? new JsonObject()
                : JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            return Error($"Ошибка: аргументы не являются корректным JSON ({ex.Message}).");
        }

        var toolArgs = new ToolArgs(args);
        try
        {
            ct.ThrowIfCancellationRequested();
            var request = tool.Confirmation?.Invoke(toolArgs, Context);
            if (request != null && confirm != null && !await confirm(request))
                return Error("Пользователь отклонил это действие. Не повторяй его без явной просьбы пользователя.");

            var (output, warning, change, diff) = await Task.Run(async () =>
            {
                var pending = FileChanges.Capture(Context, name, toolArgs);
                var output = await tool.Handler(toolArgs, Context, ct);
                bool warning = name is "run_command" or "run_plugin" && CommandFailed(output);
                var (change, diff) = warning ? (null, null) :
                    FileChanges.Complete(Context, pending, name == "generate_image" ? ImageTools.SavedPath(output) : null);
                return (output, warning, change, diff);
            }, ct);
            if (output.Length > MaxToolOutputChars)
                output = output[..MaxToolOutputChars] + $"\n... (результат обрезан до {MaxToolOutputChars} символов)";
            return new ToolOutcome(output, false, warning, diff) { Change = change };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CommandRestrictedException ex)
        {
            return Error(ex.Message) with { Restriction = ex.Restriction };
        }
        catch (ToolException ex)
        {
            return Error("Ошибка: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Error($"Ошибка доступа: {ex.Message} Выберите файл или папку, доступные текущей учётной записи.");
        }
        catch (Exception ex)
        {
            return Error($"Ошибка ({ex.GetType().Name}): {ex.Message}");
        }
    }

    private static ToolOutcome Error(string message) => new(message, true);
}
