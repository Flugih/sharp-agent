using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpAgent.Agent;

internal sealed record SourceSymbol(string Name, int Start, int End, string Signature, string[] Dependencies);
internal sealed record SourceSyntax(SourceSymbol[] Symbols, string[] Diagnostics);

internal static class LocalDeveloperTools
{
    private sealed record Sdk(string Dotnet, string Roslyn, string References);
    private static readonly Lazy<Sdk?> InstalledSdk = new(FindSdk);
    private static readonly SemaphoreSlim CompilerLock = new(1);
    private static volatile bool _roslynUnavailable;
    private static readonly string[] FrameworkReferences =
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
            var worker = await WorkerAsync(sdk, cache, folder, ct);
            if (worker == null)
            {
                _roslynUnavailable = true;
                return new();
            }
            var paths = sources.Keys.ToArray();
            for (int i = 0; i < paths.Length; i++)
                File.WriteAllText(Path.Combine(folder, i + ".source"), sources[paths[i]]);
            var output = await RunAsync(sdk.Dotnet, cache,
                ["exec", "--runtimeconfig", Path.Combine(sdk.Roslyn, "csc.runtimeconfig.json"), worker, sdk.Roslyn, folder],
                15, 2_000, ct);
            if (output.ExitCode != 0)
            {
                _roslynUnavailable = true;
                return new();
            }
            var parsed = new Dictionary<string, SourceSyntax>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < paths.Length; i++)
            {
                string result = Path.Combine(folder, i + ".json");
                if (File.Exists(result) && JsonSerializer.Deserialize<SourceSyntax>(File.ReadAllText(result)) is { } syntax)
                    parsed[paths[i]] = syntax;
            }
            return parsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private static async Task<string?> WorkerAsync(Sdk sdk, string cache, string folder, CancellationToken ct)
    {
        await CompilerLock.WaitAsync(ct);
        try
        {
            using var resource = typeof(LocalDeveloperTools).Assembly.GetManifestResourceStream("SharpAgent.CodeIndexWorker.cs")!;
            using var reader = new StreamReader(resource);
            var source = reader.ReadToEnd();
            var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sdk.Roslyn + source)))[..16];
            var worker = Path.Combine(cache, "parser-" + id + ".dll");
            if (File.Exists(worker)) return worker;
            var file = Path.Combine(folder, "Worker.cs");
            var staging = Path.Combine(folder, "Worker.dll");
            File.WriteAllText(file, source);
            var arguments = new List<string>
            {
                Path.Combine(sdk.Roslyn, "csc.dll"), "-nologo", "-noconfig", "-nostdlib", "-target:exe",
                "-langversion:12.0", "-nowarn:1701", "-out:" + staging,
                "-reference:" + Path.Combine(sdk.Roslyn, "Microsoft.CodeAnalysis.dll"),
                "-reference:" + Path.Combine(sdk.Roslyn, "Microsoft.CodeAnalysis.CSharp.dll"),
            };
            arguments.AddRange(FrameworkReferences.Select(reference => "-reference:" + Path.Combine(sdk.References, reference + ".dll")));
            arguments.Add(file);
            var compiled = await RunAsync(sdk.Dotnet, cache, arguments, 12, 2_000, ct);
            if (compiled.ExitCode != 0 || !File.Exists(staging)) return null;
            File.Move(staging, worker, overwrite: true);
            return worker;
        }
        finally
        {
            CompilerLock.Release();
        }
    }

    private static Sdk? FindSdk()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        var dotnet = Path.Combine(root, "dotnet.exe");
        if (!File.Exists(dotnet)) return null;
        try
        {
            var packs = VersionFolders(Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref")).ToArray();
            foreach (var (folder, version) in VersionFolders(Path.Combine(root, "sdk")))
            {
                var roslyn = Path.Combine(folder, "Roslyn", "bincore");
                var pack = packs.FirstOrDefault(pair => pair.Version.Major == version.Major).Folder;
                if (pack == null || !File.Exists(Path.Combine(roslyn, "csc.dll"))) continue;
                return new(dotnet, roslyn, Path.Combine(pack, "ref", $"net{version.Major}.0"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static IEnumerable<(string Folder, Version Version)> VersionFolders(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root)
                .Select(folder => (Folder: folder, Version: Version.TryParse(Path.GetFileName(folder), out var v) ? v : null))
                .Where(pair => pair.Version != null).OrderByDescending(pair => pair.Version)
                .Select(pair => (pair.Folder, pair.Version!))
            : [];

    public static string? GitExecutable(string root)
    {
        foreach (var part in (Environment.GetEnvironmentVariable("Path") ?? "").Split(Path.PathSeparator))
        {
            var path = part.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(path)) continue;
            var git = Path.Combine(path, "git.exe");
            if (File.Exists(git) && !Tools.PathHelper.IsInside(Tools.PathHelper.FinalPath(git), root)) return git;
        }
        return null;
    }

    public static Task<(int ExitCode, string Output)> GitAsync(string git, string root, IReadOnlyList<string> arguments,
        int limit, CancellationToken ct) =>
        RunAsync(git, root, ["--no-pager", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL",
            "-c", "core.quotePath=false", .. arguments], 4, limit, ct, git: true);

    private static async Task<(int ExitCode, string Output)> RunAsync(string exe, string root, IReadOnlyList<string> arguments,
        int seconds, int limit, CancellationToken ct, bool git = false)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        if (git)
        {
            foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
                start.Environment.Remove(key);
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
            start.Environment["GIT_NO_LAZY_FETCH"] = "1";
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        }
        else
        {
            start.Environment["TEMP"] = root;
            start.Environment["TMP"] = root;
        }
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return (-1, "");
            process.StandardInput.Close();
            using var time = CancellationTokenSource.CreateLinkedTokenSource(ct);
            time.CancelAfter(TimeSpan.FromSeconds(seconds));
            var output = ReadOutputAsync(process.StandardOutput, limit, time.Token);
            var errors = ReadOutputAsync(process.StandardError, 0, time.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(time.Token), output, errors);
                return (process.ExitCode, output.Result);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                ct.ThrowIfCancellationRequested();
                return (-1, "");
            }
        }
        catch (Win32Exception)
        {
            return (-1, "");
        }
    }

    private static async Task<string> ReadOutputAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        while (await reader.ReadAsync(buffer.AsMemory(), ct) is > 0 and var read)
            text.Append(buffer, 0, Math.Min(read, limit - text.Length));
        return text.ToString();
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
