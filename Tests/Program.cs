global using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using SharpAgent.Agent;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Tools;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
var root = Path.Combine(AppContext.BaseDirectory, "checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
if (args is ["--benchmark", var report])
{
    await LocalBenchmark.RunAsync(root, report);
    return;
}
Migration(root);
VoiceReplyStreaming();
await VoiceScriptsAsync();
await ComputerInventoryAsync(root);
await ChatReconnectAsync();
await VoiceDownloadResumeAsync(root);
await AppUpdateResumeAsync(root);
await ReplyLanguagesAsync(root);
await NextActionsAsync(root);
await ChatTitlesAsync(root);
await ReadsAndIndexAsync(root);
await AgentBudgetAsync(root);
await DelegatedStepsAsync(root);
RequestComponents();
ResearchLabels();
await ToolExpansionAsync(root);
await RepeatedInspectionAsync(root);
await WebResearchChecks.RunAsync(root);
await SavedOutputsAsync(root);
await CodexWorkflowAsync(root);
await BackgroundCompactionAsync(root);
await ProtectedContextAsync(root);
await ConcurrentTaskAccessAsync(root);
await ContextBenchmarkAsync(root);
await LocalWorkflowsAsync(root);
await WatcherAsync(root);
await SemanticTestsAsync(root);
await ContractAndCoverageAsync(root);
WorkspaceMentions(root);
await UiChecksAsync(root);
await LocalIntelligenceChecksAsync(root);
await FinalLocalChecks.RunAsync(root);
MemoryAndCache(root);
if (Environment.GetEnvironmentVariable("SHARP_AGENT_TEST_NATIVE") == "1") await NativeChecks.RunAsync(root);
Console.WriteLine("Sharp Agent: migration, DPAPI, file reads, project index, local routing and model budget passed.");

static async Task ChatReconnectAsync()
{
    var draft = "";
    string? reasoning = null;
    var attempts = 0;
    var result = await ApiException.RetryAsync((_, _, text, progress) =>
    {
        attempts++;
        if (attempts == 1)
        {
            text("Незавершённый", false);
            progress(new(Reasoning: "Проверяю"));
        }
        if (attempts < 6)
            throw new ApiException("Disconnected", retryable: true, retryAfter: TimeSpan.Zero) { ConnectionInterrupted = true };
        text("Полный ответ", false);
        return Task.FromResult(new ChatResult(new(), "stop", new(10, 10), null));
    }, _ => false, (text, _) => draft = text == null ? "" : draft + text, progress => reasoning = progress.Reasoning,
        (attempt, total) => Check(draft == "Незавершённый" && reasoning == "Проверяю" && attempt <= total,
            "retries preserve the draft and progress until the replacement response starts"), default, recoverConnection: true);
    Check(draft == "Полный ответ" && reasoning == null && result.Usage?.Requests == 6,
        "a recovered response replaces the interrupted draft and counts every request");
    using var stop = new CancellationTokenSource();
    attempts = 0;
    try
    {
        await ApiException.RetryAsync((_, _, _, _) =>
        {
            attempts++;
            throw new HttpRequestException(HttpRequestError.ConnectionError, "Disconnected");
        }, _ => false, (_, _) => { }, _ => { }, (_, _) => stop.Cancel(), stop.Token, recoverConnection: true);
        throw new Exception("Reconnect should have been cancelled");
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested)
    {
        Check(attempts == 1, "Stop cancels reconnect before another request starts");
    }
}

static void Migration(string root)
{
    var old = Path.Combine(root, "old");
    var data = Path.Combine(old, "data");
    var stage = data + ".migrating";
    Put(Path.Combine(stage, ".migration-version"), "1");
    Put(Path.Combine(stage, "outdated"), "discarded");
    Put(Path.Combine(old, ".env"), "OPENAI_API_KEY=old-dummy\nOPENAI_API_KEY=new-dummy\nAGENT_LANGUAGE=ru");
    Put(Path.Combine(old, "auth", "chatgpt.json"), """{"client_id":"dummy"}""");
    Put(Path.Combine(old, "history", "chat_test.json"), "{}");
    Put(Path.Combine(old, "history", "logs", "test.log"), "log");
    Put(Path.Combine(old, "history", "changes", "test.bak"), "backup");
    AppPaths.Migrate(old, data, true);
    Check(File.Exists(Path.Combine(old, "auth", "chatgpt.json")), "legacy source kept");
    Check(!Directory.Exists(stage) && !File.Exists(Path.Combine(data, "outdated")), "restart from interrupted stage");
    Check(File.Exists(Path.Combine(data, "history", "chats", "chat_test.json")) &&
        File.Exists(Path.Combine(data, "logs", "tools", "test.log")) &&
        File.Exists(Path.Combine(data, "checkpoints", "test.bak")), "history layout");
    var env = Path.Combine(data, ".env");
    Check(!File.ReadAllText(env).Contains("-dummy") && DotEnv.Parse(env)["OPENAI_API_KEY"] == "new-dummy", "protect duplicate secret values");
    var auth = File.ReadAllText(Path.Combine(data, "auth", "chatgpt.json"));
    Check(!auth.Contains("dummy") && AppPaths.ReadAuth(auth).Contains("dummy"), "protect and read auth");
    Put(Path.Combine(old, "history", "chat_test.json"), "changed");
    AppPaths.Migrate(old, data, true);
    Check(File.ReadAllText(Path.Combine(data, "history", "chats", "chat_test.json")) == "{}", "completed migration is idempotent");
    var partial = Path.Combine(root, "partial");
    Put(Path.Combine(partial, ".env"), "TOKEN=legacy");
    Put(Path.Combine(partial, "data", "state.json"), "{}");
    bool incompleteRejected = false;
    try { AppPaths.Migrate(partial, Path.Combine(partial, "data"), true); }
    catch (IOException) { incompleteRejected = true; }
    Check(incompleteRejected && File.Exists(Path.Combine(partial, ".env")) &&
        File.Exists(Path.Combine(partial, "data", "state.json")), "partial destination is never silently accepted or overwritten");
}

static async Task ReadsAndIndexAsync(string root)
{
    var working = Path.Combine(root, "project");
    var data = Path.Combine(root, "private", "data");
    var context = new ToolContext { WorkingDirectory = working, AppFolder = data, LogsFolder = Path.Combine(data, "logs") };
    Directory.CreateDirectory(context.CodeIndexFolder);
    Put(Path.Combine(data, ".env"), "PRIVATE=secret");
    var path = Path.Combine(working, "Проект.cs");
    Put(path, "class Example\n{\n    public int ExampleMethod() => 1;\n}");
    var tools = new ToolRegistry(context);
    var files = new JsonArray(
        new JsonObject { ["path"] = path, ["offset"] = 3, ["limit"] = 1 },
        new JsonObject { ["path"] = path, ["offset"] = 2, ["limit"] = 2 },
        new JsonObject { ["path"] = Path.Combine(data, ".env") });
    var args = new JsonObject { ["files"] = files }.ToJsonString();
    var first = await tools.ExecuteAsync("local_batch", LocalBatch("ranges", args), null, default);
    Check(first.Output.Contains("=> 1;") && !first.Output.Contains("PRIVATE") && first.Output.Contains("Ошибка"), "batch execution and secret guard");
    Check(first.Output.Split("=> 1;").Length == 2, "overlapping line ranges are returned once");
    bool roslynInstalled = LocalDeveloperTools.ParserVersion.Length > 0;
    var index = await ProjectSourceIndex.UpdateAsync(context, working, context.CodeIndexFolder, ["Проект.cs"], true, default);
    Check(index["Проект.cs"].Syntax.Symbols.Any(symbol => symbol.Name == "ExampleMethod"), "indexed C# symbol");
    Check(!roslynInstalled || index["Проект.cs"].Roslyn, "Roslyn index when SDK is usable");
    Check(File.Exists(Path.Combine(context.DatabaseFolder, "projects.db")), "system SQLite database");
    var largeDiff = "@@ -1,1 +1,1 @@\n" + string.Concat(Enumerable.Range(0, 80).Select(i => "+value" + i + "\n"));
    var preview = AgentRunner.EditPreview(largeDiff);
    Check(preview != null && preview.Contains("неполный diff") && preview.Contains("пропущено"),
        "large edit previews remain explicitly partial rather than disappearing");
    var secondIndex = await ProjectSourceIndex.UpdateAsync(context, working, context.CodeIndexFolder, ["Проект.cs"], true, default);
    Check(index["Проект.cs"].Hash == secondIndex["Проект.cs"].Hash, "read persisted index");
    Put(path, "class Example\n{\n    public int ExampleMethod() => 22;\n}");
    var second = await tools.ExecuteAsync("local_batch", LocalBatch("ranges", args), null, default);
    Check(second.Output.Contains("=> 22;"), "batch reads refresh file snapshots");
    var removed = await ProjectSourceIndex.UpdateAsync(context, working, context.CodeIndexFolder, [], true, default);
    Check(removed.Count == 0, "deleted entries removed");
}

static async Task AgentBudgetAsync(string root)
{
    var context = new ToolContext { WorkingDirectory = root, AppFolder = Path.Combine(root, "private", "data") };
    var store = new ChatStore(Path.Combine(root, "chats"), Path.Combine(root, "logs"));
    var agent = new AgentRunner(new ToolRegistry(context), store) { IsTemporary = true, RussianLanguage = true };
    bool received = false;
    agent.AssistantText += (text, _, _) => received |= text == root;
    await agent.RunAsync("покажи рабочую папку", [], "fake", default);
    Check(received, "local route without provider");
    using var provider = new FakeProvider(100_000, 4);
    agent.Api = provider;
    string? stop = null;
    agent.Stopped += text => stop = text;
    await agent.RunAsync("Исследуй файлы", [], "fake", default);
    Check(provider.Calls == 5 && stop == null && !agent.CanContinue, "usage does not impose a fixed turn budget");
    var totals = agent.Totals();
    Check(totals.PromptTokens == 500_000 && totals.CachedTokens == 5_000 && totals.ReasoningTokens == 50 && totals.Requests == 5, "telemetry retains cache and reasoning");
    using var cheap = new FakeProvider(30, 32);
    agent.Api = cheap;
    await agent.RunAsync("Исследуй файлы", [], "fake", default);
    Check(cheap.Calls == 33 && stop == null && !agent.CanContinue, "agent completes more than 24 tool steps");
}

static async Task ContextBenchmarkAsync(string root)
{
    for (int i = 1; i <= 24; i++)
        Put(Path.Combine(root, $"source_{i}.txt"), string.Join("\n", Enumerable.Repeat("Проверяемый фрагмент исходного кода " + new string('x', 360), 70)));
    var context = new ToolContext { WorkingDirectory = root, AppFolder = Path.Combine(root, "private", "data") };
    using var provider = new FakeProvider(null, 24);
    var agent = new AgentRunner(new ToolRegistry(context), new ChatStore(Path.Combine(root, "chats"), Path.Combine(root, "logs")))
        { Api = provider, IsTemporary = true };
    var timer = System.Diagnostics.Stopwatch.StartNew();
    await agent.RunAsync("sentinel: сохрани исходное требование", [], "fake", default);
    Check(provider.Calls == 25 && provider.PeakInput < 27_000 && !agent.CanContinue, "large tool results fit without a summary when local previews suffice");
    Console.WriteLine($"Context replay: {provider.Calls} requests, {provider.TotalInput:N0} estimated input, {provider.PeakInput:N0} peak, {timer.ElapsedMilliseconds} ms.");
}

static async Task DelegatedStepsAsync(string root)
{
    using var provider = new SubagentProvider();
    var context = new ToolContext { WorkingDirectory = root, AppFolder = Path.Combine(root, "subagent-data") };
    var agent = new AgentRunner(new ToolRegistry(context), new ChatStore(Path.Combine(root, "subagent-history"), root))
        { Api = provider, IsTemporary = true, UseSubagents = true };
    await agent.RunAsync("Use a subagent to review fixture paths.", [], "fake", default);
    Check(provider.WorkerCalls == 7 && !agent.CanContinue && agent.History.Any(message =>
        AgentRunner.Role(message) == "tool" && message["content"]?.ToString().Contains("All fixture paths checked.") == true),
        "delegated work completes beyond the old step cap");
}

static async Task ChatTitlesAsync(string root)
{
    const string question = "Почему у листьев меняется цвет осенью?", title = "Осенняя окраска листьев";
    var context = new ToolContext { WorkingDirectory = root, AppFolder = Path.Combine(root, "private", "data") };
    var store = new ChatStore(Path.Combine(root, "titles"), Path.Combine(root, "logs"));
    var result = new ChatResult(new JsonObject { ["content"] = $"«{title}»" }, "stop", new UsageInfo(8, 3), null);
    using var provider = new TitleProvider(Task.FromResult(result));
    var agent = new AgentRunner(new ToolRegistry(context), store) { Api = provider };
    int changes = 0;
    agent.ChatTitleChanged += () => changes++;
    await agent.RunAsync(question, [], "fake", default);
    Check(agent.Title == title && store.Load(agent.ChatPath!).Title == title && provider.Subject == question && changes == 1 &&
        agent.Totals().Requests == 1, "generated title is saved and its usage is recorded");
    await agent.RunAsync(question, [], "fake", default);
    agent.NewChat();
    agent.AutoChatTitles = false;
    await agent.RunAsync(question, [], "fake", default);
    Check(agent.DisplayTitle == question && provider.TitleCalls == 1, "only a new chat with automatic titles enabled requests a title");
    var pending = new TaskCompletionSource<ChatResult>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var delayed = new TitleProvider(pending.Task);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    agent.NewChat();
    agent.AutoChatTitles = true;
    agent.Api = delayed;
    var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    agent.AssistantText += (_, _, _) => answered.TrySetResult();
    var run = agent.RunAsync(question, [], "fake", timeout.Token);
    await answered.Task.WaitAsync(timeout.Token);
    Check(agent.DisplayTitle == question, "a pending title shows the first message immediately");
    await agent.RenameAsync("Моё название");
    pending.SetResult(result);
    await run;
    Check(delayed.TitleCalls == 1 && agent.Title == "Моё название" && store.Load(agent.ChatPath!).Title == agent.Title && changes == 1,
        "a pending automatic title allows the answer and preserves a manual rename");
    const string greeting = "але тут тыт?";
    using var echo = new TitleProvider(Task.FromResult(new ChatResult(new JsonObject { ["content"] = "«але тут тыт»" }, "stop", null, null)));
    agent.NewChat();
    agent.Api = echo;
    agent.Provider = AgentProvider.ChatGpt;
    agent.RussianLanguage = true;
    await agent.RunAsync(greeting, [], "fake", default);
    var path = agent.ChatPath!;
    Check(echo.TitleCalls == 1 && echo.TitleTokenLimit == null && echo.TitlePrompt?.Contains("use Russian.") == true &&
        echo.ReplyPrompt?.Contains("Otherwise reply in Russian.") == true && agent.Title == null && agent.DisplayTitle == greeting && store.Load(path).Title == null,
        "a rejected automatic title leaves the first message as the display title");
    var pendingTitle = store.Load(path);
    await pendingTitle.Messages.SaveAsync(path, pendingTitle.Model, "Новый чат", pendingTitle.WorkMode);
    Check(store.Load(path).Title == null && ChatStore.Summaries([new FileInfo(path)]).Single().Title == greeting,
        "legacy pending titles use the first message in opened chats and the sidebar");
    using var recovered = new TitleProvider(Task.FromResult(new ChatResult(new JsonObject { ["content"] = "Проверка связи" }, "stop", null, null)));
    agent.Api = recovered;
    await agent.OpenAsync(path);
    await agent.RunAsync("Да, проверяю связь.", [], "fake", default);
    Check(recovered.Subject == greeting && recovered.TitleCalls == 1 && store.Load(path).Title == "Проверка связи",
        "a pending title is retried after a reload and the generated topic is saved");
    await agent.RunAsync("Спасибо.", [], "fake", default);
    Check(recovered.TitleCalls == 1, "completed titles are not generated again");
}

static void Put(string path, string value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, value);
}

static string LocalBatch(string operation, string arguments) => new JsonObject
{
    ["operations"] = new JsonArray(new JsonObject { ["name"] = operation, ["arguments"] = JsonNode.Parse(arguments) }),
}.ToJsonString();

static void ResearchLabels()
{
    var names = new[] { "index", "search", "inspect", "related", "duplicates" };
    var labels = names.Select(action => SharpAgent.MainWindow.Describe(LocalResearchEngine.Name, new JsonObject { ["action"] = action }.ToJsonString())).ToArray();
    Check(labels.Select(item => item.Label).Distinct().Count() == names.Length &&
        labels.All(item => item.Glyph == SharpAgent.Ui.Glyphs.Search && !item.Label.Contains(LocalResearchEngine.Name)) &&
        SharpAgent.MainWindow.Describe("unknown_tool").Label != "unknown_tool",
        "chat rows show a readable label for each research action and for unrecognized tools");
}

static async Task CodexWorkflowAsync(string root)
{
    var work = Path.Combine(root, "codex-workflow");
    var context = new ToolContext { WorkingDirectory = work, AppFolder = Path.Combine(root, "codex-data"), LogsFolder = Path.Combine(root, "codex-logs") };
    var registry = new ToolRegistry(context);
    var names = registry.Definitions.Select(item => item?["function"]?["name"]?.ToString()).ToArray();
    Check(names.Contains("exec_command") && names.Contains("apply_patch") &&
        !names.Intersect(["search_text", "read_file", "read_many", "edit_file", "run_command"]).Any(), "old tool schemas removed");
    Check(SystemPrompt.Base(true).Contains("apply_patch") && !SystemPrompt.Base(false).Contains("apply_patch") &&
        SystemPrompt.Base(false).Length < SystemPrompt.Base(true).Length, "Work and Chat own embedded prompts");
    var source = Path.Combine(work, "source.txt");
    Put(source, "alpha\r\nneedle\r\nomega\r\n");
    Put(Path.Combine(work, "move.txt"), "move");
    Put(Path.Combine(work, "old.txt"), "delete");
    async Task<ToolOutcome> Patch(string patch, bool allow = true) => await registry.ExecuteAsync("apply_patch",
        new JsonObject { ["patch"] = patch }.ToJsonString(), _ => Task.FromResult(allow), default);
    var invalid = await Patch("""
        *** Begin Patch
        *** Add File: should-not-exist
        +text
        *** Update File: source.txt
        @@
        -missing
        +updated
        *** End Patch
        """);
    Check(invalid.IsError && !File.Exists(Path.Combine(work, "should-not-exist")), "invalid multi-file patch leaves the entire workspace unchanged");
    var patch = """
        *** Begin Patch
        *** Update File: source.txt
        @@
         alpha
        -needle
        +marker
         omega
        *** Add File: created.txt
        +created
        *** Update File: move.txt
        *** Move to: nested/moved.txt
        *** Delete File: old.txt
        *** End Patch
        """;
    Check((await Patch(patch, false)).Output.StartsWith("Пользователь отклонил") && File.Exists(Path.Combine(work, "old.txt")), "patch confirms deletions");
    var applied = await Patch(patch);
    Check(applied.Changes?.Length == 5 && File.ReadAllText(source) == "alpha\r\nmarker\r\nomega\r\n" &&
        File.Exists(Path.Combine(work, "nested", "moved.txt")) && !File.Exists(Path.Combine(work, "old.txt")), "multi-file patch preserves CRLF and records every path");
    foreach (var change in applied.Changes!.Reverse()) FileChanges.Restore(context, change);
    Check(File.ReadAllText(source).Contains("needle") && File.Exists(Path.Combine(work, "old.txt")) &&
        File.Exists(Path.Combine(work, "move.txt")) && !File.Exists(Path.Combine(work, "created.txt")), "multi-file changes can be undone");
    var bundled = await registry.ExecuteAsync("exec_command", """{"command":"rg -n -C 1 needle ."}""", _ => Task.FromResult(true), default);
    Check(CommandLog.ResultOf(bundled.Output) == "код 0" && bundled.Output.Contains("source.txt") && bundled.Output.Contains("omega"), "packaged search returns matching lines and context");
    var resolved = await registry.ExecuteAsync("exec_command", """{"command":"(Get-Command rg).Source"}""", _ => Task.FromResult(true), default);
    Check(resolved.Output.Contains(Path.Combine(AppContext.BaseDirectory, "tools", "ripgrep", "rg.exe"), StringComparison.OrdinalIgnoreCase),
        "the shell finds published ripgrep ahead of any host installation");
    Check(LocalGrep.Run("rg -n -C 1 needle .", work, context, default) is { Code: 0, Output: var fallback } && fallback.Contains("omega"),
        "managed search preserves context");
    var blocked = "Program 'rg.exe' failed to run: This program is blocked by group policy.";
    Check(CommandRestriction.RipgrepLaunchFailure(blocked,
        CommandRestriction.FromOutput(CommandShell.PowerShell, "", blocked, null, 1)) != null &&
        CommandRestriction.RipgrepLaunchFailure(blocked, new("Shell disabled", CommandShell.PowerShell)) == null &&
        CommandRestriction.RipgrepLaunchFailure("rg: source.txt: Access is denied", null) == null,
        "fallback is limited to a failure to launch ripgrep");
    var pending = await registry.ExecuteAsync("exec_command", """{"command":"Start-Sleep -Milliseconds 1400; Write-Output finished","yield_ms":100}""",
        _ => Task.FromResult(true), default);
    var id = ShellTools.Sessions.RunningId(pending.Output);
    Check(id != null && ShellTools.Sessions.RunningId(pending.Output + "\nextra") == null, "only the exact pending response identifies a command session");
    var pollArgs = new JsonObject { ["session_id"] = id, ["yield_ms"] = 100 }.ToJsonString();
    Check(SharpAgent.MainWindow.CommandText("""{"command":"dotnet build"}""") == "dotnet build" &&
        SharpAgent.MainWindow.CommandText("""{"commands":["rg --files","git status"]}""") == "rg --files\ngit status" &&
        SharpAgent.MainWindow.CommandText(pollArgs, "completed command") == "completed command",
        "collapsed tool rows retain single, batch and session command text");
    var finished = await registry.ExecuteAsync("exec_command", pollArgs,
        _ => throw new InvalidOperationException("Polling must not ask for command approval"), default);
    Check(CommandLog.ResultOf(finished.Output) == "код 0" && finished.Output.Contains("finished"), "one poll waits locally for the final result");
    Put(Path.Combine(work, "Wait.proj"), """
        <Project DefaultTargets="Build">
          <Target Name="Build">
            <Exec Command="powershell -NoProfile -Command &quot;Start-Sleep -Milliseconds 600&quot;" />
            <Message Text="verification-finished" Importance="high" />
          </Target>
        </Project>
        """);
    var verification = await registry.ExecuteAsync("exec_command", """{"command":"dotnet build Wait.proj --no-restore --nologo","yield_ms":100}""",
        _ => Task.FromResult(true), default);
    Check(verification.Output.Contains("verification-finished") && CommandLog.ResultOf(verification.Output) == "код 0" &&
        registry.PendingCommandOutput == null, "verification returns the final output even with a short yield");
    var sandbox = new ToolRegistry(new ToolContext { WorkingDirectory = work, AppFolder = context.AppFolder, SandboxRoot = work });
    Check((await sandbox.ExecuteAsync("exec_command", """{"command":"rg --files"}""", null, default)).IsError, "sandbox forbids execution and its fallback");
}

static void RequestComponents()
{
    var meter = new RequestInputMeter();
    JsonArray tools = new(new JsonObject { ["function"] = "test" });
    JsonNode[] messages =
    [
        new JsonObject { ["role"] = "system", ["content"] = "system instructions" },
        new JsonObject { ["role"] = "user", ["content"] = "request and project data" },
        new JsonObject { ["role"] = "tool", ["content"] = "saved output", ["tool_call_id"] = "call" },
    ];
    var first = meter.Measure(AgentProvider.ChatGpt, "fake", messages, tools, 4, 0);
    var next = meter.Measure(AgentProvider.ChatGpt, "fake", [.. messages, new JsonObject { ["role"] = "user", ["content"] = "later" }], tools, 4, 0);
    Check(first.Total == HistoryCompressor.EstimateAll(messages) + HistoryCompressor.Estimate(tools) && next.Project == 4 &&
        next.StablePrefix == first.Total && next.RepeatedResults == first.Results && next.RepeatedInPrefix == first.Results &&
        next.SchemaVersion == first.SchemaVersion && next.PrefixChange == null && next.History > first.History,
        "component estimates reconcile and matching prefixes exclude new messages");
    JsonArray expanded = [tools[0]?.DeepClone(), new JsonObject { ["function"] = "extra" }];
    var changed = meter.Measure(AgentProvider.ChatGpt, "fake", messages, expanded, 4, 0);
    Check(changed.StablePrefix == 0 && changed.RepeatedResults == first.Results && changed.RepeatedInPrefix == 0 &&
        changed.SchemaVersion != first.SchemaVersion && changed.PrefixChange == "tools_changed", "schema change explains why the prefix was lost");
    Check(LocalActionRouter.Match("""read files "one.cs" and "two.cs" """) == "read_many one.cs\ntwo.cs" &&
        LocalActionRouter.Match("""read files "one.cs" and "two.cs" and explain them""") == null,
        "only explicit batches bypass the model");
}

static async Task ToolExpansionAsync(string root)
{
    var work = Path.Combine(root, "tool-expansion");
    Directory.CreateDirectory(work);
    using var provider = new ExpansionProvider();
    var agent = new AgentRunner(new ToolRegistry(new ToolContext { AppFolder = work, WorkingDirectory = work }),
        new ChatStore(Path.Combine(work, "chats"), work)) { Api = provider, AutoChatTitles = false };
    await agent.RunAsync("Edit the methods in Code.cs and Caller.cs. No tests.", [], "fake", default);
    Check(provider.Definitions.Count == 3, "tool expansion uses one round and keeps subsequent schema stable");
    var initial = provider.Definitions[0];
    var expanded = provider.Definitions[1];
    Check(initial.Any(item => item?["function"]?["name"]?.ToString() == "update_plan"),
        "work tasks expose the plan tool before any tool expansion");
    Check(initial.Any(item => item?["function"]?["name"]?.ToString() == "apply_patch") &&
        !initial.Any(item => item?["function"]?["name"]?.ToString() == "get_system_info") &&
        expanded.Count == initial.Count + 1 && expanded[^1]?["function"]?["name"]?.ToString() == "get_system_info" &&
        expanded.Take(initial.Count).Zip(initial).All(pair => JsonNode.DeepEquals(pair.First, pair.Second)) &&
        ReferenceEquals(expanded, provider.Definitions[2]), "opening one tool appends only that tool and leaves prior definitions unchanged");
    int projections = 0;
    var wire = new ToolSchemaWire(schema => { projections++; return schema; });
    var cached = wire.Get(expanded);
    Check(ReferenceEquals(cached, wire.Get(expanded)) && projections == 1 &&
        JsonNode.DeepEquals(JsonNode.Parse(ToolSchemaWire.Serialize(new JsonObject { ["tools"] = null }, cached))?["tools"], expanded),
        "wire schema is encoded once and retains definition order");
}

static async Task RepeatedInspectionAsync(string root)
{
    var selection = ToolDefinitionSelection.Initial("Измени шрифт в App.xaml");
    Check(selection("local_batch") && selection(PatchTool.Name) && !selection("exec_command") &&
        ToolDefinitionSelection.Initial("Измени шрифт и выполни сборку")("exec_command"),
        "a style change starts with targeted reads and a patch, while an explicit build gets shell access");
    Check(ShellTools.GitInspectionOf(new(new() { ["command"] = "git status --short; git add -- App.xaml" })) == null,
        "a Git inspection followed by a write never enters the read cache");
    var work = Path.Combine(root, "repeat-inspection");
    var data = Path.Combine(root, "repeat-inspection-data");
    Put(Path.Combine(work, "App.xaml"), "FontFamily=Before\n");
    if (LocalDeveloperTools.GitExecutable(work) == null) return;
    var context = new ToolContext { WorkingDirectory = work, AppFolder = data, LogsFolder = Path.Combine(data, "logs") };
    var registry = new ToolRegistry(context);
    var init = await registry.ExecuteAsync("exec_command", """{"command":"git init --quiet; git add -- App.xaml"}""", null, default);
    Check(!init.IsError && !init.IsWarning && Directory.Exists(Path.Combine(work, ".git")), "a local Git fixture is initialized");
    using var watcher = context.ProjectWatcher;
    watcher.Watch(context);
    using var provider = new InspectionProvider();
    var agent = new AgentRunner(registry, new ChatStore(Path.Combine(data, "chats"), context.LogsFolder!))
        { Api = provider, AutoChatTitles = false };
    var checks = new List<string>();
    agent.ToolCallFinished += result =>
    {
        if (result.Name == "exec_command") checks.Add(result.Output);
    };
    await agent.RunAsync("Измени шрифт в App.xaml и проверь изменения Git. Без сборки и тестов.", [], "fake", default);
    Check(provider.Calls == 5 && checks.Count == 2 && !checks[0].Contains("+FontFamily=After") && checks[1].Contains("+FontFamily=After"),
        $"identical Git inspections run once until the file is changed, got {provider.Calls} requests, {checks.Count} checks:\n{string.Join("\n\n", checks)}");
}

static async Task SavedOutputsAsync(string root)
{
    var work = Path.Combine(root, "saved-output");
    var file = Path.Combine(work, "sample.txt");
    Put(file, string.Join("\n", Enumerable.Range(0, 170).Select(i => $"row {i:D3} " + new string('x', 90))));
    var context = new ToolContext { WorkingDirectory = work, AppFolder = Path.Combine(root, "saved-output-data") };
    var tools = new ToolRegistry(context);
    var args = new JsonObject { ["path"] = file }.ToJsonString();
    var preview = await tools.ExecuteAsync("local_batch", LocalBatch("read", args), null, default);
    var duplicate = await tools.ExecuteAsync("local_batch", LocalBatch("read", args), null, default);
    Check(preview.Output.StartsWith(ToolOutputStore.ReferencePrefix) && duplicate.Output == preview.Output &&
        preview.Output.Length < 6000 && !preview.Output.Contains("row 080"), "large reads use a stable compact reference");
    var id = preview.Output[ToolOutputStore.ReferencePrefix.Length..].Split(',')[0];
    var page = new JsonObject { ["id"] = id, ["offset"] = 6500, ["limit"] = 4000 }.ToJsonString();
    var result = await tools.ExecuteAsync(ToolOutputStore.Name, page, null, default);
    Check(!result.IsError && result.Output.Contains("row 080"), "read an omitted range without reopening the source");
    File.AppendAllText(file, "\nchanged content");
    Check((await tools.ExecuteAsync("local_batch", LocalBatch("read", args), null, default)).Output != preview.Output &&
        (await tools.ExecuteAsync(ToolOutputStore.Name, page, null, default)).Output == result.Output, "immutable result survives source change");
    var allowed = new ToolRegistry(new ToolContext { WorkingDirectory = work, AppFolder = context.AppFolder, SandboxRoot = root });
    Check((await allowed.ExecuteAsync(ToolOutputStore.Name, page, null, default)).IsError, "sandbox cannot read results created outside it");
    var batch = await tools.ExecuteAsync("local_batch", """
        {"operations":[{"name":"search_files","arguments":{"pattern":"sample.txt"}},{"name":"diagnostics"}]}
        """, null, default);
    Check(!batch.IsError && batch.Output.Contains("sample.txt") && batch.Output.Contains("diagnostics"), "mixed local operations return ordered independent results");
    var missing = await tools.ExecuteAsync("local_batch", LocalBatch("read", """{"path":"missing.txt"}"""), null, default);
    Check(missing.IsError && !missing.IsWarning && ToolRegistry.IsErrorOutput(missing.Output) && missing.Output.Contains("missing.txt"),
        "a failed batch reports failure and keeps the missing-file error");
    var partial = await tools.ExecuteAsync("local_batch", """
        {"operations":[{"name":"read","arguments":{"path":"sample.txt"}},{"name":"read","arguments":{"path":"missing.txt"}}]}
        """, null, default);
    Check(!partial.IsError && partial.IsWarning && ToolRegistry.IsPartialBatch(partial.Output) &&
        partial.Output.Contains("row 000") && partial.Output.Contains("missing.txt") && ToolOutputStore.ReferenceOf(partial.Output) != null,
        "a partial batch preserves its status, readable results and saved-output reference in a compact preview");
    var ranges = await tools.ExecuteAsync("local_batch", LocalBatch("ranges", """
        {"files":[{"path":"missing.txt"},{"path":"sample.txt","limit":1}]}
        """), null, default);
    Check(!ranges.IsError && ranges.IsWarning && ranges.Output.Contains("row 000") && ranges.Output.Contains("missing.txt"),
        "a ranges operation with partial results marks the batch with a warning");
    var failedGit = await allowed.ExecuteAsync("local_batch", """{"operations":[{"name":"git_status"}]}""", null, default);
    Check(failedGit.IsError && failedGit.Output.Contains("режиме песочницы"), "an unavailable Git check is a failed batch operation");
    var logs = Path.Combine(root, "saved-command-output");
    context.LogsFolder = logs;
    var command = new JsonObject
    {
        ["command"] = "1..1600 | ForEach-Object { if ($_ -eq 800) { 'midpoint-800' } else { 'z' * 800 } }",
        ["shell"] = "powershell", ["working_directory"] = work,
    };
    var logged = await tools.ExecuteAsync("exec_command", command.ToJsonString(), _ => Task.FromResult(true), default);
    var log = CommandLog.IdOf(logged.Output);
    Check(log != null && !logged.Output.Contains("midpoint-800"), "command preview omits ordinary middle lines");
    var logPage = await tools.ExecuteAsync("read_log", new JsonObject { ["log_id"] = log, ["offset"] = 800, ["limit"] = 1 }.ToJsonString(), null, default);
    Check(!logPage.IsError && logPage.Output.Contains("midpoint-800"), "read_log can retrieve a line formerly lost by capture limits");
}

static async Task BackgroundCompactionAsync(string root)
{
    var work = Path.Combine(root, "background-summary");
    Directory.CreateDirectory(work);
    var context = new ToolContext { WorkingDirectory = work, AppFolder = Path.Combine(root, "background-data") };
    var store = new ChatStore(Path.Combine(work, "chats"), work);
    var history = new ChatHistory();
    for (int i = 0; i < 120; i++)
    {
        history.Add(new JsonObject { ["role"] = "user", ["content"] = $"requirement-{i}: retain exact wording" });
        history.Add(new JsonObject { ["role"] = "assistant", ["content"] = $"Decision {i}: " + new string((char)('a' + i % 20), 760) });
    }
    var path = store.NewPath();
    await history.SaveAsync(path, "fake", null, true);
    var failurePath = store.NewPath();
    var failureFixture = new ChatHistory();
    foreach (var message in history) failureFixture.Add(message.DeepClone());
    await failureFixture.SaveAsync(failurePath, "fake", null, true);
    using var shortRun = new CompactionProvider(1);
    var shortAgent = new AgentRunner(new ToolRegistry(context), store) { Api = shortRun, AutoChatTitles = false };
    await shortAgent.OpenAsync(failurePath);
    await shortAgent.RunAsync("Keep the current request verbatim.", [], "fake", default);
    Check(shortRun.Summaries == 0, "brief task with room never starts a speculative summary");
    using var provider = new CompactionProvider();
    var agent = new AgentRunner(new ToolRegistry(context), store) { Api = provider, AutoChatTitles = false };
    await agent.OpenAsync(path);
    await agent.RunAsync("Keep the current request verbatim.", [], "fake", default);
    var persisted = store.Load(path).Messages;
    int boundary = persisted.LastIndex(ChatJournal.Flags.Summary);
    Check(provider.Overlapped && provider.Summaries == 1 && boundary > 0 && persisted[240]["content"]?.ToString() == "Keep the current request verbatim." &&
        persisted[boundary]["meta"]?["summary_requirements"]?.ToString().Contains("requirement-0: retain exact wording") == true,
        "background summary overlaps the response and retains verbatim requirements");
    using var failed = new CompactionProvider(6, "length");
    var retry = new AgentRunner(new ToolRegistry(context), store) { Api = failed, AutoChatTitles = false };
    await retry.OpenAsync(failurePath);
    await retry.RunAsync("Keep the current request verbatim.", [], "fake", default);
    Check(failed.Summaries == 1 && !retry.CanContinue, "a rejected summary is not regenerated after each small step");
    using var slow = new CompactionProvider(3, holdSummary: true);
    var finishing = new AgentRunner(new ToolRegistry(context), store) { Api = slow, AutoChatTitles = false };
    await finishing.OpenAsync(failurePath);
    await finishing.RunAsync("Keep the current request verbatim.", [], "fake", default);
    var cancelled = AgentProgressManager.Last(context, failurePath)?.Operations.SingleOrDefault(step => step.Name == "compaction");
    Check(cancelled is { State: "cancelled", Error: "turn_ended", Usage: null },
        "a background summary cancelled at completion records its cause and missing usage");

    const string active = "Keep active-sentinel verbatim and finish the current work.";
    var unfinished = new ChatHistory();
    unfinished.Add(new JsonObject { ["role"] = "user", ["content"] = "Previous exact constraint" });
    unfinished.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Understood." });
    unfinished.Add(new JsonObject { ["role"] = "user", ["content"] = active });
    for (int i = 0; i < 24; i++)
        unfinished.Add(new JsonObject { ["role"] = "assistant", ["content"] = $"Decision {i}: " + new string('x', 2_100) });
    unfinished.Add(new JsonObject { ["role"] = "assistant", ["content"] = "" });
    path = store.NewPath();
    await unfinished.SaveAsync(path, "fake", null, true);
    using var forced = new CompactionProvider(0);
    var resumed = new AgentRunner(new ToolRegistry(context), store)
        { Api = forced, AutoChatTitles = false, ModelContextTokens = _ => 19_000 };
    await resumed.OpenAsync(path);
    await resumed.ContinueAsync("fake", default);
    var messages = forced.Messages!;
    Check(forced.Summaries == 1 && !forced.Overlapped &&
        messages.Count(message => StrContent(message) == active) == 1 &&
        messages.Any(message => StrContent(message)?.Contains("Previous exact constraint") == true),
        "hard overflow requires one summary with the original active message and earlier constraints");
    await resumed.OpenAsync(path);
    await resumed.RunAsync("Continue the project review.", [], "fake", default);
    Check(forced.Messages!.Count(message => StrContent(message) == active) == 1, "previously pinned request survives reopening and a new turn");

    static string? StrContent(JsonNode message) => message["content"]?.ToString();
}

static async Task ConcurrentTaskAccessAsync(string root)
{
    var access = new ChatFileAccess();
    using var primary = access.Begin();
    var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var exclusive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var extra = Task.Run(async () =>
    {
        using var secondary = access.Begin(allowConcurrentReads: true);
        reading.SetResult();
        await exclusive.Task;
        try { access.Reserve(Path.Combine(root, "parallel-task.txt")); }
        catch (ToolException) { return true; }
        return false;
    });
    await reading.Task;
    try { access.RequireExclusiveAccess(reserve: true); }
    finally { exclusive.SetResult(); }
    Check(await extra, "an extra task can read concurrently but cannot write while the primary task has exclusive access");
}

static async Task ProtectedContextAsync(string root)
{
    var work = Path.Combine(root, "protected-context");
    Directory.CreateDirectory(work);
    var context = new ToolContext { WorkingDirectory = work, AppFolder = work, LogsFolder = Path.Combine(work, "logs") };
    var registry = new ToolRegistry(context);
    var store = new ChatStore(Path.Combine(work, "chats"), context.LogsFolder);
    var history = new ChatHistory();
    const string current = "Review the project and keep this request exactly.";
    string[] outputs = [.. Enumerable.Range(0, 5).Select(i => CommandLog.Summarize("Код выхода: 1",
        string.Join("\n", Enumerable.Range(1, 34).Select(row => row == 17 ? $"error unique-{i}" : new string((char)('a' + i), 150))), context.LogsFolder))];
    for (int i = 0; i < outputs.Length; i++)
    {
        history.Add(new JsonObject
        {
            ["role"] = "assistant", ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = $"call-{i}", ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "exec_command", ["arguments"] = "{}" },
            }),
        });
        history.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = $"call-{i}", ["content"] = outputs[i] });
    }
    var path = store.NewPath();
    await history.SaveAsync(path, "fake", null, true);
    foreach (int window in new[] { 128_000, 16_000 })
    {
        using var provider = new CompactionProvider(0);
        var agent = new AgentRunner(registry, store) { Api = provider, AutoChatTitles = false, ModelContextTokens = _ => window };
        await agent.OpenAsync(path);
        await agent.RunAsync(current, [], "fake", default);
        var sent = provider.Messages!;
        Check(provider.Summaries == 0 && sent.Any(node => node["content"]?.ToString() == current), "local previews avoid summary and keep the active request");
        var results = sent.Where(node => AgentRunner.Role(node) == "tool").ToArray();
        Check(results.Length == outputs.Length && results.All(result => sent.Any(node =>
            (node["tool_calls"] as JsonArray)?.Any(call => call?["id"]?.ToString() == result["tool_call_id"]?.ToString()) == true)),
            "projected tool results remain paired");
        var older = results.First()["content"]!.ToString();
        Check(older.Contains("Код выхода: 1") && older.Contains("error unique-0") && older.Contains("read_log") &&
            CommandLog.IdOf(older) == CommandLog.IdOf(outputs[0]), "an old command keeps exit code, an error and its journal");
        Check(window == 128_000 ? results[^1]["content"]?.ToString() == outputs[^1] &&
            results.Skip(1).SkipLast(1).Any(node => node["content"]!.ToString().Contains("Output preview")) :
            results.Skip(1).Any(node => node["content"]!.ToString().Contains("Output preview")),
            "newest output stays whole while repeated large results use recoverable previews");
    }
    var log = await registry.ExecuteAsync("read_log", new JsonObject
        { ["log_id"] = CommandLog.IdOf(outputs[0]), ["offset"] = 17, ["limit"] = 1 }.ToJsonString(), null, default);
    Check(!log.IsError && log.Output.Contains("error unique-0"), "original output remains accessible by range");
    using var oversizedProvider = new CompactionProvider(0);
    var oversized = new AgentRunner(registry, store)
        { Api = oversizedProvider, IsTemporary = true, ModelContextTokens = _ => 20_000 };
    var large = "Review the project as described.\n" + new string('x', 60_000) + "\nKeep this requirement.";
    await oversized.RunAsync(large, [], "fake", default);
    var preview = oversizedProvider.Messages!.Select(node => AgentRunner.ExtractText(node["content"]))
        .OfType<string>().Single(text => text.Contains("Request preview."));
    var saved = ToolOutputStore.ReferenceOf(preview)!;
    var read = await registry.ExecuteAsync(ToolOutputStore.Name, new JsonObject
    {
        ["id"] = saved[ToolOutputStore.ReferencePrefix.Length..].Split(',')[0], ["offset"] = large.Length - 22,
    }.ToJsonString(), null, default);
    Check(preview.Contains("Keep this requirement.") && read.Output.EndsWith("Keep this requirement.") &&
        oversized.History.Any(message => AgentRunner.ExtractText(message["content"]) == large),
        "oversized active text is recoverable by range and never altered in chat history");
}

static async Task WatcherAsync(string root)
{
    var project = Path.Combine(root, "watch-project");
    var data = Path.Combine(root, "watch-data");
    var source = Path.Combine(project, "One.cs");
    Put(Path.Combine(project, "Watch.csproj"), "<Project />");
    Put(source, "class One { public void Before() {} }");
    Put(Path.Combine(project, "Stable.cs"), "class Stable { public void Untouched() {} }");
    var context = new ToolContext { AppFolder = data, WorkingDirectory = project };
    using var watcher = context.ProjectWatcher;
    watcher.Watch(context);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var first = await watcher.SnapshotAsync(project, timeout.Token);
    Check(first?.ContainsKey("One.cs") == true && first.ContainsKey("Stable.cs"), "initial background index");
    Put(source, "class One { public void AfterChanged() {} }");
    var modified = await WaitForAsync(index => index.GetValueOrDefault("One.cs")?.Syntax.Symbols.Any(s => s.Name == "AfterChanged") == true);
    Check(ReferenceEquals(first!["Stable.cs"], modified["Stable.cs"]), "watcher updates changed entries only");
    File.Move(source, Path.Combine(project, "Renamed.cs"));
    var renamed = await WaitForAsync(index => !index.ContainsKey("One.cs") && index.ContainsKey("Renamed.cs"));
    Check(ReferenceEquals(modified["Stable.cs"], renamed["Stable.cs"]), "watcher updates renames without full scan");

    async Task<Dictionary<string, IndexedSource>> WaitForAsync(Func<Dictionary<string, IndexedSource>, bool> ready)
    {
        while (true)
        {
            var snapshot = await watcher.SnapshotAsync(project, timeout.Token);
            if (snapshot != null && ready(snapshot)) return snapshot;
            await Task.Delay(75, timeout.Token);
        }
    }
}

static async Task LocalWorkflowsAsync(string root)
{
    var workspace = Path.Combine(root, "workflows");
    var data = Path.Combine(root, "workflow-data");
    Directory.CreateDirectory(workspace);
    var context = new ToolContext { AppFolder = data, WorkingDirectory = workspace, LogsFolder = Path.Combine(data, "logs") };
    var registry = new ToolRegistry(context);
    var source = Path.Combine(workspace, "test.ps1");
    Put(source, "Write-Output 'scenario-complete'");
    var registration = new JsonObject
    {
        ["action"] = "register", ["id"] = "test-script", ["source"] = source, ["title"] = "Script test",
        ["version"] = "1", ["permissions"] = "Print only", ["result"] = "Text",
    };
    var denied = await registry.ExecuteAsync(ScriptRegistry.Name, registration.ToJsonString(), null, default);
    Check(denied.IsError, "no silent script registration");
    var changed = await registry.ExecuteAsync(ScriptRegistry.Name, registration.ToJsonString(), _ =>
    {
        Put(source, "Write-Output 'changed'");
        return Task.FromResult(true);
    }, default);
    Check(changed.IsError && changed.Output.Contains("после предпросмотра"), "approval pinned to exact script");
    Put(source, "Write-Output 'scenario-complete'");
    var saved = await registry.ExecuteAsync(ScriptRegistry.Name, registration.ToJsonString(), _ => Task.FromResult(true), default);
    Check(!saved.IsError, "script registry saves approved source");
    var run = new JsonObject { ["action"] = "run", ["id"] = "test-script" }.ToJsonString();
    Check((await registry.ExecuteAsync(ScriptRegistry.Name, run, null, default)).IsError, "no silent script execution");
    if (Environment.GetEnvironmentVariable("SHARP_AGENT_TEST_NATIVE") == "1")
    {
        var executed = await registry.ExecuteAsync(ScriptRegistry.Name, run, _ => Task.FromResult(true), default);
        Check(!executed.IsError && executed.Output.Contains("scenario-complete"), "approved PowerShell script executes: " + executed.Output);
    }
    var known = new JsonObject
    {
        ["action"] = "save", ["category"] = "compile", ["signature"] = "CS1002", ["technology"] = "C#",
        ["version"] = "12", ["fix"] = "Inspect the statement terminator.", ["verification"] = "synthetic fixture", ["limits"] = "project only",
    };
    Check((await registry.ExecuteAsync(AgentKnowledge.Name, known.ToJsonString(), null, default)).IsError, "knowledge save needs approval");
    Check(!(await registry.ExecuteAsync(AgentKnowledge.Name, known.ToJsonString(), _ => Task.FromResult(true), default)).IsError &&
        AgentKnowledge.Relevant(context, "Build failed with CS1002")?.Contains("terminator") == true, "knowledge is searchable in project");
    var fix = Path.Combine(workspace, "Fixture.cs");
    Put(Path.Combine(workspace, "Fixture.csproj"), "<Project />");
    Put(fix, "class Fixture {}");
    known["path"] = fix;
    known["before"] = "class Fixture";
    known["after"] = "class Fixture {}";
    var registered = await registry.ExecuteAsync(AgentKnowledge.Name, known.ToJsonString(), _ => Task.FromResult(true), default);
    Check(!registered.IsError, "verified patch can be registered");
    var apply = new JsonObject { ["action"] = "apply", ["id"] = registered.Output.Split(' ')[^1] }.ToJsonString();
    Put(fix, "class Unrelated {}");
    Check((await registry.ExecuteAsync(AgentKnowledge.Name, apply, _ => Task.FromResult(true), default)).IsError &&
        File.ReadAllText(fix) == "class Unrelated {}", "verified patch rejects different source");
    Put(fix, "class Fixture");
    var diagnostic = new ProjectVerification.Diagnostic(fix, 1, "CS1002", "; expected");
    Check(AgentKnowledge.ApplicableId(context, [diagnostic], workspace) == registered.Output.Split(' ')[^1] &&
        AgentKnowledge.ApplicableId(context, [diagnostic with { File = source }], workspace) == null, "diagnostic selects exactly the matching patch");
    var fixedFile = await registry.ExecuteAsync(AgentKnowledge.Name, apply, _ => Task.FromResult(true), default);
    Check(!fixedFile.IsError && fixedFile.Change != null && File.ReadAllText(fix) == "class Fixture {}", "verified patch preserves rollback");
    var diagnostics = ProjectVerification.Parse("main.cs(10,5): error CS1002: ; expected\nmodule.go:12:4: undefined: value");
    Check(diagnostics.Length == 2 && diagnostics[0] is { Code: "CS1002", Line: 10 }, "structured diagnostics");
    Check(LocalTestTools.Cases("Check(int n, string? name)").Contains("-2147483648"), "boundary case generation");
    Check(AdaptiveModelRouter.IsSimple("Переведи на английский: привет") && !AdaptiveModelRouter.IsSimple("Исправь проект"), "adaptive model eligibility");
    Check(LocalLanguageModel.FromSettings("http://localhost:11434/v1", "test")?.Endpoint.Host == "127.0.0.1" &&
        LocalLanguageModel.FromSettings("https://example.com/v1", "test") == null, "local model endpoint stays on loopback");
    var store = new ChatStore(Path.Combine(data, "chats"), context.LogsFolder);
    var agent = new AgentRunner(registry, store);
    await agent.RunAsync("pwd", [], "none", default);
    Check(AgentProgressManager.Last(context, agent.ChatPath) is { State: "completed", Operations.Length: > 0 }, "local timeline persisted");
    using var memory = new LocalRecords<TaskCheckpoint>(context.DatabaseFolder, "memory", AgentKnowledge.Scope(context));
    Check(agent.ChatPath != null && memory.Get(Path.GetFileName(agent.ChatPath))?.HistoryLength == agent.History.Count, "task checkpoint persisted");
    var timeline = new AgentProgressManager();
    timeline.Begin("model-step", "response", "AI", model: "fixture", estimated: 12, images: 1);
    timeline.Retry("model-step");
    timeline.ModelResult("model-step", "completed", new UsageInfo(5, 2, 1, 1));
    timeline.Save(context, "timeline.json", AgentProvider.OpenAI, "completed");
    Check(AgentProgressManager.Last(context, "timeline.json")?.Operations.Single() is
        { Step: 1, Model: "fixture", EstimatedInput: 12, Images: 1, Retries: 1, Usage.PromptTokens: 5 }, "persisted model step usage and retry");
    var support = Path.Combine(root, "diagnostic-no-project");
    Directory.CreateDirectory(support);
    using var research = new HttpClient(new WebResearchChecks.Sources());
    var diagnosticContext = new ToolContext { WorkingDirectory = support, AppFolder = data, ResearchClient = research };
    diagnosticContext.ReadBrowser = (_, _, _, _) =>
    {
        throw new InvalidOperationException("Diagnostics opened the browser.");
    };
    var diagnosticAgent = new AgentRunner(new ToolRegistry(diagnosticContext), store) { IsTemporary = true, RussianLanguage = true };
    string? answer = null;
    diagnosticAgent.AssistantText += (text, _, _) => answer = text;
    await diagnosticAgent.RunAsync("диагностируй сборку CS1002", [], "none", default);
    Check(answer?.Contains("Fixture documentation") == true, "diagnostic route works with built-in documentation and without a provider");
}

static async Task SemanticTestsAsync(string root)
{
    if (LocalDeveloperTools.ParserVersion.Length == 0) return;
    var working = Path.Combine(root, "semantic");
    var context = new ToolContext { WorkingDirectory = working, AppFolder = Path.Combine(root, "semantic-data") };
    Put(Path.Combine(working, "Fixture.sln"), "");
    Put(Path.Combine(working, "Core", "Core.csproj"), "<Project />");
    var testsManifest = Path.Combine(working, "Tests", "Tests.csproj");
    Put(testsManifest, """<Project><ItemGroup><ProjectReference Include="..\Core\Core.csproj" /></ItemGroup></Project>""");
    Put(Path.Combine(working, "Core", "Subject.cs"), "namespace CoreLib; public static class Subject { public static int Checked() => 1; }");
    Put(Path.Combine(working, "Tests", "Fixture.cs"), """
        namespace TestLib;
        class Fixture {
            [Xunit.Fact] public void Validates() { CoreLib.Subject.Checked(); }
            [Xunit.Fact] public void Unrelated() { Checked(); }
            static void Checked() {}
        }
        """);
    var index = await ProjectSourceIndex.UpdateAsync(context, working, context.CodeIndexFolder,
        [@"Core\Subject.cs", @"Tests\Fixture.cs"], true, default);
    var subject = index[@"Core\Subject.cs"].Syntax.Symbols.Single(symbol => symbol.Name == "Checked");
    var tests = index[@"Tests\Fixture.cs"].Syntax.Symbols;
    Check(subject.Id != null && tests.Single(symbol => symbol.Name == "Validates") is { Test: true, References: { } references } &&
        references.Contains(subject.Id) && tests.Single(symbol => symbol.Name == "Unrelated").References?.Contains(subject.Id) == false,
        "cross-project Roslyn call binds only to the actual tested symbol");
    using var watcher = context.ProjectWatcher;
    var report = await LocalProjectContext.CollectAsync(context, "Проверь CoreLib.Subject.Checked() в Subject.cs", [], default);
    Check(report?.ReplaceLineEndings("\n").Contains("Связанные тесты:\nTests\\Fixture.cs:3 — Validates\n") == true,
        "verified semantic test link shown in project context");
    Put(testsManifest, "<Project />");
    var rebound = await ProjectSourceIndex.UpdateAsync(context, working, context.CodeIndexFolder,
        [@"Core\Subject.cs", @"Tests\Fixture.cs"], true, default);
    Check(rebound[@"Tests\Fixture.cs"].Syntax.Symbols.Single(symbol => symbol.Name == "Validates").References?.Contains(subject.Id!) == false,
        "removing a project reference invalidates an unchanged test file");
}

static async Task ContractAndCoverageAsync(string root)
{
    var working = Path.Combine(root, "contract");
    var context = new ToolContext { WorkingDirectory = working, AppFolder = Path.Combine(root, "contract-data") };
    Directory.CreateDirectory(working);
    File.Copy(typeof(ContractFixture).Assembly.Location, Path.Combine(working, "fixture.dll"));
    var registry = new ToolRegistry(context);
    const string generate = """
        {"action":"generate","id":"fixture","contract":{"assembly":"fixture.dll","type":"ContractFixture","method":"Increment",
        "parameters":["int"],"returns":"int","cases":[{"args":[-1],"expected":0},{"args":[2147483647],"throws":"System.OverflowException"}]}}
        """;
    const string run = """{"action":"run","id":"fixture"}""";
    var generated = await registry.ExecuteAsync(LocalTestTools.Name, generate, null, default);
    Check(!generated.IsError && generated.Output.Contains("System.OverflowException"), "executable fixture generated: " + generated.Output);
    Check((await registry.ExecuteAsync(LocalTestTools.Name, run, null, default)).IsError, "contract requires execution approval");
    if (Environment.GetEnvironmentVariable("SHARP_AGENT_TEST_NATIVE") == "1")
    {
        var result = await registry.ExecuteAsync(LocalTestTools.Name, run, _ => Task.FromResult(true), default);
        Check(!result.IsError && !result.IsWarning && result.Output.Contains("Passed 2/2"), "contract fixture is compiled and executed: " + result.Output);
    }
    var reportPath = Path.Combine(working, "TestResults", "coverage.cobertura.xml");
    Put(reportPath, """
        <coverage><packages><package><classes><class name="Example" filename="Example.cs"><methods>
        <method name="Edited"><lines><line number="4" hits="1"/><line number="5" hits="0"/></lines></method>
        <method name="Unrelated"><lines><line number="9" hits="8"/></lines></method>
        </methods></class></classes></package></packages></coverage>
        """);
    var edits = new[] { new ChangedCoverage.Edit(Path.Combine(working, "Example.cs"), new("@@ -4,2 +4,2 @@\n-old\n+new\n context", 1, 1)) };
    var coverage = ChangedCoverage.Latest(context, working, DateTime.UtcNow.AddMinutes(-1), edits);
    Check(coverage?.Contains("Example.Edited: 1/2") == true && coverage.Contains("изменённых покрыто 1/1") &&
        !coverage.Contains("Unrelated") && ChangedCoverage.Latest(context, working, DateTime.UtcNow.AddMinutes(1), edits) == null,
        "fresh Cobertura maps changed lines to their method only");
}

static void WorkspaceMentions(string root)
{
    var project = Directory.CreateDirectory(Path.Combine(root, "neosync")).FullName;
    var spaced = Directory.CreateDirectory(Path.Combine(root, "Другой проект")).FullName;
    var file = Path.Combine(project, "README.md");
    Put(file, "Project");
    var paths = new SharpAgent.Ui.PathRefs(() => root);
    Check(paths.FindWorkspace($"Просмотри проект {project} и скажи, про что он.") == project, "bare workspace path in prose");
    Check(paths.FindWorkspace($"Посмотри «{spaced}», затем проверь `{spaced}`.") == spaced, "quoted workspace paths with spaces are deduplicated");
    Check(paths.FindWorkspace($"Сравни {project} и {spaced}.") == null, "ambiguous workspace paths");
    Check(paths.FindWorkspace($"Открой {file}.") == null && paths.FindWorkspace($"Открой {project}-missing.") == null,
        "files and missing directories are not workspaces");
}

static void VoiceReplyStreaming()
{
    var connection = new VoiceEffort();
    var capabilities = VoiceEffort.CatalogModes(new JsonObject
    {
        ["supported_reasoning_levels"] = new JsonArray(new JsonObject { ["effort"] = "none" }, "high"),
    });
    string? optional = VoiceEffort.Select(capabilities),
        required = VoiceEffort.Select(["high", "low"]),
        regular = "high";
    Check(connection.Start("new-model-1", ref optional) != null && optional == "none" &&
        connection.Start("new-model-2", ref required) != null && required == "low" &&
        connection.Start("new-model-1", ref regular) == null && regular == "high",
        "voice effort is chosen from capabilities without changing regular requests");
    string? probe = VoiceEffort.Select(null);
    var attempt = connection.Start("new-model-3", ref probe)!;
    Check(probe == "none" && !attempt.Retry(new ApiException("Rate limit", System.Net.HttpStatusCode.TooManyRequests), ref probe),
        "unknown models first try no reasoning and rate limits do not change the mode");
    var rejected = new ApiException("reasoning_effort: supported values are 'medium' and 'high'.", code: "unsupported_value");
    Check(attempt.Retry(rejected, ref probe) && probe == "medium", "the rejection can advertise a minimum different from low");
    attempt.Accept(probe);
    probe = VoiceEffort.Select(null);
    Check(connection.Start("new-model-3", ref probe) != null && probe == "medium", "the accepted voice effort is reused for the same connection and model");
    probe = VoiceEffort.Select(null);
    var newAttempt = new VoiceEffort().Start("new-model-3", ref probe)!;
    Check(probe == "none", "a different connection does not inherit a learned voice effort");
    Check(newAttempt.Retry(new ApiException("Unsupported parameter", code: "unsupported_parameter") { Parameter = "reasoning.effort" }, ref probe) &&
        probe == null, "a model without the reasoning parameter retries with the provider default");
    var upstream = ApiException.FromResponse(System.Net.HttpStatusCode.OK, """{"error":{"code":"upstream_error","retryable":false}}""", null);
    Check(SharpAgent.MainWindow.CanRetryTalk(upstream) &&
        !SharpAgent.MainWindow.CanRetryTalk(new ApiException("Invalid key", System.Net.HttpStatusCode.Unauthorized)),
        "a temporary provider error inside HTTP 200 can retry without treating invalid credentials as transient");
    var talk = new SharpAgent.MainWindow.TalkSpeech();
    talk.Push("SA", false);
    talk.Push("Y\nПривет.", false);
    Check(talk.Lines.Reader.TryRead(out var first) && first == "Привет.", "voice replies stream even when the current delta ends at punctuation");
    talk.Finish("Привет. Чем помочь?");
    Check(talk.Lines.Reader.TryRead(out var last) && last == "Чем помочь?", "final voice text does not repeat played sentences");
    var task = new SharpAgent.MainWindow.TalkSpeech();
    task.Push("TASK\nПонял. Проверю.", false);
    Check(!task.Lines.Reader.TryRead(out _), "task acknowledgement waits for task routing");
    var retried = new SharpAgent.MainWindow.TalkSpeech();
    retried.Push("SAY\nПривет.", false);
    Check(retried.Lines.Reader.TryRead(out var beforeRetry) && beforeRetry == "Привет.", "the first attempt starts speaking");
    retried.Push(null, false);
    retried.Push("SAY\nПривет. Чем помочь?", false);
    retried.Finish("Привет. Чем помочь?");
    Check(retried.Lines.Reader.TryRead(out var afterRetry) && afterRetry == "Чем помочь?" && !retried.Lines.Reader.TryRead(out _),
        "a provider retry speaks only the new part of the reply");
    var fragment = new SharpAgent.MainWindow.TalkSpeech();
    const string early = "Я уже могу рассказать про устройство проекта";
    fragment.Push("SAY\n" + early, false);
    Check(fragment.Lines.Reader.TryRead(out var chunk) && chunk != null && early.StartsWith(chunk, StringComparison.Ordinal) &&
        chunk.Length < early.Length, "a long opening phrase starts speaking before sentence punctuation arrives");
    fragment.Finish(early + ".");
    Check(fragment.Lines.Reader.TryRead(out var tail) && chunk + " " + tail == early + ".", "the remaining part of a streamed phrase is spoken once");
}

static async Task VoiceScriptsAsync()
{
    Check(TalkWords.IsTaskStop("Останови задачу текущую!") && TalkWords.IsTaskStop("Пожалуйста, заверши текущую задачу") &&
        TalkWords.IsTaskStop("Заверши или останови задачу текущую") && TalkWords.IsTaskStop("Could you cancel the current task?") &&
        !TalkWords.IsTaskStop("Как остановить текущую задачу?") && !TalkWords.IsTaskStop("Заверши сборку") && !TalkWords.IsTaskStop("Секунду"),
        "voice cancellation recognizes direct task controls without intercepting questions or unrelated requests");
    var settings = new UiState { MixedVoiceInput = true };
    Check(settings.VoiceInputMode == VoiceRecognitionMode.Auto && VoicePackage.RecognizerFolder(settings.VoiceInputMode) == VoicePackage.GigaAmMultilingual,
        "the former mixed mode uses multilingual GigaAM in Auto");
    settings.VoiceInputMode = VoiceRecognitionMode.English;
    Check(!settings.MixedVoiceInput && settings.VoiceInputMode == VoiceRecognitionMode.English, "an explicit recognition language overrides Auto");
    Check(TalkWords.RussianPlaybackText("Проверю Git, API и PostgreSQL.") == "Проверю гит, эй пи ай и Постгрес.",
        "Russian speech keeps foreign names and acronyms in the same voice");
    var command = new SharpAgent.MainWindow.TalkCommand("dotnet build", CommandLog.ResultOf("Код выхода: 0\nГотово"), null, false, false);
    Check(command.Fact == "dotnet build | succeeded" && command.Reply(ReplyLanguage.Russian) == "Команда успешно выполнилась." &&
        command.Reply(ReplyLanguage.English) == "The command ran successfully.", "voice reports a successful command in plain language");
    var warning = command with { IsWarning = true };
    Check(warning.Fact == "dotnet build | warning" && warning.Reply(ReplyLanguage.Russian).Contains("с предупреждением"),
        "a zero exit code still preserves command warnings");
    Check(TalkWords.IsRecheck("смотри, проверь еще раз задачу") && TalkWords.IsRecheck("проверь эту задачу ещё раз") &&
        TalkWords.IsRecheck("please recheck the task") && !TalkWords.IsRecheck("Ну что по задаче?") &&
        !TalkWords.IsRecheck("Объясни, как перепроверить задачу"), "only direct recheck requests interrupt the current task");
    var conversation = new LanguageDetector.Conversation(ReplyLanguage.English);
    Check(conversation.Resolve("Привет!") == ReplyLanguage.Russian &&
        conversation.Resolve("OK, okay") == ReplyLanguage.Russian &&
        conversation.Resolve("Посмотри build status in Visual Studio") == ReplyLanguage.Russian &&
        conversation.Resolve("Thank you for watching") == ReplyLanguage.Russian &&
        conversation.Resolve("Да, продолжай") == ReplyLanguage.Russian,
        "isolated foreign text and mixed speech preserve the established conversation language");
    Check(conversation.Resolve("Explain how the project works", typed: true) == ReplyLanguage.English &&
        conversation.Resolve("Okay") == ReplyLanguage.Russian &&
        conversation.Resolve("Переведи на английский слово привет") == ReplyLanguage.Russian,
        "a typed request or translation does not change the language of later voice turns");
    Check(conversation.Resolve("Ответь, пожалуйста, по-английски.") == ReplyLanguage.English &&
        conversation.Resolve("Угу") == ReplyLanguage.English &&
        conversation.Resolve("Switch to Russian, please!") == ReplyLanguage.Russian,
        "an explicit language request switches the conversation immediately");
    Check(conversation.Resolve("Can you explain this?") == ReplyLanguage.Russian &&
        conversation.Resolve("I would like more details") == ReplyLanguage.English,
        "sustained switching to another language is still recognized");
    Check(conversation.Resolve("Погоди, а у Bitrix есть API?") == ReplyLanguage.Russian &&
        conversation.Resolve("А где смотреть документацию Bitrix API?") == ReplyLanguage.Russian,
        "a clear Russian request with English product names immediately restores Russian");
    Check(LanguageDetector.SpeechLanguageFor("Переведи привет на английский", ReplyLanguage.Russian) == null,
        "an explicit translation may contain text in the other language");
    var response = new SharpAgent.MainWindow.TalkSpeech(ReplyLanguage.Russian);
    int languageAttempts = 0;
    var corrected = await response.ReceiveAsync(correct =>
    {
        Check(correct == (++languageAttempts == 2), "a correction is attempted exactly once");
        var payload = correct ? "SAY\nДа, у Bitrix есть API." : "SAY\nYes. Bitrix has a REST API.";
        response.Push(payload, false);
        if (!correct) Check(!response.Lines.Reader.TryPeek(out _), "foreign streamed output is held before playback");
        return Task.FromResult(new ChatResult(new JsonObject { ["content"] = payload }, "stop", null, null));
    });
    Check(languageAttempts == 2 && corrected is { Kind: "SAY" } &&
        response.Lines.Reader.TryRead(out var russianReply) && russianReply == "Да, у Bitrix есть API." && !response.Lines.Reader.TryPeek(out _),
        "a model reply in the wrong language is retried and only the Russian reply is played");
    var handoff = new SharpAgent.MainWindow.TalkSpeech(ReplyLanguage.Russian);
    const string connect = "Подключись к VPS и передай бандл.", details = "IP 192.0.2.10\nПорт 2222\nПароль Example#123";
    const string followUp = "Используй чат для подключения с этими данными.";
    string[] earlier = ["Привет", connect, details];
    var routed = await handoff.ReceiveAsync(correct =>
    {
        Check(!correct, "chat routing needs no spoken language correction");
        const string payload = "CHAT\n3, 2, 3";
        handoff.Push(payload, false);
        return Task.FromResult(new ChatResult(new JsonObject { ["content"] = payload }, "stop", null, null));
    });
    Check(routed is { Kind: "CHAT" } route && !handoff.Lines.Reader.TryPeek(out _) &&
        SharpAgent.MainWindow.TalkTaskText(followUp, earlier, route.Text, ReplyLanguage.Russian) is { } taskText &&
        taskText.EndsWith($"{connect}\n\n{details}\n\nПоследняя реплика:\n{followUp}", StringComparison.Ordinal) && !taskText.Contains(earlier[0]),
        "chat receives the original task and exact referenced details in order without reading them aloud");
    Check(SharpAgent.MainWindow.TalkTaskText(connect, earlier, "", ReplyLanguage.Russian) == connect &&
        SharpAgent.MainWindow.TalkTaskText(followUp, earlier, "2, 4", ReplyLanguage.Russian) == null,
        "standalone tasks stay verbatim and invalid references never submit partial context");
    var namedProduct = new SharpAgent.MainWindow.TalkSpeech(ReplyLanguage.Russian);
    namedProduct.Push("SAY\nBitrix REST API. Он доступен.", false);
    Check(namedProduct.Lines.Reader.TryRead(out var withProduct) && withProduct == "Bitrix REST API. Он доступен.",
        "a technical name at the start of a Russian reply remains speakable");
    var script = new VoiceScript();
    var greeting = script.Reply("Ну, ПРИВЕТ !", ReplyLanguage.Russian);
    Check(greeting != null && script.Reply("Привет-привет!", ReplyLanguage.Russian) is { } next && next != greeting &&
        script.Reply("Здравствуйте!", ReplyLanguage.Russian) != null,
        "greetings match full phrases and never repeat consecutively");
    Check(script.Reply("Алё, алё, ты как?", ReplyLanguage.Russian) != null &&
        script.Reply("Hey, how’s it going?", ReplyLanguage.English) != null &&
        script.Reply("Can you hear me?", ReplyLanguage.English) != null &&
        script.Reply("Спасибо за помощь.", ReplyLanguage.Russian) != null,
        "local scripts match greetings, small talk, microphone checks and thanks in both languages");
    Check(script.Reply("Привет, исправь ошибку в проекте", ReplyLanguage.Russian) == null &&
        script.Reply("Hello! What's the build status?", ReplyLanguage.English) == null &&
        script.Reply("Привет. Какая погода?", ReplyLanguage.Russian) == null,
        "a greeting at the start does not intercept tasks, status requests or factual questions");
    Check(script.Reply("Э-э, не знаю…", ReplyLanguage.Russian) is { Repeat: false } &&
        script.Reply("Что ты умеешь?", ReplyLanguage.Russian) is { Repeat: false } &&
        script.Reply("Я сегодня устала.", ReplyLanguage.Russian) is { Repeat: false } &&
        script.Reply("Give me a moment to think.", ReplyLanguage.English) is { Repeat: false } &&
        script.Reply("Do you speak Russian?", ReplyLanguage.English) is { Repeat: false } &&
        script.Reply("Sorry, I misspoke.", ReplyLanguage.English) is { Repeat: false },
        "expanded conversational scripts recognize both languages");
    Check(script.Reply("Повтори, пожалуйста, последний ответ.", ReplyLanguage.Russian) is { Repeat: true } &&
        script.Reply("Could you repeat what you just said, please?", ReplyLanguage.English) is { Repeat: true } &&
        script.Reply("Повтори последнюю команду.", ReplyLanguage.Russian) == null &&
        script.Reply("Can you repeat the test with verbose output?", ReplyLanguage.English) == null,
        "repeat requests replay speech while task commands stay on the model route");
    Check(script.Reply("Ну что там по задаче?", ReplyLanguage.Russian) is { Question: VoiceQuestion.Task } &&
        script.Reply("Сколько всего этапов в плане?", ReplyLanguage.Russian) is { Question: VoiceQuestion.Plan } &&
        script.Reply("Какая сейчас команда выполнилась?", ReplyLanguage.Russian) is { Question: VoiceQuestion.LastCommand } &&
        script.Reply("Which command is running?", ReplyLanguage.English) is { Question: VoiceQuestion.RunningCommand } &&
        script.Reply("Как поменять голос?", ReplyLanguage.Russian) is { Question: VoiceQuestion.None } &&
        script.Reply("Расскажи шутку.", ReplyLanguage.Russian) is { Question: VoiceQuestion.None } &&
        script.Reply("Сколько шагов в плане и проверь Docker", ReplyLanguage.Russian) == null,
        "voice scripts answer plain status and conversation requests locally without intercepting compound tasks");
    foreach (var language in Enum.GetValues<ReplyLanguage>())
        foreach (var cue in Enum.GetValues<VoiceCue>())
        {
            var variants = Enumerable.Range(0, 12).Select(_ => script.Line(cue, language)).ToArray();
            Check(variants.Distinct().Count() == variants.Length, "voice cues use fresh variants before repeating");
        }
    var waiting = new SharpAgent.MainWindow.TalkSpeech();
    waiting.Push("SAY\n", false);
    await waiting.AnnounceWaitingAsync(TimeSpan.Zero, () => "waiting", default);
    Check(waiting.Lines.Reader.TryRead(out var filler) && filler == "waiting", "the waiting phrase is queued during silence");
    waiting.Push("Готово.", false);
    await waiting.AnnounceWaitingAsync(TimeSpan.Zero, () => "late filler", default);
    Check(waiting.Lines.Reader.TryRead(out var answer) && answer == "Готово." && !waiting.Lines.Reader.TryRead(out _),
        "a waiting phrase is suppressed once the model starts answering");
    var retry = new SharpAgent.MainWindow.TalkSpeech();
    int attempts = 0;
    await ApiException.RetryAsync((_, _, _, _) => ++attempts == 1
        ? Task.FromException<ChatResult>(new ApiException("Temporary failure", System.Net.HttpStatusCode.OK, code: "upstream_error"))
        : Task.FromResult(new ChatResult(new(), "stop", null, null)), _ => false, retry.Push, _ => { },
        (_, _) => retry.AnnounceRetry("retrying"), default, voice: true);
    await retry.AnnounceWaitingAsync(TimeSpan.Zero, () => "late filler", default);
    Check(attempts == 2 && retry.Lines.Reader.TryRead(out var notice) && notice == "retrying" && !retry.Lines.Reader.TryRead(out _),
        "a transient upstream failure starts one retry and schedules the retry notice instead of a waiting phrase");
}

static async Task ReplyLanguagesAsync(string root)
{
    Check(LanguageDetector.Detect("Исправь LoginController и обнови API") == ReplyLanguage.Russian &&
        LanguageDetector.Detect("Review `русский пример` and update the tests") == ReplyLanguage.English &&
        LanguageDetector.Detect("https://example.org/пример") == null, "language detection uses prose rather than code and links");
    var folder = Path.Combine(root, "reply-languages");
    Directory.CreateDirectory(folder);
    using var provider = new TitleProvider(Task.FromResult(new ChatResult(new JsonObject(), "stop", null, null)));
    var agent = new AgentRunner(new ToolRegistry(new ToolContext { WorkingDirectory = folder }), new ChatStore(folder, folder))
        { Api = provider, AutoChatTitles = false, RussianLanguage = true };
    await agent.RunAsync("Hello, please review the project.", [], "fake", default);
    Check(provider.ReplyPrompt?.Contains("Otherwise reply in English.") == true, "typed English overrides the Russian app language");
    await agent.RunAsync("…", [], "fake", default, useSelectedModel: true, replyLanguage: ReplyLanguage.Russian, fromVoice: true);
    Check(provider.ReplyPrompt?.Contains("Otherwise reply in Russian.") == true, "the language of a voice task is passed to the agent");
    await agent.RunAsync("!", [], "fake", default);
    Check(provider.ReplyPrompt?.Contains("Otherwise reply in Russian.") == true, "ambiguous follow-ups inherit the previous turn's language");
    Check(agent.History.Where(message => AgentRunner.Role(message) == "user").Select(AgentRunner.FromVoice).SequenceEqual([false, true, false]),
        "only voice-mode requests carry the persisted voice label");
}

static async Task NextActionsAsync(string root)
{
    const string answer = "Отчёт готов.\n<sharp-next>[\"Показать сводку\",\"Сравнить с прошлым отчётом\"]</sharp-next>";
    var folder = Directory.CreateDirectory(Path.Combine(root, "next-actions")).FullName;
    using var provider = new TitleProvider(Task.FromResult(new ChatResult(new(), "stop", null, null)), answer);
    var agent = new AgentRunner(new ToolRegistry(new ToolContext { WorkingDirectory = folder }), new ChatStore(folder, folder))
        { Api = provider, AutoChatTitles = false };
    await agent.RunAsync("Подготовь отчёт.", [], "fake", default);
    var reply = agent.History[^1];
    Check(provider.ReplyPrompt?.Contains("<sharp-next>", StringComparison.Ordinal) == true && AgentRunner.ExtractText(reply["content"]) == "Отчёт готов." &&
        AgentRunner.NextActionsOf(reply).SequenceEqual(["Показать сводку", "Сравнить с прошлым отчётом"]),
        "the work prompt requests suggestions and stores them separately from the answer");
    agent.SuggestNextActions = false;
    await agent.RunAsync("Обнови отчёт.", [], "fake", default);
    reply = agent.History[^1];
    Check(provider.ReplyPrompt?.Contains("<sharp-next>", StringComparison.Ordinal) == false && AgentRunner.ExtractText(reply["content"]) == "Отчёт готов." &&
        AgentRunner.NextActionsOf(reply).Length == 0, "disabling suggestions refreshes the prompt and keeps stray suggestions out of the answer and history");
}

static async Task ComputerInventoryAsync(string root)
{
    var work = Path.Combine(root, "pc-search-project");
    var first = Path.Combine(root, "pc-search-first");
    var second = Path.Combine(root, "pc-search-second");
    var app = Path.Combine(first, "sharp-agent-data");
    var service = Path.Combine(first, "Telematik-Service");
    var server = Path.Combine(second, "Телематик_сервер");
    foreach (var path in new[] { work, service, server, Path.Combine(app, "auth", "Телематик сервис") }) Directory.CreateDirectory(path);
    var context = new ToolContext { WorkingDirectory = work, AppFolder = app };
    var tools = new ToolRegistry(context);
    var results = ComputerInventory.SearchFolders(context, ["Telematik Service", "Телематик сервер", "Телематик сервис"], [first, second], default);
    Check(results.Contains(service) && results.Contains(server) && !results.Contains("sharp-agent-data"),
        "folder name variants match across roots and never expose the application secret directory:\n" + results);
    const string names = """{"names":["Телематик сервис"]}""";
    int requests = 0;
    bool executed = false;
    Task<bool> Decline(ConfirmRequest request)
    {
        Check(request.AlwaysAsk, "outside-project permissions cannot be turned off");
        requests++;
        return Task.FromResult(false);
    }
    Check((await tools.ExecuteAsync(ComputerInventory.FindFolders, names, Decline, default, () => executed = true)).IsError &&
        (await tools.ExecuteAsync(ComputerInventory.ListApps, "{}", Decline, default, () => executed = true)).IsError && requests == 2 && !executed,
        "declining inventory actions stops them before disks or the application list can be scanned");
    var external = new JsonObject { ["path"] = service }.ToJsonString();
    Check((await tools.ExecuteAsync("get_file_info", external, null, default)).IsError &&
        (await tools.ExecuteAsync("get_file_info", external, Decline, default)).IsError && requests == 3 &&
        !(await tools.ExecuteAsync("get_file_info", external, request => Task.FromResult(request.AlwaysAsk && request.Subject == service), default)).IsError,
        "a known external path requires a mandatory approval that shows the path");
    context.SandboxRoot = work;
    Check(tools.Definitions.All(definition => definition?["function"]?["name"]?.ToString() is not (ComputerInventory.FindFolders or ComputerInventory.ListApps)) &&
        (await tools.ExecuteAsync(ComputerInventory.FindFolders, names, Decline, default)).IsError && requests == 3,
        "sandbox never offers or starts whole-PC inventory tools");
}

static async Task VoiceDownloadResumeAsync(string root)
{
    var bytes = "voice-model"u8.ToArray();
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    var path = Path.Combine(root, "voice-download");
    var offsets = new List<long?>();
    using var http = new HttpClient(new DownloadHandler(request =>
    {
        long? offset = request.Headers.Range?.Ranges.Single().From;
        offsets.Add(offset);
        var response = new HttpResponseMessage(offset == null ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(offset == null ? bytes[..4] : bytes[(int)offset.Value..]),
        };
        if (offset != null) response.Content.Headers.ContentRange = new(offset.Value, bytes.Length - 1, bytes.Length);
        return response;
    }));
    await ResumableDownload.FetchAsync(http, "https://example.org/model", path, bytes.Length, hash, null, default);
    Check(offsets.SequenceEqual(new long?[] { null, 4 }) && File.ReadAllBytes(path).SequenceEqual(bytes),
        "voice download retries from the byte saved before an interrupted response");
    File.WriteAllBytes(path, bytes[..6]);
    using var ignoresRange = new HttpClient(new DownloadHandler(_ => new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
    bool rejected = false;
    try { await ResumableDownload.FetchAsync(ignoresRange, "https://example.org/model", path, bytes.Length, hash, null, default); }
    catch (IOException) { rejected = true; }
    Check(rejected && File.ReadAllBytes(path).SequenceEqual(bytes[..6]), "a server ignoring the range cannot overwrite the saved portion");
    await ResumableDownload.FetchAsync(http, "https://example.org/model", path, bytes.Length, hash, null, default);
    Check(offsets[^1] == 6 && File.ReadAllBytes(path).SequenceEqual(bytes), "a fresh download invocation resumes the saved file");
}

static async Task AppUpdateResumeAsync(string root)
{
    var bytes = "fixture-installer"u8.ToArray();
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    var release = new JsonObject
    {
        ["tag_name"] = "v2.0.0",
        ["assets"] = new JsonArray(new JsonObject
        {
            ["name"] = "SharpAgent-Setup.exe", ["state"] = "uploaded", ["size"] = (long)bytes.Length,
            ["digest"] = "sha256:" + hash, ["browser_download_url"] = "https://example.org/other.exe",
        }),
    };
    var installed = new Version(1, 9, 2);
    var expected = AppUpdates.FindRelease(release, installed);
    Check(expected is { DownloadUrl: "https://github.com/Flugih/sharp-agent/releases/download/v2.0.0/SharpAgent-Setup.exe" } &&
        AppUpdates.FindRelease(release, new Version(2, 0, 0)) == null, "updates require a newer release and use the fixed repository URL");
    release["prerelease"] = true;
    Check(AppUpdates.FindRelease(release, installed) == null, "prerelease installers are excluded from automatic updates");
    release["prerelease"] = false;
    var folder = Path.Combine(root, "app-update");
    var updates = new AppUpdates(folder, installed);
    using var stop = new CancellationTokenSource();
    var requests = new List<long?>();
    int releaseChecks = 0;
    using var http = new HttpClient(new DownloadHandler(request =>
    {
        if (request.RequestUri?.AbsoluteUri == AppUpdates.LatestUrl)
        {
            releaseChecks++;
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(release.ToJsonString()) };
        }
        Check(request.RequestUri?.AbsoluteUri == expected!.DownloadUrl, "download is scoped to the known installer URL");
        var offset = request.Headers.Range?.Ranges.Single().From;
        requests.Add(offset);
        if (releaseChecks == 1 && offset != null)
        {
            stop.Cancel();
            throw new OperationCanceledException(stop.Token);
        }
        var response = new HttpResponseMessage(offset == null ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(offset == null ? bytes[..4] : bytes[(int)offset.Value..]),
        };
        if (offset != null) response.Content.Headers.ContentRange = new(offset.Value, bytes.Length - 1, bytes.Length);
        return response;
    }));
    try { await updates.PrepareAsync(http, stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    Check(File.ReadAllBytes(Path.Combine(folder, "SharpAgent-Setup.exe.part")).SequenceEqual(bytes[..4]) &&
        !File.Exists(Path.Combine(folder, "SharpAgent-Setup.exe")), "interrupted application exits with a partial installer only");
    Check(await new AppUpdates(folder, installed).PrepareAsync(http, default) == expected && requests.SequenceEqual(new long?[] { null, 4, 4 }),
        "a new application instance resumes the installer at the persisted byte offset");
    using var offline = new HttpClient(new DownloadHandler(_ => throw new InvalidOperationException("A ready update must not contact the network.")));
    Check(await new AppUpdates(folder, installed).PrepareAsync(offline, default) == expected, "a deferred update is offered again even offline");
    var path = Path.Combine(folder, "SharpAgent-Setup.exe");
    File.WriteAllBytes(path, bytes.Select(b => (byte)(b ^ 1)).ToArray());
    Check(await updates.OpenVerifiedAsync(expected!, default) == null, "an altered installer with the expected size is rejected before launch");
}

static async Task UiChecksAsync(string root)
{
    var work = Path.Combine(root, "ui-checks");
    var data = Path.Combine(root, "ui-checks-data");
    Directory.CreateDirectory(work);
    var context = new ToolContext { WorkingDirectory = work, AppFolder = data };
    context.ReadBrowser = (url, selector, text, ct) =>
        Task.FromResult(new BrowserTools.Snapshot(url ?? "https://example.org", "Test", "", [], selector == "#present", true));
    var tools = new ToolRegistry(context);
    const string save = """
        {"action":"save","id":"regression-ui","mode":"browser",
         "suite":{"checks":[{"target":"https://example.org","selector":"#present"},
                            {"target":"https://example.org","selector":"#missing"}]}}
        """;
    var stored = await tools.ExecuteAsync(LocalTestTools.Name, save, null, default);
    Check(!stored.IsError, "UI suite is stored locally");
    var failed = await tools.ExecuteAsync(LocalTestTools.Name, """{"action":"run","id":"regression-ui"}""", null, default, allowBrowser: true);
    Check(failed.IsError && failed.Output.Contains("1/2") && failed.Output.Contains("#missing"),
        "failed UI assertions produce a failed tool outcome, not a false success: " + failed.Output);
    const string passing = """
        {"action":"save","id":"passing-ui","mode":"browser",
         "suite":{"checks":[{"target":"https://example.org","selector":"#present"}]}}
        """;
    Check(!(await tools.ExecuteAsync(LocalTestTools.Name, passing, null, default)).IsError, "passing UI suite stored");
    var denied = await tools.ExecuteAsync(LocalTestTools.Name, """{"action":"run","id":"passing-ui"}""", null, default);
    Check(denied.IsError, "browser test requires a request to use a page");
    var succeeded = await tools.ExecuteAsync(LocalTestTools.Name, """{"action":"run","id":"passing-ui"}""", null, default, allowBrowser: true);
    Check(!succeeded.IsError && succeeded.Output.Contains("1/1"), "passing UI assertions still succeed");
}

static async Task LocalIntelligenceChecksAsync(string root)
{
    var working = Path.Combine(root, "research-project");
    var data = Path.Combine(root, "research-data");
    var context = new ToolContext { WorkingDirectory = working, AppFolder = data };
    var path = Path.Combine(working, "notes.md");
    Put(path, "# Project\nUniqueResearchMarker: blue sky\n");
    var tools = new ToolRegistry(context);
    var indexed = await tools.ExecuteAsync(LocalResearchEngine.Name, """{"action":"index"}""", null, default);
    Check(!indexed.IsError, "document index created: " + indexed.Output);
    var found = await tools.ExecuteAsync(LocalResearchEngine.Name, """{"action":"search","query":"UniqueResearchMarker"}""", null, default);
    Check(!found.IsError && found.Output.Contains("notes.md"), "local FTS5 document search");
    var pass = await tools.ExecuteAsync(LocalContractEngine.Name, new JsonObject {
        ["checks"] = new JsonArray(new JsonObject { ["kind"] = "contains", ["path"] = path, ["expected"] = "blue sky" },
                                    new JsonObject { ["kind"] = "exists", ["path"] = path })
    }.ToJsonString(), null, default);
    Check(!pass.IsError && pass.Output.Contains("2/2"), "mandatory contract passes");
    var fail = await tools.ExecuteAsync(LocalContractEngine.Name, new JsonObject {
        ["checks"] = new JsonArray(new JsonObject { ["kind"] = "contains", ["path"] = path, ["expected"] = "not present" })
    }.ToJsonString(), null, default);
    Check(fail.IsError && fail.Output.Contains("0/1"), "mandatory contract failure is an error");
    var selection = ToolDefinitionSelection.Initial("Исправь метод класса в C# проекте. Выполни критерии приёмки.");
    Check(selection("apply_patch") && selection(LocalContractEngine.Name) && !selection(LocalTestTools.Name),
          "edit tasks with explicit acceptance criteria retain the contract tool");
    var ambiguous = ToolDefinitionSelection.Initial("Помоги разобраться");
    Check(ambiguous("local_batch") && ambiguous(WebResearch.Name) && !ambiguous(BrowserTools.Name) && !ambiguous(ComputerTools.Name),
          "ambiguous tasks start with compact local and web tools");
}

static void MemoryAndCache(string root)
{
    var working = Path.Combine(root, "memory-project");
    var context = new ToolContext { WorkingDirectory = working, AppFolder = Path.Combine(root, "memory-data") };
    var source = Path.Combine(working, "Verified.cs");
    Put(source, "class Verified {}");
    var fact = new TaskMemory.Fact(source, FileChanges.CurrentHash(source)!, DateTimeOffset.UtcNow);
    TaskMemory.Remember(context, [fact with { Verification = "dotnet build" }]);
    TaskMemory.Remember(context, [fact]);
    Check(TaskMemory.Relevant(context, "Verified.cs")?.Contains("код выхода 0: dotnet build") == true, "verified unchanged file and check remembered");
    Put(source, "class Changed {}");
    Check(TaskMemory.Relevant(context, "Verified.cs") == null, "stale evidence is discarded");
    TaskMemory.Save(context, "pending.json", new("request", 3, null, [], ["exec_command"]));
    Check(TaskMemory.Load(context, "pending.json", "request", 4)?.Pending is ["exec_command"] &&
        TaskMemory.Load(context, "pending.json", "next", 4) == null, "pending operations survive restart for the original request");
    var cache = Path.Combine(context.AppFolder!, "cache");
    var expired = Path.Combine(cache, "expired.txt");
    var excess = Path.Combine(cache, "excess.txt");
    var recent = Path.Combine(cache, "recent.txt");
    Put(expired, "expired");
    Put(excess, "excess");
    Put(recent, "fresh");
    var now = DateTime.UtcNow;
    File.SetLastWriteTimeUtc(expired, now.AddDays(-21));
    File.SetLastWriteTimeUtc(excess, now.AddHours(-1));
    AppCache.Prune(cache, 5, TimeSpan.FromDays(7), nowUtc: now);
    Check(!File.Exists(expired) && !File.Exists(excess) && File.Exists(recent) && File.Exists(source), "cache age, quota and active grace period");

    Put(Path.Combine(working, "SharpAgent.csproj"), "<Project />");
    var scratch = Path.Combine(working, ".sharpagent", "tmp");
    var abandonedRun = VerificationRun();
    var busyRun = VerificationRun();
    var recentRun = VerificationRun();
    var unmarked = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
    var unrelated = Path.Combine(working, "sharp_agent_build_check_old");
    var savedReport = Path.Combine(working, ".sharpagent", "report.json");
    Directory.CreateDirectory(unmarked);
    Directory.CreateDirectory(unrelated);
    Put(savedReport, "{}");
    Put(Path.Combine(recentRun, "recent.log"), "process output");
    var recycled = new List<string>();
    using (File.Open(Path.Combine(busyRun, AppCache.VerificationMarker), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        AppCache.CleanupVerificationArtifacts(working, recycled.Add, now);
    Check(recycled.SequenceEqual([abandonedRun]) && Directory.Exists(unmarked) && Directory.Exists(unrelated) && File.Exists(savedReport),
        "cleanup only selects old, marked and unlocked verification runs within the project");

    string VerificationRun()
    {
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(scratch, id);
        var marker = Path.Combine(path, AppCache.VerificationMarker);
        Put(marker, id);
        File.SetLastWriteTimeUtc(marker, now.AddHours(-1));
        return path;
    }
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
}

sealed class DownloadHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(respond(request));
}

sealed class InspectionProvider : IChatProvider
{
    public int Calls { get; private set; }
    public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<List<ModelInfo>>([]);
    public Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools = true, Action<int, int>? retrying = null)
    {
        if (++Calls >= 5) return Task.FromResult(new ChatResult(new JsonObject { ["content"] = "Verified." }, "stop", null, null));
        var command = Calls == 2 ? "git status --short;  git diff --stat; git diff -- App.xaml" : "git status --short; git diff --stat; git diff -- App.xaml";
        var args = Calls == 3
            ? new JsonObject { ["patch"] = "*** Begin Patch\n*** Update File: App.xaml\n@@\n-FontFamily=Before\n+FontFamily=After\n*** End Patch" }
            : new JsonObject { ["command"] = command };
        return Task.FromResult(new ChatResult(new JsonObject
        {
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = $"inspection_{Calls}", ["function"] = new JsonObject
                    { ["name"] = Calls == 3 ? PatchTool.Name : "exec_command", ["arguments"] = args.ToJsonString() },
            }),
        }, "tool_calls", null, null));
    }
    public void Dispose() { }
}

sealed class FakeProvider(long? fixedInput, int toolCalls) : IChatProvider
{
    public int Calls { get; private set; }
    public long PeakInput { get; private set; }
    public long TotalInput { get; private set; }
    public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<List<ModelInfo>>([]);
    public Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools = true, Action<int, int>? retrying = null)
    {
        Calls++;
        long input = fixedInput ?? (HistoryCompressor.EstimateAll(messages) + HistoryCompressor.Estimate(tools));
        TotalInput += input;
        PeakInput = Math.Max(PeakInput, input);
        if (fixedInput == null && !messages.Any(message => message["role"]?.ToString() == "user" &&
                message["content"]?.ToString().Contains("sentinel:") == true))
            throw new InvalidOperationException("The active request was lost during local compaction.");
        var usage = new UsageInfo(input, 10, input > 1_000 ? 1_000 : 0, 10);
        if (Calls > toolCalls) return Task.FromResult(new ChatResult(new JsonObject { ["content"] = "Verified." }, "stop", usage, null));
        return Task.FromResult(new ChatResult(new JsonObject
        {
            ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "test_" + Calls,
                ["function"] = new JsonObject
                {
                    ["name"] = fixedInput == null ? "local_batch" : "get_file_info",
                    ["arguments"] = fixedInput == null ? new JsonObject { ["operations"] = new JsonArray(new JsonObject
                        { ["name"] = "read", ["arguments"] = new JsonObject { ["path"] = $"source_{Calls}.txt" } }) } :
                        new JsonObject { ["path"] = "missing_" + Calls },
                },
            }),
        }, "tool_calls", usage, null));
    }
    public void Dispose() { }
}

sealed class TitleProvider(Task<ChatResult> title, string answer = "Ответ") : IChatProvider
{
    public int TitleCalls { get; private set; }
    public string? Subject { get; private set; }
    public string? TitlePrompt { get; private set; }
    public string? ReplyPrompt { get; private set; }
    public int? TitleTokenLimit { get; private set; }
    public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<List<ModelInfo>>([]);
    public Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools = true, Action<int, int>? retrying = null)
    {
        if (callTools)
        {
            ReplyPrompt = messages[0]["content"]?.ToString();
            return Task.FromResult(new ChatResult(new JsonObject { ["content"] = answer }, "stop", null, null));
        }
        TitleCalls++;
        Subject = messages[1]["content"]?.ToString();
        TitlePrompt = messages[0]["content"]?.ToString();
        TitleTokenLimit = maxTokens;
        return title.WaitAsync(ct);
    }
    public void Dispose() { }
}

sealed class SubagentProvider : IChatProvider
{
    public int WorkerCalls { get; private set; }
    bool _delegated;
    public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<List<ModelInfo>>([]);
    public Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools = true, Action<int, int>? retrying = null)
    {
        if (messages.Any(message => AgentRunner.Role(message) == "user" &&
            AgentRunner.ExtractText(message["content"])?.Contains("Assignment from the main agent:", StringComparison.Ordinal) == true))
            return ++WorkerCalls <= 6 ? Call($"read-{WorkerCalls}", "get_file_info", new() { ["path"] = $"fixture-{WorkerCalls}" }) :
                Task.FromResult(new ChatResult(new JsonObject { ["content"] = "All fixture paths checked." }, "stop", null, null));
        if (_delegated) return Task.FromResult(new ChatResult(new JsonObject { ["content"] = "Verified." }, "stop", null, null));
        _delegated = true;
        return Call("delegate", "delegate_tasks", new() { ["tasks"] = new JsonArray(new JsonObject
            { ["title"] = "Fixture paths", ["task"] = "Check each fixture path." }) });

        static Task<ChatResult> Call(string id, string name, JsonObject args) => Task.FromResult(new ChatResult(new JsonObject
        {
            ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = id,
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args.ToJsonString() } }),
        }, "tool_calls", null, null));
    }
    public void Dispose() { }
}

sealed class CompactionProvider(int toolCalls = 4, string summaryFinish = "stop", bool holdSummary = false) : IChatProvider
{
    readonly TaskCompletionSource _summary = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int _steps;
    public int Summaries { get; private set; }
    public IReadOnlyList<JsonNode>? Messages { get; private set; }
    public bool Overlapped { get; private set; }
    public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<List<ModelInfo>>([]);
    public async Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools = true, Action<int, int>? retrying = null)
    {
        if (!callTools)
        {
            Summaries++;
            if (holdSummary) await Task.Delay(Timeout.Infinite, ct);
            else if (toolCalls > 0) await _summary.Task.WaitAsync(ct);
            return new(new JsonObject { ["content"] = "Archived decisions and pending work." }, summaryFinish, new UsageInfo(100, 10), null);
        }
        Messages = messages;
        if (toolCalls > 0 && Summaries > 0)
        {
            Overlapped |= !_summary.Task.IsCompleted;
            _summary.TrySetResult();
        }
        await Task.Delay(40, ct);
        if (++_steps <= toolCalls)
            return new(new JsonObject
            {
                ["content"] = $"Decision at step {_steps}: " + new string('x', 1_100),
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = "inspect-" + _steps,
                    ["function"] = new JsonObject { ["name"] = "get_file_info", ["arguments"] = new JsonObject { ["path"] = "fixture-" + _steps }.ToJsonString() },
                }),
            }, "tool_calls", null, null);
        return new(new JsonObject { ["content"] = "Done." }, "stop", null, null);
    }
    public void Dispose() { }
}

sealed class ExpansionProvider : IChatProvider
{
    public List<JsonArray> Definitions { get; } = [];
    public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<List<ModelInfo>>([]);
    public Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools = true, Action<int, int>? retrying = null)
    {
        Definitions.Add(tools!);
        if (Definitions.Count >= 3) return Task.FromResult(new ChatResult(new JsonObject { ["content"] = "Done." }, "stop", null, null));
        return Task.FromResult(new ChatResult(new JsonObject
        {
            ["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "expand" + Definitions.Count,
                ["function"] = new JsonObject { ["name"] = "enable_tools", ["arguments"] = """{"tool":"get_system_info"}""" } }),
        }, "tool_calls", null, null));
    }
    public void Dispose() { }
}

public static class ContractFixture
{
    public static int Increment(int value) => checked(value + 1);
}
