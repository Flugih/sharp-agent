using static System.Net.HttpStatusCode;

namespace SharpAgent.Agent;

public sealed record ToolCallEvent(string Id, string Name, string ArgumentsJson);
public sealed record ToolResultEvent(string Id, string Name, string Output, bool IsError, bool IsWarning = false, FileDiff? Diff = null)
{
    internal FileChange? Change { get; init; }
}

public sealed record ChatTotals(long PromptTokens, long CompletionTokens, long ContextTokens,
    IReadOnlyDictionary<(AgentProvider Provider, string Model), long> TokensByModel);

public sealed partial class AgentRunner(ToolRegistry tools, ChatStore store)
{
    public const string AttachmentsField = "attachments", MetaField = "meta";
    public const string RepeatReply = "\n\n[Программа: этот же вызов уже несколько раз вернул тот же результат. Не повторяй его — измени запрос, " +
        "сузь папку или переходи к следующему шагу.]";
    const string ElapsedField = "elapsed_ms", StopField = "stopped", SentAtField = "sent_at";
    const string TruncatedCallReply = "Ошибка: ответ оборвался на лимите токенов, аргументы вызова пришли не полностью — действие не выполнено. " +
        "Повтори его меньшими частями (например, write_file по частям с append=true).";
    const int ImageTokens = 1_000, MaxInputTokens = 80_000;

    readonly bool delegated;
    readonly List<JsonNode> _history = [];
    readonly HashSet<string> _noVisionModels = new(StringComparer.OrdinalIgnoreCase), _noEffortModels = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _toolReasoningModels = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string?> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (string Output, int Count)> _repeats = new();
    readonly Dictionary<CommandShell, CommandRestriction> _blockedShells = new();
    readonly Dictionary<ReadKey, (string Id, ToolOutcome Outcome)> _reads = new();
    readonly Dictionary<string, string> _activePlugins = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, int> _inputCaps = new(StringComparer.OrdinalIgnoreCase);
    readonly Stopwatch _turnClock = new();
    IReadOnlyList<JsonNode> _sent = [];
    JsonObject? _systemMessage;
    string? _systemKey, _resumeContext, _model;
    bool _saveFailed;
    IChatProvider? _api;

    AgentRunner(ToolRegistry tools, ChatStore store, bool delegated) : this(tools, store) => this.delegated = delegated;

    public IChatProvider? Api
    {
        get => _api;
        set
        {
            if (_api != null && !delegated) _api.Retrying -= OnRetrying;
            _api = value;
            _noVisionModels.Clear();
            _noEffortModels.Clear();
            _toolReasoningModels.Clear();
            if (_api != null && !delegated) _api.Retrying += OnRetrying;
        }
    }

    public AgentProvider Provider { get; set; }
    public int? MaxTokens { get; set; }
    public string? ReasoningEffort { get; set; }
    public IReadOnlyList<JsonNode> History => _history;
    public ChatStore Store => store;
    public string? ChatPath { get; private set; }
    public string? Title { get; private set; }
    public Func<ConfirmRequest, Task<bool>>? ConfirmAsync { get; set; }
    public Func<string, bool?>? ModelSupportsVision { get; set; }
    public Func<string, long?>? ModelContextTokens { get; set; }
    public Func<Task<bool>>? CanSpendAsync { get; set; }

    public event Action<string, TimeSpan?, DateTimeOffset>? AssistantText;
    public event Action<string?, bool>? AssistantDelta;
    public event Action<ModelProgress>? ProgressReported;
    public event Action<ToolCallEvent>? ToolCallStarted;
    public event Action<ToolResultEvent>? ToolCallFinished;
    public event Action<string, UsageInfo>? UsageReported;
    public event Action<int, bool>? ThinkingStarted;
    public event Action<string>? Warning, Stopped, Notice;
    public event Action<int, int>? Retrying;
    public event Action? ChatCreated;

    void OnRetrying(int attempt, int total) => Retrying?.Invoke(attempt, total);

    public bool CanContinue => _history.LastOrDefault() is JsonObject last && (Role(last) is "user" or "tool" ||
        Role(last) == "assistant" && last["tool_calls"] == null && (string.IsNullOrWhiteSpace(ExtractText(last["content"])) || ReviewOf(last) != null));

    public void Open(string path)
    {
        var chat = store.Load(path);
        NewChat();
        _history.AddRange(chat.Messages);
        RepairHistory();
        ChatPath = path;
        Title = chat.Title;
    }

    public void NewChat()
    {
        _history.Clear();
        _imageCache.Clear();
        ResetReads();
        _inputCaps.Clear();
        _turnClock.Reset();
        ChatPath = Title = null;
        _saveFailed = false;
    }

    public void Rename(string? title)
    {
        var clean = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (ChatPath != null) store.Rename(ChatPath, clean);
        Title = clean;
    }

    public async Task RunAsync(string userText, IReadOnlyList<Attachment> attachments, string model, CancellationToken ct, DateTimeOffset? sentAt = null)
    {
        var local = attachments.Count == 0 ? LocalCommand(userText) : null;
        if (local == null) EnsureApi();
        _model = model;
        ResetReads();
        _turnClock.Reset();
        var message = new JsonObject { ["role"] = "user", ["content"] = userText,
            [MetaField] = new JsonObject { [SentAtField] = FormatSentAt(sentAt ?? DateTimeOffset.UtcNow) } };
        if (attachments.Count > 0) message[AttachmentsField] = new JsonArray([.. attachments.Select(a => a.ToJson())]);
        _history.Add(message);
        Save();
        if (local == null && attachments.Any(a => a.Kind == AttachmentKind.Image) && !AllowImages(model))
            Notice?.Invoke($"Модель {model} не видит изображения — картинки переданы агенту как обычные файлы.");
        await (local != null ? RunLocalAsync(local, ct) : LoopAsync(model, ct));
    }

    public async Task ContinueAsync(string model, CancellationToken ct)
    {
        var local = PendingLocalCommand;
        if (local == null) EnsureApi();
        _model = model;
        if (MetaOf(_history.LastOrDefault())?.Remove(StopField) == true) Save();
        if (local != null)
        {
            await RunLocalAsync(local, ct);
            return;
        }
        _repeats.Clear();
        _blockedShells.Clear();
        RestorePlugins();
        RestoreCompletedWrites();
        InvalidateProjectContext();
        _resumeContext = ResumeContext();
        try { await LoopAsync(model, ct); }
        finally
        {
            _resumeContext = null;
            _completedWrites.Clear();
        }
    }

    void ResetReads()
    {
        _repeats.Clear();
        _blockedShells.Clear();
        _reads.Clear();
        _activePlugins.Clear();
        _completedWrites.Clear();
        _resumeContext = null;
        _systemMessage = null;
        _plugins = null;
        InvalidateProjectContext();
    }

    void EnsureApi() => _ = Api ?? throw new InvalidOperationException("Провайдер не подключён: укажите ключ или войдите в аккаунт в настройках.");

    async Task Timed(Func<Task> run)
    {
        _turnClock.Start();
        try { await run(); }
        finally { _turnClock.Stop(); }
    }

    Task LoopAsync(string model, CancellationToken ct) => Timed(async () =>
    {
        bool truncationReported = false;
        for (int step = 1; ; step++)
        {
            ct.ThrowIfCancellationRequested();
            if (CanSpendAsync != null && !await CanSpendAsync()) throw new OperationCanceledException();
            bool finalWorkerStep = delegated && step == SubagentStepLimit;
            var result = await CompleteAsync(model, step, finalWorkerStep, ct);
            if (result.FinishReason == "content_filter")
            {
                ResetModelProgress();
                throw new ApiException("Модель остановила ответ фильтром содержимого. Переформулируйте запрос.", code: "content_filter");
            }
            var sentAt = DateTimeOffset.UtcNow;
            var message = result.Message;
            message["role"] = "assistant";
            var progress = result.Progress ?? ProgressOf(message);
            ProgressReported?.Invoke(progress);
            var text = ExtractText(message["content"]);
            var calls = message["tool_calls"] is JsonArray { Count: > 0 } array ? array : null;
            bool blockedCalls = finalWorkerStep && calls != null;
            if (blockedCalls) calls = null;
            var review = calls == null && !string.IsNullOrWhiteSpace(text) && result.FinishReason != "length" ? ReviewNote() : null;
            TimeSpan? elapsed = calls == null && review == null ? _turnClock.Elapsed : null;
            if (calls == null) message.Remove("tool_calls");
            else
            {
                NormalizeToolCalls(calls);
                if (string.IsNullOrEmpty(text)) message["content"] = null;
            }
            var meta = new JsonObject { ["model"] = model, ["provider"] = Provider.ToString().ToLowerInvariant() };
            if (result.Usage is { } usage)
            {
                meta["prompt_tokens"] = usage.PromptTokens;
                meta["completion_tokens"] = usage.CompletionTokens;
                meta["request_id"] = result.RequestId;
            }
            if (elapsed is { } time) meta[ElapsedField] = (long)time.TotalMilliseconds;
            if (review != null) meta[ReviewField] = review;
            if (result.Progress?.Reasoning is { } reasoning) meta["reasoning"] = reasoning;
            if (progress.Commentary != null) meta["commentary"] = progress.Commentary;
            if (result.Phase != null) meta["phase"] = result.Phase;
            meta[SentAtField] = FormatSentAt(sentAt);
            message[MetaField] = meta;
            bool truncated = calls != null && result.FinishReason == "length";
            var skipped = truncated ? calls!.OfType<JsonObject>().Select(ParseCall).ToList() : [];
            if (calls != null || !string.IsNullOrWhiteSpace(text) || progress.Reasoning != null || progress.Commentary != null)
            {
                _history.Add(message);
                foreach (var call in skipped) _history.Add(ToolMessage(call.Id, TruncatedCallReply));
                Save();
            }
            if (result.Usage != null) UsageReported?.Invoke(model, result.Usage);
            AssistantText?.Invoke(text?.Trim() ?? "", elapsed, sentAt);
            if (blockedCalls)
            {
                RecordStop(SubagentStepStop);
                return;
            }
            foreach (var call in skipped)
            {
                ToolCallStarted?.Invoke(call);
                ToolCallFinished?.Invoke(new(call.Id, call.Name, TruncatedCallReply, true));
            }
            if (truncated && truncationReported)
            {
                RecordStop("Агент остановлен: лимит токенов дважды прервал вызов инструмента. Увеличьте лимит ответа или упростите задачу.");
                return;
            }
            truncationReported = truncated;
            if (truncated)
            {
                Warning?.Invoke("Лимит токенов прервал вызов инструмента. Агент попробует выполнить действие по частям.");
                continue;
            }
            if (calls == null)
            {
                if (review != null)
                {
                    Notice?.Invoke("Агент проверяет результат перед завершением.");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(text))
                    Warning?.Invoke("Модель вернула пустой ответ. Попробуйте переформулировать запрос или выбрать другую модель" +
                                    $" (причина остановки: {result.FinishReason ?? "не указана"}).");
                else if (result.FinishReason == "length")
                    Warning?.Invoke("Ответ обрезан: достигнут лимит токенов ответа. Напишите «продолжай» или увеличьте лимит в настройках.");
                return;
            }
            if (await ExecuteToolCallsAsync(calls, ct) is { } stuck)
            {
                RecordStop(stuck);
                return;
            }
        }
    });

    bool AllowImages(string model) => !_noVisionModels.Contains(model) && ModelSupportsVision?.Invoke(model) != false;

    async Task<ChatResult> CompleteAsync(string model, int step, bool finalWorkerStep, CancellationToken ct)
    {
        var definitions = finalWorkerStep ? new JsonArray() : Definitions();
        bool withImages = AllowImages(model), droppedImages = false, harder = false;
        while (true)
        {
            var (messages, images, compressed) = await BuildRequestAsync(model, definitions, withImages, harder, finalWorkerStep, ct);
            ThinkingStarted?.Invoke(step, compressed);
            var effort = _noEffortModels.Contains(model) ? null : ReasoningEffort;
            try
            {
                var result = await Api!.CompleteAsync(model, messages, definitions, MaxTokens, effort,
                    (text, usesTools) => AssistantDelta?.Invoke(text, usesTools), progress => ProgressReported?.Invoke(progress), ct);
                _sent = messages;
                if (droppedImages)
                {
                    _noVisionModels.Add(model);
                    Notice?.Invoke($"Модель {model} не приняла изображения — картинки переданы агенту как обычные файлы.");
                }
                return result;
            }
            catch (ApiException ex) when (Provider is not (AgentProvider.ChatGpt or AgentProvider.Groq) && definitions.Count > 0 &&
                !_toolReasoningModels.Contains(model) && RequiresToolReasoning(ex) &&
                _history.OfType<JsonObject>().Any(m => Role(m) == "assistant" && HasReasoning(m) && FromModel(m, model)))
            {
                ResetModelProgress();
                _toolReasoningModels.Add(model);
            }
            catch (ApiException ex) when (effort != null && LooksLikeEffortRejection(ex) && !RequiresToolReasoning(ex))
            {
                ResetModelProgress();
                _noEffortModels.Add(model);
                Notice?.Invoke($"Модель {model} не приняла выбранную силу рассуждения — она отвечает в обычном режиме.");
            }
            catch (ApiException ex) when (images > 0 && LooksLikeImageRejection(ex))
            {
                ResetModelProgress();
                withImages = false;
                droppedImages = true;
            }
            catch (ApiException ex) when (IsContextOverflow(ex))
            {
                if (harder)
                    throw new ApiException($"Запрос не помещается в память модели {model} даже после сжатия давних шагов. " +
                                           "Сократите текущее вложение или сообщение (лучше в новом чате) либо выберите модель " +
                                           "с большей памятью и нажмите «Повторить» — переписка сохранена.\n\n" + ex.Message, ex.StatusCode);
                ResetModelProgress();
                int cap = HistoryCompressor.EstimateAll(messages) + HistoryCompressor.Estimate(definitions) + images * ImageTokens -
                          HistoryCompressor.EstimateAll(messages.Skip(1)) / 4;
                if (cap < InputBudget(model)) _inputCaps[model] = cap;
                harder = true;
            }
        }
    }

    void ResetModelProgress()
    {
        AssistantDelta?.Invoke(null, false);
        ProgressReported?.Invoke(new());
    }

    int InputBudget(string model)
    {
        long budget = Math.Min(delegated ? SubagentInputTokens : MaxInputTokens, _inputCaps.GetValueOrDefault(model, int.MaxValue));
        if (ModelContextTokens?.Invoke(model) is > 0 and var window) budget = Math.Min(budget, window - Math.Min(MaxTokens ?? 8_000, window / 2));
        return (int)budget;
    }

    static bool Mentions(string? text, params string[] hints) =>
        text != null && hints.Any(hint => text.Contains(hint, StringComparison.OrdinalIgnoreCase));

    static bool IsContextOverflow(ApiException ex)
    {
        string[] hints =
        [
            "context_length", "context length", "context window", "maximum context", "too many tokens", "prompt is too long",
            "input is too long", "reduce the length", "token limit", "контекст",
        ];
        return ex.StatusCode is BadRequest or RequestEntityTooLarge or UnprocessableEntity && (Mentions(ex.Message, hints) || Mentions(ex.Code, hints));
    }

    static bool LooksLikeEffortRejection(ApiException ex) =>
        ex.StatusCode is BadRequest or UnprocessableEntity && Mentions(ex.Message, "reasoning", "effort");

    static bool LooksLikeImageRejection(ApiException ex) => ex.StatusCode == RequestEntityTooLarge
        ? ex.Code != "rate_limit_exceeded"
        : ex.StatusCode is BadRequest or NotFound or UnprocessableEntity or UnsupportedMediaType &&
          Mentions(ex.Message, "image", "vision", "multimodal", "content must be a string", "изображен", "картин");

    static bool HasReasoning(JsonObject message) => message["reasoning"] != null || message["reasoning_content"] != null;

    bool FromModel(JsonObject message, string model) => MetaOf(message) is { } meta &&
        string.Equals(Str(meta["provider"]), Provider.ToString(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Str(meta["model"]), model, StringComparison.OrdinalIgnoreCase);

    static bool RequiresToolReasoning(ApiException ex) => ex.StatusCode is BadRequest or UnprocessableEntity &&
        (ex.Code is "missing_reasoning_content" or "reasoning_content_required" ||
        (ex.Parameter is "reasoning" or "reasoning_content" ||
            Mentions(ex.Message, "reasoning_content", "'reasoning' field", "\"reasoning\" field", "reasoning field", "reasoning property")) &&
        !Mentions(ex.Message, "not required", "must not", "not allowed") &&
        Mentions(ex.Message, "missing", "required", "mandatory", "must be passed", "must be provided", "must be present",
            "pass back", "not provided", "отсутств", "обязател", "требуется"));

    async Task<(List<JsonNode> Messages, int Images, bool Compressed)> BuildRequestAsync(string model, JsonArray definitions,
        bool withImages, bool harder, bool finalWorkerStep, CancellationToken ct)
    {
        var system = SystemMessage();
        int currentUser = _history.FindLastIndex(m => Role(m) == "user");
        var shownImages = withImages && currentUser >= 0 && _history[currentUser] is JsonObject active
            ? Attachment.ListFrom(active).Where(a => a.Kind == AttachmentKind.Image).TakeLast(8).Select(a => (active, a.Path)).ToHashSet()
            : [];
        var screenshot = withImages ? LatestScreenshot(currentUser) : null;
        int room = InputBudget(model) - HistoryCompressor.Estimate(system) - HistoryCompressor.Estimate(definitions) -
                   (shownImages.Count + (screenshot != null ? 1 : 0)) * ImageTokens;
        var note = RequestTail(harder);
        if (finalWorkerStep) note += "\n\n[Последний шаг субагента. Подведи итог по собранным данным и укажи, чего не удалось проверить.]";
        var project = await ProjectContextAsync(Math.Clamp(room * 3 / (harder ? 12 : 5), 0, harder ? 2200 : 11_000), ct);
        room -= ((note?.Length ?? 0) + (project?.Length ?? 0)) / 3;
        int completedBefore = currentUser;
        for (int i = _history.Count - 1, retained = 0; i > currentUser; i--)
            if (Role(_history[i]) == "assistant" && _history[i]["tool_calls"] is JsonArray { Count: > 0 } && ++retained == 2)
            {
                completedBefore = i;
                break;
            }
        bool omitReasoning = definitions.Count == 0 || !_toolReasoningModels.Contains(model);
        bool shortened = false;
        var superseded = SupersededResults(currentUser);
        var projected = new List<JsonNode>(_history.Count);
        JsonNode Shorten(JsonNode node)
        {
            var compact = HistoryCompressor.Shorten(node);
            shortened |= !ReferenceEquals(node, compact);
            return compact;
        }
        for (int i = 0; i < _history.Count; i++)
        {
            var node = WithVerificationSummary(_history[i]);
            // Current-task results stay whole unless repeated later: the model keeps what it read and the prompt prefix stays cacheable.
            if (Role(node) == "tool" && (i < currentUser || superseded.Contains(Str(node["tool_call_id"]) ?? ""))) node = Shorten(node);
            if (node is JsonObject message && Role(message) == "assistant")
            {
                bool sameModel = FromModel(message, model);
                bool continuation = i > currentUser && message["tool_calls"] is JsonArray { Count: > 0 } && sameModel;
                bool removeReasoning = HasReasoning(message) && (omitReasoning || !sameModel);
                bool removeDetails = message["reasoning_details"] != null && !(continuation && Provider is AgentProvider.OpenRouter or AgentProvider.Tooken);
                bool removeSignatures = !(continuation && Provider is AgentProvider.Gemini or AgentProvider.Tooken) &&
                    message["tool_calls"] is JsonArray calls && calls.OfType<JsonObject>().Any(call => call["extra_content"] != null);
                if (removeReasoning || removeDetails || removeSignatures)
                {
                    var clean = (JsonObject)message.DeepClone();
                    if (removeReasoning)
                    {
                        clean.Remove("reasoning");
                        clean.Remove("reasoning_content");
                    }
                    if (removeDetails) clean.Remove("reasoning_details");
                    if (removeSignatures && clean["tool_calls"] is JsonArray cleaned)
                        foreach (var call in cleaned.OfType<JsonObject>()) call.Remove("extra_content");
                    node = clean;
                }
            }
            projected.Add(node);
        }
        if (HistoryCompressor.EstimateAll(projected) > room)
            for (int i = currentUser + 1; i < completedBefore; i++)
                if (Role(projected[i]) == "tool") projected[i] = Shorten(projected[i]);
        bool compress = harder || HistoryCompressor.EstimateAll(projected) > room;
        IReadOnlyList<JsonNode> source = compress ? HistoryCompressor.Compress(projected, room, harder || delegated) : projected;
        var list = new List<JsonNode> { system };
        int shown = 0;
        foreach (var node in source)
        {
            if (node is JsonObject message && message.ContainsKey(AttachmentsField) && Role(message) == "user")
            {
                var (converted, count) = await ToApiUserMessageAsync(message, shownImages, ct);
                list.Add(converted);
                shown += count;
            }
            else if (MetaOf(node) is { } meta)
            {
                var clean = (JsonObject)node.DeepClone();
                clean.Remove(MetaField);
                if (Provider == AgentProvider.ChatGpt && Role(node) == "assistant")
                    foreach (var key in new[] { "commentary", "phase" })
                        if (meta[key] is { } value) clean[key] = value.DeepClone();
                list.Add(clean);
                if (Str(meta[ReviewField]) is { } review) list.Add(new JsonObject { ["role"] = "user", ["content"] = review });
            }
            else list.Add(node);
        }
        if (screenshot is var (id, path) && list.FindIndex(node => Str(node["tool_call_id"]) == id) is >= 0 and var at &&
            await GetImageAsync(path, ct) is { } url)
        {
            while (Role(list.ElementAtOrDefault(at + 1)) == "tool") at++;
            list.Insert(at + 1, new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = $"[Программа: скриншот {path}]" },
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } },
                },
            });
            shown++;
        }
        AddContext(list, project == null ? null : "\n\n" + project, "user");
        if ((compress || shortened) && PlanNote() is { } plan) note += "\n\n[Программа: часть шагов свёрнута]\n" + plan;
        AddContext(list, note, "tool", "user");
        if (delegated && HistoryCompressor.EstimateAll(list) + HistoryCompressor.Estimate(definitions) + shown * ImageTokens > InputBudget(model))
            throw new ApiException("Поручение не помещается в бюджет контекста субагента. Сузьте задачу или передайте меньше вложений.");
        return (list, shown, compress || shortened);
    }

    (string Id, string Path)? LatestScreenshot(int currentUser) =>
        _history.Skip(currentUser + 1).LastOrDefault(m => Role(m) == "tool" &&
            Str(m["content"])?.StartsWith(ComputerTools.ShotPrefix, StringComparison.Ordinal) == true) is { } shot
            ? (Str(shot["tool_call_id"]) ?? "", Str(shot["content"])![ComputerTools.ShotPrefix.Length..].Split('\n')[0].Trim())
            : null;

    JsonObject SystemMessage()
    {
        var key = $"{tools.Context.WorkingDirectory}|{tools.Context.SandboxRoot}|{DateTime.Now:yyyy-MM-dd}|{UseSubagents}|{SubagentTaskLimit}|{SubagentStepLimit}|{ClarifyAsync != null}|{tools.Context.ComputerUse}";
        if (_systemMessage == null || _systemKey != key)
            (_systemMessage, _systemKey) = (new JsonObject
            {
                ["role"] = "system",
                ["content"] = (delegated ? SubagentPrompt : SystemPrompt.Build(tools.Context, PluginTools.Describe(AvailablePlugins()))) +
                              InteractionPrompt(),
            }, key);
        return _systemMessage;
    }

    async Task<(JsonObject Message, int Images)> ToApiUserMessageAsync(JsonObject message, HashSet<(JsonObject, string)> shownImages,
        CancellationToken ct)
    {
        var text = ExtractText(message["content"]) ?? "";
        var content = new JsonArray();
        var attachments = Attachment.ListFrom(message);
        if (attachments.Count > 0)
        {
            text += (text.Length > 0 ? "\n\n" : "") + "[Пользователь приложил к сообщению файлы и папки. Работай с ними по этим путям:]";
            foreach (var attachment in attachments)
            {
                var note = "";
                if (attachment.Kind == AttachmentKind.Image && shownImages.Contains((message, attachment.Path)))
                {
                    var url = await GetImageAsync(attachment.Path, ct);
                    note = url == null ? "; открыть как изображение не удалось" : "; само изображение показано тебе ниже";
                    if (url != null) content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
                }
                text += $"\n- {attachment.Path} ({attachment.Describe()}{note})";
            }
        }
        int images = content.Count;
        if (images > 0) content.Insert(0, new JsonObject { ["type"] = "text", ["text"] = text });
        return (new JsonObject { ["role"] = "user", ["content"] = images > 0 ? content : text }, images);
    }

    async Task<string?> GetImageAsync(string path, CancellationToken ct)
    {
        string key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { return null; }
        if (!_imageCache.TryGetValue(key, out var url)) _imageCache[key] = url = await Task.Run(() => ImageEncoder.ToDataUrl(path), ct);
        return url;
    }

    async Task<string?> ExecuteToolCallsAsync(JsonArray toolCalls, CancellationToken ct)
    {
        var calls = toolCalls.OfType<JsonObject>().Select(ParseCall).ToList();
        var counted = new Dictionary<string, ToolCallEvent>();
        int done = 0;
        try
        {
            while (done < calls.Count)
            {
                ct.ThrowIfCancellationRequested();
                int end = done + 1;
                if (ToolRegistry.IsReadOnly(calls[done].Name))
                    while (end < calls.Count && ToolRegistry.IsReadOnly(calls[end].Name)) end++;
                var batch = calls.GetRange(done, end - done);
                var executions = PrepareCalls(batch, ct);
                var outcomes = await Task.WhenAll(executions.Select(execution => execution.Task));
                var finished = new List<ToolResultEvent>();
                for (int k = 0; k < batch.Count; k++)
                {
                    var (call, outcome, execution) = (batch[k], outcomes[k], executions[k]);
                    bool reused = execution.PreviousId != null;
                    bool repeat = Repeats(call, outcome, counted, execution.Key?.Identity, reused) >= 3;
                    var output = ResultForModel(call, outcome, k, executions);
                    if (repeat && outcome.Restriction == null) output += RepeatReply;
                    var message = ToolMessage(call.Id, output, outcome.Diff);
                    if (reused) message[MetaField] = new JsonObject { ["action_reused"] = true };
                    if (call.Name == "run_command" && VerificationCommand(call)) message[MetaField] = new JsonObject { ["verification"] = true };
                    if (outcome.Restriction != null) EnsureMeta(message)["command_restricted"] = true;
                    if (outcome.DelegatedUsage is { } usage)
                        message[MetaField] = new JsonObject
                        {
                            ["prompt_tokens"] = usage.PromptTokens, ["completion_tokens"] = usage.CompletionTokens,
                            ["provider"] = Provider.ToString(), ["model"] = _model,
                        };
                    if (!reused && !outcome.IsError && !outcome.IsWarning) RecordChange(call, message, outcome.Change);
                    _history.Add(message);
                    done++;
                    if (reused) continue;
                    finished.Add(new(call.Id, call.Name, output, outcome.IsError, outcome.IsWarning, outcome.Diff) { Change = outcome.Change });
                    if (outcome.Restriction?.UnavailableShell == null && !ToolRegistry.IsReadOnly(call.Name) &&
                        call.Name is not ("ask_user" or "delegate_tasks" or "update_plan"))
                        InvalidateReads(call);
                }
                Save();
                foreach (var result in finished) ToolCallFinished?.Invoke(result);
            }
        }
        finally
        {
            foreach (var call in calls.Skip(done)) _history.Add(ToolMessage(call.Id, "Отменено пользователем."));
            if (done < calls.Count) Save();
        }
        ct.ThrowIfCancellationRequested();
        foreach (var (key, call) in counted)
            if (_repeats.GetValueOrDefault(key) is { Count: >= 4 } repeated)
                return key.StartsWith("shell:", StringComparison.Ordinal)
                    ? "Агент остановлен. " + CommandRestriction.Prefix + repeated.Output
                    : $"Агент остановлен: модель {repeated.Count} раза запросила одно и то же действие " +
                      $"({HistoryCompressor.Describe(call.Name, call.ArgumentsJson)}). Уточните задачу или нажмите «Продолжить».";
        return null;
    }

    static ToolCallEvent ParseCall(JsonObject call)
    {
        var function = call["function"] as JsonObject;
        return new(Str(call["id"])!, Str(function?["name"]) ?? "", Str(function?["arguments"]) ?? "{}");
    }

    int Repeats(ToolCallEvent call, ToolOutcome outcome, Dictionary<string, ToolCallEvent> counted, string? identity, bool reused)
    {
        if (!reused && outcome.Restriction == null && !ToolRegistry.IsReadOnly(call.Name))
        {
            if (!outcome.IsError) _repeats.Clear();
            return 0;
        }
        string key = outcome.Restriction?.UnavailableShell is { } shell ? "shell:" + shell : identity ?? CallKey(call);
        if (!counted.TryAdd(key, call)) return 0;
        bool known = _repeats.TryGetValue(key, out var seen);
        if (!known) foreach (var other in _repeats.Keys.ToList()) _repeats[other] = (_repeats[other].Output, 0);
        var output = outcome.Restriction?.Message ?? outcome.Output;
        int count = known && seen.Output == output ? seen.Count + 1 : 1;
        _repeats[key] = (output, count);
        return count;
    }

    static string CallKey(ToolCallEvent call)
    {
        try { return call.Name + " " + (JsonNode.Parse(call.ArgumentsJson)?.ToJsonString() ?? ""); }
        catch (JsonException) { return call.Name + " " + call.ArgumentsJson; }
    }

    static JsonObject ToolMessage(string id, string content, FileDiff? diff = null)
    {
        var message = new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = content };
        if (diff != null) message[MetaField] = new JsonObject { ["diff"] = diff.Unified, ["added"] = diff.Added, ["removed"] = diff.Removed };
        return message;
    }

    internal static string? Role(JsonNode? message) => Str((message as JsonObject)?["role"]);

    internal static JsonObject? MetaOf(JsonNode? message) => (message as JsonObject)?[MetaField] as JsonObject;

    static JsonObject EnsureMeta(JsonObject message)
    {
        if (message[MetaField] is not JsonObject meta) message[MetaField] = meta = new JsonObject();
        return meta;
    }

    internal static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    static JsonObject? ArgsOf(ToolCallEvent call) => ParseObject(call.ArgumentsJson);

    public static FileDiff? DiffOf(JsonObject toolMessage) =>
        MetaOf(toolMessage) is { } meta && Str(meta["diff"]) is { } diff
            ? new FileDiff(diff, (int)Long(meta["added"]), (int)Long(meta["removed"]))
            : null;

    public static TimeSpan? ElapsedOf(JsonObject message) =>
        MetaOf(message)?[ElapsedField] is { } elapsed ? TimeSpan.FromMilliseconds(Long(elapsed)) : null;

    static string FormatSentAt(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    internal static DateTimeOffset? SentAtOf(JsonObject message) =>
        DateTimeOffset.TryParseExact(Str(MetaOf(message)?[SentAtField]), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time : null;

    internal static string? StopReasonOf(JsonObject message) => Str(MetaOf(message)?[StopField]);

    internal void RecordStop(string text)
    {
        if (_history.LastOrDefault() is JsonObject last)
        {
            EnsureMeta(last)[StopField] = text;
            Save();
        }
        Stopped?.Invoke(text);
    }

    public static ModelProgress ProgressOf(JsonObject message) =>
        new(Str(MetaOf(message)?["reasoning"]) ?? Str(message["reasoning_content"]) ?? Str(message["reasoning"]) ??
            ChatCompletionStream.ThinkingOf(message["content"]) ?? ChatCompletionStream.DetailText(message["reasoning_details"] as JsonArray),
            Str(MetaOf(message)?["commentary"]));

    static void NormalizeToolCalls(JsonArray toolCalls)
    {
        foreach (var call in toolCalls.OfType<JsonObject>())
        {
            if (string.IsNullOrEmpty(Str(call["id"]))) call["id"] = "call_" + Guid.NewGuid().ToString("N")[..16];
            call["type"] ??= "function";
            if (call["function"] is JsonObject function && function["arguments"] is var args &&
                (args is not JsonValue value || !value.TryGetValue<string>(out _)))
                function["arguments"] = args?.ToJsonString() ?? "{}";
        }
    }

    void RepairHistory()
    {
        for (int i = 0; i < _history.Count; i++)
        {
            if (Role(_history[i]) != "assistant" || _history[i]["tool_calls"] is not JsonArray calls) continue;
            int j = i + 1;
            var answered = new HashSet<string>();
            while (Role(_history.ElementAtOrDefault(j)) == "tool") answered.Add(Str(_history[j++]["tool_call_id"]) ?? "");
            foreach (var id in calls.OfType<JsonObject>().Select(call => Str(call["id"]) ?? ""))
                if (!answered.Contains(id)) _history.Insert(j++, ToolMessage(id, "Выполнение было прервано (программа закрыта или остановлена)."));
            i = j - 1;
        }
    }

    public static string? ExtractText(JsonNode? content) => content is JsonArray parts
        ? string.Concat(parts.Select(part => Str((part as JsonObject)?["text"])))
        : Str(content);

    public static string? TitleOf(IEnumerable<JsonNode> messages) =>
        messages.OfType<JsonObject>().FirstOrDefault(m => Role(m) == "user") is not { } first ? null
        : ExtractText(first["content"])?.ReplaceLineEndings(" ").Trim() is { Length: > 0 } text ? text
        : Attachment.ListFrom(first).FirstOrDefault()?.Name;

    public ChatTotals Totals()
    {
        long prompt = 0, completion = 0, context = 0;
        var byModel = new Dictionary<(AgentProvider, string), long>();
        foreach (var message in _history)
        {
            if (MetaOf(message) is not { } meta || meta["prompt_tokens"] == null) continue;
            long p = Long(meta["prompt_tokens"]), c = Long(meta["completion_tokens"]);
            prompt += p;
            completion += c;
            if (Role(message) == "assistant") context = p + c;
            var key = (Enum.TryParse<AgentProvider>(Str(meta["provider"]), true, out var named) ? named : AgentProvider.Tooken,
                Str(meta["model"]) ?? "");
            byModel[key] = byModel.GetValueOrDefault(key) + p + c;
        }
        return new(prompt, completion, context, byModel);
    }

    void Save()
    {
        if (delegated) return;
        try
        {
            bool created = ChatPath == null;
            ChatPath ??= store.NewPath();
            store.Save(ChatPath, _history, _model, Title);
            _saveFailed = false;
            if (created) ChatCreated?.Invoke();
        }
        catch (Exception ex)
        {
            if (!_saveFailed) Warning?.Invoke("Не удалось сохранить переписку на диск: " + ex.Message);
            _saveFailed = true;
        }
    }
}
