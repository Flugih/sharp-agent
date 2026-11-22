namespace SharpAgent.Tools;

partial class ToolRegistry
{
    AgentTool BatchTool() => new()
    {
        Name = "local_batch",
        Description = "Параллельно выполнить до 8 независимых операций чтения, поиска, анализа Git и диагностики синтаксиса. Для зависимых действий используй отдельные этапы.",
        Parameters = JsonNode.Parse("""
            {"type":"object","properties":{"operations":{"type":"array","minItems":1,"maxItems":8,"items":{"type":"object",
            "properties":{"name":{"type":"string","enum":["read","ranges","search_files","grep","git_status","git_diff","diagnostics"]},
            "arguments":{"type":"object"}},"required":["name"]}}},"required":["operations"]}
            """)!.AsObject(),
        Handler = RunBatchAsync,
    };

    async Task<string> RunBatchAsync(ToolArgs args, ToolContext context, CancellationToken ct)
    {
        var operations = args.Array("operations");
        if (operations.Count is < 1 or > 8 || operations.Any(operation => operation is not JsonObject))
            throw new ToolException("Нужно от одной до восьми операций.");
        var work = operations.OfType<JsonObject>().Select(operation =>
            (Name: Str(operation["name"]) ?? "", Args: operation["arguments"] as JsonObject ?? [])).ToArray();
        if (work.Any(operation => operation.Name is not ("read" or "ranges" or "search_files" or "grep" or "git_status" or "git_diff" or "diagnostics")))
            throw new ToolException("В пакет можно включать только указанные операции чтения и диагностики.");
        var results = await Task.WhenAll(work.Select(async operation =>
        {
            var result = operation.Name switch
            {
                "git_status" => await LocalProjectContext.RunGitRequestAsync(context, "git status", ct),
                "git_diff" => await LocalProjectContext.RunGitRequestAsync(context, "git diff", ct),
                "diagnostics" => await LocalProjectContext.DiagnosticsAsync(context, ct),
                "read" or "ranges" or "grep" => await Task.Run(() =>
                {
                    var local = new ToolArgs(operation.Args);
                    try
                    {
                        return operation.Name switch
                        {
                            "read" => FileTools.ReadFile(local, context, ct),
                            "ranges" => FileTools.ReadMany(local, context, ct),
                            _ => FileTools.SearchText(local, context, ct),
                        };
                    }
                    catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
                    {
                        return "Ошибка: " + ex.Message;
                    }
                }, ct),
                _ => (await ExecuteAsync(operation.Name, operation.Args.ToJsonString(), null, ct)).Output,
            };
            return operation.Name + "\n" + result;
        }));
        return string.Join("\n\n", results);
    }
}
