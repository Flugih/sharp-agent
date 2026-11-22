namespace SharpAgent.Tools;

public static partial class ShellTools
{
    const int PreviewBytes = 256 * 1024, LogBytes = 8 * 1024 * 1024;
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [DllImport("kernel32.dll")]
    static extern uint GetOEMCP();

    static Encoding OemEncoding()
    {
        try { return Encoding.GetEncoding((int)GetOEMCP()); }
        catch { return Encoding.GetEncoding(866); }
    }

    internal static IEnumerable<AgentTool> All(Sessions sessions) =>
    [
        new AgentTool
        {
            Name = "exec_command",
            Description = "Run PowerShell or cmd to search, read files, use Git, build and test. Use rg to search. Batch independent commands. Poll long-running sessions and retrieve saved output with read_log.",
            Parameters = Parameters(),
            Handler = sessions.RunAsync,
            Confirmation = (a, c) => a.Optional("session_id") != null ? null : new ConfirmRequest(
                ShellOf(a) == CommandShell.Cmd ? "Выполнить команду cmd?" : "Выполнить команду PowerShell?",
                string.Join("\n", Commands(a)), "Выполнить", Danger: true,
                Note: "Папка: " + c.Resolve(a.Optional("working_directory"))),
        },
    ];

    public static Task<string> RunPowerShellAsync(string command, ToolContext c, CancellationToken ct, int timeout = 120, bool saveLog = true) =>
        RunCommandAsync(new ToolArgs(new JsonObject { ["command"] = command, ["timeout_seconds"] = timeout }), c, ct, saveLog);

    internal static CommandShell ShellOf(ToolArgs a) => string.Equals(a.Optional("shell"), "cmd", StringComparison.OrdinalIgnoreCase)
        ? CommandShell.Cmd : CommandShell.PowerShell;

    static async Task<string> RunCommandAsync(ToolArgs a, ToolContext c, CancellationToken ct, bool saveLog)
    {
        var command = a.Required("command");
        var workDir = c.Resolve(a.Optional("working_directory"));
        if (!Directory.Exists(workDir)) throw new ToolException($"Папка не найдена: {workDir}");
        var rgDir = Path.Combine(AppContext.BaseDirectory, "tools", "ripgrep");
        var rgPath = Path.Combine(rgDir, "rg.exe");
        bool bundled = File.Exists(rgPath);
        string original = command;
        var rg = Regex.Match(command, @"^\s*rg(?:\.exe)?(?=\s|$)", RegexOptions.IgnoreCase);
        if (!bundled && saveLog && rg.Success && Fallback("Встроенный rg.exe отсутствует.") is { } missing) return missing;
        int timeout = Math.Clamp(a.Int("timeout_seconds", 120), 1, 1800);
        var shell = ShellOf(a);
        if (bundled && rg.Success)
            command = (shell == CommandShell.Cmd ? $"\"{rgPath}\"" : "& '" + rgPath.Replace("'", "''") + "'") + command[rg.Length..];
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var proxy = AppNetwork.ConfigureCommand(psi);
        if (bundled)
        {
            psi.Environment.TryGetValue("PATH", out var path);
            psi.Environment["PATH"] = rgDir + Path.PathSeparator + path;
        }
        var proxyCredential = AppNetwork.ProxyCredential;
        string? scriptPath = null;
        async Task<string> WriteScriptAsync(string extension, byte[] script)
        {
            scriptPath = Path.Combine(c.TempFolder, $"cmd_{Guid.NewGuid():N}{extension}");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
                await File.WriteAllBytesAsync(scriptPath, script, ct);
                return "\"" + scriptPath + "\"";
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                throw new ToolException("Не удалось создать временный файл команды. Проверьте доступ на запись в папку Sharp Agent.");
            }
        }
        try
        {
            if (shell == CommandShell.Cmd)
            {
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                psi.Arguments = command.Contains('\n') ? "/d /c " + await WriteScriptAsync(".cmd", CmdScript(command))
                    : "/d /s /c \"" + command + "\"";
            }
            else
            {
                var script = "$ProgressPreference = 'SilentlyContinue'\r\n" +
                    "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [Console]::OutputEncoding } catch {}\r\n" +
                    (proxy == null ? "" : "[Net.WebRequest]::DefaultWebProxy = New-Object Net.WebProxy" + (proxy.Length > 0 ? $" '{proxy}'" : "") + "\r\n") +
                    command + "\r\nif (-not $?) { if ($LASTEXITCODE) { exit $LASTEXITCODE }; exit 1 }; if ($LASTEXITCODE) { exit $LASTEXITCODE }\r\n";
                psi.Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                psi.FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            }
            ct.ThrowIfCancellationRequested();
            using var process = new Process { StartInfo = psi };
            try { process.Start(); }
            catch (Win32Exception ex)
            {
                var denied = CommandRestriction.LaunchDenied(shell, ex.NativeErrorCode);
                throw new CommandRestrictedException("Ошибка запуска. " + CommandRestriction.Prefix + denied.Message, denied);
            }
            process.StandardInput.Close();
            int capacity = saveLog && c.LogsFolder != null ? LogBytes : PreviewBytes;
            var stdout = new HeadTailBuffer(capacity);
            var stderr = new HeadTailBuffer(capacity);
            var reading = Task.WhenAll(stdout.FillFromAsync(process.StandardOutput.BaseStream),
                stderr.FillFromAsync(process.StandardError.BaseStream));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
            bool timedOut = false;
            try { await process.WaitForExitAsync(timeoutCts.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                timedOut = true;
            }
            await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(3)));
            var output = Redact(stdout.Text());
            var errors = Redact(stderr.Text());
            string Redact(string value)
            {
                if (proxyCredential?.Password is not { Length: >= 6 } secret) return value;
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(proxyCredential.UserName + ":" + secret));
                return value.Replace(basic, "***").Replace(Uri.EscapeDataString(secret), "***").Replace(secret, "***");
            }
            int? exitCode = timedOut ? null : process.ExitCode;
            var restriction = CommandRestriction.FromOutput(shell, output, errors, scriptPath, exitCode);
            if (bundled && rg.Success && exitCode is not (null or 0) && saveLog &&
                CommandRestriction.RipgrepLaunchFailure(errors, restriction) is { } reason &&
                Fallback(reason) is { } alternative) return alternative;
            if (errors.Length > 0) output += (output.Length > 0 ? "\n" : "") + "[stderr]\n" + errors;
            if (restriction != null) output = CommandRestriction.Prefix + restriction.Message + "\n\n" + output;
            var result = CommandLog.Summarize(timedOut ? $"{CommandLog.TimeoutPrefix} ({timeout} с)" : CommandLog.ExitPrefix + exitCode,
                output, saveLog ? c.LogsFolder : null);
            return restriction == null ? result : throw new CommandRestrictedException(result, restriction);
        }
        finally
        {
            try { if (scriptPath != null) File.Delete(scriptPath); } catch { }
        }

        string? Fallback(string reason) => LocalGrep.Run(original, workDir, c, ct) is { } fallback
            ? CommandLog.Summarize(CommandLog.ExitPrefix + fallback.Code, reason + " Использован локальный поиск.\n" + fallback.Output, c.LogsFolder)
            : null;
    }

    static byte[] CmdScript(string command)
    {
        var oem = (Encoding)OemEncoding().Clone();
        oem.EncoderFallback = EncoderFallback.ExceptionFallback;
        try { return oem.GetBytes("@echo off\r\n" + command.ReplaceLineEndings("\r\n") + "\r\n"); }
        catch (EncoderFallbackException)
        {
            throw new ToolException($"Многострочная команда cmd выполняется из файла в кодировке консоли ({oem.WebName}), а в ней есть " +
                "символы вне этой кодировки — cmd выполнил бы искажённую команду. Используй PowerShell или одну строку.");
        }
    }

    static string Decode(byte[] bytes)
    {
        var oem = OemEncoding();
        var sb = new StringBuilder();
        ReadOnlySpan<byte> data = bytes;
        while (true)
        {
            int end = data.IndexOf((byte)'\n');
            var line = end < 0 ? data : data[..end];
            try { sb.Append(StrictUtf8.GetString(line)); }
            catch (DecoderFallbackException) { sb.Append(oem.GetString(line)); }
            sb.Append('\n');
            if (end < 0) break;
            data = data[(end + 1)..];
        }
        return sb.ToString().Replace("\r", "").Replace("\uFEFF", "").TrimEnd();
    }

    sealed class HeadTailBuffer(int capacity)
    {
        readonly MemoryStream _head = new();
        readonly byte[] _tail = new byte[PreviewBytes];
        long _tailBytes;

        public async Task FillFromAsync(Stream stream)
        {
            var buffer = new byte[8192];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                    lock (_head)
                    {
                        int toHead = (int)Math.Min(read, capacity - _head.Length);
                        _head.Write(buffer, 0, toHead);
                        for (int i = toHead; i < read; i++) _tail[_tailBytes++ % _tail.Length] = buffer[i];
                    }
            }
            catch { }
        }

        public string Text()
        {
            lock (_head)
            {
                if (_tailBytes <= _tail.Length) return Decode([.. _head.ToArray(), .. _tail.AsSpan(0, (int)_tailBytes)]);
                int start = (int)(_tailBytes % _tail.Length);
                byte[] head = _head.ToArray(), tail = [.. _tail.AsSpan(start), .. _tail.AsSpan(0, start)];
                int end = head.Length, cut = Array.IndexOf(tail, (byte)'\n', 0, tail.Length - 1) + 1;
                while (end > 0 && IsContinuation(head[end - 1])) end--;
                if (end > 0 && head[end - 1] >= 0xC0) end--;
                if (cut == 0) while (cut < tail.Length && IsContinuation(tail[cut])) cut++;
                return Decode(head[..end]) + $"\n{CommandLog.GapPrefix}{_tailBytes - _tail.Length + cut + head.Length - end} байт вывода …\n" +
                    Decode(tail[cut..]);
            }
        }

        static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;
    }
}
