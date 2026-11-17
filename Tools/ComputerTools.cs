using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SharpAgent.Tools;

public static class ComputerTools
{
    public const string Name = "computer", ShotPrefix = "Скриншот сохранён: ";
    const int MaxTreeLines = 180, MaxShotSide = 1280;
    static readonly AutomationProperty[] Cached =
    [
        AutomationElement.NameProperty, AutomationElement.AutomationIdProperty, AutomationElement.ControlTypeProperty,
        AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, ValuePattern.ValueProperty, TogglePattern.ToggleStateProperty,
    ];
    static readonly HashSet<ControlType> Interactive =
    [
        ControlType.Button, ControlType.Edit, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox, ControlType.MenuItem,
        ControlType.ListItem, ControlType.TabItem, ControlType.Hyperlink, ControlType.Slider, ControlType.TreeItem, ControlType.DataItem,
        ControlType.SplitButton, ControlType.Document,
    ];
    static IntPtr _window;
    static int _pid;
    static Process? _launched;
    static Dictionary<int, AutomationElement> _refs = [];
    static (int Left, int Top, double Scale)? _shot;

    public static IEnumerable<AgentTool> All() =>
    [
        new AgentTool
        {
            Name = Name,
            Description = "Управление программами на экране через UI Automation: запуск, окна, дерево элементов, нажатия, ввод текста, " +
                "клавиши, скриншот. Работает с WPF, WinForms, Win32, Qt, UWP и другими; если элементов нет в дереве — щёлкай по координатам " +
                "со скриншота. После действий программа сама сообщает заголовок окна, новые диалоги с их текстом и завершение процесса. " +
                "Сначала смотри tree — это дёшево; screenshot делай только для проверки внешнего вида или когда дерево пустое.",
            Parameters = Schema.Object(
                new Prop("action", "string", "Действие.", Required: true,
                    Enum: ["launch", "windows", "tree", "click", "type", "keys", "screenshot", "wait", "close"]),
                new Prop("path", "string", "launch: программа, ярлык или файл."),
                new Prop("arguments", "string", "launch: аргументы командной строки."),
                new Prop("window", "string", "Окно: id из windows/launch или часть заголовка. По умолчанию — текущее окно."),
                new Prop("element", "string", "Элемент: #номер из последнего tree либо имя или AutomationId."),
                new Prop("x", "integer", "click без element: X на последнем скриншоте."),
                new Prop("y", "integer", "click без element: Y на последнем скриншоте."),
                new Prop("button", "string", "click: left (по умолчанию), right или double.", Enum: ["left", "right", "double"]),
                new Prop("text", "string", "type: текст (заменяет значение поля); keys: сочетания через пробел, например \"ctrl+s enter\"; " +
                    "tree: показать только элементы с этим текстом."),
                new Prop("seconds", "integer", "wait: сколько ждать (по умолчанию 10, максимум 60); с element или window — до их появления.")),
            Handler = RunAsync,
            Confirmation = (a, c) => a.Optional("action") == "launch"
                ? new ConfirmRequest("Запустить программу?", $"{a.Required("path")} {a.Optional("arguments")}".Trim(), "Запустить", Danger: true)
                : null,
        },
    ];

    static async Task<string> RunAsync(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var action = a.Required("action");
        try
        {
            return action switch
            {
                "launch" => await LaunchAsync(a, c, ct),
                "windows" => Windows(),
                "wait" => await WaitAsync(a, ct),
                "tree" => Tree(Target(a.Optional("window")), a.Optional("text")),
                "screenshot" => Screenshot(Target(a.Optional("window")), c),
                "click" or "type" or "keys" or "close" => await AfterAsync(Target(a.Optional("window")), action, a, ct),
                _ => throw new ToolException($"неизвестное действие '{action}'."),
            };
        }
        catch (ElementNotAvailableException) { throw new ToolException("элемент исчез или окно закрылось — запроси tree заново."); }
        catch (InvalidOperationException ex) { throw new ToolException("элемент не поддерживает это действие: " + ex.Message); }
    }

    static async Task<string> LaunchAsync(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        string path = a.Required("path"), arguments = a.Optional("arguments") ?? "";
        foreach (var part in arguments.Split([' ', '"'], StringSplitOptions.RemoveEmptyEntries).Append(arguments).Append(path))
            if (Existing(part, c) is { } full)
            {
                c.GuardSecrets(full);
                if (Environment.ProcessPath is { } self && PathHelper.SamePath(PathHelper.FinalPath(full), PathHelper.FinalPath(self))) throw OwnWindow();
            }
        if (Existing(path, c) != null) path = c.Resolve(path);
        var before = TopWindows().Select(w => w.Handle).ToHashSet();
        var process = Process.Start(new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = true, WorkingDirectory = Path.IsPathFullyQualified(path) && Path.GetDirectoryName(path) is { } dir ? dir : c.WorkingDirectory,
        });
        for (var clock = Stopwatch.StartNew(); clock.Elapsed.TotalSeconds < 30; await Task.Delay(300, ct))
        {
            var fresh = TopWindows().Where(w => !before.Contains(w.Handle)).ToList();
            var found = fresh.FirstOrDefault(w => w.Pid == process?.Id);
            if (found.Handle == IntPtr.Zero && (process == null || process.HasExited || clock.Elapsed.TotalSeconds > 3)) found = fresh.FirstOrDefault();
            if (found.Handle != IntPtr.Zero)
            {
                _launched = process is { HasExited: false } ? process : null;
                Use(found.Handle);
                try { Process.GetProcessById(found.Pid).WaitForInputIdle(3000); }
                catch (InvalidOperationException) { }
                return $"Запущено: «{found.Title}» (окно {Id(found.Handle)}, процесс {ProcessName(found.Pid)}, PID {found.Pid})\n" + Tree(found.Handle, null);
            }
            if (process is { HasExited: true } && clock.Elapsed.TotalSeconds > 5)
                return $"Процесс завершился с кодом {process.ExitCode}, не открыв окна.";
        }
        return "Программа запущена, но новое окно за 30 с не появилось. Посмотри список окон (windows).";
    }

    static string? Existing(string part, ToolContext c)
    {
        try { return PathHelper.Resolve(part, c.WorkingDirectory) is var full && Path.Exists(full) ? full : null; }
        catch (ToolException) { return null; }
    }

    static string Windows()
    {
        var windows = TopWindows();
        return windows.Count == 0 ? "Видимых окон других программ нет." : "Окна (id — заголовок — процесс):\n" + string.Join("\n",
            windows.Select(w => $"{(w.Handle == _window ? "* " : "")}{Id(w.Handle)} — {AgentRunner.Clip(w.Title, 90)} — {ProcessName(w.Pid)}"));
    }

    static async Task<string> WaitAsync(ToolArgs a, CancellationToken ct)
    {
        int seconds = Math.Clamp(a.Int("seconds", 10), 1, 60);
        string? window = a.Optional("window"), element = a.Optional("element");
        if (window == null && element == null)
        {
            await Task.Delay(seconds * 1000, ct);
            return $"Прошло {seconds} с.\n" + State(Target(null));
        }
        for (var clock = Stopwatch.StartNew(); ; await Task.Delay(400, ct))
            try
            {
                var target = Target(window);
                if (element != null) Find(target, element);
                return (element != null ? $"Элемент «{element}» появился.\n" : "") + State(target);
            }
            catch (ToolException) when (clock.Elapsed.TotalSeconds < seconds) { }
    }

    static IntPtr Target(string? window)
    {
        var windows = TopWindows();
        var found = window == null
            ? IsWindowVisible(_window) ? _window : windows.FirstOrDefault(w => w.Pid == _pid).Handle
            : window.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(window[2..], NumberStyles.HexNumber, null, out var raw)
                ? windows.FirstOrDefault(w => w.Handle == (IntPtr)raw).Handle
                : (windows.FirstOrDefault(w => w.Title.Contains(window, StringComparison.OrdinalIgnoreCase)) is { Handle: not 0 } titled ? titled
                    : windows.FirstOrDefault(w => ProcessName(w.Pid).Equals(window, StringComparison.OrdinalIgnoreCase))).Handle;
        if (found == IntPtr.Zero)
            throw new ToolException(window == null ? "текущего окна нет — запусти программу (launch) или укажи окно из списка windows."
                : $"окно «{window}» не найдено среди видимых окон других программ. Посмотри список windows.");
        Use(found);
        return found;
    }

    static void Use(IntPtr window)
    {
        if (window != _window) _shot = null;
        _window = window;
        _pid = PidOf(window);
    }

    static string Tree(IntPtr window, string? filter)
    {
        var refs = new Dictionary<int, AutomationElement>();
        var lines = new List<string>();
        int hidden = 0, omitted = 0;
        void Walk(AutomationElement parent, int depth)
        {
            foreach (AutomationElement child in parent.CachedChildren)
            {
                if (child.Cached.IsOffscreen)
                {
                    hidden++;
                    continue;
                }
                var line = Describe(child);
                bool shown = line != null && (filter == null || line.Contains(filter, StringComparison.OrdinalIgnoreCase));
                if (shown && lines.Count >= MaxTreeLines) omitted++;
                else if (shown)
                {
                    refs[refs.Count + 1] = child;
                    lines.Add($"{new string(' ', filter == null ? depth * 2 : 0)}#{refs.Count} {line}");
                }
                Walk(child, shown && filter == null ? depth + 1 : depth);
            }
        }
        Walk(Snapshot(window), 0);
        _refs = refs;
        var head = $"Окно «{TitleOf(window)}» ({Id(window)}). Элементы (#номер — для click/type):";
        if (lines.Count == 0)
            return head + (filter != null ? $"\nЭлементов с текстом «{filter}» нет."
                : "\nДерево пусто: программа не сообщает элементы UI Automation (игра или своя отрисовка). Сделай screenshot и щёлкай по координатам.");
        return head + "\n" + string.Join("\n", lines) + (omitted > 0 ? $"\n… ещё {omitted} элементов — сузь поиск параметром text." : "") +
               (hidden > 0 ? $"\n(скрытых за пределами экрана пропущено: {hidden})" : "");
    }

    static AutomationElement Snapshot(IntPtr window)
    {
        var request = new CacheRequest { TreeScope = TreeScope.Subtree, TreeFilter = Automation.ControlViewCondition };
        foreach (var property in Cached) request.Add(property);
        return AutomationElement.FromHandle(window).GetUpdatedCache(request);
    }

    static IEnumerable<AutomationElement> Flatten(AutomationElement root) =>
        root.CachedChildren.Cast<AutomationElement>().Where(e => !e.Cached.IsOffscreen).SelectMany(e => Flatten(e).Prepend(e));

    static string? Describe(AutomationElement e)
    {
        var info = e.Cached;
        var name = AgentRunner.Clip(info.Name.ReplaceLineEndings(" ").Trim(), 80);
        var value = e.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string;
        if (name.Length == 0 && info.AutomationId.Length == 0 && string.IsNullOrEmpty(value) && !Interactive.Contains(info.ControlType)) return null;
        var text = new StringBuilder(info.ControlType.ProgrammaticName["ControlType.".Length..]);
        if (name.Length > 0) text.Append($" \"{name}\"");
        if (info.AutomationId.Length > 0 && info.AutomationId != info.Name) text.Append(" id=" + info.AutomationId);
        if (!string.IsNullOrEmpty(value) && value != info.Name) text.Append($" = \"{AgentRunner.Clip(value.ReplaceLineEndings(" "), 80)}\"");
        if (e.GetCachedPropertyValue(TogglePattern.ToggleStateProperty, true) is ToggleState state)
            text.Append(state switch { ToggleState.On => " [отмечен]", ToggleState.Off => " [не отмечен]", _ => " [частично]" });
        if (!info.IsEnabled) text.Append(" [недоступен]");
        return text.ToString();
    }

    static AutomationElement? Find(IntPtr window, string? query)
    {
        if (query == null) return null;
        if (query.StartsWith('#') && int.TryParse(query[1..], out var number))
            return _refs.GetValueOrDefault(number) ?? throw new ToolException($"элемента {query} нет в последнем tree — запроси tree заново.");
        var all = Flatten(Snapshot(window)).ToList();
        bool Exact(AutomationElement e) => e.Cached.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ||
                                           e.Cached.AutomationId.Equals(query, StringComparison.OrdinalIgnoreCase);
        return all.Where(Exact).OrderBy(e => !e.Cached.IsEnabled).FirstOrDefault() ??
               all.Where(e => e.Cached.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(e => !e.Cached.IsEnabled).FirstOrDefault() ??
               throw new ToolException($"элемент «{query}» не найден в окне «{TitleOf(window)}». Посмотри tree.");
    }

    static async Task<string> AfterAsync(IntPtr window, string action, ToolArgs a, CancellationToken ct)
    {
        var element = Find(window, a.Optional("element"));
        if (element != null && IsOwn(element.Current.ProcessId)) throw OwnWindow();
        var done = action switch
        {
            "click" => Click(window, element, a.Optional("button") ?? "left", a),
            "type" => Type(window, element, a.Required("text", allowEmpty: true)),
            "keys" => Keys(window, element, a.Required("text")),
            _ => Close(window),
        };
        await Task.Delay(action == "close" ? 1200 : 600, ct);
        return done + "\n" + State(window);
    }

    static string Label(AutomationElement e) => e.Current.Name is { Length: > 0 } name ? $"«{AgentRunner.Clip(name, 60)}»"
        : e.Current.AutomationId is { Length: > 0 } id ? id : e.Current.LocalizedControlType;

    static string Click(IntPtr window, AutomationElement? element, string button, ToolArgs a)
    {
        if (element != null && button == "left")
        {
            object? pattern = null;
            foreach (var kind in new[] { InvokePattern.Pattern, TogglePattern.Pattern, SelectionItemPattern.Pattern, ExpandCollapsePattern.Pattern })
                if (element.TryGetCurrentPattern(kind, out pattern)) break;
            Action? act = pattern switch
            {
                InvokePattern invoke => invoke.Invoke,
                TogglePattern toggle => toggle.Toggle,
                SelectionItemPattern select => select.Select,
                ExpandCollapsePattern expand => expand.Current.ExpandCollapseState == ExpandCollapseState.Expanded ? expand.Collapse : expand.Expand,
                _ => null,
            };
            if (act != null)
            {
                // Invoke blocks until a modal dialog it opens is closed, so don't wait for it forever.
                var task = Task.Run(act);
                Task.WaitAny(task, Task.Delay(3000));
                if (task.IsFaulted) task.GetAwaiter().GetResult();
                return "Нажато: " + Label(element);
            }
        }
        System.Windows.Point point;
        if (element != null)
        {
            if (!element.TryGetClickablePoint(out point))
            {
                var bounds = element.Current.BoundingRectangle;
                if (bounds.IsEmpty) throw new ToolException("у элемента нет видимой области — прокрути окно или посмотри screenshot.");
                point = new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            }
        }
        else if (_shot is { } shot && a.Int("x", -1) is >= 0 and var x && a.Int("y", -1) is >= 0 and var y)
            point = new(shot.Left + x / shot.Scale, shot.Top + y / shot.Scale);
        else throw new ToolException("укажи element или сначала сделай screenshot этого окна и передай x, y по нему.");
        Activate(window);
        if (IsOwn(PidOf(WindowFromPoint(new() { X = (int)point.X, Y = (int)point.Y })))) throw OwnWindow();
        SetCursorPos((int)point.X, (int)point.Y);
        var (down, up) = button == "right" ? (0x8, 0x10) : (0x2, 0x4);
        Send(Enumerable.Repeat(new[] { Mouse(down), Mouse(up) }, button == "double" ? 2 : 1).SelectMany(x => x).ToArray());
        return $"Щелчок ({button}) по {(element != null ? Label(element) : $"точке {a.Int("x", 0)}, {a.Int("y", 0)} снимка")}";
    }

    static string Type(IntPtr window, AutomationElement? element, string text)
    {
        if (element != null && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern { Current.IsReadOnly: false } value)
        {
            value.SetValue(text);
            return $"Введено в {Label(element)}: «{AgentRunner.Clip(text, 80)}»";
        }
        Focus(window, element);
        Send(text.Replace("\r", "").SelectMany(ch => ch == '\n' ? Key(0x0D) : [Unicode(ch, 0), Unicode(ch, 2)]).ToArray());
        return $"Напечатано: «{AgentRunner.Clip(text, 80)}»";
    }

    static string Keys(IntPtr window, AutomationElement? element, string keys)
    {
        Focus(window, element);
        foreach (var chord in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var codes = chord.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(VirtualKey).ToArray();
            Send([.. codes.Select(code => Keyboard(code, 0)), .. codes.Reverse().Select(code => Keyboard(code, 2))]);
            Thread.Sleep(60);
        }
        return "Нажаты клавиши: " + keys;
    }

    static string Close(IntPtr window)
    {
        PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
        return $"Окну «{TitleOf(window)}» отправлена команда закрыться.";
    }

    static void Focus(IntPtr window, AutomationElement? element)
    {
        Activate(window);
        try { element?.SetFocus(); }
        catch (InvalidOperationException) { }
        if (IsOwn(PidOf(GetForegroundWindow()))) throw OwnWindow();
    }

    static void Activate(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, 9);
        SetForegroundWindow(window);
        Thread.Sleep(150);
    }

    static string State(IntPtr window)
    {
        if (!IsWindow(window))
        {
            if (_launched is { } launched && launched.Id == _pid && launched.WaitForExit(500)) return $"Окно закрылось, процесс завершился с кодом {launched.ExitCode}.";
            return "Окно закрылось." + (TopWindows().FirstOrDefault(w => w.Pid == _pid) is { Handle: not 0 } next
                ? $" Открыто окно «{next.Title}» ({Id(next.Handle)})." : "");
        }
        var state = $"Окно: «{TitleOf(window)}»";
        foreach (var other in TopWindows().Where(w => GetWindow(w.Handle, 4) == window).Take(3))
        {
            var texts = Flatten(Snapshot(other.Handle)).Where(e => e.Cached.ControlType == ControlType.Text || e.Cached.ControlType == ControlType.Button)
                .Select(e => e.Cached.Name.Trim()).Where(name => name.Length > 0).Distinct();
            state += $"\nТакже открыто окно «{other.Title}» ({Id(other.Handle)}): {AgentRunner.Clip(string.Join(" | ", texts), 400)}";
        }
        return state;
    }

    static string Screenshot(IntPtr window, ToolContext c)
    {
        if (IsIconic(window)) ShowWindow(window, 9);
        GetWindowRect(window, out var rect);
        if (DwmGetWindowAttribute(window, 9, out Rect frame, Marshal.SizeOf<Rect>()) != 0) frame = rect;
        var image = Capture(window, rect, false);
        if (image == null || Blank(image))
        {
            Activate(window);
            image = Capture(window, rect, true) ?? throw new ToolException("не удалось снять окно.");
        }
        int left = Math.Max(0, frame.Left - rect.Left), top = Math.Max(0, frame.Top - rect.Top);
        var crop = new Int32Rect(left, top, Math.Min(frame.Right - frame.Left, image.PixelWidth - left), Math.Min(frame.Bottom - frame.Top, image.PixelHeight - top));
        BitmapSource shot = crop.Width > 0 && crop.Height > 0 ? new CroppedBitmap(image, crop) : image;
        double scale = Math.Min(1, (double)MaxShotSide / Math.Max(shot.PixelWidth, shot.PixelHeight));
        if (scale < 1) shot = new TransformedBitmap(shot, new ScaleTransform(scale, scale));
        var path = Path.Combine(c.LogsFolder ?? c.TempFolder, $"screen_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ImageEncoder.Encode(new PngBitmapEncoder(), shot));
        _shot = (rect.Left + left, rect.Top + top, scale);
        return $"{ShotPrefix}{path}\nОкно «{TitleOf(window)}», снимок {shot.PixelWidth}×{shot.PixelHeight}. Координаты x, y для click — по этому снимку. " +
               "Изображение передаётся тебе следующим сообщением, если модель видит картинки.";
    }

    static BitmapSource? Capture(IntPtr window, Rect r, bool fromScreen)
    {
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width <= 0 || height <= 0) throw new ToolException("окно свёрнуто или не имеет размера.");
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            bool ok = fromScreen ? BitBlt(memory, 0, 0, width, height, screen, r.Left, r.Top, 0x40CC0020) : PrintWindow(window, memory, 2);
            SelectObject(memory, old);
            return ok ? Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()) : null;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    static bool Blank(BitmapSource image)
    {
        var pixels = new int[image.PixelWidth * image.PixelHeight];
        new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0).CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels.All(p => (p & 0xFFFFFF) == 0);
    }

    static List<(IntPtr Handle, string Title, int Pid)> TopWindows()
    {
        var list = new List<(IntPtr, string, int)>();
        EnumWindows((handle, _) =>
        {
            if (IsWindowVisible(handle) && TitleOf(handle) is { Length: > 0 } title &&
                !(DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) && PidOf(handle) is var pid && !IsOwn(pid))
                list.Add((handle, title, pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static bool IsOwn(int pid)
    {
        if (pid == Environment.ProcessId) return true;
        try { return Environment.ProcessPath is { } self && Process.GetProcessById(pid).MainModule?.FileName is { } path && PathHelper.SamePath(path, self); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { return false; }
    }

    static ToolException OwnWindow() => new("управлять окнами самой программы Sharp Agent нельзя.");

    static string ProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch (ArgumentException) { return "?"; }
    }

    static int PidOf(IntPtr window)
    {
        GetWindowThreadProcessId(GetAncestor(window, 2) is var root && root != IntPtr.Zero ? root : window, out var pid);
        return (int)pid;
    }

    static string TitleOf(IntPtr window)
    {
        var text = new StringBuilder(GetWindowTextLength(window) + 1);
        GetWindowText(window, text, text.Capacity);
        return text.ToString();
    }

    static string Id(IntPtr window) => $"0x{window.ToInt64():X}";

    static ushort VirtualKey(string key) => key.ToLowerInvariant() switch
    {
        "ctrl" or "control" => 0x11, "shift" => 0x10, "alt" => 0x12, "win" => 0x5B, "enter" => 0x0D, "tab" => 0x09, "esc" or "escape" => 0x1B,
        "backspace" => 0x08, "delete" or "del" => 0x2E, "insert" => 0x2D, "space" => 0x20, "home" => 0x24, "end" => 0x23,
        "pageup" => 0x21, "pagedown" => 0x22, "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
        ['f', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 24 => (ushort)(0x6F + f),
        [var ch] when char.IsAsciiLetterOrDigit(ch) => char.ToUpperInvariant(ch),
        _ => throw new ToolException($"неизвестная клавиша «{key}». Пример: ctrl+shift+s, enter, f5, alt+f4."),
    };

    static Input[] Key(ushort code) => [Keyboard(code, 0), Keyboard(code, 2)];

    static Input Keyboard(ushort code, int flags) =>
        new() { Type = 1, Data = { Key = { Vk = code, Flags = flags | (code is >= 0x21 and <= 0x2E ? 1 : 0) } } };

    static Input Unicode(char ch, int flags) => new() { Type = 1, Data = { Key = { Scan = ch, Flags = flags | 4 } } };

    static Input Mouse(int flags) => new() { Data = { Mouse = { Flags = flags } } };

    static void Send(Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new ToolException("Windows не дала отправить ввод (окно программы с правами администратора или экран блокировки).");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput { public int Dx, Dy, Data, Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyInput { public ushort Vk, Scan; public int Flags, Time; public IntPtr Extra; }

    [StructLayout(LayoutKind.Explicit)]
    struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyInput Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Input { public int Type; public InputData Data; }

    delegate bool EnumProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
