global using System.IO;
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
await ReadsAndIndexAsync(root);
await AgentBudgetAsync(root);
await ChatTitlesAsync(root);
RequestComponents();
await ToolExpansionAsync(root);
await SavedOutputsAsync(root);
await CodexWorkflowAsync(root);
await BackgroundCompactionAsync(root);
await ContextBenchmarkAsync(root);
await LocalWorkflowsAsync(root);
await WatcherAsync(root);
await SemanticTestsAsync(root);
await ContractAndCoverageAsync(root);
await UiChecksAsync(root);
await LocalIntelligenceChecksAsync(root);
await FinalLocalChecks.RunAsync(root);
MemoryAndCache(root);
if (Environment.GetEnvironmentVariable("SHARP_AGENT_TEST_NATIVE") == "1") await NativeChecks.RunAsync(root);
Console.WriteLine("Sharp Agent: migration, DPAPI, file reads, project index, local routing and model budget passed.");

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
    using var provider = new FakeProvider(100_000);
    agent.Api = provider;
    string? stop = null;
    agent.Stopped += text => stop = text;
    await agent.RunAsync("Исследуй файлы", [], "fake", default);
    Check(provider.Calls == 3 && stop?.Contains("Продолжить") == true && agent.CanContinue, "token budget pauses and can resume");
    var totals = agent.Totals();
    Check(totals.PromptTokens == 300_000 && totals.CachedTokens == 3_000 && totals.ReasoningTokens == 30 && totals.Requests == 3, "telemetry retains cache and reasoning");
    await agent.ContinueAsync("fake", default);
    Check(provider.Calls == 6, "resume starts a new budget");
    using var cheap = new FakeProvider(30);
    agent.Api = cheap;
    await agent.RunAsync("Исследуй файлы", [], "fake", default);
    Check(cheap.Calls == 24 && agent.CanContinue, "step limit works independently of token limit");
}

static async Task ContextBenchmarkAsync(string root)
{
    for (int i = 1; i <= 24; i++)
        Put(Path.Combine(root, $"source_{i}.txt"), string.Join("\n", Enumerable.Repeat("Проверяемый фрагмент исходного кода " + new string('x', 360), 70)));
    var context = new ToolContext { WorkingDirectory = root, AppFolder = Path.Combine(root, "private", "data") };
    using var provider = new FakeProvider(null);
    var agent = new AgentRunner(new ToolRegistry(context), new ChatStore(Path.Combine(root, "chats"), Path.Combine(root, "logs")))
        { Api = provider, IsTemporary = true };
    var timer = System.Diagnostics.Stopwatch.StartNew();
    await agent.RunAsync("sentinel: сохрани исходное требование", [], "fake", default);
    Check(provider.Calls > 5 && provider.PeakInput < 27_000 && agent.CanContinue, "large tool results fit the bounded context");
    Console.WriteLine($"Context replay: {provider.Calls} requests, {provider.TotalInput:N0} estimated input, {provider.PeakInput:N0} peak, {timer.ElapsedMilliseconds} ms.");
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
    await agent.RenameAsync("Моё название");
    pending.SetResult(result);
    await run;
    Check(delayed.TitleCalls == 1 && agent.Title == "Моё название" && store.Load(agent.ChatPath!).Title == agent.Title && changes == 1,
        "a pending automatic title allows the answer and preserves a manual rename");
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
    var pending = await registry.ExecuteAsync("exec_command", """{"command":"Start-Sleep -Milliseconds 800; Write-Output finished","yield_ms":100}""",
        _ => Task.FromResult(true), default);
    var id = pending.Output.Split("session_id=")[1].Split('.')[0];
    var finished = await registry.ExecuteAsync("exec_command", new JsonObject { ["session_id"] = id, ["yield_ms"] = 5000 }.ToJsonString(),
        _ => throw new InvalidOperationException("Polling must not ask for command approval"), default);
    Check(CommandLog.ResultOf(finished.Output) == "код 0" && finished.Output.Contains("finished"), "execution session can be polled");
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
        next.StablePrefix == first.Total && next.RepeatedResults == first.Results && next.History > first.History,
        "component estimates reconcile and matching prefixes exclude new messages");
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
    for (int i = 0; i < 21; i++)
    {
        history.Add(new JsonObject { ["role"] = "user", ["content"] = $"requirement-{i}: retain exact wording" });
        history.Add(new JsonObject { ["role"] = "assistant", ["content"] = $"Decision {i}: " + new string((char)('a' + i % 20), 2100) });
    }
    var path = store.NewPath();
    await history.SaveAsync(path, "fake", null, true);
    using var provider = new CompactionProvider();
    var agent = new AgentRunner(new ToolRegistry(context), store) { Api = provider, AutoChatTitles = false };
    await agent.OpenAsync(path);
    await agent.RunAsync("Keep the current request verbatim.", [], "fake", default);
    var persisted = store.Load(path).Messages;
    int boundary = persisted.LastIndex(ChatJournal.Flags.Summary);
    Check(provider.Overlapped && boundary > 0 && persisted[42]["content"]?.ToString() == "Keep the current request verbatim." &&
        persisted[boundary]["meta"]?["summary_requirements"]?.ToString().Contains("requirement-0: retain exact wording") == true,
        "background summary overlaps the response and retains verbatim requirements");
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
    var diagnosticContext = new ToolContext { WorkingDirectory = support, AppFolder = data };
    diagnosticContext.ReadBrowser = (url, _, term, _) =>
    {
        Check(url == "https://learn.microsoft.com/search/?terms=CS1002" && term == "CS1002", "diagnostic sends only the public code to documentation search");
        return Task.FromResult(new BrowserTools.Snapshot(url!, "Docs", "Fixture documentation", [], true, true));
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
    var failed = await tools.ExecuteAsync(LocalTestTools.Name, """{"action":"run","id":"regression-ui"}""", null, default);
    Check(failed.IsError && failed.Output.Contains("1/2") && failed.Output.Contains("#missing"),
        "failed UI assertions produce a failed tool outcome, not a false success: " + failed.Output);
    const string passing = """
        {"action":"save","id":"passing-ui","mode":"browser",
         "suite":{"checks":[{"target":"https://example.org","selector":"#present"}]}}
        """;
    Check(!(await tools.ExecuteAsync(LocalTestTools.Name, passing, null, default)).IsError, "passing UI suite stored");
    var succeeded = await tools.ExecuteAsync(LocalTestTools.Name, """{"action":"run","id":"passing-ui"}""", null, default);
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
    Check(selection != null && selection("apply_patch") && selection(LocalContractEngine.Name) && !selection(LocalTestTools.Name),
          "edit tasks with explicit acceptance criteria retain the contract tool");
    Check(ToolDefinitionSelection.Initial("Помоги разобраться") == null,
          "ambiguous tasks retain the full tool set");
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
}

static void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
}

sealed class FakeProvider(long? fixedInput) : IChatProvider
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
        }, "tool_calls", new UsageInfo(input, 10, input > 1_000 ? 1_000 : 0, 10), null));
    }
    public void Dispose() { }
}

sealed class TitleProvider(Task<ChatResult> title) : IChatProvider
{
    public int TitleCalls { get; private set; }
    public string? Subject { get; private set; }
    public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<List<ModelInfo>>([]);
    public Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools = true, Action<int, int>? retrying = null)
    {
        if (callTools) return Task.FromResult(new ChatResult(new JsonObject { ["content"] = "Ответ" }, "stop", null, null));
        TitleCalls++;
        Subject = messages[1]["content"]?.ToString();
        return title.WaitAsync(ct);
    }
    public void Dispose() { }
}

sealed class CompactionProvider : IChatProvider
{
    readonly TaskCompletionSource _summary = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool _started;
    public bool Overlapped { get; private set; }
    public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<List<ModelInfo>>([]);
    public async Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
        Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools = true, Action<int, int>? retrying = null)
    {
        if (!callTools)
        {
            _started = true;
            await _summary.Task.WaitAsync(ct);
            return new(new JsonObject { ["content"] = "Archived decisions and pending work." }, "stop", new UsageInfo(100, 10), null);
        }
        Overlapped |= _started && !_summary.Task.IsCompleted;
        _summary.TrySetResult();
        await Task.Delay(40, ct);
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
