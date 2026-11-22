namespace SharpAgent.Tools;

internal sealed record FileChange(string Path, string? BeforeHash, string AfterHash, string? Backup)
{
    public bool Created => BeforeHash == null;
}

internal static class FileChanges
{
    internal sealed record Pending(string Path, byte[]? Before);
    const int MaxSnapshotBytes = 4 * 1024 * 1024, MaxDiffBytes = 1024 * 1024;

    static bool Expected(Exception ex) => ex is IOException or UnauthorizedAccessException or ToolException;

    public static Pending? Capture(ToolContext context, string tool, ToolArgs args)
    {
        string path;
        try
        {
            var target = tool switch
            {
                "write_file" or "edit_file" => context.Resolve(args.Required("path")),
                "run_plugin" => PluginTools.TargetOf(args, context),
                AgentKnowledge.Name => AgentKnowledge.TargetOf(args, context),
                "copy_path" => FileTools.ResolveSourceAndTarget(args, context) is var (source, copy) && File.Exists(source) ? copy : null,
                _ => null,
            };
            if (target == null) return null;
            path = SafePath(context, target);
            if (Directory.Exists(path)) return null;
        }
        catch (Exception ex) when (Expected(ex)) { return null; }
        context.FileAccess.Reserve(path);
        try
        {
            var before = ReadSnapshot(path);
            return before == null && File.Exists(path) ? null : new Pending(path, before);
        }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }

    public static (FileChange? Change, FileDiff? Diff) Complete(ToolContext context, Pending? pending, string? image)
    {
        try
        {
            if (pending == null && image != null) pending = new Pending(SafePath(context, image), null);
            if (pending == null || !Unmoved(context, pending.Path) || ReadSnapshot(pending.Path) is not { } after) return default;
            var beforeHash = pending.Before != null ? Hash(pending.Before) : null;
            var afterHash = Hash(after);
            if (beforeHash == afterHash) return default;
            var backup = pending.Before != null ? SaveBackup(context.LogsFolder, pending.Before) : null;
            return (new FileChange(pending.Path, beforeHash, afterHash, backup), Compare(pending.Before, after));
        }
        catch (Exception ex) when (Expected(ex)) { return default; }
    }

    public static string? BackupPath(string? logsFolder, string? backup) =>
        logsFolder != null && Guid.TryParseExact(backup, "N", out var id)
            ? Path.Combine(AppPaths.CheckpointsFor(logsFolder), id.ToString("N") + ".bak") : null;

    static string? SaveBackup(string? logsFolder, byte[] bytes)
    {
        var id = Guid.NewGuid().ToString("N");
        if (BackupPath(logsFolder, id) is not { } path) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return id;
        }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }

    public static FileDiff? NetDiff(ToolContext context, FileChange change)
    {
        try { return Current(context, change) is { } after ? Compare(ReadBackup(change, context.LogsFolder), after) : null; }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }

    public static void EnsureRestorable(ToolContext context, FileChange change)
    {
        if (PathHelper.IsProtected(change.Path) || Current(context, change) == null)
            throw new ToolException($"Файл изменился после ответа: {Path.GetFileName(change.Path)}. Откройте его и проверьте текущую версию.");
        ReadBackup(change, context.LogsFolder);
    }

    public static void Restore(ToolContext context, FileChange change)
    {
        EnsureRestorable(context, change);
        if (ReadBackup(change, context.LogsFolder) is not { } before) { RecycleBin.Send(change.Path); return; }
        var staging = Path.Combine(Path.GetDirectoryName(change.Path)!, $".sharp-agent-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var write = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write.Write(before);
            EnsureRestorable(context, change);
            File.Replace(staging, change.Path, null);
        }
        finally
        {
            try { File.Delete(staging); }
            catch (Exception ex) when (Expected(ex)) { }
        }
    }

    static byte[]? Current(ToolContext context, FileChange change) =>
        Unmoved(context, change.Path) && ReadSnapshot(change.Path) is { } bytes && Hash(bytes) == change.AfterHash ? bytes : null;

    static bool Unmoved(ToolContext context, string path) => PathHelper.SamePath(SafePath(context, path), path);

    static byte[]? ReadBackup(FileChange change, string? logsFolder)
    {
        if (change.Created) return null;
        if (BackupPath(logsFolder, change.Backup) is { } path && PathHelper.SamePath(path, PathHelper.FinalPath(path)) &&
            ReadSnapshot(path) is { } bytes && Hash(bytes) == change.BeforeHash) return bytes;
        throw new ToolException($"Резервная копия недоступна: {Path.GetFileName(change.Path)}.");
    }

    static string SafePath(ToolContext context, string raw)
    {
        var path = PathHelper.FinalPath(context.Resolve(raw));
        context.GuardSecrets(path);
        context.GuardAppFiles(path);
        return path;
    }

    static byte[]? ReadSnapshot(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            if (file.Length > MaxSnapshotBytes) return null;
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            return bytes;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    static FileDiff? Compare(byte[]? before, byte[] after)
    {
        if (before?.Length > MaxDiffBytes || after.Length > MaxDiffBytes) return null;
        try { return TextDiff.Compare(before != null ? TextFileIO.Decode(before).Text : "", TextFileIO.Decode(after).Text); }
        catch (ToolException) { return null; }
    }

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string? CurrentHash(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return file.Length <= MaxSnapshotBytes ? Convert.ToHexString(SHA256.HashData(file)) : null;
        }
        catch (Exception ex) when (Expected(ex)) { return null; }
    }
}
