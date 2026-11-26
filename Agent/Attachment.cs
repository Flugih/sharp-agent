using static System.IO.Path;

namespace SharpAgent.Agent;

public enum AttachmentKind { File, Folder, Image }

public sealed record Attachment(string Path, AttachmentKind Kind, long Size)
{
    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff",
    };

    public string Name => GetFileName(TrimEndingDirectorySeparator(Path)) is { Length: > 0 } name ? name : TrimEndingDirectorySeparator(Path);

    public static bool IsImagePath(string path) => ImageExtensions.Contains(GetExtension(path));

    internal bool IsWord => Kind == AttachmentKind.File && GetExtension(Path).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public static Attachment? FromPath(string path)
    {
        try
        {
            var full = GetFullPath(path);
            if (Directory.Exists(full)) return new(full, AttachmentKind.Folder, 0);
            var info = new FileInfo(full);
            return info.Exists ? new(full, IsImagePath(full) ? AttachmentKind.Image : AttachmentKind.File, info.Length) : null;
        }
        catch { return null; }
    }

    public string Describe(bool russian = false) => Kind switch
    {
        AttachmentKind.Folder => russian ? "папка" : "folder",
        AttachmentKind.Image => $"{(russian ? "изображение" : "image")}, {PathHelper.FormatSize(Size)}",
        _ => $"{(russian ? "файл" : "file")}, {PathHelper.FormatSize(Size)}",
    };

    public JsonObject ToJson() => new() { ["path"] = Path, ["kind"] = Kind.ToString().ToLowerInvariant(), ["size"] = Size };

    public static List<Attachment> ListFrom(JsonObject message) =>
        message[AgentRunner.AttachmentsField] is JsonArray array ? [.. array.Select(FromJson).OfType<Attachment>()] : [];

    static Attachment? FromJson(JsonNode? node) =>
        node is JsonObject obj && JsonUtil.Str(obj["path"]) is { } path && !string.IsNullOrWhiteSpace(path)
            ? new(AppPaths.Relocate(path), JsonUtil.Str(obj["kind"]) switch
            {
                "folder" => AttachmentKind.Folder,
                "image" => AttachmentKind.Image,
                _ => AttachmentKind.File,
            }, JsonUtil.Long(obj["size"]))
            : null;
}
