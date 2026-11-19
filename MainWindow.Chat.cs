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
        public readonly List<ProducedFile> TurnFiles = [];
        public readonly List<(FrameworkElement View, ProducedFile[] Files)> ChangeCards = [];
        public readonly Dictionary<string, ProgressSection> FileEditGroups = new(StringComparer.OrdinalIgnoreCase);
        public StackPanel? LastAnswer;
        public FrameworkElement? LastStopNotice;
        public ProgressSection? Progress;
        public (ProgressSection Section, bool Commands)? ToolGroup;
        public RichTextBox? ReasoningRow, CommentaryRow;
        public TextBlock? ProgressHint;
        public ToolCallEvent? PendingPlan;
        public ProducedFile[]? LastChanges;
    }

    ChatRenderState _chatView = new();
    Border? _lastErrorCard;
    Border? _stickyPlanPlaceholder;
    bool _autoScroll = true, _suppressAnimation;

    void RenderCurrentChat()
    {
        ReleaseStickyProgress();
        ClearSubagents();
        ClearDraft();
        ChatPanel.Children.Clear();
        _chatView = new();
        _lastErrorCard = null;
        _autoScroll = true;
        ClosePreview();
        ResetHistoryPages();
        UpdateChangesPill();
        UpdateSendButton();
        EmptyChat.Visibility = VisibleIf(ChatPanel.Children.Count == 0);
        UpdateContextBanner();
        SyncCurrentChat(touched: false);
        SyncCompactChat();
    }

    void AddUserBubble(string text, IReadOnlyList<Attachment> attachments)
    {
        ReleaseStickyProgress();
        FlushTurnFiles();
        _chatView.ChangeCards.Clear();
        _chatView.FileEditGroups.Clear();
        _chatView.LastStopNotice = null;
        _chatView.Progress = null;
        _chatView.ProgressHint = null;
        _chatView.PendingPlan = null;
        ResetProgressStep();
        _chatView.ToolRows.Clear();
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(64, 8, 0, 8) };
        if (attachments.Count > 0)
        {
            var files = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var attachment in attachments)
            {
                var chip = _links.FileChip(attachment.Path, attachment.Name, attachment.Kind == AttachmentKind.Image
                    ? [FileActions.Item("Прикрепить к новому сообщению", Glyphs.Picture, () => _ = AddAttachmentsAsync([attachment.Path]))]
                    : []);
                chip.Margin = new(6, 0, 0, 6);
                files.Children.Add(chip);
            }
            stack.Children.Add(files);
        }
        if (text.Length > 0)
        {
            var measure = new TextBlock { Text = text, FontFamily = Theme.Ui, FontSize = 14 };
            measure.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            var body = ReadOnlyChatText(new System.Windows.Documents.FlowDocument(
                new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(text)) { Margin = new(0) })
            {
                FontFamily = Theme.Ui, FontSize = 14, PagePadding = new(0), LineHeight = 23,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            }.Paint(System.Windows.Documents.FlowDocument.ForegroundProperty, "UserBubbleText"), "Сообщение пользователя");
            var menu = new ContextMenu();
            menu.Items.Add(FileActions.Item("Копировать", Glyphs.Copy,
                () => TextClipboard.SetText(body.Selection.IsEmpty ? text : body.Selection.Text), "Ctrl+C"));
            menu.Items.Add(FileActions.Item("Вставить в поле ввода", Glyphs.Edit, () => PutIntoInput(text)));
            body.ContextMenu = menu;
            var bubble = new Border
            {
                CornerRadius = new(18), Padding = new(16, 8, 16, 8), HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = Math.Min(515, Math.Ceiling(measure.DesiredSize.Width) + 34), Child = body,
            };
            bubble.SetResourceReference(BackgroundProperty, "UserBubble");
            stack.Children.Add(bubble);
        }
        AppendToChat(stack);
    }

    void PutIntoInput(string text)
    {
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        InputBox.Focus();
    }

    System.Windows.Documents.FlowDocument ChatDocument(string markdown, string foreground) => _markdown.Render(markdown, foreground);

    RichTextBox ChatText(string markdown, string foreground, string label) => ReadOnlyChatText(ChatDocument(markdown, foreground), label);

    static RichTextBox ReadOnlyChatText(System.Windows.Documents.FlowDocument content, string label)
    {
        var box = new RichTextBox
        {
            Document = content, IsReadOnly = true, IsDocumentEnabled = true, IsReadOnlyCaretVisible = false, IsTabStop = false,
            // The editor insets its text by 5 on each side; the negative margin puts it on the column edge.
            Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), Margin = new(-5, 0, -5, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, FocusVisualStyle = null,
        }.Paint(TextBoxBase.SelectionBrushProperty, "Accent");
        AutomationProperties.SetName(box, label);
        return box;
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
        var document = ChatText(markdown, "Text", "Ответ агента");
        const string copyHint = "Скопировать ответ";
        var copy = Theme.IconButton(Glyphs.Copy, copyHint, 28);
        copy.Style = Theme.Style("QuietIconButton");
        copy.Margin = new(-6, 0, 0, 0);
        copy.Click += (_, _) => ChatLinks.CopyWithFeedback(copy, markdown, copyHint);
        var copySelection = FileActions.Item("Копировать выделенное", Glyphs.Copy, () => document.Copy(), "Ctrl+C");
        var menu = new ContextMenu();
        menu.Items.Add(copySelection);
        menu.Items.Add(FileActions.Item("Копировать ответ целиком", null, () => ChatLinks.CopyWithFeedback(copy, markdown, copyHint)));
        menu.Items.Add(new Separator());
        menu.Items.Add(FileActions.Item("Выделить всё", null, () =>
        {
            document.Focus();
            document.SelectAll();
        }, "Ctrl+A"));
        menu.Opened += (_, _) => copySelection.IsEnabled = !document.Selection.IsEmpty;
        document.ContextMenu = menu;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 4, 0, 0), Children = { copy } };
        footer.Children.Add(new TextBlock
        {
            Text = sentAt?.ToLocalTime().ToString("HH:mm", Ru) ?? "—",
            ToolTip = sentAt?.ToLocalTime().ToString("d MMMM yyyy, HH:mm:ss", Ru) ?? "Время ответа не сохранено",
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

    void FlushTurnFiles()
    {
        var files = _chatView.TurnFiles.Where(file => file.Change != null || file.Undone || File.Exists(file.Path))
            .Where(file => file.Change is not { Created: false } change || change.BeforeHash != change.AfterHash)
            .Select(file => file is { Chained: true, Undone: false, Change: { } change }
                ? file with { Diff = FileChanges.NetDiff(_toolContext, change) ?? file.Diff } : file).ToArray();
        if (files.Length > 0)
        {
            FrameworkElement card = files.Length > 1 ? ChangesCard(files)
                : files[0].Undone ? new TextBlock { Text = "Изменение отменено" }.Paint(ForegroundProperty, "TextMuted")
                : FileCard(files[0].Path, files[0].Diff, files[0].Created);
            var cards = new Border { Margin = new(0, 12, 0, 4), Child = card };
            _chatView.ChangeCards.Add((cards, files));
            if (_chatView.LastAnswer is { } answer) answer.Children.Insert(answer.Children.Count - 1, cards);
            else AppendToChat(cards);
            if (files.Any(file => file.Undone || IsChange(file)))
            {
                _chatView.LastChanges = files;
                if (_chatView.Target == null) UpdateChangesPill();
            }
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
        _compactChat?.SetStatus("Ошибка", "DangerText");
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
            ErrorFix.Retry when _agent.CanContinue => ("Повторить", () => _ = RetryAsync()),
            ErrorFix.Settings => ("Открыть настройки", ShowKeySettings),
            ErrorFix.Usage => ("Управлять использованием", () => FileActions.OpenUrl(ChatGptClient.UsageUrl)),
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
        AutomationProperties.SetName(card, "Ошибка: " + text);
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

    void OnToolCallStarted(ToolCallEvent e)
    {
        _compactChat?.SetStatus(Activity(e.Name, e.ArgumentsJson));
        if (e.Name == "update_plan") _chatView.PendingPlan = e;
        else AddToolRow(e.Id, e.Name, e.ArgumentsJson);
    }

    void OnToolCallFinished(ToolResultEvent e)
    {
        if (_chatView.PendingPlan is { } plan && plan.Id == e.Id)
        {
            _chatView.PendingPlan = null;
            CompletePlan(plan.Id, plan.ArgumentsJson, e.Output, e.IsError);
            return;
        }
        if (!_chatView.ToolRows.TryGetValue(e.Id, out var row)) return;
        CompleteToolRow(row, e.Output, e.IsError, e.IsWarning, e.Diff, e.Change);
    }

    static string Activity(string tool, string argumentsJson)
    {
        string? Get(string key) => ArgumentOf(argumentsJson, key);
        string Name(string key = "path", string fallback = "") => Get(key) is { Length: > 0 } path ? ShortName(path) : fallback;
        return tool switch
        {
            "read_file" => "Читаю файл " + Name(),
            "list_directory" => ShortName(Get("path")) is var name and not ("" or ".") ? "Смотрю папку " + name : "Смотрю рабочую папку",
            "get_file_info" => "Смотрю сведения о " + Name(fallback: "файле"),
            "search_files" => "Ищу файлы " + OneLine(Get("pattern")),
            "search_text" => $"Ищу текст «{OneLine(Get("query"))}»",
            "write_file" => "Записываю файл " + Name(),
            "edit_file" => "Правлю файл " + Name(),
            "create_directory" => "Создаю папку " + Name(),
            "move_path" => "Перемещаю " + Name("source"),
            "copy_path" => "Копирую " + Name("source"),
            "delete_path" => "Удаляю " + Name(),
            "open_path" => "Открываю " + Name(),
            "run_command" => "Выполняю " + OneLine(Get("command")),
            "run_plugin" => "Запускаю плагин " + Get("name"),
            "use_plugin" => "Открываю плагин " + Get("name"),
            "generate_image" => "Рисую изображение",
            "get_system_info" => "Смотрю сведения о системе",
            "read_log" => "Читаю журнал команды",
            "ask_user" => "Ожидаю ответа на уточнение",
            "delegate_tasks" => "Субагенты выполняют поручения",
            "update_plan" => "Обновляю план",
            ComputerTools.Name => ComputerActivity(Get("action")) + ((Get("element") ?? Get("path")) is { } target ? " " + OneLine(target) : ""),
            _ => "Выполняю " + tool,
        };
    }

    static string ComputerActivity(string? action) => action switch
    {
        "launch" => "Запускаю",
        "windows" => "Смотрю список окон",
        "tree" => "Смотрю элементы окна",
        "click" => "Нажимаю",
        "type" => "Ввожу текст",
        "keys" => "Нажимаю клавиши",
        "scroll" => "Прокручиваю к элементу",
        "screenshot" => "Делаю скриншот",
        "wait" => "Жду",
        "close" => "Закрываю окно",
        _ => "Управляю программой",
    };

    static (string Glyph, string Label) Describe(string tool) => tool switch
    {
        "read_file" => (Glyphs.View, "Чтение файла"),
        "list_directory" => (Glyphs.Folder, "Просмотр папки"),
        "get_file_info" => (Glyphs.Info, "Сведения о файле"),
        "write_file" => (Glyphs.Save, "Запись файла"),
        "edit_file" => (Glyphs.Edit, "Правка файла"),
        "create_directory" => (Glyphs.NewFolder, "Новая папка"),
        "move_path" => (Glyphs.Move, "Перемещение"),
        "copy_path" => (Glyphs.Copy, "Копирование"),
        "delete_path" => (Glyphs.Delete, "Удаление"),
        "search_files" => (Glyphs.Search, "Поиск файлов"),
        "search_text" => (Glyphs.Search, "Поиск текста"),
        "run_command" => (Glyphs.Terminal, "Команда"),
        "read_log" => (Glyphs.Terminal, "Журнал команды"),
        "open_path" => (Glyphs.Open, "Открытие"),
        "get_system_info" => (Glyphs.Info, "Сведения о системе"),
        "generate_image" => (Glyphs.Picture, "Создание изображения"),
        "use_plugin" => (Glyphs.Plugin, "Плагин"),
        "run_plugin" => (Glyphs.Plugin, "Работа плагина"),
        "ask_user" => (Glyphs.Info, "Уточнение"),
        "delegate_tasks" => (Glyphs.Tool, "Поручение субагентам"),
        "update_plan" => (Glyphs.Check, "План"),
        ComputerTools.Name => (Glyphs.Open, "Управление программой"),
        _ => (Glyphs.Tool, tool),
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
            group = _chatView.FileEditGroups[path] = ProgressSection.Collapsed("Правки файла · " + ShortName(path), Glyphs.Edit, progress.Items);
        group.Items.Children.Add(item);
        group.SetTitle($"Правки файла · {ShortName(path)} · {group.Items.Children.Count}");
        return group;
    }

    ToolRow AddToolRow(string id, string name, string argumentsJson)
    {
        var (glyph, label) = Describe(name);
        var running = new Shimmer { Text = label, Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsActive = true };
        var status = new TextBlock
        {
            FontFamily = Theme.Icons, FontSize = 14, Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        var expand = Theme.IconButton(Glyphs.ChevronRight, $"Подробности действия «{label}»", 24, 12);
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
        var subject = MakeSubject(name, argumentsJson);
        if (subject != null) header.Children.Add(subject);
        var root = new StackPanel { Children = { header } };
        var row = new ToolRow(id, name, argumentsJson, root, header, running, status, expand)
        {
            Subject = subject, FilePath = name is "write_file" or "edit_file" ? ResolvePath(ArgumentOf(argumentsJson, "path")) : null,
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

    string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return _toolContext.Resolve(path); }
        catch (ToolException) { return null; }
    }

    FrameworkElement? MakeSubject(string tool, string argumentsJson)
    {
        string? Get(string key) => ArgumentOf(argumentsJson, key);
        TextBlock? Plain(string? text) => string.IsNullOrWhiteSpace(text) ? null : new TextBlock
        {
            Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = Shorten(PrettyJson(argumentsJson), 800),
        }.Paint(ForegroundProperty, "TextMuted");
        return tool switch
        {
            "run_command" => Plain(OneLine(Get("command"))),
            "search_files" => Plain(OneLine(Get("pattern"))),
            "search_text" => Plain(OneLine(Get("query"))) ?? Plain("Запрос без текста"),
            "generate_image" => Plain(OneLine(Get("prompt"))),
            "use_plugin" => Plain(Get("name")),
            "read_log" => Plain(Get("log_id")),
            "run_plugin" => Plain($"{Get("name")} · {Get("script")}"),
            ComputerTools.Name => Plain(OneLine($"{ComputerActivity(Get("action"))} {Get("element") ?? Get("path") ?? Get("text") ?? Get("window")}")),
            "delete_path" => Plain(ShortName(Get("path"))),
            "move_path" or "copy_path" => Plain($"{ShortName(Get("source"))} → {ShortName(Get("destination"))}"),
            "get_system_info" or "update_plan" => null,
            _ => Get("path") is var path && ResolvePath(path) is { } full ? _links.FileRef(full, ShortName(path)) : Plain(ShortName(path)),
        };
    }

    void CompleteToolRow(ToolRow row, string output, bool isError, bool isWarning = false, FileDiff? diff = null, FileChange? change = null, bool undone = false)
    {
        row.Label.IsActive = false;
        if (isError || isWarning)
        {
            row.Status.Text = isError ? Glyphs.Cancel : Glyphs.Warning;
            row.Status.Paint(ForegroundProperty, isError ? "DangerText" : "Warning");
            row.Status.Visibility = Visibility.Visible;
            row.Status.ToolTip = isError ? "Не выполнено" : "Команда завершилась с ошибкой или по таймауту — раскройте подробности";
            var line = output.Split('\n')[0].Trim();
            row.Root.Children.Add(new TextBlock
            {
                Text = Shorten(line, 220), TextWrapping = TextWrapping.Wrap, Margin = new(24, 0, 0, 6),
            }.Paint(ForegroundProperty, isError ? "DangerText" : "Warning"));
            if (isError) row.EditGroup?.ShowError(Shorten(line, 100));
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
        if (row.Name == "generate_image" && !isError && ImageTools.SavedPath(output) is { } image && File.Exists(image))
            row.Root.Children.Add(ImagePreview(image));
        if (row.Name == ComputerTools.Name && !isError && output.StartsWith(ComputerTools.ShotPrefix, StringComparison.Ordinal) &&
            output[ComputerTools.ShotPrefix.Length..].Split('\n')[0].Trim() is var shot && File.Exists(shot))
            row.Root.Children.Add(ViewedImage(shot));
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

    FrameworkElement ImagePreview(string path)
    {
        var preview = new Border
        {
            Width = 320, Height = 320, CornerRadius = new(12), Margin = new(24, 4, 0, 8), HorizontalAlignment = HorizontalAlignment.Left,
            Cursor = Cursors.Hand, ToolTip = $"{path}\nЩелчок — посмотреть, правая кнопка — другие действия",
            ContextMenu = FileActions.BuildMenu(this, path),
        }.Paint(BackgroundProperty, "Surface");
        AutomationProperties.SetName(preview, "Изображение " + Path.GetFileName(path));
        preview.MouseLeftButtonUp += (_, _) => ShowPicture(path);
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

    FrameworkElement ViewedImage(string path)
    {
        var chevron = GlyphText(Glyphs.ChevronRight, 12, "TextMuted", new(6, 0, 0, 0));
        var toggle = new Button
        {
            Style = Theme.Style("RowButton"), Padding = new(4, 6, 8, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(20, 0, 0, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { GlyphText(Glyphs.Picture, 16, "TextMuted", new(0, 0, 8, 0)), new TextBlock { Text = "Просмотрено изображение" }.Paint(ForegroundProperty, "TextMuted"), chevron },
            },
        };
        AutomationProperties.SetName(toggle, "Просмотрено изображение — показать скриншот");
        var reveal = new RevealPanel();
        toggle.Click += (_, _) =>
        {
            reveal.Child ??= ImagePreview(path);
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
        if (row.Details == null)
        {
            var details = new StackPanel { Margin = new(24, 4, 0, 8) };
            row.Details = new RevealPanel { Child = details };
            if (row.Diff is { } diff)
            {
                var name = new TextBlock { Text = ShortName(row.FilePath) }.Paint(ForegroundProperty, "Text");
                var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(12, 8, 12, 8), Children = { name, DiffSummary(diff.Added, diff.Removed) } };
                TextBlock.SetFontSize(header, 13);
                var view = BlockBox(new StackPanel { Children = { header, DiffView(diff, InlineDiffLines) } }, new(0, 0, 0, 8));
                view.Margin = new(0, 0, 0, 8);
                if (row.FilePath is { } path)
                {
                    view.Cursor = Cursors.Hand;
                    view.ToolTip = "Открыть справа";
                    view.MouseLeftButtonUp += (_, _) => ShowPreview(path, diff);
                }
                details.Children.Add(view);
            }
            var log = row.Name is "run_command" or "run_plugin" ? CommandLog.IdOf(output) : null;
            if (log != null && CommandLog.Read(AppPaths.LogsFolder, log) is { } full) output = output.Split('\n', 2)[0] + "\n" + full;
            var command = row.Name == "run_command" ? ArgumentOf(row.ArgumentsJson, "command") : null;
            var raw = SelectableText(KeepEnds(command != null ? output : $"Параметры:\n{PrettyJson(row.ArgumentsJson)}\n\nРезультат:\n{output}", 16_000),
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
            var state = BlockLabel(IsShown(row.Status) ? "Не выполнено" : "Выполнено");
            DockPanel.SetDock(state, Dock.Right);
            var footer = new DockPanel { LastChildFill = false, Margin = new(0, 8, 0, 0), Children = { state } };
            if (log != null)
            {
                var journal = new Button { Content = "Открыть журнал", ToolTip = "Показать полный вывод в панели справа" };
                AutomationProperties.SetName(journal, "Открыть журнал команды");
                journal.Click += (_, _) =>
                {
                    if (CommandLog.PathOf(AppPaths.LogsFolder, log) is { } path && File.Exists(path))
                        ShowPreview(path, title: OneLine(ArgumentOf(row.ArgumentsJson, "command") ?? Describe(row.Name).Label));
                    else ShowToast($"Журнал команды удалён. Журналы хранятся {CommandLog.KeepDays} дней.", ToastKind.Warning);
                };
                footer.Children.Add(journal);
            }
            details.Children.Add(BlockBox(new StackPanel { Children = { BlockLabel(Describe(row.Name).Label), code, footer } }, new(12)));
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
        var hint = (open ? "Скрыть " : "Показать ") + "параметры и результат";
        row.Expand.ToolTip = hint;
        AutomationProperties.SetName(row.Expand, hint);
    }

    void FinishTurn()
    {
        MarkUnfinishedToolRows();
        FinishProgress("Выполнение прервано");
        FlushTurnFiles();
    }

    void MarkUnfinishedToolRows()
    {
        _chatView.PendingPlan = null;
        foreach (var row in _chatView.ToolRows.Values.Where(r => r.Output == null)) CompleteToolRow(row, "Выполнение прервано.", isError: true);
    }

    static string OneLine(string? value) => Shorten((value ?? "").Replace("\r", "").Replace("\n", " ⏎ ").Trim(), 70);

    static string Shorten(string text, int max) => text.Length > max ? text[..max] + "…" : text;

    static string KeepEnds(string text, int max) =>
        text.Length <= max ? text : text[..(max / 4)] + $"\n… пропущено {text.Length - max} символов …\n" + text[^(max - max / 4)..];

    static string ShortName(string? path) => string.IsNullOrWhiteSpace(path) ? ""
        : path.Trim().TrimEnd('\\', '/') is var trimmed && Path.GetFileName(trimmed) is { Length: > 0 } name ? name : trimmed;

    void RenderHistory(IReadOnlyList<JsonNode> history)
    {
        bool wasSuppressed = _suppressAnimation;
        _suppressAnimation = true;
        try
        {
            var messages = history.OfType<JsonObject>().ToArray();
            for (int i = 0; i < messages.Length; i++)
            {
                var m = messages[i];
                var role = JsonUtil.Str(m["role"]);
                if (role == "user")
                {
                    FinishTurn();
                    AddUserBubble(AgentRunner.ExtractText(m["content"]) ?? "", Attachment.ListFrom(m));
                }
                else if (role == "assistant")
                {
                    ResetProgressStep();
                    OnModelProgress(AgentRunner.ProgressOf(m));
                    var sentAt = AgentRunner.SentAtOf(m);
                    var text = AgentRunner.ExtractText(m["content"])?.Trim() ?? "";
                    if (m["tool_calls"] is JsonArray { Count: > 0 } calls)
                    {
                        if (text.Length > 0) AddProgressItem(ProgressText(text, "Промежуточное сообщение"));
                        var results = messages.Skip(i + 1).TakeWhile(next => JsonUtil.Str(next["role"]) == "tool").ToArray();
                        foreach (var call in calls.OfType<JsonObject>())
                        {
                            var id = JsonUtil.Str(call["id"]) ?? "";
                            var result = Array.FindLast(results, next => (JsonUtil.Str(next["tool_call_id"]) ?? "") == id);
                            var meta = result?[AgentRunner.MetaField] as JsonObject;
                            if (JsonUtil.Bool(meta?["read_reused"]) == true || JsonUtil.Bool(meta?["action_reused"]) == true) continue;
                            var function = call["function"] as JsonObject;
                            var name = JsonUtil.Str(function?["name"]) ?? "?";
                            var arguments = JsonUtil.Str(function?["arguments"]) ?? "{}";
                            var output = JsonUtil.Str(result?["content"]) ?? "";
                            bool isError = JsonUtil.Bool(meta?["command_restricted"]) == true || ToolRegistry.IsErrorOutput(output);
                            if (name == "update_plan")
                            {
                                if (result != null) CompletePlan(id, arguments, output, isError);
                                continue;
                            }
                            var row = AddToolRow(id, name, arguments);
                            if (result == null) continue;
                            CompleteToolRow(row, output, isError,
                                row.Name is "run_command" or "run_plugin" && ToolRegistry.CommandFailed(output), AgentRunner.DiffOf(result),
                                AgentRunner.FileChangeOf(result), AgentRunner.FileChangeUndone(result));
                        }
                        continue;
                    }
                    var elapsed = AgentRunner.ElapsedOf(m);
                    if (AgentRunner.ReviewOf(m) != null) AddProgressItem(ProgressText(text, "Промежуточное сообщение"));
                    else if (text.Length > 0 || elapsed != null) ShowFinalAnswer(text, elapsed, sentAt);
                }
                if (AgentRunner.StopReasonOf(m) is { } stop) AddNotice(stop, warning: true, inProgress: false);
            }
            FinishTurn();
        }
        finally
        {
            _suppressAnimation = wasSuppressed;
        }
    }

    void CopyLastAnswer_Click(object sender, RoutedEventArgs e)
    {
        var last = _agent.History.OfType<JsonObject>().Where(m => JsonUtil.Str(m["role"]) == "assistant" && m["tool_calls"] == null && AgentRunner.ReviewOf(m) == null)
            .Select(m => AgentRunner.ExtractText(m["content"])).LastOrDefault(text => !string.IsNullOrWhiteSpace(text));
        if (last == null) ShowToast("В этом чате пока нет ответов");
        else if (TextClipboard.SetText(last.Trim())) ShowToast("Последний ответ скопирован", ToastKind.Success);
        else ShowToast("Буфер обмена занят другой программой — попробуйте ещё раз", ToastKind.Error);
    }

    void AppendToChat(UIElement element)
    {
        (_chatView.Target ?? ChatPanel).Children.Add(element);
        if (_chatView.Target == null) EmptyChat.Visibility = Visibility.Collapsed;
        if (!_suppressAnimation && Theme.AnimationsEnabled)
            element.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void ReleaseStickyProgress()
    {
        ChatStickyHost.Visibility = Visibility.Collapsed;
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

    // Pins the header and the plan of the running section once the user scrolls its top out of view.
    void UpdateStickyProgress(bool userScroll)
    {
        if (_chatView.Progress is not { View.IsExpanded: true } progress || !progress.View.IsDescendantOf(ChatScroll) ||
            progress.View.TransformToAncestor(ChatScroll).Transform(default).Y >= 0)
        {
            ReleaseStickyProgress();
            return;
        }
        if (!IsShown(ChatStickyHost))
        {
            if (!userScroll) return;
            ChatStickyTitle.SetBinding(TextBlock.TextProperty, new Binding { Source = progress.View, Path = new(AutomationProperties.NameProperty) });
            ChatStickyHost.Visibility = Visibility.Visible;
            ChatStickyTitle.IsActive = true;
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
        if (e.ExtentHeightChange != 0 && _autoScroll && _chatMotion == null) ChatScroll.ScrollToEnd();
        else if (e.VerticalChange != 0 && _chatMotion == null) _autoScroll = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 30;
        if (e.VerticalChange < 0 && e.ExtentHeightChange == 0 && _chatMotion == null) CheckOlderHistory();
        UpdateStickyProgress(e.VerticalChange != 0 && !_autoScroll && _chatMotion == null);
        UpdateScrollDownButton();
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

    static FrameworkElement MakeSpinner(double size)
    {
        var rotate = new RotateTransform();
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = size, Height = size, StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 7, 14 }, StrokeDashCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new(0.5, 0.5), RenderTransform = rotate,
        }.Paint(System.Windows.Shapes.Shape.StrokeProperty, "AccentText");
        if (Theme.AnimationsEnabled)
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        return ring;
    }

    static void StopSpinner(FrameworkElement spinner)
    {
        if (spinner.RenderTransform is RotateTransform rotate) rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        spinner.Visibility = Visibility.Collapsed;
    }

    static string PrettyJson(string json)
    {
        try { return JsonNode.Parse(json)?.ToJsonString(JsonUtil.Pretty) ?? json; }
        catch { return json; }
    }
}
