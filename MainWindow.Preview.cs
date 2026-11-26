using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    const int MaxPreviewLines = 5000, MaxHighlightedPreviewChars = 120_000, InlineDiffLines = 40;
    const double PreviewLineHeight = 19, PreviewFontSize = 12.5;
    const double ChatMinWidth = 460, PaneMinWidth = 300;

    static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".sln", ".py", ".js", ".jsx", ".ts", ".tsx", ".json", ".xml", ".html", ".css", ".sql",
        ".ps1", ".bat", ".cmd", ".sh", ".yaml", ".yml", ".ini", ".toml", ".java", ".kt", ".c", ".h", ".cpp", ".go", ".rs", ".php",
    };

    string? _previewPath;
    string[] _pictures = [];
    int _pictureIndex, _pictureVersion;
    IInputElement? _pictureFocus;
    string? PicturePath => _pictures.ElementAtOrDefault(_pictureIndex);
    FileDiff? _previewDiff;
    ProducedFile[]? _previewChanges;
    int _previewSelected;
    double _previewWidth;
    CancellationTokenSource? _officeLoad;
    OfficePreview? _office;
    XpsDocument? _officeXps;

    void OpenInApp(string path)
    {
        _compactChat?.Close();
        foreach (var (_, files) in _chatView.ChangeCards.AsEnumerable().Reverse())
        {
            int index = Array.FindIndex(files, file => !file.Undone && file.Diff != null && PathHelper.SamePath(file.Path, path));
            if (index < 0) continue;
            ReviewChanges(files, index);
            return;
        }
        if (Attachment.IsImagePath(path)) ShowPicture(path);
        else ShowPreview(path);
    }

    static string[] AttachmentPictures(IReadOnlyList<Attachment> attachments) =>
        [.. attachments.Where(file => file.Kind == AttachmentKind.Image).Select(file => file.Path)];

    void ShowPicture(string path, IReadOnlyList<string>? pictures = null)
    {
        _compactChat?.Close();
        _pictures = pictures?.Where(file => PathHelper.SamePath(file, path) || File.Exists(file))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [path];
        _pictureIndex = Array.FindIndex(_pictures, file => PathHelper.SamePath(file, path));
        if (_pictureIndex < 0)
        {
            _pictures = [path];
            _pictureIndex = 0;
        }
        if (!IsShown(PictureOverlay)) _pictureFocus = Keyboard.FocusedElement;
        PictureOverlay.Visibility = Visibility.Visible;
        PictureOverlay.Focus();
        UpdatePicture();
    }

    void MovePicture(int direction)
    {
        int next = _pictureIndex + direction;
        if ((uint)next >= (uint)_pictures.Length) return;
        _pictureIndex = next;
        UpdatePicture();
    }

    void UpdatePicture()
    {
        var path = _pictures[_pictureIndex];
        PictureName.Text = ChatLinks.DisplayName(path);
        PictureImage.Source = null;
        PicturePrevious.Visibility = PictureNext.Visibility = VisibleIf(_pictures.Length > 1);
        PicturePrevious.IsEnabled = _pictureIndex > 0;
        PictureNext.IsEnabled = _pictureIndex + 1 < _pictures.Length;
        _ = LoadPictureAsync(path, ++_pictureVersion);
    }

    async Task LoadPictureAsync(string path, int version)
    {
        try
        {
            var picture = await Task.Run(() =>
            {
                int width;
                using (var stream = File.OpenRead(path))
                    width = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
                return DecodeImageAsync(path, Math.Min(width, 2400));
            });
            if (_pictureVersion == version) PictureImage.Source = picture;
        }
        catch
        {
            if (_pictureVersion != version) return;
            if (_pictures.Length == 1) ClosePicture();
            ShowToast(Tr("Не удалось открыть изображение ", "Could not open image ") + ChatLinks.DisplayName(path), ToastKind.Error);
        }
    }

    void ClosePicture()
    {
        PictureOverlay.Visibility = Visibility.Collapsed;
        PictureImage.Source = null;
        _pictures = [];
        _pictureVersion++;
        if (_pictureFocus is UIElement { IsVisible: true } element) element.Focus();
        _pictureFocus = null;
    }

    void PictureOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { if (ReferenceEquals(e.OriginalSource, PictureOverlay)) ClosePicture(); }

    void PictureClose_Click(object sender, RoutedEventArgs e) => ClosePicture();

    void PicturePrevious_Click(object sender, RoutedEventArgs e) => MovePicture(-1);

    void PictureNext_Click(object sender, RoutedEventArgs e) => MovePicture(1);

    void PictureOpen_Click(object sender, RoutedEventArgs e) { if (PicturePath is { } path) FileActions.Open(this, path); }

    void PictureReveal_Click(object sender, RoutedEventArgs e) { if (PicturePath is { } path) FileActions.Reveal(path); }

    void ShowPreview(string path, FileDiff? diff = null, string? title = null, ProducedFile[]? changes = null, int selected = 0, int searchLine = 0)
    {
        HideBrowser();
        CloseSubagents(focusChat: false);
        _previewPath = path;
        _previewDiff = diff;
        _previewChanges = changes;
        _previewSelected = selected;
        bool changesView = diff != null || changes != null;
        PreviewTitle.Text = changesView ? Tr("Изменения", "Changes") : title ?? ChatLinks.DisplayName(path);
        PreviewPath.ToolTip = PreviewPath.Text = path;
        PreviewPath.Visibility = VisibleIf(!changesView);
        RenderPreviewBody(searchLine);
        if (IsShown(PreviewPane)) return;
        PreviewPane.Visibility = Visibility.Visible;
        ApplyPreviewLayout();
    }

    void RenderPreviewBody(int searchLine = 0)
    {
        if (_previewPath is not { } path) return;
        CloseOfficePreview();
        if (_previewChanges != null || _previewDiff != null) ShowDiffPreview(path);
        else if (OfficePreview.IsOfficeFile(path)) _ = ShowOfficePreviewAsync(path);
        else PreviewBody.Content = Attachment.IsImagePath(path) ? ImagePreview(path) : FileView(path, searchLine);
    }

    void PreviewClose_Click(object sender, RoutedEventArgs e) => ClosePreview();

    void PreviewOpen_Click(object sender, RoutedEventArgs e) { if (_previewPath != null) FileActions.Open(this, _previewPath); }

    void PreviewReveal_Click(object sender, RoutedEventArgs e) { if (_previewPath != null) FileActions.Reveal(_previewPath); }

    void PreviewSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!e.Canceled) SavePaneWidth();
    }

    void PreviewSplitter_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right) SavePaneWidth();
    }

    void SavePaneWidth()
    {
        double width = PreviewColumn.Width.IsAbsolute ? PreviewColumn.Width.Value : PreviewColumn.ActualWidth;
        if (IsShown(SubagentsWorkspace) && !_subagentsExpanded)
        {
            _state.SubagentsWidth = width;
            _state.Save();
        }
        else if (IsShown(BrowserPane)) _browserWidth = width;
        else _previewWidth = width;
    }

    void ClosePreview()
    {
        if (!IsShown(PreviewPane)) return;
        PreviewPane.Visibility = Visibility.Collapsed;
        PreviewBody.Content = null;
        CloseOfficePreview();
        _previewPath = null;
        _previewDiff = null;
        _previewChanges = null;
        ApplyPreviewLayout();
    }

    double MainWidth => WorkArea.ActualWidth;

    bool PreviewDocks => MainWidth >= ChatMinWidth + PaneMinWidth;

    void ApplyPreviewLayout()
    {
        bool browser = IsShown(BrowserPane), subagents = IsShown(SubagentsWorkspace);
        bool side = browser && !_browserExpanded || subagents && !_subagentsExpanded;
        bool open = !browser && !subagents && IsShown(PreviewPane), previewDocked = open && PreviewDocks, overlay = open && !previewDocked;
        bool docked = side || previewDocked;
        RightPane.Visibility = VisibleIf(open);
        if (_previewWidth == 0) _previewWidth = Math.Clamp(MainWidth * 0.45, 340, 760);
        var width = browser ? (_browserWidth > 0 ? _browserWidth : MainWidth * 0.46)
            : side && _state.SubagentsWidth is { } saved && double.IsFinite(saved) ? saved : side ? 360 : _previewWidth;
        PreviewColumn.MinWidth = side ? Math.Min(PaneMinWidth, MainWidth * 0.37) : previewDocked ? PaneMinWidth : 0;
        PreviewColumn.MaxWidth = side ? MainWidth - Math.Min(ChatMinWidth, MainWidth * 0.55) : previewDocked ? MainWidth - ChatMinWidth : 0;
        PreviewColumn.Width = new GridLength(docked ? Math.Clamp(width, PreviewColumn.MinWidth, PreviewColumn.MaxWidth) : 0);
        Grid.SetColumn(RightPane, overlay ? 0 : 1);
        Grid.SetColumnSpan(RightPane, overlay ? 2 : 1);
        Grid.SetColumn(SubagentsWorkspace, side ? 1 : 0);
        Grid.SetColumnSpan(SubagentsWorkspace, side ? 1 : 2);
        Grid.SetColumn(BrowserPane, _browserExpanded ? 0 : 1);
        Grid.SetColumnSpan(BrowserPane, _browserExpanded ? 2 : 1);
        SubagentsWorkspace.BorderThickness = new Thickness(side ? 1 : 0, 0, 0, 0);
        PreviewSplitter.Visibility = VisibleIf(docked);
        UpdateLayoutWidths();
    }

    static FrameworkElement FileView(string path, int searchLine) =>
        !File.Exists(path) ? EmptyState(Tr("Файла больше нет: его переместили или удалили.", "The file was moved or deleted."))
        : TextFileIO.TryRead(path, 2 * 1024 * 1024) is { } text ? CodeView(text, Path.GetExtension(path), searchLine)
        : EmptyState(Tr("Этот файл здесь не показать. Откройте его в программе — кнопка вверху.", "Preview is unavailable for this file. Use the button above to open it in its app."));

    static FrameworkElement CodeView(string text, string extension, int searchLine)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        int first = 0;
        if (lines.Length > MaxPreviewLines && searchLine > 0)
        {
            first = Math.Clamp(searchLine - 1 - MaxPreviewLines / 2, 0, lines.Length - MaxPreviewLines);
            lines = lines[first..(first + MaxPreviewLines)];
        }
        var lineNumbers = Enumerable.Range(first + 1, lines.Length).Select(number => number.ToString()).ToArray();
        if (lines.Length > MaxPreviewLines)
        {
            int half = MaxPreviewLines / 2;
            lines = [.. lines[..half], $"… пропущено строк: {lines.Length - 2 * half} — весь текст: «Открыть в программе» вверху …", .. lines[^half..]];
            lineNumbers = [.. lineNumbers[..half], "", .. lineNumbers[^half..]];
        }
        var gutter = new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = string.Join("\n", lineNumbers), FontFamily = Theme.Mono, FontSize = 12, LineHeight = PreviewLineHeight,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight, TextAlignment = TextAlignment.Right, Padding = new Thickness(0, 0, 12, 24),
            }.Paint(ForegroundProperty, "TextMuted"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false, IsHitTestVisible = false,
        };
        var content = string.Join('\n', lines);
        TextBoxBase body = content.Length <= MaxHighlightedPreviewChars && CodeExtensions.Contains(extension)
            ? HighlightedCodeView(content, extension.ToLowerInvariant(), lines) : new TextBox { Text = content };
        body.IsReadOnly = true;
        body.BorderThickness = new(0);
        body.Background = Brushes.Transparent;
        body.FontFamily = Theme.Mono;
        body.FontSize = PreviewFontSize;
        body.Padding = new(0);
        body.FocusVisualStyle = null;
        body.HorizontalScrollBarVisibility = body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        body.ContextMenu = (ContextMenu)Application.Current.Resources["ReadOnlyTextMenu"];
        body.Paint(ForegroundProperty, "Text").Paint(TextBoxBase.SelectionBrushProperty, "Accent");
        TextBlock.SetLineHeight(body, PreviewLineHeight);
        TextBlock.SetLineStackingStrategy(body, LineStackingStrategy.BlockLineHeight);
        body.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => gutter.ScrollToVerticalOffset(e.VerticalOffset)));
        Grid.SetColumn(body, 1);
        return new Grid { ColumnDefinitions = { new() { Width = GridLength.Auto }, new() }, Children = { gutter, body } };
    }

    static TextBoxBase HighlightedCodeView(string content, string extension, string[] lines)
    {
        var longest = lines.MaxBy(line => line.Length + 7 * line.AsSpan().Count('\t'))!;
        var width = new FormattedText(longest.Replace("\t", "        "), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Theme.Mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), PreviewFontSize, Brushes.Transparent,
            VisualTreeHelper.GetDpi(Application.Current.MainWindow).PixelsPerDip).WidthIncludingTrailingWhitespace;
        if (width > 100_000) return new TextBox { Text = content };
        var paragraph = new Paragraph { Margin = new(0) };
        CodeSyntax.Write(paragraph.Inlines, content, extension, MaxHighlightedPreviewChars);
        var document = new FlowDocument(paragraph)
        {
            PagePadding = new(0), PageWidth = width + 4, LineHeight = PreviewLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight, TextAlignment = TextAlignment.Left,
        };
        return new RichTextBox(document) { AutoWordSelection = false };
    }

    Button FileCard(string path, FileDiff? diff, bool created)
    {
        bool changed = diff != null && !created;
        var title = CardTitle(path, keepExtension: false);
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var subtitle = extension.Length > 0 ? $"{KindOf(path)} · {extension}" : KindOf(path);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis }.Paint(ForegroundProperty, "TextDim"));
        var action = new Button { Content = "Открыть", Margin = new Thickness(12, 0, 0, 0), ToolTip = Tr("Открыть в программе по умолчанию", "Open in default app") };
        AutomationProperties.SetName(action, Tr($"Открыть {title} в программе по умолчанию", $"Open {title} in the default app"));
        action.Click += (_, e) =>
        {
            e.Handled = true;
            FileActions.Open(ActiveChatWindow, path);
        };
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(action, 2);
        var card = new Button
        {
            Style = Theme.Style("FileCard"),
            Content = new Grid
            {
                ColumnDefinitions = { new() { Width = GridLength.Auto }, new(), new() { Width = GridLength.Auto } },
                Children = { CardPage(path, changed), texts, action },
            },
            ToolTip = $"{path}\nЩелчок — {(changed ? "показать изменения" : "посмотреть")}, правая кнопка — другие действия",
            ContextMenu = FileActions.BuildMenu(this, path),
        };
        AutomationProperties.SetName(card, $"{title}, {subtitle}");
        card.Click += (_, _) =>
        {
            _compactChat?.Close();
            ShowPreview(path, diff);
        };
        FileActions.EnableDragOut(card, path);
        return card;
    }

    string CardTitle(string path, bool keepExtension)
    {
        var workDir = _toolContext.WorkingDirectory;
        var title = PathHelper.IsInside(path, workDir) && !PathHelper.SamePath(path, workDir) ? Path.GetRelativePath(workDir, path) : Path.GetFileName(path);
        if (keepExtension) return title;
        var bare = Path.ChangeExtension(title, null);
        return Path.GetFileName(bare).Length > 0 ? bare : title;
    }

    static string KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" or ".doc" or ".rtf" or ".odt" or ".pdf" => Tr("Документ", "Document"),
        ".xlsx" or ".xls" or ".csv" or ".tsv" or ".ods" => Tr("Таблица", "Spreadsheet"),
        ".pptx" or ".ppt" or ".odp" => Tr("Презентация", "Presentation"),
        ".txt" or ".md" or ".log" => Tr("Текст", "Text"),
        var extension when CodeExtensions.Contains(extension) => Tr("Код", "Code"),
        _ when Attachment.IsImagePath(path) => Tr("Изображение", "Image"),
        _ => Tr("Файл", "File"),
    };

    static Border CardPage(string path, bool changed)
    {
        bool picture = !changed && Attachment.IsImagePath(path);
        var page = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = changed || CodeExtensions.Contains(Path.GetExtension(path)) ? Glyphs.Code : picture ? Glyphs.Picture : Glyphs.Document,
                FontFamily = Theme.Icons, FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            }.Paint(ForegroundProperty, "TextSoft"),
        }.Paint(BackgroundProperty, "Bg");
        if (picture) _ = ShowPageImageAsync(page, path);
        return page;
    }

    static async Task ShowPageImageAsync(Border page, string path)
    {
        try
        {
            page.Background = new ImageBrush(await DecodeImageAsync(path, 96)) { Stretch = Stretch.UniformToFill };
            page.Child = null;
        }
        catch { }
    }

    static TextBlock EmptyGlyph(string glyph)
    {
        var block = GlyphText(glyph, 28, "TextMuted");
        block.HorizontalAlignment = HorizontalAlignment.Center;
        return block;
    }

    static StackPanel EmptyState(string title, UIElement? action = null)
    {
        var state = new StackPanel
        {
            MaxWidth = 360, Margin = new(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                EmptyGlyph(Glyphs.Document),
                new TextBlock
                {
                    Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new(0, 12, 0, 0),
                }.Paint(ForegroundProperty, "Text"),
            },
        };
        if (action != null) state.Children.Add(action);
        return state;
    }


    async Task ShowOfficePreviewAsync(string path)
    {
        var load = _officeLoad = new CancellationTokenSource();
        PreviewBody.Content = EmptyState(Tr("Готовлю визуальный предпросмотр…", "Preparing preview…"));
        try
        {
            var preview = await OfficePreview.OpenAsync(path, load.Token);
            if (!ReferenceEquals(_officeLoad, load))
            {
                preview.Dispose();
                return;
            }
            _office = preview;
            PreviewBody.Content = OfficePagesView(preview, path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ReferenceEquals(_officeLoad, load))
                PreviewBody.Content = ExternalOfficeView(path, ex is IOException ? ex.Message : Tr("Не удалось отобразить документ. Откройте его во внешней программе.", "Could not display the document. Open it in an external app."));
        }
        finally
        {
            if (ReferenceEquals(_officeLoad, load)) _officeLoad = null;
            load.Dispose();
        }
    }

    FrameworkElement OfficePagesView(OfficePreview preview, string path)
    {
        var content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var previous = Theme.IconButton("\uE76B", Tr("Предыдущая страница", "Previous page"));
        var next = Theme.IconButton(Glyphs.ChevronRight, Tr("Следующая страница", "Next page"));
        var counter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Width = 66, TextAlignment = TextAlignment.Center }.Paint(ForegroundProperty, "TextMuted");
        var sheet = new Button
        {
            Style = Theme.Style("DropdownButton"), ToolTip = Tr("Выбрать лист", "Choose sheet"), HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 215,
            Margin = new Thickness(0, 0, 8, 0),
        };
        AutomationProperties.SetName(sheet, Tr("Выбрать лист", "Choose sheet"));
        var group = preview.Groups[0];
        bool fit = group.Name.Length == 0;
        string ZoomHint() => fit ? Tr("Показать в исходном размере", "Show actual size") : Tr("Подогнать под панель", "Fit to panel");
        var zoom = new Button { Style = Theme.Style("GhostButton"), Content = GlyphText(Glyphs.Search, 16, null), ToolTip = ZoomHint() };
        AutomationProperties.SetName(zoom, ZoomHint());
        var navigation = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { zoom, previous, counter, next },
        };
        DockPanel.SetDock(navigation, Dock.Right);
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 8, 8), Children = { navigation } };
        if (!fit) toolbar.Children.Add(sheet);
        DockPanel.SetDock(toolbar, Dock.Top);
        var root = new DockPanel { Children = { toolbar, content } };
        System.Windows.Documents.DocumentPaginator? paginator = null;
        int page = 0, count = 0;
        void ShowPage()
        {
            try
            {
                previous.IsEnabled = page > 0;
                next.IsEnabled = page + 1 < count;
                counter.Text = count > 0 ? $"{page + 1} / {count}" : "";
                if (count == 0)
                {
                    content.Content = ExternalOfficeView(path, Tr("У этого листа нет страниц для показа.", "This sheet has no pages to display."));
                    return;
                }
                FrameworkElement visual;
                if (paginator == null)
                {
                    var bitmap = LoadFrozenBitmap(group.Files[page]);
                    visual = new Image { Source = bitmap, Width = bitmap.Width, Height = bitmap.Height };
                }
                else
                {
                    using var first = paginator.GetPage(page);
                    var size = first.Size;
                    visual = new DocumentPageView { DocumentPaginator = paginator, PageNumber = page, Width = size.Width, Height = size.Height };
                }
                content.Content = fit
                    ? new Viewbox
                    {
                        Child = visual, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
                        VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
                    }
                    : new ScrollViewer
                    {
                        Content = visual, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
                    };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                content.Content = ExternalOfficeView(path, Tr("Не удалось отобразить страницу. Откройте документ во внешней программе.", "Could not display this page. Open the document in an external app."));
            }
        }
        void SelectGroup(OfficePages selected)
        {
            content.Content = null;
            _officeXps?.Close();
            _officeXps = null;
            paginator = null;
            group = selected;
            page = 0;
            sheet.Content = selected.Name;
            count = selected.Files.Length;
            try
            {
                if (count > 0 && Path.GetExtension(selected.Files[0]) == ".xps")
                {
                    _officeXps = new XpsDocument(selected.Files[0], FileAccess.Read);
                    paginator = _officeXps.GetFixedDocumentSequence().DocumentPaginator;
                    if (!paginator.IsPageCountValid) paginator.ComputePageCount();
                    count = paginator.PageCount;
                }
                ShowPage();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                next.IsEnabled = previous.IsEnabled = false;
                content.Content = ExternalOfficeView(path, Tr("Не удалось прочитать страницы. Откройте документ во внешней программе.", "Could not read the pages. Open the document in an external app."));
            }
        }
        previous.Click += (_, _) => { if (page > 0) { page--; ShowPage(); } };
        next.Click += (_, _) => { if (page + 1 < count) { page++; ShowPage(); } };
        zoom.Click += (_, _) =>
        {
            fit = !fit;
            var hint = ZoomHint();
            zoom.ToolTip = hint;
            AutomationProperties.SetName(zoom, hint);
            ShowPage();
        };
        sheet.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = sheet, MaxHeight = 280 };
            foreach (var item in preview.Groups)
                menu.Items.Add(FileActions.Item(item.Name.Replace("_", "__"), item == group ? Glyphs.Check : null, () => SelectGroup(item)));
            menu.IsOpen = true;
        };
        SelectGroup(group);
        return root;
    }

    FrameworkElement ExternalOfficeView(string path, string message)
    {
        var open = new Button
        {
            Content = Tr("Открыть во внешней программе", "Open in external app"),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0),
        };
        AutomationProperties.SetName(open, Tr("Открыть во внешней программе", "Open in external app"));
        open.Click += (_, _) => FileActions.Open(this, path);
        return EmptyState(message, open);
    }

    void CloseOfficePreview()
    {
        _officeLoad?.Cancel();
        _officeLoad = null;
        if (_office == null) return;
        PreviewBody.Content = null;
        _officeXps?.Close();
        _officeXps = null;
        _office.Dispose();
        _office = null;
    }
}
