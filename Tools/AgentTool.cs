using static SharpAgent.Tools.PathHelper;

namespace SharpAgent.Tools;

public sealed class ToolException(string message) : Exception(message);

public sealed record ConfirmRequest(string Title, string Subject, string Action, bool Danger = false, string? Note = null, bool AlwaysAsk = false,
    string? Revision = null);

public sealed class ToolContext
{
    // The data folder of the installed version is guarded in both modes: a portable copy must not reach its secrets.
    static readonly string InstalledData = FinalPath(AppPaths.InstalledDataFolder);
    static readonly string LegacyData = FinalPath(AppPaths.LegacyFolder);
    static readonly string MigrationData = FinalPath(AppPaths.MigrationFolder);
    string? _appFolder, _installationFolder;
    public string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string? AppFolder { get => _appFolder; set => _appFolder = value != null ? FinalPath(value) : null; }
    public string? LogsFolder { get; set; }
    public string? SandboxRoot { get; set; }
    public string? InstallationFolder { get => _installationFolder; set => _installationFolder = value != null ? FinalPath(value) : null; }
    public bool ComputerUse { get; set; }
    internal ChatFileAccess FileAccess { get; } = new();
    internal SnapshotStore Snapshots { get; } = new();
    internal ProjectWatcher ProjectWatcher { get; } = new();
    public Func<string, string, CancellationToken, Task<byte[]>>? GenerateImage { get; set; }
    public Action<string>? OpenUrl { get; set; }
    public Func<IntPtr>? BrowserWindow { get; set; }
    internal Func<string?, string?, string?, CancellationToken, Task<BrowserTools.Snapshot>>? ReadBrowser { get; set; }
    internal HttpClient? ResearchClient { get; init; }
    public string TempFolder => AppFolder != null ? App("temp") : Path.GetTempPath();
    public string? PluginsFolder => AppFolder != null ? App("plugins") : null;
    internal string CodeIndexFolder => App(@"cache\code-index");
    internal string DatabaseFolder => App("database");

    string App(string name) => Path.Combine(AppFolder!, name);

    string[] DataFolders => [AppFolder!, LegacyData, MigrationData, InstalledData, Path.Combine(InstalledData, "data"), Path.Combine(InstalledData, "data.migrating")];

    static readonly AsyncLocal<bool> NetworkAllowed = new();

    internal static void AllowNetwork() => NetworkAllowed.Value = true;

    internal bool IsForeignShare(string full) =>
        full.StartsWith(@"\\") && !IsInside(full, WorkingDirectory) && (AppFolder == null || !IsInside(full, AppFolder));

    public string Resolve(string? path)
    {
        var full = PathHelper.Resolve(path, WorkingDirectory);
        if (SandboxRoot is { } root && !(IsInside(full, root) && IsInside(FinalPath(full), FinalPath(root))))
            throw new ToolException($"Включён режим песочницы: работать можно только внутри папки {SandboxRoot}. " +
                $"Путь «{full}» находится за её пределами.");
        return IsForeignShare(full) && !NetworkAllowed.Value
            ? throw new ToolException($"Сетевой путь «{full}» доступен агенту только с разрешения пользователя.") : full;
    }

    public bool IsSecret(string fullPath)
    {
        if (AppFolder == null) return false;
        fullPath = FinalPath(fullPath);
        return IsInside(fullPath, MigrationData) || IsInside(fullPath, Path.Combine(InstalledData, "data.migrating")) ||
            DataFolders.Any(folder => HoldsSecrets(folder, fullPath));
    }

    static bool HoldsSecrets(string folder, string path) =>
        IsInside(Path.Combine(folder, ".env"), path) || SamePath(path, Path.Combine(folder, ".env.tmp")) ||
        IsInside(Path.Combine(folder, "auth"), path) || IsInside(path, Path.Combine(folder, "auth")) || IsInside(path, Path.Combine(folder, "Browser")) ||
        IsInside(path, Path.Combine(folder, @"history\changes")) || IsInside(path, Path.Combine(folder, "checkpoints")) ||
        IsInside(path, folder) && Path.GetFileName(path).Equals(".env", StringComparison.OrdinalIgnoreCase);

    public void GuardSecrets(string fullPath)
    {
        if (IsSecret(fullPath))
            throw new ToolException("Здесь лежат секреты программы (файл .env с ключами или вход в ChatGPT в папке auth) — " +
                "читать или копировать их агенту нельзя.");
    }

    public void GuardAppFiles(string fullPath)
    {
        if (AppFolder == null) return;
        fullPath = FinalPath(fullPath);
        if (DataFolders.Any(folder => HoldsAppFiles(folder, fullPath)) ||
            InstallationFolder is { } install && (IsInside(fullPath, install) || IsInside(install, fullPath)) ||
            IsInside(FinalPath(AppPaths.InstalledModePath), fullPath) || IsInside(FinalPath(AppPaths.PortableModePath), fullPath) ||
            IsInside(fullPath, FinalPath(Path.Combine(AppContext.BaseDirectory, "plugins"))) ||
            Environment.ProcessPath != null && SamePath(fullPath, FinalPath(Environment.ProcessPath)))
            throw new ToolException("Это служебные файлы самой программы Sharp Agent (.env, auth, history, temp, plugins, state.json, exe " +
                "или папка программы целиком) — изменять, перемещать и удалять их агенту нельзя.");
    }

    internal void GuardWrite(string fullPath)
    {
        GuardAppFiles(fullPath);
        FileAccess.Reserve(fullPath);
    }

    static bool HoldsAppFiles(string folder, string path) =>
        new[] { ".env", ".env.tmp", "state.json", "errors.log" }.Any(name => SamePath(path, Path.Combine(folder, name))) ||
        new[] { "auth", "history", "temp" }.Any(name => IsInside(path, Path.Combine(folder, name))) ||
        Path.GetFileName(folder).Equals("data", StringComparison.OrdinalIgnoreCase) &&
            new[] { "cache", "logs", "checkpoints", "database", "scripts", "models" }.Any(name => IsInside(path, Path.Combine(folder, name))) ||
        FinalPath(Path.Combine(folder, "plugins")) is var plugins && (IsInside(path, plugins) || IsInside(plugins, path)) || IsInside(folder, path);
}

public sealed class AgentTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Parameters { get; init; }
    public required Func<ToolArgs, ToolContext, CancellationToken, Task<string>> Handler { get; init; }
    public Func<ToolArgs, ToolContext, ConfirmRequest?>? Confirmation { get; init; }

    public static Func<ToolArgs, ToolContext, CancellationToken, Task<string>> Sync(
        Func<ToolArgs, ToolContext, CancellationToken, string> handler) => (args, ctx, ct) => Task.FromResult(handler(args, ctx, ct));
}

public sealed class ToolArgs(JsonObject obj)
{
    internal bool BrowserAllowed { get; init; }
    internal PatchTool.Change[]? PatchChanges { get; set; }
    internal string? CompletedCommand { get; set; }
    internal string? ConfirmedRevision { get; set; }
    internal JsonArray Array(string name) => obj[name] as JsonArray ?? [];
    public string Required(string name, bool allowEmpty = false) =>
        JsonUtil.Str(obj[name]) is { } value && (allowEmpty || value.Length > 0) ? value
            : throw new ToolException($"Не указан обязательный параметр '{name}'.");

    public string? Optional(string name) => JsonUtil.Str(obj[name]) is var value && !string.IsNullOrWhiteSpace(value) ? value : null;

    public JsonObject Object(string name) => obj[name] as JsonObject ?? [];

    public bool Bool(string name, bool defaultValue = false) => obj[name] is not JsonValue v ? defaultValue
        : v.TryGetValue<bool>(out var b) ? b
        : v.TryGetValue<string>(out var s) && bool.TryParse(s, out b) ? b : defaultValue;

    public int Int(string name, int defaultValue) => obj[name] is not JsonValue v ? defaultValue
        : v.TryGetValue<int>(out var i) ? i
        : v.TryGetValue<double>(out var d) ? (int)d
        : v.TryGetValue<string>(out var s) && int.TryParse(s, out i) ? i : defaultValue;
}

public sealed record Prop(string Name, string Type, string Description, bool Required = false, string[]? Enum = null);

public static class Schema
{
    public static JsonObject Object(params Prop[] props)
    {
        JsonObject properties = [], schema = new() { ["type"] = "object", ["properties"] = properties };
        JsonArray required = [];
        foreach (var p in props)
        {
            var prop = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Enum != null) prop["enum"] = new JsonArray([.. p.Enum.Select(e => (JsonNode?)e)]);
            properties[p.Name] = prop;
            if (p.Required) required.Add((JsonNode?)p.Name);
        }
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }
}
