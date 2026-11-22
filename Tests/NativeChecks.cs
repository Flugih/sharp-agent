using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SharpAgent.Agent;
using SharpAgent.Core;
using SharpAgent.Tools;
using SharpAgent.Ui;

static class NativeChecks
{
    internal static Task RunAsync(string root)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var app = new SharpAgent.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.InitializeComponent();
                    WindowsControls(root);
                    ScreenBaseline(root);
                    await OcrAsync(root);
                    if (Environment.GetEnvironmentVariable("SHARP_AGENT_TEST_BROWSER") == "1") await BrowserAsync(root);
                    done.SetResult();
                }
                catch (Exception ex) { done.SetException(ex); }
                finally { dispatcher.InvokeShutdown(); }
            });
            Dispatcher.Run();
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task.WaitAsync(TimeSpan.FromSeconds(70));
    }

    static void WindowsControls(string root)
    {
        var view = (Grid)SharpAgent.MainWindow.DiffView("Example.cs",
            new FileDiff("@@ -40,2 +40,2 @@\n-old one\n-old two\n+new one\n+new two", 2, 2), 20);
        var side = (ScrollViewer)view.Children[2];
        var text = ((DockPanel)side.Content).Children.OfType<RichTextBox>().Single();
        text.Selection.Select(text.Document.ContentStart, text.Document.ContentEnd);
        if (!text.Selection.Text.Contains("new one") || !text.Selection.Text.Contains("new two") ||
            text.Selection.Text.Contains("old one") || text.Selection.Text.Contains("40"))
            throw new InvalidOperationException("Diff selection must span code lines and exclude the gutter.");
        var compact = new CompactChatWindow((_, _) => new StackPanel());
        var attach = (Button)compact.FindName("CompactAttachButton");
        bool picked = false;
        compact.ChooseFiles += () => picked = true;
        attach.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var file = new Attachment(Path.Combine(root, "fixture.txt"), AttachmentKind.File, 0);
        var panel = (WrapPanel)compact.FindName("Attachments");
        compact.ShowAttachments([file], _ => compact.ShowAttachments([], _ => { }));
        if (!picked || panel.Children.Count != 1) throw new InvalidOperationException("Compact attachment button and list.");
        var chip = (Border)panel.Children[0];
        ((StackPanel)chip.Child).Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (panel.Children.Count != 0) throw new InvalidOperationException("Compact attachment removal.");
        compact.Close();
        Console.WriteLine("WPF diff selection and compact attachment controls passed.");
    }

    static void ScreenBaseline(string root)
    {
        var context = new ToolContext { WorkingDirectory = root, AppFolder = root };
        byte[] before = [0, 0, 0, 255, 0, 0, 0, 255];
        byte[] after = [0, 0, 200, 255, 0, 0, 200, 255];
        static BitmapSource Frame(byte[] pixels) => BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        ScreenStateCache.Baseline(context, Frame(before), "stable", true, "0,0,1,1");
        var diff = ScreenStateCache.Baseline(context, Frame(after), "stable", false, null);
        if (!diff.Contains("пикселей: 1") || !diff.Contains("x=1")) throw new InvalidOperationException("Saved baseline animation mask: " + diff);
        var screen = new ScreenStateCache();
        screen.TreeChanges((IntPtr)1, 1, null, ["Save", "Cancel"]);
        if (!screen.TreeChanges((IntPtr)1, 1, null, ["Save"]).Contains("Cancel") ||
            screen.TreeChanges((IntPtr)2, 1, null, []).Length > 0) throw new InvalidOperationException("Disappeared UIA control in the same window.");
    }

    static async Task OcrAsync(string root)
    {
        var surface = new DrawingVisual();
        using (var draw = surface.RenderOpen())
        {
            draw.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 120));
            draw.DrawText(new FormattedText("SHARP AGENT TEST", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"),
                42, Brushes.Black, 1), new Point(24, 24));
        }
        var bitmap = new RenderTargetBitmap(640, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var target = Path.Combine(root, "ocr-fixture.png");
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(target)) png.Save(stream);
        var context = new ToolContext { AppFolder = root, WorkingDirectory = root };
        var result = await WindowsOcr.RecognizeAsync(target, "en", "AGENT", context, default);
        if (!result.Contains("SHARP AGENT", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Windows OCR: " + result);
        var scan = Path.Combine(root, "ocr-fixture.pdf");
        using var image = new MemoryStream();
        var jpeg = new JpegBitmapEncoder();
        jpeg.Frames.Add(BitmapFrame.Create(bitmap));
        jpeg.Save(image);
        using (var pdf = File.Create(scan))
        {
            var offsets = new List<long> { 0 };
            void Write(string text) => pdf.Write(Encoding.ASCII.GetBytes(text));
            void Object(int id, string text) { offsets.Add(pdf.Position); Write($"{id} 0 obj\n{text}\nendobj\n"); }
            Write("%PDF-1.4\n");
            Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
            Object(2, "<< /Type /Pages /Count 1 /Kids [3 0 R] >>");
            Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 640 120] /Contents 4 0 R /Resources << /XObject << /Im0 5 0 R >> >> >>");
            const string content = "q 640 0 0 120 0 0 cm /Im0 Do Q\n";
            Object(4, $"<< /Length {content.Length} >>\nstream\n{content}endstream");
            offsets.Add(pdf.Position);
            Write($"5 0 obj\n<< /Type /XObject /Subtype /Image /Width 640 /Height 120 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {image.Length} >>\nstream\n");
            image.WriteTo(pdf);
            Write("\nendstream\nendobj\n");
            var xref = pdf.Position;
            Write("xref\n0 6\n0000000000 65535 f \n");
            foreach (var offset in offsets.Skip(1)) Write($"{offset:D10} 00000 n \n");
            Write($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }
        var recognizedPdf = await WindowsOcr.ReadPdfAsync(scan, context, default);
        if (!recognizedPdf.Contains("SHARP AGENT", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("PDF renderer OCR: " + recognizedPdf);
        var scanText = await new ToolRegistry(context).ExecuteAsync(LocalResearchEngine.Name,
            new System.Text.Json.Nodes.JsonObject { ["action"] = "inspect", ["path"] = scan }.ToJsonString(), null, default);
        if (scanText.IsError || !scanText.Output.Contains("SHARP AGENT", StringComparison.OrdinalIgnoreCase) || !scanText.Output.Contains("partial"))
            throw new InvalidOperationException("Windows PDF OCR: " + scanText.Output);
        var constrained = new ToolContext { AppFolder = root, WorkingDirectory = root, SandboxRoot = root };
        var withoutOcr = await new ToolRegistry(constrained).ExecuteAsync(LocalResearchEngine.Name,
            new System.Text.Json.Nodes.JsonObject { ["action"] = "inspect", ["path"] = scan }.ToJsonString(), null, default);
        if (withoutOcr.IsError || !withoutOcr.Output.Contains("partial") || withoutOcr.Output.Contains("SHARP AGENT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Scanned PDF without OCR should remain partial.");
        Console.WriteLine("Windows OCR passed.");
    }

    static async Task BrowserAsync(string root)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(22));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var serve = Task.Run(async () =>
        {
            while (!deadline.IsCancellationRequested)
            {
                using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
                using var stream = connection.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (await reader.ReadLineAsync(deadline.Token) is { Length: > 0 }) { }
                const string response = "<title>WebView fixture</title><main><h1 id=\"pass\">Browser DOM ready</h1></main>";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n{response}"), deadline.Token);
            }
        }, deadline.Token);
        using var browser = new BrowserView(uri, NetworkMode.Direct, null, root, Path.Combine(root, "browser-fixture"));
        var window = new Window { Width = 340, Height = 240, ShowInTaskbar = false, ShowActivated = false, Content = browser, Title = "Sharp Agent browser test" };
        var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.Failed += error => failure.TrySetResult(error);
        window.Show();
        try
        {
            while (true)
            {
                if (failure.Task.IsCompleted) throw new InvalidOperationException(await failure.Task);
                if (await browser.ReadDocumentAsync("#pass", "Browser DOM ready", deadline.Token) is { Matches: true, Title: "WebView fixture" }) break;
                await Task.Delay(200, deadline.Token);
            }
            Console.WriteLine("WebView2 DOM passed.");
        }
        finally
        {
            window.Close();
            deadline.Cancel();
            try { await serve; } catch (OperationCanceledException) { }
        }
    }
}
