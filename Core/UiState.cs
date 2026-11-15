using System.Text;
using System.Text.Json;
using SharpAgent.Api;

namespace SharpAgent.Core;

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

public sealed class UsageDay
{
    public long Cost { get; set; }
}

public sealed class UsageCache
{
    public string? KeyId { get; set; }
    public Dictionary<string, UsageDay> Days { get; set; } = new();
    public string? CoveredFrom { get; set; }
    public DateTime? RefreshedUtc { get; set; }
    public long? LastBalance { get; set; }
    public long? FullBalance { get; set; }
    public Dictionary<string, double> Rates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ModelCatalogCache(string Connection, List<ModelInfo> Models);

public sealed class UiState
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public WindowPlacement? Window { get; set; }
    public bool SidebarCollapsed { get; set; }
    public double? SidebarWidth { get; set; }
    public string? CurrentChat { get; set; }
    public string? Draft { get; set; }
    public UsageCache? Usage { get; set; }
    public string? LimitOverrideDay { get; set; }
    public Dictionary<AgentProvider, ModelCatalogCache>? ModelCatalogs { get; set; }

    public static UiState Load()
    {
        try
        {
            if (File.Exists(AppPaths.StatePath))
                return JsonSerializer.Deserialize<UiState>(File.ReadAllText(AppPaths.StatePath, Encoding.UTF8), Options) ?? new UiState();
        }
        catch
        {
        }
        return new UiState();
    }

    public bool Save()
    {
        try
        {
            var temp = AppPaths.StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
            File.Move(temp, AppPaths.StatePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
