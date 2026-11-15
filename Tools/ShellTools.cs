using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace SharpAgent.Tools;

public static class ShellTools
{
    private const int KeptBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static Encoding OemEncoding()
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

    private static async Task<string> RunCommandAsync(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var command = a.Required("command");
        var workDir = c.Resolve(a.Optional("working_directory"));
        if (!Directory.Exists(workDir)) throw new ToolException($"Папка не найдена: {workDir}");
        int timeout = Math.Clamp(a.Int("timeout_seconds", 120), 1, 1800);
        var shell = ShellOf(a);

        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        string? scriptPath = null;
        try
        {
            if (shell == CommandShell.Cmd)
            {
                psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
                if (command.Contains('\n'))
                {
                    scriptPath = Path.Combine(c.TempFolder, $"cmd_{Guid.NewGuid():N}.cmd");
                    await WriteScriptAsync(scriptPath, "@echo off\r\n" + command.ReplaceLineEndings("\r\n") + "\r\n", OemEncoding(), ct);
                    psi.Arguments = "/d /c \"" + scriptPath + "\"";
                }
                else
                {
                    psi.Arguments = "/d /s /c \"" + command + "\"";
                }
            }
            else
            {
                scriptPath = Path.Combine(c.TempFolder, $"cmd_{Guid.NewGuid():N}.ps1");
                var script =
                    "$ProgressPreference = 'SilentlyContinue'\r\n" +
                    "try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [Console]::OutputEncoding } catch {}\r\n" +
                    command + "\r\n" +
                    "if ($LASTEXITCODE) { exit $LASTEXITCODE }\r\n";
                await WriteScriptAsync(scriptPath, script, new UTF8Encoding(true), ct);
                psi.FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath })
                    psi.ArgumentList.Add(arg);
            }

            ct.ThrowIfCancellationRequested();
            using var process = new Process { StartInfo = psi };
            try { process.Start(); }
            catch (Win32Exception ex)
            {
                var restriction = CommandRestriction.LaunchDenied(shell, ex.NativeErrorCode);
                throw new CommandRestrictedException("Ошибка запуска. " + CommandRestriction.Prefix + restriction.Message, restriction);
            }
            process.StandardInput.Close();

            var stdout = new HeadTailBuffer(KeptBytes);
            var stderr = new HeadTailBuffer(KeptBytes);
            var reading = Task.WhenAll(stdout.FillFromAsync(process.StandardOutput.BaseStream),
                                       stderr.FillFromAsync(process.StandardError.BaseStream));

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));
            bool timedOut = false;
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                timedOut = true;
            }

            await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(3)));

            var output = stdout.Text();
            var errors = stderr.Text();
            int? exitCode = timedOut ? null : process.ExitCode;
            var restrictionAfterLaunch = CommandRestriction.FromOutput(shell, output, errors, scriptPath, exitCode);
            if (errors.Length > 0) output += (output.Length > 0 ? "\n" : "") + "[stderr]\n" + errors;
            if (restrictionAfterLaunch != null)
                output = CommandRestriction.Prefix + restrictionAfterLaunch.Message + "\n\n" + output;

            var status = timedOut ? $"{CommandLog.TimeoutPrefix} ({timeout} с)" : CommandLog.ExitPrefix + exitCode;
            var result = CommandLog.Summarize(status, output, c.LogsFolder);
            return restrictionAfterLaunch == null ? result : throw new CommandRestrictedException(result, restrictionAfterLaunch);
        }
        finally
        {
            try { if (scriptPath != null) File.Delete(scriptPath); } catch { }
        }
    }

    private static async Task WriteScriptAsync(string path, string script, Encoding encoding, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, script, encoding, ct);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new ToolException("Не удалось создать временный файл команды. Проверьте доступ на запись в папку Sharp Agent.");
        }
    }

    private static string Decode(byte[] bytes)
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

    private sealed class HeadTailBuffer(int capacity)
    {
        private readonly MemoryStream _head = new();
        private readonly byte[] _tail = new byte[capacity];
        private long _tailBytes;

        public async Task FillFromAsync(Stream stream)
        {
            var buffer = new byte[8192];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                    lock (_head) Append(buffer, read);
            }
            catch
            {
            }
        }

        private void Append(byte[] buffer, int count)
        {
            int toHead = (int)Math.Min(count, capacity - _head.Length);
            _head.Write(buffer, 0, toHead);
            for (int i = toHead; i < count; i++) _tail[_tailBytes++ % capacity] = buffer[i];
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

        private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;
    }
}
