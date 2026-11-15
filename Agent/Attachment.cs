using System.Text.Json.Nodes;
using SharpAgent.Core;
using SharpAgent.Tools;

namespace SharpAgent.Agent;

public enum AttachmentKind { File, Folder, Image }

public sealed record Attachment(string Path, AttachmentKind Kind, long Size)
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff",
    };

    public string Name
    {
        get
        {
            var trimmed = System.IO.Path.TrimEndingDirectorySeparator(Path);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    public static bool IsImagePath(string path) => ImageExtensions.Contains(System.IO.Path.GetExtension(path));

    public static Attachment? FromPath(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            if (Directory.Exists(full)) return new Attachment(full, AttachmentKind.Folder, 0);
            var info = new FileInfo(full);
            if (!info.Exists) return null;
            return new Attachment(full, IsImagePath(full) ? AttachmentKind.Image : AttachmentKind.File, info.Length);
        }
        catch
        {
            return null;
        }
    }

    public string Describe() => Kind switch
    {
        AttachmentKind.Folder => "папка",
        AttachmentKind.Image => $"изображение, {PathHelper.FormatSize(Size)}",
        _ => $"файл, {PathHelper.FormatSize(Size)}",
    };

    public JsonObject ToJson() => new()
    {
        ["path"] = Path,
        ["kind"] = Kind.ToString().ToLowerInvariant(),
        ["size"] = Size,
    };

    public static Attachment? FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var path = JsonUtil.Str(obj["path"]);
        if (string.IsNullOrWhiteSpace(path)) return null;
        var kind = JsonUtil.Str(obj["kind"]) switch
        {
            "folder" => AttachmentKind.Folder,
            "image" => AttachmentKind.Image,
            _ => AttachmentKind.File,
        };
        return new Attachment(path, kind, JsonUtil.Long(obj["size"]));
    }

    public static List<Attachment> ListFrom(JsonObject message) =>
        message[AgentRunner.AttachmentsField] is JsonArray array ? [.. array.Select(FromJson).OfType<Attachment>()] : [];
}
