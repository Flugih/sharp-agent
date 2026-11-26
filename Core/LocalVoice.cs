using System.Media;
using SherpaOnnx;

namespace SharpAgent.Core;

sealed class LocalVoice : IDisposable
{
    const int MaxSegmentSeconds = 20;
    readonly OfflineRecognizer? _recognizer;
    readonly string _russianVoice, _englishVoice;
    readonly SileroSpeech? _silero;
    OfflineTts? _russian, _english;

    internal LocalVoice(bool recognize = false, VoiceRecognitionMode mode = VoiceRecognitionMode.Auto, string? russianVoice = null, string? englishVoice = null)
    {
        _russianVoice = RussianVoice(russianVoice);
        _englishVoice = EnglishVoice(englishVoice);
        if (_russianVoice != "denis" && VoicePackage.SileroReady) _silero = new(Path.Combine(VoicePackage.Root, VoicePackage.Silero, VoicePackage.SileroModel));
        if (!recognize) return;
        var config = new OfflineRecognizerConfig();
        var folder = Path.Combine(VoicePackage.Root, VoicePackage.RecognizerFolder(mode));
        if (mode == VoiceRecognitionMode.Russian)
        {
            config.ModelConfig.ModelType = "nemo_transducer";
            config.ModelConfig.Transducer.Encoder = Path.Combine(folder, "encoder.int8.onnx");
            config.ModelConfig.Transducer.Decoder = Path.Combine(folder, "decoder.onnx");
            config.ModelConfig.Transducer.Joiner = Path.Combine(folder, "joiner.onnx");
            config.ModelConfig.Tokens = Path.Combine(folder, "tokens.txt");
        }
        else if (mode == VoiceRecognitionMode.Auto)
        {
            config.ModelConfig.ModelType = "nemo_ctc";
            config.ModelConfig.NeMoCtc.Model = Path.Combine(folder, VoicePackage.MultilingualModel);
            config.ModelConfig.Tokens = Path.Combine(folder, "tokens.txt");
        }
        else
        {
            config.ModelConfig.Whisper.Encoder = Path.Combine(folder, "base-encoder.int8.onnx");
            config.ModelConfig.Whisper.Decoder = Path.Combine(folder, "base-decoder.int8.onnx");
            config.ModelConfig.Whisper.Task = "transcribe";
            if (mode == VoiceRecognitionMode.English) config.ModelConfig.Whisper.Language = "en";
            config.ModelConfig.Tokens = Path.Combine(folder, "base-tokens.txt");
        }
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        _recognizer = new(config);
    }

    internal static string RussianVoice(string? voice) => voice is "aidar" or "baya" or "kseniya" or "eugene" or "denis" ? voice : "xenia";
    internal static string EnglishVoice(string? voice) => voice == "ryan" ? voice : "lessac";

    internal static VoiceActivityDetector Detector()
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = Path.Combine(VoicePackage.Root, VoicePackage.Vad);
        config.SileroVad.MinSilenceDuration = 0.55f;
        config.SileroVad.MaxSpeechDuration = MaxSegmentSeconds;
        return new(config, 30);
    }

    internal string Recognize(float[] samples)
    {
        var recognizer = _recognizer ?? throw new InvalidOperationException();
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(VoiceRecorder.SampleRate, samples);
        recognizer.Decode(stream);
        return stream.Result.Text.Trim();
    }

    internal string RecognizeRecording(byte[] wav, CancellationToken ct)
    {
        const int rate = VoiceRecorder.SampleRate;
        int length = (wav.Length - VoiceRecorder.WavHeaderBytes) / sizeof(short);
        ct.ThrowIfCancellationRequested();
        if (length <= rate * MaxSegmentSeconds) return Recognize(ReadSamples(0, length));
        using var vad = Detector();
        var phrases = new List<string>();
        int previousEnd = 0;
        for (int at = 0; at < length; at += rate)
        {
            ct.ThrowIfCancellationRequested();
            vad.AcceptWaveform(ReadSamples(at, Math.Min(rate, length - at)));
            DecodeSegments();
        }
        vad.Flush();
        DecodeSegments();
        return string.Join(' ', phrases);

        void DecodeSegments()
        {
            while (!vad.IsEmpty())
            {
                ct.ThrowIfCancellationRequested();
                var segment = vad.Front();
                vad.Pop();
                int start = Math.Max(previousEnd, segment.Start - rate);
                int end = Math.Min(length, segment.Start + segment.Samples.Length);
                if (end <= start) continue;
                previousEnd = end;
                var text = Recognize(ReadSamples(start, end - start));
                if (text.Length > 0) phrases.Add(text);
            }
        }

        float[] ReadSamples(int start, int count)
        {
            var pcm = MemoryMarshal.Cast<byte, short>(wav.AsSpan(VoiceRecorder.WavHeaderBytes + start * sizeof(short), count * sizeof(short)));
            var samples = new float[count];
            for (int i = 0; i < samples.Length; i++) samples[i] = pcm[i] / 32768f;
            return samples;
        }
    }

    internal async Task SpeakAsync(string text, ReplyLanguage language, double speed, CancellationToken ct, TalkAudio? capture = null, Action? playbackStarted = null)
    {
        ct.ThrowIfCancellationRequested();
        if (language == ReplyLanguage.Russian) text = TalkWords.RussianPlaybackText(text);
        if (!text.Any(char.IsLetterOrDigit)) return;
        using var stream = new MemoryStream();
        int rate, count;
        if (language == ReplyLanguage.Russian && _silero != null)
        {
            var pcm = await _silero.GenerateAsync(text, _russianVoice, speed, ct).ConfigureAwait(false);
            rate = SileroSpeech.SampleRate;
            count = pcm.Length / 2;
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            VoiceRecorder.WriteWaveHeader(writer, rate, pcm.Length);
            writer.Write(pcm);
        }
        else (rate, count) = await Task.Run(() =>
        {
            var audio = SpeechEngine(language).Generate(text, (float)speed, 0);
            try
            {
                var samples = audio.Samples;
                using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
                VoiceRecorder.WriteWaveHeader(writer, audio.SampleRate, samples.Length * 2);
                foreach (var sample in samples) writer.Write((short)(Math.Clamp(sample, -1, 1) * short.MaxValue));
                return (audio.SampleRate, samples.Length);
            }
            finally { audio.Dispose(); }
        }, ct).ConfigureAwait(false);
        if (rate <= 0 || count == 0) return;
        stream.Position = 0;
        ct.ThrowIfCancellationRequested();
        using var player = new SoundPlayer(stream);
        player.Load();
        ct.ThrowIfCancellationRequested();
        if (capture != null)
            capture.BeginPlayback(MemoryMarshal.Cast<byte, short>(stream.GetBuffer().AsSpan(VoiceRecorder.WavHeaderBytes, count * sizeof(short))).ToArray(), rate);
        try
        {
            player.Play();
            playbackStarted?.Invoke();
            await Task.Delay(TimeSpan.FromSeconds((double)count / rate), ct).ConfigureAwait(false);
        }
        finally
        {
            player.Stop();
            capture?.EndPlayback();
        }
    }

    internal void PrepareSpeech(ReplyLanguage language)
    {
        if (language == ReplyLanguage.Russian && _silero != null) _silero.Prepare();
        else SpeechEngine(language);
    }

    OfflineTts SpeechEngine(ReplyLanguage language) => language == ReplyLanguage.Russian
        ? _russian ??= CreateVoice(VoicePackage.Denis)
        : _english ??= CreateVoice(_englishVoice == "ryan" && VoicePackage.RyanReady ? VoicePackage.Ryan : VoicePackage.Lessac);

    static OfflineTts CreateVoice(string name)
    {
        var folder = Path.Combine(VoicePackage.Root, name);
        var config = new OfflineTtsConfig();
        config.Model.Vits.Model = Path.Combine(folder, VoicePackage.VoiceModel(name));
        config.Model.Vits.Tokens = Path.Combine(folder, "tokens.txt");
        config.Model.Vits.DataDir = Path.Combine(folder, "espeak-ng-data");
        config.Model.NumThreads = 2;
        return new(config);
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _silero?.Dispose();
        _russian?.Dispose();
        _english?.Dispose();
    }
}
