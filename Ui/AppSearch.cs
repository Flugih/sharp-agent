namespace SharpAgent.Ui;

enum SearchArea { All, Actions, Chats, Folders, Files, Text }

sealed record SearchMatch(SearchArea Area, string Title, string Detail, string Path, string Snippet = "", int Line = 0, int MessageIndex = -1, string Query = "")
{
    public string Group => Area switch
    {
        SearchArea.Actions => Tr("Быстрые действия", "Quick actions"),
        SearchArea.Chats => Tr("Чаты", "Chats"),
        SearchArea.Folders => Tr("Папки", "Folders"),
        SearchArea.Text => Tr("Текст в файлах", "Text in files"),
        _ => Tr("Файлы", "Files"),
    };
    public string Glyph => Area switch
    {
        SearchArea.Actions => Path == "new" ? Glyphs.Edit : Path == "settings" ? "\uE713" : Glyphs.Folder,
        SearchArea.Chats => "\uE8F2",
        SearchArea.Folders => Glyphs.Folder,
        SearchArea.Text => Glyphs.Search,
        _ => Glyphs.Document,
    };
}

static class AppSearch
{
    const int MaxResults = 60, MaxEntries = 20_000;
    static readonly Regex SentenceBoundary = new("""[.!?…]+[»”’"')\]}]*(?=\s|$)|[。！？]+[」』】»”"]*|\r?\n[^\S\r\n]*\r?\n|\u2029""",
        RegexOptions.Compiled);
    static readonly Regex Abbreviation = new(@"(?:\b(?:Mr|Mrs|Ms|Dr|Prof|St|vs|г|гг|им|ул|рис|стр|см|т)|(?-i:\b\p{Lu})|\b(?:e\.g|i\.e|т\.\s*[едпн]))\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "node_modules", "dist", "build", "target", "vendor", "packages", "__pycache__", "auth", "history", "temp", "attachments", "publish" };

    public static (List<SearchMatch> Matches, bool Limited) Find(ToolContext context, ChatStore store, string query, SearchArea area, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var matches = new List<SearchMatch>();
        bool limited = false;
        bool Includes(SearchArea kind) => area == SearchArea.All || area == kind;
        bool Matches(string text) => query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase);
        if (Includes(SearchArea.Actions))
        {
            AddAction("new", Tr("Новый чат", "New chat"), "Ctrl+N");
            AddAction("folder", Tr("Открыть рабочую папку", "Open working folder"), "Ctrl+Shift+O");
            AddAction("settings", Tr("Настройки", "Settings"), "Ctrl+,");
        }
        if (Includes(SearchArea.Chats))
        {
            foreach (var file in store.ListChatFiles())
            {
                ct.ThrowIfCancellationRequested();
                if (matches.Count >= MaxResults) { limited = true; break; }
                try
                {
                    var summary = ChatStore.Summaries([file]).FirstOrDefault();
                    if (summary == null) continue;
                    string snippet = "";
                    int messageIndex = -1;
                    if (query.Length > 0)
                    {
                        if (file.Length > 32 * 1024 * 1024 && !ChatJournal.IsJournal(file.FullName)) limited = true;
                        else
                        {
                            foreach (var (position, message) in store.ReadMessages(file.FullName, textOnly: true))
                            {
                                ct.ThrowIfCancellationRequested();
                                if (AgentRunner.Role(message) is not ("user" or "assistant") || !Matches(AgentRunner.ExtractText(message["content"]) ?? "")) continue;
                                messageIndex = position;
                                snippet = Excerpt(AgentRunner.ExtractText(message["content"]) ?? "", query);
                                break;
                            }
                        }
                        if (messageIndex < 0 && !Matches(summary.Title)) continue;
                    }
                    matches.Add(new(SearchArea.Chats, summary.Title, summary.Updated.ToString("d MMM", UiLanguage.Culture), file.FullName,
                        snippet, MessageIndex: messageIndex, Query: query));
                    if (query.Length == 0 && matches.Count >= 10) break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            }
        }
        if (Includes(SearchArea.Files) || Includes(SearchArea.Folders) || query.Length > 0 && Includes(SearchArea.Text))
        {
            var root = context.Resolve(context.WorkingDirectory);
            var directories = new Stack<string>();
            directories.Push(root);
            int scanned = 0;
            long bytes = 0;
            var recent = new List<FileInfo>();
            while (directories.TryPop(out var folder))
            {
                ct.ThrowIfCancellationRequested();
                if (scanned >= MaxEntries || matches.Count >= MaxResults) { limited = true; break; }
                try
                {
                    foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline }))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (++scanned > MaxEntries || matches.Count >= MaxResults) { limited = true; break; }
                        if (entry.Name.StartsWith('.') || entry.Name.EndsWith(".key", StringComparison.OrdinalIgnoreCase) || entry.Name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)) continue;
                        if (entry is DirectoryInfo && SkippedFolders.Contains(entry.Name)) continue;
                        string path;
                        try
                        {
                            path = context.Resolve(entry.FullName);
                            if (!PathHelper.IsInside(PathHelper.FinalPath(path), root)) continue;
                            context.GuardSecrets(path);
                            context.GuardAppFiles(path);
                        }
                        catch (ToolException) { continue; }
                        var relative = Path.GetRelativePath(root, path);
                        if (entry is DirectoryInfo)
                        {
                            directories.Push(path);
                            if (Includes(SearchArea.Folders) && (query.Length > 0 || area == SearchArea.Folders) && Matches(relative))
                                matches.Add(new(SearchArea.Folders, entry.Name, relative, path));
                            continue;
                        }
                        var file = (FileInfo)entry;
                        if (query.Length == 0)
                        {
                            if (Includes(SearchArea.Files)) recent.Add(file);
                            continue;
                        }
                        if (Includes(SearchArea.Files) && Matches(relative)) matches.Add(new(SearchArea.Files, entry.Name, relative, path));
                        if (!Includes(SearchArea.Text)) continue;
                        if (bytes + file.Length > 32 * 1024 * 1024) { limited = true; continue; }
                        bytes += file.Length;
                        var text = TextFileIO.TryRead(path, 512 * 1024);
                        if (text == null) continue;
                        int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
                        if (at < 0) continue;
                        int line = text.AsSpan(0, at).Count('\n') + 1;
                        matches.Add(new(SearchArea.Text, entry.Name, relative + $":{line}", path, Excerpt(text, query), line, Query: query));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (query.Length == 0 && Includes(SearchArea.Files))
                matches.AddRange(recent.OrderByDescending(file => file.LastWriteTimeUtc).Take(8)
                    .Select(file => new SearchMatch(SearchArea.Files, file.Name, Path.GetRelativePath(root, file.FullName), file.FullName)));
            if (query.Length == 0 && Includes(SearchArea.Folders)) matches.Add(new(SearchArea.Folders, Path.GetFileName(root), root, root));
        }
        return (matches.Take(MaxResults).ToList(), limited || matches.Count > MaxResults);

        void AddAction(string id, string title, string shortcut)
        {
            if (Matches(title)) matches.Add(new(SearchArea.Actions, title, shortcut, id));
        }
    }

    internal static (int Start, int Length) SentenceRange(string text, int at, int length)
    {
        int start = 0, end = text.Length, matchEnd = at + length;
        foreach (var boundary in SentenceBoundary.EnumerateMatches(text))
        {
            int index = boundary.Index, next = index + boundary.Length;
            if (text[index] == '.' && (boundary.Length == 1 || text[index + 1] != '.') && next < text.Length &&
                Abbreviation.IsMatch(text.AsSpan(Math.Max(0, index - 12)..(index + 1)))) continue;
            if (next <= at) start = next;
            else if (next >= matchEnd)
            {
                end = text[index] is '\r' or '\n' or '\u2029' ? Math.Max(index, matchEnd) : next;
                break;
            }
        }
        while (start < at && char.IsWhiteSpace(text[start])) start++;
        while (end > matchEnd && char.IsWhiteSpace(text[end - 1])) end--;
        return (start, end - start);
    }

    static string Excerpt(string text, string query)
    {
        int at = Math.Max(0, text.IndexOf(query, StringComparison.OrdinalIgnoreCase));
        int start = Math.Max(0, at - 50);
        return (start > 0 ? "…" : "") + text.Substring(start, Math.Min(180, text.Length - start)).ReplaceLineEndings(" ");
    }
}
