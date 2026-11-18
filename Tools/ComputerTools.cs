using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SharpAgent.Tools;

public sealed class ComputerTools
{
    public const string Name = "computer", ShotPrefix = "Скриншот сохранён: ";
    const int MaxTreeLines = 180, MaxShotSide = 1280;
    readonly SemaphoreSlim _actions = new(1, 1);
    static readonly AutomationProperty[] Cached =
    [
        AutomationElement.NameProperty, AutomationElement.AutomationIdProperty, AutomationElement.ControlTypeProperty, AutomationElement.IsPasswordProperty,
        AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty, ValuePattern.ValueProperty, TogglePattern.ToggleStateProperty,
    ];
    static readonly HashSet<ControlType> Interactive =
    [
        ControlType.Button, ControlType.Edit, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox, ControlType.MenuItem,
        ControlType.ListItem, ControlType.TabItem, ControlType.Hyperlink, ControlType.Slider, ControlType.TreeItem, ControlType.DataItem,
        ControlType.SplitButton, ControlType.Document,
    ];
    IntPtr _window;
    int _pid, _nextRef;
    Process? _launched;
    Dictionary<int, AutomationElement> _refs = [];
    (Rect Window, int Left, int Top, int Width, int Height, double Scale)? _shot;

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
                    Enum: ["launch", "windows", "tree", "click", "type", "keys", "scroll", "screenshot", "wait", "close"]),
                new Prop("path", "string", "launch: программа, ярлык или файл."),
                new Prop("arguments", "string", "launch: аргументы командной строки."),
                new Prop("window", "string", "Окно: id из windows/launch или часть заголовка. По умолчанию — текущее окно."),
                new Prop("element", "string", "Элемент: #номер из последнего tree либо имя или AutomationId. Для scroll укажи элемент, который нужно показать."),
                new Prop("x", "integer", "click без element: X на последнем скриншоте."),
                new Prop("y", "integer", "click без element: Y на последнем скриншоте."),
                new Prop("button", "string", "click: left (по умолчанию), right или double.", Enum: ["left", "right", "double"]),
                new Prop("text", "string", "type: текст (заменяет значение поля); keys: сочетания через пробел, например \"ctrl+s enter\"; " +
                    "tree: показать только элементы с этим текстом."),
                new Prop("mode", "string", "type: заменить содержимое (по умолчанию) или вставить в позицию курсора.", Enum: ["replace", "insert"]),
                new Prop("state", "string", "wait: условие для окна или элемента. Для value укажи точное ожидаемое значение в text.",
                    Enum: ["visible", "gone", "enabled", "disabled", "checked", "unchecked", "selected", "value"]),
                new Prop("seconds", "integer", "wait: предел ожидания (по умолчанию 10, максимум 60).")),
            Handler = new ComputerTools().RunAsync,
            Confirmation = (a, c) => a.Optional("action") == "launch"
                ? new ConfirmRequest("Запустить программу?", $"{a.Required("path")} {a.Optional("arguments")}".Trim(), "Запустить", Danger: true)
                : null,
        },
    ];

    async Task<string> RunAsync(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var action = a.Required("action");
        await _actions.WaitAsync(ct);
        try
        {
            return action switch
            {
                "launch" => await LaunchAsync(a, c, ct),
                "windows" => Windows(),
                "wait" => await WaitAsync(a, ct),
                "tree" => Tree(Target(a.Optional("window")), a.Optional("text")),
                "screenshot" => Screenshot(Target(a.Optional("window")), c),
                "click" or "type" or "keys" or "scroll" or "close" => await AfterAsync(Target(a.Optional("window")), action, a, ct),
                _ => throw new ToolException($"неизвестное действие '{action}'."),
            };
        }
        catch (ElementNotAvailableException) { throw new ToolException("элемент исчез или окно закрылось — запроси tree заново."); }
        catch (InvalidOperationException ex) { throw new ToolException("элемент не поддерживает это действие: " + ex.Message); }
        finally { _actions.Release(); }
    }

    async Task<string> LaunchAsync(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        string path = a.Required("path"), arguments = a.Optional("arguments") ?? "";
        foreach (var part in arguments.Split([' ', '"'], StringSplitOptions.RemoveEmptyEntries).Append(arguments).Append(path))
            if (Existing(part, c) is { } full)
            {
                c.GuardSecrets(full);
                if (Environment.ProcessPath is { } self && PathHelper.SamePath(PathHelper.FinalPath(full), PathHelper.FinalPath(self))) throw OwnWindow();
            }
        if (Existing(path, c) != null) path = c.Resolve(path);
        var foreground = GetForegroundWindow();
        var before = TopWindows().Select(w => w.Handle).ToHashSet();
        Use(IntPtr.Zero);
        _launched?.Dispose();
        var process = _launched = Process.Start(new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = true, WorkingDirectory = Path.IsPathFullyQualified(path) && Path.GetDirectoryName(path) is { } dir ? dir : c.WorkingDirectory,
        });
        for (var clock = Stopwatch.StartNew(); clock.Elapsed.TotalSeconds < 30; await Task.Delay(300, ct))
        {
            var windows = TopWindows();
            var matches = windows.Where(w => process is { HasExited: false } && w.Pid == process.Id).ToList();
            var found = matches.Count == 1 ? matches[0] : matches.FirstOrDefault(w => w.Handle == process?.MainWindowHandle);
            if (found.Handle == IntPtr.Zero && Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var active = GetForegroundWindow();
                found = windows.FirstOrDefault(w => w.Handle == active && active != foreground && before.Contains(active) && SameExecutable(w.Pid, path));
            }
            if (found.Handle != IntPtr.Zero)
            {
                Use(found.Handle);
                return $"Запущено: «{found.Title}» (окно {Id(found.Handle)}, процесс {ProcessName(found.Pid)}, PID {found.Pid})\n" + Tree(found.Handle, null);
            }
            if ((process == null || process.HasExited) && clock.Elapsed.TotalSeconds > 2) break;
            process?.Refresh();
        }
        var status = process is { HasExited: true } ? $"Процесс завершился с кодом {process.ExitCode}." : "Запрос запуска уже отправлен.";
        throw new ToolException(status + " Окно запуска не определено. Выбери подходящее окно по id.\n" + Windows());
    }

    static string? Existing(string part, ToolContext c)
    {
        try { return c.Resolve(part) is var full && Path.Exists(full) ? full : null; }
        catch (ToolException) { return null; }
    }

    string Windows()
    {
        var windows = TopWindows();
        return windows.Count == 0 ? "Видимых окон других программ нет." : "Окна (id — заголовок — процесс):\n" + string.Join("\n",
            windows.Select(w => $"{(w.Handle == _window ? "* " : "")}{Id(w.Handle)} — {AgentRunner.Clip(w.Title, 90)} — {ProcessName(w.Pid)}"));
    }

    async Task<string> WaitAsync(ToolArgs a, CancellationToken ct)
    {
        int seconds = Math.Clamp(a.Int("seconds", 10), 1, 60);
        string? window = a.Optional("window"), query = a.Optional("element");
        var state = a.Optional("state") ?? "visible";
        if (query == null && state is not ("visible" or "gone")) throw new ToolException("для этого состояния укажи element.");
        var expected = state == "value" ? a.Required("text", allowEmpty: true) : null;
        if (window == null && query == null && a.Optional("state") == null)
        {
            await Task.Delay(seconds * 1000, ct);
            return $"Прошло {seconds} с.\n" + State(Target(null));
        }
        if (window == null && _window == IntPtr.Zero) throw new ToolException("укажи окно из списка windows.");
        var clock = Stopwatch.StartNew();
        long nextPartial = 0;
        AutomationElement? element = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var handle = ResolveWindow(window);
            if (handle == IntPtr.Zero && state == "gone") return "Окно отсутствует.";
            if (handle != IntPtr.Zero)
            {
                if (handle != _window) element = null;
                Use(handle);
                try
                {
                    bool partial = clock.ElapsedMilliseconds >= nextPartial;
                    if (partial) nextPartial = clock.ElapsedMilliseconds + 2000;
                    if (query != null && element == null) element = Find(handle, query, partial: partial);
                    bool visible = query == null || element != null && !element.Current.IsOffscreen;
                    bool reached = state switch
                    {
                        "visible" => visible, "gone" => !visible && (element != null || partial),
                        "enabled" => visible && element!.Current.IsEnabled, "disabled" => visible && !element!.Current.IsEnabled,
                        "checked" or "unchecked" => visible && element!.GetCurrentPropertyValue(TogglePattern.ToggleStateProperty, true)
                            is ToggleState toggle && toggle == (state == "checked" ? ToggleState.On : ToggleState.Off),
                        "selected" => visible && element!.GetCurrentPropertyValue(SelectionItemPattern.IsSelectedProperty, true) is true,
                        "value" => visible && !element!.Current.IsPassword && element.GetCurrentPropertyValue(ValuePattern.ValueProperty, true) as string == expected,
                        _ => throw new ToolException("неизвестное условие ожидания."),
                    };
                    if (reached) return $"Условие {state} выполнено: {query ?? TitleOf(handle)}.";
                    if (!visible || state == "gone") element = null;
                }
                catch (ElementNotAvailableException) { element = null; }
            }
            await Task.Delay(300, ct);
        } while (clock.Elapsed.TotalSeconds < seconds);
        throw new ToolException($"за {seconds} с условие {state} не выполнено: {query ?? window}.");
    }

    IntPtr ResolveWindow(string? window)
    {
        if (window == null || window.Equals(Id(_window), StringComparison.OrdinalIgnoreCase))
            return IsWindowVisible(_window) && PidOf(_window) == _pid ? _window : IntPtr.Zero;
        var windows = TopWindows();
        if (window.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(window[2..], NumberStyles.HexNumber, null, out var raw))
            return windows.FirstOrDefault(w => w.Handle == (IntPtr)raw).Handle;
        var matches = windows.Where(w => w.Title.Contains(window, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) matches = windows.Where(w => ProcessName(w.Pid).Equals(window, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new ToolException("подходят несколько окон — укажи id из windows.");
        return matches.FirstOrDefault().Handle;
    }

    IntPtr Target(string? window)
    {
        var found = ResolveWindow(window);
        if (found == IntPtr.Zero) throw new ToolException("окно не найдено — укажи id из windows.");
        Use(found);
        return found;
    }

    void Use(IntPtr window)
    {
        int pid = window == IntPtr.Zero ? 0 : PidOf(window);
        if (window != _window || pid != _pid)
        {
            _shot = null;
            _refs.Clear();
        }
        _window = window;
        _pid = pid;
    }

    string Tree(IntPtr window, string? filter)
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
                    refs[++_nextRef] = child;
                    lines.Add($"{new string(' ', filter == null ? depth * 2 : 0)}#{_nextRef} {line}");
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

    static IEnumerable<AutomationElement> Flatten(AutomationElement root, bool offscreen = false) =>
        root.CachedChildren.Cast<AutomationElement>().Where(e => offscreen || !e.Cached.IsOffscreen).SelectMany(e => Flatten(e, offscreen).Prepend(e));

    static string? Describe(AutomationElement e)
    {
        var info = e.Cached;
        var name = AgentRunner.Clip(info.Name.ReplaceLineEndings(" ").Trim(), 80);
        var value = e.Cached.IsPassword ? null : e.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string;
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

    AutomationElement? Find(IntPtr window, string query, bool offscreen = false, bool partial = true)
    {
        if (query.StartsWith('#') && int.TryParse(query[1..], out var number))
        {
            if (!_refs.TryGetValue(number, out var reference)) throw new ToolException($"ссылка {query} устарела — запроси tree этого окна.");
            try { return BelongsTo(reference, window) && (offscreen || !reference.Current.IsOffscreen) ? reference : null; }
            catch (ElementNotAvailableException) { return null; }
        }
        var root = AutomationElement.FromHandle(window);
        var exact = new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, query, PropertyConditionFlags.IgnoreCase),
            new PropertyCondition(AutomationElement.NameProperty, query, PropertyConditionFlags.IgnoreCase));
        System.Windows.Automation.Condition condition = offscreen ? exact : new AndCondition(exact, new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
        var inView = new AndCondition(condition, Automation.ControlViewCondition);
        var found = root.FindFirst(TreeScope.Descendants, inView);
        if (found != null && !found.Current.IsEnabled)
            return root.FindFirst(TreeScope.Descendants, new AndCondition(inView, new PropertyCondition(AutomationElement.IsEnabledProperty, true))) ?? found;
        if (found != null || !partial) return found;
        return Flatten(Snapshot(window), offscreen).FirstOrDefault(e => e.Cached.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    static bool BelongsTo(AutomationElement? element, IntPtr window)
    {
        for (; element != null; element = TreeWalker.RawViewWalker.GetParent(element))
            if (element.Current.NativeWindowHandle is not 0 and var handle) return GetAncestor((IntPtr)handle, 2) == window;
        return false;
    }

    async Task<string> AfterAsync(IntPtr window, string action, ToolArgs a, CancellationToken ct)
    {
        if (!IsWindowEnabled(window))
            throw new ToolException("окно заблокировано. Выбери активный диалог из windows.");
        var element = a.Optional("element") is { } query
            ? Find(window, query, action == "scroll") ?? throw new ToolException($"элемент «{query}» не найден в этом окне.")
            : null;
        if (element != null && (!BelongsTo(element, window) || IsOwn(element.Current.ProcessId)))
            throw new ToolException("элемент больше не принадлежит выбранному окну — обнови tree.");
        if (element != null && action is ("click" or "keys" or "type") && !element.Current.IsEnabled)
            throw new ToolException("выбранный элемент сейчас недоступен.");
        string done;
        try
        {
            done = action switch
            {
                "click" => await ClickAsync(window, element, a.Optional("button") ?? "left", a, ct),
                "type" => Type(window, element, a.Required("text", allowEmpty: true), a.Optional("mode") ?? "replace"),
                "keys" => Keys(window, element, a.Required("text")),
                "scroll" => Scroll(element ?? throw new ToolException("для прокрутки укажи element.")),
                _ => Close(window),
            };
        }
        finally { _shot = null; }
        await Task.Delay(action == "close" ? 500 : 180, ct);
        return done + "\n" + State(window);
    }

    static string Scroll(AutomationElement element)
    {
        if (!element.Current.IsOffscreen) return "Элемент уже виден: " + Label(element);
        if (!element.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var pattern))
            throw new ToolException("программа не поддерживает прокрутку к этому элементу через UI Automation.");
        ((ScrollItemPattern)pattern).ScrollIntoView();
        return element.Current.IsOffscreen ? "Команда прокрутки отправлена; видимость проверь через wait." : "Элемент виден: " + Label(element);
    }

    static string Label(AutomationElement e) => e.Current.Name is { Length: > 0 } name ? $"«{AgentRunner.Clip(name, 60)}»"
        : e.Current.AutomationId is { Length: > 0 } id ? id : e.Current.LocalizedControlType;

    async Task<string> ClickAsync(IntPtr window, AutomationElement? element, string button, ToolArgs a, CancellationToken ct)
    {
        if (element != null && button == "left")
        {
            object? pattern = null;
            foreach (var kind in new[] { TogglePattern.Pattern, SelectionItemPattern.Pattern, ExpandCollapsePattern.Pattern, InvokePattern.Pattern })
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
                var label = Label(element);
                var task = Task.Run(act, ct);
                try { await task.WaitAsync(TimeSpan.FromSeconds(2), ct); }
                catch (TimeoutException)
                {
                    _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    return $"Действие отправлено: {label}. Завершение пока не подтверждено.";
                }
                try
                {
                    return pattern switch
                    {
                        TogglePattern toggle => label + (toggle.Current.ToggleState switch
                        {
                            ToggleState.On => ": отмечен.", ToggleState.Off => ": не отмечен.", _ => ": частично отмечен.",
                        }),
                        SelectionItemPattern selection => label + (selection.Current.IsSelected ? ": выбран." : ": не выбран."),
                        ExpandCollapsePattern expand => $"{label}: {expand.Current.ExpandCollapseState}.",
                        _ => $"Действие передано элементу {label}.",
                    };
                }
                catch (ElementNotAvailableException) { return $"Действие передано, элемент {label} исчез."; }
            }
        }
        Activate(window);
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
        {
            if (x >= shot.Width || y >= shot.Height || !GetWindowRect(window, out var rect) || !rect.Equals(shot.Window))
                throw new ToolException("координаты вне актуального снимка или окно переместилось — обнови screenshot.");
            point = new(shot.Left + x / shot.Scale, shot.Top + y / shot.Scale);
        }
        else throw new ToolException("укажи element или сначала сделай screenshot этого окна и передай x, y по нему.");
        var targetPoint = new NativePoint { X = (int)point.X, Y = (int)point.Y };
        if (GetAncestor(WindowFromPoint(targetPoint), 2) != window || !SetCursorPos(targetPoint.X, targetPoint.Y) ||
            !GetCursorPos(out var cursor) || !cursor.Equals(targetPoint) || GetAncestor(WindowFromPoint(cursor), 2) != window)
            throw new ToolException("точка недоступна в выбранном окне — обнови screenshot или tree.");
        var (down, up) = button == "right" ? (0x8, 0x10) : (0x2, 0x4);
        var labelAtPoint = element != null ? Label(element) : $"точке {a.Int("x", 0)}, {a.Int("y", 0)} снимка";
        Send(Enumerable.Repeat(new[] { Mouse(down), Mouse(up) }, button == "double" ? 2 : 1).SelectMany(x => x).ToArray());
        return $"Щелчок ({button}) отправлен по {labelAtPoint}.";
    }

    static string Type(IntPtr window, AutomationElement? element, string text, string mode)
    {
        if (mode is not ("replace" or "insert")) throw new ToolException("неизвестный режим ввода.");
        if (element == null) element = Focus(window, null);
        if (element.Current.IsPassword) throw new ToolException("ввод в поле пароля недоступен.");
        var value = element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? (ValuePattern)pattern : null;
        if (value?.Current.IsReadOnly == true) throw new ToolException("поле доступно только для чтения.");
        if (mode == "replace" && value != null) value.SetValue(text);
        else
        {
            if (mode == "replace" && element.Current.ControlType != ControlType.Edit && element.Current.ControlType != ControlType.Document)
                throw new ToolException("не удалось подтвердить, что это текстовое поле. Для ввода в позицию курсора используй mode=insert.");
            Focus(window, element);
            if (mode == "replace")
            {
                Send([Keyboard(0x11, 0), .. Key(0x41), Keyboard(0x11, 2)]);
                RequireFocus(window, element);
            }
            if (text.Length > 0) Send(text.ReplaceLineEndings("\n").SelectMany(ch => ch == '\n' ? Key(0x0D) : [Unicode(ch, 0), Unicode(ch, 2)]).ToArray());
            else if (mode == "replace") Send(Key(0x08));
            if (mode == "insert") return "Вставка отправлена в выбранное поле.";
        }
        string? actual = value?.Current.Value;
        if (actual == null && element.TryGetCurrentPattern(TextPattern.Pattern, out var readable))
            actual = ((TextPattern)readable).DocumentRange.GetText(text.Length + 4).TrimEnd('\r');
        if (actual == null) return "Замена отправлена; программа не предоставляет значение поля для проверки.";
        if (actual.ReplaceLineEndings() != text.ReplaceLineEndings()) throw new ToolException("после замены значение поля отличается от ожидаемого.");
        return "Содержимое поля заменено и проверено.";
    }

    static string Keys(IntPtr window, AutomationElement? element, string keys)
    {
        Focus(window, element);
        foreach (var chord in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var codes = chord.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(VirtualKey).ToArray();
            RequireFocus(window, element);
            Send([.. codes.Select(code => Keyboard(code, 0)), .. codes.Reverse().Select(code => Keyboard(code, 2))]);
            element = null;
            Thread.Sleep(60);
        }
        return "Нажаты клавиши: " + keys;
    }

    static string Close(IntPtr window)
    {
        if (!PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero)) throw new ToolException("Windows не передала окну команду закрытия.");
        return $"Окну «{TitleOf(window)}» отправлена команда закрыться.";
    }

    static AutomationElement Focus(IntPtr window, AutomationElement? element)
    {
        Activate(window);
        if (element != null && !element.Current.HasKeyboardFocus) element.SetFocus();
        return RequireFocus(window, element);
    }

    static AutomationElement RequireFocus(IntPtr window, AutomationElement? element)
    {
        var focused = AutomationElement.FocusedElement;
        if (!IsWindowEnabled(window) || GetForegroundWindow() != window || !BelongsTo(focused, window) || element != null && !Automation.Compare(element, focused))
            throw new ToolException("фокус ввода вне выбранной цели — обнови tree.");
        return focused;
    }

    static void Activate(IntPtr window)
    {
        if (IsIconic(window)) ShowWindow(window, 9);
        if (GetForegroundWindow() == window) return;
        SetForegroundWindow(window);
        if (!SpinWait.SpinUntil(() => GetForegroundWindow() == window, 250))
            throw new ToolException("Windows не передала фокус выбранному окну.");
    }

    string State(IntPtr window)
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

    string Screenshot(IntPtr window, ToolContext c)
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
        _shot = (rect, rect.Left + left, rect.Top + top, shot.PixelWidth, shot.PixelHeight, scale);
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
        return pid == Environment.ProcessId || Environment.ProcessPath is { } self && SameExecutable(pid, self);
    }

    static bool SameExecutable(int pid, string path)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName is { } file && PathHelper.SamePath(file, path);
        }
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
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr window);
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
    [DllImport("user32.dll")] static extern bool GetCursorPos(out NativePoint point);
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
