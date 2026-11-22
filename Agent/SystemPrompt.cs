namespace SharpAgent.Agent;

static class SystemPrompt
{
    static readonly Lazy<string> Work = new(() => Read("system.work"));
    static readonly Lazy<string> Chat = new(() => Read("system.chat"));
    static readonly Lazy<string> Browser = new(() => Read("browser"));
    static readonly Lazy<string> Computer = new(() => Read("computer"));
    static readonly Lazy<string> Documents = new(() => Read("documents"));
    static readonly Lazy<string> Subagent = new(() => Read("subagent"));

    internal static string Base(bool work) => (work ? Work : Chat).Value;

    internal static string Context(ToolContext context, JsonArray definitions, bool delegated)
    {
        var names = definitions.Select(item => Str(item?["function"]?["name"])).ToHashSet();
        var notes = new List<string> { "[Application environment]", "Working directory: " + context.WorkingDirectory };
        if (context.SandboxRoot is { } root) notes.Add("Sandbox root: " + root + ". Commands are disabled.");
        if (delegated) notes.Add(Subagent.Value);
        if (names.Contains(BrowserTools.Name)) notes.Add(Browser.Value);
        if (names.Contains(ComputerTools.Name) || names.Contains(ComputerTools.AppName)) notes.Add(Computer.Value);
        if (names.Contains(LocalResearchEngine.Name)) notes.Add(Documents.Value);
        return "\n\n" + string.Join("\n", notes);
    }

    static string Read(string name)
    {
        using var stream = typeof(SystemPrompt).Assembly.GetManifestResourceStream("SharpAgent.Prompts." + name + ".txt")
            ?? throw new InvalidOperationException("Missing embedded prompt " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }
}
