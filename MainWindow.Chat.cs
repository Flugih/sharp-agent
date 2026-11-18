using System.Windows.Shell;

namespace SharpAgent;

public partial class MainWindow
{
    enum ErrorFix { None, Retry, Settings, Usage }

    static ErrorFix FixFor(ApiException ex) => ex.Code == ChatGptClient.LimitCode ? ErrorFix.Usage
        : ex.Code != ChatGptClient.InvalidUserCode && ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? ErrorFix.Settings : ErrorFix.Retry;

    sealed record ToolRow(string Id, string Name, string ArgumentsJson, StackPanel Root, Panel Header, FrameworkElement Spinner, TextBlock Status, Button Expand)
    {
        public string? FilePath, Output;
        public FrameworkElement? Subject;
        public RevealPanel? Details;
        public FileDiff? Diff;
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
    }

    ChatRenderState _chatView = new();
    Border? _lastErrorCard;
    FrameworkElement? _stickyProgress;
    Panel? _stickyProgressParent;
    Border? _stickyProgressPlaceholder;
    int _stickyProgressIndex = -1;
    Thickness _stickyProgressMargin;
    bool _stickyProgressChanging;
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
        UpdateSendButton();
        EmptyChat.Visibility = VisibleIf(ChatPanel.Children.Count == 0);
        UpdateContextBanner();
        SyncCurrentChat(touched: false);
        SyncCompactChat();
    }

    void AddUserBubble(string text, IReadOnlyList<Attachment> attachments)
    {
        FlushTurnFiles();
        _chatView.ChangeCards.Clear();
        _chatView.FileEditGroups.Clear();
        _chatView.LastStopNotice = null;
        _chatView.Progress = null;
        _chatView.ProgressHint = null;
        _chatView.PendingPlan = null;
        ResetProgressStep();
        _chatView.ToolRows.Clear();
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(64, 16, 0, 8) };
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
                FontFamily = Theme.Ui, Foreground = Brushes.White, FontSize = 14, PagePadding = new(0), LineHeight = 23,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            }, Brushes.White, "Сообщение пользователя");
            var menu = new ContextMenu();
            menu.Items.Add(FileActions.Item("Копировать", Glyphs.Copy,
                () => TextClipboard.SetText(body.Selection.IsEmpty ? text : body.Selection.Text), "Ctrl+C"));
            menu.Items.Add(FileActions.Item("Вставить в поле ввода", Glyphs.Edit, () => PutIntoInput(text)));
            body.ContextMenu = menu;
            var bubble = new Border
            {
                CornerRadius = new(18), Padding = new(15, 6, 15, 12), HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = Math.Min(515, Math.Ceiling(measure.DesiredSize.Width) + 42), Child = body,
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

    System.Windows.Documents.FlowDocument ChatDocument(string markdown, Brush foreground)
    {
        var document = _markdown.Render(markdown);
        document.Foreground = foreground;
        return document;
    }

    RichTextBox ChatText(string markdown, Brush foreground, string label) => ReadOnlyChatText(ChatDocument(markdown, foreground), foreground, label);

    static RichTextBox ReadOnlyChatText(System.Windows.Documents.FlowDocument content, Brush foreground, string label)
    {
        var box = new RichTextBox
        {
            Document = content, IsReadOnly = true, IsDocumentEnabled = true, IsReadOnlyCaretVisible = false, IsTabStop = false,
            Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0), Foreground = foreground,
            SelectionBrush = Theme.Accent, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, FocusVisualStyle = null,
        };
        AutomationProperties.SetName(box, label);
        return box;
    }

    static TextBlock GlyphText(string glyph, double size, Brush? foreground, Thickness margin = default, bool center = true)
    {
        var block = new TextBlock
        {
            Text = glyph, FontFamily = Theme.Icons, FontSize = size, Margin = margin, VerticalAlignment = center ? VerticalAlignment.Center : VerticalAlignment.Stretch,
        };
        if (foreground != null) block.Foreground = foreground;
        return block;
    }

    static Border SurfaceBox(UIElement child, double radius, Thickness padding = default, Thickness margin = default) => new()
    {
        Background = Theme.Surface, BorderBrush = Theme.Line, BorderThickness = new(1), CornerRadius = new(radius),
        Padding = padding, Margin = margin, Child = child,
    };

    StackPanel AnswerView(string markdown, DateTimeOffset? sentAt)
    {
        var document = ChatText(markdown, Theme.Text, "Ответ агента");
        const string copyHint = "Скопировать ответ";
        var copy = Theme.IconButton(Glyphs.Copy, copyHint);
        copy.HorizontalAlignment = HorizontalAlignment.Left;
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
            FontSize = 12, Foreground = Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new(5, 0, 0, 0),
        });
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
                : files[0].Undone ? new TextBlock { Text = "Изменение отменено", Foreground = Theme.TextMuted }
                : FileCard(files[0].Path, files[0].Diff, files[0].Created);
            var cards = new Border { Margin = new(0, 14, 0, 2), Child = card };
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
        _compactChat?.SetStatus("Ошибка");
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
        var body = new StackPanel { Children = { SelectableText(text, Theme.DangerText) } };
        (string Label, Action Run)? action = fix switch
        {
            ErrorFix.Retry when _agent.CanContinue => ("Повторить", () => _ = RetryAsync()),
            ErrorFix.Settings => ("Открыть настройки", ShowSettings),
            ErrorFix.Usage => ("Управлять использованием", () => FileActions.OpenUrl(ChatGptClient.UsageUrl)),
            _ => null,
        };
        if (action is { } fixIt)
        {
            var button = new Button { Content = fixIt.Label, Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 10, 0, 0) };
            button.Click += (_, _) => fixIt.Run();
            body.Children.Add(button);
        }
        Grid.SetColumn(body, 1);
        var card = new Border
        {
            Background = Theme.DangerSoft, BorderBrush = Theme.DangerLine, BorderThickness = new(1), CornerRadius = new(8),
            Padding = new(16, 12, 16, 12), Margin = new(0, 8, 0, 8), Tag = text,
            Child = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition() },
                Children = { GlyphText(Glyphs.Warning, 16, Theme.DangerText, new(0, 2, 12, 0), false), body },
            },
        };
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
        if (warning || !inProgress) _compactChat?.SetStatus(text.ReplaceLineEndings(" "));
        var row = new DockPanel { Margin = inProgress ? new(0, 6, 0, 6) : new(0, 8, 0, 8) };
        if (warning) row.Children.Add(GlyphText(Glyphs.Warning, 13, Theme.Warning, new(0, 2, 8, 0), false));
        row.Children.Add(SelectableText(text, inProgress ? Theme.TextMuted : Theme.Text, fontSize: inProgress ? 13 : 14));
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

    void AddFileEditProgress(string name, string? path, UIElement item)
    {
        if (name is not ("write_file" or "edit_file") || path == null)
        {
            AddProgressTool(name, item);
            return;
        }
        var progress = EnsureProgress();
        if (!_chatView.FileEditGroups.TryGetValue(path, out var group))
        {
            group = _chatView.FileEditGroups[path] = new("Правки файла · " + ShortName(path), Glyphs.Edit);
            group.View.IsExpanded = false;
            group.View.Margin = new(-3, 2, 0, 2);
            progress.Items.Children.Add(group.View);
        }
        group.Items.Children.Add(item);
        group.SetTitle($"Правки файла · {ShortName(path)} · {group.Items.Children.Count}");
    }

    ToolRow AddToolRow(string id, string name, string argumentsJson)
    {
        var (glyph, label) = Describe(name);
        var spinner = MakeSpinner(12);
        spinner.Margin = new(10, 0, 0, 0);
        var status = new TextBlock
        {
            FontFamily = Theme.Icons, FontSize = 12, Margin = new(10, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        var expand = Theme.IconButton(Glyphs.ChevronRight, $"Подробности действия «{label}»", 24, 10);
        expand.Margin = new(4, 0, 0, 0);
        expand.Visibility = Visibility.Collapsed;
        var header = new WrapPanel
        {
            Background = Brushes.Transparent,
            Children =
            {
                spinner, status, expand,
            },
        };
        var subject = MakeSubject(name, argumentsJson);
        if (name != "search_text")
        {
            header.Children.Insert(0, GlyphText(glyph, 14, Theme.TextMuted, new(0, 1, 8, 0)));
            header.Children.Insert(1, new TextBlock { Text = label, FontSize = 13, Foreground = Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center });
        }
        else subject ??= TextChip("Запрос без текста");
        if (subject != null) header.Children.Insert(name == "search_text" ? 0 : 2, subject);
        var root = new StackPanel { Margin = new(0, 4, 0, 4), Children = { header } };
        var row = new ToolRow(id, name, argumentsJson, root, header, spinner, status, expand)
        {
            Subject = subject, FilePath = name is "write_file" or "edit_file" ? ResolvePath(ArgumentOf(argumentsJson, "path")) : null,
        };
        header.MouseLeftButtonUp += (_, _) => ToggleDetails(row);
        expand.Click += (_, _) => ToggleDetails(row);
        _chatView.ToolRows[id] = row;
        AddFileEditProgress(name, row.FilePath, root);
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
        var element = tool switch
        {
            "run_command" => TextChip(OneLine(Get("command")), mono: true),
            "search_files" => TextChip(OneLine(Get("pattern"))),
            "search_text" => TextChip(OneLine(Get("query"))),
            "generate_image" => TextChip(OneLine(Get("prompt"))),
            "use_plugin" => TextChip(Get("name")),
            "read_log" => TextChip(Get("log_id"), mono: true),
            "run_plugin" => TextChip($"{Get("name")} · {Get("script")}"),
            ComputerTools.Name => TextChip(OneLine($"{ComputerActivity(Get("action"))} {Get("element") ?? Get("path") ?? Get("text") ?? Get("window")}")),
            "delete_path" => TextChip(ShortName(Get("path"))),
            "move_path" or "copy_path" => TextChip($"{ShortName(Get("source"))} → {ShortName(Get("destination"))}"),
            "get_system_info" => null,
            "update_plan" => null,
            _ => Get("path") is var path && ResolvePath(path) is { } full ? _links.FileChip(full, ShortName(path)) : TextChip(ShortName(path)),
        };
        if (element == null) return null;
        if (tool != "search_text") element.Margin = new(8, 0, 0, 0);
        if (element is not Button) element.ToolTip = Shorten(PrettyJson(argumentsJson), 800);
        return element;
    }

    static FrameworkElement? TextChip(string? text, bool mono = false) => string.IsNullOrWhiteSpace(text) ? null : new Border
    {
        Background = Theme.Surface2, BorderBrush = Theme.LineStrong, BorderThickness = new(1), CornerRadius = new(8),
        Padding = new(8, 2, 8, 3), VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 13, Foreground = Theme.TextSoft, FontFamily = mono ? Theme.Mono : Theme.Ui },
    };

    void CompleteToolRow(ToolRow row, string output, bool isError, bool isWarning = false, FileDiff? diff = null, FileChange? change = null, bool undone = false)
    {
        StopSpinner(row.Spinner);
        row.Status.Text = isError ? Glyphs.Cancel : isWarning ? Glyphs.Warning : Glyphs.Check;
        row.Status.Foreground = isError ? Theme.DangerText : isWarning ? Theme.Warning : Theme.Success;
        row.Status.Visibility = Visibility.Visible;
        row.Status.ToolTip = isError ? "Не выполнено" : isWarning ? "Команда завершилась с ошибкой или по таймауту — раскройте подробности" : "Выполнено";
        if (isError || isWarning)
            row.Root.Children.Add(new TextBlock
            {
                Text = Shorten(output.Split('\n')[0].Trim(), 220), Foreground = isError ? Theme.DangerText : Theme.Warning, FontSize = 13,
                TextWrapping = TextWrapping.Wrap, Margin = new(22, 4, 0, 0),
            });
        else if (row.Name is "move_path" or "copy_path" && ResultTarget(output) is { } target)
        {
            if (row.Subject is { } subject && row.Header.Children.IndexOf(subject) is >= 0 and var index)
            {
                var chip = _links.FileChip(target);
                chip.Margin = new(8, 0, 0, 0);
                row.Header.Children.RemoveAt(index);
                row.Header.Children.Insert(index, chip);
                row.Subject = chip;
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
        if (row.Diff != null) row.Header.Children.Insert(row.Header.Children.IndexOf(row.Spinner), DiffSummary(row.Diff.Added, row.Diff.Removed));
        if (!isError && change != null) NoteProduced(change.Path, row.Diff, change.Created, change, undone);
        else if (!isError && !isWarning && Produced(row.Name, row.ArgumentsJson, row.FilePath, output) is { } produced) NoteProduced(produced.Path, row.Diff, produced.Created, null, undone);
        row.Expand.Visibility = Visibility.Visible;
        ShowExpandState(row, open: false);
    }

    static StackPanel DiffSummary(long added, long removed) => new()
    {
        Orientation = Orientation.Horizontal, Margin = new(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Children =
        {
            new TextBlock { Text = $"+{added}", FontSize = 12.5, Foreground = Theme.Success },
            new TextBlock { Text = $"−{removed}", FontSize = 12.5, Foreground = Theme.DangerText, Margin = new(6, 0, 0, 0) },
        },
    };

    FrameworkElement ImagePreview(string path)
    {
        var preview = new Border
        {
            Width = 320, Height = 320, CornerRadius = new(8), Margin = new(22, 8, 0, 4), HorizontalAlignment = HorizontalAlignment.Left,
            Background = Theme.Surface, Cursor = Cursors.Hand, ToolTip = $"{path}\nЩелчок — посмотреть, правая кнопка — другие действия",
            ContextMenu = FileActions.BuildMenu(this, path),
        };
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
        var chevron = new TextBlock { FontFamily = Theme.Icons, FontSize = 9, Text = Glyphs.ChevronRight, Margin = new(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        var toggle = new Button
        {
            Style = Theme.Style("RowButton"), Padding = new(6, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new(16, 4, 0, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { chevron, GlyphText(Glyphs.Picture, 13, Theme.TextMuted, new(0, 1, 6, 0), false), new TextBlock { Text = "Просмотрено изображение", FontSize = 13, Foreground = Theme.TextMuted } },
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
            var details = new StackPanel();
            row.Details = new RevealPanel { Child = details };
            if (row.Diff is { } diff)
            {
                var view = SurfaceBox(DiffView(diff, InlineDiffLines), 8, new(0, 4, 0, 4), new(22, 6, 0, 4));
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
            var raw = SelectableText(KeepEnds($"Параметры:\n{PrettyJson(row.ArgumentsJson)}\n\nРезультат:\n{output}", 16_000),
                Theme.TextSoft, mono: true, fontSize: 13);
            raw.MaxHeight = 360;
            raw.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            details.Children.Add(SurfaceBox(raw, 8, new(12, 8, 4, 8), new(22, 8, 0, 4)));
            if (log != null)
            {
                var journal = new Button
                {
                    Style = Theme.Style("RowButton"), Content = "Открыть журнал", Padding = new(8, 4, 8, 4), Margin = new(22, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Показать полный вывод в панели справа",
                };
                AutomationProperties.SetName(journal, "Открыть журнал команды");
                journal.Click += (_, _) =>
                {
                    if (CommandLog.PathOf(AppPaths.LogsFolder, log) is { } path && File.Exists(path))
                        ShowPreview(path, title: OneLine(ArgumentOf(row.ArgumentsJson, "command") ?? Describe(row.Name).Label));
                    else ShowToast($"Журнал команды удалён. Журналы хранятся {CommandLog.KeepDays} дней.", ToastKind.Warning);
                };
                details.Children.Add(journal);
            }
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
        if (_stickyProgress is not { } view || _stickyProgressParent is not { } parent) return;
        _stickyProgressChanging = true;
        try
        {
            ChatStickyPanel.Children.Remove(view);
            if (_stickyProgressPlaceholder is { } placeholder && placeholder.Parent is Panel placeholderParent)
                placeholderParent.Children.Remove(placeholder);
            int index = Math.Clamp(_stickyProgressIndex, 0, parent.Children.Count);
            parent.Children.Insert(index, view);
            view.Margin = _stickyProgressMargin;
        }
        finally
        {
            _stickyProgress = null;
            _stickyProgressParent = null;
            _stickyProgressPlaceholder = null;
            _stickyProgressIndex = -1;
            _stickyProgressChanging = false;
            ChatStickyHost.Visibility = Visibility.Collapsed;
        }
    }

    void UpdateStickyProgress(double verticalChange)
    {
        if (_stickyProgressChanging) return;
        if (verticalChange < 0)
        {
            ReleaseStickyProgress();
            return;
        }
        if (verticalChange <= 0 || _stickyProgress != null) return;
        var progress = _chatView.Progress;
        if (progress is not { View.IsExpanded: true } || progress.View.Parent is not Panel parent) return;
        if (progress.View.TransformToAncestor(ChatScroll).Transform(new Point(0, 0)).Y > 0) return;

        _stickyProgressChanging = true;
        try
        {
            var view = _stickyProgress = progress.View;
            _stickyProgressParent = parent;
            _stickyProgressIndex = parent.Children.IndexOf(view);
            _stickyProgressMargin = view.Margin;
            _stickyProgressPlaceholder = new Border { Height = view.ActualHeight, Margin = view.Margin };
            parent.Children.RemoveAt(_stickyProgressIndex);
            parent.Children.Insert(_stickyProgressIndex, _stickyProgressPlaceholder);
            view.Margin = new(0, 0, 0, 0);
            ChatStickyPanel.Children.Add(view);
            ChatStickyHost.Visibility = Visibility.Visible;
            ChatStickyPanel.UpdateLayout();
        }
        finally { _stickyProgressChanging = false; }
    }

    void ChatScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_changingHistory || !ReferenceEquals(e.OriginalSource, ChatScroll)) return;
        if (e.ExtentHeightChange != 0 && _autoScroll && _chatMotion == null) ChatScroll.ScrollToEnd();
        else if (e.VerticalChange != 0 && _chatMotion == null) _autoScroll = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 30;
        if (e.VerticalChange < 0 && e.ExtentHeightChange == 0 && _chatMotion == null) CheckOlderHistory();
        UpdateStickyProgress(e.VerticalChange);
        UpdateScrollDownButton();
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

    static TextBox SelectableText(string text, Brush foreground, bool mono = false, double fontSize = 14) => new()
    {
        Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = false, IsTabStop = false, BorderThickness = new(0),
        Background = Brushes.Transparent, Foreground = foreground, SelectionBrush = Theme.Accent, TextWrapping = TextWrapping.Wrap,
        Padding = new(0), FontSize = fontSize, FontFamily = mono ? Theme.Mono : Theme.Ui, FocusVisualStyle = null,
        ContextMenu = (ContextMenu)Application.Current.Resources["ReadOnlyTextMenu"],
    };

    static FrameworkElement MakeSpinner(double size)
    {
        var rotate = new RotateTransform();
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = size, Height = size, Stroke = Theme.AccentText, StrokeThickness = 1.6, StrokeDashArray = new DoubleCollection { 7, 14 }, StrokeDashCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center, RenderTransformOrigin = new(0.5, 0.5), RenderTransform = rotate,
        };
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
