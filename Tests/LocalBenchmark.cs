using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using SharpAgent.Agent;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Tools;

static class LocalBenchmark
{
    internal sealed record Row(string Scenario, int Requests, long EstimatedInput, long EstimatedOutput, long RepeatedContext,
        long? Input, long? Output, long? Cached, long? Reasoning, int LocalActions, long Milliseconds, bool Passed, string? Error);
    sealed record Step(string Name, JsonObject Arguments);
    sealed record Case(string Name, string Request, Step[] Steps, Func<string, AgentRunner, bool> Verify, bool Work = true, bool Resume = false);

    internal static async Task RunAsync(string root, string output)
    {
        var result = new List<Row>();
        var live = Environment.GetEnvironmentVariable("SHARP_AGENT_BENCH_LIVE") == "1";
        var model = live ? Setting("MODEL") : "deterministic-replay";
        foreach (var scenario in Cases())
        {
            var workspace = Path.Combine(root, scenario.Name);
            Directory.CreateDirectory(workspace);
            Prepare(workspace, scenario.Name);
            var context = new ToolContext
            {
                WorkingDirectory = workspace, AppFolder = Path.Combine(root, scenario.Name + "-data"),
                LogsFolder = Path.Combine(root, scenario.Name + "-logs"),
            };
            using var watcher = context.ProjectWatcher;
            watcher.Watch(context);
            var store = new ChatStore(Path.Combine(root, scenario.Name + "-chat"), context.LogsFolder);
            using var provider = new MeasuredProvider(scenario.Steps, live ? new ChatApiClient(Setting("URL"), Setting("KEY"), AgentProvider.OpenAI) : null,
                scenario.Resume);
            AgentRunner agent = Make();
            AgentRunner Make() => new(new ToolRegistry(context), store)
            {
                Api = provider, Provider = AgentProvider.OpenAI, RussianLanguage = false, ConfirmAsync = _ => Task.FromResult(true),
            };
            int actions = 0;
            agent.ToolCallStarted += _ => actions++;
            await agent.SetWorkModeAsync(scenario.Work);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var timer = Stopwatch.StartNew();
            string? error = null;
            bool passed = false;
            try
            {
                string request = scenario.Request + (live && scenario.Name is "05-compile" or "06-tests" ?
                    " Everything is synthetic. Run only the requested .NET fixture commands and edit files in this folder." : "");
                try { await agent.RunAsync(request, [], model, timeout.Token); }
                catch (OperationCanceledException) when (provider.Interrupted && !timeout.IsCancellationRequested)
                {
                    var path = agent.ChatPath ?? throw new InvalidOperationException("Interrupted chat was not saved.");
                    agent = Make();
                    agent.ToolCallStarted += _ => actions++;
                    await agent.OpenAsync(path);
                    if (!agent.CanContinue) throw new InvalidOperationException("Interrupted chat cannot be resumed.");
                    await agent.ContinueAsync(model, timeout.Token);
                }
                passed = scenario.Verify(workspace, agent);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException or ApiException)
            { error = ex.GetType().Name; }
            result.Add(new(scenario.Name, provider.Calls, provider.EstimateInput, provider.EstimateOutput, provider.Repeated,
                provider.Usage?.PromptTokens, provider.Usage?.CompletionTokens, provider.Usage?.CachedTokens, provider.Usage?.ReasoningTokens,
                actions, timer.ElapsedMilliseconds, passed, error));
            Console.WriteLine($"{scenario.Name}: {provider.Calls} model calls, {provider.EstimateInput} estimated input, pass={passed}, error={error ?? "-"}");
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { Mode = live ? "live" : "replay", Model = model, Results = result },
            new JsonSerializerOptions { WriteIndented = true }));
        if (result.Any(row => !row.Passed)) throw new InvalidOperationException("One or more benchmark scenarios failed. See the report.");
    }

    static string Setting(string suffix) => Environment.GetEnvironmentVariable("SHARP_AGENT_BENCH_" + suffix) is { Length: > 0 } value ? value :
        throw new InvalidOperationException("A required live benchmark environment setting is absent.");

    static Case[] Cases() =>
    [
        new("01-file", "read file \"one.txt\"", [Read("one.txt")], (_, agent) => Saw(agent, "FileBenchmark")),
        new("02-project", "Search the project for SearchBenchmark without editing.", [new("search_text", new() { ["query"] = "SearchBenchmark", ["pattern"] = "SearchBenchmark" })],
            (_, agent) => Saw(agent, "Needle032.cs")),
        new("03-method", "Edit Value method in Code.cs from 1 to 2. No tests.", [Read("Code.cs"), Edit("Code.cs", "=> 1;", "=> 2;")],
            (path, _) => File.ReadAllText(Path.Combine(path, "Code.cs")).Contains("=> 2;")),
        new("04-related", "Update the methods in Code.cs and Caller.cs from 1 to 2. No tests.",
            [Read("Code.cs"), Read("Caller.cs"), Edit("Code.cs", "=> 1;", "=> 2;"), Edit("Caller.cs", "=> 1;", "=> 2;")],
            (path, _) => new[] { "Code.cs", "Caller.cs" }.All(name => File.ReadAllText(Path.Combine(path, name)).Contains("=> 2;"))),
        new("05-compile", "Run dotnet build, fix the missing terminator in Program.cs and run the build again.",
            [Command("dotnet build --nologo"), Edit("Program.cs", "return 0\n", "return 0;\n"), Command("dotnet build --nologo")],
            (_, agent) => ExitCodes(agent) is [false, true], Work: false),
        new("06-tests", "Run dotnet run on the fixture tests, fix the expected value in Program.cs and run the tests again.",
            [Command("dotnet run --project Check.csproj"), Edit("Program.cs", "return 1;", "return 0;"), Command("dotnet run --project Check.csproj")],
            (_, agent) => ExitCodes(agent) is [false, true], Work: false),
        new("07-pdf", "read document \"sample.pdf\"", [new("local_research", new() { ["action"] = "inspect", ["path"] = "sample.pdf" })],
            (_, agent) => Saw(agent, "PdfBenchmark")),
        new("08-documents", "search documents for \"DocBenchmark\"", [new("local_research", new() { ["action"] = "index" }),
            new("local_research", new() { ["action"] = "search", ["query"] = "DocBenchmark" })],
            (_, agent) => Saw(agent, "guide.md") && Saw(agent, "records.csv")),
        new("09-long", "Review the methods across the project and inspect each file listed from Fixture01.cs to Fixture12.cs.",
            Enumerable.Range(1, 12).Select(i => Read($"Fixture{i:D2}.cs", 30)).ToArray(), (_, agent) => Saw(agent, "Fixture12")),
        new("10-resume", "Edit Code.cs Value from 1 to 2 and Caller.cs from 1 to 3. No tests.",
            [Edit("Code.cs", "=> 1;", "=> 2;"), Edit("Caller.cs", "=> 1;", "=> 3;")],
            (path, _) => File.ReadAllText(Path.Combine(path, "Code.cs")).Contains("=> 2;") &&
                         File.ReadAllText(Path.Combine(path, "Caller.cs")).Contains("=> 3;"), Resume: true),
    ];

    static Step Read(string path, int count = 20) => new("read_file", new() { ["path"] = path, ["limit"] = count });
    static Step Edit(string path, string from, string to) => new("edit_file", new() { ["path"] = path, ["old_text"] = from, ["new_text"] = to });
    static Step Command(string command) => new("run_command", new() { ["command"] = command, ["timeout_seconds"] = 90 });
    static bool Saw(AgentRunner agent, string text) => agent.History.Any(item =>
        (item["role"]?.ToString() is "tool" or "assistant") && item["content"]?.ToString().Contains(text, StringComparison.OrdinalIgnoreCase) == true);
    static bool[] ExitCodes(AgentRunner agent) => agent.History.Where(item =>
        item["role"]?.ToString() == "tool" && CommandLog.ResultOf(item["content"]?.ToString() ?? "") != null)
        .Select(item => CommandLog.ResultOf(item["content"]!.ToString()) == "код 0").ToArray();

    static void Prepare(string path, string scenario)
    {
        void Put(string name, string content) => File.WriteAllText(Path.Combine(path, name), content);
        Put("one.txt", "FileBenchmark");
        Put("Code.cs", "class Code { public int Value() => 1; }");
        Put("Caller.cs", "class Caller { public int Value() => 1; }");
        Put("guide.md", "# Guide\nDocBenchmark appears in related guides.");
        Put("records.csv", "marker,description\nDocBenchmark,fixture provenance\n");
        if (scenario is "05-compile" or "06-tests")
        {
            File.Delete(Path.Combine(path, "Code.cs"));
            File.Delete(Path.Combine(path, "Caller.cs"));
            Put("Check.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup></Project>""");
            Put("Program.cs", scenario == "05-compile" ? "return 0\n" : "System.Console.WriteLine(\"Fixture assertion\");\nreturn 1;\n");
        }
        if (scenario == "02-project")
            for (int i = 1; i <= 320; i++) Put($"Needle{i:D3}.cs", $"class Needle{i:D3} {{ string Key = \"{(i == 32 ? "SearchBenchmark" : $"Other{i:D3}")}\"; }}");
        if (scenario == "07-pdf")
            Put("sample.pdf", """
                %PDF-1.4
                1 0 obj
                << /Type /Catalog /Pages 2 0 R >>
                endobj
                2 0 obj
                << /Type /Pages /Kids [3 0 R] /Count 1 >>
                endobj
                3 0 obj
                << /Type /Page /Parent 2 0 R /Contents 4 0 R >>
                endobj
                4 0 obj
                << >>
                stream
                BT (PdfBenchmark) Tj ET
                endstream
                endobj
                %%EOF
                """);
        if (scenario == "09-long")
            for (int i = 1; i <= 12; i++) Put($"Fixture{i:D2}.cs", "class Fixture" + i.ToString("D2") + " {\n" +
                string.Concat(Enumerable.Range(1, 30).Select(n => $"// row {n} " + new string((char)('a' + i), 160) + "\n")) + "}");
    }

    sealed class MeasuredProvider(Step[] steps, IChatProvider? live, bool interrupt) : IChatProvider
    {
        readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        int _step;
        bool _hadLiveStep;
        public bool Interrupted { get; private set; }
        public int Calls { get; private set; }
        public long EstimateInput { get; private set; }
        public long EstimateOutput { get; private set; }
        public long Repeated { get; private set; }
        public UsageInfo? Usage { get; private set; }

        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => live?.ListModelsAsync(ct) ?? Task.FromResult<List<ModelInfo>>([]);
        public async Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens, string? effort,
            Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct, bool callTools = true, Action<int, int>? retrying = null)
        {
            if (interrupt && (_step == 1 || _hadLiveStep) && !Interrupted && callTools)
            {
                Interrupted = true;
                throw new OperationCanceledException();
            }
            Calls++;
            EstimateInput += HistoryCompressor.EstimateAll(messages) + HistoryCompressor.Estimate(tools);
            foreach (var item in messages.Where(msg => msg["role"]?.ToString() == "tool"))
            {
                var text = item["content"]?.ToString() ?? "";
                if (!_seen.Add(text)) Repeated += HistoryCompressor.Estimate(item);
            }
            ChatResult result;
            if (live != null)
            {
                result = await live.CompleteAsync(model, messages, tools, maxTokens, effort, reportText, reportProgress, ct, callTools, retrying);
                _hadLiveStep |= result.Message["tool_calls"] is JsonArray { Count: > 0 };
            }
            else if (callTools && _step < steps.Length)
            {
                var step = steps[_step];
                _step++;
                result = new(new JsonObject
                {
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "bench_" + _step, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = step.Name, ["arguments"] = step.Arguments.ToJsonString() },
                    }),
                }, "tool_calls", null, null);
            }
            else result = new(new JsonObject { ["content"] = callTools ? "Completed the requested checks." : "Fixture task" }, "stop", null, null);
            EstimateOutput += HistoryCompressor.Estimate(result.Message);
            if (result.Usage is { } usage) Usage = Usage?.Add(usage) ?? usage;
            return result;
        }
        public void Dispose() => live?.Dispose();
    }
}
