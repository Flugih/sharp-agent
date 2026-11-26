using System.Windows.Automation.Peers;

namespace SharpAgent;

public partial class MainWindow
{
    readonly DispatcherTimer _voiceTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    VoiceRecorder? _recorder;
    CancellationTokenSource? _voice;

    bool VoiceShown => IsShown(VoicePanel);

    UIElement ComposerFocus => !VoiceShown ? InputBox : _recorder != null ? VoiceDoneButton : VoiceTime;

    async void Mic_Click(object sender, RoutedEventArgs e)
    {
        var mode = _state.VoiceInputMode;
        if (mode == VoiceRecognitionMode.Groq && !RequireGroqVoiceKey()) return;
        if (mode != VoiceRecognitionMode.Groq && !VoicePackage.RecognitionReady(mode))
        {
            ShowToast(Tr("Для голосового ввода скачайте локальный голосовой пакет.", "Download the local voice package for dictation."),
                ToastKind.Info, Tr("Настройки", "Settings"), () => OpenSettings(VoicePackageButton));
            return;
        }
        VoiceRecorder recorder;
        MicButton.IsEnabled = false;
        try
        {
            _localPlaybackStop?.Cancel();
            await _localPlayback;
            recorder = await Task.Run(VoiceRecorder.Start);
        }
        catch (MicrophoneException ex)
        {
            ShowMicrophoneProblem(ex);
            return;
        }
        finally { MicButton.IsEnabled = true; }
        if (_voice != null)
        {
            recorder.Cancel();
            return;
        }
        _recorder = recorder;
        _voice = new CancellationTokenSource();
        ShowVoice(recording: true);
        _ = Dispatcher.InvokeAsync(() => VoiceDoneButton.Focus(), DispatcherPriority.Input);
    }

    void OnVoiceTick()
    {
        if (_recorder is not { } recorder) return;
        while (recorder.Levels.TryDequeue(out var level)) VoiceWave.Push(level);
        var length = recorder.Length;
        VoiceTime.Text = $"{(int)length.TotalMinutes}:{length.Seconds:00}";
        if (recorder.Ended) _ = FinishVoiceAsync();
    }

    void VoiceDone_Click(object sender, RoutedEventArgs e) => _ = FinishVoiceAsync();

    void VoiceCancel_Click(object sender, RoutedEventArgs e) => CancelVoice();

    async Task FinishVoiceAsync()
    {
        if (_recorder is not { } recorder || _voice is not { } session) return;
        _recorder = null;
        ShowVoice(recording: false);
        byte[] wav;
        try { wav = await recorder.StopAsync(); }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (session.IsCancellationRequested) return;
            EndVoice(session);
            ShowToast(Tr("Не удалось записать звук: ", "Could not record audio: ") + ex.Message, ToastKind.Error);
            return;
        }
        if (session.IsCancellationRequested) return;
        if (recorder.Silent || recorder.Length < TimeSpan.FromSeconds(0.5))
        {
            EndVoice(session);
            if (recorder.Silent) ShowMicrophoneProblem(VoiceRecorder.SilenceProblem());
            else ShowToast(Tr("Запись слишком короткая. Нажмите на микрофон, продиктуйте сообщение и нажмите «Готово».", "The recording is too short. Select the microphone, speak your message, then choose Done."), ToastKind.Warning);
            return;
        }
        if (recorder.Full) ShowToast(Tr($"Запись длится не больше {VoiceRecorder.MaxMinutes} минут — распознаю то, что успело записаться", $"Recording is limited to {VoiceRecorder.MaxMinutes} minutes. Transcribing the captured audio."));
        await TranscribeAsync(wav, session);
    }

    async Task TranscribeAsync(byte[] wav, CancellationTokenSource session)
    {
        try
        {
            var mode = _state.VoiceInputMode;
            var ct = session.Token;
            var text = mode != VoiceRecognitionMode.Groq
                ? await Task.Run(() =>
                {
                    if (!VoicePackage.RecognitionReady(mode)) throw new IOException(Tr("Не установлен голосовой пакет.", "The voice package is missing."));
                    using var voice = new LocalVoice(recognize: true, mode: mode);
                    return voice.RecognizeRecording(wav, ct);
                }, ct)
                : await Groq.TranscribeAsync(_config.GroqApiKey ?? throw new ApiException(
                    Tr("Укажите API-ключ Groq в настройках.", "Enter the Groq API key in settings."), HttpStatusCode.Unauthorized), wav, ct);
            if (_voice != session) return;
            EndVoice(session);
            if (text.Length > 0) InsertDictation(text);
            else ShowToast(Tr("Речь не распознана. Говорите ближе к микрофону и запишите ещё раз.", "No speech was detected. Move closer to the microphone and record again."), ToastKind.Warning);
        }
        catch (Exception ex)
        {
            if (session.IsCancellationRequested) return;
            EndVoice(session);
            if (ex is not (ApiException or HttpRequestException or OperationCanceledException)) App.LogError(ex);
            bool key = ex is ApiException { StatusCode: HttpStatusCode.Unauthorized };
            ShowToast(ex switch
            {
                ApiException => ex.Message.Split('\n')[0],
                HttpRequestException => Tr("Нет связи с Groq. Проверьте подключение к интернету и нажмите «Повторить».", "Could not reach Groq. Check your internet connection and select Retry."),
                OperationCanceledException => Tr("Groq не ответил вовремя. Нажмите «Повторить».", "Groq did not respond in time. Select Retry."),
                _ => Tr("Не удалось распознать речь: ", "Could not transcribe speech: ") + ex.Message,
            }, ToastKind.Error, key ? Tr("Указать ключ", "Enter API key") : Tr("Повторить", "Retry"), key ? ShowVoiceKeySettings : () => RetryVoice(wav));
        }
    }

    void RetryVoice(byte[] wav)
    {
        if (_voice != null) return;
        _voice = new CancellationTokenSource();
        ShowVoice(recording: false);
        _ = TranscribeAsync(wav, _voice);
    }

    void CancelVoice()
    {
        _recorder?.Cancel();
        _recorder = null;
        _voice?.Cancel();
        _voice?.Dispose();
        _voice = null;
        HideVoice();
    }

    void EndVoice(CancellationTokenSource session)
    {
        session.Dispose();
        if (_voice != session) return;
        _voice = null;
        HideVoice();
    }

    void ShowVoice(bool recording)
    {
        VoiceTime.Text = recording ? "0:00" : Tr("Распознаю речь…", "Transcribing…");
        if (!recording && VoiceDoneButton.IsKeyboardFocused) VoiceTime.Focus();
        _voiceTimer.IsEnabled = recording;
        VoicePanel.Visibility = Visibility.Visible;
        InputBox.Visibility = Visibility.Collapsed;
        UpdateInputHint();
        VoiceWave.Visibility = VoiceDoneButton.Visibility = VisibleIf(recording);
        if (!recording) UIElementAutomationPeer.CreatePeerForElement(VoiceTime)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        SetVoiceMark(recording ? RecordingDot() : MakeSpinner(14));
        UpdateComposerButtons();
    }

    void HideVoice()
    {
        _voiceTimer.Stop();
        SetVoiceMark(null);
        VoiceWave.Clear();
        VoicePanel.Visibility = Visibility.Collapsed;
        InputBox.Visibility = Visibility.Visible;
        UpdateInputHint();
        UpdateComposerButtons();
        Dispatcher.InvokeAsync(() =>
        {
            if (Keyboard.FocusedElement is not (TextBox or PasswordBox)) InputBox.Focus();
        }, DispatcherPriority.Input);
    }

    void SetVoiceMark(FrameworkElement? mark)
    {
        if (VoiceMark.Child is FrameworkElement old)
            old.BeginAnimation(OpacityProperty, null);
        VoiceMark.Child = mark;
    }

    static FrameworkElement RecordingDot()
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8, Height = 8, Fill = Theme.DangerText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (Theme.AnimationsEnabled)
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.7)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        return dot;
    }

    void InsertDictation(string text)
    {
        var current = InputBox.Text;
        int start = InputBox.SelectionStart, end = start + InputBox.SelectionLength;
        if (start > 0 && !char.IsWhiteSpace(current[start - 1])) text = " " + text;
        if (end < current.Length && !char.IsWhiteSpace(current[end])) text += " ";
        InputBox.SelectedText = text;
        InputBox.Select(start + text.Length, 0);
    }

    void ShowMicrophoneProblem(MicrophoneException problem) => ShowToast(problem.Message, ToastKind.Error,
        problem.Privacy ? Tr("Открыть параметры", "Open settings") : null, problem.Privacy ? () => FileActions.OpenWindowsSettings("privacy-microphone") : null);

    bool RequireGroqVoiceKey()
    {
        if (_config.HasGroqKey) return true;
        ShowToast(Tr("Укажите API-ключ Groq для распознавания речи.", "Enter the Groq API key for speech recognition."),
            ToastKind.Info, Tr("Указать ключ", "Enter API key"), ShowVoiceKeySettings);
        return false;
    }

    void ShowVoiceKeySettings() => OpenSettings(_config.Provider == AgentProvider.Groq ? KeyPasswordBox : VoiceKeyBox);
}
