namespace SharpAgent.Api;

// Image generation for chat providers that have no OpenAI-style /images/generations endpoint.
public static class ImageApi
{
    const string AgentName = "Sharp Agent — изображения";
    static readonly HttpClient Http = AppNetwork.Client(TimeSpan.FromMinutes(3), 64 << 20);
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> MistralAgents = new();

    public static Task<byte[]> GenerateAsync(AgentProvider provider, string key, string model, string prompt, string size, CancellationToken ct) => provider switch
    {
        AgentProvider.Gemini => GeminiAsync(key, model, prompt, size, ct),
        AgentProvider.Mistral => MistralAsync(key, model, prompt, size, ct),
        _ => OpenRouterAsync(key, model, prompt, size, ct),
    };

    static string Aspect(string size) => size switch { "1536x1024" => "3:2", "1024x1536" => "2:3", _ => "1:1" };

    static async Task<byte[]> GeminiAsync(string key, string model, string prompt, string size, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent",
            ("x-goog-api-key", key), new JsonObject
            {
                ["contents"] = new JsonArray(new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = prompt }) }),
                ["generationConfig"] = new JsonObject
                {
                    ["responseModalities"] = new JsonArray("TEXT", "IMAGE"), ["imageConfig"] = new JsonObject { ["aspectRatio"] = Aspect(size) },
                },
            }, ct);
        var parts = ((json["candidates"] as JsonArray)?.FirstOrDefault()?["content"]?["parts"] as JsonArray) ?? [];
        return parts.Select(part => Str(part?["inlineData"]?["data"])).FirstOrDefault(data => data != null) is { } image
            ? Convert.FromBase64String(image) : throw NoImage(json);
    }

    static async Task<byte[]> OpenRouterAsync(string key, string model, string prompt, string size, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, "https://openrouter.ai/api/v1/images", ("Authorization", "Bearer " + key),
            new JsonObject { ["model"] = model, ["prompt"] = prompt, ["aspect_ratio"] = Aspect(size) }, ct);
        return Str((json["data"] as JsonArray)?.FirstOrDefault()?["b64_json"]) is { Length: > 0 } image ? Convert.FromBase64String(image) : throw NoImage(json);
    }

    // Mistral draws through its built-in image_generation tool, which needs an agent; one agent per model is reused.
    static async Task<byte[]> MistralAsync(string key, string model, string prompt, string size, CancellationToken ct, string? missing = null)
    {
        var auth = ("Authorization", "Bearer " + key);
        var cacheKey = UsageTracker.KeyId(key, model);
        if (!MistralAgents.TryGetValue(cacheKey, out var agent) || agent == missing)
        {
            var existing = await SendAsync(HttpMethod.Get, "https://api.mistral.ai/v1/agents?page_size=100", auth, null, ct);
            agent = (existing as JsonArray ?? existing["data"] as JsonArray ?? [])
                .FirstOrDefault(item => Str(item?["name"]) == AgentName && Str(item?["model"]) == model && Str(item?["id"]) != missing)?["id"] is { } found ? Str(found)! :
                Str((await SendAsync(HttpMethod.Post, "https://api.mistral.ai/v1/agents", auth, new JsonObject
                {
                    ["model"] = model, ["name"] = AgentName, ["instructions"] = "Нарисуй изображение по описанию пользователя.",
                    ["tools"] = new JsonArray(new JsonObject { ["type"] = "image_generation" }),
                }, ct))["id"]) ?? throw new ApiException("Mistral не создал агента для изображений.");
            MistralAgents[cacheKey] = agent;
        }
        JsonNode json;
        try
        {
            json = await SendAsync(HttpMethod.Post, "https://api.mistral.ai/v1/conversations", auth,
                new JsonObject { ["agent_id"] = agent, ["inputs"] = $"{prompt}\nСоотношение сторон: {Aspect(size)}." }, ct);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound && missing == null)
        {
            return await MistralAsync(key, model, prompt, size, ct, agent);
        }
        var file = ((json["outputs"] as JsonArray) ?? []).SelectMany(output => output?["content"] as JsonArray ?? [])
            .Where(chunk => Str(chunk?["type"]) == "tool_file").Select(chunk => Str(chunk?["file_id"])).LastOrDefault(id => id != null)
            ?? throw NoImage(json);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.mistral.ai/v1/files/{Uri.EscapeDataString(file)}/content");
        request.Headers.TryAddWithoutValidation(auth.Item1, auth.Item2);
        using var response = await Http.SendAsync(request, ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (!response.IsSuccessStatusCode) throw ApiException.FromResponse(response.StatusCode, Encoding.UTF8.GetString(bytes), null);
        return bytes is [0x89, 0x50, ..] or [0xFF, 0xD8, ..] or [(byte)'R', (byte)'I', ..] ? bytes
            : Convert.FromBase64String(Encoding.ASCII.GetString(bytes).Trim().Trim('"'));
    }

    static async Task<JsonNode> SendAsync(HttpMethod method, string url, (string Name, string Value) auth, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation(auth.Name, auth.Value);
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw ApiException.FromResponse(response.StatusCode, text, null);
        try { return JsonNode.Parse(text) ?? throw new JsonException(); }
        catch (JsonException) { throw new ApiException("Сервер вернул не JSON: " + ApiException.Truncate(text, 300)); }
    }

    static ApiException NoImage(JsonNode json) => new("Провайдер не вернул изображение: " + ApiException.Truncate(json.ToJsonString(), 400));
}
