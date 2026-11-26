using System.Windows.Data;
using System.Windows.Shell;

namespace SharpAgent;

public partial class MainWindow
{
    enum ErrorFix { None, Retry, Settings, Usage }

    static ErrorFix FixFor(ApiException ex) => ex.Code == ChatGptClient.LimitCode ? ErrorFix.Usage
        : ex.Code != ChatGptClient.InvalidUserCode && ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? ErrorFix.Settings : ErrorFix.Retry;

    sealed record ToolRow(string Id, string Name, string ArgumentsJson, StackPanel Root, Panel Header, Shimmer Label, TextBlock Status, Button Expand)
    {
        public string? CommandText { get; init; }
        public string? FilePath, Output;
        public FrameworkElement? Subject;
        public RevealPanel? Details;
        public FileDiff? Diff;
        public ProgressSection? EditGroup;
    }

    sealed record ProducedFile(string Path, FileDiff? Diff, bool Created, FileChange? Change, bool Undone, bool Chained = false, int Steps = 1);

    sealed class ChatRenderState(Panel? target = null)
    {
        public readonly Panel? Target = target;
        public readonly Dictionary<string, ToolRow> ToolRows = new();
        public readonly Dictionary<string, string> ActiveTools = new();
        public readonly List<ProducedFile> TurnFiles = [];
        public List<string> TurnPictures = [];
        public readonly List<(FrameworkElement View, ProducedFile[] Files)> ChangeCards = [];
        public readonly Dictionary<string, ProgressSection> FileEditGroups = new(StringComparer.OrdinalIgnoreCase);
        public StackPanel? LastAnswer;
        public FrameworkElement? LastStopNotice;
        public ProgressSection? Progress;
        public ProgressSection? Commands;
        public UIElement? SingleCommand;
        public RichTextBox? ReasoningRow, CommentaryRow;
        public TextBlock? ProgressHint;
        public ToolCallEvent? PendingPlan;
        public bool Compacting;
        public (string Status, long Until)? FinishedToolStatus;
        public string? ContextStatus;
        public string[] NextActions = [];
    }

    ChatRenderState _chatView = new();
    Border? _lastErrorCard;
    Border? _stickyPlanPlaceholder;
    bool _autoScroll = true, _suppressAnimation;

    void RenderCurrentChat()
    {
        ReleaseStickyProgress();
        _modelProgressTimer?.Stop();
        _workStatusTimer?.Stop();
        _pendingModelProgress = null;
        ClearSubagents();
        ClearDraft();
        ChatPanel.Children.Clear();
        _chatView = new();
        _lastErrorCard = null;
        _autoScroll = true;
        ClosePreview();
        ResetHistoryPages();
        RestoreRunningChat();
        SetTaskbar(TaskbarItemProgressState.None);
        SetRunning(_cts != null);
        PlaceQuestion();
        if (_cts == null && _agent.ChatPath is { } path && _chatErrors.TryGetValue(path, out var error)) AddError(error.Message, error.Fix);
        UpdateChatMode();
        UpdateContextBanner();
        SyncChat(_agent, touched: false);
        SyncCompactChat();
    }

    void AddUserBubble(string text, IReadOnlyList<Attachment> attachments, bool fromVoice)
    {
        ReleaseStickyProgress();
        FlushTurnFiles();
        _chatView.ChangeCards.Clear();
        _chatView.FileEditGroups.Clear();
        _chatView.LastStopNotice = null;
        _chatView.Progress = null;
        _chatView.ProgressHint = null;
        _chatView.PendingPlan = null;
        _chatView.NextActions = [];
        ResetProgressStep();
        _chatView.ToolRows.Clear();
        _chatView.TurnPictures = [];
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(64, 8, 0, 8) };
        if (attachments.Count > 0)
        {
            var pictures = AttachmentPictures(attachments);
            var files = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var attachment in attachments)
            {
                bool image = attachment.Kind == AttachmentKind.Image;
                var chip = _links.FileChip(attachment.Path, attachment.Name, image ? path => ShowPicture(path, pictures) : null,
                    image ? [FileActions.Item(Tr("Прикрепить к новому сообщению", "Attach to a new message"), Glyphs.Picture, () => _ = AddAttachmentsAsync([attachment.Path]))] : []);
                chip.Margin = new(6, 0, 0, 6);
                files.Children.Add(chip);
            }
            stack.Children.Add(files);
        }
        if (text.Length > 0)
        {
            var measure = new TextBlock { Text = text, FontFamily = Theme.Ui, FontSize = 14 + ChatTextSize.Extra };
            measure.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            var body = ReadOnlyChatText(new System.Windows.Documents.FlowDocument(
                new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text)) { Margin = new(0) })
            {
                FontFamily = Theme.Ui, FontSize = 14 + ChatTextSize.Extra, PagePadding = new(0), LineHeight = 23 + ChatTextSize.Extra,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            }.Paint(System.Windows.Documents.FlowDocument.ForegroundProperty, "UserBubbleText"), Tr("Сообщение пользователя", "User message"));
            var menu = new ContextMenu();
            menu.Items.Add(FileActions.Item(Tr("Копировать", "Copy"), Glyphs.Copy,
                () => TextClipboard.SetText(body.Selection.IsEmpty ? text : body.Selection.Text), "Ctrl+C"));
            menu.Items.Add(FileActions.Item(Tr("Вставить в поле ввода", "Insert into composer"), Glyphs.Edit, () => PutIntoInput(text)));
            body.ContextMenu = menu;
            var bubble = new Border
            {
                // A fixed-height FlowDocument line puts the glyphs below its center.
                CornerRadius = new(18), Padding = new(16, 4, 16, 12), HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = Math.Min(515, Math.Ceiling(measure.DesiredSize.Width) + 34), Child = body,
            };
            bubble.SetResourceReference(BackgroundProperty, "UserBubble");
            stack.Children.Add(bubble);
        }
        if (fromVoice) stack.Children.Add(new TextBlock { Text = Tr("Голос", "Voice"), Style = Theme.Style("VoiceTaskLabel"), HorizontalAlignment = HorizontalAlignment.Right });
        AppendToChat(stack);
    }

    void AddModelChange(string previous, string current)
    {
        var line = new Grid { Margin = new(0, 12, 0, 12) };
        line.Children.Add(new Border { Height = 1, VerticalAlignment = VerticalAlignment.Center }.Paint(BackgroundProperty, "Line"));
        var label = new DockPanel
        {
            Children =
            {
                GlyphText(Glyphs.Plugin, 14, "TextMuted", new(0, 0, 8, 0)),
                new TextBlock
                {
                    Text = Tr($"Модель изменена с {previous} на {current}.", $"Model changed from {previous} to {current}."),
                    FontSize = 13, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                }.Paint(ForegroundProperty, "TextSoft"),
            },
        };
        line.Children.Add(new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new(16, 0, 16, 0), Padding = new(14, 0, 14, 0), Child = label,
        }.Paint(BackgroundProperty, "Bg"));
        AppendToChat(line);
    }

    void PutIntoInput(string text)
    {
        ComposerDraft = text;
        FocusAttachmentInput();
    }

    void RenderNextActions()
    {
        NextActionPanel.Children.Clear();
        if (_cts != null || !_agent.WorkMode) return;
        foreach (var action in _chatView.NextActions)
        {
            var button = new Button
            {
                Style = Theme.Style("FileChip"), Margin = new(0, 0, 6, 4), MaxWidth = 260,
                Content = new TextBlock { Text = action, TextTrimming = TextTrimming.CharacterEllipsis },
            };
            AutomationProperties.SetName(button, action);
            button.Click += (_, _) => PutIntoInput(action);
            NextActionPanel.Children.Add(button);
        }
    }

    System.Windows.Documents.FlowDocument ChatDocument(string markdown, string foreground) => _markdown.Render(markdown, foreground);

    RichTextBox ChatText(string markdown, string foreground, string label) => ReadOnlyChatText(ChatDocument(markdown, foreground), label);

    static RichTextBox ReadOnlyChatText(System.Windows.Documents.FlowDocument content, string label)
    {
        var box = new RichTextBox
        {
            Document = content, IsReadOnly = true, IsDocumentEnabled = true, IsReadOnlyCaretVisible = false, IsTabStop = false,
            AutoWordSelection = false, IsInactiveSelectionHighlightEnabled = true,
            // The editor insets its text by 5 on each side; the negative margin puts it on the column edge.
            Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), Margin = new(-5, 0, -5, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, FocusVisualStyle = null,
        }.Paint(TextBoxBase.SelectionBrushProperty, "Accent");
        AutomationProperties.SetName(box, label);
        box.SelectionChanged += ApplyPendingChatDocument;
        box.LostMouseCapture += ApplyPendingChatDocument;
        return box;
    }

    static void ApplyPendingChatDocument(object sender, RoutedEventArgs e)
    {
        var box = (RichTextBox)sender;
        if (!box.Selection.IsEmpty || box.IsMouseCaptureWithin || box.Tag == null) return;
        box.Dispatcher.InvokeAsync(() =>
        {
            if (!box.Selection.IsEmpty || box.IsMouseCaptureWithin || box.Tag is not System.Windows.Documents.FlowDocument pending) return;
            box.Tag = null;
            box.Document = pending;
        }, DispatcherPriority.Background);
    }

    static TextBlock GlyphText(string glyph, double size, string? foreground, Thickness margin = default, bool center = true)
    {
        var block = new TextBlock
        {
            Text = glyph, FontFamily = Theme.Icons, FontSize = size, Margin = margin, VerticalAlignment = center ? VerticalAlignment.Center : VerticalAlignment.Stretch,
        };
        return foreground == null ? block : block.Paint(ForegroundProperty, foreground);
    }

    static Border BlockBox(UIElement child, Thickness padding) =>
        new Border { CornerRadius = new(12), Padding = padding, Child = child }.Paint(BackgroundProperty, "BlockBg");

    static TextBlock BlockLabel(string text) => new TextBlock { Text = text, FontSize = 12 }.Paint(ForegroundProperty, "TextDim");

    StackPanel AnswerView(string markdown, DateTimeOffset? sentAt)
    {
        var document = ChatText(markdown, "Text", Tr("Ответ агента", "Agent response"));
        var copyHint = Tr("Скопировать ответ", "Copy response");
        var copy = Theme.IconButton(Glyphs.Copy, copyHint, 28);
        copy.Style = Theme.Style("QuietIconButton");
        copy.Margin = new(-6, 0, 0, 0);
        copy.Click += (_, _) => ChatLinks.CopyWithFeedback(copy, markdown, copyHint);
        var copySelection = FileActions.Item(Tr("Копировать выделенное", "Copy selection"), Glyphs.Copy, () => document.Copy(), "Ctrl+C");
        var menu = new ContextMenu();
        menu.Items.Add(copySelection);
        menu.Items.Add(FileActions.Item(Tr("Копировать ответ целиком", "Copy full response"), null, () => ChatLinks.CopyWithFeedback(copy, markdown, copyHint)));
        menu.Items.Add(new Separator());
        menu.Items.Add(FileActions.Item(Tr("Выделить всё", "Select all"), null, () =>
        {
            document.Focus();
            document.SelectAll();
        }, "Ctrl+A"));
        menu.Opened += (_, _) => copySelection.IsEnabled = !document.Selection.IsEmpty;
        document.ContextMenu = menu;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 0), Children = { copy, ReadAloudButton(document) } };
        footer.Children.Add(new TextBlock
        {
            Text = sentAt?.ToLocalTime().ToString("HH:mm", UiLanguage.Culture) ?? "—",
            ToolTip = sentAt?.ToLocalTime().ToString("d MMMM yyyy, HH:mm:ss", UiLanguage.Culture) ?? Tr("Время ответа не сохранено", "Response time was not saved"),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 0, 0),
        }.Paint(ForegroundProperty, "TextMuted"));
        return new StackPanel { Margin = new(0, 8, 0, 8), Children = { document, footer } };
    }

    void AddAssistantMessage(string markdown, DateTimeOffset? sentAt)
    {
        var panel = AnswerView(markdown, sentAt);
        AppendToChat(panel);
        _chatView.LastAnswer = panel;
    }

    void NoteProduced(string path, FileDiff? diff, bool created, FileChange? change, bool undone)
    {
        var files = _chatView.TurnFiles;
        int index = files.FindIndex(file => PathHelper.SamePath(file.Path, path));
        var current = new ProducedFile(path, diff, created, change, undone);
        if (index < 0)
        {
            files.Add(current);
            return;
        }
        var known = files[index];
        if (undone && !known.Undone) return;
        var chain = known.Change is { } first && change != null && first.AfterHash == change.BeforeHash
            ? first with { AfterHash = change.AfterHash } : null;
        files[index] = known.Undone && !undone ? current
            : new ProducedFile(path, Combine(known.Diff, diff), known.Created, chain, undone, chain != null, known.Steps + 1);
    }

    static FileDiff? Combine(FileDiff? first, FileDiff? second) => first == null || second == null ? second ?? first
        : new FileDiff(first.Unified.TrimEnd('\n') + "\n" + second.Unified, first.Added + second.Added, first.Removed + second.Removed);

    void HideChangeCards()
    {
        foreach (var (view, files) in _chatView.ChangeCards)
        {
            if (view.Parent is Panel parent) parent.Children.Remove(view);
            _chatView.TurnFiles.AddRange(files);
        }
        _chatView.ChangeCards.Clear();
    }

    ProducedFile[] TurnFilesForDisplay() =>
        _chatView.TurnFiles.Where(file => file.Change != null || file.Undone || File.Exists(file.Path))
            .Where(file => file.Change is not { Created: false } change || change.BeforeHash != change.AfterHash)
            .Select(file => file is { Chained: true, Undone: false, Change: { } change }
                ? file with { Diff = FileChanges.NetDiff(_toolContext, change) ?? file.Diff } : file).ToArray();

    void FlushTurnFiles()
    {
        var files = TurnFilesForDisplay();
        if (files.Length > 0)
        {
            FrameworkElement card = files.Length > 1 ? ChangesCard(files)
                : files[0].Undone ? new TextBlock { Text = Tr("Изменение отменено", "Change undone") }.Paint(ForegroundProperty, "TextMuted")
                : FileCard(files[0].Path, files[0].Diff, files[0].Created);
            var cards = new Border { Margin = new(0, 12, 0, 4), Child = card };
            _chatView.ChangeCards.Add((cards, files));
            if (_chatView.LastAnswer is { } answer) answer.Children.Insert(answer.Children.Count - 1, cards);
            else AppendToChat(cards);
        }
        _chatView.TurnFiles.Clear();
        _chatView.LastAnswer = null;
    }

    (string Path, bool Created)? Produced(string name, string arguments, string? path, string output) => name switch
    {
        "write_file" or "edit_file" when path != null => (path, output.StartsWith("Файл создан", StringComparison.Ordinal)),
        "generate_image" when ImageTools.SavedPath(output) is { } image => (image, true),
        "copy_path" when ResultTarget(output) is { } file && File.Exists(file) => (file, true),
        "run_plugin" when !ToolRegistry.CommandFailed(output) &&
            ResolvePath(ArgumentOf(ArgumentOf(arguments, "arguments") ?? "{}", "Target")) is { } target && File.Exists(target) => (target, true),
        _ => null,
    };

    static string? ResultPath(string output)
    {
        int start = output.IndexOf(": ", StringComparison.Ordinal), end = output.LastIndexOf(" (", StringComparison.Ordinal);
        return start >= 0 && end > start && output[(start + 2)..end].Trim() is var path && Path.IsPathFullyQualified(path) ? path : null;
    }

    void AddError(string text, ErrorFix fix = ErrorFix.None)
    {
        _compactChat?.SetStatus(Tr("Ошибка", "Error"), "DangerText");
        _compactChat?.ShowError(text);
        if (!IsActive)
        {
            SetTaskbar(TaskbarItemProgressState.Error);
            WindowEffects.Flash(this);
        }
        if (_lastErrorCard is { } last && Equals(last.Tag, text) && ChatPanel.Children.Contains(last))
        {
            ChatPanel.Children.Remove(last);
            AppendToChat(last);
            return;
        }
        RemoveErrorCard();
        var body = new StackPanel { Children = { SelectableText(text, "DangerText") } };
        (string Label, Action Run)? action = fix switch
        {
            ErrorFix.Retry when _agent.CanContinue => (Tr("Повторить", "Retry"), () => _ = RetryAsync()),
            ErrorFix.Settings => (Tr("Открыть настройки", "Open settings"), ShowKeySettings),
            ErrorFix.Usage => (Tr("Управлять использованием", "Manage usage"), () => FileActions.OpenUrl(ChatGptClient.UsageUrl)),
            _ => null,
        };
        if (action is { } fixIt)
        {
            var button = new Button { Content = fixIt.Label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 12, 0, 0) };
            button.Click += (_, _) => fixIt.Run();
            body.Children.Add(button);
        }
        Grid.SetColumn(body, 1);
        var card = new Border
        {
            CornerRadius = new(12), Padding = new(12), Margin = new(0, 8, 0, 8), Tag = text,
            Child = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
                Children = { GlyphText(Glyphs.Warning, 16, "DangerText", new(0, 2, 10, 0), false), body },
            },
        }.Paint(BackgroundProperty, "DangerSoft");
        AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Assertive);
        AutomationProperties.SetName(card, Tr("Ошибка: ", "Error: ") + text);
        _lastErrorCard = card;
        AppendToChat(card);
    }

    void RemoveErrorCard()
    {
        if (_lastErrorCard != null) ChatPanel.Children.Remove(_lastErrorCard);
        _lastErrorCard = null;
        _compactChat?.ShowError(null);
    }

    void AddNotice(string text, bool warning = false, bool inProgress = true)
    {
        if ((warning || !inProgress) && !_suppressAnimation) _compactChat?.SetStatus(text.ReplaceLineEndings(" "), warning ? "Warning" : "TextMuted");
        var row = new DockPanel { Margin = inProgress ? new(0, 6, 0, 6) : new(0, 8, 0, 8) };
        if (warning) row.Children.Add(GlyphText(Glyphs.Warning, 16, "Warning", new(0, 2, 8, 0), false));
        row.Children.Add(SelectableText(text, "TextMuted"));
        if (inProgress && _chatView.Progress != null && _cts != null) AddProgressItem(row);
        else
        {
            if (!inProgress) _chatView.LastStopNotice = row;
            AppendToChat(row);
        }
    }

    void ShowStop(string reason)
    {
        if (!AgentRunner.IsRunLimitPause(reason))
        {
            AddNotice(reason, warning: true, inProgress: false);
            return;
        }
        var title = Tr("Задача на паузе", "Task paused");
        FinishProgress(title);
        _chatView.Progress = null;
        _chatView.ProgressHint = null;
        if (!_suppressAnimation) _compactChat?.SetStatus(title);
    }

    void OnToolCallStarted(ToolCallEvent e)
    {
        FlushModelProgress();
        var status = e.Name switch
        {
            "enable_tools" => Tr("Подключение инструментов…", "Enabling tools…"),
            PatchTool.Name => Tr("Применяю патч…", "Applying patch…"),
            "read_file" or "read_many" or "read_log" or "list_directory" or "get_file_info" => Tr("Чтение файлов…", "Reading files…"),
            "search_files" or "search_text" => Tr("Поиск по проекту…", "Searching project…"),
            "write_file" or "edit_file" => Tr("Редактирование файлов…", "Editing files…"),
            "create_directory" => Tr("Создание папки…", "Creating folder…"),
            "copy_path" => Tr("Копирование файлов…", "Copying files…"),
            "move_path" => Tr("Перемещение файлов…", "Moving files…"),
            "delete_path" => Tr("Удаление файлов…", "Deleting files…"),
            "exec_command" => Tr("Выполнение команды…", "Running command…"),
            "run_plugin" => Tr("Выполнение плагина…", "Running plugin…"),
            ScriptRegistry.Name => Tr("Локальный сценарий…", "Local script…"),
            LocalTestTools.Name => Tr("Локальные проверки…", "Local checks…"),
            LocalResearchEngine.Name => Activity(e.Name, e.ArgumentsJson) + "…",
            "local_batch" => Tr("Выполняю локальные проверки…", "Running local checks…"),
            ToolOutputStore.Name => Tr("Дочитываю сохранённый результат…", "Reading the saved result…"),
            LocalContractEngine.Name => Tr("Проверяю, выполнены ли условия задачи…", "Checking whether the task meets its requirements…"),
            AgentKnowledge.Name => Tr("Работа с базой знаний…", "Working with project knowledge…"),
            BrowserTools.Name => Tr("Чтение страницы…", "Reading the page…"),
            WebResearch.Name => Tr("Поиск в интернете…", "Searching the web…"),
            "generate_image" => Tr("Создание изображения…", "Generating image…"),
            "open_path" => Tr("Открытие файла или ссылки…", "Opening file or link…"),
            "delegate_tasks" => Tr("Проверка субагентами…", "Subagents are checking…"),
            ComputerTools.Name when ArgumentOf(e.ArgumentsJson, "action") == "ocr" => Tr("Распознавание текста…", "Recognizing screen text…"),
            ComputerTools.Name => Tr("Управление компьютером…", "Controlling computer…"),
            ComputerTools.AppName => Tr("Проверка приложения…", "Checking application…"),
            _ => null,
        };
        if (status != null)
        {
            _chatView.ActiveTools[e.Id] = status;
            UpdateWorkStatus();
        }
        if (e.Name == "update_plan") _chatView.PendingPlan = e;
        else AddToolRow(e.Id, e.Name, e.ArgumentsJson);
    }

    void OnToolCallFinished(ToolResultEvent e, long finishedAt)
    {
        if (_chatView.ActiveTools.Remove(e.Id, out var status))
        {
            if (_chatView.ActiveTools.Count == 0) _chatView.FinishedToolStatus = (status, finishedAt + ToolStatusGraceMs);
            UpdateWorkStatus();
        }
        if (_chatView.PendingPlan is { } plan && plan.Id == e.Id)
        {
            _chatView.PendingPlan = null;
            CompletePlan(plan.Id, plan.ArgumentsJson, e.Output, e.IsError);
            return;
        }
        if (!_chatView.ToolRows.TryGetValue(e.Id, out var row)) return;
        CompleteToolRow(row, e.Output, e.IsError, e.IsWarning, e.Diff, e.Change);
        if (e.Changes != null)
            foreach (var change in e.Changes) NoteProduced(change.Path, FileChanges.NetDiff(_toolContext, change), change.Created, change, false);
        if (!e.IsError && _chatView.TurnFiles.Count > 0 && !ToolRegistry.IsReadOnly(e.Name)) UpdateChangesPill();
    }

    static string Activity(string tool, string argumentsJson)
    {
        string? Get(string key) => ArgumentOf(argumentsJson, key);
        string Name(string key = "path", string fallback = "") => Get(key) is { Length: > 0 } path ? ShortName(path) : fallback;
        return tool switch
        {
            "enable_tools" => Tr("Подключаю инструменты для задачи", "Enabling tools for the task"),
            "read_file" => Tr("Читаю файл ", "Reading ") + Name(),
            "read_many" => Tr("Читаю файлы", "Reading files"),
            "list_directory" => ShortName(Get("path")) is var name and not ("" or ".") ? Tr("Смотрю папку ", "Inspecting folder ") + name : Tr("Смотрю рабочую папку", "Inspecting the working folder"),
            "get_file_info" => Tr("Смотрю сведения о ", "Checking details for ") + Name(fallback: Tr("файле", "file")),
            "search_files" => Tr("Ищу файлы ", "Searching for files ") + OneLine(Get("pattern")),
            "search_text" when OneLine(Get("query")) is { Length: > 0 } query => Tr($"Ищу текст «{query}»", $"Searching for “{query}”"),
            "search_text" => Tr("Поиск текста", "Text search"),
            "write_file" => Tr("Записываю файл ", "Writing ") + Name(),
            "edit_file" => Tr("Правлю файл ", "Editing ") + Name(),
            "create_directory" => Tr("Создаю папку ", "Creating folder ") + Name(),
            "move_path" => Tr("Перемещаю ", "Moving ") + Name("source"),
            "copy_path" => Tr("Копирую ", "Copying ") + Name("source"),
            "delete_path" => Tr("Удаляю ", "Deleting ") + Name(),
            "open_path" => Tr("Открываю ", "Opening ") + Name(),
            "exec_command" when CommandText(argumentsJson) is { } command => Tr("Выполняю ", "Running ") + OneLine(command),
            "exec_command" => Describe(tool).Label,
            PatchTool.Name => Tr("Применяю патч", "Applying patch"),
            "run_plugin" => Tr("Запускаю плагин ", "Running plugin ") + Get("name"),
            ScriptRegistry.Name => Tr("Локальный сценарий", "Local script"),
            LocalTestTools.Name => Tr("Локальные проверки", "Local checks"),
            LocalResearchEngine.Name => ResearchAction(Get("action")).Activity,
            "local_batch" or ToolOutputStore.Name => Describe(tool).Label,
            LocalContractEngine.Name => Describe(tool).Label,
            AgentKnowledge.Name => Tr("База проверенных решений", "Verified solutions"),
            WebResearch.Name => Describe(tool).Label,
            BrowserTools.Name => Get("action") == "research" ? Tr("Ищу документацию", "Finding documentation") : Tr("Проверяю страницу", "Checking the page"),
            "use_plugin" => Tr("Открываю плагин ", "Opening plugin ") + Get("name"),
            "generate_image" => Tr("Рисую изображение", "Generating an image"),
            "get_system_info" => Tr("Смотрю сведения о системе", "Checking system information"),
            "read_log" => Tr("Читаю журнал команды", "Reading the command log"),
            "ask_user" => Tr("Ожидаю ответа на уточнение", "Waiting for clarification"),
            "delegate_tasks" => Tr("Субагенты выполняют поручения", "Subagents are working"),
            "update_plan" => Tr("Обновляю план", "Updating the plan"),
            ComputerTools.Name or ComputerTools.AppName => ComputerActivity(Get("action")) + ((Get("element") ?? Get("path")) is { } target ? " " + OneLine(target) : ""),
            _ => Tr("Выполняю действие", "Running an action"),
        };
    }

    static (string Label, string Activity) ResearchAction(string? action) => action switch
    {
        "index" => (Tr("Индексация файлов", "File indexing"), Tr("Обновляю индекс файлов", "Updating the file index")),
        "search" => (Tr("Поиск по локальным файлам", "Local file search"), Tr("Ищу текст в локальных файлах", "Searching local files")),
        "inspect" => (Tr("Чтение документа", "Read document"), Tr("Читаю документ", "Reading a document")),
        "related" => (Tr("Поиск похожих документов", "Related document search"), Tr("Ищу похожие документы", "Finding related documents")),
        "duplicates" => (Tr("Поиск копий файла", "Duplicate file search"), Tr("Ищу файлы с таким же содержимым", "Finding identical files")),
        _ => (Tr("Работа с локальными файлами", "Local file research"), Tr("Просматриваю локальные файлы", "Inspecting local files")),
    };

    static string ComputerActivity(string? action) => action switch
    {
        "launch" => Tr("Запускаю", "Launching"),
        "windows" => Tr("Смотрю список окон", "Listing windows"),
        "tree" => Tr("Смотрю элементы окна", "Inspecting window controls"),
        "click" => Tr("Нажимаю", "Clicking"),
        "type" => Tr("Ввожу текст", "Entering text"),
        "keys" => Tr("Нажимаю клавиши", "Pressing keys"),
        "scroll" => Tr("Прокручиваю к элементу", "Scrolling to the element"),
        "screenshot" => Tr("Делаю скриншот", "Taking a screenshot"),
        "ocr" => Tr("Распознаю текст", "Recognizing text"),
        "save_baseline" => Tr("Сохраняю эталон окна", "Saving the window baseline"),
        "compare_baseline" => Tr("Сравниваю окно с эталоном", "Comparing the window with its baseline"),
        "wait" => Tr("Жду", "Waiting"),
        "close" => Tr("Закрываю окно", "Closing the window"),
        _ => Tr("Управляю программой", "Controlling the application"),
    };

    internal static (string Glyph, string Label) Describe(string tool, string argumentsJson = "{}") => tool switch
    {
        "enable_tools" => (Glyphs.Info, Tr("Подключение инструментов", "Enable tools")),
        "read_file" => (Glyphs.View, Tr("Чтение файла", "Read file")),
        "read_many" => (Glyphs.View, Tr("Чтение файлов", "Read files")),
        ScriptRegistry.Name => (Glyphs.Terminal, Tr("Локальный сценарий", "Local script")),
        LocalTestTools.Name => (Glyphs.Info, Tr("Локальные проверки", "Local checks")),
        LocalResearchEngine.Name => (Glyphs.Search, ResearchAction(ArgumentOf(argumentsJson, "action")).Label),
        LocalContractEngine.Name => (Glyphs.Check, Tr("Проверка условий задачи", "Task requirements check")),
        "local_batch" => (Glyphs.Check, Tr("Пакет локальных проверок", "Batch of local checks")),
        ToolOutputStore.Name => (Glyphs.View, Tr("Чтение сохранённого результата", "Read saved tool output")),
        AgentKnowledge.Name => (Glyphs.Info, Tr("Проверенные решения", "Verified solutions")),
        BrowserTools.Name => (Glyphs.View, Tr("Встроенный браузер", "Built-in browser")),
        WebResearch.Name => (Glyphs.Globe, Tr("Поиск в интернете", "Web search")),
        "list_directory" => (Glyphs.Folder, Tr("Просмотр папки", "List directory")),
        "get_file_info" => (Glyphs.Info, Tr("Сведения о файле", "File information")),
        "write_file" => (Glyphs.Save, Tr("Запись файла", "Write file")),
        "edit_file" => (Glyphs.Edit, Tr("Правка файла", "Edit file")),
        "create_directory" => (Glyphs.NewFolder, Tr("Новая папка", "New folder")),
        "move_path" => (Glyphs.Move, Tr("Перемещение", "Move")),
        "copy_path" => (Glyphs.Copy, Tr("Копирование", "Copy")),
        "delete_path" => (Glyphs.Delete, Tr("Удаление", "Delete")),
        "search_files" => (Glyphs.Search, Tr("Поиск файлов", "File search")),
        "search_text" => (Glyphs.Search, Tr("Поиск текста", "Text search")),
        ComputerInventory.FindFolders => (Glyphs.Search, Tr("Поиск папок на компьютере", "Search folders on the computer")),
        ComputerInventory.ListApps => (Glyphs.View, Tr("Список приложений Windows", "Windows application list")),
        "exec_command" => (Glyphs.Terminal, Tr("Команда", "Command")),
        PatchTool.Name => (Glyphs.Edit, Tr("Патч файлов", "File patch")),
        "read_log" => (Glyphs.Terminal, Tr("Журнал команды", "Command log")),
        "open_path" => (Glyphs.Open, Tr("Открытие", "Open")),
        "get_system_info" => (Glyphs.Info, Tr("Сведения о системе", "System information")),
        "generate_image" => (Glyphs.Picture, Tr("Создание изображения", "Image generation")),
        "use_plugin" => (Glyphs.Plugin, Tr("Плагин", "Plugin")),
        "run_plugin" => (Glyphs.Plugin, Tr("Работа плагина", "Plugin execution")),
        "ask_user" => (Glyphs.Info, Tr("Уточнение", "Clarification")),
        "delegate_tasks" => (Glyphs.Tool, Tr("Поручение субагентам", "Subagent task")),
        "update_plan" => (Glyphs.Check, Tr("План", "Plan")),
        ComputerTools.Name => (Glyphs.Open, Tr("Управление программой", "Application control")),
        ComputerTools.AppName => (Glyphs.Open, Tr("Проверка приложения", "Application check")),
        _ => (Glyphs.Tool, Tr("Действие агента", "Agent action")),
    };

    ProgressSection? AddFileEditProgress(string name, string? path, UIElement item)
    {
        if (name is not ("write_file" or "edit_file") || path == null)
        {
            AddProgressTool(name, item);
            return null;
        }
        var progress = EnsureProgress();
        if (!_chatView.FileEditGroups.TryGetValue(path, out var group))
            group = _chatView.FileEditGroups[path] = ProgressSection.Collapsed(Tr("Правки файла · ", "File edits · ") + ShortName(path), Glyphs.Edit, progress.Items);
        group.Items.Children.Add(item);
        group.SetTitle(Tr($"Правки файла · {ShortName(path)} · {group.Items.Children.Count}", $"File edits · {ShortName(path)} · {group.Items.Children.Count}"));
        return group;
    }

    ToolRow AddToolRow(string id, string name, string argumentsJson, string? completedCommand = null)
    {
        if (name == "exec_command" && ArgumentOf(argumentsJson, "session_id") is { } session)
        {
            var pending = ShellTools.Sessions.Running(session);
            var previous = _chatView.ToolRows.FirstOrDefault(entry => entry.Value.Name == name && entry.Value.Output == pending);
            if (previous.Value is { } source)
            {
                _chatView.ToolRows.Remove(previous.Key);
                return _chatView.ToolRows[id] = source;
            }
        }
        var (glyph, label) = Describe(name, argumentsJson);
        var running = new Shimmer { Text = label, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsActive = true };
        var status = new TextBlock
        {
            FontFamily = Theme.Icons, FontSize = 14, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        var expand = Theme.IconButton(Glyphs.ChevronRight, Tr($"Подробности действия «{label}»", $"Details for “{label}”"), 24, 12);
        expand.Style = Theme.Style("QuietIconButton");
        expand.Margin = new(4, 0, 0, 0);
        expand.Visibility = Visibility.Collapsed;
        // Left-aligned so the trailing status and chevron follow the text, which trims when the row is full.
        var header = new DockPanel { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 33 };
        if (name != "search_text")
        {
            header.Children.Add(GlyphText(glyph, 16, "TextMuted", new(0, 0, 8, 0)));
            header.Children.Add(running);
        }
        foreach (var tail in new FrameworkElement[] { expand, status })
        {
            DockPanel.SetDock(tail, Dock.Right);
            header.Children.Add(tail);
        }
        var command = name == "exec_command" ? CommandText(argumentsJson, completedCommand) : null;
        var subject = MakeSubject(name, argumentsJson, command);
        if (subject != null) header.Children.Add(subject);
        var root = new StackPanel { Children = { header } };
        var row = new ToolRow(id, name, argumentsJson, root, header, running, status, expand)
        {
            Subject = subject, CommandText = command, FilePath = name is "write_file" or "edit_file" ? ResolvePath(ArgumentOf(argumentsJson, "path")) : null,
        };
        header.MouseLeftButtonUp += (_, _) => ToggleDetails(row);
        expand.Click += (_, _) => ToggleDetails(row);
        _chatView.ToolRows[id] = row;
        row.EditGroup = AddFileEditProgress(name, row.FilePath, root);
        return row;
    }

    static string? ArgumentOf(string argumentsJson, string key)
    {
        try { return JsonNode.Parse(argumentsJson) is JsonObject args ? JsonUtil.Str(args[key]) : null; }
        catch { return null; }
    }

    internal static string? CommandText(string argumentsJson, string? completed = null)
    {
        var args = ParseObject(argumentsJson);
        if (args == null) return completed;
        var options = new ToolArgs(args);
        if (options.Optional("command") is { } command) return command;
        if (args["commands"] is JsonArray commands)
        {
            var batch = commands.OfType<JsonValue>().Select(value => value.TryGetValue<string>(out var line) ? line : null)
                .Where(line => !string.IsNullOrWhiteSpace(line));
            var text = string.Join("\n", batch);
            if (text.Length != 0) return text;
        }
        return completed;
    }

    string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return _toolContext.Resolve(path); }
        catch (ToolException) { return null; }
    }

    FrameworkElement? MakeSubject(string tool, string argumentsJson, string? command)
    {
        string? Get(string key) => ArgumentOf(argumentsJson, key);
        TextBlock? Plain(string? text) => string.IsNullOrWhiteSpace(text) ? null : new TextBlock
        {
            Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        }.Paint(ForegroundProperty, "TextMuted");
        return tool switch
        {
            "exec_command" => Plain(OneLine(command)) ??
                (Get("session_id") is { Length: > 0 } ? Plain(Tr("Получение результата", "Waiting for the result")) : null),
            "search_files" => Plain(OneLine(Get("pattern"))),
            "search_text" => Plain(OneLine(Get("query"))) ?? Plain(Tr("Запрос без текста", "Empty search query")),
            LocalResearchEngine.Name when Get("action") == "search" => Plain(OneLine(Get("query"))),
            LocalResearchEngine.Name when Get("action") == "index" => Plain(OneLine(ShortName(Get("directory")))),
            "generate_image" => Plain(OneLine(Get("prompt"))),
            "use_plugin" => Plain(Get("name")),
            "read_log" => Plain(Get("log_id")),
            "run_plugin" => Plain($"{Get("name")} · {Get("script")}"),
            ComputerTools.Name or ComputerTools.AppName => Plain(OneLine($"{ComputerActivity(Get("action"))} {Get("element") ?? Get("path") ?? Get("text") ?? Get("window")}")),
            "delete_path" => Plain(ShortName(Get("path"))),
            "move_path" or "copy_path" => Plain($"{ShortName(Get("source"))} → {ShortName(Get("destination"))}"),
            "get_system_info" or "update_plan" => null,
            _ => Get("path") is var path && ResolvePath(path) is { } full ? _links.FileRef(full, ShortName(path)) : Plain(ShortName(path)),
        };
    }

    void CompleteToolRow(ToolRow row, string output, bool isError, bool isWarning = false, FileDiff? diff = null, FileChange? change = null, bool undone = false)
    {
        if (row.Name == "exec_command" && ShellTools.Sessions.RunningId(output) != null)
        {
            row.Output = output;
            return;
        }
        row.Label.IsActive = false;
        if (isError || isWarning)
        {
            row.Status.Text = isError ? Glyphs.Cancel : Glyphs.Warning;
            row.Status.Paint(ForegroundProperty, isError ? "DangerText" : "Warning");
            row.Status.Visibility = Visibility.Visible;
            row.Status.ToolTip = isError ? Tr("Не выполнено", "Failed") : row.Name == "local_batch"
                ? Tr("Пакет выполнен частично", "Batch partially completed")
                : "Команда завершилась с ошибкой или по таймауту — раскройте подробности";
            var line = output.Split('\n')[0].Trim();
            if (!line.StartsWith(CommandLog.ExitPrefix, StringComparison.Ordinal))
            {
                row.Root.Children.Add(new TextBlock
                {
                    Text = Shorten(line, 220), TextWrapping = TextWrapping.Wrap, Margin = new(24, 0, 0, 6),
                }.Paint(ForegroundProperty, isError ? "DangerText" : "Warning"));
                if (isError) row.EditGroup?.ShowError(Shorten(line, 100));
            }
        }
        else if (row.Name is "move_path" or "copy_path" && ResultTarget(output) is { } target)
        {
            if (row.Subject is { } subject && row.Header.Children.IndexOf(subject) is >= 0 and var index)
            {
                var link = _links.FileRef(target);
                row.Header.Children.RemoveAt(index);
                row.Header.Children.Insert(index, link);
                row.Subject = link;
            }
            var files = _chatView.TurnFiles;
            if (row.Name == "move_path" && ResolvePath(ArgumentOf(row.ArgumentsJson, "source")) is { } source &&
                files.FindIndex(file => PathHelper.SamePath(file.Path, source)) is >= 0 and var moved)
                files[moved] = files[moved] with { Path = target, Change = null };
        }
        if (!isError)
        {
            var image = row.Name switch
            {
                "generate_image" => ImageTools.SavedPath(output),
                ComputerTools.Name when output.StartsWith(ComputerTools.ShotPrefix, StringComparison.Ordinal) =>
                    AppPaths.Relocate(output[ComputerTools.ShotPrefix.Length..].Split('\n')[0].Trim()),
                _ => null,
            };
            if (image != null && File.Exists(image))
            {
                var pictures = _chatView.TurnPictures;
                pictures.Add(image);
                row.Root.Children.Add(row.Name == ComputerTools.Name ? ViewedImage(image, pictures) : ImagePreview(image, pictures));
            }
        }
        if (change != null) row.FilePath = change.Path;
        else if (row.FilePath != null && !isError && ResultPath(output) is { } exact) row.FilePath = exact;
        row.Output = output;
        row.Diff = isError ? null : diff;
        if (row.Diff != null)
        {
            var summary = DiffSummary(row.Diff.Added, row.Diff.Removed);
            DockPanel.SetDock(summary, Dock.Right);
            row.Header.Children.Insert(row.Header.Children.IndexOf(row.Status) + 1, summary);
        }
        if (!isError && change != null) NoteProduced(change.Path, row.Diff, change.Created, change, undone);
        else if (!isError && !isWarning && Produced(row.Name, row.ArgumentsJson, row.FilePath, output) is { } produced) NoteProduced(produced.Path, row.Diff, produced.Created, null, undone);
        row.Expand.Visibility = Visibility.Visible;
        ShowExpandState(row, open: false);
    }

    static StackPanel DiffSummary(long added, long removed) => new()
    {
        Orientation = Orientation.Horizontal, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new TextBlock { Text = $"+{added}" }.Paint(ForegroundProperty, "Success"),
            new TextBlock { Text = $"−{removed}", Margin = new(6, 0, 0, 0) }.Paint(ForegroundProperty, "DangerText"),
        },
    };

    FrameworkElement ImagePreview(string path, IReadOnlyList<string>? pictures = null)
    {
        var preview = new Border
        {
            Width = 320, Height = 320, CornerRadius = new(12), Margin = new(24, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand, ToolTip = $"{path}\nЩелчок — посмотреть, правая кнопка — другие действия",
            ContextMenu = FileActions.BuildMenu(this, path),
        }.Paint(BackgroundProperty, "Surface");
        AutomationProperties.SetName(preview, Tr("Изображение ", "Image ") + Path.GetFileName(path));
        preview.MouseLeftButtonUp += (_, _) => ShowPicture(path, pictures);
        FileActions.EnableDragOut(preview, path);
        _ = LoadAsync();
        return preview;
        async Task LoadAsync()
        {
            try
            {
                var image = await DecodeImageAsync(path, 640);
                preview.Height = preview.Width * image.PixelHeight / Math.Max(1, image.PixelWidth);
                preview.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            }
            catch
            {
                preview.Visibility = Visibility.Collapsed;
            }
        }
    }

    FrameworkElement ViewedImage(string path, IReadOnlyList<string> pictures)
    {
        var chevron = GlyphText(Glyphs.ChevronRight, 12, "TextMuted", new(6, 0, 0, 0));
        var toggle = new Button
        {
            Style = Theme.Style("RowButton"), Padding = new(4, 6, 8, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(20, 0, 0, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { GlyphText(Glyphs.Picture, 16, "TextMuted", new(0, 0, 8, 0)), new TextBlock { Text = Tr("Просмотрено изображение", "Image viewed") }.Paint(ForegroundProperty, "TextMuted"), chevron },
            },
        };
        AutomationProperties.SetName(toggle, Tr("Просмотрено изображение — показать скриншот", "Image viewed. Show screenshot"));
        var reveal = new RevealPanel();
        toggle.Click += (_, _) =>
        {
            reveal.Child ??= ImagePreview(path, pictures);
            reveal.IsOpen = !reveal.IsOpen;
            chevron.Text = reveal.IsOpen ? Glyphs.ChevronDown : Glyphs.ChevronRight;
            if (reveal.IsOpen) _autoScroll = false;
        };
        return new StackPanel { Children = { toggle, reveal } };
    }

    static string? ResultTarget(string output)
    {
        int arrow = output.IndexOf(" → ", StringComparison.Ordinal);
        if (arrow < 0) return null;
        var target = output[(arrow + 3)..];
        int note = target.IndexOf(" (файлов:", StringComparison.Ordinal);
        target = (note >= 0 ? target[..note] : target).Trim();
        return FileActions.Exists(target) ? target : null;
    }

    void ToggleDetails(ToolRow row)
    {
        if (row.Output is not { } output) return;
        if (row.Details == null && row.Name == WebResearch.Name)
        {
            var links = new StackPanel { Margin = new(24, 4, 0, 8) };
            foreach (var url in WebResearch.Sources(output))
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 2, 0, 2) };
                line.Inlines.Add(_links.WebLink(url, url));
                links.Children.Add(line);
            }
            if (links.Children.Count == 0)
                links.Children.Add(new TextBlock
                {
                    Text = IsShown(row.Status) ? output : Tr("Источники не найдены", "No sources found"),
                    TextWrapping = TextWrapping.Wrap,
                }.Paint(ForegroundProperty, "TextMuted"));
            row.Details = new RevealPanel { Child = links };
            row.Root.Children.Add(row.Details);
        }
        else if (row.Details == null)
        {
            var details = new StackPanel { Margin = new(24, 4, 0, 8) };
            row.Details = new RevealPanel { Child = details };
            if (row.Diff is { } diff)
            {
                var name = new TextBlock { Text = ShortName(row.FilePath) }.Paint(ForegroundProperty, "Text");
                var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(12, 8, 12, 8), Children = { name, DiffSummary(diff.Added, diff.Removed) } };
                TextBlock.SetFontSize(header, 13);
                var view = BlockBox(new StackPanel { Children = { header, DiffView(row.FilePath, diff, InlineDiffLines) } }, new(0, 0, 0, 8));
                view.Margin = new(0, 0, 0, 8);
                if (row.FilePath is { } path)
                {
                    view.Cursor = Cursors.Hand;
                    view.ToolTip = Tr("Открыть справа", "Open in side panel");
                    view.MouseLeftButtonUp += (_, _) => ShowPreview(path, diff);
                }
                details.Children.Add(view);
            }
            var log = row.Name is "exec_command" or "run_plugin" ? CommandLog.IdOf(output) : null;
            bool hasExitCode = output.StartsWith(CommandLog.ExitPrefix, StringComparison.Ordinal);
            if (log != null && CommandLog.Read(AppPaths.LogsFolder, log) is { } full)
                output = hasExitCode ? full : output.Split('\n', 2)[0] + "\n" + full;
            else if (hasExitCode)
            {
                int headerEnd = output.IndexOf('\n');
                output = headerEnd < 0 ? "" : output[(headerEnd + 1)..];
                if (row.Name == "exec_command" && output.StartsWith("Команда 1\n", StringComparison.Ordinal))
                    output = Regex.Replace(output, @"(?m)^(Команда [1-6]\r?\n)" + CommandLog.ExitPrefix + @"-?\d+\r?\n", "$1");
            }
            var command = row.CommandText;
            bool hasCommand = row.Name == "exec_command" && ArgumentOf(row.ArgumentsJson, "command") != null;
            var raw = SelectableText(KeepEnds(hasCommand ? output : Tr($"Параметры:\n{PrettyJson(row.ArgumentsJson)}\n\nРезультат:\n{output}", $"Parameters:\n{PrettyJson(row.ArgumentsJson)}\n\nResult:\n{output}"), 16_000),
                "TextSoft", mono: true, fontSize: 12.5);
            raw.MaxHeight = 360;
            raw.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            var code = new StackPanel { Margin = new(0, 8, 0, 0), Children = { raw } };
            if (command != null)
            {
                var line = SelectableText("$ " + command, "Text", mono: true, fontSize: 12.5);
                line.Margin = new(0, 0, 0, 8);
                code.Children.Insert(0, line);
            }
            TextBlock.SetLineHeight(code, 19);
            var state = BlockLabel(row.Name == "local_batch" && ToolRegistry.IsPartialBatch(output)
                ? Tr("Выполнено частично", "Partially completed")
                : IsShown(row.Status) ? Tr("Не выполнено", "Failed") : Tr("Выполнено", "Completed"));
            DockPanel.SetDock(state, Dock.Right);
            var footer = new DockPanel { LastChildFill = false, Margin = new(0, 8, 0, 0), Children = { state } };
            if (log != null)
            {
                var journal = new Button { Content = Tr("Открыть журнал", "Open log") };
                AutomationProperties.SetName(journal, Tr("Открыть журнал команды", "Open command log"));
                journal.Click += (_, _) =>
                {
                    if (CommandLog.PathOf(AppPaths.LogsFolder, log) is { } path && File.Exists(path))
                        ShowPreview(path, title: OneLine(command ?? Describe(row.Name).Label));
                    else ShowToast(Tr($"Журнал команды удалён. Журналы хранятся {CommandLog.KeepDays} дней.", $"Command log deleted. Logs are kept for {CommandLog.KeepDays} days."), ToastKind.Warning);
                };
                footer.Children.Add(journal);
            }
            details.Children.Add(BlockBox(new StackPanel { Children = { BlockLabel(Describe(row.Name, row.ArgumentsJson).Label), code, footer } }, new(12)));
            row.Root.Children.Add(row.Details);
        }
        bool open = !row.Details.IsOpen;
        if (open) _autoScroll = false;
        row.Details.IsOpen = open;
        ShowExpandState(row, open);
    }

    static void ShowExpandState(ToolRow row, bool open)
    {
        row.Expand.Content = open ? Glyphs.ChevronDown : Glyphs.ChevronRight;
        var hint = row.Name == WebResearch.Name
            ? open ? Tr("Скрыть источники", "Hide sources") : Tr("Показать источники", "Show sources")
            : open ? Tr("Скрыть параметры и результат", "Hide parameters and result") : Tr("Показать параметры и результат", "Show parameters and result");
        row.Expand.ToolTip = hint;
        AutomationProperties.SetName(row.Expand, hint);
    }

    void FinishTurn()
    {
        MarkUnfinishedToolRows();
        FinishProgress(Tr("Выполнение прервано", "Interrupted"));
        FlushTurnFiles();
    }

    void MarkUnfinishedToolRows()
    {
        _chatView.PendingPlan = null;
        foreach (var row in _chatView.ToolRows.Values.Where(r => r.Output == null || r.Name == "exec_command" && ShellTools.Sessions.RunningId(r.Output) != null))
            CompleteToolRow(row, Tr("Выполнение прервано.", "Task interrupted."), isError: true);
    }

    static string OneLine(string? value) => Shorten((value ?? "").Replace("\r", "").Replace("\n", " ⏎ ").Trim(), 70);

    static string Shorten(string text, int max) => text.Length > max ? text[..max] + "…" : text;

    static string KeepEnds(string text, int max) =>
        text.Length <= max ? text : text[..(max / 4)] + $"\n… пропущено {text.Length - max} символов …\n" + text[^(max - max / 4)..];

    static string ShortName(string? path) => string.IsNullOrWhiteSpace(path) ? ""
        : path.Trim().TrimEnd('\\', '/') is var trimmed && Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;

    void RenderHistory(IReadOnlyList<JsonNode> history, int start)
    {
        bool wasSuppressed = _suppressAnimation;
        _suppressAnimation = true;
        try
        {
            var messages = history.OfType<JsonObject>().ToArray();
            for (int i = 0; i < messages.Length; i++)
            {
                if (i > 0) RenderModelChanges(messages[i - 1]);
                var m = messages[i];
                var role = JsonUtil.Str(m["role"]);
                if (role == "user")
                {
                    FinishTurn();
                    AddUserBubble(AgentRunner.ExtractText(m["content"]) ?? "", Attachment.ListFrom(m), AgentRunner.FromVoice(m));
                    ((_chatView.Target ?? ChatPanel).Children[^1]).SetValue(SearchMessageProperty, start + i);
                }
                else if (role == "assistant")
                {
                    ResetProgressStep();
                    OnModelProgress(AgentRunner.ProgressOf(m));
                    var sentAt = AgentRunner.SentAtOf(m);
                    var text = AgentRunner.ExtractText(m["content"])?.Trim() ?? "";
                    if (m["tool_calls"] is JsonArray { Count: > 0 } calls)
                    {
                        if (text.Length > 0)
                        {
                            var content = ProgressText(text, Tr("Промежуточное сообщение", "Interim message"));
                            content.SetValue(SearchMessageProperty, start + i);
                            AddProgressItem(content);
                        }
                        var results = messages.Skip(i + 1).TakeWhile(next => JsonUtil.Str(next["role"]) == "tool").ToArray();
                        foreach (var call in calls.OfType<JsonObject>())
                        {
                            var id = JsonUtil.Str(call["id"]) ?? "";
                            var result = Array.FindLast(results, next => (JsonUtil.Str(next["tool_call_id"]) ?? "") == id);
                            var meta = result?[AgentRunner.MetaField] as JsonObject;
                            if (JsonUtil.Bool(meta?["read_reused"]) == true || JsonUtil.Bool(meta?["action_reused"]) == true) continue;
                            var function = call["function"] as JsonObject;
                            var name = JsonUtil.Str(function?["name"]) ?? "?";
                            if (name == "run_command") name = "exec_command";
                            var arguments = JsonUtil.Str(function?["arguments"]) ?? "{}";
                            var output = JsonUtil.Str(result?["content"]) ?? "";
                            bool isError = JsonUtil.Bool(meta?["command_restricted"]) == true || ToolRegistry.IsErrorOutput(output);
                            if (name == "update_plan")
                            {
                                if (result != null) CompletePlan(id, arguments, output, isError);
                                continue;
                            }
                            var row = AddToolRow(id, name, arguments, JsonUtil.Str(meta?["completed_command"]));
                            if (result == null) continue;
                            bool warning = name == "local_batch" && ToolRegistry.IsPartialBatch(output) ||
                                name is "exec_command" or "run_plugin" && ToolRegistry.CommandFailed(output);
                            CompleteToolRow(row, output, isError, warning, AgentRunner.DiffOf(result),
                                AgentRunner.FileChangeOf(result), AgentRunner.FileChangeUndone(result));
                            foreach (var (change, undone) in AgentRunner.PatchChangesOf(result))
                                NoteProduced(change.Path, FileChanges.NetDiff(_toolContext, change), change.Created, change, undone);
                        }
                        continue;
                    }
                    var elapsed = AgentRunner.ElapsedOf(m);
                    if (AgentRunner.ReviewOf(m) != null)
                    {
                        var content = ProgressText(text, Tr("Промежуточное сообщение", "Interim message"));
                        content.SetValue(SearchMessageProperty, start + i);
                        AddProgressItem(content);
                    }
                    else if (text.Length > 0 || elapsed != null)
                    {
                        ShowFinalAnswer(text, elapsed, sentAt, AgentRunner.NextActionsOf(m));
                        if (text.Length > 0) _chatView.LastAnswer?.SetValue(SearchMessageProperty, start + i);
                    }
                }
                if (AgentRunner.StopReasonOf(m) is { } stop) ShowStop(stop);
            }
            if (messages.Length > 0) RenderModelChanges(messages[^1]);
            FinishTurn();
        }
        finally
        {
            _suppressAnimation = wasSuppressed;
        }
    }

    void RenderModelChanges(JsonObject message)
    {
        foreach (var (previous, current) in AgentRunner.ModelChangesOf(message))
        {
            FinishTurn();
            AddModelChange(previous, current);
        }
    }

    void CopyLastAnswer_Click(object sender, RoutedEventArgs e)
    {
        var last = _agent.LastAnswerText();
        if (last == null) ShowToast(Tr("В этом чате пока нет ответов", "There are no responses in this chat yet."));
        else if (TextClipboard.SetText(last.Trim())) ShowToast(Tr("Последний ответ скопирован", "Latest response copied"), ToastKind.Success);
        else ShowToast(Tr("Буфер обмена занят другой программой — попробуйте ещё раз", "The clipboard is busy. Try again."), ToastKind.Error);
    }

    void AppendToChat(UIElement element)
    {
        (_chatView.Target ?? ChatPanel).Children.Add(element);
        if (_chatView.Target == null) UpdateComposerPlacement();
        if (!_suppressAnimation && Theme.AnimationsEnabled && !(bool)GetValue(Shimmer.MotionSuspendedProperty))
            element.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void ReleaseStickyProgress()
    {
        if (!IsShown(ChatStickyHost)) return;
        ChatStickyHost.Visibility = Visibility.Collapsed;
        BindingOperations.ClearBinding(ChatStickyTitle, TextBlock.TextProperty);
        BindingOperations.ClearBinding(ChatStickyHost, Expander.IsExpandedProperty);
        BindingOperations.ClearBinding(ChatStickyHost, AutomationProperties.NameProperty);
        UpdateStickyClip();
        ChatStickyTitle.IsActive = false;
        var plan = ChatStickyPanel.Children.Count > 0 ? ChatStickyPanel.Children[0] : null;
        ChatStickyPanel.Children.Clear();
        if (plan != null && _stickyPlanPlaceholder?.Parent is Panel parent)
        {
            int at = parent.Children.IndexOf(_stickyPlanPlaceholder);
            parent.Children.RemoveAt(at);
            parent.Children.Insert(at, plan);
        }
        _stickyPlanPlaceholder = null;
    }

    void UpdateStickyProgress()
    {
        if (!IsShown(ChatPanel) || _chatView.Progress is not { IsRunning: true } progress || !progress.View.IsDescendantOf(ChatScroll) ||
            progress.View.TransformToAncestor(ChatScroll).Transform(default).Y > ChatStickyHost.Margin.Top)
        {
            ReleaseStickyProgress();
            return;
        }
        if (!IsShown(ChatStickyHost))
        {
            ChatStickyTitle.SetBinding(TextBlock.TextProperty, new Binding { Source = progress.Label, Path = new(TextBlock.TextProperty) });
            ChatStickyHost.SetBinding(Expander.IsExpandedProperty, new Binding(nameof(Expander.IsExpanded)) { Source = progress.View, Mode = BindingMode.TwoWay });
            ChatStickyHost.SetBinding(AutomationProperties.NameProperty, new Binding { Source = progress.View, Path = new(AutomationProperties.NameProperty) });
            ChatStickyHost.Visibility = Visibility.Visible;
            ChatStickyTitle.IsActive = true;
            UpdateStickyClip();
        }
        if (_stickyPlanPlaceholder != null || progress.Plan?.View is not { Parent: Panel parent } plan) return;
        _stickyPlanPlaceholder = new() { Margin = plan.Margin };
        _stickyPlanPlaceholder.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = plan });
        int at = parent.Children.IndexOf(plan);
        parent.Children.RemoveAt(at);
        parent.Children.Insert(at, _stickyPlanPlaceholder);
        ChatStickyPanel.Children.Add(plan);
    }

    void ChatScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_changingHistory || !ReferenceEquals(e.OriginalSource, ChatScroll)) return;
        if (e.VerticalChange == 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && e.ViewportWidthChange == 0) return;
        if (e.ExtentHeightChange != 0 && _autoScroll && _chatMotion == null) ChatScroll.ScrollToEnd();
        else if (e.VerticalChange != 0 && _chatMotion == null) _autoScroll = Mouse.LeftButton != MouseButtonState.Pressed && IsShown(ChatPanel) && ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 30;
        if (e.VerticalChange != 0 && e.ExtentHeightChange == 0 && _chatMotion == null) CheckHistory(older: e.VerticalChange < 0);
        UpdateStickyProgress();
        if (IsShown(ChatStickyHost) && (e.ViewportHeightChange != 0 || e.ViewportWidthChange != 0)) UpdateStickyClip();
        UpdateScrollDownButton();
    }

    void ChatStickyHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateStickyClip();

    void UpdateStickyClip()
    {
        if (ChatScroll.Template?.FindName("PART_ScrollContentPresenter", ChatScroll) is not ScrollContentPresenter viewport) return;
        // Leave room for the pinned rows without painting a second surface over the chat.
        var margin = ChatStickyHost.Margin;
        double top = Math.Min(margin.Top + ChatStickyHost.ActualHeight + margin.Bottom, viewport.ActualHeight);
        if (!IsShown(ChatStickyHost)) viewport.Clip = null;
        else
        {
            var bounds = new Rect(0, top, viewport.ActualWidth, viewport.ActualHeight - top);
            if (viewport.Clip is not RectangleGeometry geometry) viewport.Clip = new RectangleGeometry(bounds);
            else if (geometry.Rect != bounds) geometry.Rect = bounds;
        }
    }

    void ChatStickyHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ScrollChatBy(-e.Delta / 2.0);
        e.Handled = true;
    }

    void ScrollDown_Click(object sender, RoutedEventArgs e) => ScrollToLatest();

    void ChatScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node != null && node != ChatScroll; node = ParentOf(node))
            if (node is ScrollViewer { ScrollableHeight: > 0 } inner && (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight))
                return;
        ScrollChatBy(-e.Delta / 2.0);
        e.Handled = true;
    }

    static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    static TextBox SelectableText(string text, string foreground, bool mono = false, double fontSize = 14) => new TextBox
    {
        Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = false, IsTabStop = false, BorderThickness = new(0),
        Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap,
        Padding = new(0), FontSize = fontSize, FontFamily = mono ? Theme.Mono : Theme.Ui, FocusVisualStyle = null,
        ContextMenu = (ContextMenu)Application.Current.Resources["ReadOnlyTextMenu"],
    }.Paint(ForegroundProperty, foreground).Paint(TextBoxBase.SelectionBrushProperty, "Accent");

    static FrameworkElement MakeSpinner(double size, bool alwaysAnimate = false)
    {
        var rotate = new RotateTransform();
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = size, Height = size, StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 7, 14 }, StrokeDashCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new(0.5, 0.5), RenderTransform = rotate,
        }.Paint(System.Windows.Shapes.Shape.StrokeProperty, "AccentText");
        void Update() => rotate.BeginAnimation(RotateTransform.AngleProperty, ring.IsLoaded && ring.IsVisible && (alwaysAnimate || Theme.AnimationsEnabled)
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
        ring.Loaded += (_, _) => Update();
        ring.IsVisibleChanged += (_, _) => Update();
        ring.Unloaded += (_, _) => rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        return ring;
    }

    static string PrettyJson(string json)
    {
        try { return JsonNode.Parse(json)?.ToJsonString(JsonUtil.Pretty) ?? json; }
        catch { return json; }
    }
}
