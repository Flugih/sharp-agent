namespace SharpAgent.Tools;

partial class ToolRegistry
{
    const string FailedBatchPrefix = "Ошибка: пакет не выполнен", PartialBatchPrefix = "Пакет выполнен частично";

    internal static bool IsPartialBatch(string output) => output.StartsWith(PartialBatchPrefix, StringComparison.Ordinal);
    internal static bool HasBatchStatus(string output) => output.StartsWith(FailedBatchPrefix, StringComparison.Ordinal) || IsPartialBatch(output);

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
        var results = await Task.WhenAll(work.Select(operation => Task.Run(async () =>
        {
            ToolOutcome result;
            try
            {
                var local = new ToolArgs(operation.Args) { RetryBusyDocument = args.RetryBusyDocument };
                result = operation.Name switch
                {
                    "git_status" => await LocalProjectContext.RunGitRequestAsync(context, "git status", ct),
                    "git_diff" => await LocalProjectContext.RunGitRequestAsync(context, "git diff", ct),
                    "diagnostics" => await LocalProjectContext.DiagnosticsAsync(context, ct),
                    "read" => new(await local.WithDocumentRetryAsync(() => Task.FromResult(FileTools.ReadFile(local, context, ct)), ct), false),
                    "ranges" => await FileTools.ReadManyAsync(local, context, ct),
                    "grep" => new(FileTools.SearchText(local, context, ct), false),
                    _ => new(FileTools.SearchFiles(local, context, ct), false),
                };
            }
            catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException)
            {
                result = Error("Ошибка: " + ex.Message);
            }
            return result with { Output = operation.Name + "\n" + result.Output };
        }, ct)));
        int completed = results.Count(result => !result.IsError && !result.IsWarning);
        var output = string.Join("\n\n", results.Select(result => result.Output));
        if (completed == results.Length) return output;
        var status = results.All(result => result.IsError) ? FailedBatchPrefix : PartialBatchPrefix;
        return $"{status}. Операций без ошибок: {completed} из {results.Length}.\n{output}";
    }
}
