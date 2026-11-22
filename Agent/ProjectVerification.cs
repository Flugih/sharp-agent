namespace SharpAgent.Agent;

static partial class ProjectVerification
{
    internal sealed record Recipe(string Directory, string[] Commands);
    internal sealed record Diagnostic(string File, int Line, string Code, string Message);
    internal sealed record Result(string Report, bool Success, Diagnostic[] Diagnostics, bool Blocked = false);

    internal static Recipe? Detect(ToolContext context, string? source = null, bool testsOnly = false)
    {
        var working = PathHelper.FinalPath(context.Resolve(null));
        var root = source == null ? working : Path.GetDirectoryName(context.Resolve(source)) ?? working;
        for (var directory = new DirectoryInfo(root); directory != null && PathHelper.IsInside(directory.FullName, working); directory = directory.Parent)
        {
            var path = directory.FullName;
            if (!directory.Exists || !PathHelper.SamePath(path, PathHelper.FinalPath(path))) break;
            bool Has(string file) => File.Exists(Path.Combine(path, file));
            if (directory.EnumerateFiles("*.csproj").Any() || directory.EnumerateFiles("*.sln").Any() || directory.EnumerateFiles("*.slnx").Any())
                return new(path, testsOnly ? ["dotnet test --nologo"] : ["dotnet build --nologo", "dotnet test --no-build --nologo"]);
            if (Has("go.mod"))
                return new(path, testsOnly ? ["go test ./..."] : ["go vet ./...", .. (OnPath("staticcheck", working) ? new[] { "staticcheck ./..." } : []), "go test ./..."]);
            if (Has("Cargo.toml"))
                return new(path, testsOnly ? ["cargo test --offline"] : ["cargo check --offline", "cargo clippy --offline", "cargo test --offline"]);
            if (Has("pyproject.toml") || Has("pytest.ini") || Has("ruff.toml"))
            {
                var commands = new List<string>();
                if (!testsOnly && OnPath("ruff", working)) commands.Add("ruff check .");
                if (!testsOnly && OnPath("pyright", working)) commands.Add("pyright");
                if (OnPath("pytest", working)) commands.Add("pytest -q");
                return commands.Count == 0 ? null : new(path, [.. commands]);
            }
            if (Has("package.json"))
            {
                var commands = NodeCommands(context, Path.Combine(path, "package.json"), testsOnly);
                return commands.Length == 0 ? null : new(path, commands);
            }
        }
        return null;
    }

    static string[] NodeCommands(ToolContext context, string manifest, bool testsOnly)
    {
        var path = context.Resolve(manifest);
        context.GuardSecrets(path);
        try
        {
            if (TextFileIO.TryRead(path, 256_000) is not { } json || ParseObject(json)?["scripts"] is not JsonObject scripts) return [];
            return (testsOnly ? new[] { "test" } : ["lint", "typecheck", "test"]).Where(s => Str(scripts[s]) is { Length: > 0 } command &&
                !command.Contains("no test specified", StringComparison.OrdinalIgnoreCase))
                .Select(s => "npm run " + s).ToArray();
        }
        catch (JsonException) { return []; }
    }

    static bool OnPath(string executable, string workspace) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(path => path.Trim('"'))
            .Where(Path.IsPathFullyQualified).Any(path => !PathHelper.IsInside(path, workspace) &&
                (File.Exists(Path.Combine(path, executable + ".exe")) || File.Exists(Path.Combine(path, executable + ".cmd"))));

    internal static Diagnostic[] Parse(string output)
    {
        var found = new List<Diagnostic>();
        foreach (var line in output.Split('\n').Take(6_000))
        {
            if (Location().Match(line) is not { Success: true } match ||
                !int.TryParse(match.Groups["line"].Value, out int number)) continue;
            var code = match.Groups["code"].Value;
            var text = AgentRunner.Clip(match.Groups["message"].Value.Trim(), 280);
            if (text.Length == 0) continue;
            found.Add(new(match.Groups["path"].Value.Trim(), number, code.Length > 0 ? code : "diagnostic", text));
        }
        return found.Distinct().Take(12).ToArray();
    }

    internal static Diagnostic[] Diagnostics(ToolContext context, string outcome)
    {
        var transcript = context.LogsFolder != null && CommandLog.IdOf(outcome) is { } id ? CommandLog.Read(context.LogsFolder, id) : null;
        return Parse(transcript ?? outcome);
    }

    internal static string Report(ToolContext context, string outcome, Diagnostic[]? diagnostics = null)
    {
        diagnostics ??= Diagnostics(context, outcome);
        if (diagnostics.Length == 0) return CommandLog.Brief(outcome) + "\n" + CommandLog.Details(outcome, 1200);
        var known = AgentKnowledge.Relevant(context, string.Join(" ", diagnostics.Select(d => d.Code)));
        return CommandLog.Brief(outcome) + "\n" + string.Join("\n", diagnostics.Select(d => $"{d.File}:{d.Line} [{d.Code}] {d.Message}")) +
            (known == null ? "" : "\n\n" + known);
    }

    internal static bool Command(string command) => Verification().IsMatch(command);
    internal static bool Tests(string command) => TestCommand().IsMatch(command) && Command(command);
    internal static bool TestsExecuted(string output) => TestResults().IsMatch(output) ||
        Regex.IsMatch(output, @"(?m)^ok\s+\S+\s+(?:\d|\(cached\))");
    internal static string Transcript(ToolContext context, string output) =>
        context.LogsFolder != null && CommandLog.IdOf(output) is { } id ? CommandLog.Read(context.LogsFolder, id) ?? output : output;

    [GeneratedRegex(@"^\s*(?:dotnet\s+test|cargo\s+test|go\s+test|(?:npm|pnpm|yarn)\s+(?:run\s+)?test|(?:python\s+-m\s+)?pytest)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TestCommand();
    [GeneratedRegex(@"(?im)(?:\b[1-9]\d*\s+passed\b|(?:total(?:\s+tests)?|passed|пройдено|всего)(?:\s+tests)?\s*:\s*[1-9]\d*|tests run\s*:\s*[1-9]\d*)")]
    private static partial Regex TestResults();

    [GeneratedRegex(@"^\s*(?<path>.+?)(?:\((?<line>\d+)(?:,\d+)*\)\s*:|:(?<line>\d+)(?::\d+)?:)\s*(?:(?:error|warning|ошибка|предупреждение)\s+)?(?:(?<code>[A-Z]{1,10}[0-9]{2,8})\s*:?\s*)?(?<message>\S.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Location();
    [GeneratedRegex(@"^\s*(?:dotnet\s+(?:build|test)|(?:npm|pnpm|yarn)\s+(?:run\s+)?(?:test|lint|typecheck)|(?:python\s+-m\s+)?pytest|ruff\s+check|pyright|cargo\s+(?:build|check|clippy|test)|go\s+(?:build|vet|test)|staticcheck)\b[^\r\n;|&]*$", RegexOptions.IgnoreCase)]
    private static partial Regex Verification();
}
