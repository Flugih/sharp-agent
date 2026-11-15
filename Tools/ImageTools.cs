namespace SharpAgent.Tools;

public static class ImageTools
{
    public const string ResultPrefix = "Изображение создано: ";

    public static IEnumerable<AgentTool> All() =>
    [
        new AgentTool
        {
            Name = "generate_image",
            Description = "Нарисовать изображение по описанию моделью для картинок из настроек и сохранить его файлом. " +
                          "Опиши подробно: что изображено, стиль, ракурс, освещение, фон. Возвращает путь к файлу.",
            Parameters = Schema.Object(
                new Prop("prompt", "string", "Подробное описание изображения.", Required: true),
                new Prop("path", "string", "Куда сохранить файл. По умолчанию — новый файл в рабочей папке."),
                new Prop("size", "string", "Размер: квадрат 1024x1024 (по умолчанию), горизонтальный 1536x1024 или вертикальный 1024x1536.",
                    Enum: ["1024x1024", "1536x1024", "1024x1536"])),
            Handler = GenerateAsync,
        },
    ];

    public static string? SavedPath(string output) =>
        output.StartsWith(ResultPrefix, StringComparison.Ordinal) ? output[ResultPrefix.Length..].Split('\n')[0].Trim() : null;

    private static async Task<string> GenerateAsync(ToolArgs a, ToolContext c, CancellationToken ct)
    {
        var generate = c.GenerateImage ?? throw new ToolException(
            "картинки рисует Tooken Club, а его API-ключ не указан. Пользователь может добавить ключ Tooken Club в настройках " +
            "(«Подключение» → Tooken Club), даже если агент отвечает через ChatGPT или Groq.");
        var prompt = a.Required("prompt");
        var requested = c.Resolve(a.Optional("path") ?? NameFrom(prompt));
        if (Directory.Exists(requested)) requested = Path.Combine(requested, NameFrom(prompt));
        c.GuardAppFiles(requested);

        var bytes = await generate(prompt, a.Optional("size") ?? "1024x1024", ct);
        var path = PathHelper.UniquePath(Path.ChangeExtension(requested, ExtensionOf(bytes)));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return $"{ResultPrefix}{path}\nРазмер файла: {PathHelper.FormatSize(bytes.Length)}";
    }

    private static string NameFrom(string prompt)
    {
        var words = string.Join(' ', prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6));
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(words.Select(ch => invalid.Contains(ch) ? ' ' : ch).ToArray()).Trim(' ', '.');
        if (name.Length > 48) name = name[..48].Trim(' ', '.');
        return (name.Length > 0 ? name : "Изображение") + ".png";
    }

    private static string ExtensionOf(byte[] bytes) => bytes switch
    {
        [0xFF, 0xD8, ..] => ".jpg",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => ".webp",
        _ => ".png",
    };
}
