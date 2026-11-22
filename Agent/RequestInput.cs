using System.Runtime.CompilerServices;

namespace SharpAgent.Agent;

sealed record RequestInput(int System, int Tools, int History, int Project, int Results, int Attachments,
    int StablePrefix = 0, int RepeatedResults = 0, int RepeatedInPrefix = 0, string? SchemaVersion = null,
    string? PrefixChange = null, string? Provider = null)
{
    internal int Total => System + Tools + History + Project + Results + Attachments;
}

sealed class RequestInputMeter
{
    sealed record SchemaMeasurement(string Hash, int Tokens);
    static readonly ConditionalWeakTable<JsonArray, SchemaMeasurement> Schemas = new();
    static readonly SchemaMeasurement Empty = new(Hash(""), 0);
    (AgentProvider Provider, string Model, string Schema, string? Options, Part[] Parts)? _previous;
    HashSet<string> _previousResults = [];
    string? _reset;
    readonly record struct Part(string Hash, string? Role, int Tokens, bool Repeated = false);

    internal void Reset(string reason)
    {
        _previous = null;
        _previousResults.Clear();
        _reset = reason;
    }

    internal RequestInput Measure(AgentProvider provider, string model, IReadOnlyList<JsonNode> messages, JsonArray? definitions, int project,
        int attachments, string? options = null)
    {
        int system = 0, results = 0, history = 0, repeated = 0;
        var schemas = MeasureSchemas(definitions);
        int tools = schemas.Tokens;
        var parts = new List<Part>(messages.Count);
        var outputs = new HashSet<string>();
        bool sameModel = _previous is { } previous && previous.Provider == provider && previous.Model == model;
        foreach (var message in messages)
        {
            int estimate = HistoryCompressor.Estimate(message);
            var role = AgentRunner.Role(message);
            bool sent = false;
            switch (role)
            {
                case "system": system += estimate; break;
                case "tool":
                    results += estimate;
                    var hash = Hash(AgentRunner.ExtractText(message["content"]) ?? "");
                    outputs.Add(hash);
                    sent = sameModel && _previousResults.Contains(hash);
                    if (sent) repeated += estimate;
                    break;
                default: history += estimate; break;
            }
            parts.Add(new(Hash(message.ToJsonString()), role, estimate, sent));
        }
        int prefix = 0, repeatedPrefix = 0;
        string? reason = _reset ?? "first_request";
        if (_previous is { } prior)
        {
            reason = prior.Provider != provider ? "provider_changed" : !sameModel ? "model_changed" :
                prior.Schema != schemas.Hash ? "tools_changed" : prior.Options != options ? "options_changed" : null;
            if (reason == null)
            {
                prefix = tools;
                int i = 0;
                for (; i < parts.Count && i < prior.Parts.Length && parts[i].Hash == prior.Parts[i].Hash; i++)
                {
                    prefix += parts[i].Tokens;
                    if (parts[i].Repeated) repeatedPrefix += parts[i].Tokens;
                }
                if (i < prior.Parts.Length)
                    reason = i >= parts.Count ? "history_shortened" : parts[i].Role == "tool" && prior.Parts[i].Role == "tool" ?
                        "tool_output_changed" : parts[i].Role == "system" ? "system_changed" : "history_changed";
            }
        }
        _reset = null;
        _previous = (provider, model, schemas.Hash, options, [.. parts]);
        _previousResults = outputs;
        return new(system, tools, Math.Max(0, history - project), Math.Min(history, project), results, attachments,
            prefix, repeated, repeatedPrefix, schemas.Hash[..12], reason, provider.ToString());
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
