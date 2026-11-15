using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Tools;

namespace SharpAgent.Agent;

public sealed record ToolCallEvent(string Id, string Name, string ArgumentsJson);
public sealed record ToolResultEvent(string Id, string Name, string Output, bool IsError, bool IsWarning = false, FileDiff? Diff = null)
{
    internal FileChange? Change { get; init; }
}

public sealed record ChatTotals(long PromptTokens, long CompletionTokens, long ContextTokens,
    IReadOnlyDictionary<(AgentProvider Provider, string Model), long> TokensByModel);

public sealed partial class AgentRunner
{
    private readonly ToolRegistry tools;
    private readonly ChatStore store;
    private readonly bool delegated;

    public AgentRunner(ToolRegistry tools, ChatStore store) : this(tools, store, false) { }

    private AgentRunner(ToolRegistry tools, ChatStore store, bool delegated) =>
        (this.tools, this.store, this.delegated) = (tools, store, delegated);

    public const string AttachmentsField = "attachments";
    public const string MetaField = "meta";

    private const string ElapsedField = "elapsed_ms";
    private const string StopField = "stopped";
    private const int MaxImagesPerRequest = 8, ImageTokens = 1_000;
    private const int AnswerReserve = 8_000;
    private const int MaxInputTokens = 80_000;
    private const int RepeatHint = 3, RepeatStop = 4;

    private const string TruncatedCallReply =
        "Ошибка: ответ оборвался на лимите токенов, аргументы вызова пришли не полностью — действие не выполнено. " +
        "Повтори его меньшими частями (например, write_file по частям с append=true).";

    public const string RepeatReply =
        "\n\n[Программа: этот же вызов уже несколько раз вернул тот же результат. Не повторяй его — измени запрос, " +
        "сузь папку или переходи к следующему шагу.]";

    private sealed record Request(List<JsonNode> Messages, int Images, bool Compressed);

    private readonly List<JsonNode> _history = new();
    private readonly HashSet<string> _noVisionModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _noEffortModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Output, int Count)> _repeats = new();
    private readonly Dictionary<CommandShell, CommandRestriction> _blockedShells = new();
    private readonly Dictionary<ReadKey, (string Id, ToolOutcome Outcome)> _reads = new();
    private readonly Dictionary<string, string> _activePlugins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _inputCaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch _turnClock = new();
    private IReadOnlyList<JsonNode> _sent = [];
    private JsonObject? _systemMessage;
    private string? _systemKey;
    private bool _saveFailed;
    private string? _resumeContext;

    private IChatProvider? _api;

    public IChatProvider? Api
    {
        get => _api;
        set
        {
            if (_api != null && !delegated) _api.Retrying -= OnRetrying;
            _api = value;
            _noVisionModels.Clear();
            _noEffortModels.Clear();
            if (_api != null && !delegated) _api.Retrying += OnRetrying;
        }
    }

    public AgentProvider Provider { get; set; }
    public int? MaxTokens { get; set; }
    public string? ReasoningEffort { get; set; }
    public string? CurrentModel { get; private set; }
    public IReadOnlyList<JsonNode> History => _history;
    public ChatStore Store => store;
    public string? ChatPath { get; private set; }
    public string? Title { get; private set; }

    public Func<ConfirmRequest, Task<bool>>? ConfirmAsync { get; set; }

    public Func<string, bool?>? ModelSupportsVision { get; set; }

    public Func<string, long?>? ModelContextTokens { get; set; }

    public Func<Task<bool>>? CanSpendAsync { get; set; }

    public event Action<string, TimeSpan?>? AssistantText;
    public event Action<string?, bool>? AssistantDelta;
    public event Action<ModelProgress>? ProgressReported;
    public event Action<ToolCallEvent>? ToolCallStarted;
    public event Action<ToolResultEvent>? ToolCallFinished;
    public event Action<string, UsageInfo>? UsageReported;
    public event Action<int, bool>? ThinkingStarted;
    public event Action<string>? Warning;
    public event Action<string>? Stopped;
    public event Action<string>? Notice;
    public event Action<int, int>? Retrying;
    public event Action? ChatCreated;

    private void OnRetrying(int attempt, int total) => Retrying?.Invoke(attempt, total);

    public bool CanContinue =>
        _history.Count > 0 && _history[^1] is JsonObject last &&
        (JsonUtil.Str(last["role"]) is "user" or "tool" ||
         JsonUtil.Str(last["role"]) == "assistant" && last["tool_calls"] == null && string.IsNullOrWhiteSpace(ExtractText(last["content"])));

    public void Open(string path)
    {
        var chat = store.Load(path);
        NewChat();
        _history.AddRange(chat.Messages);
        RepairHistory(_history);
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
        ChatPath = null;
        Title = null;
        _saveFailed = false;
    }

    public void Rename(string? title)
    {
        var clean = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (ChatPath != null) store.Rename(ChatPath, clean);
        Title = clean;
    }

    public async Task RunAsync(string userText, IReadOnlyList<Attachment> attachments, string model, CancellationToken ct)
    {
        var local = attachments.Count == 0 ? LocalCommand(userText) : null;
        if (local == null) EnsureApi();
        CurrentModel = model;
        ResetReads();
        _turnClock.Reset();

        var message = new JsonObject { ["role"] = "user", ["content"] = userText };
        if (attachments.Count > 0)
            message[AttachmentsField] = new JsonArray(attachments.Select(a => (JsonNode?)a.ToJson()).ToArray());
        _history.Add(message);
        Save();

        if (local != null)
        {
            await RunLocalAsync(local, ct);
            return;
        }
        if (attachments.Any(a => a.Kind == AttachmentKind.Image) && !AllowImages(model))
            Notice?.Invoke($"Модель {model} не видит изображения — картинки переданы агенту как обычные файлы.");

        await LoopAsync(model, ct);
    }

    public async Task ContinueAsync(string model, CancellationToken ct)
    {
        var local = PendingLocalCommand;
        if (local == null) EnsureApi();
        CurrentModel = model;
        if (_history.LastOrDefault()?[MetaField] is JsonObject meta && meta.Remove(StopField)) Save();
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

    private void ResetReads()
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

    private void EnsureApi()
    {
        if (Api is null) throw new InvalidOperationException("Провайдер не подключён: укажите ключ или войдите в аккаунт в настройках.");
    }

    private async Task LoopAsync(string model, CancellationToken ct)
    {
        _turnClock.Start();
        try { await RunStepsAsync(model, ct); }
        finally { _turnClock.Stop(); }
    }

    private async Task RunStepsAsync(string model, CancellationToken ct)
    {
        bool truncationReported = false;
        for (int step = 1; ; step++)
        {
            ct.ThrowIfCancellationRequested();
            if (CanSpendAsync != null && !await CanSpendAsync()) throw new OperationCanceledException();

            var result = await CompleteAsync(model, step, ct);
            if (result.FinishReason == "content_filter")
            {
                AssistantDelta?.Invoke(null, false);
                ProgressReported?.Invoke(new());
                throw new ApiException("Модель остановила ответ фильтром содержимого. Переформулируйте запрос.", code: "content_filter");
            }
            var message = result.Message;
            message["role"] = "assistant";
            var progress = result.Progress ?? ProgressOf(message);
            ProgressReported?.Invoke(progress);
            var text = ExtractText(message["content"]);
            var toolCalls = message["tool_calls"] as JsonArray;
            bool hasToolCalls = toolCalls is { Count: > 0 };
            TimeSpan? elapsed = hasToolCalls ? null : _turnClock.Elapsed;

            if (hasToolCalls)
            {
                NormalizeToolCalls(toolCalls!);
                if (string.IsNullOrEmpty(text)) message["content"] = null;
            }
            else
            {
                message.Remove("tool_calls");
            }

            var meta = new JsonObject();
            if (result.Usage != null)
            {
                meta["model"] = model;
                meta["provider"] = Provider.ToString().ToLowerInvariant();
                meta["prompt_tokens"] = result.Usage.PromptTokens;
                meta["completion_tokens"] = result.Usage.CompletionTokens;
                meta["request_id"] = result.RequestId;
            }
            if (elapsed is { } time) meta[ElapsedField] = (long)time.TotalMilliseconds;
            if (result.Progress?.Reasoning is { } reasoning) meta["reasoning"] = reasoning;
            if (progress.Commentary != null) meta["commentary"] = progress.Commentary;
            if (result.Phase != null) meta["phase"] = result.Phase;
            if (meta.Count > 0) message[MetaField] = meta;

            if (hasToolCalls && result.FinishReason == "length")
            {
                _history.Add(message);
                var skipped = toolCalls!.OfType<JsonObject>().Select(ParseCall).ToList();
                foreach (var call in skipped) _history.Add(ToolMessage(call.Id, TruncatedCallReply));
                Save();
                if (result.Usage != null) UsageReported?.Invoke(model, result.Usage);
                AssistantText?.Invoke(text?.Trim() ?? "", null);
                foreach (var call in skipped)
                {
                    ToolCallStarted?.Invoke(call);
                    ToolCallFinished?.Invoke(new(call.Id, call.Name, TruncatedCallReply, true));
                }
                if (truncationReported)
                {
                    RecordStop("Агент остановлен: лимит токенов дважды прервал вызов инструмента. Увеличьте лимит ответа или упростите задачу.");
                    return;
                }
                Warning?.Invoke("Лимит токенов прервал вызов инструмента. Агент попробует выполнить действие по частям.");
                truncationReported = true;
                continue;
            }
            truncationReported = false;

            if (hasToolCalls || !string.IsNullOrWhiteSpace(text) || progress.Reasoning != null || progress.Commentary != null)
            {
                _history.Add(message);
                Save();
            }
            if (result.Usage != null) UsageReported?.Invoke(model, result.Usage);
            AssistantText?.Invoke(text?.Trim() ?? "", elapsed);

            if (!hasToolCalls)
            {
                if (string.IsNullOrWhiteSpace(text))
                    Warning?.Invoke("Модель вернула пустой ответ. Попробуйте переформулировать запрос или выбрать другую модель" +
                                    $" (причина остановки: {result.FinishReason ?? "не указана"}).");
                else if (result.FinishReason == "length")
                    Warning?.Invoke("Ответ обрезан: достигнут лимит токенов ответа. Напишите «продолжай» или увеличьте лимит в настройках.");
                return;
            }

            if (await ExecuteToolCallsAsync(toolCalls!, ct) is { } stuck)
            {
                RecordStop(stuck);
                return;
            }
        }
    }

    private bool AllowImages(string model) =>
        !_noVisionModels.Contains(model) && ModelSupportsVision?.Invoke(model) != false;

    private async Task<ChatResult> CompleteAsync(string model, int step, CancellationToken ct)
    {
        var definitions = Definitions();
        bool withImages = AllowImages(model), droppedImages = false, harder = false;
        while (true)
        {
            var request = await BuildRequestAsync(model, definitions, withImages, harder, ct);
            ThinkingStarted?.Invoke(step, request.Compressed);
            var effort = _noEffortModels.Contains(model) ? null : ReasoningEffort;
            try
            {
                var result = await Api!.CompleteAsync(model, request.Messages, definitions, MaxTokens, effort,
                    (text, tools) => AssistantDelta?.Invoke(text, tools), progress => ProgressReported?.Invoke(progress), ct);
                _sent = request.Messages;
                if (droppedImages)
                {
                    _noVisionModels.Add(model);
                    Notice?.Invoke($"Модель {model} не приняла изображения — картинки переданы агенту как обычные файлы.");
                }
                return result;
            }
            catch (ApiException ex) when (effort != null && LooksLikeEffortRejection(ex))
            {
                ResetModelProgress();
                _noEffortModels.Add(model);
                Notice?.Invoke($"Модель {model} не приняла выбранную силу рассуждения — она отвечает в обычном режиме.");
            }
            catch (ApiException ex) when (request.Images > 0 && LooksLikeImageRejection(ex))
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
                                           "с большей памятью и нажмите «Повторить» — переписка сохранена.\n\n" + ex.Message,
                        ex.StatusCode);
                ResetModelProgress();
                int cap = HistoryCompressor.EstimateAll(request.Messages) + HistoryCompressor.Estimate(definitions) +
                          request.Images * ImageTokens - HistoryCompressor.EstimateAll(request.Messages.Skip(1)) / 4;
                if (cap < InputBudget(model)) _inputCaps[model] = cap;
                harder = true;
            }
        }
    }

    private void ResetModelProgress()
    {
        AssistantDelta?.Invoke(null, false);
        ProgressReported?.Invoke(new());
    }

    private int InputBudget(string model)
    {
        long budget = Math.Min(MaxInputTokens, _inputCaps.GetValueOrDefault(model, int.MaxValue));
        if (ModelContextTokens?.Invoke(model) is > 0 and var window)
            budget = Math.Min(budget, window - Math.Min(MaxTokens ?? AnswerReserve, window / 2));
        return (int)budget;
    }

    private static bool IsContextOverflow(ApiException ex)
    {
        if (ex.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity))
            return false;
        string[] hints =
        [
            "context_length", "context length", "context window", "maximum context", "too many tokens", "prompt is too long",
            "input is too long", "reduce the length", "token limit", "контекст",
        ];
        return hints.Any(hint => ex.Message.Contains(hint, StringComparison.OrdinalIgnoreCase) ||
                                 (ex.Code?.Contains(hint, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    private static bool LooksLikeEffortRejection(ApiException ex) =>
        ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity &&
        (ex.Message.Contains("reasoning", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("effort", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeImageRejection(ApiException ex)
    {
        if (ex.StatusCode == HttpStatusCode.RequestEntityTooLarge) return ex.Code != "rate_limit_exceeded";
        if (ex.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.NotFound
            or HttpStatusCode.UnprocessableEntity or HttpStatusCode.UnsupportedMediaType))
            return false;
        string[] hints = ["image", "vision", "multimodal", "content must be a string", "изображен", "картин"];
        return hints.Any(hint => ex.Message.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Request> BuildRequestAsync(string model, JsonArray definitions, bool withImages, bool harder, CancellationToken ct)
    {
        var system = SystemMessage();
        var imagesToShow = new HashSet<(JsonObject Message, string Path)>();
        if (withImages)
        {
            int budget = MaxImagesPerRequest;
            for (int i = _history.Count - 1; i >= 0 && budget > 0; i--)
            {
                if (_history[i] is not JsonObject m || JsonUtil.Str(m["role"]) != "user") continue;
                var images = Attachment.ListFrom(m).Where(a => a.Kind == AttachmentKind.Image).ToList();
                for (int k = images.Count - 1; k >= 0 && budget > 0; k--, budget--) imagesToShow.Add((m, images[k].Path));
            }
        }
        int room = InputBudget(model) - HistoryCompressor.Estimate(system) - HistoryCompressor.Estimate(definitions) -
                   imagesToShow.Count * ImageTokens;
        var note = RequestTail(harder);
        var project = await ProjectContextAsync(Math.Clamp(room * 3 / (harder ? 12 : 5), 0, harder ? 2200 : 11_000), ct);
        room -= ((note?.Length ?? 0) + (project?.Length ?? 0)) / 3;
        var projected = _history.Select(WithVerificationSummary).ToArray();
        bool compress = harder || HistoryCompressor.EstimateAll(projected) > room;
        IReadOnlyList<JsonNode> source = compress ? HistoryCompressor.Compress(projected, room, harder) : projected;

        var list = new List<JsonNode>(source.Count + 1) { system };
        int shown = 0;
        foreach (var node in source)
        {
            if (node is not JsonObject message)
            {
                list.Add(node);
            }
            else if (message.ContainsKey(AttachmentsField) && JsonUtil.Str(message["role"]) == "user")
            {
                var (converted, count) = await ToApiUserMessageAsync(message, path => imagesToShow.Contains((message, path)), ct);
                list.Add(converted);
                shown += count;
            }
            else if (message[MetaField] is JsonObject meta)
            {
                var clean = (JsonObject)message.DeepClone();
                clean.Remove(MetaField);
                if (Provider == AgentProvider.ChatGpt && JsonUtil.Str(message["role"]) == "assistant")
                {
                    if (meta["commentary"] is { } commentary) clean["commentary"] = commentary.DeepClone();
                    if (meta["phase"] is { } phase) clean["phase"] = phase.DeepClone();
                }
                list.Add(clean);
            }
            else
            {
                list.Add(message);
            }
        }
        if (project != null)
        {
            int user = list.FindLastIndex(node => node is JsonObject m && JsonUtil.Str(m["role"]) == "user");
            if (user >= 0) list[user] = WithContext((JsonObject)list[user], "\n\n" + project);
        }
        if (note != null)
        {
            int last = list.FindLastIndex(node => node is JsonObject m && JsonUtil.Str(m["role"]) is "tool" or "user");
            if (last >= 0) list[last] = WithContext((JsonObject)list[last], note);
        }
        return new Request(list, shown, compress);
    }

    private JsonObject SystemMessage()
    {
        var key = tools.Context.WorkingDirectory + "|" + tools.Context.SandboxRoot + "|" + DateTime.Now.ToString("yyyy-MM-dd") +
                  "|" + UseSubagents + "|" + (ClarifyAsync != null);
        if (_systemMessage == null || _systemKey != key)
        {
            _systemMessage = new JsonObject
            {
                ["role"] = "system",
                ["content"] = (delegated ? SubagentPrompt : SystemPrompt.Build(tools.Context,
                    PluginTools.Describe(AvailablePlugins()))) + InteractionPrompt(),
            };
            _systemKey = key;
        }
        return _systemMessage;
    }

    private async Task<(JsonObject Message, int Images)> ToApiUserMessageAsync(JsonObject message,
        Func<string, bool> showImage, CancellationToken ct)
    {
        var text = new StringBuilder((ExtractText(message["content"]) ?? "").TrimEnd());
        var parts = new List<JsonNode>();
        var attachments = Attachment.ListFrom(message);
        if (attachments.Count > 0)
        {
            if (text.Length > 0) text.Append("\n\n");
            text.Append("[Пользователь приложил к сообщению файлы и папки. Работай с ними по этим путям:]");
            foreach (var attachment in attachments)
            {
                var note = "";
                if (attachment.Kind == AttachmentKind.Image && showImage(attachment.Path))
                {
                    var dataUrl = await GetImageAsync(attachment.Path, ct);
                    if (dataUrl != null)
                    {
                        note = "; само изображение показано тебе ниже";
                        parts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = dataUrl },
                        });
                    }
                    else
                    {
                        note = "; открыть как изображение не удалось";
                    }
                }
                text.Append($"\n- {attachment.Path} ({attachment.Describe()}{note})");
            }
        }

        var result = new JsonObject { ["role"] = "user" };
        if (parts.Count == 0)
        {
            result["content"] = text.ToString();
        }
        else
        {
            var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text.ToString() } };
            foreach (var part in parts) content.Add(part);
            result["content"] = content;
        }
        return (result, parts.Count);
    }

    private async Task<string?> GetImageAsync(string path, CancellationToken ct)
    {
        string key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return null;
        }

        if (_imageCache.TryGetValue(key, out var cached)) return cached;
        var dataUrl = await Task.Run(() => ImageEncoder.ToDataUrl(path), ct);
        _imageCache[key] = dataUrl;
        return dataUrl;
    }

    private async Task<string?> ExecuteToolCallsAsync(JsonArray toolCalls, CancellationToken ct)
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
                    var (call, outcome) = (batch[k], outcomes[k]);
                    var execution = executions[k];
                    bool reused = execution.PreviousId != null;
                    bool repeat = Repeats(call, outcome, counted, execution.Key?.Identity, reused) >= RepeatHint;
                    var output = ResultForModel(execution, call, outcome, k, executions);
                    if (repeat && outcome.Restriction == null) output += RepeatReply;
                    var message = ToolMessage(call.Id, output, outcome.Diff);
                    if (reused) message[MetaField] = new JsonObject { ["action_reused"] = true };
                    if (call.Name == "run_command" && VerificationCommand(call))
                        message[MetaField] = new JsonObject { ["verification"] = true };
                    if (outcome.Restriction != null)
                    {
                        if (message[MetaField] is not JsonObject meta) message[MetaField] = meta = new JsonObject();
                        meta["command_restricted"] = true;
                    }
                    if (outcome.DelegatedUsage is { } usage)
                        message[MetaField] = new JsonObject
                        {
                            ["prompt_tokens"] = usage.PromptTokens, ["completion_tokens"] = usage.CompletionTokens,
                            ["provider"] = Provider.ToString(), ["model"] = CurrentModel,
                        };
                    if (!reused && !outcome.IsError && !outcome.IsWarning)
                    {
                        RecordFileChange(message, outcome.Change);
                        RecordCompletedWrite(call, message, outcome.Change);
                    }
                    _history.Add(message);
                    done++;
                    if (!reused)
                        finished.Add(new ToolResultEvent(call.Id, call.Name, output, outcome.IsError, outcome.IsWarning, outcome.Diff)
                            { Change = outcome.Change });
                    if (!reused && outcome.Restriction?.UnavailableShell == null &&
                        !ToolRegistry.IsReadOnly(call.Name) && call.Name is not ("ask_user" or "delegate_tasks"))
                    {
                        InvalidateReads(call.Name);
                        InvalidateProjectContext(call);
                    }
                }
                Save();
                foreach (var result in finished) ToolCallFinished?.Invoke(result);
            }
        }
        finally
        {
            for (int i = done; i < calls.Count; i++)
                _history.Add(ToolMessage(calls[i].Id, "Отменено пользователем."));
            if (done < calls.Count) Save();
        }
        foreach (var (key, call) in counted)
            if (_repeats.GetValueOrDefault(key) is { Count: >= RepeatStop } repeated)
                return key.StartsWith("shell:", StringComparison.Ordinal)
                    ? "Агент остановлен. " + CommandRestriction.Prefix + repeated.Output
                    : $"Агент остановлен: модель {repeated.Count} раза запросила одно и то же действие " +
                       $"({HistoryCompressor.Describe(call.Name, call.ArgumentsJson)}). Уточните задачу или нажмите «Продолжить».";
        return null;
    }

    private static ToolCallEvent ParseCall(JsonObject call)
    {
        var function = call["function"] as JsonObject;
        return new ToolCallEvent(JsonUtil.Str(call["id"])!, JsonUtil.Str(function?["name"]) ?? "",
            JsonUtil.Str(function?["arguments"]) ?? "{}");
    }

    private int Repeats(ToolCallEvent call, ToolOutcome outcome, Dictionary<string, ToolCallEvent> counted, string? identity, bool reused)
    {
        if (!reused && outcome.Restriction == null && !ToolRegistry.IsReadOnly(call.Name))
        {
            if (!outcome.IsError) _repeats.Clear();
            return 0;
        }
        string key = outcome.Restriction?.UnavailableShell is { } shell ? "shell:" + shell : identity ?? CallKey(call);
        if (!counted.TryAdd(key, call)) return 0;
        bool known = _repeats.TryGetValue(key, out var seen);
        if (!known)
            foreach (var other in _repeats.Keys.ToList()) _repeats[other] = (_repeats[other].Output, 0);
        var output = outcome.Restriction?.Message ?? outcome.Output;
        int count = known && seen.Output == output ? seen.Count + 1 : 1;
        _repeats[key] = (output, count);
        return count;
    }

    private static string CallKey(ToolCallEvent call)
    {
        try
        {
            return call.Name + " " + (JsonNode.Parse(call.ArgumentsJson)?.ToJsonString() ?? "");
        }
        catch (JsonException)
        {
            return call.Name + " " + call.ArgumentsJson;
        }
    }

    private static JsonObject ToolMessage(string id, string content, FileDiff? diff = null)
    {
        var message = new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = content };
        if (diff != null)
            message[MetaField] = new JsonObject { ["diff"] = diff.Unified, ["added"] = diff.Added, ["removed"] = diff.Removed };
        return message;
    }

    public static FileDiff? DiffOf(JsonObject toolMessage) =>
        toolMessage[MetaField] is JsonObject meta && JsonUtil.Str(meta["diff"]) is { } diff
            ? new FileDiff(diff, (int)JsonUtil.Long(meta["added"]), (int)JsonUtil.Long(meta["removed"]))
            : null;

    public static TimeSpan? ElapsedOf(JsonObject message) =>
        message[MetaField] is JsonObject meta && meta[ElapsedField] != null
            ? TimeSpan.FromMilliseconds(JsonUtil.Long(meta[ElapsedField]))
            : null;

    internal static string? StopReasonOf(JsonObject message) => JsonUtil.Str((message[MetaField] as JsonObject)?[StopField]);

    internal void RecordStop(string text)
    {
        if (_history.LastOrDefault() is JsonObject last)
        {
            if (last[MetaField] is not JsonObject meta) last[MetaField] = meta = new JsonObject();
            meta[StopField] = text;
            Save();
        }
        Stopped?.Invoke(text);
    }

    public static ModelProgress ProgressOf(JsonObject message)
    {
        var meta = message[MetaField] as JsonObject;
        return new ModelProgress(JsonUtil.Str(meta?["reasoning"]) ?? JsonUtil.Str(message["reasoning_content"]) ??
            JsonUtil.Str(message["reasoning"]), JsonUtil.Str(meta?["commentary"]));
    }

    private static void NormalizeToolCalls(JsonArray toolCalls)
    {
        foreach (var call in toolCalls.OfType<JsonObject>())
        {
            if (string.IsNullOrEmpty(JsonUtil.Str(call["id"])))
                call["id"] = "call_" + Guid.NewGuid().ToString("N")[..16];
            call["type"] ??= "function";

            if (call["function"] is JsonObject function)
            {
                var args = function["arguments"];
                if (args == null) function["arguments"] = "{}";
                else if (args is not JsonValue v || !v.TryGetValue<string>(out _))
                    function["arguments"] = args.ToJsonString();
            }
        }
    }

    private static void RepairHistory(List<JsonNode> history)
    {
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i] is not JsonObject msg || JsonUtil.Str(msg["role"]) != "assistant" ||
                msg["tool_calls"] is not JsonArray calls)
                continue;

            int j = i + 1;
            var answered = new HashSet<string>();
            while (j < history.Count && history[j] is JsonObject t && JsonUtil.Str(t["role"]) == "tool")
            {
                answered.Add(JsonUtil.Str(t["tool_call_id"]) ?? "");
                j++;
            }

            foreach (var call in calls.OfType<JsonObject>())
            {
                var id = JsonUtil.Str(call["id"]) ?? "";
                if (answered.Contains(id)) continue;
                history.Insert(j++, ToolMessage(id, "Выполнение было прервано (программа закрыта или остановлена)."));
            }
            i = j - 1;
        }
    }

    public static string? ExtractText(JsonNode? content) => content switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray parts => string.Concat(parts.Select(p => p is JsonObject o ? JsonUtil.Str(o["text"]) : null)),
        _ => content.ToJsonString(),
    };

    public static string? TitleOf(IEnumerable<JsonNode> messages)
    {
        var first = messages.OfType<JsonObject>().FirstOrDefault(m => JsonUtil.Str(m["role"]) == "user");
        if (first == null) return null;
        var text = ExtractText(first["content"])?.ReplaceLineEndings(" ").Trim();
        if (!string.IsNullOrEmpty(text)) return text;
        return Attachment.ListFrom(first).FirstOrDefault()?.Name;
    }

    public ChatTotals Totals()
    {
        long prompt = 0, completion = 0, context = 0;
        var byModel = new Dictionary<(AgentProvider, string), long>();
        foreach (var message in _history.OfType<JsonObject>())
        {
            if (message[MetaField] is not JsonObject meta || meta["prompt_tokens"] == null) continue;
            long p = JsonUtil.Long(meta["prompt_tokens"]), c = JsonUtil.Long(meta["completion_tokens"]);
            prompt += p;
            completion += c;
            if (JsonUtil.Str(message["role"]) == "assistant") context = p + c;
            var provider = Enum.TryParse<AgentProvider>(JsonUtil.Str(meta["provider"]), true, out var named) ? named : AgentProvider.Tooken;
            var key = (provider, JsonUtil.Str(meta["model"]) ?? "");
            byModel[key] = byModel.GetValueOrDefault(key) + p + c;
        }
        return new ChatTotals(prompt, completion, context, byModel);
    }

    private void Save()
    {
        if (delegated) return;
        try
        {
            bool created = ChatPath == null;
            ChatPath ??= store.NewPath();
            store.Save(ChatPath, _history, CurrentModel, Title);
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
