using System.Net;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private static readonly TimeSpan ShortestRecording = TimeSpan.FromSeconds(0.5);

    private readonly DispatcherTimer _voiceTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private VoiceRecorder? _recorder;
    private CancellationTokenSource? _voice;

    private bool VoiceShown => VoicePanel.Visibility == Visibility.Visible;

    private UIElement ComposerFocus => !VoiceShown ? InputBox : _recorder != null ? VoiceDoneButton : VoiceTime;

    private async void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (!_config.HasGroqKey)
        {
            ShowToast("Для голосового ввода нужен бесплатный ключ Groq", ToastKind.Info, "Указать ключ", ShowVoiceKeySettings);
            return;
        }
        VoiceRecorder recorder;
        MicButton.IsEnabled = false;
        try
        {
            recorder = await Task.Run(VoiceRecorder.Start);
        }
        catch (MicrophoneException ex)
        {
            ShowMicrophoneProblem(ex);
            return;
        }
        finally
        {
            MicButton.IsEnabled = true;
        }
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

    private void OnVoiceTick()
    {
        if (_recorder is not { } recorder) return;
        while (recorder.Levels.TryDequeue(out var level)) VoiceWave.Push(level);
        var length = recorder.Length;
        VoiceTime.Text = $"{(int)length.TotalMinutes}:{length.Seconds:00}";
        if (recorder.Ended) _ = FinishVoiceAsync();
    }

    private void VoiceDone_Click(object sender, RoutedEventArgs e) => _ = FinishVoiceAsync();

    private void VoiceCancel_Click(object sender, RoutedEventArgs e) => CancelVoice();

    private async Task FinishVoiceAsync()
    {
        if (_recorder is not { } recorder || _voice is not { } session) return;
        _recorder = null;
        ShowVoice(recording: false);
        byte[] wav;
        try
        {
            wav = await recorder.StopAsync();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            if (session.IsCancellationRequested) return;
            EndVoice(session);
            ShowToast("Не удалось записать звук: " + ex.Message, ToastKind.Error);
            return;
        }
        if (session.IsCancellationRequested) return;
        if (recorder.Silent || recorder.Length < ShortestRecording)
        {
            EndVoice(session);
            if (recorder.Silent) ShowMicrophoneProblem(VoiceRecorder.SilenceProblem());
            else ShowToast("Запись слишком короткая. Нажмите на микрофон, продиктуйте сообщение и нажмите «Готово».", ToastKind.Warning);
            return;
        }
        if (recorder.Full) ShowToast($"Запись длится не больше {VoiceRecorder.MaxMinutes} минут — распознаю то, что успело записаться");
        await TranscribeAsync(wav, session);
    }

    private async Task TranscribeAsync(byte[] wav, CancellationTokenSource session)
    {
        try
        {
            var text = await Groq.TranscribeAsync(_config.GroqApiKey ?? "", wav, session.Token);
            EndVoice(session);
            if (text.Length > 0) InsertDictation(text);
            else ShowToast("Речь не распознана. Говорите ближе к микрофону и запишите ещё раз.", ToastKind.Warning);
        }
        catch (Exception ex)
        {
            if (session.IsCancellationRequested) return;
            EndVoice(session);
            if (ex is not (ApiException or HttpRequestException or OperationCanceledException)) App.LogError(ex);
            bool key = ex is ApiException { StatusCode: HttpStatusCode.Unauthorized };
            ShowToast(VoiceError(ex), ToastKind.Error, key ? "Указать ключ" : "Повторить",
                key ? ShowVoiceKeySettings : () => RetryVoice(wav));
        }
    }

    private static string VoiceError(Exception ex) => ex switch
    {
        ApiException => ex.Message.Split('\n')[0],
        HttpRequestException => "Нет связи с Groq. Проверьте подключение к интернету и нажмите «Повторить».",
        OperationCanceledException => "Groq не ответил вовремя. Нажмите «Повторить».",
        _ => "Не удалось распознать речь: " + ex.Message,
    };

    private void RetryVoice(byte[] wav)
    {
        if (_voice != null) return;
        _voice = new CancellationTokenSource();
        ShowVoice(recording: false);
        _ = TranscribeAsync(wav, _voice);
    }

    private void CancelVoice()
    {
        _recorder?.Cancel();
        _recorder = null;
        _voice?.Cancel();
        _voice?.Dispose();
        _voice = null;
        HideVoice();
    }

    private void EndVoice(CancellationTokenSource session)
    {
        session.Dispose();
        if (_voice != session) return;
        _voice = null;
        HideVoice();
    }

    private void ShowVoice(bool recording)
    {
        VoiceTime.Text = recording ? "0:00" : "Распознаю речь…";
        if (!recording && VoiceDoneButton.IsKeyboardFocused) VoiceTime.Focus();
        _voiceTimer.IsEnabled = recording;
        VoicePanel.Visibility = Visibility.Visible;
        InputBox.Visibility = Visibility.Collapsed;
        UpdateInputHint();
        VoiceWave.Visibility = VoiceDoneButton.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (!recording) UIElementAutomationPeer.CreatePeerForElement(VoiceTime)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        SetVoiceMark(recording ? RecordingDot() : MakeSpinner(14));
        UpdateComposerButtons();
    }

    private void HideVoice()
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

    private void SetVoiceMark(FrameworkElement? mark)
    {
        if (VoiceMark.Child is FrameworkElement old)
        {
            old.BeginAnimation(OpacityProperty, null);
            StopSpinner(old);
        }
        VoiceMark.Child = mark;
    }

    private static FrameworkElement RecordingDot()
    {
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8, Height = 8, Fill = Theme.DangerText,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (Theme.AnimationsEnabled)
            dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.7))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            });
        return dot;
    }

    private void InsertDictation(string text)
    {
        var current = InputBox.Text;
        int start = InputBox.SelectionStart, end = start + InputBox.SelectionLength;
        if (start > 0 && !char.IsWhiteSpace(current[start - 1])) text = " " + text;
        if (end < current.Length && !char.IsWhiteSpace(current[end])) text += " ";
        InputBox.SelectedText = text;
        InputBox.Select(start + text.Length, 0);
    }

    private void ShowMicrophoneProblem(MicrophoneException problem) =>
        ShowToast(problem.Message, ToastKind.Error, problem.Privacy ? "Открыть параметры" : null,
            problem.Privacy ? () => FileActions.OpenWindowsSettings("privacy-microphone") : null);

    private void ShowVoiceKeySettings() => OpenSettings(_config.Provider == AgentProvider.Groq ? KeyPasswordBox : VoiceKeyBox);
}
