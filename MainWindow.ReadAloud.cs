using System.Windows.Data;
using System.Windows.Documents;

namespace SharpAgent;

public partial class MainWindow
{
    static readonly (double Value, string Label)[] VoiceSpeeds = [(0.75, "0.75×"), (1, "1×"), (1.25, "1.25×"), (1.5, "1.5×")];
    static readonly (string Value, string Label)[] RussianVoices = [("xenia", "Ксения"), ("kseniya", "Ксения (kseniya)"),
        ("baya", "Бая"), ("aidar", "Айдар"), ("eugene", "Евгений"), ("denis", "Денис (Piper)")];
    static readonly (string Value, string Label)[] EnglishVoices = [("lessac", "Lessac"), ("ryan", "Ryan")];
    double _settingsVoiceSpeed;
    VoiceRecognitionMode _settingsVoiceInputMode;
    string _settingsRussianVoice = "xenia", _settingsEnglishVoice = "lessac";
    Style? _readAloudStyle;
    Button? _readingButton;
    CancellationTokenSource? _localPlaybackStop;
    Task _localPlayback = Task.CompletedTask;

    void VoiceSpeed_Click(object sender, RoutedEventArgs e) => PickChoice(VoiceSpeedButton, VoiceSpeeds, _settingsVoiceSpeed, speed =>
    {
        _settingsVoiceSpeed = speed;
        ShowChoice(VoiceSpeedButton, VoiceSpeeds, speed);
    });

    static (VoiceRecognitionMode Value, string Label)[] VoiceInputModes() =>
        [(VoiceRecognitionMode.Auto, Tr("Авто (GigaAM)", "Auto (GigaAM)")), (VoiceRecognitionMode.Russian, Tr("Русский", "Russian")),
            (VoiceRecognitionMode.English, Tr("Английский", "English")),
            (VoiceRecognitionMode.Groq, Tr("API-ключ Groq", "Groq API key"))];

    static (string Value, string Label)[] RussianVoiceChoices() => RussianVoices.Select(voice =>
        (voice.Value, Tr(voice.Label, voice.Value == "denis" ? "Denis (Piper)" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(voice.Value)))).ToArray();

    void ShowVoiceOptions()
    {
        ShowChoice(VoiceRecognitionButton, VoiceInputModes(), _settingsVoiceInputMode);
        ShowChoice(RussianVoiceButton, RussianVoiceChoices(), _settingsRussianVoice);
        ShowChoice(EnglishVoiceButton, EnglishVoices, _settingsEnglishVoice);
    }

    void VoiceOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender == VoiceRecognitionButton) PickChoice(VoiceRecognitionButton, VoiceInputModes(), _settingsVoiceInputMode, choice =>
        {
            _settingsVoiceInputMode = choice;
            ShowVoiceOptions();
            ShowVoicePackageStatus();
        });
        else if (sender == RussianVoiceButton) PickChoice(RussianVoiceButton, RussianVoiceChoices(), _settingsRussianVoice, choice =>
        {
            _settingsRussianVoice = choice;
            ShowVoiceOptions();
        });
        else PickChoice(EnglishVoiceButton, EnglishVoices, _settingsEnglishVoice, choice =>
        {
            _settingsEnglishVoice = choice;
            ShowVoiceOptions();
        });
    }

    void PreviewVoice_Click(object sender, RoutedEventArgs e)
    {
        if (_readingButton == PreviewVoiceButton)
        {
            _localPlaybackStop?.Cancel();
            SetReadingButton(null);
            return;
        }
        if (_settingsRussianVoice != "denis" && !VoicePackage.SileroReady || _settingsEnglishVoice == "ryan" && !VoicePackage.RyanReady)
        {
            ShowToast(Tr("Скачайте голосовой пакет для выбранных голосов.", "Download the voice package for the selected voices."), ToastKind.Info);
            return;
        }
        PlayLocalVoice([("Привет! Вот как звучит голос агента.", ReplyLanguage.Russian),
            ("Hello! This is how the agent sounds.", ReplyLanguage.English)], _settingsVoiceSpeed, _settingsRussianVoice, _settingsEnglishVoice, PreviewVoiceButton);
    }

    Button ReadAloudButton(RichTextBox answer)
    {
        if (_readAloudStyle == null)
        {
            _readAloudStyle = new(typeof(Button), Theme.Style("QuietIconButton"));
            var hidden = new DataTrigger { Binding = new Binding(nameof(Visibility)) { Source = TalkPanel }, Value = Visibility.Visible };
            hidden.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
            _readAloudStyle.Triggers.Add(hidden);
        }
        var button = Theme.IconButton("\uE767", Tr("Озвучить ответ", "Read response aloud"), 28);
        button.Style = _readAloudStyle;
        button.Click += (_, _) =>
        {
            if (_readingButton == button)
            {
                _localPlaybackStop?.Cancel();
                SetReadingButton(null);
                return;
            }
            var text = new TextRange(answer.Document.ContentStart, answer.Document.ContentEnd).Text;
            text = Regex.Replace(text, @"[\uE000-\uF8FF\uFFFC]", "").Trim();
            if (text.Length == 0) return;
            PlayLocalVoice([(text, _agent.ReplyLanguageFor(text))], _state.VoiceSpeed, _state.RussianVoice, _state.EnglishVoice, button);
        };
        return button;
    }

    void SetReadingButton(Button? button, bool loading = false)
    {
        if (_readingButton == PreviewVoiceButton)
            PreviewVoiceButton.SetCurrentValue(ContentControl.ContentProperty, Tr("Прослушать", "Preview voices"));
        else if (_readingButton is { } previous)
        {
            previous.Content = "\uE767";
            var read = Tr("Озвучить ответ", "Read response aloud");
            previous.ToolTip = read;
            AutomationProperties.SetName(previous, read);
        }
        _readingButton = button;
        if (button == null) return;
        if (button == PreviewVoiceButton)
        {
            button.SetCurrentValue(ContentControl.ContentProperty, Tr("Остановить", "Stop"));
            return;
        }
        var stop = loading ? Tr("Готовлю озвучку… Нажмите для отмены", "Preparing audio… Select to cancel") :
            Tr("Остановить озвучку", "Stop reading aloud");
        button.Content = loading ? MakeSpinner(14, alwaysAnimate: true) : new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("F0 M7,0 A7,7 0 1 1 7,14 A7,7 0 1 1 7,0 Z M5,5 H9 V9 H5 Z"),
            Fill = button.Foreground,
        };
        button.ToolTip = stop;
        AutomationProperties.SetName(button, stop);
    }

    void PlayLocalVoice((string Text, ReplyLanguage Language)[] phrases, double speed, string? russianVoice, string? englishVoice, Button? button = null)
    {
        if (_talk != null || _talkClosing || VoiceShown || !MicButton.IsEnabled) return;
        if (!VoicePackage.SpeechReady)
        {
            ShowToast(Tr("Для озвучки нужен локальный голосовой пакет.", "Read aloud needs the local voice package."),
                ToastKind.Info, Tr("Настройки", "Settings"), () => OpenSettings(VoicePackageButton));
            return;
        }
        _localPlaybackStop?.Cancel();
        var stop = _localPlaybackStop = new();
        SetReadingButton(button, loading: true);
        _localPlayback = PlayLocalVoiceAsync(_localPlayback, stop, phrases, speed, russianVoice, englishVoice, button);
    }

    async Task PlayLocalVoiceAsync(Task previous, CancellationTokenSource stop, (string Text, ReplyLanguage Language)[] phrases, double speed,
        string? russianVoice, string? englishVoice, Button? button)
    {
        using (stop)
        {
            try
            {
                await previous;
                stop.Token.ThrowIfCancellationRequested();
                using var voice = new LocalVoice(russianVoice: russianVoice, englishVoice: englishVoice);
                Action? playbackStarted = null;
                if (button != null && button != PreviewVoiceButton) playbackStarted = () =>
                {
                    playbackStarted = null;
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (_localPlaybackStop == stop && !stop.IsCancellationRequested) SetReadingButton(button);
                    });
                };
                foreach (var (text, language) in phrases)
                    for (int at = 0; at < text.Length;)
                    {
                        int length = Math.Min(500, text.Length - at);
                        if (at + length < text.Length &&
                            text.LastIndexOfAny(['\n', ' ', '.', '!', '?'], at + length - 1, length) is var end && end >= at + 100)
                            length = end + 1 - at;
                        var chunk = text.Substring(at, length).Trim();
                        at += length;
                        if (chunk.Length > 0) await voice.SpeakAsync(chunk, language, speed, stop.Token, playbackStarted: playbackStarted);
                    }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception ex)
            {
                App.LogError(ex);
                ShowToast(Tr("Не удалось озвучить текст.", "Could not read the text aloud."), ToastKind.Error);
            }
            finally
            {
                if (_localPlaybackStop == stop)
                {
                    _localPlaybackStop = null;
                    SetReadingButton(null);
                }
            }
        }
    }
}
