using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using SharpAgent.Agent;
using SharpAgent.Api;
using SharpAgent.Core;
using SharpAgent.Tools;
using SharpAgent.Ui;

static class FinalLocalChecks
{
    internal static async Task RunAsync(string root)
    {
        var project = Path.Combine(root, "final-project");
        Directory.CreateDirectory(project);
        var context = new ToolContext { WorkingDirectory = project, AppFolder = Path.Combine(root, "final-data") };
        using var watcher = context.ProjectWatcher;
        var source = Path.Combine(project, "Sample.cs");
        File.WriteAllText(source, "class Sample { int Before() => 1; }");
        var word = Path.Combine(project, "note.docx");
        Package(word, new Dictionary<string, string>
        {
            ["word/document.xml"] = """
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <w:body><w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr>
                <w:hyperlink r:id="rId1"><w:r><w:t>WordIndexUnique</w:t></w:r></w:hyperlink></w:p>
                <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Столбец</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>
                """,
            ["word/_rels/document.xml.rels"] = """
                <Relationships><Relationship Id="rId1" Type="type/hyperlink" TargetMode="External"
                Target="https://example.test/path?private=example"/></Relationships>
                """,
        });
        var slides = Path.Combine(project, "slides.pptx");
        Package(slides, new Dictionary<string, string>
        {
            ["ppt/presentation.xml"] = """
                <presentation xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sldIdLst><sldId r:id="rId2"/><sldId r:id="rId1"/></sldIdLst></presentation>
                """,
            ["ppt/_rels/presentation.xml.rels"] = """
                <Relationships><Relationship Id="rId1" Target="slides/slide1.xml"/><Relationship Id="rId2" Target="slides/slide2.xml"/></Relationships>
                """,
            ["ppt/slides/slide1.xml"] = "<slide><t>Last slide</t></slide>",
            ["ppt/slides/slide2.xml"] = "<slide><t>First slide</t></slide>",
            ["ppt/slides/_rels/slide2.xml.rels"] = """
                <Relationships><Relationship Id="notes" Type="type/notesSlide" Target="../notesSlides/notesSlide1.xml"/></Relationships>
                """,
            ["ppt/notesSlides/notesSlide1.xml"] = "<notes><t>NotesUnique</t></notes>",
        });
        var sheet = Path.Combine(project, "book.xlsx");
        Package(sheet, new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = """
                <workbook xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                <sheet name="SheetName" r:id="rId3"/></workbook>
                """,
            ["xl/_rels/workbook.xml.rels"] = """
                <Relationships><Relationship Id="rId3" Target="worksheets/data.xml"/></Relationships>
                """,
            ["xl/worksheets/data.xml"] = "<worksheet><row><c t=\"inlineStr\"><is><t>CellIndex</t></is></c><c><v>42</v></c></row></worksheet>",
        });
        var csv = Path.Combine(project, "table.csv");
        File.WriteAllText(csv, "Name,Description\r\n\"quoted, name\",\"line one\r\nline two\"\r\n");
        var pdf = Path.Combine(project, "plain.pdf");
        WritePdf(pdf);
        var broken = Path.Combine(project, "corrupt.docx");
        File.WriteAllText(broken, "not a zip archive");
        var note = Path.Combine(project, "notes.txt");
        File.WriteAllText(note, "VersionOld", Encoding.GetEncoding(1251));
        File.WriteAllText(Path.Combine(project, "settings.yaml"), "service:\n  endpoint: YamlUnique\n");
        File.WriteAllText(Path.Combine(project, "values.json"), """{"service":{"endpoint":["JsonUnique"]}}""");
        File.WriteAllText(Path.Combine(project, "nodes.xml"), "<service><endpoint>XmlUnique</endpoint></service>");
        var registry = new ToolRegistry(context);
        Require(OfficeText.Extract(word) is { } doc && doc.Contains("WordIndexUnique") && doc.Contains("Столбец") &&
            doc.Contains("https://example.test/path") && !doc.Contains("private="), "DOCX headings, table cells and links");
        Require(OfficeText.Extract(slides) is { } deck && deck.IndexOf("First slide", StringComparison.Ordinal) < deck.IndexOf("Last slide", StringComparison.Ordinal) &&
            deck.Contains("NotesUnique"), "PPTX presentation order and slide notes");
        Require(OfficeText.Extract(sheet) is { } workbook && workbook.Contains("SheetName") && workbook.Contains("CellIndex\t42"), "XLSX sheet relation and cell types");
        var extracted = LocalResearchEngine.Extract(pdf, default);
        Require(extracted.Status == "ready" && extracted.Text.Contains("PDF 1") && extracted.Text.Contains("Привет"), "PDF Flate and Unicode CMap");
        Require(LocalResearchEngine.Extract(csv, default).Text.Contains("quoted, name\tline one line two"), "quoted and multiline CSV");
        for (int i = 0; i < 220; i++) File.WriteAllText(Path.Combine(project, $"notes-{i:D3}.txt"), $"Fixture document {i} with independent content.");
        watcher.Watch(context);
        var original = await watcher.SnapshotAsync(project, default);
        Require(original?.ContainsKey("Sample.cs") == true, "plain folder is indexed automatically");
        Require((await Search(registry, "WordIndexUnique")).Contains("note.docx"), "automatic document FTS");
        Require((await Search(registry, "Привет")).Contains("PDF 1"), "PDF search reports page position");
        Require((await Search(registry, "YamlUnique")).Contains("service.endpoint") &&
            (await Search(registry, "JsonUnique")).Contains("$.service.endpoint[0]") &&
            (await Search(registry, "XmlUnique")).Contains("/service/endpoint"), "structured YAML, JSON and XML positions");
        Require(!(await Search(registry, "not a zip")).Contains("corrupt.docx"), "invalid documents do not become complete index hits");
        var stamp = File.GetLastWriteTimeUtc(note);
        Require(context.Snapshots.Read(note)?.Text == "VersionOld", "initial cached version");
        File.WriteAllText(note, "VersionNew", Encoding.GetEncoding(1251));
        File.SetLastWriteTimeUtc(note, stamp);
        Require(context.Snapshots.Read(note)?.Text == "VersionNew", "same-size same-timestamp write invalidates snapshot");
        Require((await Search(registry, "VersionNew")).Contains("notes.txt") && !(await Search(registry, "VersionOld")).Contains("notes.txt"),
            "changed text replaces indexed chunks");
        var oldTime = File.GetLastWriteTimeUtc(source);
        File.WriteAllText(source, "class Sample { int Update() => 2; }");
        File.SetLastWriteTimeUtc(source, oldTime);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((await watcher.SnapshotAsync(project, timeout.Token))?.GetValueOrDefault("Sample.cs")?.Syntax.Symbols.Any(symbol => symbol.Name == "Update") != true)
            await Task.Delay(80, timeout.Token);
        var removed = Path.Combine(project, "Removed.cs");
        File.WriteAllText(removed, "class Removed {}\n");
        watcher.Reconcile(project);
        while ((await watcher.SnapshotAsync(project, timeout.Token))?.ContainsKey("Removed.cs") != true) await Task.Delay(80, timeout.Token);
        File.Delete(removed);
        watcher.Reconcile(project);
        while ((await watcher.SnapshotAsync(project, timeout.Token))?.ContainsKey("Removed.cs") != false) await Task.Delay(80, timeout.Token);
        File.Move(note, Path.Combine(project, "moved.txt"));
        Require((await Search(registry, "VersionNew")).Contains("moved.txt"), "renamed document is searchable at the new path");
        File.Delete(Path.Combine(project, "moved.txt"));
        Require(!(await Search(registry, "VersionNew")).Contains("moved.txt"), "deleted document disappears");
        var persisted = new AgentRunner(registry, new ChatStore(Path.Combine(root, "title-chats"), Path.Combine(root, "title-logs")));
        await persisted.RunAsync("прочитай файл \"Sample.cs\"", [], "fixture", default);
        Require(persisted.Api == null && persisted.Title?.Contains("Sample.cs") == true && persisted.History.Last()["content"]?.ToString().Contains("Update") == true,
            "deterministic file read and title use no provider");
        UiLanguage.Current.Value.Apply(false);
        Require(registry.Definitions.Any(def => def?["function"]?["description"]?.ToString().StartsWith("Local source and document", StringComparison.Ordinal) == true),
            "tool schema follows language changes in the current registry");
        await persisted.RunAsync("search documents for \"NotesUnique\"", [], "fixture", default);
        Require(persisted.History.Last()["content"]?.ToString().Contains("Local results", StringComparison.Ordinal) == true, "EN search in the existing chat");
        UiLanguage.Current.Value.Apply(true);
        await persisted.RunAsync("найди в документах \"NotesUnique\"", [], "fixture", default);
        Require(persisted.History.Last()["content"]?.ToString().Contains("Локальные результаты", StringComparison.Ordinal) == true, "RU search in the existing chat");
        await CheckContractAsync(registry, root);
        UiLanguage.Current.Value.Apply(false);
    }

    static async Task CheckContractAsync(ToolRegistry tools, string root)
    {
        using var provider = new IncompleteProvider();
        var agent = new AgentRunner(tools, new ChatStore(Path.Combine(root, "contract-history"), root))
            { Api = provider, IsTemporary = true, RussianLanguage = true };
        await agent.SetWorkModeAsync(false);
        await agent.RunAsync("Проверь обязательный файл.", [], "fixture", default);
        var finish = agent.History.Last()["content"]?.ToString() ?? "";
        Require(provider.Calls == 3 && finish.Contains("не подтверждено") && finish.Contains("failed") && !finish.Contains("SUCCESS"),
            "mandatory failed contract vetoes false success and stops asking when evidence does not change");
        var absent = new JsonObject { ["checks"] = new JsonArray(new JsonObject { ["kind"] = "unit_tests" }) };
        var checkedTests = await tools.ExecuteAsync(LocalContractEngine.Name, absent.ToJsonString(), null, default);
        Require(checkedTests.IsError && checkedTests.Output.Contains("not_verified"), "missing test evidence is unverified");
    }

    static async Task<string> Search(ToolRegistry tools, string query)
    {
        var result = await tools.ExecuteAsync(LocalResearchEngine.Name, new JsonObject { ["action"] = "search", ["query"] = query }.ToJsonString(), null, default);
        Require(!result.IsError, "document search succeeded: " + result.Output);
        return result.Output;
    }

    static void Package(string path, Dictionary<string, string> parts)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, text) in parts)
        {
            using var stream = new StreamWriter(archive.CreateEntry(entry).Open());
            stream.Write(text);
        }
    }

    internal static void WritePdf(string path, bool unicode = true)
    {
        using var file = new MemoryStream();
        void Write(string value) => file.Write(Encoding.ASCII.GetBytes(value));
        Write("%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 500] /Contents 4 0 R /Resources << /Font << /T1 5 0 R >> >> >>\nendobj\n");
        using var compressed = new MemoryStream();
        using (var zipper = new ZLibStream(compressed, CompressionLevel.Fastest, true))
            zipper.Write(Encoding.ASCII.GetBytes(unicode ? "BT /T1 18 Tf <010203040506> Tj ET" : "BT /T1 18 Tf (BenchmarkMarker) Tj ET"));
        Write($"4 0 obj\n<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
        compressed.WriteTo(file);
        Write("\nendstream\nendobj\n5 0 obj\n<< /Type /Font /ToUnicode 6 0 R >>\nendobj\n");
        Write("6 0 obj\n<< >>\nstream\n6 beginbfchar\n<01> <041F>\n<02> <0440>\n<03> <0438>\n<04> <0432>\n<05> <0435>\n<06> <0442>\nendbfchar\nendstream\nendobj\n%%EOF\n");
        File.WriteAllBytes(path, file.ToArray());
    }

    static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    sealed class IncompleteProvider : IChatProvider
    {
        public int Calls { get; private set; }

        public Task<List<ModelInfo>> ListModelsAsync(CancellationToken ct) => Task.FromResult<List<ModelInfo>>([]);
        public Task<ChatResult> CompleteAsync(string model, IReadOnlyList<JsonNode> messages, JsonArray? tools, int? maxTokens,
            string? effort, Action<string?, bool> reportText, Action<ModelProgress> reportProgress, CancellationToken ct,
            bool callTools = true, Action<int, int>? retrying = null)
        {
            Calls++;
            return Task.FromResult(Calls == 1 ? new ChatResult(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = "mandatory_check", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "local_contract",
                        ["arguments"] = """{"checks":[{"kind":"exists","path":"must-exist.txt"}]}""" },
                }),
            }, "tool_calls", null, null) : new ChatResult(new JsonObject { ["content"] = "SUCCESS" }, "stop", null, null));
        }
        public void Dispose() { }
    }
}
