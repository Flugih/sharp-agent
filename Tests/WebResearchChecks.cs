using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using SharpAgent.Agent;
using SharpAgent.Api;
using SharpAgent.Tools;

static class WebResearchChecks
{
    const string BrowserReport = @"Подготовь Word по варианту 6. Набери SQL в SQLiteOnline по адресу https\://sqliteonline.com, нарисуй схему в draw io и приложи кадры с результатами.";

    internal static async Task RunAsync(string root)
    {
        const string repository = "https://github.com/example/project";
        var request = "Просмотри репу и скажи, про что она: " + repository;
        var tools = ToolDefinitionSelection.Initial(request);
        Require(ToolDefinitionSelection.RepositoryOverview(request) && WebResearch.RepositoryIn(request) == "example/project" &&
            tools(WebResearch.Name) && tools("exec_command") && !tools(BrowserTools.Name), "repository review starts with local reads and background search");
        Require(ToolDefinitionSelection.BrowserProhibited($"Изучи {repository} без браузера") &&
            !ToolDefinitionSelection.BrowserProhibited($"Открой {repository} в браузере"),
            "explicit browser opt-outs remain recognized");
        using var handler = new Sources();
        using var client = new HttpClient(handler);
        var overview = await WebResearch.ReadAsync(client, new(new() { ["repository"] = repository }), default);
        Require(handler.Addresses.Count == 3 && handler.Addresses.All(url => url.StartsWith("https://api.github.com/repos/example/project", StringComparison.Ordinal)) &&
            overview.Contains("Example project") && overview.Contains("src/") && overview.Contains("README content"), "metadata, root files and README come from GitHub API");
        Require(WebResearch.Sources(overview).SequenceEqual([repository, repository + "/blob/main/README.md"]), "only source links displayed for overview");
        var read = await WebResearch.ReadAsync(client, new(new()
        {
            ["repository"] = "example/project", ["paths"] = new JsonArray("src/Core.cs", "missing.txt"), ["ref"] = "feature/topic",
        }), default);
        Require(read.Contains("class Core") && read.Contains("Не найдено") &&
            handler.Addresses.Contains("https://api.github.com/repos/example/project/contents/src/Core.cs?ref=feature%2Ftopic") &&
            WebResearch.Sources(read).Contains(repository + "/blob/main/src/Core.cs"), "file reads are batched and failures are isolated");
        var search = await WebResearch.ReadAsync(client, new(new() { ["query"] = "C# dictionaries" }), default);
        Require(search.Contains("Dictionary docs") && WebResearch.Sources(search).SequenceEqual(["https://example.org/docs"]), "search extracts valid links");
        await FollowUpAsync(root, client);
        await BrowserReportAsync(root);
        var sandbox = new ToolRegistry(new ToolContext { SandboxRoot = AppContext.BaseDirectory });
        Require((await sandbox.ExecuteAsync(WebResearch.Name, """{"query":"public query"}""", null, default)).IsError &&
            sandbox.Definitions.All(tool => (string?)tool?["function"]?["name"] != WebResearch.Name), "sandbox hides and rejects external search");
    }

    static async Task BrowserReportAsync(string root)
    {
        Require(!ToolDefinitionSelection.BrowserScreenshotDocument(BrowserReport),
            "a paraphrased report does not need to match the local screenshot heuristic");
        var workspace = Path.Combine(root, "browser-report");
        Directory.CreateDirectory(workspace);
        var pages = new List<string>();
        var context = new ToolContext { WorkingDirectory = workspace, ComputerUse = true, ReadBrowser = (url, _, _, _) =>
        {
            pages.Add(url!);
            return Task.FromResult(new BrowserTools.Snapshot(url!, "Page", "Visible contents", [], true, true));
        } };
        using var provider = new FollowUpProvider(browserReport: true);
        var agent = new AgentRunner(new ToolRegistry(context), new ChatStore(Path.Combine(workspace, "chats"), Path.Combine(workspace, "logs")))
        {
            Api = provider, IsTemporary = true,
        };
        string? final = null;
        agent.AssistantText += (text, elapsed, _) => { if (elapsed != null) final = text; };
        await agent.RunAsync(BrowserReport, [], "fixture", default);
        Require(provider.Definitions[0].Contains("enable_tools") && !provider.Definitions[0].Contains(BrowserTools.Name) &&
            !provider.Definitions[0].Contains(ComputerTools.Name) && provider.Definitions[1].Contains(BrowserTools.Name) &&
            provider.Definitions[1].Contains(ComputerTools.Name), "the model can enable the browser and screenshot tools on demand");
        Require(provider.Definitions[1].Take(provider.Definitions[0].Length).SequenceEqual(provider.Definitions[0]) &&
            provider.Definitions.Count == 4 && pages.SequenceEqual(["https://sqliteonline.com/", "https://app.diagrams.net/"]),
            $"expansion preserves tool order and the unfinished report is reviewed; requests: {provider.Definitions.Count}, pages: {string.Join(", ", pages)}");
        Require(final?.Contains("нет сохранённых скриншотов", StringComparison.Ordinal) == true ||
            final?.Contains("no saved screenshots", StringComparison.Ordinal) == true,
            $"a Word report without the requested captures stays incomplete; answer: {final}");
    }

    static async Task FollowUpAsync(string root, HttpClient client)
    {
        var workspace = Path.Combine(root, "web-followup");
        Directory.CreateDirectory(workspace);
        int pages = 0, links = 0;
        var context = new ToolContext { WorkingDirectory = workspace, ResearchClient = client, OpenUrl = _ => links++ };
        context.ReadBrowser = (url, _, _, _) =>
        {
            pages++;
            return Task.FromResult(new BrowserTools.Snapshot(url!, "Page", "Visible contents", [], true, true));
        };
        using var provider = new FollowUpProvider();
        var agent = new AgentRunner(new ToolRegistry(context), new ChatStore(Path.Combine(workspace, "chats"), Path.Combine(workspace, "logs")))
        {
            Api = provider, IsTemporary = true,
        };
        List<ToolResultEvent> results = [];
        agent.ToolCallFinished += results.Add;
        await agent.RunAsync("просмотри в github openai/codex и скажи что это", [], "fixture", default);
        Require(results.Single() is { Name: WebResearch.Name, IsError: false } overview &&
            overview.Output.Contains("README content"), "a bare GitHub slug is read before the first model call");
        await agent.RunAsync("подробней как он работает", [], "fixture", default);
        Require(provider.Definitions.Take(3).All(names => !names.Contains(BrowserTools.Name) && names.Contains(WebResearch.Name)) &&
            results.Any(result => result.Name == BrowserTools.Name && result.IsError) &&
            results.Any(result => result.Name == "open_path" && result.IsError) &&
            results.Count(result => result.Name == WebResearch.Name && !result.IsError) == 2 && pages == 0 && links == 0,
            "follow-up uses background research even if the model tries a browser tool or a URL open");
        await agent.RunAsync("В справке https://example.org/docs переключи режим отображения и скажи, что изменилось.", [], "fixture", default);
        Require(!provider.Definitions[3].Contains(BrowserTools.Name) && provider.Definitions[4].Contains(BrowserTools.Name) && pages == 1 && links == 0,
            "a paraphrased interactive request enables the browser in the same conversation");
        await agent.RunAsync("Продолжи разбор, но без браузера.", [], "fixture", default);
        Require(!provider.Definitions[^1].Contains(BrowserTools.Name) && pages == 1 && links == 0 &&
            results.Any(result => result.Id == "denied-enable" && result.IsError),
            "browser access does not carry across turns and the user's opt-out blocks another activation");
    }

    static void Require(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
    }

    internal sealed class Sources : HttpMessageHandler
    {
        internal ConcurrentBag<string> Addresses { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Addresses.Add(url.AbsoluteUri);
            var path = url.AbsolutePath.Replace("/repos/openai/codex", "/repos/example/project", StringComparison.Ordinal);
            var content = path switch
            {
                "/repos/example/project" => """{"full_name":"example/project","description":"Example project","language":"C#","default_branch":"main","html_url":"https://github.com/example/project"}""",
                "/repos/example/project/readme" => File("README content", "README.md"),
                "/repos/example/project/contents" => """[{"name":"src","type":"dir"},{"name":"README.md","type":"file"}]""",
                "/repos/example/project/contents/src/Core.cs" => File("class Core {}", "src/Core.cs"),
                "/search" when Uri.UnescapeDataString(url.Query) == "?format=rss&q=site:learn.microsoft.com CS1002" => """
                    <rss><channel><item><title>Fixture documentation</title><link>https://learn.microsoft.com/example</link></item></channel></rss>
                    """,
                "/search" => """
                    <rss><channel>
                      <item><title>Dictionary docs</title><link>https://example.org/docs</link><description>Example snippet</description></item>
                      <item><link>javascript:alert(1)</link></item>
                    </channel></rss>
                    """,
                _ => null,
            };
            return Task.FromResult(content == null ? new HttpResponseMessage(HttpStatusCode.NotFound) :
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
        }

        static string File(string text, string path) => new JsonObject
        {
            ["type"] = "file", ["encoding"] = "base64", ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
            ["html_url"] = "https://github.com/example/project/blob/main/" + path,
        }.ToJsonString();
    }

    sealed class FollowUpProvider(bool browserReport = false) : IChatProvider
    {
        internal List<string[]> Definitions { get; } = [];
        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<List<ModelInfo>>([]);

        public Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens,
            string? effort, Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct,
            bool callTools = true, Action<int, int>? retrying = null)
        {
            Definitions.Add(tools?.Select(tool => tool?["function"]?["name"]?.ToString() ?? "").ToArray() ?? []);
            var calls = (browserReport, Definitions.Count) switch
            {
                (true, 1) => new JsonArray(Call("capture", "enable_tools", """{"group":"browser_capture"}""")),
                (true, 2) => new JsonArray(
                    Call("sqlite", BrowserTools.Name, """{"action":"inspect","url":"https://sqliteonline.com/"}"""),
                    Call("diagrams", BrowserTools.Name, """{"action":"inspect","url":"https://app.diagrams.net/"}""")),
                (false, 2) => new JsonArray(
                    Call("all", "enable_tools", """{"group":"all"}"""),
                    Call("denied-browser", BrowserTools.Name, """{"action":"inspect","url":"https://example.org/docs"}"""),
                    Call("denied-link", "open_path", """{"path":"https://example.org/docs"}"""),
                    Call("docs", WebResearch.Name, """{"query":"Codex CLI reference"}""")),
                (false, 4) => new JsonArray(Call("enable-browser", "enable_tools", """{"group":"browser"}""")),
                (false, 5) => new JsonArray(Call("allowed-browser", BrowserTools.Name, """{"action":"inspect","url":"https://example.org/docs"}""")),
                (false, 7) => new JsonArray(
                    Call("denied-enable", "enable_tools", """{"tool":"browser_page"}"""),
                    Call("denied-again", BrowserTools.Name, """{"action":"inspect","url":"https://example.org/docs"}""")),
                _ => null,
            };
            return Task.FromResult(calls == null
                ? new ChatResult(new JsonObject { ["content"] = "Готово." }, "stop", null, null)
                : new ChatResult(new JsonObject { ["tool_calls"] = calls }, "tool_calls", null, null));
        }

        static JsonObject Call(string id, string name, string arguments) =>
            new() { ["id"] = id, ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments } };

        public void Dispose() { }
    }
}
