using System.IO.Enumeration;
using System.Security;
using Microsoft.CSharp.RuntimeBinder;
using Microsoft.Win32;

namespace SharpAgent.Tools;

static class ComputerInventory
{
    internal const string FindFolders = "find_folders_on_pc", ListApps = "list_installed_apps";
    const int MatchesPerDisk = 50;

    internal static IEnumerable<AgentTool> All() =>
    [
        new AgentTool
        {
            Name = FindFolders,
            Description = "Найти папки по названию на всех подключённых локальных дисках, когда диск или путь неизвестен. Возвращает полные пути. Перед поиском приложение обязательно спросит разрешение пользователя.",
            Parameters = JsonNode.Parse("""{"type":"object","properties":{"names":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":8,"description":"Названия папки и варианты написания, в том числе латиницей для распознанной речи."}},"required":["names"]}""")!.AsObject(),
            Confirmation = (args, _) => new(Tr("Искать папку на всех дисках?", "Search all local drives for the folder?"), string.Join("\n", FolderNames(args)),
                Tr("Разрешить поиск", "Allow search"), Note: Tr("Агент просмотрит названия папок на подключённых локальных дисках за пределами проекта.",
                    "The agent will look at folder names on connected local drives outside the project."), AlwaysAsk: true),
            Handler = AgentTool.Sync((args, context, ct) => SearchFolders(context, FolderNames(args), DriveInfo.GetDrives()
                .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram).Select(drive => drive.Name), ct)),
        },
        new AgentTool
        {
            Name = ListApps,
            Description = "Показать установленные приложения Windows и приложения из списка Пуск. Перед чтением списка приложение обязательно спросит разрешение пользователя.",
            Parameters = Schema.Object(),
            Confirmation = (_, _) => new(Tr("Прочитать список приложений Windows?", "Read the Windows application list?"),
                Tr("Установленные приложения и список Пуск", "Installed apps and the Start application list"), Tr("Разрешить чтение", "Allow reading"),
                Note: Tr("Агент получит названия приложений и версии, указанные в Windows.", "The agent will receive application names and versions registered with Windows."),
                AlwaysAsk: true),
            Handler = AgentTool.Sync((_, _, ct) => ListApplications(ct)),
        },
    ];

    static string[] FolderNames(ToolArgs args)
    {
        var input = args.Array("names");
        var names = input.Select(Str).OfType<string>().Select(name => name.Trim()).ToArray();
        if (names.Length is < 1 or > 8 || names.Length != input.Count || names.Any(name => name.Length > 160 || Key(name).Length == 0))
            throw new ToolException("Укажите от одного до восьми названий папки.");
        return names;
    }

    static string Key(string name) => string.Concat(name.Where(char.IsLetterOrDigit));

    internal static string SearchFolders(ToolContext context, string[] names, IEnumerable<string> roots, CancellationToken ct)
    {
        var keys = names.Select(Key).ToArray();
        var protectedRoots = context.ProtectedInventoryRoots();
        var output = new StringBuilder();
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            var path = context.Resolve(root);
            if (Protected(path)) continue;
            output.AppendLine(path);
            try
            {
                var entries = new FileSystemEnumerable<string>(path, (ref FileSystemEntry entry) => entry.ToFullPath(),
                    new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                {
                    ShouldRecursePredicate = (ref FileSystemEntry entry) => !Protected(entry.ToFullPath()),
                    ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!entry.IsDirectory) return false;
                        var name = Key(entry.FileName.ToString());
                        return keys.Any(key => name.Contains(key, StringComparison.OrdinalIgnoreCase)) && !Protected(entry.ToFullPath());
                    },
                };
                int hits = 0;
                foreach (var match in entries)
                {
                    var candidate = context.Resolve(match);
                    if (context.IsSecret(candidate)) continue;
                    if (hits++ == MatchesPerDisk) { output.AppendLine("  Есть ещё совпадения. Уточните название."); break; }
                    output.AppendLine("  " + candidate);
                }
                if (hits == 0) output.AppendLine("  Совпадений в доступных папках нет.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.AppendLine("  Часть или весь диск недоступны.");
            }
        }
        return output.Length == 0 ? "Подключённых локальных дисков не найдено." : output.Append("Недоступные папки и ссылки на другие папки пропущены.").ToString();

        bool Protected(string path) => protectedRoots.Any(root => PathHelper.IsInside(path, root));
    }

    static string ListApplications(CancellationToken ct)
    {
        var apps = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        bool registryComplete = true;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                try
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    foreach (var id in uninstall?.GetSubKeyNames() ?? [])
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            using var entry = uninstall!.OpenSubKey(id);
                            if (entry?.GetValue("SystemComponent") is 1 || entry?.GetValue("DisplayName") is not string name) continue;
                            Add(name, entry.GetValue("DisplayVersion") as string);
                        }
                        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException) { registryComplete = false; }
                    }
                }
                catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException) { registryComplete = false; }
        bool startComplete = ReadStartApps(name => Add(name, null), ct);
        var result = new StringBuilder($"Приложения Windows ({apps.Count}):\n");
        foreach (var (name, versions) in apps)
            result.AppendLine(name + (versions.Count > 0 ? " · " + string.Join(", ", versions) : ""));
        if (!registryComplete) result.AppendLine("Часть списка установленных приложений недоступна.");
        if (!startComplete) result.AppendLine("Не удалось прочитать полный список приложений Пуск.");
        return result.ToString().TrimEnd();

        void Add(string name, string? version)
        {
            name = name.Trim().ReplaceLineEndings(" ");
            if (name.Length == 0) return;
            if (!apps.TryGetValue(name, out var versions)) apps[name] = versions = new(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(version)) versions.Add(version.Trim().ReplaceLineEndings(" "));
        }
    }

    static bool ReadStartApps(Action<string> add, CancellationToken ct)
    {
        object? shell = null, folder = null, items = null;
        try
        {
            if (Type.GetTypeFromProgID("Shell.Application") is not { } type || (shell = Activator.CreateInstance(type)) == null) return false;
            folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
            if (folder == null) return false;
            items = ((dynamic)folder).Items();
            if (items == null) return false;
            int count = ((dynamic)items).Count;
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                object? item = ((dynamic)items).Item(i);
                try { if (item != null && ((dynamic)item).Name is string name) add(name); }
                finally { Release(item); }
            }
            return true;
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or UnauthorizedAccessException) { return false; }
        finally
        {
            Release(items);
            Release(folder);
            Release(shell);
        }
        static void Release(object? value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }
    }
}
