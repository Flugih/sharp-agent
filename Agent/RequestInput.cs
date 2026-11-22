using System.Runtime.CompilerServices;

namespace SharpAgent.Agent;

sealed record RequestInput(int System, int Tools, int History, int Project, int Results, int Attachments,
    int StablePrefix = 0, int RepeatedResults = 0)
{
    internal int Total => System + Tools + History + Project + Results + Attachments;
}

sealed class RequestInputMeter
{
    sealed record SchemaMeasurement(string Hash, int Tokens);
    static readonly ConditionalWeakTable<JsonArray, SchemaMeasurement> Schemas = new();
    static readonly SchemaMeasurement Empty = new(Hash(""), 0);
    (AgentProvider Provider, string Model, (string Hash, int Tokens)[] Parts)? _previous;
    HashSet<string> _previousResults = [];

    internal RequestInput Measure(AgentProvider provider, string model, IReadOnlyList<JsonNode> messages, JsonArray? definitions, int project, int attachments)
    {
        int system = 0, results = 0, history = 0, repeated = 0;
        var schemas = MeasureSchemas(definitions);
        int tools = schemas.Tokens;
        var parts = new List<(string Hash, int Tokens)> { (schemas.Hash, tools) };
        var outputs = new HashSet<string>();
        bool sameModel = _previous is { } previous && previous.Provider == provider && previous.Model == model;
        foreach (var message in messages)
        {
            int estimate = HistoryCompressor.Estimate(message);
            parts.Add((Hash(message.ToJsonString()), estimate));
            switch (AgentRunner.Role(message))
            {
                case "system": system += estimate; break;
                case "tool":
                    results += estimate;
                    var hash = Hash(AgentRunner.ExtractText(message["content"]) ?? "");
                    outputs.Add(hash);
                    if (sameModel && _previousResults.Contains(hash)) repeated += estimate;
                    break;
                default: history += estimate; break;
            }
        }
        int prefix = 0;
        if (sameModel && _previous is { } prior)
            for (int i = 0; i < parts.Count && i < prior.Parts.Length && parts[i].Hash == prior.Parts[i].Hash; i++)
                prefix += parts[i].Tokens;
        _previous = (provider, model, [.. parts]);
        _previousResults = outputs;
        return new(system, tools, Math.Max(0, history - project), Math.Min(history, project), results, attachments, prefix, repeated);
    }

    internal static int ToolTokens(JsonArray definitions) => MeasureSchemas(definitions).Tokens;

    static SchemaMeasurement MeasureSchemas(JsonArray? definitions) => definitions == null ? Empty :
        Schemas.GetValue(definitions, static items =>
        {
            var text = items.ToJsonString(Compact);
            return new(Hash(text), text.Length / 3);
        });

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
