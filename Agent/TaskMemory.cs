namespace SharpAgent.Agent;

sealed record TaskCheckpoint(string? RequestTime, int HistoryLength, string? Plan, string[] ChangedFiles, string[]? Pending = null);

static class TaskMemory
{
    internal sealed record Fact(string Path, string Hash, DateTimeOffset Observed, string? Verification = null);

    internal static void Save(ToolContext context, string? chat, TaskCheckpoint checkpoint)
    {
        if (context.AppFolder == null || chat == null) return;
        try
        {
            using var memory = new LocalRecords<TaskCheckpoint>(context.DatabaseFolder, "memory", AgentKnowledge.Scope(context));
            memory.Save(Path.GetFileName(chat), checkpoint, 500);
        }
        catch (Exception ex) when (LocalRecords<TaskCheckpoint>.Unavailable(ex)) { }
    }

    internal static TaskCheckpoint? Load(ToolContext context, string? chat, string? requestTime, int length)
    {
        if (context.AppFolder == null || chat == null || !File.Exists(Path.Combine(context.DatabaseFolder, "memory.db"))) return null;
        try
        {
            using var memory = new LocalRecords<TaskCheckpoint>(context.DatabaseFolder, "memory", AgentKnowledge.Scope(context));
            return memory.Get(Path.GetFileName(chat)) is { } saved && saved.RequestTime == requestTime && length >= saved.HistoryLength ? saved : null;
        }
        catch (Exception ex) when (LocalRecords<TaskCheckpoint>.Unavailable(ex)) { return null; }
    }

    internal static void Remember(ToolContext context, Fact[] facts)
    {
        if (context.AppFolder == null || facts.Length == 0) return;
        try
        {
            using var memory = new LocalRecords<Fact>(context.DatabaseFolder, "memory", "facts:" + AgentKnowledge.Scope(context));
            foreach (var fact in facts)
            {
                var key = fact.Path.ToUpperInvariant();
                var previous = memory.Get(key);
                var verification = fact.Verification ?? (previous?.Hash == fact.Hash ? previous.Verification : null);
                memory.Save(key, fact with { Verification = verification }, 500);
            }
        }
        catch (Exception ex) when (LocalRecords<Fact>.Unavailable(ex)) { }
    }

    internal static string? Relevant(ToolContext context, string query)
    {
        if (context.AppFolder == null || !File.Exists(Path.Combine(context.DatabaseFolder, "memory.db"))) return null;
        try
        {
            using var memory = new LocalRecords<Fact>(context.DatabaseFolder, "memory", "facts:" + AgentKnowledge.Scope(context));
            var files = new List<string>();
            foreach (var fact in memory.Recent(140))
            {
                var stem = Path.GetFileNameWithoutExtension(fact.Path);
                if (stem.Length < 3 || !query.Contains(stem, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var path = PathHelper.FinalPath(context.Resolve(fact.Path));
                    context.GuardSecrets(path);
                    if (PathHelper.IsInside(path, context.WorkingDirectory) && FileChanges.CurrentHash(path) == fact.Hash)
                        files.Add($"{path} ({fact.Observed:yyyy-MM-dd})" + (fact.Verification == null ? "" : " · код выхода 0: " + fact.Verification));
                    if (files.Count == 6) break;
                }
                catch (Exception ex) when (ex is ToolException or IOException or UnauthorizedAccessException) { }
            }
            return files.Count == 0 ? null : "Подтверждённые версии файлов из прошлых запусков:\n" + string.Join("\n", files);
        }
        catch (Exception ex) when (LocalRecords<Fact>.Unavailable(ex)) { return null; }
    }
}
