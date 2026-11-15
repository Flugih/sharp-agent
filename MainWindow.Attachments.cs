using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SharpAgent.Agent;
using SharpAgent.Core;
using SharpAgent.Tools;
using SharpAgent.Ui;

namespace SharpAgent;

public partial class MainWindow
{
    private const int MaxAttachments = 20;
    private const long SandboxCopyLimit = 100L * 1024 * 1024;

    private readonly List<Attachment> _pendingAttachments = new();
    private DispatcherTimer? _dropHideTimer;

    private static bool HasFiles(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) || VirtualFiles.HasFiles(data);

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!HasFiles(e.Data)) return;

        e.Handled = true;
        _dropHideTimer?.Stop();
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        if (e.Data.GetDataPresent(FileActions.DragMarker))
        {
            bool overComposer = IsPointerOver(Composer, e);
            e.Effects = overComposer ? DragDropEffects.Copy : DragDropEffects.None;
            Composer.BorderBrush = overComposer || InputBox.IsKeyboardFocused ? Theme.Accent : Theme.LineStrong;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        if (DropOverlay.Visibility == Visibility.Visible) return;
        DropHint.Text = _config.Sandbox
            ? "Включена песочница: файлы будут скопированы в папку программы, чтобы агент мог с ними работать."
            : "Агент получит пути к файлам и папкам и сможет с ними работать. Изображения модель увидит сама.";
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        if (_dropHideTimer == null)
        {
            _dropHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            _dropHideTimer.Tick += (_, _) => HideDropOverlay();
        }
        _dropHideTimer.Stop();
        _dropHideTimer.Start();
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        bool accepted = !e.Data.GetDataPresent(FileActions.DragMarker) || IsPointerOver(Composer, e);
        HideDropOverlay();
        if (!HasFiles(e.Data)) return;

        e.Handled = true;
        if (!accepted || SettingsOverlay.Visibility == Visibility.Visible) return;
        var paths = e.Data.GetData(DataFormats.FileDrop) as string[] ?? SaveVirtualFiles(e.Data);
        if (paths.Length == 0) return;
        _ = AddAttachmentsAsync(paths);
        Activate();
        InputBox.Focus();
    }

    private string[] SaveVirtualFiles(IDataObject data)
    {
        try
        {
            var paths = VirtualFiles.Read(data).Select(file =>
            {
                Directory.CreateDirectory(AppPaths.AttachmentsFolder);
                var path = PathHelper.UniquePath(Path.Combine(AppPaths.AttachmentsFolder, file.Name));
                File.WriteAllBytes(path, file.Data);
                return path;
            }).ToArray();
            if (paths.Length == 0)
                ShowToast("Эти файлы не удалось получить. Сохраните их в папку и перетащите оттуда.", ToastKind.Warning);
            return paths;
        }
        catch (Exception ex)
        {
            ShowToast("Не удалось получить перетащенные файлы: " + ex.Message, ToastKind.Error);
            return [];
        }
    }

    private void HideDropOverlay()
    {
        _dropHideTimer?.Stop();
        DropOverlay.Visibility = Visibility.Collapsed;
        Composer.BorderBrush = InputBox.IsKeyboardFocused ? Theme.Accent : Theme.LineStrong;
    }

    private static bool IsPointerOver(FrameworkElement element, DragEventArgs e)
    {
        var point = e.GetPosition(element);
        return point.X >= 0 && point.Y >= 0 && point.X <= element.ActualWidth && point.Y <= element.ActualHeight;
    }

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Прикрепить файлы", Multiselect = true, CheckFileExists = true };
        if (Directory.Exists(_toolContext.WorkingDirectory)) dialog.InitialDirectory = _toolContext.WorkingDirectory;
        if (dialog.ShowDialog(this) == true) _ = AddAttachmentsAsync(dialog.FileNames);
        InputBox.Focus();
    }

    private void AttachFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Прикрепить папку", Multiselect = true };
        if (Directory.Exists(_toolContext.WorkingDirectory)) dialog.InitialDirectory = _toolContext.WorkingDirectory;
        if (dialog.ShowDialog(this) == true) _ = AddAttachmentsAsync(dialog.FolderNames);
        InputBox.Focus();
    }

    private void InputBox_PreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !ClipboardHasAttachment()) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    private void InputBox_PreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Paste && TryAttachFromClipboard()) e.Handled = true;
    }

    private static bool ClipboardHasAttachment()
    {
        try
        {
            return Clipboard.ContainsFileDropList() || (Clipboard.ContainsImage() && !Clipboard.ContainsText());
        }
        catch
        {
            return false;
        }
    }

    private bool TryAttachFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (files.Length == 0) return false;
                _ = AddAttachmentsAsync(files);
                return true;
            }

            if (Clipboard.ContainsImage() && !Clipboard.ContainsText() && SaveClipboardImage() is { } saved)
            {
                _ = AddAttachmentsAsync([saved]);
                return true;
            }
        }
        catch (Exception ex)
        {
            ShowToast("Не удалось взять данные из буфера обмена: " + ex.Message, ToastKind.Error);
            return true;
        }
        return false;
    }

    private static string? SaveClipboardImage()
    {
        Directory.CreateDirectory(AppPaths.AttachmentsFolder);
        var path = PathHelper.UniquePath(Path.Combine(AppPaths.AttachmentsFolder, $"Снимок {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png"));

        if (Clipboard.GetDataObject() is { } data && data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
        {
            File.WriteAllBytes(path, png.ToArray());
            return path;
        }

        var image = Clipboard.GetImage();
        if (image == null) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0)));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private async Task AddAttachmentsAsync(IEnumerable<string> paths)
    {
        var problems = new List<string>();
        foreach (var raw in paths)
        {
            if (_pendingAttachments.Count >= MaxAttachments)
            {
                problems.Add($"к одному сообщению можно приложить не больше {MaxAttachments} файлов");
                break;
            }

            var name = ChatLinks.DisplayName(raw);
            try
            {
                var path = Path.GetFullPath(raw);
                if (File.Exists(path) && _toolContext.IsSecret(path))
                {
                    problems.Add("секретные файлы программы (.env с ключами, вход в ChatGPT) прикрепить нельзя");
                    continue;
                }
                if (_config.Sandbox && !PathHelper.IsInside(path, AppPaths.AppFolder))
                {
                    var copy = await CopyIntoSandboxAsync(path, problems);
                    if (copy == null) continue;
                    path = copy;
                }

                var attachment = Attachment.FromPath(path);
                if (attachment == null)
                {
                    problems.Add($"«{name}» не найден");
                    continue;
                }
                if (_pendingAttachments.Any(a => PathHelper.SamePath(a.Path, attachment.Path))) continue;
                _pendingAttachments.Add(attachment);
            }
            catch (Exception ex)
            {
                problems.Add($"«{name}»: {ex.Message}");
            }
        }

        RenderPendingAttachments();
        if (problems.Count > 0) ShowToast("Не всё удалось прикрепить: " + string.Join("; ", problems) + ".", ToastKind.Warning);
    }

    private static async Task<string?> CopyIntoSandboxAsync(string source, List<string> problems)
    {
        var name = ChatLinks.DisplayName(source);
        if (Directory.Exists(source))
        {
            problems.Add($"папку «{name}» в песочнице прикрепить нельзя — агент работает только внутри папки программы");
            return null;
        }
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists)
            {
                problems.Add($"«{name}» не найден");
                return null;
            }
            if (info.Length > SandboxCopyLimit)
            {
                problems.Add($"«{name}» больше {PathHelper.FormatSize(SandboxCopyLimit)} — в песочницу не копируется");
                return null;
            }
            Directory.CreateDirectory(AppPaths.AttachmentsFolder);
            var target = PathHelper.UniquePath(Path.Combine(AppPaths.AttachmentsFolder, info.Name));
            await Task.Run(() => File.Copy(source, target));
            return target;
        }
        catch (Exception ex)
        {
            problems.Add($"«{name}»: {ex.Message}");
            return null;
        }
    }

    private void RenderPendingAttachments()
    {
        AttachPanel.Children.Clear();
        foreach (var attachment in _pendingAttachments)
        {
            void Remove()
            {
                _pendingAttachments.Remove(attachment);
                RenderPendingAttachments();
                InputBox.Focus();
            }

            var chip = _links.FileChip(attachment.Path, attachment.Name,
                FileActions.Item("Убрать из сообщения", Glyphs.Cancel, Remove));
            chip.ToolTip = $"{attachment.Path}\n{attachment.Describe()}\nЩелчок — открыть, правая кнопка — другие действия";
            chip.Background = System.Windows.Media.Brushes.Transparent;
            chip.BorderThickness = new Thickness(0);
            if (attachment.Kind == AttachmentKind.Image) _ = LoadThumbnailAsync(chip, attachment.Path);

            var remove = Theme.IconButton(Glyphs.Cancel, "Убрать вложение " + attachment.Name, 22, 9);
            remove.Margin = new Thickness(0, 0, 3, 0);
            remove.VerticalAlignment = VerticalAlignment.Center;
            remove.Click += (_, _) => Remove();

            var item = new StackPanel { Orientation = Orientation.Horizontal };
            item.Children.Add(chip);
            item.Children.Add(remove);
            AttachPanel.Children.Add(new Border
            {
                Child = item, Background = Theme.Surface2, CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 6, 5),
            });
        }
        AttachPanel.Visibility = _pendingAttachments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSendButton();
    }

    private void ClearPendingAttachments()
    {
        _pendingAttachments.Clear();
        RenderPendingAttachments();
    }

    private static Task<BitmapImage> DecodeImageAsync(string path, int width) => Task.Run(() =>
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = width;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    });

    private static async Task LoadThumbnailAsync(Button chip, string path)
    {
        try
        {
            var image = await DecodeImageAsync(path, 40);
            if (chip.Content is not StackPanel { Children.Count: > 0 } content) return;
            content.Children[0] = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill },
            };
        }
        catch
        {
        }
    }
}
