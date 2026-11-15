using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using SharpAgent.Ui;
using Glyphs = SharpAgent.Ui.Glyphs;

namespace SharpAgent;

public partial class MainWindow
{
    private CancellationTokenSource? _officeLoad;
    private OfficePreview? _office;
    private XpsDocument? _officeXps;

    private async Task ShowOfficePreviewAsync(string path)
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
                PreviewBody.Content = ExternalOfficeView(path, ex is IOException ? ex.Message :
                    "Не удалось отобразить документ. Откройте его во внешней программе.");
        }
        finally
        {
            if (ReferenceEquals(_officeLoad, load)) _officeLoad = null;
            load.Dispose();
        }
    }

    private FrameworkElement OfficePagesView(OfficePreview preview, string path)
    {
        var content = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var previous = Theme.IconButton("\uE76B", "Предыдущая страница");
        var next = Theme.IconButton(Glyphs.ChevronRight, "Следующая страница");
        var counter = new TextBlock { Foreground = Theme.TextMuted, VerticalAlignment = VerticalAlignment.Center, Width = 66, TextAlignment = TextAlignment.Center };
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        navigation.Children.Add(previous);
        navigation.Children.Add(counter);
        navigation.Children.Add(next);
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 4, 8) };
        DockPanel.SetDock(navigation, Dock.Right);
        toolbar.Children.Add(navigation);

        var sheet = new Button
        {
            Style = Theme.Style("ModelPicker"), ToolTip = "Выбрать лист", HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = 215, Margin = new Thickness(0, 0, 8, 0),
        };
        AutomationProperties.SetName(sheet, "Выбрать лист");
        bool hasSheets = preview.Groups[0].Name.Length > 0;
        bool fit = !hasSheets;
        var zoom = Theme.IconButton(Glyphs.Search, fit ? "Показать в исходном размере" : "Подогнать под панель");
        navigation.Children.Insert(0, zoom);
        if (hasSheets) toolbar.Children.Add(sheet);

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(content);
        OfficePages group = preview.Groups[0];
        DocumentPaginator? paginator = null;
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
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(group.Files[page]);
                    bitmap.EndInit();
                    bitmap.Freeze();
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
                        Content = visual, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
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
            string title = fit ? "Показать в исходном размере" : "Подогнать под панель";
            zoom.ToolTip = title;
            AutomationProperties.SetName(zoom, title);
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

    private FrameworkElement ExternalOfficeView(string path, string message)
    {
        var body = new StackPanel();
        body.Children.Add(PreviewNote(message));
        var open = new Button
        {
            Content = "Открыть во внешней программе", ToolTip = "Открыть документ в приложении Windows",
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 9, 0, 0),
        };
        AutomationProperties.SetName(open, "Открыть во внешней программе");
        open.Click += (_, _) => FileActions.Open(this, path);
        body.Children.Add(open);
        return body;
    }

    private void CloseOfficePreview()
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
