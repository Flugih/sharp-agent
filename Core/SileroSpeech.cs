using System.Buffers.Binary;

namespace SharpAgent.Core;

sealed class SileroSpeech(string model, bool verifyArchive = false) : IDisposable
{
    internal const int SampleRate = 24_000;
    const int MaxPcmBytes = SampleRate * 2 * 180;
    static readonly Lazy<string> Script = new(() =>
    {
        using var stream = typeof(SileroSpeech).Assembly.GetManifestResourceStream("SharpAgent.Voice.silero.py")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
    Process? _process;

    internal void Prepare() => _process ??= Start(Path.Combine(VoicePackage.Root, VoicePackage.Runtime, "python.exe"),
        ["-B", "-u", "-c", Script.Value, model, SampleRate.ToString(CultureInfo.InvariantCulture), verifyArchive ? "1" : "0"]);

    internal async Task<byte[]> GenerateAsync(string text, string speaker, double speed, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            ct.ThrowIfCancellationRequested();
            Prepare();
            var process = _process!;
            var request = JsonSerializer.Serialize(new { text, speaker, speed });
            await process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token).ConfigureAwait(false);
            var output = process.StandardOutput.BaseStream;
            var header = new byte[4];
            await output.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            int size = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (size < 0 || size > MaxPcmBytes || (size & 1) != 0) throw new InvalidDataException("Silero не удалось синтезировать речь.");
            var samples = new byte[size];
            await output.ReadExactlyAsync(samples, timeout.Token).ConfigureAwait(false);
            return samples;
        }
        catch (EndOfStreamException ex)
        {
            Dispose();
            throw new InvalidDataException("Silero не удалось загрузить модель или синтезировать речь.", ex);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Dispose();
            throw new IOException("Silero слишком долго готовил озвучку.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal static async Task ValidateRuntimeAsync(string python, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = Start(python, ["-B", "-c", "import torch; assert torch.ones(1).item() == 1"]);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Не удалось запустить PyTorch для Silero.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("PyTorch для Silero не запустился вовремя.");
        }
        finally { Stop(process); }
    }

    static Process Start(string python, string[] args)
    {
        var folder = Path.GetDirectoryName(python)!;
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        start.Environment.Clear();
        start.Environment["SystemRoot"] = Path.GetDirectoryName(Environment.SystemDirectory);
        start.Environment["PATH"] = Environment.SystemDirectory;
        start.Environment["TMP"] = start.Environment["TEMP"] = folder;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new IOException("Не удалось запустить Silero.");
        process.BeginErrorReadLine();
        return process;
    }

    static void Stop(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        process.WaitForExit(1500);
    }

    public void Dispose()
    {
        if (_process is not { } process) return;
        _process = null;
        using (process) Stop(process);
    }
}
