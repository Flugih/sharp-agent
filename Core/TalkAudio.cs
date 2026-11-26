namespace SharpAgent.Core;

sealed class TalkAudio(SherpaOnnx.VoiceActivityDetector vad, Action speechStarted, Action<float[]> phraseReady)
{
    const int Rate = VoiceRecorder.SampleRate;
    readonly float[] _recent = new float[32 * Rate];
    long _written, _previousEnd;
    long _lastSpeech = Environment.TickCount64;
    bool _speaking;
    Playback? _playback;

    internal bool IsQuiet => Environment.TickCount64 - Volatile.Read(ref _lastSpeech) >= 1200;

    sealed record Playback(float[] Samples, long Started, long Stopped = long.MaxValue);

    internal void BeginPlayback(short[] pcm, int rate)
    {
        var reference = new float[(int)((long)pcm.Length * Rate / rate)];
        for (int i = 0; i < reference.Length; i++)
        {
            double source = (double)i * rate / Rate;
            int at = (int)source;
            float fraction = (float)(source - at);
            reference[i] = (pcm[at] * (1 - fraction) + pcm[Math.Min(at + 1, pcm.Length - 1)] * fraction) / 32768f;
        }
        Volatile.Write(ref _playback, new(reference, Environment.TickCount64));
    }

    internal void EndPlayback()
    {
        if (Volatile.Read(ref _playback) is { } playback)
            Volatile.Write(ref _playback, playback with { Stopped = Environment.TickCount64 });
    }

    internal void Accept(float[] samples)
    {
        long now = Environment.TickCount64;
        if (Volatile.Read(ref _playback) is { } playback && now - 250 < playback.Stopped)
            RemoveEcho(samples, playback.Samples, (int)((now - playback.Started) * Rate / 1000) - samples.Length);
        foreach (var sample in samples) _recent[_written++ % _recent.Length] = sample;
        vad.AcceptWaveform(samples);
        bool speaking = vad.IsSpeechDetected();
        if (speaking) Volatile.Write(ref _lastSpeech, now);
        if (speaking && !_speaking) speechStarted();
        _speaking = speaking;
        while (!vad.IsEmpty())
        {
            var segment = vad.Front();
            vad.Pop();
            long segmentStart = _written - unchecked((uint)_written - (uint)segment.Start);
            long start = Math.Max(Math.Max(_previousEnd, _written - _recent.Length), segmentStart - Rate / 3);
            long end = Math.Min(_written, segmentStart + segment.Samples.Length);
            if (end <= start) continue;
            var phrase = new float[end - start];
            for (int i = 0; i < phrase.Length; i++) phrase[i] = _recent[(start + i) % _recent.Length];
            _previousEnd = end;
            phraseReady(phrase);
        }
    }

    internal static void RemoveEcho(float[] samples, float[] reference, int expected)
    {
        const int stride = 12;
        float inputEnergy = 0;
        for (int i = 0; i < samples.Length; i += stride) inputEnergy += samples[i] * samples[i];
        if (inputEnergy < 0.00001f) return;
        int from = Math.Max(-samples.Length / 2, expected - Rate / 4);
        int to = Math.Min(reference.Length - samples.Length / 2, expected + Rate / 20);
        float best = 0.28f, gain = 0;
        int offset = 0;
        for (int lag = from; lag <= to; lag++)
        {
            float cross = 0, energy = 0;
            for (int i = 0; i < samples.Length; i += stride)
            {
                int at = lag + i;
                if ((uint)at >= (uint)reference.Length) continue;
                float value = reference[at];
                cross += samples[i] * value;
                energy += value * value;
            }
            if (energy < 0.00001f) continue;
            float score = cross * cross / (inputEnergy * energy);
            if (score <= best) continue;
            best = score;
            gain = cross / energy;
            offset = lag;
        }
        for (int i = Math.Max(0, -offset); gain != 0 && i < samples.Length && offset + i < reference.Length; i++)
            samples[i] -= reference[offset + i] * gain;
    }
}
