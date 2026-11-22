using System.Xml;
using System.Xml.Linq;

namespace SharpAgent.Agent;

sealed class SourceProjects(ToolContext context, string root)
{
    internal sealed record Project(string Id, string[] References, string Revision);
    readonly Dictionary<string, string?> _directories = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Project> _projects = new(StringComparer.OrdinalIgnoreCase);

    internal static bool Manifest(string path) => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);

    internal Project Of(string file)
    {
        var directory = Path.GetDirectoryName(Path.Combine(root, file))!;
        return Find(directory) is { } path ? Read(path) : new("loose", [], "loose");
    }

    string? Find(string directory)
    {
        if (!PathHelper.IsInside(directory, root)) return null;
        if (_directories.TryGetValue(directory, out var saved)) return saved;
        string? result = null;
        try
        {
            var projects = Directory.EnumerateFiles(directory, "*.csproj").Where(Safe).Take(2).ToArray();
            if (projects.Length == 1) result = projects[0];
            else if (projects.Length == 0 && Path.GetDirectoryName(directory) is { } parent) result = Find(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ToolException) { }
        return _directories[directory] = result;
    }

    Project Read(string path)
    {
        if (_projects.TryGetValue(path, out var old)) return old;
        string[] references = [];
        var revision = new StringBuilder();
        try
        {
            Stamp(revision, path);
            using var stream = File.OpenRead(path);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 250_000 });
            var paths = XDocument.Load(reader).Descendants().Where(element => element.Name.LocalName == "ProjectReference" &&
                    !element.AncestorsAndSelf().Any(node => node.Attribute("Condition") != null) &&
                    !string.Equals(element.Element(element.Name.Namespace + "ReferenceOutputAssembly")?.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase))
                .Select(element => (string?)element.Attribute("Include")).OfType<string>()
                .Where(include => include.Length > 0 && !Path.IsPathRooted(include) && include.IndexOfAny(['*', '?', '$', '@', ';']) < 0)
                .Select(include => Path.GetFullPath(include, Path.GetDirectoryName(path)!))
                .Where(reference => Manifest(reference) && Safe(reference)).Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
            foreach (var reference in paths) Stamp(revision, reference);
            references = paths.Select(Id).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException or ToolException) { }
        return _projects[path] = new(Id(path), references, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(revision.ToString()))));
    }

    static void Stamp(StringBuilder revision, string path)
    {
        var file = new FileInfo(path);
        revision.Append(Id(path)).Append(':').Append(file.LastWriteTimeUtc.Ticks).Append(':').Append(file.Length).Append(';');
    }

    internal static string Id(string path) => "project_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];

    bool Safe(string path)
    {
        if (!ProjectSourceIndex.Trackable(Path.GetRelativePath(root, path))) return false;
        var full = context.Resolve(path);
        context.GuardSecrets(full);
        return File.Exists(full) && PathHelper.SamePath(full, PathHelper.FinalPath(full));
    }
}
