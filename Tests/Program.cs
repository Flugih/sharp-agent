global using System.IO;
using System.Text.Json.Nodes;
using SharpAgent.Agent;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Tools;

System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
var root = Path.Combine(AppContext.BaseDirectory, "checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Migration(root);
await ReadsAndIndexAsync(root);
await AgentBudgetAsync(root);
await ContextBenchmarkAsync(root);
await LocalWorkflowsAsync(root);
await WatcherAsync(root);
await SemanticTestsAsync(root);
await ContractAndCoverageAsync(root);
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
    var first = await tools.ExecuteAsync("read_many", args, null, default);
    Check(first.Output.Contains("=> 1;") && !first.Output.Contains("PRIVATE") && first.Output.Contains("Ошибка"), "batch execution and secret guard");
    Check(first.Output.Split("=> 1;").Length == 2, "overlapping line ranges are returned once");
    bool roslynInstalled = LocalDeveloperTools.ParserVersion.Length > 0;
    var index = await ProjectSourceIndex.UpdateAsync(context, working, context.CodeIndexFolder, ["Проект.cs"], true, default);
    Check(index["Проект.cs"].Syntax.Symbols.Any(symbol => symbol.Name == "ExampleMethod"), "indexed C# symbol");
    Check(!roslynInstalled || index["Проект.cs"].Roslyn, "Roslyn index when SDK is usable");
    Check(File.Exists(Path.Combine(context.DatabaseFolder, "projects.db")), "system SQLite database");
    var secondIndex = await ProjectSourceIndex.UpdateAsync(context, working, context.CodeIndexFolder, ["Проект.cs"], true, default);
    Check(index["Проект.cs"].Hash == secondIndex["Проект.cs"].Hash, "read persisted index");
    Put(path, "class Example\n{\n    public int ExampleMethod() => 22;\n}");
    var second = await tools.ExecuteAsync("read_many", args, null, default);
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

static void Put(string path, string value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, value);
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
    TaskMemory.Save(context, "pending.json", new("request", 3, null, [], ["run_command"]));
    Check(TaskMemory.Load(context, "pending.json", "request", 4)?.Pending is ["run_command"] &&
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
                    ["name"] = fixedInput == null ? "read_file" : "get_file_info",
                    ["arguments"] = new JsonObject { ["path"] = fixedInput == null ? $"source_{Calls}.txt" : "missing_" + Calls },
                },
            }),
        }, "tool_calls", new UsageInfo(input, 10, input > 1_000 ? 1_000 : 0, 10), null));
    }
    public void Dispose() { }
}

public static class ContractFixture
{
    public static int Increment(int value) => checked(value + 1);
}
