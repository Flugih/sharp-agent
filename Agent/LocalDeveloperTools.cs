namespace SharpAgent.Agent;

sealed record SourceSymbol(string Name, int Start, int End, string Signature, string[] Dependencies);
sealed record SourceSyntax(SourceSymbol[] Symbols, string[] Diagnostics);

static class LocalDeveloperTools
{
    sealed record Sdk(string Dotnet, string Roslyn, string References);
    static readonly Lazy<Sdk?> InstalledSdk = new(FindSdk);
    static readonly SemaphoreSlim CompilerLock = new(1);
    static volatile bool _roslynUnavailable;
    static readonly string[] FrameworkReferences =
        ["System.Runtime", "System.Linq", "System.Collections", "System.Collections.Immutable", "System.Runtime.Loader", "System.Text.Json"];

    public static string ParserVersion => !_roslynUnavailable && InstalledSdk.Value is { } sdk
        ? Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(sdk.Roslyn)))! : "";

    public static async Task<Dictionary<string, SourceSyntax>> ParseCSharpAsync(string cache, IReadOnlyDictionary<string, string> sources,
        CancellationToken ct)
    {
        if (sources.Count == 0 || _roslynUnavailable || InstalledSdk.Value is not { } sdk) return new();
        var folder = Path.Combine(cache, "parse-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            if (await WorkerAsync(sdk, cache, folder, ct) is { } worker)
            {
                var paths = sources.Keys.ToArray();
                for (int i = 0; i < paths.Length; i++) File.WriteAllText(Path.Combine(folder, i + ".source"), sources[paths[i]]);
                if ((await RunAsync(sdk.Dotnet, cache, ["exec", "--runtimeconfig", Path.Combine(sdk.Roslyn, "csc.runtimeconfig.json"),
                        worker, sdk.Roslyn, folder], 15, 2_000, ct)).ExitCode == 0)
                {
                    var parsed = new Dictionary<string, SourceSyntax>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < paths.Length; i++)
                        if (Path.Combine(folder, i + ".json") is var result && File.Exists(result) &&
                            JsonSerializer.Deserialize<SourceSyntax>(File.ReadAllText(result)) is { } syntax)
                            parsed[paths[i]] = syntax;
                    return parsed;
                }
            }
            _roslynUnavailable = true;
            return new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static async Task<string?> WorkerAsync(Sdk sdk, string cache, string folder, CancellationToken ct)
    {
        await CompilerLock.WaitAsync(ct);
        try
        {
            using var reader = new StreamReader(typeof(LocalDeveloperTools).Assembly.GetManifestResourceStream("SharpAgent.CodeIndexWorker.cs")!);
            var source = reader.ReadToEnd();
            var worker = Path.Combine(cache, "parser-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sdk.Roslyn + source)))[..16] + ".dll");
            if (File.Exists(worker)) return worker;
            var file = Path.Combine(folder, "Worker.cs");
            var staging = Path.Combine(folder, "Worker.dll");
            File.WriteAllText(file, source);
            if ((await RunAsync(sdk.Dotnet, cache,
                [
                    Path.Combine(sdk.Roslyn, "csc.dll"), "-nologo", "-noconfig", "-nostdlib", "-target:exe", "-langversion:12.0", "-nowarn:1701",
                    "-out:" + staging, "-reference:" + Path.Combine(sdk.Roslyn, "Microsoft.CodeAnalysis.dll"),
                    "-reference:" + Path.Combine(sdk.Roslyn, "Microsoft.CodeAnalysis.CSharp.dll"),
                    .. FrameworkReferences.Select(r => "-reference:" + Path.Combine(sdk.References, r + ".dll")), file,
                ], 12, 2_000, ct)).ExitCode != 0 || !File.Exists(staging)) return null;
            File.Move(staging, worker, true);
            return worker;
        }
        finally { CompilerLock.Release(); }
    }

    static Sdk? FindSdk()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        var dotnet = Path.Combine(root, "dotnet.exe");
        if (!File.Exists(dotnet)) return null;
        try
        {
            var packs = VersionFolders(Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref")).ToArray();
            foreach (var (folder, version) in VersionFolders(Path.Combine(root, "sdk")))
                if (packs.FirstOrDefault(p => p.Version.Major == version.Major).Folder is { } pack &&
                    Path.Combine(folder, "Roslyn", "bincore") is var roslyn && File.Exists(Path.Combine(roslyn, "csc.dll")))
                    return new(dotnet, roslyn, Path.Combine(pack, "ref", $"net{version.Major}.0"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }

    static IEnumerable<(string Folder, Version Version)> VersionFolders(string root) => Directory.Exists(root)
        ? Directory.EnumerateDirectories(root).Select(f => (Folder: f, Version: Version.TryParse(Path.GetFileName(f), out var v) ? v : null))
            .Where(p => p.Version != null).OrderByDescending(p => p.Version).Select(p => (p.Folder, p.Version!))
        : [];

    public static string? GitExecutable(string root) => (Environment.GetEnvironmentVariable("Path") ?? "").Split(Path.PathSeparator)
        .Select(part => part.Trim().Trim('"')).Where(Path.IsPathFullyQualified).Select(path => Path.Combine(path, "git.exe"))
        .FirstOrDefault(git => File.Exists(git) && !PathHelper.IsInside(PathHelper.FinalPath(git), root));

    public static Task<(int ExitCode, string Output)> GitAsync(string git, string root, IReadOnlyList<string> arguments, int limit, CancellationToken ct) =>
        RunAsync(git, root, ["--no-pager", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL", "-c", "core.quotePath=false",
            "--literal-pathspecs", "--work-tree=" + root, .. arguments], 4, limit, ct, git: true);

    static async Task<(int ExitCode, string Output)> RunAsync(string exe, string root, IReadOnlyList<string> arguments,
        int seconds, int limit, CancellationToken ct, bool git = false)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        var env = start.Environment;
        if (git)
        {
            foreach (var key in env.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) env.Remove(key);
            env["GIT_CONFIG_NOSYSTEM"] = "1";
            env["GIT_CONFIG_GLOBAL"] = "NUL";
            env["GIT_OPTIONAL_LOCKS"] = "0";
            env["GIT_NO_LAZY_FETCH"] = "1";
            env["GIT_ALLOW_PROTOCOL"] = "none";
            env["GIT_TERMINAL_PROMPT"] = "0";
        }
        else env["TEMP"] = env["TMP"] = root;
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return (-1, "");
            process.StandardInput.Close();
            using var time = CancellationTokenSource.CreateLinkedTokenSource(ct);
            time.CancelAfter(TimeSpan.FromSeconds(seconds));
            var output = ReadOutputAsync(process.StandardOutput, limit, time.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(time.Token), output, ReadOutputAsync(process.StandardError, 0, time.Token));
                return (process.ExitCode, output.Result);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or AggregateException) { }
                ct.ThrowIfCancellationRequested();
                return (-1, "");
            }
        }
        catch (Win32Exception) { return (-1, ""); }
    }

    static async Task<string> ReadOutputAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        while (await reader.ReadAsync(buffer.AsMemory(), ct) is > 0 and var read) text.Append(buffer, 0, Math.Min(read, limit - text.Length));
        return text.ToString();
    }
}
