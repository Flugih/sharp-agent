using System.Collections.Concurrent;

namespace SharpAgent.Api;

sealed class VoiceEffort
{
    const string Prefix = "voice:";
    static readonly string[] Modes = ["none", "between_tools", .. AppConfig.EffortLevels];
    readonly ConcurrentDictionary<string, string> _accepted = new(StringComparer.OrdinalIgnoreCase);

    internal static string Select(string[]? advertised) =>
        Prefix + (advertised == null ? "?" : string.Join(',', Modes.Where(advertised.Contains)));

    internal static string[]? CatalogModes(JsonObject? model)
    {
        if (model == null) return null;
        var caps = model["capabilities"] as JsonObject;
        return ReadModes(model["supported_reasoning_levels"] ?? (model["reasoning"] as JsonObject)?["supported_efforts"] ??
            caps?["supported_reasoning_levels"] ?? (caps?["reasoning"] as JsonObject)?["supported_efforts"]);
    }

    static string[]? ReadModes(JsonNode? node) => node is JsonArray array
        ? array.Select(item => Str(item is JsonObject entry ? entry["effort"] : item))
            .OfType<string>().Select(mode => mode.ToLowerInvariant()).Select(mode => mode is "off" or "disabled" ? "none" : mode)
            .Where(Modes.Contains).Distinct().ToArray()
        : null;

    internal Attempt? Start(string model, ref string? effort, bool betweenTools = false)
    {
        if (effort?.StartsWith(Prefix, StringComparison.Ordinal) != true) return null;
        var raw = effort[Prefix.Length..];
        string[] options = raw switch
        {
            "?" when betweenTools => ["none", "between_tools", "low"],
            "?" => ["none", "minimal", "low"],
            "" => [],
            _ => raw.Split(','),
        };
        effort = _accepted.TryGetValue(model, out var saved) ? saved.Length == 0 ? null : saved : options.FirstOrDefault();
        return new(this, model, options);
    }

    internal sealed class Attempt(VoiceEffort owner, string model, string[] options)
    {
        readonly HashSet<string> _rejected = new(StringComparer.Ordinal);

        internal void Accept(string? effort) => owner._accepted[model] = effort ?? "";

        internal bool Retry(ApiException error, ref string? effort)
        {
            if (effort == null || !Rejected(error)) return false;
            _rejected.Add(effort);
            owner._accepted.TryRemove(model, out _);
            if (error.Code == "unsupported_parameter")
            {
                effort = null;
                return true;
            }
            var supported = Regex.Match(error.Message,
                @"(?:supported (?:values|efforts|levels)(?: are)?|allowed values(?: are)?|one of)\s*[:=]?\s*(?<values>[^\r\n]+)",
                RegexOptions.IgnoreCase).Groups["values"].Value;
            var advertised = Regex.Matches(supported.ToLowerInvariant(), @"[a-z_]+").Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
            effort = (advertised.Overlaps(Modes) ? Modes.Where(advertised.Contains) : options)
                .FirstOrDefault(candidate => !_rejected.Contains(candidate));
            return true;
        }
    }

    static bool Rejected(ApiException error) =>
        !error.Retryable && (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity ||
         error.StatusCode is (null or HttpStatusCode.OK) && error.Code is ("unsupported_value" or "unsupported_parameter" or "invalid_parameter")) &&
        Regex.IsMatch(error.Parameter ?? error.Message, @"\b(?:reasoning(?:_effort|\.effort)?|thinking(?:_level|\.type)?|output_config)\b", RegexOptions.IgnoreCase);
}
