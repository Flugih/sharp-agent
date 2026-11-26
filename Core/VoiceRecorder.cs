using System.Collections.Concurrent;
using Microsoft.Win32;

namespace SharpAgent.Core;

public sealed class MicrophoneException(string message, bool privacy = false) : Exception(message)
{
    public bool Privacy { get; } = privacy;
}

public sealed class VoiceRecorder
{
    public const int MaxMinutes = 10;
    internal const int SampleRate = 16_000;
    const int BytesPerSecond = SampleRate * 2, MaxBytes = MaxMinutes * 60 * BytesPerSecond;
    internal const int WavHeaderBytes = 44;
    const int BufferBytes = BytesPerSecond / 20, BufferCount = 8;
    const int WaveMapper = -1, CallbackEvent = 0x50000, HeaderDone = 0x1, HeaderInQueue = 0x10;
    const int BadDeviceId = 2, Allocated = 4, NoDriver = 6;
    const int SilenceFloor = 64;
    const double LevelRange = 50;

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHeader
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public int Flags;
        public int Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormat
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSecond;
        public int AverageBytesPerSecond;
        public short BlockAlign;
        public short BitsPerSample;
        public short ExtraSize;
    }

    [DllImport("winmm.dll")] static extern int waveInOpen(out nint device, int deviceId, ref WaveFormat format, nint callback, nint instance, int flags);
    [DllImport("winmm.dll")] static extern int waveInPrepareHeader(nint device, nint header, int size);
    [DllImport("winmm.dll")] static extern int waveInUnprepareHeader(nint device, nint header, int size);
    [DllImport("winmm.dll")] static extern int waveInAddBuffer(nint device, nint header, int size);
    [DllImport("winmm.dll")] static extern int waveInStart(nint device);
    [DllImport("winmm.dll")] static extern int waveInReset(nint device);
    [DllImport("winmm.dll")] static extern int waveInClose(nint device);

    static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));
    static readonly int RecordedOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.BytesRecorded));
    readonly AutoResetEvent _signal = new(false);
    readonly nint[] _headers = new nint[BufferCount];
    readonly byte[] _chunk = new byte[BufferBytes];
    readonly MemoryStream _wav = new();
    readonly TaskCompletionSource<byte[]> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Action<float[]>? _samples;
    nint _device;
    int _next, _loudest;
    volatile int _bytes;
    volatile bool _stop, _discard;

    VoiceRecorder(Action<float[]>? samples = null)
    {
        _samples = samples;
        var format = new WaveFormat
            { FormatTag = 1, Channels = 1, SamplesPerSecond = SampleRate, AverageBytesPerSecond = BytesPerSecond, BlockAlign = 2, BitsPerSample = 16 };
        int result = waveInOpen(out _device, WaveMapper, ref format, _signal.SafeWaitHandle.DangerousGetHandle(), 0, CallbackEvent);
        if (result != 0)
        {
            _signal.Dispose();
            throw Failure(result);
        }
        try
        {
            for (int i = 0; i < BufferCount; i++)
            {
                var data = Marshal.AllocHGlobal(BufferBytes);
                _headers[i] = Marshal.AllocHGlobal(HeaderSize);
                Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = BufferBytes }, _headers[i], false);
                Check(waveInPrepareHeader(_device, _headers[i], HeaderSize));
                Check(waveInAddBuffer(_device, _headers[i], HeaderSize));
            }
            Check(waveInStart(_device));
        }
        catch
        {
            Release();
            throw;
        }
        _wav.SetLength(WavHeaderBytes);
        _wav.Position = WavHeaderBytes;
        new Thread(Record) { IsBackground = true, Name = "Microphone" }.Start();
    }

    public ConcurrentQueue<float> Levels { get; } = new();
    public TimeSpan Length => TimeSpan.FromSeconds((double)_bytes / BytesPerSecond);
    public bool Silent => _loudest < SilenceFloor;
    public bool Full => _samples == null && _bytes >= MaxBytes;
    public bool Ended => _done.Task.IsCompleted;

    public static VoiceRecorder Start() => new();
    internal static VoiceRecorder Start(Action<float[]> samples) => new(samples);
    internal Task<byte[]> Completion => _done.Task;

    public Task<byte[]> StopAsync()
    {
        _stop = true;
        return _done.Task;
    }

    public void Cancel() => _stop = _discard = true;

    public static MicrophoneException SilenceProblem() => AccessDenied() ? PrivacyProblem()
        : new("Микрофон не передаёт звук. Проверьте, что он подключён и включён в Параметры → Система → Звук, и запишите ещё раз.");

    void Record()
    {
        byte[] wav = [];
        try
        {
            while (!_stop)
            {
                _signal.WaitOne(200);
                if (!Collect(requeue: true)) break;
            }
            ReturnBuffers();
            Collect(requeue: false);
            if (!_discard && _samples == null) wav = Finish();
        }
        catch (Exception ex) { _done.TrySetException(ex); }
        finally
        {
            Release();
            _done.TrySetResult(wav);
        }
    }

    bool Collect(bool requeue)
    {
        for (int i = 0; i < BufferCount; i++)
        {
            var header = _headers[_next];
            int flags = Marshal.ReadInt32(header, FlagsOffset);
            if ((flags & HeaderDone) == 0) return true;
            if (requeue || _samples == null) Take(Marshal.ReadIntPtr(header), Marshal.ReadInt32(header, RecordedOffset));
            Marshal.WriteInt32(header, RecordedOffset, 0);
            Marshal.WriteInt32(header, FlagsOffset, flags & ~HeaderDone);
            _next = (_next + 1) % BufferCount;
            if (requeue && (Full || waveInAddBuffer(_device, header, HeaderSize) != 0)) return false;
        }
        return true;
    }

    void Take(nint data, int count)
    {
        count = Math.Min(count, _samples == null ? Math.Min(BufferBytes, MaxBytes - _bytes) : BufferBytes) & ~1;
        if (count <= 0) return;
        Marshal.Copy(data, _chunk, 0, count);
        if (_samples == null) _wav.Write(_chunk, 0, count);
        var samples = _samples == null ? null : new float[count / 2];
        double power = 0;
        for (int i = 0; i < count; i += 2)
        {
            int sample = (short)(_chunk[i] | _chunk[i + 1] << 8);
            if (samples != null) samples[i / 2] = sample / 32768f;
            power += (double)sample * sample;
            _loudest = Math.Max(_loudest, Math.Abs(sample));
        }
        _bytes = Math.Min(MaxBytes, _bytes + count);
        double decibels = 10 * Math.Log10(power / (count / 2) / (32768.0 * 32768.0) + 1e-10);
        Levels.Enqueue((float)Math.Clamp(1 + decibels / LevelRange, 0, 1));
        if (samples != null) _samples!(samples);
    }

    byte[] Finish()
    {
        int data = (int)_wav.Length - WavHeaderBytes;
        _wav.Position = 0;
        using var writer = new BinaryWriter(_wav, Encoding.ASCII, leaveOpen: true);
        WriteWaveHeader(writer, SampleRate, data);
        return _wav.ToArray();
    }

    internal static byte[] EncodeWave(float[] samples)
    {
        int length = samples.Length * sizeof(short);
        using var stream = new MemoryStream(WavHeaderBytes + length);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        WriteWaveHeader(writer, SampleRate, length);
        foreach (var sample in samples) writer.Write((short)(Math.Clamp(sample, -1, 1) * short.MaxValue));
        return stream.ToArray();
    }

    internal static void WriteWaveHeader(BinaryWriter writer, int rate, int data)
    {
        writer.Write("RIFF"u8);
        writer.Write(WavHeaderBytes - 8 + data);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(rate);
        writer.Write(rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(data);
    }

    void ReturnBuffers()
    {
        waveInReset(_device);
        for (int i = 0; i < 20 && _headers.Any(header => header != 0 && (Marshal.ReadInt32(header, FlagsOffset) & HeaderInQueue) != 0); i++)
            _signal.WaitOne(50);
    }

    void Release()
    {
        ReturnBuffers();
        var headers = _headers.Where(header => header != 0).ToList();
        foreach (var header in headers) waveInUnprepareHeader(_device, header, HeaderSize);
        waveInClose(_device);
        foreach (var header in headers)
        {
            Marshal.FreeHGlobal(Marshal.ReadIntPtr(header));
            Marshal.FreeHGlobal(header);
        }
        _signal.Dispose();
    }

    static void Check(int result)
    {
        if (result != 0) throw Failure(result);
    }

    static MicrophoneException Failure(int result) => AccessDenied() ? PrivacyProblem() : new(result switch
    {
        BadDeviceId or NoDriver => "Микрофон не найден. Подключите микрофон или гарнитуру и нажмите на кнопку ещё раз.",
        Allocated => "Микрофон занят другой программой. Закройте её и нажмите на кнопку ещё раз.",
        _ => $"Не удалось включить микрофон (код {result}). Проверьте, что он подключён, и нажмите на кнопку ещё раз.",
    });

    static MicrophoneException PrivacyProblem() => new(
        "Windows не даёт программам доступ к микрофону. Откройте Параметры → Конфиденциальность и защита → Микрофон " +
        "и включите «Доступ к микрофону» и «Разрешить классическим приложениям доступ к микрофону».", privacy: true);

    static bool AccessDenied()
    {
        const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        return Denied(Registry.LocalMachine, store) || Denied(Registry.CurrentUser, store) || Denied(Registry.CurrentUser, store + @"\NonPackaged");
    }

    static bool Denied(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
