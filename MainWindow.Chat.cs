using System.Net;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using SharpAgent.Agent;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Tools;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private enum ErrorFix { None, Retry, Settings, Usage }

    private static ErrorFix FixFor(ApiException ex) => ex.Code switch
    {
        ChatGptClient.LimitCode => ErrorFix.Usage,
        ChatGptClient.InvalidUserCode => ErrorFix.Retry,
        _ => ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? ErrorFix.Settings : ErrorFix.Retry,
    };

    private sealed class ToolRow
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string ArgumentsJson { get; init; }
        public required StackPanel Root { get; init; }
        public required Panel Header { get; init; }
        public required FrameworkElement Spinner { get; init; }
        public required TextBlock Status { get; init; }
        public required Button Expand { get; init; }
        public string? FilePath { get; set; }
        public FrameworkElement? Subject { get; set; }
        public RevealPanel? Details { get; set; }
        public string? Output { get; set; }
        public FileDiff? Diff { get; set; }
        public bool Finished { get; set; }
    }

    private sealed record ProducedFile(string Path, FileDiff? Diff, bool Created, FileChange? Change, string[] Ids,
        bool Undone = false, bool Chained = false);

    private sealed class ChatRenderState(Panel? target = null)
    {
        public Panel? Target { get; } = target;
        public Dictionary<string, ToolRow> ToolRows { get; } = new();
        public List<ProducedFile> TurnFiles { get; } = [];
        public StackPanel? LastAnswer;
        public FrameworkElement? LastStopNotice;
        public ProgressSection? Progress;
        public (ProgressSection Section, bool Commands)? ToolGroup;
        public RichTextBox? ReasoningRow;
        public RichTextBox? CommentaryRow;
        public TextBlock? ProgressHint;
    }

    private ChatRenderState _chatView = new();
    private Border? _lastErrorCard;
    private string? _lastErrorText;
    private bool _autoScroll = true;
    private bool _suppressAnimation;

    private void RenderCurrentChat()
    {
        ClearSubagents();
        ClearDraft();
        ChatPanel.Children.Clear();
        _chatView = new ChatRenderState();
        _lastErrorCard = null;
        _lastErrorText = null;
        _autoScroll = true;
        ClosePreview();

        ResetHistoryPages();
        UpdateSendButton();
        EmptyChat.Visibility = ChatPanel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        UpdateContextBanner();
        SyncCurrentChat(touched: false);
    }

    private void AddUserBubble(string text, IReadOnlyList<Attachment> attachments)
    {
        FlushTurnFiles();
        _chatView.LastStopNotice = null;
        _chatView.Progress = null;
        _chatView.ProgressHint = null;
        ResetProgressStep();
        _chatView.ToolRows.Clear();

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(64, 16, 0, 8) };
        if (attachments.Count > 0)
        {
            var files = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var attachment in attachments)
            {
                var chip = _links.FileChip(attachment.Path, attachment.Name);
                chip.Margin = new Thickness(6, 0, 0, 6);
                files.Children.Add(chip);
            }
            stack.Children.Add(files);
        }

        if (text.Length > 0)
        {
            var measure = new TextBlock { Text = text, FontFamily = Theme.Ui, FontSize = 14 };
            measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var document = new System.Windows.Documents.FlowDocument(new System.Windows.Documents.Paragraph(
                new System.Windows.Documents.Run(text)) { Margin = new Thickness(0) })
            {
                FontFamily = Theme.Ui, Foreground = Brushes.White, FontSize = 14, PagePadding = new Thickness(0),
                LineHeight = 23, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            var body = ReadOnlyChatText(document, Brushes.White, "Сообщение пользователя");
            var menu = new ContextMenu();
            menu.Items.Add(FileActions.Item("Копировать", Glyphs.Copy,
                () => TextClipboard.SetText(body.Selection.IsEmpty ? text : body.Selection.Text), "Ctrl+C"));
            menu.Items.Add(FileActions.Item("Вставить в поле ввода", Glyphs.Edit, () => PutIntoInput(text)));
            body.ContextMenu = menu;

            var bubble = new Border
            {
                CornerRadius = new CornerRadius(18),
                Padding = new Thickness(15, 6, 15, 12),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = Math.Min(515, Math.Ceiling(measure.DesiredSize.Width) + 42),
                Child = body,
            };
            bubble.SetResourceReference(BackgroundProperty, "UserBubble");
            stack.Children.Add(bubble);
        }
        AppendToChat(stack);
    }

    private void PutIntoInput(string text)
    {
        InputBox.Text = text;
        InputBox.CaretIndex = text.Length;
        InputBox.Focus();
    }

    private System.Windows.Documents.FlowDocument ChatDocument(string markdown, Brush foreground)
    {
        var document = _markdown.Render(markdown);
        document.Foreground = foreground;
        return document;
    }

    private RichTextBox ChatText(string markdown, Brush foreground, string label) =>
        ReadOnlyChatText(ChatDocument(markdown, foreground), foreground, label);

    private static RichTextBox ReadOnlyChatText(System.Windows.Documents.FlowDocument content, Brush foreground, string label)
    {
        var document = new RichTextBox
        {
            Document = content,
            IsReadOnly = true,
            IsDocumentEnabled = true,
            IsReadOnlyCaretVisible = false,
            IsTabStop = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = foreground,
            SelectionBrush = Theme.Accent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FocusVisualStyle = null,
        };
        AutomationProperties.SetName(document, label);
        return document;
    }

    private void AddAssistantMessage(string markdown)
    {
        var document = ChatText(markdown, Theme.Text, "Ответ агента");
        const string copyHint = "Скопировать ответ";
        var copy = Theme.IconButton(Glyphs.Copy, copyHint);
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        copy.Margin = new Thickness(-6, 4, 0, 0);
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

        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(document);
        panel.Children.Add(copy);
        AppendToChat(panel);
        _chatView.LastAnswer = panel;
    }

    private void NoteProduced(string path, FileDiff? diff, bool created, FileChange? change, string id, bool undone)
    {
        int index = _chatView.TurnFiles.FindIndex(file => PathHelper.SamePath(file.Path, path));
        var current = new ProducedFile(path, diff, created, change, [id], undone);
        if (index < 0)
        {
            _chatView.TurnFiles.Add(current);
            return;
        }
        var known = _chatView.TurnFiles[index];
        var chain = known.Change is { } first && change != null && first.AfterHash == change.BeforeHash
            ? first with { AfterHash = change.AfterHash } : null;
        _chatView.TurnFiles[index] = known.Undone && !undone ? current :
            new ProducedFile(path, Combine(known.Diff, diff), known.Created, chain, [.. known.Ids, id],
                known.Undone && undone, chain != null);
    }

    private static FileDiff? Combine(FileDiff? first, FileDiff? second) =>
        first == null || second == null
            ? second ?? first
            : new FileDiff(first.Unified.TrimEnd('\n') + "\n" + second.Unified, first.Added + second.Added, first.Removed + second.Removed);

    private void MoveProduced(string source, string target)
    {
        int index = _chatView.TurnFiles.FindIndex(file => PathHelper.SamePath(file.Path, source));
        if (index >= 0) _chatView.TurnFiles[index] = _chatView.TurnFiles[index] with { Path = target, Change = null };
    }

    private void FlushTurnFiles()
    {
        var files = _chatView.TurnFiles.Where(file => file.Change != null || file.Undone || File.Exists(file.Path))
            .Where(file => file.Change is not { Created: false } change || change.BeforeHash != change.AfterHash)
            .Select(file => file is { Chained: true, Undone: false, Change: { } change }
                ? file with { Diff = FileChanges.NetDiff(_toolContext, change) ?? file.Diff } : file).ToArray();
        if (files.Length > 0)
        {
            FrameworkElement card = files.Length > 1 ? ChangesCard(files) :
                files[0].Undone ? new TextBlock { Text = "Изменение отменено", Foreground = Theme.TextMuted } :
                FileCard(files[0].Path, files[0].Diff, files[0].Created);
            var cards = new Border { Margin = new Thickness(0, 14, 0, 2), Child = card };
            if (_chatView.LastAnswer != null) _chatView.LastAnswer.Children.Insert(_chatView.LastAnswer.Children.Count - 1, cards);
            else AppendToChat(cards);
        }
        _chatView.TurnFiles.Clear();
        _chatView.LastAnswer = null;
    }

    private void OpenCommandLog(string logId, string command)
    {
        var path = CommandLog.PathOf(AppPaths.LogsFolder, logId);
        if (path != null && File.Exists(path)) ShowPreview(path, title: command);
        else ShowToast($"Журнал команды удалён. Журналы хранятся {CommandLog.KeepDays} дней.",
            ToastKind.Warning);
    }

    private (string Path, bool Created)? Produced(ToolRow row, string output) => row.Name switch
    {
        "write_file" or "edit_file" when row.FilePath != null =>
            (row.FilePath, output.StartsWith("Файл создан", StringComparison.Ordinal)),
        "generate_image" when ImageTools.SavedPath(output) is { } image => (image, true),
        "copy_path" when ResultTarget(output) is { } file && File.Exists(file) => (file, true),
        "run_plugin" when !ToolRegistry.CommandFailed(output) &&
                          ResolvePath(ArgumentOf(ArgumentOf(row.ArgumentsJson, "arguments") ?? "{}", "Target")) is { } target &&
                          File.Exists(target) => (target, true),
        _ => null,
    };

    private static string? ResultPath(string output)
    {
        int start = output.IndexOf(": ", StringComparison.Ordinal), end = output.LastIndexOf(" (", StringComparison.Ordinal);
        if (start < 0 || end <= start) return null;
        var path = output[(start + 2)..end].Trim();
        return Path.IsPathFullyQualified(path) ? path : null;
    }

    private void AddError(string text, ErrorFix fix = ErrorFix.None)
    {
        if (!IsActive)
        {
            SetTaskbar(TaskbarItemProgressState.Error);
            WindowEffects.Flash(this);
        }

        if (_lastErrorCard != null && _lastErrorText == text && ChatPanel.Children.Contains(_lastErrorCard))
        {
            ChatPanel.Children.Remove(_lastErrorCard);
            AppendToChat(_lastErrorCard);
            return;
        }
        RemoveErrorCard();

        var body = new StackPanel();
        body.Children.Add(SelectableText(text, Theme.DangerText));
        (string Label, Action Run)? action = fix switch
        {
            ErrorFix.Retry when _agent.CanContinue => ("Повторить", () => _ = RetryAsync()),
            ErrorFix.Settings => ("Открыть настройки", ShowSettings),
            ErrorFix.Usage => ("Управлять использованием", () => FileActions.OpenUrl(ChatGptClient.UsageUrl)),
            _ => null,
        };
        if (action is { } fixIt)
        {
            var button = new Button
            {
                Content = fixIt.Label, Height = 28, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
            };
            button.Click += (_, _) => fixIt.Run();
            body.Children.Add(button);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock
        {
            Text = Glyphs.Warning, FontFamily = Theme.Icons, FontSize = 16,
            Foreground = Theme.DangerText, Margin = new Thickness(0, 2, 12, 0),
        });
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var card = new Border
        {
            Background = Theme.DangerSoft,
            BorderBrush = Theme.DangerLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 8, 0, 8),
            Child = grid,
        };
        AutomationProperties.SetLiveSetting(card, AutomationLiveSetting.Assertive);
        AutomationProperties.SetName(card, "Ошибка: " + text);
        _lastErrorCard = card;
        _lastErrorText = text;
        AppendToChat(card);
    }

    private void RemoveErrorCard()
    {
        if (_lastErrorCard != null) ChatPanel.Children.Remove(_lastErrorCard);
        _lastErrorCard = null;
        _lastErrorText = null;
    }

    private void AddNotice(string text, bool warning = false, bool inProgress = true)
    {
        var row = new DockPanel
        {
            Margin = inProgress ? new Thickness(0, 6, 0, 6) : new Thickness(0, 8, 0, 8),
        };
        if (warning)
        {
            var icon = new TextBlock
            {
                Text = Glyphs.Warning, FontFamily = Theme.Icons, FontSize = 13, Foreground = Theme.Warning,
                Margin = new Thickness(0, 2, 8, 0),
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
        }
        row.Children.Add(SelectableText(text, inProgress ? Theme.TextMuted : Theme.Text, fontSize: inProgress ? 13 : 14));
        if (inProgress && _chatView.Progress != null && _cts != null) AddProgressItem(row);
        else
        {
            if (!inProgress) _chatView.LastStopNotice = row;
            AppendToChat(row);
        }
    }

    private void OnToolCallStarted(ToolCallEvent e) => AddToolRow(e.Id, e.Name, e.ArgumentsJson);

    private void OnToolCallFinished(ToolResultEvent e)
    {
        if (!_chatView.ToolRows.TryGetValue(e.Id, out var row)) return;
        CompleteToolRow(row, e.Output, e.IsError, e.IsWarning, e.Diff, e.Change);
        if (e.Diff != null && !e.IsError && row.FilePath != null && PreviewDocks) ShowPreview(row.FilePath, e.Diff);
    }

    private static string Activity(string tool, string argumentsJson)
    {
        string? Get(string key) => ArgumentOf(argumentsJson, key);
        string Name(string key, string fallback) => Get(key) is { Length: > 0 } path ? ShortName(path) : fallback;
        return tool switch
        {
            "read_file" => "Читаю файл " + Name("path", ""),
            "list_directory" => ShortName(Get("path")) is var name and not ("" or ".") ? "Смотрю папку " + name : "Смотрю рабочую папку",
            "get_file_info" => "Смотрю сведения о " + Name("path", "файле"),
            "search_files" => "Ищу файлы " + OneLine(Get("pattern")),
            "search_text" => $"Ищу текст «{OneLine(Get("query"))}»",
            "write_file" => "Записываю файл " + Name("path", ""),
            "edit_file" => "Правлю файл " + Name("path", ""),
            "create_directory" => "Создаю папку " + Name("path", ""),
            "move_path" => "Перемещаю " + Name("source", ""),
            "copy_path" => "Копирую " + Name("source", ""),
            "delete_path" => "Удаляю " + Name("path", ""),
            "open_path" => "Открываю " + Name("path", ""),
            "run_command" => "Выполняю " + OneLine(Get("command")),
            "run_plugin" => "Запускаю плагин " + Get("name"),
            "use_plugin" => "Открываю плагин " + Get("name"),
            "generate_image" => "Рисую изображение",
            "get_system_info" => "Смотрю сведения о системе",
            "read_log" => "Читаю журнал команды",
            "ask_user" => "Ожидаю ответа на уточнение",
            "delegate_tasks" => "Субагенты выполняют поручения",
            _ => "Выполняю " + tool,
        };
    }

    private static (string Glyph, string Label) Describe(string tool) => tool switch
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
        _ => (Glyphs.Tool, tool),
    };

    private ToolRow AddToolRow(string id, string name, string argumentsJson)
    {
        var (glyph, label) = Describe(name);

        var header = new WrapPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
        header.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = Theme.Icons, FontSize = 14, Foreground = Theme.TextMuted,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 8, 0),
        });
        header.Children.Add(new TextBlock
        {
            Text = label, FontSize = 13, Foreground = Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center,
        });

        var subject = MakeSubject(name, argumentsJson);
        if (subject != null) header.Children.Add(subject);

        var spinner = MakeSpinner(12);
        spinner.Margin = new Thickness(10, 0, 0, 0);
        var status = new TextBlock
        {
            FontFamily = Theme.Icons, FontSize = 12, Margin = new Thickness(10, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        var expand = Theme.IconButton(Glyphs.ChevronRight, $"Подробности действия «{label}»", 24, 10);
        expand.Margin = new Thickness(4, 0, 0, 0);
        expand.Visibility = Visibility.Collapsed;
        header.Children.Add(spinner);
        header.Children.Add(status);
        header.Children.Add(expand);

        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        root.Children.Add(header);

        var row = new ToolRow
        {
            Id = id, Name = name, ArgumentsJson = argumentsJson, Root = root, Header = header,
            Spinner = spinner, Status = status, Expand = expand, Subject = subject,
            FilePath = name is "write_file" or "edit_file" ? ResolvePath(ArgumentOf(argumentsJson, "path")) : null,
        };
        header.MouseLeftButtonUp += (_, _) => ToggleDetails(row);
        expand.Click += (_, _) => ToggleDetails(row);

        _chatView.ToolRows[id] = row;
        AddProgressTool(name, root);
        return row;
    }

    private static string? ArgumentOf(string argumentsJson, string key)
    {
        try
        {
            return JsonNode.Parse(argumentsJson) is JsonObject args ? JsonUtil.Str(args[key]) : null;
        }
        catch
        {
            return null;
        }
    }

    private string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return PathHelper.Resolve(path, _toolContext.WorkingDirectory);
        }
        catch (ToolException)
        {
            return null;
        }
    }

    private FrameworkElement? MakeSubject(string tool, string argumentsJson)
    {
        string? Get(string key) => ArgumentOf(argumentsJson, key);

        FrameworkElement? element = tool switch
        {
            "run_command" => TextChip(OneLine(Get("command")), mono: true),
            "search_files" => TextChip(OneLine(Get("pattern"))),
            "search_text" => TextChip(OneLine(Get("query"))),
            "generate_image" => TextChip(OneLine(Get("prompt"))),
            "use_plugin" => TextChip(Get("name")),
            "read_log" => TextChip(Get("log_id"), mono: true),
            "run_plugin" => TextChip($"{Get("name")} · {Get("script")}"),
            "delete_path" => TextChip(ShortName(Get("path"))),
            "move_path" or "copy_path" => TextChip($"{ShortName(Get("source"))} → {ShortName(Get("destination"))}"),
            "get_system_info" => null,
            _ => PathChip(Get("path")),
        };
        if (element == null) return null;

        element.Margin = new Thickness(8, 0, 0, 0);
        if (element is not Button) element.ToolTip = Shorten(PrettyJson(argumentsJson), 800);
        return element;
    }

    private FrameworkElement? PathChip(string? path) =>
        ResolvePath(path) is { } full ? _links.FileChip(full, ShortName(path)) : TextChip(ShortName(path));

    private static FrameworkElement? TextChip(string? text, bool mono = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new Border
        {
            Background = Theme.Surface2,
            BorderBrush = Theme.LineStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 2, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = Theme.TextSoft,
                FontFamily = mono ? Theme.Mono : Theme.Ui,
            },
        };
    }

    private void CompleteToolRow(ToolRow row, string output, bool isError, bool isWarning = false, FileDiff? diff = null,
        FileChange? change = null, bool undone = false)
    {
        row.Finished = true;
        StopSpinner(row.Spinner);
        row.Status.Text = isError ? Glyphs.Cancel : isWarning ? Glyphs.Warning : Glyphs.Check;
        row.Status.Foreground = isError ? Theme.DangerText : isWarning ? Theme.Warning : Theme.Success;
        row.Status.Visibility = Visibility.Visible;
        row.Status.ToolTip = isError ? "Не выполнено"
            : isWarning ? "Команда завершилась с ошибкой или по таймауту — раскройте подробности"
            : "Выполнено";

        if (isError || isWarning)
        {
            row.Root.Children.Add(new TextBlock
            {
                Text = Shorten(output.Split('\n')[0].Trim(), 220), Foreground = isError ? Theme.DangerText : Theme.Warning,
                FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(22, 4, 0, 0),
            });
        }
        else if (row.Name is "move_path" or "copy_path" && ResultTarget(output) is { } target)
        {
            int index = row.Subject != null ? row.Header.Children.IndexOf(row.Subject) : -1;
            if (index >= 0)
            {
                var chip = _links.FileChip(target);
                chip.Margin = new Thickness(8, 0, 0, 0);
                row.Header.Children.RemoveAt(index);
                row.Header.Children.Insert(index, chip);
                row.Subject = chip;
            }
            if (row.Name == "move_path" && ResolvePath(ArgumentOf(row.ArgumentsJson, "source")) is { } source)
                MoveProduced(source, target);
        }

        if (row.Name == "generate_image" && !isError && ImageTools.SavedPath(output) is { } image && File.Exists(image))
            row.Root.Children.Add(ImagePreview(image));

        if (change != null) row.FilePath = change.Path;
        else if (row.FilePath != null && !isError && ResultPath(output) is { } exact) row.FilePath = exact;
        row.Output = output;
        row.Diff = isError ? null : diff;
        if (row.Diff != null) row.Header.Children.Insert(row.Header.Children.IndexOf(row.Spinner), DiffSummary(row.Diff));
        if (!isError && change != null) NoteProduced(change.Path, row.Diff, change.Created, change, row.Id, undone);
        else if (!isError && !isWarning && Produced(row, output) is { } produced)
            NoteProduced(produced.Path, row.Diff, produced.Created, null, row.Id, undone);
        row.Expand.Visibility = Visibility.Visible;
        ShowExpandState(row, open: false);
    }

    private static string DetailsOutput(string tool, string output) =>
        tool is "run_command" or "run_plugin" && CommandLog.IdOf(output) is { } id && CommandLog.Read(AppPaths.LogsFolder, id) is { } full
            ? output.Split('\n', 2)[0] + "\n" + full
            : output;

    private static Border RawDetails(string argumentsJson, string output)
    {
        var details = SelectableText(
            KeepEnds($"Параметры:\n{PrettyJson(argumentsJson)}\n\nРезультат:\n{output}", 16_000), Theme.TextSoft, mono: true, fontSize: 13);
        details.MaxHeight = 360;
        details.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        return new Border
        {
            Background = Theme.Surface,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 4, 8),
            Margin = new Thickness(22, 8, 0, 4),
            Child = details,
        };
    }

    private static StackPanel DiffSummary(FileDiff diff) => DiffSummary(diff.Added, diff.Removed);

    private static StackPanel DiffSummary(long added, long removed)
    {
        var summary = new StackPanel
        {
            Orientation = Orientation.Horizontal, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        summary.Children.Add(new TextBlock { Text = $"+{added}", FontSize = 12.5, Foreground = Theme.Success });
        summary.Children.Add(new TextBlock
        {
            Text = $"−{removed}", FontSize = 12.5, Foreground = Theme.DangerText, Margin = new Thickness(6, 0, 0, 0),
        });
        return summary;
    }

    private Border DiffDetails(ToolRow row, FileDiff diff)
    {
        var view = new Border
        {
            Background = Theme.Surface,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(22, 6, 0, 4),
            Child = DiffView(diff, InlineDiffLines),
        };
        if (row.FilePath is { } path)
        {
            view.Cursor = Cursors.Hand;
            view.ToolTip = "Открыть справа";
            view.MouseLeftButtonUp += (_, _) => ShowPreview(path, diff);
        }
        return view;
    }

    private FrameworkElement ImagePreview(string path)
    {
        var preview = new Border
        {
            Width = 320,
            Height = 320,
            CornerRadius = new CornerRadius(8),
            Margin = new Thickness(22, 8, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Theme.Surface,
            Cursor = Cursors.Hand,
            ToolTip = $"{path}\nЩелчок — посмотреть, правая кнопка — другие действия",
            ContextMenu = FileActions.BuildMenu(this, path),
        };
        AutomationProperties.SetName(preview, "Изображение " + Path.GetFileName(path));
        preview.MouseLeftButtonUp += (_, _) => ShowPicture(path);
        FileActions.EnableDragOut(preview, path);
        _ = ShowPreviewAsync(preview, path);
        return preview;
    }

    private static async Task ShowPreviewAsync(Border preview, string path)
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

    private static string? ResultTarget(string output)
    {
        int arrow = output.IndexOf(" → ", StringComparison.Ordinal);
        if (arrow < 0) return null;
        var target = output[(arrow + 3)..];
        int note = target.IndexOf(" (файлов:", StringComparison.Ordinal);
        if (note >= 0) target = target[..note];
        target = target.Trim();
        return target.Length > 0 && FileActions.Exists(target) ? target : null;
    }

    private void ToggleDetails(ToolRow row)
    {
        if (row.Output == null) return;
        if (row.Details == null)
        {
            var details = new StackPanel();
            row.Details = new RevealPanel { Child = details };
            if (row.Diff != null) details.Children.Add(DiffDetails(row, row.Diff));
            details.Children.Add(RawDetails(row.ArgumentsJson, DetailsOutput(row.Name, row.Output)));
            if (row.Name is "run_command" or "run_plugin" && CommandLog.IdOf(row.Output) is { } id)
            {
                var journal = new Button
                {
                    Style = Theme.Style("RowButton"), Content = "Открыть журнал",
                    Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(22, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Показать полный вывод в панели справа",
                };
                AutomationProperties.SetName(journal, "Открыть журнал команды");
                journal.Click += (_, _) => OpenCommandLog(id, OneLine(ArgumentOf(row.ArgumentsJson, "command") ?? Describe(row.Name).Label));
                details.Children.Add(journal);
            }
            row.Root.Children.Add(row.Details);
        }
        bool open = !row.Details.IsOpen;
        if (open) _autoScroll = false;
        row.Details.IsOpen = open;
        ShowExpandState(row, open);
    }

    private static void ShowExpandState(ToolRow row, bool open)
    {
        row.Expand.Content = open ? Glyphs.ChevronDown : Glyphs.ChevronRight;
        var hint = (open ? "Скрыть " : "Показать ") + "параметры и результат";
        row.Expand.ToolTip = hint;
        AutomationProperties.SetName(row.Expand, hint);
    }

    private void MarkUnfinishedToolRows()
    {
        foreach (var row in _chatView.ToolRows.Values.Where(r => !r.Finished))
        {
            CompleteToolRow(row, "Выполнение прервано.", isError: true);
        }
    }

    private static string OneLine(string? value) =>
        Shorten((value ?? "").Replace("\r", "").Replace("\n", " ⏎ ").Trim(), 70);

    private static string Shorten(string text, int max) => text.Length > max ? text[..max] + "…" : text;

    private static string KeepEnds(string text, int max) =>
        text.Length <= max ? text : text[..(max / 4)] + $"\n… пропущено {text.Length - max} символов …\n" + text[^(max - max / 4)..];

    private static string ShortName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var trimmed = path.Trim().TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private void RenderHistory(IReadOnlyList<JsonNode> history)
    {
        bool wasSuppressed = _suppressAnimation;
        _suppressAnimation = true;
        try
        {
            var results = new Dictionary<string, JsonObject>();
            foreach (var m in history.OfType<JsonObject>().Where(m => JsonUtil.Str(m["role"]) == "tool"))
                results[JsonUtil.Str(m["tool_call_id"]) ?? ""] = m;

            foreach (var m in history.OfType<JsonObject>())
            {
                var role = JsonUtil.Str(m["role"]);
                if (role == "user")
                {
                    MarkUnfinishedToolRows();
                    FinishProgress("Выполнение прервано");
                    AddUserBubble(AgentRunner.ExtractText(m["content"]) ?? "", Attachment.ListFrom(m));
                    if (AgentRunner.StopReasonOf(m) is { } userStop) AddNotice(userStop, warning: true, inProgress: false);
                    continue;
                }
                if (role != "assistant")
                {
                    if (AgentRunner.StopReasonOf(m) is { } reason) AddNotice(reason, warning: true, inProgress: false);
                    continue;
                }

                ResetProgressStep();
                OnModelProgress(AgentRunner.ProgressOf(m));
                var text = AgentRunner.ExtractText(m["content"])?.Trim() ?? "";
                var calls = m["tool_calls"] as JsonArray;
                if (calls is not { Count: > 0 })
                {
                    var elapsed = AgentRunner.ElapsedOf(m);
                    if (text.Length > 0 || elapsed != null) ShowFinalAnswer(text, elapsed);
                    if (AgentRunner.StopReasonOf(m) is { } assistantStop) AddNotice(assistantStop, warning: true, inProgress: false);
                    continue;
                }
                if (text.Length > 0) AddProgressItem(ProgressText(text, "Промежуточное сообщение"));
                foreach (var call in calls.OfType<JsonObject>())
                {
                    var id = JsonUtil.Str(call["id"]) ?? "";
                    results.TryGetValue(id, out var result);
                    if (result?[AgentRunner.MetaField] is JsonObject toolMeta &&
                        (JsonUtil.Bool(toolMeta["read_reused"]) == true || JsonUtil.Bool(toolMeta["action_reused"]) == true)) continue;
                    var function = call["function"] as JsonObject;
                    var row = AddToolRow(id, JsonUtil.Str(function?["name"]) ?? "?", JsonUtil.Str(function?["arguments"]) ?? "{}");
                    if (result == null) continue;
                    var output = JsonUtil.Str(result["content"]) ?? "";
                    var restricted = JsonUtil.Bool((result[AgentRunner.MetaField] as JsonObject)?["command_restricted"]) == true;
                    CompleteToolRow(row, output, restricted || ToolRegistry.IsErrorOutput(output),
                        row.Name is "run_command" or "run_plugin" && ToolRegistry.CommandFailed(output), AgentRunner.DiffOf(result),
                        AgentRunner.FileChangeOf(result), AgentRunner.FileChangeUndone(result));
                }
            }
            MarkUnfinishedToolRows();
            FinishProgress("Выполнение прервано");
            FlushTurnFiles();
        }
        finally
        {
            _suppressAnimation = wasSuppressed;
        }
    }

    private void CopyLastAnswer_Click(object sender, RoutedEventArgs e)
    {
        var last = _agent.History.OfType<JsonObject>()
            .Where(m => JsonUtil.Str(m["role"]) == "assistant")
            .Select(m => AgentRunner.ExtractText(m["content"]))
            .LastOrDefault(text => !string.IsNullOrWhiteSpace(text));
        if (last == null) ShowToast("В этом чате пока нет ответов");
        else if (TextClipboard.SetText(last.Trim())) ShowToast("Последний ответ скопирован", ToastKind.Success);
        else ShowToast("Буфер обмена занят другой программой — попробуйте ещё раз", ToastKind.Error);
    }

    private void AppendToChat(UIElement element)
    {
        (_chatView.Target ?? ChatPanel).Children.Add(element);
        if (_chatView.Target == null) EmptyChat.Visibility = Visibility.Collapsed;
        if (_suppressAnimation || !Theme.AnimationsEnabled) return;
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void ChatScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_changingHistory || !ReferenceEquals(e.OriginalSource, ChatScroll)) return;
        if (e.ExtentHeightChange != 0 && _autoScroll && _chatMotion == null)
            ChatScroll.ScrollToEnd();
        else if (e.VerticalChange != 0 && _chatMotion == null)
            _autoScroll = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 30;
        if (e.VerticalChange < 0 && e.ExtentHeightChange == 0 && _chatMotion == null) CheckOlderHistory();
        UpdateScrollDownButton();
    }

    private void ScrollDown_Click(object sender, RoutedEventArgs e)
    {
        ScrollToLatest();
    }

    private void ChatScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node != null && node != ChatScroll; node = ParentOf(node))
        {
            if (node is ScrollViewer { ScrollableHeight: > 0 } inner &&
                (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight))
                return;
        }
        ScrollChatBy(-e.Delta / 2.0);
        e.Handled = true;
    }

    private static DependencyObject? ParentOf(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static TextBox SelectableText(string text, Brush foreground, bool mono = false, double fontSize = 14) => new()
    {
        Text = text,
        IsReadOnly = true,
        IsReadOnlyCaretVisible = false,
        IsTabStop = false,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        Foreground = foreground,
        SelectionBrush = Theme.Accent,
        TextWrapping = TextWrapping.Wrap,
        Padding = new Thickness(0),
        FontSize = fontSize,
        FontFamily = mono ? Theme.Mono : Theme.Ui,
        FocusVisualStyle = null,
        ContextMenu = (ContextMenu)Application.Current.Resources["ReadOnlyTextMenu"],
    };

    private static FrameworkElement MakeSpinner(double size)
    {
        var rotate = new RotateTransform();
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = size,
            Height = size,
            Stroke = Theme.AccentText,
            StrokeThickness = 1.6,
            StrokeDashArray = new DoubleCollection { 7, 14 },
            StrokeDashCap = PenLineCap.Round,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = rotate,
        };
        if (Theme.AnimationsEnabled)
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        return ring;
    }

    private static void StopSpinner(FrameworkElement spinner)
    {
        if (spinner.RenderTransform is RotateTransform rotate) rotate.BeginAnimation(RotateTransform.AngleProperty, null);
        spinner.Visibility = Visibility.Collapsed;
    }

    private static string PrettyJson(string json)
    {
        try { return JsonNode.Parse(json)?.ToJsonString(JsonUtil.Pretty) ?? json; }
        catch { return json; }
    }
}
