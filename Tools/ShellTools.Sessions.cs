namespace SharpAgent.Tools;

partial class ShellTools
{
    static JsonObject Parameters()
    {
        var schema = Schema.Object(new Prop("command", "string", "Command to run."),
            new Prop("working_directory", "string", "Working directory."), new Prop("shell", "string", "Shell, defaults to PowerShell.", Enum: ["powershell", "cmd"]),
            new Prop("timeout_seconds", "integer", "Deadline, at most 1800 seconds."),
            new Prop("yield_ms", "integer", "Omit to wait for the final result in one call. Set only if you have independent work to do while the command runs. Build and test checks always wait."),
            new Prop("session_id", "string", "Wait for the final result of a running command session."));
        schema["properties"]!["commands"] = new JsonObject { ["type"] = "array", ["maxItems"] = 6,
            ["description"] = "Run independent commands in the same directory.", ["items"] = new JsonObject { ["type"] = "string" } };
        return schema;
    }

    static string[] Commands(ToolArgs args)
    {
        var batch = args.Array("commands");
        if (batch.Count == 0) return [args.Required("command")];
        var commands = batch.Select(Str).OfType<string>().ToArray();
        if (commands.Length != batch.Count || commands.Length > 6 || commands.Any(string.IsNullOrWhiteSpace) || args.Optional("command") != null)
            throw new ToolException("Укажите одну команду либо до шести независимых команд.");
        return commands;
    }

    internal static string? GitInspectionOf(ToolArgs args)
    {
        if (args.Optional("session_id") != null) return null;
        var checks = Commands(args).SelectMany(command => command.Split(';', StringSplitOptions.TrimEntries)).ToArray();
        return checks.All(check => GitInspection().IsMatch(check)) ? string.Join("; ", checks.Select(check => Regex.Replace(check, @"[ \t]+", " "))) : null;
    }

    internal static string? FileInspectionOf(ToolArgs args)
    {
        if (ShellOf(args) != CommandShell.PowerShell || args.Optional("session_id") != null || args.Int("yield_ms", 0) > 0 ||
            args.Array("commands").Count > 0 || args.Optional("command") is not { Length: <= 4096 } command) return null;
        var match = FileInspection().Match(command);
        return match.Success ? match.Groups["path"].Value : null;
    }

    [GeneratedRegex("""
        \A\s*(?:
            Get-Content\s+(?:-(?:Literal)?Path\s+)?(?<quote>['"])(?<path>[\p{L}\p{N}._/\\\x20-]+)\k<quote>
            (?:\s+-Encoding\s+UTF8)?(?:\s*\|\s*Select-Object\s+-Skip\s+\d{1,9}\s+-First\s+\d{1,9})?
            |
            \$p\s*=\s*(?<quote>['"])(?<path>[\p{L}\p{N}._/\\\x20-]+)\k<quote>\s*;\s*
            \$a\s*=\s*Get-Content\s+(?:-(?:Literal)?Path\s+)?\$p(?:\s+-Encoding\s+UTF8)?\s*;\s*
            for\s*\(\s*\$i\s*=\s*\d{1,9}\s*;\s*\$i\s+-le\s+\d{1,9}\s*;\s*\$i\+\+\s*\)\s*\{\s*
            (?<guard>if\s*\(\s*\$i\s+-le\s+\$a\.(?:Length|Count)\s*\)\s*\{\s*)?
            (?:(?<format>['"])\{0(?:,-?\d{1,2})?\}:\s*\{1\}\k<format>\s+-f\s+\$i\s*,\s*\$a\[\s*\$i\s*-\s*1\s*\]
                |"\$i`:\s*\$\(\$a\[\s*\$i\s*-\s*1\s*\]\)")\s*;?\s*(?(guard)\}\s*)\}
        )\s*;?\s*\z
        """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex FileInspection();

    [GeneratedRegex(@"\Agit[ \t]+(?:status(?:[ \t]+(?:--short|--branch|-s|-b|--porcelain(?:=v[12])?))*|diff(?:[ \t]+(?:--stat|--name-only|--name-status|--cached|--staged|--no-ext-diff|--no-textconv))*(?:[ \t]+--(?:[ \t]+[\w.\\/-]+)*)?)\z")]
    private static partial Regex GitInspection();

    internal sealed class Sessions
    {
        const string RunningPrefix = "Команда выполняется. session_id=";
        (string Id, Task<string> Result, CancellationTokenSource Cancellation, string? Command)? _active;
        internal string? PendingOutput => _active is { } active ? Running(active.Id) : null;

        internal async Task<string> RunAsync(ToolArgs args, ToolContext context, CancellationToken ct)
        {
            if (context.SandboxRoot != null) throw new ToolException("Команды отключены в режиме песочницы.");
            if (args.Optional("session_id") is { } id)
            {
                if (_active is not { } session || session.Id != id) throw new ToolException("Сессия команды не найдена.");
                args.CompletedCommand = session.Command;
                try { return await session.Result.WaitAsync(ct); }
                finally
                {
                    if (session.Result.IsCompleted)
                    {
                        _active = null;
                        session.Cancellation.Dispose();
                    }
                }
            }
            if (_active != null) throw new ToolException("Сначала получите результат запущенной команды.");
            var commands = Commands(args);
            int yield = args.Int("yield_ms", 0);
            if (yield <= 0 || commands.All(ProjectVerification.Command)) return await ExecuteAsync(ct);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var result = ExecuteAsync(cancellation.Token);
            await Task.WhenAny(result, Task.Delay(Math.Clamp(yield, 100, 10_000), ct));
            if (result.IsCompleted || ct.IsCancellationRequested)
            {
                using (cancellation) return await result;
            }
            var sessionId = Guid.NewGuid().ToString("N");
            _active = (sessionId, result, cancellation, commands.Length == 1 ? commands[0] : null);
            return Running(sessionId);

            async Task<string> ExecuteAsync(CancellationToken token)
            {
                var outputs = await Task.WhenAll(commands.Select(command => Task.Run(() => RunCommandAsync(new ToolArgs(new JsonObject
                {
                    ["command"] = command, ["shell"] = args.Optional("shell"), ["working_directory"] = args.Optional("working_directory"),
                    ["timeout_seconds"] = args.Int("timeout_seconds", 120),
                }), context, token, true), token)));
                if (outputs.Length == 1) return outputs[0];
                return CommandLog.ExitPrefix + (outputs.All(output => CommandLog.ResultOf(output) == "код 0") ? "0" : "1") +
                    "\n" + string.Join("\n\n", outputs.Select((output, index) => $"Команда {index + 1}\n{output}"));
            }
        }

        internal static string Running(string id) => $"{RunningPrefix}{id}. Получи результат через exec_command.";

        internal static string? RunningId(string? output) => output != null && output.StartsWith(RunningPrefix, StringComparison.Ordinal) &&
            output.Length >= RunningPrefix.Length + 32 && output.Substring(RunningPrefix.Length, 32) is var id &&
            Guid.TryParseExact(id, "N", out _) && output == Running(id) ? id : null;

        internal async Task CancelAsync()
        {
            if (_active is not { } session) return;
            _active = null;
            using var cancellation = session.Cancellation;
            cancellation.Cancel();
            try { await session.Result; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }
}
