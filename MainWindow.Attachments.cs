using System.Windows.Media.Imaging;

namespace SharpAgent;

public partial class MainWindow
{
    const int MaxAttachments = 20;
    const long SandboxCopyLimit = 100L * 1024 * 1024;

    readonly List<Attachment> _pendingAttachments = [];
    DispatcherTimer? _dropHideTimer;

    static bool HasFiles(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop) || VirtualFiles.HasFiles(data);

    void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!HasFiles(e.Data)) return;
        e.Handled = true;
        _dropHideTimer?.Stop();
        if (SettingsOpen || IsShown(SearchLayer))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        if (e.Data.GetDataPresent(FileActions.DragMarker))
        {
            bool overComposer = IsPointerOver(Composer, e);
            e.Effects = overComposer ? DragDropEffects.Copy : DragDropEffects.None;
            Composer.Paint(Border.BorderBrushProperty, overComposer ? "Accent" : "ComposerLine");
            return;
        }
        e.Effects = DragDropEffects.Copy;
        if (IsShown(DropOverlay)) return;
        DropHint.Text = _config.Sandbox
            ? Tr("Включена песочница: файлы будут скопированы в папку программы, чтобы агент мог с ними работать.", "Sandbox is enabled. Files will be copied into the app folder so the agent can access them.")
            : Tr("Агент получит пути к файлам и папкам и сможет с ними работать. Изображения модель увидит сама.", "The agent receives file and folder paths. The model can view attached images.");
        DropOverlay.Visibility = Visibility.Visible;
    }

    void Window_PreviewDragLeave(object sender, DragEventArgs e)
    {
        _dropHideTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, (_, _) => HideDropOverlay(), Dispatcher);
        _dropHideTimer.Stop();
        _dropHideTimer.Start();
    }

    void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        bool accepted = !e.Data.GetDataPresent(FileActions.DragMarker) || IsPointerOver(Composer, e);
        HideDropOverlay();
        if (!HasFiles(e.Data)) return;
        e.Handled = true;
        if (!accepted || SettingsOpen || IsShown(SearchLayer) || WaitForChatFile(_chatOperation != null)) return;
        var paths = AttachmentDropPaths(e.Data);
        if (paths.Length == 0) return;
        _ = AddAttachmentsAsync(paths);
        Activate();
        InputBox.Focus();
    }

    string[] AttachmentDropPaths(IDataObject data) => data.GetData(DataFormats.FileDrop) as string[] ?? SaveVirtualFiles(data);

    string[] SaveVirtualFiles(IDataObject data)
    {
        try
        {
            var paths = VirtualFiles.Read(data).Select(file =>
            {
                var path = NewAttachmentPath(file.Name);
                File.WriteAllBytes(path, file.Data);
                return path;
            }).ToArray();
            if (paths.Length == 0) ShowToast(Tr("Эти файлы не удалось получить. Сохраните их в папку и перетащите оттуда.", "Could not access these files. Save them to a folder and drag them from there."), ToastKind.Warning);
            return paths;
        }
        catch (Exception ex)
        {
            ShowToast(Tr("Не удалось получить перетащенные файлы: ", "Could not access dropped files: ") + ex.Message, ToastKind.Error);
            return [];
        }
    }

    static string NewAttachmentPath(string name)
    {
        Directory.CreateDirectory(AppPaths.AttachmentsFolder);
        return PathHelper.UniquePath(Path.Combine(AppPaths.AttachmentsFolder, name));
    }

    void HideDropOverlay()
    {
        _dropHideTimer?.Stop();
        DropOverlay.Visibility = Visibility.Collapsed;
        Composer.Paint(Border.BorderBrushProperty, "ComposerLine");
    }

    static bool IsPointerOver(FrameworkElement element, DragEventArgs e) => new Rect(element.RenderSize).Contains(e.GetPosition(element));

    void Attach_Click(object sender, RoutedEventArgs e) => PickAttachmentFiles();

    void PickAttachmentFiles()
    {
        if (SettingsOpen) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = Tr("Прикрепить файлы", "Attach files"), Multiselect = true, CheckFileExists = true };
        if (Directory.Exists(_toolContext.WorkingDirectory)) dialog.InitialDirectory = _toolContext.WorkingDirectory;
        if (dialog.ShowDialog(ActiveChatWindow) == true) _ = AddAttachmentsAsync(dialog.FileNames);
        FocusAttachmentInput();
    }

    void AttachFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsOpen) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Tr("Прикрепить папку", "Attach folder"), Multiselect = true };
        if (Directory.Exists(_toolContext.WorkingDirectory)) dialog.InitialDirectory = _toolContext.WorkingDirectory;
        if (dialog.ShowDialog(this) == true) _ = AddAttachmentsAsync(dialog.FolderNames);
        InputBox.Focus();
    }

    void InputBox_PreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !ClipboardHasAttachment()) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    void InputBox_PreviewExecuted(object sender, ExecutedRoutedEventArgs e) { if (e.Command == ApplicationCommands.Paste && TryAttachFromClipboard()) e.Handled = true; }

    static bool ClipboardHasAttachment()
    {
        try { return Clipboard.ContainsFileDropList() || (Clipboard.ContainsImage() && !Clipboard.ContainsText()); }
        catch { return false; }
    }

    bool TryAttachFromClipboard()
    {
        if (ClipboardHasAttachment() && (WaitForChatFile(_chatOperation != null))) return true;
        try
        {
            string[] files = Clipboard.ContainsFileDropList() ? [.. Clipboard.GetFileDropList().Cast<string>()]
                : Clipboard.ContainsImage() && !Clipboard.ContainsText() && SaveClipboardImage() is { } saved ? [saved] : [];
            if (files.Length == 0) return false;
            _ = AddAttachmentsAsync(files);
        }
        catch (Exception ex)
        {
            ShowToast(Tr("Не удалось взять данные из буфера обмена: ", "Could not read the clipboard: ") + ex.Message, ToastKind.Error);
        }
        return true;
    }

    static string? SaveClipboardImage()
    {
        var path = NewAttachmentPath($"Снимок {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png");
        if (Clipboard.GetDataObject() is { } data && data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
        {
            File.WriteAllBytes(path, png.ToArray());
            return path;
        }
        if (Clipboard.GetImage() is not { } image) return null;
        var encoder = new PngBitmapEncoder { Frames = { BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0)) } };
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    int _attachmentImports;

    async Task AddAttachmentsAsync(IEnumerable<string> paths)
    {
        if (WaitForChatFile(_chatOperation != null)) return;
        _attachmentImports++;
        UpdateSendButton();
        try { await ImportAttachmentsAsync(paths); }
        finally
        {
            _attachmentImports--;
            UpdateSendButton();
        }
    }

    async Task ImportAttachmentsAsync(IEnumerable<string> paths)
    {
        var problems = new List<string>();
        foreach (var raw in paths)
        {
            if (_pendingAttachments.Count >= MaxAttachments)
            {
                problems.Add(Tr($"к одному сообщению можно приложить не больше {MaxAttachments} файлов", $"you can attach up to {MaxAttachments} files per message"));
                break;
            }
            var name = ChatLinks.DisplayName(raw);
            try
            {
                var path = Path.GetFullPath(raw);
                if (File.Exists(path) && _toolContext.IsSecret(path))
                {
                    problems.Add(Tr("секретные файлы программы (.env с ключами, вход в ChatGPT) прикрепить нельзя", "private settings and ChatGPT sign-in files cannot be attached"));
                    continue;
                }
                if (_config.Sandbox && !PathHelper.IsInside(path, AppPaths.AppFolder))
                {
                    var copy = await CopyIntoSandboxAsync(path, problems);
                    if (copy == null) continue;
                    if (_pendingAttachments.Count >= MaxAttachments)
                    {
                        File.Delete(copy);
                        problems.Add(Tr($"к одному сообщению можно приложить не больше {MaxAttachments} файлов", $"you can attach up to {MaxAttachments} files per message"));
                        break;
                    }
                    path = copy;
                }
                if (Attachment.FromPath(path) is not { } attachment) problems.Add($"«{name}» не найден");
                else if (!_pendingAttachments.Any(a => PathHelper.SamePath(a.Path, attachment.Path))) _pendingAttachments.Add(attachment);
            }
            catch (Exception ex)
            {
                problems.Add($"«{name}»: {ex.Message}");
            }
        }
        RenderPendingAttachments();
        if (problems.Count > 0) ShowToast(Tr("Не всё удалось прикрепить: ", "Some attachments could not be added: ") + string.Join("; ", problems) + ".", ToastKind.Warning);
    }

    static async Task<string?> CopyIntoSandboxAsync(string source, List<string> problems)
    {
        var name = ChatLinks.DisplayName(source);
        if (Directory.Exists(source))
        {
            problems.Add(Tr($"папку «{name}» в песочнице прикрепить нельзя — агент работает только внутри папки программы", $"“{name}” is outside the sandbox and cannot be attached"));
            return null;
        }
        try
        {
            var info = new FileInfo(source);
            if (!info.Exists || info.Length > SandboxCopyLimit)
            {
                problems.Add(info.Exists ? $"«{name}» больше {PathHelper.FormatSize(SandboxCopyLimit)} — в песочницу не копируется" : $"«{name}» не найден");
                return null;
            }
            var target = NewAttachmentPath(info.Name);
            await Task.Run(() => File.Copy(source, target));
            return target;
        }
        catch (Exception ex)
        {
            problems.Add($"«{name}»: {ex.Message}");
            return null;
        }
    }

    void RenderPendingAttachments()
    {
        AttachPanel.Children.Clear();
        foreach (var attachment in _pendingAttachments)
        {
            void Remove()
            {
                RemovePendingAttachment(attachment);
            }
            var chip = _links.FileChip(attachment.Path, attachment.Name, FileActions.Item(Tr("Убрать из сообщения", "Remove from message"), Glyphs.Cancel, Remove));
            chip.ToolTip = $"{attachment.Path}\n{attachment.Describe()}\nЩелчок — открыть, правая кнопка — другие действия";
            if (attachment.Kind == AttachmentKind.Image) _ = LoadThumbnailAsync(chip, attachment.Path);
            var remove = Theme.IconButton(Glyphs.Cancel, Tr("Убрать вложение ", "Remove attachment ") + attachment.Name, 24, 10);
            remove.Margin = new Thickness(0, 0, 1, 0);
            remove.VerticalAlignment = VerticalAlignment.Center;
            remove.Click += (_, _) => Remove();
            AttachPanel.Children.Add(new Border
            {
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { chip, remove } },
                CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 6, 6),
            }.Paint(Border.BackgroundProperty, "Surface3"));
        }
        AttachPanel.Visibility = VisibleIf(_pendingAttachments.Count > 0);
        _compactChat?.ShowAttachments(_pendingAttachments, RemovePendingAttachment);
        UpdateSendButton();
    }

    void RemovePendingAttachment(Attachment attachment)
    {
        _pendingAttachments.Remove(attachment);
        RenderPendingAttachments();
        FocusAttachmentInput();
    }

    void FocusAttachmentInput()
    {
        if (_compactChat is { } compact) compact.FocusInput();
        else InputBox.Focus();
    }

    void ClearPendingAttachments()
    {
        _pendingAttachments.Clear();
        RenderPendingAttachments();
    }

    static Task<BitmapImage> DecodeImageAsync(string path, int width) =>
        Task.Run(() => LoadFrozenBitmap(path, width, BitmapCreateOptions.IgnoreColorProfile));

    static BitmapImage LoadFrozenBitmap(string path, int width = 0, BitmapCreateOptions options = BitmapCreateOptions.None)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = options;
        bitmap.DecodePixelWidth = width;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    static async Task LoadThumbnailAsync(Button chip, string path)
    {
        try
        {
            var image = await DecodeImageAsync(path, 40);
            if (chip.Content is not StackPanel { Children.Count: > 0 } content) return;
            content.Children[0] = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center, Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill },
            };
        }
        catch { }
    }
}
