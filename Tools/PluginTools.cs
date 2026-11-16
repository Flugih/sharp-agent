namespace SharpAgent.Tools;

public sealed record Plugin(string Name, string Description, string Folder, string Instructions);

public static partial class PluginTools
{
    public static IEnumerable<AgentTool> All() =>
    [
        new AgentTool
        {
            Name = "use_plugin",
            Description = "Открыть инструкцию плагина из списка «Плагины» в правилах. Вызывай, когда задача подходит под плагин, " +
                "и дальше действуй строго по инструкции.",
            Parameters = Schema.Object(new Prop("name", "string", "Имя плагина.", Required: true)),
            Handler = AgentTool.Sync(Use),
        },
        new AgentTool
        {
            Name = "run_plugin",
            Description = "Запустить скрипт плагина с параметрами. Какой скрипт и с какими параметрами — сказано в инструкции плагина.",
            Parameters = Schema.Object(
                new Prop("name", "string", "Имя плагина.", Required: true),
                new Prop("script", "string", "Имя скрипта из папки плагина, например make-docx.ps1.", Required: true),
                new Prop("arguments", "object", "Параметры скрипта: имя → текстовое значение.")),
            Handler = RunAsync,
            Confirmation = ConfirmOverwrite,
        },
    ];

    public static List<Plugin> Load(string? folder)
    {
        var plugins = new List<Plugin>();
        try
        {
            if (folder != null && Directory.Exists(folder))
                foreach (var dir in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var manifest = Path.Combine(dir, "PLUGIN.md");
                    try { if (File.Exists(manifest)) plugins.Add(Parse(dir, File.ReadAllText(manifest).Replace("\r\n", "\n"))); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return plugins;
    }

    public static string Describe(IEnumerable<Plugin> plugins) => string.Join("\n", plugins.Select(p => $"- {p.Name}: {p.Description}"));

    static Plugin Parse(string dir, string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var body = text;
        int end = text.StartsWith("---\n", StringComparison.Ordinal) ? text.IndexOf("\n---", 4, StringComparison.Ordinal) : -1;
        if (end > 0)
        {
            foreach (var line in text[4..end].Split('\n'))
                if (line.IndexOf(':') is > 0 and var colon) fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            body = text[(end + 4)..];
        }
        return new Plugin(fields.GetValueOrDefault("name") ?? Path.GetFileName(dir), fields.GetValueOrDefault("description") ?? "", dir,
            body.Trim());
    }

    static Plugin Find(ToolContext c, string name)
    {
        var plugins = Load(c.PluginsFolder);
        return plugins.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new ToolException(
                $"Плагин «{name}» не найден. Установлены: " +
                (plugins.Count > 0 ? string.Join(", ", plugins.Select(p => p.Name)) : "нет") + ".");
    }

    static string Use(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var plugin = Find(c, a.Required("name"));
        return $"Плагин {plugin.Name}\nПапка плагина: {plugin.Folder}\n\n{plugin.Instructions}";
    }

    static Task<string> RunAsync(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        if (c.SandboxRoot != null) throw new ToolException("В режиме песочницы скрипты плагинов не запускаются.");
        var plugin = Find(c, a.Required("name"));
        var script = Path.Combine(plugin.Folder, Path.GetFileName(a.Required("script")));
        if (!script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || !File.Exists(script))
            throw new ToolException($"В плагине {plugin.Name} нет скрипта {Path.GetFileName(script)}.");
        return ShellTools.RunPowerShellAsync("& " + Quote(script) + string.Concat(a.Object("arguments").Select(p =>
            ParameterName().IsMatch(p.Key) ? $" -{p.Key}:{Quote(ArgumentValue(p.Key, JsonUtil.Str(p.Value) ?? "", c))}"
                : throw new ToolException($"Недопустимое имя параметра: {p.Key}"))) + "\nif (-not $?) { exit 1 }", c, ct);
    }

    static string ArgumentValue(string key, string value, ToolContext c)
    {
        if (Abbreviates(key, "Overwrite")) return IsYes(value) ? value : "no";
        bool target = Abbreviates(key, "Target");
        if (value.Length == 0 || !(target || Abbreviates(key, "Source"))) return value;
        var full = c.Resolve(value);
        c.GuardSecrets(full);
        if (target) c.GuardAppFiles(full);
        return full;
    }

    static bool Abbreviates(string key, string name) => name.StartsWith(key, StringComparison.OrdinalIgnoreCase);

    static bool IsYes(string? value) => "yes".Equals(value, StringComparison.OrdinalIgnoreCase);

    internal static string? TargetOf(ToolArgs a, ToolContext c) =>
        JsonUtil.Str(a.Object("arguments").FirstOrDefault(p => Abbreviates(p.Key, "Target")).Value) is { Length: > 0 } path
            ? c.Resolve(path) : null;

    static ConfirmRequest? ConfirmOverwrite(ToolArgs a, ToolContext c)
    {
        if (!a.Object("arguments").Any(p => Abbreviates(p.Key, "Overwrite") && IsYes(JsonUtil.Str(p.Value)))) return null;
        var target = TargetOf(a, c) ?? c.Resolve(null);
        c.GuardAppFiles(target);
        return File.Exists(target)
            ? new ConfirmRequest("Перезаписать файл?", target, "Перезаписать", Danger: true, Note: "Прежнее содержимое пропадёт.")
            : null;
    }

    static string Quote(string value) => "'" + SingleQuote().Replace(value, "$0$0") + "'";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex ParameterName();

    [GeneratedRegex("['\u2018\u2019\u201A\u201B]")]
    private static partial Regex SingleQuote();
}
