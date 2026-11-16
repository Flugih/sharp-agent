using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;

namespace SharpAgent;

public partial class MainWindow
{
    const int MaxPreviewLines = 5000, InlineDiffLines = 40;
    const double ChatMinWidth = 460, PaneMinWidth = 300;

    static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".sln", ".py", ".js", ".jsx", ".ts", ".tsx", ".json", ".xml", ".html", ".css", ".sql",
        ".ps1", ".bat", ".cmd", ".sh", ".yaml", ".yml", ".ini", ".toml", ".java", ".kt", ".c", ".h", ".cpp", ".go", ".rs", ".php",
    };

    string? _previewPath, _picturePath;
    FileDiff? _previewDiff;
    double _previewWidth;
    CancellationTokenSource? _officeLoad;
    OfficePreview? _office;
    XpsDocument? _officeXps;

    void OpenInApp(string path)
    {
        if (Attachment.IsImagePath(path)) ShowPicture(path);
        else ShowPreview(path);
    }

    void ShowPicture(string path)
    {
        _picturePath = path;
        PictureName.Text = ChatLinks.DisplayName(path);
        PictureImage.Source = null;
        PictureOverlay.Visibility = Visibility.Visible;
        if (Theme.AnimationsEnabled) PictureOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        _ = LoadPictureAsync(path);
    }

    async Task LoadPictureAsync(string path)
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
            if (_picturePath == path) PictureImage.Source = picture;
        }
        catch
        {
            if (_picturePath != path) return;
            ClosePicture();
            ShowToast("Не удалось открыть изображение " + ChatLinks.DisplayName(path), ToastKind.Error);
        }
    }

    void ClosePicture()
    {
        PictureOverlay.Visibility = Visibility.Collapsed;
        PictureImage.Source = null;
        _picturePath = null;
    }

    void PictureOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) { if (ReferenceEquals(e.OriginalSource, PictureOverlay)) ClosePicture(); }

    void PictureClose_Click(object sender, RoutedEventArgs e) => ClosePicture();

    void PictureOpen_Click(object sender, RoutedEventArgs e) { if (_picturePath != null) FileActions.Open(this, _picturePath); }

    void PictureReveal_Click(object sender, RoutedEventArgs e) { if (_picturePath != null) FileActions.Reveal(_picturePath); }

    void ShowPreview(string path, FileDiff? diff = null, string? title = null, bool review = false)
    {
        _previewPath = path;
        _previewDiff = diff;
        PreviewTitle.Text = title ?? ChatLinks.DisplayName(path);
        PreviewPath.ToolTip = PreviewPath.Text = path;
        PreviewChangePicker.Visibility = VisibleIf(review);
        if (!review) PreviewChangePicker.ContextMenu = null;
        PreviewTabs.Visibility = VisibleIf(diff != null);
        var tab = diff != null ? PreviewDiffTab : PreviewFileTab;
        if (tab.IsChecked == true) RenderPreviewBody();
        else tab.IsChecked = true;
        if (IsShown(PreviewPane)) return;
        PreviewPane.Visibility = Visibility.Visible;
        ApplyPreviewLayout();
    }

    void RenderPreviewBody()
    {
        if (_previewPath is not { } path) return;
        CloseOfficePreview();
        var diff = PreviewDiffTab.IsChecked == true ? _previewDiff : null;
        if (diff == null && OfficePreview.IsOfficeFile(path)) _ = ShowOfficePreviewAsync(path);
        else PreviewBody.Content = diff != null
            ? new ScrollViewer { Content = DiffView(diff, int.MaxValue), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            : IsShown(PreviewChangePicker) && Attachment.IsImagePath(path) ? ImagePreview(path) : FileView(path);
    }

    void PreviewTab_Checked(object sender, RoutedEventArgs e) => RenderPreviewBody();

    void PreviewClose_Click(object sender, RoutedEventArgs e) => ClosePreview();

    void PreviewOpen_Click(object sender, RoutedEventArgs e) { if (_previewPath != null) FileActions.Open(this, _previewPath); }

    void PreviewReveal_Click(object sender, RoutedEventArgs e) { if (_previewPath != null) FileActions.Reveal(_previewPath); }

    void PreviewSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => _previewWidth = PreviewColumn.ActualWidth;

    void ClosePreview()
    {
        if (!IsShown(PreviewPane)) return;
        PreviewPane.Visibility = Visibility.Collapsed;
        PreviewBody.Content = null;
        PreviewChangePicker.Visibility = Visibility.Collapsed;
        PreviewChangePicker.ContextMenu = null;
        CloseOfficePreview();
        _previewPath = null;
        _previewDiff = null;
        ApplyPreviewLayout();
    }

    double MainWidth => WorkArea.ActualWidth;

    bool PreviewDocks => MainWidth >= ChatMinWidth + PaneMinWidth;

    void ApplyPreviewLayout()
    {
        bool open = IsShown(PreviewPane), docked = open && PreviewDocks, overlay = open && !docked;
        RightPane.Visibility = VisibleIf(open);
        if (_previewWidth == 0) _previewWidth = Math.Clamp(MainWidth * 0.45, 340, 760);
        PreviewColumn.MinWidth = docked ? PaneMinWidth : 0;
        PreviewColumn.MaxWidth = docked ? MainWidth - ChatMinWidth : 0;
        PreviewColumn.Width = new GridLength(docked ? Math.Clamp(_previewWidth, PaneMinWidth, PreviewColumn.MaxWidth) : 0);
        Grid.SetColumn(RightPane, overlay ? 0 : 1);
        Grid.SetColumnSpan(RightPane, overlay ? 2 : 1);
        PreviewSplitter.Visibility = VisibleIf(docked);
        UpdateLayoutWidths();
    }

    static FrameworkElement FileView(string path) =>
        !File.Exists(path) ? PreviewNote("Файла больше нет: его переместили или удалили.")
        : TextFileIO.TryRead(path, 2 * 1024 * 1024) is { } text ? CodeView(text)
        : PreviewNote("Этот файл здесь не показать. Откройте его в программе — кнопка вверху.");

    static FrameworkElement CodeView(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 1 && lines[^1].Length == 0) lines = lines[..^1];
        var lineNumbers = Enumerable.Range(1, lines.Length).Select(number => number.ToString()).ToArray();
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
                Text = string.Join("\n", lineNumbers), FontFamily = Theme.Mono, FontSize = 12.5, Foreground = Theme.TextMuted,
                TextAlignment = TextAlignment.Right, Padding = new Thickness(0, 0, 12, 24),
            },
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false, IsHitTestVisible = false,
        };
        var body = new TextBox
        {
            Text = string.Join("\n", lines), IsReadOnly = true, IsReadOnlyCaretVisible = false, BorderThickness = new Thickness(0),
            Background = Brushes.Transparent, Foreground = Theme.Text, SelectionBrush = Theme.Accent, FontFamily = Theme.Mono, FontSize = 12.5,
            Padding = new Thickness(0), FocusVisualStyle = null, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ContextMenu = (ContextMenu)Application.Current.Resources["ReadOnlyTextMenu"],
        };
        body.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) => gutter.ScrollToVerticalOffset(e.VerticalOffset)));
        Grid.SetColumn(body, 1);
        return new Grid { ColumnDefinitions = { new() { Width = GridLength.Auto }, new() }, Children = { gutter, body } };
    }

    Button FileCard(string path, FileDiff? diff, bool created)
    {
        bool changed = diff != null && !created;
        var title = CardTitle(path, keepExtension: false);
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var subtitle = extension.Length > 0 ? $"{KindOf(path)} · {extension}" : KindOf(path);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock { Text = subtitle, FontSize = 12.5, Foreground = Theme.TextMuted, Margin = new Thickness(0, 2, 0, 0) });
        var action = new Button
        {
            Content = "Открыть", Height = 30, Margin = new Thickness(12, 0, 0, 0), ToolTip = "Открыть в программе по умолчанию",
            Background = Brushes.Transparent,
        };
        AutomationProperties.SetName(action, $"Открыть {title} в программе по умолчанию");
        action.Click += (_, e) =>
        {
            e.Handled = true;
            FileActions.Open(this, path);
        };
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(action, 2);
        var card = new Button
        {
            Style = Theme.Style("FileCard"), Background = Theme.Surface2, BorderBrush = Theme.Get("ControlBorder"),
            Content = new Grid
            {
                ColumnDefinitions = { new() { Width = GridLength.Auto }, new(), new() { Width = GridLength.Auto } },
                Children = { CardPage(path, changed), texts, action },
            },
            ToolTip = $"{path}\nЩелчок — {(changed ? "показать изменения" : "посмотреть")}, правая кнопка — другие действия",
            ContextMenu = FileActions.BuildMenu(this, path),
        };
        AutomationProperties.SetName(card, $"{title}, {subtitle}");
        card.Click += (_, _) => { if (changed) ShowPreview(path, diff); else OpenInApp(path); };
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
        ".docx" or ".doc" or ".rtf" or ".odt" or ".pdf" => "Документ",
        ".xlsx" or ".xls" or ".csv" or ".tsv" or ".ods" => "Таблица",
        ".pptx" or ".ppt" or ".odp" => "Презентация",
        ".txt" or ".md" or ".log" => "Текст",
        var extension when CodeExtensions.Contains(extension) => "Код",
        _ when Attachment.IsImagePath(path) => "Изображение",
        _ => "Файл",
    };

    static Border CardPage(string path, bool changed)
    {
        bool picture = !changed && Attachment.IsImagePath(path);
        var page = new Border
        {
            Width = 34, Height = 42, CornerRadius = new CornerRadius(5), Margin = new Thickness(2, 0, 16, 0), Background = Theme.Surface2,
            BorderBrush = Theme.LineStrong, BorderThickness = new Thickness(1), RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-5),
            Child = new TextBlock
            {
                Text = changed || CodeExtensions.Contains(Path.GetExtension(path)) ? Glyphs.Code : picture ? Glyphs.Picture : Glyphs.Document,
                FontFamily = Theme.Icons, FontSize = 14, Foreground = Theme.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
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

    static TextBlock PreviewNote(string text) => new()
    {
        Text = text, FontSize = 13, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 8, 0),
    };

    static StackPanel DiffView(FileDiff diff, int limit)
    {
        var rows = new StackPanel();
        int oldLine = 0, newLine = 0, shown = 0;
        foreach (var line in diff.Unified.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var parts = line.Split(' ');
                oldLine = int.Parse(parts[1][1..].Split(',')[0]);
                newLine = int.Parse(parts[2][1..].Split(',')[0]);
                if (rows.Children.Count > 0) rows.Children.Add(DiffRow(' ', "", "", "⋯", Theme.TextMuted));
                continue;
            }
            if (line == TextDiff.Truncated || shown++ == limit)
            {
                rows.Children.Add(DiffRow(' ', "", "", "… изменения показаны не полностью", Theme.TextMuted));
                break;
            }
            char kind = line[0];
            rows.Children.Add(DiffRow(kind, kind == '+' ? "" : oldLine.ToString(), kind == '-' ? "" : newLine.ToString(), line[1..]));
            if (kind != '+') oldLine++;
            if (kind != '-') newLine++;
        }
        return rows;
    }

    static Border DiffRow(char kind, string oldNumber, string newNumber, string text, Brush? foreground = null) => new()
    {
        Background = kind switch { '+' => Theme.Get("DiffAdd"), '-' => Theme.Get("DiffRemove"), _ => Brushes.Transparent },
        Padding = new Thickness(4, 1, 8, 1),
        Child = new DockPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = $"{oldNumber,5} {newNumber,5} {(kind == ' ' ? ' ' : kind == '-' ? '−' : '+')}", FontFamily = Theme.Mono, FontSize = 12.5,
                    Foreground = kind switch { '+' => Theme.Success, '-' => Theme.DangerText, _ => Theme.TextMuted }, Margin = new Thickness(0, 0, 10, 0),
                },
                new TextBlock
                {
                    Text = text, FontFamily = Theme.Mono, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                    Foreground = foreground ?? (kind == ' ' ? Theme.TextSoft : Theme.Text),
                },
            },
        },
    };

    async Task ShowOfficePreviewAsync(string path)
    {
        var load = _officeLoad = new CancellationTokenSource();
        PreviewBody.Content = PreviewNote("Готовлю визуальный предпросмотр…");
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
                PreviewBody.Content = ExternalOfficeView(path, ex is IOException ? ex.Message : "Не удалось отобразить документ. Откройте его во внешней программе.");
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
        var previous = Theme.IconButton("\uE76B", "Предыдущая страница");
        var next = Theme.IconButton(Glyphs.ChevronRight, "Следующая страница");
        var counter = new TextBlock { Foreground = Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center, Width = 66, TextAlignment = TextAlignment.Center };
        var sheet = new Button
        {
            Style = Theme.Style("ModelPicker"), ToolTip = "Выбрать лист", HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 215,
            Margin = new Thickness(0, 0, 8, 0),
        };
        AutomationProperties.SetName(sheet, "Выбрать лист");
        var group = preview.Groups[0];
        bool fit = group.Name.Length == 0;
        string ZoomHint() => fit ? "Показать в исходном размере" : "Подогнать под панель";
        var zoom = Theme.IconButton(Glyphs.Search, ZoomHint());
        var navigation = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { zoom, previous, counter, next },
        };
        DockPanel.SetDock(navigation, Dock.Right);
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 4, 8), Children = { navigation } };
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
                    content.Content = ExternalOfficeView(path, "У этого листа нет страниц для показа.");
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
                content.Content = ExternalOfficeView(path, "Не удалось отобразить страницу. Откройте документ во внешней программе.");
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
                content.Content = ExternalOfficeView(path, "Не удалось прочитать страницы. Откройте документ во внешней программе.");
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
            Content = "Открыть во внешней программе", ToolTip = "Открыть документ в приложении Windows",
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 9, 0, 0),
        };
        AutomationProperties.SetName(open, "Открыть во внешней программе");
        open.Click += (_, _) => FileActions.Open(this, path);
        return new StackPanel { Children = { PreviewNote(message), open } };
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
