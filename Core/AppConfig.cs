using System.Globalization;
using SharpAgent.Tools;

namespace SharpAgent.Core;

public enum AgentProvider { Tooken, ChatGpt, Groq }

public sealed class AppConfig
{
    public const string DefaultBaseUrl = "https://tooken.club/v1";
    public const string DefaultModel = "claude-sonnet-5-5";
    public const string DefaultImageModel = "gpt-image-2";

    public static readonly string[] EffortLevels = ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    public AgentProvider Provider { get; private init; }
    public string? ApiKey { get; private init; }
    public string? GroqApiKey { get; private init; }
    public string BaseUrl { get; private init; } = DefaultBaseUrl;
    public string Model { get; private init; } = DefaultModel;
    public string? ImageModel { get; private init; }
    public string? ReasoningEffort { get; private init; }
    public string WorkingDirectory { get; private init; } = "";
    public bool Sandbox { get; private init; }
    public bool UseSubagents { get; private init; }
    public int? MaxTokens { get; private init; }
    public long? DayLimit { get; private init; }
    public long? MonthLimit { get; private init; }
    public string? LoadError { get; private init; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);
    public bool HasGroqKey => !string.IsNullOrWhiteSpace(GroqApiKey);

    public static AppConfig Load()
    {
        var vars = new Dictionary<string, string>();
        string? loadError = null;
        try
        {
            if (File.Exists(AppPaths.EnvPath)) vars = DotEnv.Parse(AppPaths.EnvPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            loadError = ex.Message;
        }

        string? Get(string key) =>
            vars.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var provider = Enum.TryParse<AgentProvider>(Get("AGENT_PROVIDER"), true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : AgentProvider.Tooken;

        bool sandbox = IsTrue(Get("AGENT_SANDBOX"));
        string workDir;
        try { workDir = ExpandWorkDir(Get("AGENT_WORKDIR")); }
        catch { workDir = AppPaths.DefaultWorkspace; }
        if (sandbox && !PathHelper.IsInside(workDir, AppPaths.AppFolder)) workDir = AppPaths.DefaultWorkspace;
        try { Directory.CreateDirectory(workDir); }
        catch { }

        return new AppConfig
        {
            LoadError = loadError,
            Provider = provider,
            ApiKey = CleanKey(Get("TOOKEN_API_KEY")),
            GroqApiKey = CleanKey(Get("GROQ_API_KEY")),
            BaseUrl = NormalizeBaseUrl(Get("TOOKEN_BASE_URL") ?? DefaultBaseUrl),
            Model = Get(KeyOf(provider, "MODEL")) ?? DefaultModelOf(provider),
            ImageModel = Get("TOOKEN_IMAGE_MODEL"),
            ReasoningEffort = Get(KeyOf(provider, "REASONING_EFFORT"))?.ToLowerInvariant() is { } effort && EffortLevels.Contains(effort)
                ? effort
                : null,
            WorkingDirectory = workDir,
            Sandbox = sandbox,
            UseSubagents = IsTrue(Get("AGENT_USE_SUBAGENTS")),
            MaxTokens = int.TryParse(Get("TOOKEN_MAX_TOKENS"), out var mt) && mt > 0 ? mt : null,
            DayLimit = ParseMillions(Get("AGENT_DAY_LIMIT_MLN")),
            MonthLimit = ParseMillions(Get("AGENT_MONTH_LIMIT_MLN")),
        };
    }

    public static string KeyOf(AgentProvider provider, string setting) => provider.ToString().ToUpperInvariant() + "_" + setting;

    public static string DefaultModelOf(AgentProvider provider) => provider switch
    {
        AgentProvider.ChatGpt => "gpt-6.1-sol",
        AgentProvider.Groq => "openai/gpt-oss-120b",
        _ => DefaultModel,
    };

    public static string CleanKey(string? key)
    {
        key = key?.Trim() ?? "";
        return key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? key[7..].Trim() : key;
    }

    public static long? ParseMillions(string? value) =>
        double.TryParse(value?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var millions) &&
        millions > 0 && millions < 1e9
            ? (long)Math.Round(millions * 1_000_000)
            : null;

    public static string NormalizeBaseUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        const string suffix = "/chat/completions";
        if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) url = url[..^suffix.Length];
        if (!url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) url += "/v1";
        return url;
    }

    public static bool IsTrue(string? value) =>
        new[] { "true", "1", "yes", "да" }.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string ExpandWorkDir(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value)
            ? AppPaths.DefaultWorkspace
            : Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        return Path.GetFullPath(Path.Combine(AppPaths.AppFolder, path));
    }

    public const string EnvTemplate =
        "# Через кого отвечает агент: tooken, chatgpt или groq\r\n" +
        "AGENT_PROVIDER=tooken\r\n" +
        "# Ключ берётся в личном кабинете: https://tooken.club/dashboard (выглядит как tc_live_...)\r\n" +
        "TOOKEN_API_KEY=\r\n" +
        "TOOKEN_BASE_URL=" + DefaultBaseUrl + "\r\n" +
        "TOOKEN_MODEL=" + DefaultModel + "\r\n" +
        "# Модель, которой агент рисует картинки (всегда через Tooken Club)\r\n" +
        "TOOKEN_IMAGE_MODEL=" + DefaultImageModel + "\r\n" +
        "# Сила рассуждения для моделей, которые её поддерживают: low, medium или high (пусто — как решит модель)\r\n" +
        "TOOKEN_REASONING_EFFORT=\r\n" +
        "# ChatGPT: вход — кнопкой «Продолжить с ChatGPT» в настройках, он хранится в auth\\chatgpt.json\r\n" +
        "CHATGPT_MODEL=gpt-6.1-sol\r\n" +
        "# low, medium, high, xhigh, max или ultra — если модель это поддерживает (пусто — как решит модель)\r\n" +
        "CHATGPT_REASONING_EFFORT=\r\n" +
        "# Ключ Groq создаётся на https://console.groq.com/keys (выглядит как gsk_...); он же нужен для голосового ввода\r\n" +
        "GROQ_API_KEY=\r\n" +
        "GROQ_MODEL=openai/gpt-oss-120b\r\n" +
        "GROQ_REASONING_EFFORT=\r\n" +
        "# Рабочая папка агента (относительный путь — от папки программы)\r\n" +
        "AGENT_WORKDIR=workspace\r\n" +
        "# true — агент работает ТОЛЬКО внутри папки программы (команды PowerShell/cmd отключены)\r\n" +
        "AGENT_SANDBOX=false\r\n" +
        "# Свои лимиты расхода в миллионах токенов баланса Tooken Club (пусто — без лимита)\r\n" +
        "# AGENT_DAY_LIMIT_MLN=5\r\n" +
        "# AGENT_MONTH_LIMIT_MLN=50\r\n";
}
