namespace SharpAgent.Tools;

public static class ShellTools
{
    const int KeptBytes = 256 * 1024;
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [DllImport("kernel32.dll")]
    static extern uint GetOEMCP();

    static Encoding OemEncoding()
    {
        try { return Encoding.GetEncoding((int)GetOEMCP()); }
        catch { return Encoding.GetEncoding(866); }
    }

    public static IEnumerable<AgentTool> All() =>
    [
        new AgentTool
        {
            Name = "run_command",
            Description = "Выполнить команду PowerShell (по умолчанию) или cmd от имени текущего пользователя, БЕЗ прав администратора. " +
                "Возвращает код выхода, важные строки (ошибки, предупреждения, итоги тестов и сборки) и начало с концом " +
                "вывода; сам вывод сохраняется в журнал (у очень длинного — начало и конец). Дочитывай его через " +
                "read_log, не запуская команду снова. " +
                "Используй для того, что не покрывают файловые инструменты: архивы, git, python, сведения о процессах и т.п. " +
                "Интерактивный ввод недоступен — команды должны работать без вопросов.",
            Parameters = Schema.Object(
                new Prop("command", "string", "Команда или небольшой скрипт.", Required: true),
                new Prop("shell", "string", "Оболочка: powershell (по умолчанию) или cmd.", Enum: ["powershell", "cmd"]),
                new Prop("working_directory", "string", "Папка, в которой выполнить команду. По умолчанию — рабочая папка."),
                new Prop("timeout_seconds", "integer", "Таймаут в секундах (по умолчанию 120, максимум 1800).")),
            Handler = RunCommandAsync,
            Confirmation = (a, c) => new ConfirmRequest(
                ShellOf(a) == CommandShell.Cmd ? "Выполнить команду cmd?" : "Выполнить команду PowerShell?",
                a.Required("command"), "Выполнить", Danger: true,
                Note: "Папка: " + c.Resolve(a.Optional("working_directory"))),
        },
    ];

    public static Task<string> RunPowerShellAsync(string command, ToolContext c, CancellationToken ct) =>
        RunCommandAsync(new ToolArgs(new JsonObject { ["command"] = command }), c, ct);

    internal static CommandShell ShellOf(ToolArgs a) => string.Equals(a.Optional("shell"), "cmd", StringComparison.OrdinalIgnoreCase)
        ? CommandShell.Cmd : CommandShell.PowerShell;

    static async Task<string> RunCommandAsync(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var command = a.Required("command");
        var workDir = c.Resolve(a.Optional("working_directory"));
        if (!Directory.Exists(workDir)) throw new ToolException($"Папка не найдена: {workDir}");
        int timeout = Math.Clamp(a.Int("timeout_seconds", 120), 1, 1800);
        var shell = ShellOf(a);
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
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
                psi.Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + await WriteScriptAsync(".ps1",
                    Encoding.UTF8.GetBytes("\uFEFF$ProgressPreference = 'SilentlyContinue'\r\n" +
                    "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [Console]::OutputEncoding } catch {}\r\n" +
                    command + "\r\nif ($LASTEXITCODE) { exit $LASTEXITCODE }\r\n"));
                psi.FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            }
            var proxyCredential = await AppNetwork.ConfigureCommandAsync(psi, ct);
            ct.ThrowIfCancellationRequested();
            using var process = new Process { StartInfo = psi };
            try { process.Start(); }
            catch (Win32Exception ex)
            {
                var denied = CommandRestriction.LaunchDenied(shell, ex.NativeErrorCode);
                throw new CommandRestrictedException("Ошибка запуска. " + CommandRestriction.Prefix + denied.Message, denied);
            }
            process.StandardInput.Close();
            var stdout = new HeadTailBuffer(KeptBytes);
            var stderr = new HeadTailBuffer(KeptBytes);
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
                if (proxyCredential?.Password is not { Length: > 0 } secret) return value;
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(proxyCredential.UserName + ":" + secret));
                return value.Replace(basic, "***").Replace(Uri.EscapeDataString(secret), "***").Replace(secret, "***");
            }
            int? exitCode = timedOut ? null : process.ExitCode;
            var restriction = CommandRestriction.FromOutput(shell, output, errors, scriptPath, exitCode);
            if (errors.Length > 0) output += (output.Length > 0 ? "\n" : "") + "[stderr]\n" + errors;
            if (restriction != null) output = CommandRestriction.Prefix + restriction.Message + "\n\n" + output;
            var result = CommandLog.Summarize(timedOut ? $"{CommandLog.TimeoutPrefix} ({timeout} с)" : CommandLog.ExitPrefix + exitCode,
                output, c.LogsFolder);
            return restriction == null ? result : throw new CommandRestrictedException(result, restriction);
        }
        finally
        {
            try { if (scriptPath != null) File.Delete(scriptPath); } catch { }
        }
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
        readonly byte[] _tail = new byte[capacity];
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
                        for (int i = toHead; i < read; i++) _tail[_tailBytes++ % capacity] = buffer[i];
                    }
            }
            catch { }
        }

        public string Text()
        {
            lock (_head)
            {
                if (_tailBytes <= capacity) return Decode([.. _head.ToArray(), .. _tail.AsSpan(0, (int)_tailBytes)]);
                int start = (int)(_tailBytes % capacity);
                byte[] head = _head.ToArray(), tail = [.. _tail.AsSpan(start), .. _tail.AsSpan(0, start)];
                int end = head.Length, cut = Array.IndexOf(tail, (byte)'\n', 0, tail.Length - 1) + 1;
                while (end > 0 && IsContinuation(head[end - 1])) end--;
                if (end > 0 && head[end - 1] >= 0xC0) end--;
                if (cut == 0) while (cut < tail.Length && IsContinuation(tail[cut])) cut++;
                return Decode(head[..end]) + $"\n{CommandLog.GapPrefix}{_tailBytes - capacity + cut + head.Length - end} байт вывода …\n" +
                    Decode(tail[cut..]);
            }
        }

        static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;
    }
}
