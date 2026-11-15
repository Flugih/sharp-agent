using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SharpAgent.Agent;
using SharpAgent.Tools;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private const int MaxPreviewLines = 5000, MaxPictureWidth = 2400, InlineDiffLines = 40;
    private const long MaxPreviewBytes = 2 * 1024 * 1024;
    private const double ChatMinWidth = 460, PaneMinWidth = 300;

    private string? _previewPath;
    private FileDiff? _previewDiff;
    private string? _picturePath;
    private double _previewWidth;

    private void OpenInApp(string path)
    {
        if (Attachment.IsImagePath(path)) ShowPicture(path);
        else ShowPreview(path);
    }

    private void ShowPicture(string path)
    {
        _picturePath = path;
        PictureName.Text = ChatLinks.DisplayName(path);
        PictureImage.Source = null;
        PictureOverlay.Visibility = Visibility.Visible;
        if (Theme.AnimationsEnabled)
            PictureOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        _ = LoadPictureAsync(path);
    }

    private async Task LoadPictureAsync(string path)
    {
        try
        {
            var picture = await Task.Run(() =>
            {
                int width;
                using (var stream = File.OpenRead(path))
                    width = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).PixelWidth;
                return DecodeImageAsync(path, Math.Min(width, MaxPictureWidth));
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

    private void ClosePicture()
    {
        PictureOverlay.Visibility = Visibility.Collapsed;
        PictureImage.Source = null;
        _picturePath = null;
    }

    private void PictureOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, PictureOverlay)) ClosePicture();
    }

    private void PictureClose_Click(object sender, RoutedEventArgs e) => ClosePicture();

    private void PictureOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_picturePath != null) FileActions.Open(this, _picturePath);
    }

    private void PictureReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_picturePath != null) FileActions.Reveal(_picturePath);
    }

    private void ShowPreview(string path, FileDiff? diff = null, string? title = null, bool review = false)
    {
        _previewPath = path;
        _previewDiff = diff;
        PreviewTitle.Text = title ?? ChatLinks.DisplayName(path);
        PreviewPath.Text = path;
        PreviewPath.ToolTip = path;
        PreviewChangePicker.Visibility = review ? Visibility.Visible : Visibility.Collapsed;
        if (!review) PreviewChangePicker.ContextMenu = null;
        PreviewTabs.Visibility = diff != null ? Visibility.Visible : Visibility.Collapsed;
        var tab = diff != null ? PreviewDiffTab : PreviewFileTab;
        if (tab.IsChecked == true) RenderPreviewBody();
        else tab.IsChecked = true;
        if (PreviewPane.Visibility == Visibility.Visible) return;
        PreviewPane.Visibility = Visibility.Visible;
        ApplyPreviewLayout();
    }

    private void RenderPreviewBody()
    {
        if (_previewPath == null) return;
        CloseOfficePreview();
        if ((PreviewDiffTab.IsChecked != true || _previewDiff == null) && OfficePreview.IsOfficeFile(_previewPath))
        {
            _ = ShowOfficePreviewAsync(_previewPath);
            return;
        }
        PreviewBody.Content = PreviewDiffTab.IsChecked == true && _previewDiff != null
            ? new ScrollViewer { Content = DiffView(_previewDiff, int.MaxValue), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            : PreviewChangePicker.Visibility == Visibility.Visible && Attachment.IsImagePath(_previewPath)
                ? ImagePreview(_previewPath) : FileView(_previewPath);
    }

    private void PreviewTab_Checked(object sender, RoutedEventArgs e) => RenderPreviewBody();

    private void PreviewClose_Click(object sender, RoutedEventArgs e) => ClosePreview();

    private void PreviewOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_previewPath != null) FileActions.Open(this, _previewPath);
    }

    private void PreviewReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_previewPath != null) FileActions.Reveal(_previewPath);
    }

    private void PreviewSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => _previewWidth = PreviewColumn.ActualWidth;

    private void ClosePreview()
    {
        if (PreviewPane.Visibility != Visibility.Visible) return;
        PreviewPane.Visibility = Visibility.Collapsed;
        PreviewBody.Content = null;
        PreviewChangePicker.Visibility = Visibility.Collapsed;
        PreviewChangePicker.ContextMenu = null;
        CloseOfficePreview();
        _previewPath = null;
        _previewDiff = null;
        ApplyPreviewLayout();
    }

    private double MainWidth => WorkArea.ActualWidth;

    private bool PreviewDocks => MainWidth >= ChatMinWidth + PaneMinWidth;

    private void ApplyPreviewLayout()
    {
        bool open = PreviewPane.Visibility == Visibility.Visible, docked = open && PreviewDocks;
        RightPane.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (_previewWidth == 0) _previewWidth = Math.Clamp(MainWidth * 0.45, 340, 760);
        PreviewColumn.MinWidth = docked ? PaneMinWidth : 0;
        PreviewColumn.MaxWidth = docked ? MainWidth - ChatMinWidth : 0;
        PreviewColumn.Width = new GridLength(docked ? Math.Clamp(_previewWidth, PaneMinWidth, PreviewColumn.MaxWidth) : 0);
        Grid.SetColumn(RightPane, open && !docked ? 0 : 1);
        Grid.SetColumnSpan(RightPane, open && !docked ? 2 : 1);
        PreviewSplitter.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayoutWidths();
    }

    private static FrameworkElement FileView(string path)
    {
        if (!File.Exists(path)) return PreviewNote("Файла больше нет: его переместили или удалили.");
        string? text;
        try
        {
            text = TextFileIO.TryRead(path, MaxPreviewBytes);
        }
        catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
        {
            return PreviewNote(ex.Message);
        }
        return text == null
            ? PreviewNote("Этот файл здесь не показать. Откройте его в программе — кнопка вверху.")
            : CodeView(text);
    }

    private static FrameworkElement CodeView(string text)
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

        var numbers = new TextBlock
        {
            Text = string.Join("\n", lineNumbers),
            FontFamily = Theme.Mono, FontSize = 12.5, Foreground = Theme.TextMuted, TextAlignment = TextAlignment.Right,
            Padding = new Thickness(0, 0, 12, 24),
        };
        var gutter = new ScrollViewer
        {
            Content = numbers, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Focusable = false, IsHitTestVisible = false,
        };
        var body = new TextBox
        {
            Text = string.Join("\n", lines),
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = Theme.Text,
            SelectionBrush = Theme.Accent,
            FontFamily = Theme.Mono,
            FontSize = 12.5,
            Padding = new Thickness(0),
            FocusVisualStyle = null,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ContextMenu = (ContextMenu)Application.Current.Resources["ReadOnlyTextMenu"],
        };
        body.AddHandler(ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler((_, e) => gutter.ScrollToVerticalOffset(e.VerticalOffset)));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(body, 1);
        grid.Children.Add(gutter);
        grid.Children.Add(body);
        return grid;
    }

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".sln", ".py", ".js", ".jsx", ".ts", ".tsx", ".json", ".xml", ".html", ".css", ".sql",
        ".ps1", ".bat", ".cmd", ".sh", ".yaml", ".yml", ".ini", ".toml", ".java", ".kt", ".c", ".h", ".cpp", ".go", ".rs", ".php",
    };

    private Button FileCard(string path, FileDiff? diff, bool created)
    {
        bool changed = diff != null && !created;
        var title = CardTitle(path, keepExtension: false);
        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var kind = KindOf(path);
        var subtitle = extension.Length > 0 ? $"{kind} · {extension}" : kind;

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(new TextBlock
        {
            Text = subtitle, FontSize = 12.5, Foreground = Theme.TextMuted, Margin = new Thickness(0, 2, 0, 0),
        });

        var action = new Button { Content = "Открыть", Height = 30, Margin = new Thickness(12, 0, 0, 0),
            ToolTip = "Открыть в программе по умолчанию", Background = Brushes.Transparent };
        AutomationProperties.SetName(action, $"Открыть {title} в программе по умолчанию");
        action.Click += (_, e) =>
        {
            e.Handled = true;
            FileActions.Open(this, path);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(action, 2);
        grid.Children.Add(CardPage(path, changed));
        grid.Children.Add(texts);
        grid.Children.Add(action);

        var card = new Button
        {
            Style = Theme.Style("FileCard"),
            Background = Theme.Surface2,
            BorderBrush = Theme.Get("ControlBorder"),
            Content = grid,
            ToolTip = $"{path}\nЩелчок — {(changed ? "показать изменения" : "посмотреть")}, правая кнопка — другие действия",
            ContextMenu = FileActions.BuildMenu(this, path),
        };
        AutomationProperties.SetName(card, $"{title}, {subtitle}");
        card.Click += (_, _) =>
        {
            if (changed) ShowPreview(path, diff);
            else OpenInApp(path);
        };
        FileActions.EnableDragOut(card, path);
        return card;
    }

    private string CardTitle(string path, bool keepExtension)
    {
        var workDir = _toolContext.WorkingDirectory;
        var title = PathHelper.IsInside(path, workDir) && !PathHelper.SamePath(path, workDir)
            ? Path.GetRelativePath(workDir, path)
            : Path.GetFileName(path);
        if (keepExtension) return title;
        var bare = Path.ChangeExtension(title, null);
        return Path.GetFileName(bare).Length > 0 ? bare : title;
    }

    private static string KindOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" or ".doc" or ".rtf" or ".odt" or ".pdf" => "Документ",
        ".xlsx" or ".xls" or ".csv" or ".tsv" or ".ods" => "Таблица",
        ".pptx" or ".ppt" or ".odp" => "Презентация",
        ".txt" or ".md" or ".log" => "Текст",
        var extension when CodeExtensions.Contains(extension) => "Код",
        _ when Attachment.IsImagePath(path) => "Изображение",
        _ => "Файл",
    };

    private static Border CardPage(string path, bool changed)
    {
        bool picture = !changed && Attachment.IsImagePath(path);
        var page = new Border
        {
            Width = 34,
            Height = 42,
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(2, 0, 16, 0),
            Background = Theme.Surface2,
            BorderBrush = Theme.LineStrong,
            BorderThickness = new Thickness(1),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-5),
            Child = new TextBlock
            {
                Text = changed || CodeExtensions.Contains(Path.GetExtension(path)) ? Glyphs.Code
                    : picture ? Glyphs.Picture
                    : Glyphs.Document,
                FontFamily = Theme.Icons, FontSize = 14, Foreground = Theme.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        if (picture) _ = ShowPageImageAsync(page, path);
        return page;
    }

    private static async Task ShowPageImageAsync(Border page, string path)
    {
        try
        {
            page.Background = new ImageBrush(await DecodeImageAsync(path, 96)) { Stretch = Stretch.UniformToFill };
            page.Child = null;
        }
        catch
        {
        }
    }

    private static TextBlock PreviewNote(string text) => new()
    {
        Text = text, FontSize = 13, Foreground = Theme.TextMuted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 8, 0),
    };

    private static StackPanel DiffView(FileDiff diff, int limit)
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

    private static Border DiffRow(char kind, string oldNumber, string newNumber, string text, Brush? foreground = null)
    {
        var row = new DockPanel();
        var numbers = new TextBlock
        {
            Text = $"{oldNumber,5} {newNumber,5} {(kind == ' ' ? ' ' : kind == '-' ? '−' : '+')}",
            FontFamily = Theme.Mono, FontSize = 12.5,
            Foreground = kind switch { '+' => Theme.Success, '-' => Theme.DangerText, _ => Theme.TextMuted },
            Margin = new Thickness(0, 0, 10, 0),
        };
        DockPanel.SetDock(numbers, Dock.Left);
        row.Children.Add(numbers);
        row.Children.Add(new TextBlock
        {
            Text = text, FontFamily = Theme.Mono, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
            Foreground = foreground ?? (kind == ' ' ? Theme.TextSoft : Theme.Text),
        });
        return new Border
        {
            Background = kind switch { '+' => Theme.Get("DiffAdd"), '-' => Theme.Get("DiffRemove"), _ => Brushes.Transparent },
            Padding = new Thickness(4, 1, 8, 1),
            Child = row,
        };
    }
}
