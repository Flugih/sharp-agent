namespace SharpAgent.Tools;

partial class ShellTools
{
    static JsonObject Parameters()
    {
        var schema = Schema.Object(new Prop("command", "string", "Command to run."),
            new Prop("working_directory", "string", "Working directory."), new Prop("shell", "string", "Shell, defaults to PowerShell.", Enum: ["powershell", "cmd"]),
            new Prop("timeout_seconds", "integer", "Deadline, at most 1800 seconds."),
            new Prop("yield_ms", "integer", "Wait briefly and return a session ID if still running."),
            new Prop("session_id", "string", "Poll a running command session."));
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

    internal sealed class Sessions
    {
        (string Id, Task<string> Result, CancellationTokenSource Cancellation, string? Command)? _active;
        internal string? PendingOutput => _active is { } active ? Running(active.Id) : null;

        internal async Task<string> RunAsync(ToolArgs args, ToolContext context, CancellationToken ct)
        {
            if (context.SandboxRoot != null) throw new ToolException("Команды отключены в режиме песочницы.");
            int wait = Math.Clamp(args.Int("yield_ms", 1000), 100, 10_000);
            if (args.Optional("session_id") is { } id)
            {
                if (_active is not { } session || session.Id != id) throw new ToolException("Сессия команды не найдена.");
                await Task.WhenAny(session.Result, Task.Delay(wait, ct));
                ct.ThrowIfCancellationRequested();
                if (!session.Result.IsCompleted) return Running(id);
                _active = null;
                args.CompletedCommand = session.Command;
                using (session.Cancellation) return await session.Result;
            }
            if (_active != null) throw new ToolException("Сначала получите результат запущенной команды.");
            var commands = Commands(args);
            if (args.Int("yield_ms", 0) <= 0) return await ExecuteAsync(ct);
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var result = ExecuteAsync(cancellation.Token);
            await Task.WhenAny(result, Task.Delay(wait, ct));
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

        static string Running(string id) => $"Команда выполняется. session_id={id}. Получи результат через exec_command.";

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
