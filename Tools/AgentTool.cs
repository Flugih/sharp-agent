using System.Text.Json.Nodes;
using SharpAgent.Core;

namespace SharpAgent.Tools;

public sealed class ToolException(string message) : Exception(message);

public sealed record ConfirmRequest(string Title, string Subject, string Action, bool Danger = false, string? Note = null);

public sealed class ToolContext
{
    public string WorkingDirectory { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private string? _appFolder;

    public string? AppFolder
    {
        get => _appFolder;
        set => _appFolder = value != null ? PathHelper.FinalPath(value) : null;
    }

    public string? LogsFolder { get; set; }

    public string? SandboxRoot { get; set; }

    public Func<string, string, CancellationToken, Task<byte[]>>? GenerateImage { get; set; }

    public string TempFolder => AppFolder != null ? Path.Combine(AppFolder, "temp") : Path.GetTempPath();

    public string? PluginsFolder => AppFolder != null ? Path.Combine(AppFolder, "plugins") : null;

    private string? EnvFile => AppFolder != null ? Path.Combine(AppFolder, ".env") : null;

    private string? AuthFolder => AppFolder != null ? Path.Combine(AppFolder, "auth") : null;

    public string Resolve(string? path)
    {
        var full = PathHelper.Resolve(path, WorkingDirectory);
        if (SandboxRoot != null && !PathHelper.IsInside(full, SandboxRoot))
            throw new ToolException($"Включён режим песочницы: работать можно только внутри папки {SandboxRoot}. " +
                                    $"Путь «{full}» находится за её пределами.");
        return full;
    }

    public bool IsSecret(string fullPath)
    {
        if (EnvFile == null) return false;
        fullPath = PathHelper.FinalPath(fullPath);
        return PathHelper.IsInside(EnvFile, fullPath) ||
               PathHelper.IsInside(AuthFolder!, fullPath) ||
               PathHelper.IsInside(fullPath, AuthFolder!) ||
               PathHelper.IsInside(fullPath, Path.Combine(AppFolder!, "history", "changes")) ||
               (PathHelper.IsInside(fullPath, AppFolder!) &&
                Path.GetFileName(fullPath).Equals(".env", StringComparison.OrdinalIgnoreCase));
    }

    public void GuardSecrets(string fullPath)
    {
        if (IsSecret(fullPath))
            throw new ToolException("Здесь лежат секреты программы (файл .env с ключами или вход в ChatGPT в папке auth) — " +
                                    "читать или копировать их агенту нельзя.");
    }

    public void GuardAppFiles(string fullPath)
    {
        if (AppFolder == null) return;
        fullPath = PathHelper.FinalPath(fullPath);
        bool isAppFile =
            PathHelper.SamePath(fullPath, EnvFile!) ||
            PathHelper.IsInside(fullPath, AuthFolder!) ||
            PathHelper.IsInside(fullPath, Path.Combine(AppFolder, "history")) ||
            PathHelper.IsInside(fullPath, TempFolder) ||
            PathHelper.IsInside(fullPath, PluginsFolder!) ||
            PathHelper.SamePath(fullPath, Path.Combine(AppFolder, "state.json")) ||
            PathHelper.SamePath(fullPath, Path.Combine(AppFolder, "errors.log")) ||
            (Environment.ProcessPath != null && PathHelper.SamePath(fullPath, PathHelper.FinalPath(Environment.ProcessPath))) ||
            PathHelper.IsInside(AppFolder, fullPath);
        if (isAppFile)
            throw new ToolException("Это служебные файлы самой программы Sharp Agent (.env, auth, history, temp, plugins, state.json, exe " +
                                    "или папка программы целиком) — изменять, перемещать и удалять их агенту нельзя.");
    }
}

public sealed class AgentTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Parameters { get; init; }
    public required Func<ToolArgs, ToolContext, CancellationToken, Task<string>> Handler { get; init; }

    public Func<ToolArgs, ToolContext, ConfirmRequest?>? Confirmation { get; init; }

    public static Func<ToolArgs, ToolContext, CancellationToken, Task<string>> Sync(
        Func<ToolArgs, ToolContext, CancellationToken, string> handler) =>
        (args, ctx, ct) =>
        {
            try
            {
                return Task.FromResult(handler(args, ctx, ct));
            }
            catch (Exception ex)
            {
                return Task.FromException<string>(ex);
            }
        };
}

public sealed class ToolArgs(JsonObject obj)
{
    public string Required(string name, bool allowEmpty = false)
    {
        var value = JsonUtil.Str(obj[name]);
        if (value == null || (!allowEmpty && value.Length == 0))
            throw new ToolException($"Не указан обязательный параметр '{name}'.");
        return value;
    }

    public string? Optional(string name)
    {
        var value = JsonUtil.Str(obj[name]);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public JsonObject Object(string name) => obj[name] as JsonObject ?? new JsonObject();

    public bool Bool(string name, bool defaultValue = false)
    {
        if (obj[name] is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return b;
        }
        return defaultValue;
    }

    public int Int(string name, int defaultValue)
    {
        if (obj[name] is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<double>(out var d)) return (int)d;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out i)) return i;
        }
        return defaultValue;
    }
}

public sealed record Prop(string Name, string Type, string Description, bool Required = false, string[]? Enum = null);

public static class Schema
{
    public static JsonObject Object(params Prop[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in props)
        {
            var prop = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Enum != null)
                prop["enum"] = new JsonArray(p.Enum.Select(e => (JsonNode?)JsonValue.Create(e)).ToArray());
            properties[p.Name] = prop;
            if (p.Required) required.Add(JsonValue.Create(p.Name));
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }
}
