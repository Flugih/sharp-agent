namespace SharpAgent.Core;

public enum AgentProvider { Tooken, ChatGpt, Groq, Gemini, OpenRouter, Mistral }

public sealed class AppConfig
{
    public const string DefaultBaseUrl = "https://tooken.club/v1";
    public const string DefaultModel = "claude-sonnet-5-5";
    public const string DefaultImageModel = "gpt-image-2";
    public static readonly string[] EffortLevels = ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    public AgentProvider Provider { get; private init; }
    Dictionary<AgentProvider, string> ApiKeys { get; init; } = [];
    public string? ApiKey => ApiKeyFor(AgentProvider.Tooken);
    public string? GroqApiKey => ApiKeyFor(AgentProvider.Groq);
    public string BaseUrl { get; private init; } = DefaultBaseUrl;
    public string Model { get; private init; } = DefaultModel;
    public string? ImageModel { get; private init; }
    public string? ReasoningEffort { get; private init; }
    public string WorkingDirectory { get; private init; } = "";
    public bool Sandbox { get; private init; }
    public bool UseSubagents { get; private init; }
    public bool ComputerUse { get; private init; }
    public int? MaxTokens { get; private init; }
    public long? DayLimit { get; private init; }
    public long? MonthLimit { get; private init; }
    public string? LoadError { get; private init; }
    public bool HasApiKey => ApiKey != null;
    public bool HasGroqKey => GroqApiKey != null;

    public string? ApiKeyFor(AgentProvider provider) => ApiKeys.GetValueOrDefault(provider) is { Length: > 0 } key ? key : null;

    public static AppConfig Load()
    {
        var vars = new Dictionary<string, string>();
        string? loadError = null;
        try { if (File.Exists(AppPaths.EnvPath)) vars = DotEnv.Parse(AppPaths.EnvPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { loadError = ex.Message; }
        string? Get(string key) => vars.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        bool IsTrue(string key) => new[] { "true", "1", "yes", "да" }.Contains(Get(key), StringComparer.OrdinalIgnoreCase);
        var provider = Enum.TryParse<AgentProvider>(Get("AGENT_PROVIDER"), true, out var parsed) && Enum.IsDefined(parsed) ? parsed : AgentProvider.Tooken;
        bool sandbox = IsTrue("AGENT_SANDBOX");
        string workDir;
        try
        {
            workDir = Path.GetFullPath(Path.Combine(AppPaths.AppFolder,
                Get("AGENT_WORKDIR") is { } dir ? Environment.ExpandEnvironmentVariables(dir.Trim('"')) : AppPaths.DefaultWorkspace));
        }
        catch { workDir = AppPaths.DefaultWorkspace; }
        if (sandbox && !PathHelper.IsInside(workDir, AppPaths.AppFolder)) workDir = AppPaths.DefaultWorkspace;
        try { Directory.CreateDirectory(workDir); }
        catch { }
        return new()
        {
            LoadError = loadError,
            Provider = provider,
            ApiKeys = Enum.GetValues<AgentProvider>().Where(p => p != AgentProvider.ChatGpt)
                .ToDictionary(p => p, p => CleanKey(Get(KeyOf(p, "API_KEY")))),
            BaseUrl = NormalizeBaseUrl(Get("TOOKEN_BASE_URL") ?? DefaultBaseUrl),
            Model = Get(KeyOf(provider, "MODEL")) ?? DefaultModelOf(provider),
            ImageModel = Get("TOOKEN_IMAGE_MODEL"),
            ReasoningEffort = Get(KeyOf(provider, "REASONING_EFFORT"))?.ToLowerInvariant() is { } effort && EffortLevels.Contains(effort) ? effort : null,
            WorkingDirectory = workDir,
            Sandbox = sandbox,
            UseSubagents = IsTrue("AGENT_USE_SUBAGENTS"),
            ComputerUse = IsTrue("AGENT_COMPUTER_USE"),
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
        AgentProvider.Gemini => "gemini-2.5-flash",
        AgentProvider.OpenRouter => "openrouter/auto",
        AgentProvider.Mistral => "mistral-small-latest",
        _ => DefaultModel,
    };

    public static string DefaultBaseUrlOf(AgentProvider provider) => provider switch
    {
        AgentProvider.Groq => Groq.BaseUrl,
        AgentProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai",
        AgentProvider.OpenRouter => "https://openrouter.ai/api/v1",
        AgentProvider.Mistral => "https://api.mistral.ai/v1",
        _ => DefaultBaseUrl,
    };

    public static string CleanKey(string? key)
    {
        key = key?.Trim() ?? "";
        return key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? key[7..].Trim() : key;
    }

    public static long? ParseMillions(string? value) =>
        double.TryParse(value?.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var millions) &&
        millions > 0 && millions < 1e9 ? (long)Math.Round(millions * 1_000_000) : null;

    public static string NormalizeBaseUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        const string suffix = "/chat/completions";
        if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) url = url[..^suffix.Length];
        return url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url : url + "/v1";
    }

    public static readonly string EnvTemplate = $"""
        # Через кого отвечает агент: tooken, chatgpt, groq, gemini, openrouter или mistral
        AGENT_PROVIDER=tooken
        # Ключ берётся в личном кабинете: https://tooken.club/dashboard (выглядит как tc_live_...)
        TOOKEN_API_KEY=
        TOOKEN_BASE_URL={DefaultBaseUrl}
        TOOKEN_MODEL={DefaultModel}
        # Модель, которой агент рисует картинки (всегда через Tooken Club)
        TOOKEN_IMAGE_MODEL={DefaultImageModel}
        # Сила рассуждения для моделей, которые её поддерживают: low, medium или high (пусто — как решит модель)
        TOOKEN_REASONING_EFFORT=
        # ChatGPT: вход — кнопкой «Продолжить с ChatGPT» в настройках, он хранится в auth\chatgpt.json
        CHATGPT_MODEL=gpt-6.1-sol
        # low, medium, high, xhigh, max или ultra — если модель это поддерживает (пусто — как решит модель)
        CHATGPT_REASONING_EFFORT=
        # Ключ Groq создаётся на https://console.groq.com/keys (выглядит как gsk_...); он же нужен для голосового ввода
        GROQ_API_KEY=
        GROQ_MODEL=openai/gpt-oss-120b
        GROQ_REASONING_EFFORT=
        GEMINI_API_KEY=
        GEMINI_MODEL={DefaultModelOf(AgentProvider.Gemini)}
        GEMINI_REASONING_EFFORT=
        OPENROUTER_API_KEY=
        OPENROUTER_MODEL={DefaultModelOf(AgentProvider.OpenRouter)}
        OPENROUTER_REASONING_EFFORT=
        MISTRAL_API_KEY=
        MISTRAL_MODEL={DefaultModelOf(AgentProvider.Mistral)}
        MISTRAL_REASONING_EFFORT=
        # Рабочая папка агента (относительный путь — от папки программы)
        AGENT_WORKDIR=workspace
        # true — агент работает ТОЛЬКО внутри папки программы (команды PowerShell/cmd отключены)
        AGENT_SANDBOX=false
        AGENT_USE_SUBAGENTS=false
        # true — агент управляет программами на экране и смотрит скриншоты (скриншоты расходуют много токенов)
        AGENT_COMPUTER_USE=false
        # Свои лимиты расхода в миллионах токенов баланса Tooken Club (пусто — без лимита)
        # AGENT_DAY_LIMIT_MLN=5
        # AGENT_MONTH_LIMIT_MLN=50

        """.ReplaceLineEndings("\r\n");
}
