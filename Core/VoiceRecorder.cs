using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SharpAgent.Core;

public sealed class MicrophoneException(string message, bool privacy = false) : Exception(message)
{
    public bool Privacy { get; } = privacy;
}

public sealed class VoiceRecorder
{
    public const int MaxMinutes = 10;

    private const int SampleRate = 16_000, BytesPerSecond = SampleRate * 2, MaxBytes = MaxMinutes * 60 * BytesPerSecond;
    private const int BufferBytes = BytesPerSecond / 20, BufferCount = 8, WavHeaderBytes = 44;
    private const int WaveMapper = -1, CallbackEvent = 0x50000, HeaderDone = 0x1, HeaderInQueue = 0x10;
    private const int BadDeviceId = 2, Allocated = 4, NoDriver = 6;
    private const int SilenceFloor = 64;
    private const double LevelRange = 50;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
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
    private struct WaveFormat
    {
        public short FormatTag;
        public short Channels;
        public int SamplesPerSecond;
        public int AverageBytesPerSecond;
        public short BlockAlign;
        public short BitsPerSample;
        public short ExtraSize;
    }

    [DllImport("winmm.dll")]
    private static extern int waveInOpen(out IntPtr device, int deviceId, ref WaveFormat format, IntPtr callback,
        IntPtr instance, int flags);

    [DllImport("winmm.dll")]
    private static extern int waveInPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInAddBuffer(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveInStart(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveInReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveInClose(IntPtr device);

    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly int FlagsOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags));
    private static readonly int RecordedOffset = (int)Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.BytesRecorded));

    private readonly AutoResetEvent _signal = new(false);
    private readonly IntPtr[] _headers = new IntPtr[BufferCount];
    private readonly byte[] _chunk = new byte[BufferBytes];
    private readonly MemoryStream _wav = new();
    private readonly TaskCompletionSource<byte[]> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _device;
    private int _next, _loudest;
    private volatile int _bytes;
    private volatile bool _stop, _discard;

    private VoiceRecorder()
    {
    }

    public ConcurrentQueue<float> Levels { get; } = new();
    public TimeSpan Length => TimeSpan.FromSeconds((double)_bytes / BytesPerSecond);
    public bool Silent => _loudest < SilenceFloor;
    public bool Full => _bytes >= MaxBytes;
    public bool Ended => _done.Task.IsCompleted;

    public static VoiceRecorder Start()
    {
        var recorder = new VoiceRecorder();
        recorder.Open();
        return recorder;
    }

    public Task<byte[]> StopAsync()
    {
        _stop = true;
        return _done.Task;
    }

    public void Cancel()
    {
        _discard = true;
        _stop = true;
    }

    public static MicrophoneException SilenceProblem() => AccessDenied()
        ? PrivacyProblem()
        : new MicrophoneException("Микрофон не передаёт звук. Проверьте, что он подключён и включён в Параметры → Система → Звук, " +
                                  "и запишите ещё раз.");

    private void Open()
    {
        var format = new WaveFormat
        {
            FormatTag = 1, Channels = 1, SamplesPerSecond = SampleRate, AverageBytesPerSecond = BytesPerSecond,
            BlockAlign = 2, BitsPerSample = 16,
        };
        int result = waveInOpen(out _device, WaveMapper, ref format, _signal.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero,
            CallbackEvent);
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

    private void Record()
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
            if (!_discard) wav = Finish();
        }
        catch (Exception ex)
        {
            _done.TrySetException(ex);
        }
        finally
        {
            Release();
            _done.TrySetResult(wav);
        }
    }

    private bool Collect(bool requeue)
    {
        for (int i = 0; i < BufferCount; i++)
        {
            var header = _headers[_next];
            int flags = Marshal.ReadInt32(header, FlagsOffset);
            if ((flags & HeaderDone) == 0) return true;
            Take(Marshal.ReadIntPtr(header), Marshal.ReadInt32(header, RecordedOffset));
            Marshal.WriteInt32(header, RecordedOffset, 0);
            Marshal.WriteInt32(header, FlagsOffset, flags & ~HeaderDone);
            _next = (_next + 1) % BufferCount;
            if (requeue && (Full || waveInAddBuffer(_device, header, HeaderSize) != 0)) return false;
        }
        return true;
    }

    private void Take(IntPtr data, int count)
    {
        count = Math.Min(Math.Min(count, BufferBytes), MaxBytes - _bytes) & ~1;
        if (count <= 0) return;
        Marshal.Copy(data, _chunk, 0, count);
        _wav.Write(_chunk, 0, count);
        double power = 0;
        for (int i = 0; i < count; i += 2)
        {
            int sample = (short)(_chunk[i] | _chunk[i + 1] << 8);
            power += (double)sample * sample;
            _loudest = Math.Max(_loudest, Math.Abs(sample));
        }
        _bytes += count;
        double decibels = 10 * Math.Log10(power / (count / 2) / (32768.0 * 32768.0) + 1e-10);
        Levels.Enqueue((float)Math.Clamp(1 + decibels / LevelRange, 0, 1));
    }

    private byte[] Finish()
    {
        int data = (int)_wav.Length - WavHeaderBytes;
        _wav.Position = 0;
        using (var writer = new BinaryWriter(_wav, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(WavHeaderBytes - 8 + data);
            writer.Write("WAVEfmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(SampleRate);
            writer.Write(BytesPerSecond);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(data);
        }
        return _wav.ToArray();
    }

    private void ReturnBuffers()
    {
        waveInReset(_device);
        for (int i = 0; i < 20 && _headers.Any(Queued); i++) _signal.WaitOne(50);
    }

    private static bool Queued(IntPtr header) =>
        header != IntPtr.Zero && (Marshal.ReadInt32(header, FlagsOffset) & HeaderInQueue) != 0;

    private void Release()
    {
        ReturnBuffers();
        var headers = _headers.Where(header => header != IntPtr.Zero).ToList();
        foreach (var header in headers) waveInUnprepareHeader(_device, header, HeaderSize);
        waveInClose(_device);
        foreach (var header in headers)
        {
            Marshal.FreeHGlobal(Marshal.ReadIntPtr(header));
            Marshal.FreeHGlobal(header);
        }
        _signal.Dispose();
    }

    private static void Check(int result)
    {
        if (result != 0) throw Failure(result);
    }

    private static MicrophoneException Failure(int result) =>
        AccessDenied() ? PrivacyProblem()
        : result is BadDeviceId or NoDriver
            ? new MicrophoneException("Микрофон не найден. Подключите микрофон или гарнитуру и нажмите на кнопку ещё раз.")
        : result == Allocated
            ? new MicrophoneException("Микрофон занят другой программой. Закройте её и нажмите на кнопку ещё раз.")
            : new MicrophoneException($"Не удалось включить микрофон (код {result}). Проверьте, что он подключён, " +
                                      "и нажмите на кнопку ещё раз.");

    private static MicrophoneException PrivacyProblem() => new(
        "Windows не даёт программам доступ к микрофону. Откройте Параметры → Конфиденциальность и защита → Микрофон " +
        "и включите «Доступ к микрофону» и «Разрешить классическим приложениям доступ к микрофону».", privacy: true);

    private static bool AccessDenied()
    {
        const string store = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        return Denied(Registry.LocalMachine, store) || Denied(Registry.CurrentUser, store) ||
               Denied(Registry.CurrentUser, store + @"\NonPackaged");
    }

    private static bool Denied(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return string.Equals(key?.GetValue("Value") as string, "Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
