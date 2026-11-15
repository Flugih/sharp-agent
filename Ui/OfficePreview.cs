using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpAgent.Core;

namespace SharpAgent.Ui;

internal sealed record OfficePages(string Name, string[] Files);

internal sealed class OfficePreview(string directory, IReadOnlyList<OfficePages> groups) : IDisposable
{
    private static string Folder => Path.Combine(AppPaths.TempFolder, "office-preview");

    public IReadOnlyList<OfficePages> Groups => groups;

    public static bool IsOfficeFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".doc" or ".docx" or ".docm" or ".dotx" or ".rtf" or ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or
        ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx";

    public static async Task<OfficePreview> OpenAsync(string source, CancellationToken ct)
    {
        var directory = Path.Combine(Folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var processPath = Environment.ProcessPath ?? throw new IOException("Не удалось запустить предпросмотр.");
            var start = new ProcessStartInfo(processPath) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(OfficePreview).Assembly.GetName().Name + ".dll"));
            start.ArgumentList.Add("--office-preview");
            start.ArgumentList.Add(Path.GetFullPath(source));
            start.ArgumentList.Add(directory);
            using var child = Process.Start(start) ?? throw new IOException("Не удалось запустить предпросмотр.");
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!child.HasExited)
                    try { child.Kill(); } catch (InvalidOperationException) { }
                if (ct.IsCancellationRequested) throw;
                throw new IOException("Предпросмотр занял слишком много времени. Откройте файл во внешней программе.");
            }

            if (child.ExitCode != 0) throw new IOException(child.ExitCode switch
            {
                2 => "Для предпросмотра установите соответствующее приложение Microsoft Office или откройте файл во внешней программе.",
                3 => "Для этого формата визуальный предпросмотр недоступен. Откройте файл во внешней программе.",
                4 => "Windows не разрешила запустить Office для предпросмотра. Откройте файл во внешней программе.",
                _ => "Не удалось отобразить документ. Откройте его во внешней программе.",
            });

            var manifest = await File.ReadAllTextAsync(Path.Combine(directory, "pages.json"), ct);
            var pages = JsonSerializer.Deserialize<OfficePages[]>(manifest) ?? [];
            if (pages.Length == 0) throw new IOException("В документе нет страниц для показа.");
            var full = pages.Select(group => group with
            {
                Files = group.Files.Select(file => Path.Combine(directory, Path.GetFileName(file))).ToArray(),
            }).ToArray();
            return new OfficePreview(directory, full);
        }
        catch
        {
            Clear(directory);
            throw;
        }
    }

    public static int Export(string source, string directory)
    {
        try
        {
            var target = Path.GetFullPath(directory);
            if (!Guid.TryParseExact(Path.GetFileName(target), "N", out _) ||
                !string.Equals(Path.GetDirectoryName(target), Folder, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(target) || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                return 1;
            var kind = Path.GetExtension(source).ToLowerInvariant();
            var (program, process) = kind switch
            {
                ".docx" => ("Word.Application", "WINWORD"),
                ".xlsx" => ("Excel.Application", "EXCEL"),
                ".pptx" or ".ppsx" => ("PowerPoint.Application", "POWERPNT"),
                _ => ("", ""),
            };
            if (program.Length == 0 || !IsDocumentPackage(source)) return 3;
            if (Type.GetTypeFromProgID(program) is not { } type) return 2;
            var running = Process.GetProcessesByName(process);
            bool startedForPreview = running.Length == 0;
            foreach (var item in running) item.Dispose();
            object instance;
            try { instance = Activator.CreateInstance(type)!; }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80040154)) { return 2; }
            catch (UnauthorizedAccessException) { return 4; }
            try
            {
                var pages = kind switch
                {
                    ".docx" => ExportWord(instance, source, target),
                    ".xlsx" => ExportExcel(instance, source, target),
                    _ => ExportSlides(instance, source, target),
                };
                File.WriteAllText(Path.Combine(target, "pages.json"), JsonSerializer.Serialize(pages));
                return 0;
            }
            finally
            {
                try
                {
                    dynamic app = instance;
                    if (startedForPreview && (kind switch
                        {
                            ".docx" => app.Documents.Count == 0,
                            ".xlsx" => app.Workbooks.Count == 0,
                            _ => app.Presentations.Count == 0,
                        })) app.Quit();
                }
                catch { }
                Marshal.FinalReleaseComObject(instance);
            }
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static bool IsDocumentPackage(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return zip.GetEntry("[Content_Types].xml") != null && !zip.Entries.Any(entry =>
            entry.FullName.EndsWith("/vbaProject.bin", StringComparison.OrdinalIgnoreCase) ||
            entry.FullName.StartsWith("xl/macrosheets/", StringComparison.OrdinalIgnoreCase));
    }

    private static OfficePages[] ExportWord(dynamic app, string source, string target)
    {
        dynamic? document = null;
        int security = app.AutomationSecurity;
        int alerts = app.DisplayAlerts;
        bool update = app.Options.UpdateLinksAtOpen;
        try
        {
            app.AutomationSecurity = 3;
            app.DisplayAlerts = 0;
            app.Options.UpdateLinksAtOpen = false;
            document = app.Documents.Open(FileName: source, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                PasswordDocument: "", PasswordTemplate: "", Visible: false, NoEncodingDialog: true);
            document.ExportAsFixedFormat(Path.Combine(target, "document.xps"), 18);
            return [new OfficePages("", ["document.xps"])];
        }
        finally
        {
            try { if (document != null) document.Close(0); }
            finally
            {
                app.Options.UpdateLinksAtOpen = update;
                app.DisplayAlerts = alerts;
                app.AutomationSecurity = security;
            }
        }
    }

    private static OfficePages[] ExportExcel(dynamic app, string source, string target)
    {
        dynamic? book = null;
        int security = app.AutomationSecurity;
        bool alerts = app.DisplayAlerts;
        try
        {
            app.AutomationSecurity = 3;
            app.DisplayAlerts = false;
            book = app.Workbooks.Open(Filename: source, UpdateLinks: 0, ReadOnly: true, Password: "", WriteResPassword: "",
                IgnoreReadOnlyRecommended: true, AddToMru: false);
            var groups = new List<OfficePages>();
            dynamic sheets = book.Worksheets;
            for (int i = 1; i <= sheets.Count; i++)
            {
                dynamic sheet = sheets.Item[i];
                if (sheet.Visible != -1) continue;
                var file = $"sheet-{i}.xps";
                sheet.ExportAsFixedFormat(Type: 1, Filename: Path.Combine(target, file), IgnorePrintAreas: true);
                if (File.Exists(Path.Combine(target, file))) groups.Add(new OfficePages((string)sheet.Name, [file]));
            }
            return [.. groups];
        }
        finally
        {
            try { if (book != null) book.Close(false); }
            finally
            {
                app.DisplayAlerts = alerts;
                app.AutomationSecurity = security;
            }
        }
    }

    private static OfficePages[] ExportSlides(dynamic app, string source, string target)
    {
        dynamic? presentation = null;
        int security = app.AutomationSecurity;
        try
        {
            app.AutomationSecurity = 3;
            presentation = app.Presentations.Open(source, -1, 0, 0);
            dynamic slides = presentation.Slides;
            var files = new List<string>();
            for (int i = 1; i <= slides.Count; i++)
            {
                var file = $"slide-{i}.png";
                slides.Item(i).Export(Path.Combine(target, file), "PNG", 1600);
                files.Add(file);
            }
            return [new OfficePages("", [.. files])];
        }
        finally
        {
            try { if (presentation != null) presentation.Close(); }
            finally { app.AutomationSecurity = security; }
        }
    }

    public void Dispose() => Clear(directory);

    private static void Clear(string path)
    {
        try { Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
