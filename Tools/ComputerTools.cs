using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SharpAgent.Tools;

public sealed class ComputerTools(bool automationOnly = false)
{
    public const string Name = "computer", AppName = "app", ShotPrefix = "Скриншот сохранён: ";
    static readonly string[] AppActions = ["launch", "windows", "tree", "click", "type", "scroll", "wait", "close"];
    internal static bool IsControl(string name) => name is Name or AppName;
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
    IntPtr _window, _browserWindow;
    int _pid, _nextRef;
    Process? _launched;
    Dictionary<int, AutomationElement> _refs = [];
    (Rect Window, int Left, int Top, int Width, int Height, double Scale)? _shot;
    readonly HashSet<int> _started = [], _trusted = [];
    int _asked;
    CancellationToken _run;

    public static IEnumerable<AgentTool> All()
    {
        var tools = new ComputerTools();
        var computer = new AgentTool
        {
            Name = Name,
            Description = "Управление программами на экране через UI Automation: запуск, окна, дерево элементов, нажатия, ввод текста, " +
                "клавиши, скриншот. Работает с WPF, WinForms, Win32, Qt, UWP и другими; если элементов нет в дереве — щёлкай по координатам " +
                "со скриншота. После действий программа сама сообщает заголовок окна, до трёх его открытых диалогов с их текстом, открытое меню и завершение процесса. " +
                "Сначала смотри tree — это дёшево; screenshot делай только для проверки внешнего вида или когда дерево пустое.",
            Parameters = Schema.Object(
                new Prop("action", "string", "Действие.", Required: true,
                    Enum: ["launch", "windows", "tree", "click", "type", "keys", "scroll", "screenshot", "wait", "close"]),
                new Prop("path", "string", "launch: программа, ярлык или файл."),
                new Prop("arguments", "string", "launch: аргументы командной строки."),
                new Prop("window", "string", "Окно: id из windows/launch, browser для встроенного браузера или часть заголовка. По умолчанию — текущее окно."),
                new Prop("element", "string", "Элемент: #номер из последнего tree либо имя или AutomationId. Для scroll укажи элемент, который нужно показать."),
                new Prop("x", "integer", "click без element: X на последнем скриншоте."),
                new Prop("y", "integer", "click без element: Y на последнем скриншоте."),
                new Prop("button", "string", "click: left (по умолчанию), right или double.", Enum: ["left", "right", "double"]),
                new Prop("text", "string", "type: текст (заменяет значение поля); keys: сочетания через пробел, например \"ctrl+a ctrl+c\"; " +
                    "tree: показать только элементы с этим текстом."),
                new Prop("mode", "string", "type: заменить содержимое (по умолчанию) или вставить в позицию курсора.", Enum: ["replace", "insert"]),
                new Prop("state", "string", "wait: условие для окна или элемента. Для value укажи точное ожидаемое значение в text.",
                    Enum: ["visible", "gone", "enabled", "disabled", "checked", "unchecked", "selected", "value"]),
                new Prop("seconds", "integer", "wait: предел ожидания (по умолчанию 10, максимум 60).")),
            Handler = tools.RunAsync,
            Confirmation = tools.Confirm,
        };
        var app = new ComputerTools(automationOnly: true);
        var parameters = (JsonObject)computer.Parameters.DeepClone();
        var properties = (JsonObject)parameters["properties"]!;
        properties["action"]!["enum"] = new JsonArray(AppActions.Select(action => (JsonNode?)JsonValue.Create(action)).ToArray());
        foreach (var key in new[] { "x", "y", "button", "mode" }) properties.Remove(key);
        properties["text"]!["description"] = "type: новое значение поля через ValuePattern; tree: фильтр элементов; wait: ожидаемое значение поля.";
        return [computer, new AgentTool
        {
            Name = AppName,
            Description = "Проверка приложения через API UI Automation: запуск, дерево элементов, Invoke/Toggle/Select, замена значения поля, ожидание, прокрутка и закрытие. " +
                "Не эмулирует мышь или клавиатуру. Для click/type обязательно укажи element из tree. Если приложение не поддерживает нужный API, используй computer.",
            Parameters = parameters, Handler = app.RunAsync, Confirmation = app.Confirm,
        }];
    }

    ConfirmRequest? Confirm(ToolArgs a, ToolContext c)
    {
        _browserWindow = c.BrowserWindow?.Invoke() ?? IntPtr.Zero;
        var action = a.Optional("action");
        if (action == "launch")
            return new("Запустить программу?", $"{a.Required("path")} {a.Optional("arguments")}".Trim(), "Запустить", Danger: true);
        if (action is not ("click" or "type" or "keys" or "scroll" or "close") || ResolveWindow(a.Optional("window")) is var window && window == IntPtr.Zero)
            return null;
        var pid = PidOf(window);
        if (IsBrowserWindow(window) || Allowed(action, pid)) return null;
        _asked = pid;
        var subject = $"«{TitleOf(window)}» — {ProcessName(pid)}";
        return action == "close"
            ? new("Закрыть окно другой программы?", subject, "Закрыть", Danger: true, Note: "Несохранённые данные в этом окне могут пропасть.")
            : new("Управлять другой программой?", subject, "Разрешить", Danger: true,
                Note: "До конца этого запуска агент сможет нажимать, вводить текст и отправлять сочетания клавиш в окна этой программы.");
    }

    // Programs this run launched are trusted; the embedded browser is part of this run.
    bool Allowed(string action, int pid) => IsBrowserWindow(_window) || _started.Contains(pid) || action != "close" && _trusted.Contains(pid);

    async Task<string> RunAsync(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var action = a.Required("action");
        if (automationOnly && !AppActions.Contains(action)) throw new ToolException("это действие недоступно через API приложения — используй computer.");
        await _actions.WaitAsync(ct);
        // UI Automation, ShowWindow and PrintWindow block on a hung program; Stop must not wait for them, the next action does.
        var work = Task.Run(() => DispatchAsync(action, a, c, ct));
        _ = work.ContinueWith(t =>
        {
            _ = t.Exception;
            _actions.Release();
        }, TaskScheduler.Default);
        return await work.WaitAsync(ct);
    }

    async Task<string> DispatchAsync(string action, ToolArgs a, ToolContext c, CancellationToken ct)
    {
        try
        {
            _browserWindow = c.BrowserWindow?.Invoke() ?? IntPtr.Zero;
            if (ct != _run)
            {
                _run = ct;
                _started.Clear();
                _trusted.Clear();
            }
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
        catch (ElementNotEnabledException) { throw new ToolException("выбранный элемент сейчас недоступен."); }
        catch (InvalidOperationException) { throw new ToolException("элемент не поддерживает это действие."); }
    }

    async Task<string> LaunchAsync(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        string path = a.Required("path"), arguments = a.Optional("arguments") ?? "";
        var program = PathPart(path, c.WorkingDirectory, c);
        path = program ?? path;
        var folder = Path.IsPathFullyQualified(path) && Path.GetDirectoryName(path) is { } dir ? dir : c.WorkingDirectory;
        foreach (var full in arguments.Split([' ', '"'], StringSplitOptions.RemoveEmptyEntries).SelectMany(part => part.Split('=', 2)).Append(arguments)
                     .Select(part => PathPart(part, folder, c)).Append(program).OfType<string>())
        {
            c.GuardSecrets(full);
            c.GuardAppFiles(full);
            if (Environment.ProcessPath is { } self && PathHelper.SamePath(PathHelper.FinalPath(full), PathHelper.FinalPath(self))) throw OwnWindow();
        }
        var foreground = GetForegroundWindow();
        var before = TopWindows().Select(w => w.Handle).ToHashSet();
        Use(IntPtr.Zero);
        _launched?.Dispose();
        _launched = null;
        var since = DateTime.Now;
        Process? process;
        try { process = _launched = Process.Start(new ProcessStartInfo(path, arguments) { UseShellExecute = true, WorkingDirectory = folder }); }
        catch (Win32Exception ex) { throw new ToolException($"не удалось запустить «{path}»: {new Win32Exception(ex.NativeErrorCode).Message}"); }
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
            // Packaged and single-instance apps, explorer and dotnet run open their window in another process.
            if (found.Handle == IntPtr.Zero && (process is not { HasExited: false } || clock.Elapsed.TotalSeconds > 2) &&
                windows.Where(w => !before.Contains(w.Handle)).ToList() is { Count: > 0 } fresh)
                found = fresh.Count == 1 ? fresh[0] : fresh.FirstOrDefault(w => w.Handle == GetForegroundWindow());
            if (found.Handle != IntPtr.Zero)
            {
                // A program that was already running is not trusted just because launch surfaced its window.
                if (StartedSince(found.Pid, since)) _started.Add(found.Pid);
                GuardTitle(found.Handle);
                Use(found.Handle);
                return $"Запущено: «{found.Title}» (окно {Id(found.Handle)}, процесс {ProcessName(found.Pid)}, PID {found.Pid})\n" + Tree(found.Handle, null);
            }
            if ((process == null || process.HasExited) && clock.Elapsed.TotalSeconds > 5) break;
            process?.Refresh();
        }
        var status = process is { HasExited: true } ? $"Процесс завершился с кодом {process.ExitCode}." : "Запрос запуска уже отправлен.";
        throw new ToolException(status + " Окно запуска не определено. Выбери подходящее окно по id.\n" + Windows());
    }

    // Arguments are resolved from the folder the program starts in; network paths are refused by c.Resolve.
    static string? PathPart(string part, string folder, ToolContext c)
    {
        string full;
        try { full = PathHelper.Resolve(part, folder); }
        catch (ToolException) { return null; }
        return Path.Exists(full) || part.IndexOfAny(['\\', '/']) >= 0 || Path.IsPathRooted(part) ? c.Resolve(full) : null;
    }

    string Windows()
    {
        var windows = TopWindows();
        var lines = windows.Select(w => $"{(w.Handle == _window ? "* " : "")}{Id(w.Handle)} — {AgentRunner.Clip(w.Title, 90)} — {ProcessName(w.Pid)}").ToList();
        if (IsBrowserWindow(_browserWindow)) lines.Insert(0, $"{(_browserWindow == _window ? "* " : "")}browser — встроенный браузер Sharp Agent — {TitleOf(_browserWindow)}");
        return lines.Count == 0 ? "Видимых окон других программ нет." : "Окна (id — заголовок — процесс):\n" + string.Join("\n", lines);
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
                if (state != "gone") Use(handle);
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
        if (window?.Equals("browser", StringComparison.OrdinalIgnoreCase) == true)
            return IsBrowserWindow(_browserWindow) ? _browserWindow : IntPtr.Zero;
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
        GuardTitle(found);
        // Everything inside a minimized window reports itself as off-screen.
        if (IsIconic(found)) ShowWindow(found, 4);
        Use(found);
        return found;
    }

    static void GuardTitle(IntPtr window)
    {
        if (Regex.IsMatch(TitleOf(window), @"\.env\b|chatgpt\.json", RegexOptions.IgnoreCase))
            throw new ToolException("в этом окне открыт файл с секретами (.env с ключами или вход в ChatGPT) — смотреть и управлять им агенту нельзя.");
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
                // A full tree skips off-screen items; a text search also finds them so they can be scrolled into view.
                if (child.Cached.IsOffscreen && filter == null)
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
                    lines.Add($"{new string(' ', filter == null ? depth * 2 : 0)}#{_nextRef} {line}{(child.Cached.IsOffscreen ? " [вне экрана]" : "")}");
                }
                Walk(child, shown && filter == null ? depth + 1 : depth);
            }
        }
        Walk(Snapshot(window), 0);
        foreach (var popup in PopupRoots(window)) Walk(popup, 0);
        _refs = refs;
        var head = $"Окно «{TitleOf(window)}» ({Id(window)}). Элементы (#номер — для click/type):";
        if (lines.Count == 0)
            return head + (filter != null ? $"\nЭлементов с текстом «{filter}» нет."
                : "\nДерево пусто: программа не сообщает элементы UI Automation (игра или своя отрисовка). Сделай screenshot и щёлкай по координатам.");
        return head + "\n" + string.Join("\n", lines) + (omitted > 0 ? $"\n… ещё {omitted} элементов — сузь поиск параметром text." : "") +
               (hidden > 0 ? $"\n(вне экрана пропущено: {hidden}; найди нужный через text и покажи через scroll)" : "");
    }

    static AutomationElement Snapshot(IntPtr window)
    {
        var request = new CacheRequest { TreeScope = TreeScope.Subtree, TreeFilter = Automation.ControlViewCondition };
        foreach (var property in Cached) request.Add(property);
        return AutomationElement.FromHandle(window).GetUpdatedCache(request);
    }

    // Open menus and lists that UI Automation does not already place inside the window's own tree.
    static IEnumerable<AutomationElement> PopupRoots(IntPtr window)
    {
        foreach (var popup in Popups(window))
        {
            AutomationElement? root = null;
            try
            {
                if (Automation.Compare(TreeWalker.ControlViewWalker.GetParent(AutomationElement.FromHandle(popup)), AutomationElement.RootElement))
                    root = Snapshot(popup);
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or ArgumentException) { }
            if (root != null) yield return root;
        }
    }

    static IEnumerable<AutomationElement> Flatten(AutomationElement root, bool offscreen = false) =>
        root.CachedChildren.Cast<AutomationElement>().Where(e => offscreen || !e.Cached.IsOffscreen).SelectMany(e => Flatten(e, offscreen).Prepend(e));

    static string? Describe(AutomationElement e)
    {
        var info = e.Cached;
        var rawName = info.Name ?? "";
        var name = AgentRunner.Clip(rawName.ReplaceLineEndings(" ").Trim(), 80);
        var id = info.AutomationId ?? "";
        var type = info.ControlType ?? ControlType.Custom;
        var value = info.IsPassword ? null : e.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string;
        if (name.Length == 0 && id.Length == 0 && string.IsNullOrEmpty(value) && !Interactive.Contains(type)) return null;
        var text = new StringBuilder(type.ProgrammaticName["ControlType.".Length..]);
        if (name.Length > 0) text.Append($" \"{name}\"");
        if (id.Length > 0 && id != rawName) text.Append(" id=" + id);
        if (!string.IsNullOrEmpty(value) && value != rawName) text.Append($" = \"{AgentRunner.Clip(value.ReplaceLineEndings(" "), 80)}\"");
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
        var exact = new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, query, PropertyConditionFlags.IgnoreCase),
            new PropertyCondition(AutomationElement.NameProperty, query, PropertyConditionFlags.IgnoreCase));
        System.Windows.Automation.Condition condition = offscreen ? exact : new AndCondition(exact, new PropertyCondition(AutomationElement.IsOffscreenProperty, false));
        var inView = new AndCondition(condition, Automation.ControlViewCondition);
        foreach (var root in PopupRoots(window).Prepend(AutomationElement.FromHandle(window)))
            if (root.FindFirst(TreeScope.Descendants, inView) is { } found)
                return found.Current.IsEnabled ? found
                    : root.FindFirst(TreeScope.Descendants, new AndCondition(inView, new PropertyCondition(AutomationElement.IsEnabledProperty, true))) ?? found;
        if (!partial) return null;
        var matches = PopupRoots(window).Prepend(Snapshot(window)).SelectMany(root => Flatten(root, offscreen))
            .Where(e => e.Cached.Name?.Contains(query, StringComparison.OrdinalIgnoreCase) == true).Take(5).ToList();
        return matches.Count <= 1 ? matches.FirstOrDefault() : throw new ToolException(
            $"«{query}» есть в названиях нескольких элементов: {string.Join("; ", matches.Select(Describe))} — укажи #номер из tree или точное имя.");
    }

    bool BelongsTo(AutomationElement? element, IntPtr window)
    {
        for (; element != null; element = TreeWalker.RawViewWalker.GetParent(element))
            if (element.Current.NativeWindowHandle is not 0 and var handle && Related((IntPtr)handle, window)) return true;
        return false;
    }

    // Menus and drop-down lists are separate untitled pop-up windows of the target's GUI thread.
    bool Related(IntPtr handle, IntPtr window) => IsDescendantOf(handle, window) || Popups(window).Contains(GetAncestor(handle, 2));

    static bool IsDescendantOf(IntPtr handle, IntPtr window)
    {
        for (var current = handle; current != IntPtr.Zero; current = GetAncestor(current, 1))
            if (current == window) return true;
        return false;
    }

    async Task<string> AfterAsync(IntPtr window, string action, ToolArgs a, CancellationToken ct)
    {
        var pid = PidOf(window);
        if (!Allowed(action, pid) && pid != _asked)
            throw new ToolException("действие не выполнено: для этой программы нужно подтверждение пользователя — повтори вызов.");
        if (action != "close") _trusted.Add(pid);
        _asked = 0;
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

    static string? Observed(object? pattern) => pattern switch
    {
        TogglePattern toggle => toggle.Current.ToggleState switch { ToggleState.On => "отмечен", ToggleState.Off => "не отмечен", _ => "частично отмечен" },
        SelectionItemPattern selection => selection.Current.IsSelected ? "выбран" : "не выбран",
        ExpandCollapsePattern expand => expand.Current.ExpandCollapseState switch
        {
            ExpandCollapseState.Expanded => "раскрыт", ExpandCollapseState.Collapsed => "свёрнут",
            ExpandCollapseState.PartiallyExpanded => "раскрыт частично", _ => "без вложенных элементов",
        },
        _ => null,
    };

    static string Label(AutomationElement e) => e.Current.Name is { Length: > 0 } name ? $"«{AgentRunner.Clip(name, 60)}»"
        : e.Current.AutomationId is { Length: > 0 } id ? id : e.Current.LocalizedControlType;

    async Task<string> ClickAsync(IntPtr window, AutomationElement? element, string button, ToolArgs a, CancellationToken ct)
    {
        if (element != null && button == "left")
        {
            object? pattern = null;
            AutomationPattern[] kinds = [TogglePattern.Pattern, SelectionItemPattern.Pattern, ExpandCollapsePattern.Pattern, InvokePattern.Pattern];
            // A split button's and a menu item's own command is Invoke; Toggle on a WPF menu item only flips its check mark.
            if (element.Current.ControlType is var type && (type == ControlType.SplitButton || type == ControlType.MenuItem)) kinds = [InvokePattern.Pattern, .. kinds];
            foreach (var kind in kinds)
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
                var before = Observed(pattern);
                var task = Task.Run(act, ct);
                try { await task.WaitAsync(TimeSpan.FromSeconds(2), ct); }
                catch (TimeoutException)
                {
                    _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    return $"Действие отправлено: {label}. Завершение пока не подтверждено.";
                }
                try
                {
                    if (before == null) return $"Действие передано элементу {label}.";
                    bool Confirmed(string? state) => pattern is SelectionItemPattern ? state == "выбран" : state != before;
                    // Chromium, Electron and WebView2 apply the change asynchronously.
                    var after = Observed(pattern);
                    for (var clock = Stopwatch.StartNew(); !Confirmed(after) && clock.ElapsedMilliseconds < 1000; after = Observed(pattern))
                        await Task.Delay(100, ct);
                    return Confirmed(after) ? $"{label}: {after}." : $"{label}: отправлено, не подтверждено (сейчас {after}).";
                }
                catch (ElementNotAvailableException) { return $"Действие передано, элемент {label} исчез."; }
            }
        }
        if (automationOnly) throw new ToolException("элемент не поддерживает действие через UI Automation. Для управления мышью используй computer.");
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
        var targetPoint = new System.Drawing.Point((int)point.X, (int)point.Y);
        var covers = new List<IntPtr>();
        try
        {
            // Own layered windows over the point (the mini window is hidden from screenshots) let this click pass through.
            for (var hit = GetAncestor(WindowFromPoint(targetPoint), 2); IsOwn(PidOf(hit)) && (GetWindowLong(hit, -20) & 0x80020) == 0x80000;
                 hit = GetAncestor(WindowFromPoint(targetPoint), 2))
            {
                SetWindowLong(hit, -20, GetWindowLong(hit, -20) | 0x20);
                covers.Add(hit);
            }
            if (!Related(WindowFromPoint(targetPoint), window) || !SetCursorPos(targetPoint.X, targetPoint.Y) ||
                !GetCursorPos(out var cursor) || !cursor.Equals(targetPoint) || !Related(WindowFromPoint(cursor), window))
                throw new ToolException(IsOwn(PidOf(WindowFromPoint(targetPoint))) ? "точку закрывает окно Sharp Agent — выбери элемент по #номеру из tree."
                    : "точка недоступна в выбранном окне — обнови screenshot или tree.");
            var (down, up) = button == "right" ? (0x8, 0x10) : (0x2, 0x4);
            var labelAtPoint = element != null ? Label(element) : $"точке {a.Int("x", 0)}, {a.Int("y", 0)} снимка";
            Send(Enumerable.Repeat(new[] { Mouse(down), Mouse(up) }, button == "double" ? 2 : 1).SelectMany(x => x).ToArray());
            return $"Щелчок ({button}) отправлен по {labelAtPoint}.";
        }
        finally
        {
            // Injected input is routed asynchronously, so the windows stay click-through until it has landed.
            if (covers.Count > 0) await Task.Delay(150);
            foreach (var cover in covers) SetWindowLong(cover, -20, GetWindowLong(cover, -20) & ~0x20);
        }
    }

    string Type(IntPtr window, AutomationElement? element, string text, string mode)
    {
        if (mode is not ("replace" or "insert")) throw new ToolException("неизвестный режим ввода.");
        if (automationOnly && element == null) throw new ToolException("для ввода через API укажи element из tree.");
        element ??= Focus(window, null);
        if (element.Current.IsPassword) throw new ToolException("ввод в поле пароля недоступен.");
        var value = element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) ? (ValuePattern)pattern : null;
        if (value?.Current.IsReadOnly == true) throw new ToolException("поле доступно только для чтения.");
        if (mode == "replace" && value != null) value.SetValue(text);
        else
        {
            if (automationOnly) throw new ToolException("поле не поддерживает замену значения через API. Для ввода клавишами используй computer.");
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
        var readable = element.TryGetCurrentPattern(TextPattern.Pattern, out var found) ? (TextPattern)found : null;
        string? Actual() => value?.Current.Value ?? readable?.DocumentRange.GetText(text.Length + 4).TrimEnd('\r');
        var actual = Actual();
        for (var clock = Stopwatch.StartNew(); actual != null && actual.ReplaceLineEndings() != text.ReplaceLineEndings() && clock.ElapsedMilliseconds < 1000; actual = Actual())
            Thread.Sleep(100);
        if (actual == null) return "Замена отправлена; программа не предоставляет значение поля для проверки.";
        if (actual.ReplaceLineEndings() != text.ReplaceLineEndings())
            throw new ToolException($"после замены значение поля отличается от ожидаемого: «{AgentRunner.Clip(actual.ReplaceLineEndings(" "), 200)}».");
        return "Содержимое поля заменено и проверено.";
    }

    string Keys(IntPtr window, AutomationElement? element, string keys)
    {
        var chords = keys.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(chord => (Text: chord, Codes: chord.Split('+', StringSplitOptions.RemoveEmptyEntries).Select(VirtualKey).ToArray())).ToList();
        Focus(window, element);
        var sent = new List<string>();
        foreach (var (chord, codes) in chords)
        {
            try
            {
                if (RequireFocus(window, element).Current.IsPassword) throw new ToolException("ввод в поле пароля недоступен.");
                Send([.. codes.Select(code => Keyboard(code, 0)), .. codes.Reverse().Select(code => Keyboard(code, 2))]);
            }
            catch (ToolException ex) when (sent.Count > 0) { throw new ToolException($"{ex.Message} Уже нажато: {string.Join(" ", sent)}."); }
            sent.Add(chord);
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

    AutomationElement Focus(IntPtr window, AutomationElement? element)
    {
        Activate(window);
        if (element != null && !element.Current.HasKeyboardFocus) element.SetFocus();
        return RequireFocus(window, element);
    }

    AutomationElement RequireFocus(IntPtr window, AutomationElement? element)
    {
        var focused = AutomationElement.FocusedElement;
        if (!IsWindowEnabled(window) || GetForegroundWindow() != ActivationWindow(window) || !BelongsTo(focused, window) || element != null && !Automation.Compare(element, focused))
            throw new ToolException("фокус ввода вне выбранной цели — обнови tree.");
        return focused;
    }

    void Activate(IntPtr window)
    {
        var target = ActivationWindow(window);
        if (IsIconic(target)) ShowWindow(target, 9);
        if (GetForegroundWindow() == target) return;
        SetForegroundWindow(target);
        if (!SpinWait.SpinUntil(() => GetForegroundWindow() == target, 250))
            throw new ToolException("Windows не передала фокус выбранному окну.");
    }

    IntPtr ActivationWindow(IntPtr window) => IsBrowserWindow(window) ? GetAncestor(window, 2) : window;

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
            List<string> texts;
            // A dialog that closes while the state is read must not turn the finished action into an error.
            try
            {
                texts = [.. Flatten(Snapshot(other.Handle)).Where(e => e.Cached.ControlType == ControlType.Text || e.Cached.ControlType == ControlType.Button)
                    .Select(e => e.Cached.Name?.Trim() ?? "").Where(name => name.Length > 0).Distinct()];
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or ArgumentException) { continue; }
            state += $"\nТакже открыто окно «{other.Title}» ({Id(other.Handle)}): {AgentRunner.Clip(string.Join(" | ", texts), 400)}";
        }
        foreach (var popup in PopupRoots(window))
            if (string.Join(" | ", Flatten(popup).Select(e => e.Cached.Name?.Trim() ?? "").Where(name => name.Length > 0).Distinct()) is { Length: > 0 } items)
                state += $"\nОткрыто меню или список: {AgentRunner.Clip(items, 400)}";
        return state;
    }

    string Screenshot(IntPtr window, ToolContext c)
    {
        GetWindowRect(window, out var rect);
        if (DwmGetWindowAttribute(window, 9, out Rect frame, Marshal.SizeOf<Rect>()) != 0) frame = rect;
        var popups = Popups(window);
        var area = rect;
        foreach (var popup in popups)
            if (GetWindowRect(popup, out var bounds))
            {
                area = Union(area, bounds);
                frame = Union(frame, bounds);
            }
        var image = Stretched(window) ? null : Capture(window, popups, area, false);
        if (image == null || Blank(image))
        {
            // Activating the window would close its open menu.
            if (popups.Count == 0) Activate(window);
            image = Capture(window, popups, area, true) ?? throw new ToolException("не удалось снять окно.");
        }
        int left = Math.Max(0, frame.Left - area.Left), top = Math.Max(0, frame.Top - area.Top);
        var crop = new Int32Rect(left, top, Math.Min(frame.Right - frame.Left, image.PixelWidth - left), Math.Min(frame.Bottom - frame.Top, image.PixelHeight - top));
        BitmapSource shot = crop.Width > 0 && crop.Height > 0 ? new CroppedBitmap(image, crop) : image;
        double scale = Math.Min(1, (double)MaxShotSide / Math.Max(shot.PixelWidth, shot.PixelHeight));
        if (scale < 1) shot = new TransformedBitmap(shot, new ScaleTransform(scale, scale));
        var path = Path.Combine(c.LogsFolder ?? c.TempFolder, $"screen_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ImageEncoder.Encode(new PngBitmapEncoder(), shot));
        _shot = (rect, area.Left + left, area.Top + top, shot.PixelWidth, shot.PixelHeight, scale);
        return $"{ShotPrefix}{path}\nОкно «{TitleOf(window)}», снимок {shot.PixelWidth}×{shot.PixelHeight}. Координаты x, y для click — по этому снимку. " +
               "Изображение передаётся тебе следующим сообщением, если модель видит картинки.";
    }

    // DWM stretches a window whose DPI differs from its monitor's, while PrintWindow returns it unstretched.
    static bool Stretched(IntPtr window) => GetDpiForMonitor(MonitorFromWindow(window, 2), 0, out var dpi, out _) == 0 && GetDpiForWindow(window) != dpi;

    static Rect Union(Rect a, Rect b) =>
        new() { Left = Math.Min(a.Left, b.Left), Top = Math.Min(a.Top, b.Top), Right = Math.Max(a.Right, b.Right), Bottom = Math.Max(a.Bottom, b.Bottom) };

    static BitmapSource? Capture(IntPtr window, List<IntPtr> popups, Rect r, bool fromScreen)
    {
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width <= 0 || height <= 0) throw new ToolException("окно свёрнуто или не имеет размера.");
        IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        try
        {
            bool ok = fromScreen ? BitBlt(memory, 0, 0, width, height, screen, r.Left, r.Top, 0x40CC0020) : Print(window, memory, screen, r);
            if (ok && !fromScreen)
                foreach (var popup in Enumerable.Reverse(popups)) Print(popup, memory, screen, r);
            SelectObject(memory, old);
            // GDI leaves alpha at zero, which would make the image transparent once scaled or encoded.
            return ok ? new FormatConvertedBitmap(Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()),
                PixelFormats.Bgr32, null, 0) : null;
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // Each window is printed separately and placed at its screen position, so open menus land over the window.
    static bool Print(IntPtr window, IntPtr target, IntPtr screen, Rect area)
    {
        if (!GetWindowRect(window, out var r)) return false;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        IntPtr memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, width, height);
        var old = SelectObject(memory, bitmap);
        try { return PrintWindow(window, memory, 2) && BitBlt(target, r.Left - area.Left, r.Top - area.Top, width, height, memory, 0, 0, 0xCC0020); }
        finally
        {
            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
        }
    }

    static bool Blank(BitmapSource image)
    {
        var pixels = new int[image.PixelWidth * image.PixelHeight];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        return pixels.All(p => (p & 0xFFFFFF) == 0);
    }

    static List<IntPtr> VisibleWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((handle, _) =>
        {
            if (IsWindowVisible(handle) && !(DwmGetWindowAttribute(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0)) list.Add(handle);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static List<(IntPtr Handle, string Title, int Pid)> TopWindows() =>
        [.. VisibleWindows().Select(handle => (Handle: handle, Title: TitleOf(handle), Pid: PidOf(handle))).Where(w => w.Title.Length > 0 && !IsOwn(w.Pid))];

    // Untitled pop-ups of the same GUI thread, skipping click-through decorations such as glow frames; topmost first.
    static List<IntPtr> Popups(IntPtr window)
    {
        var thread = GetWindowThreadProcessId(window, out _);
        return [.. VisibleWindows().Where(handle => handle != window && GetWindowTextLength(handle) == 0 && GetWindowThreadProcessId(handle, out _) == thread &&
            (GetWindowLong(handle, -16) & 0x80000000) != 0 && (GetWindowLong(handle, -20) & 0x20) == 0)];
    }

    static bool StartedSince(int pid, DateTime time)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime >= time;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException) { return false; }
    }

    static bool IsOwn(int pid) => pid == Environment.ProcessId || Environment.ProcessPath is { } self && SameExecutable(pid, self);

    static bool SameExecutable(int pid, string path) => ImagePath(pid) is { } file && PathHelper.SamePath(file, path);

    static ToolException OwnWindow() => new("управлять окнами самой программы Sharp Agent нельзя.");

    bool IsBrowserWindow(IntPtr window) => window != IntPtr.Zero && window == _browserWindow && IsWindowVisible(window);

    static string ProcessName(int pid) => Path.GetFileNameWithoutExtension(ImagePath(pid)) ?? "?";

    // A limited-rights query is cheap, works for elevated processes and needs no Process object per window.
    static string? ImagePath(int pid)
    {
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref size) ? path.ToString() : null;
        }
        finally { CloseHandle(process); }
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
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(System.Drawing.Point point);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out System.Drawing.Point point);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint op);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}
