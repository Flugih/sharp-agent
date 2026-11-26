using System.IO.Compression;

namespace SharpAgent.Core;

static class VoicePackage
{
    const string Repository = "https://github.com/k2-fsa/sherpa-onnx/releases/download/";
    const string PyPi = "https://files.pythonhosted.org/packages/";
    const string MultilingualSource = "https://huggingface.co/i2z1/gigaam-multilingual-ctc-onnx-int8/resolve/ba9011bfb2e52cacad5a12e5d9346392d7825c76/";
    internal const string Whisper = "sherpa-onnx-whisper-base", Vad = "silero_vad.onnx",
        GigaAm = "sherpa-onnx-nemo-transducer-punct-giga-am-v3-russian-2025-12-16",
        GigaAmMultilingual = "gigaam-multilingual-ctc-onnx-int8", MultilingualModel = "model.int8.onnx",
        Denis = "vits-piper-ru_RU-denis-medium", Lessac = "vits-piper-en_US-lessac-medium", Ryan = "vits-piper-en_US-ryan-medium-int8",
        Runtime = "python-silero", Silero = "silero", SileroModel = "v5_6_ru.pt";
    const string PythonPaths = "python313._pth";
    static readonly string[] WhisperFiles = ["base-encoder.int8.onnx", "base-decoder.int8.onnx", "base-tokens.txt"];
    static readonly string[] GigaAmFiles = ["encoder.int8.onnx", "decoder.onnx", "joiner.onnx", "tokens.txt"];
    sealed record Asset(string Url, long Size, string Hash)
    {
        internal string Name => Path.GetFileName(Uri.UnescapeDataString(new Uri(Url).AbsolutePath));
    }

    sealed record Component(string Name, string[] Files, Asset[] Assets)
    {
        internal bool Installed(string root) => Files.All(file => Path.Exists(Path.Combine(root, Name, file)));
    }

    static Component Model(string name, string tag, long size, string hash, string[] files) =>
        new(name, files, [new(Repository + tag + "/" + name + ".tar.bz2", size, hash)]);

    static Component Piper(string name, long size, string hash) =>
        Model(name, "tts-models", size, hash, [VoiceModel(name), "tokens.txt", "espeak-ng-data"]);

    static readonly Component Multilingual = new(GigaAmMultilingual, [MultilingualModel, "tokens.txt"],
    [
        new(MultilingualSource + MultilingualModel, 224762512, "5f584553e20db1af7e0fff98728b6904b670eb6488b88da8e87cda7870a6235f"),
        new(MultilingualSource + "tokens.txt", 391, "9b5df7987cb4ca52c1a468649ce897fab1cd182067416e29fef49dfaa7a856c2"),
    ]);

    static readonly Component[] Components =
    [
        new("", [Vad], [new(Repository + "asr-models/" + Vad, 643854, "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6")]),
        Model(Whisper, "asr-models", 207557382, "911b2083efd7c0dca2ac3b358b75222660dc09fb716d64fbfc417ba6c99ff3de", WhisperFiles),
        Piper(Denis, 67190991, "efa4c18e0b5e32b81d1b6df36b9d312831e5d545200e27848ef926a4cd930300"),
        Piper(Lessac, 67230653, "9e3febfacf0abf4270172d2958bcec246032b7e88efc2720840cc80c93de334e"),
        Model(GigaAm, "asr-models", 170197019, "f9620a0099019c6afcee26525ef9ed3297fa50dd5691c1902af0c948fc1a470b", GigaAmFiles),
        Piper(Ryan, 21083446, "376eb489d42e98cb49f0e13a633e8d88580bd247ed8aacc298a733210404f771"),
        new(Runtime, ["python.exe", PythonPaths, "packages/torch/lib/torch_cpu.dll"],
        [
            new("https://www.python.org/ftp/python/3.13.13/python-3.13.13-embed-amd64.zip", 10950201, "8766a8775746235e23cf5aee5027ab1060bb981d93110577adcf3508aa0cbd55"),
            new("https://download-r2.pytorch.org/whl/cpu/torch-2.10.0%2Bcpu-cp313-cp313-win_amd64.whl", 113670411, "b719da5af01b59126ac13eefd6ba3dd12d002dc0e8e79b8b365e55267a8189d3"),
            new(PyPi + "b5/36/7fb70f04bf00bc646cd5bb45aa9eddb15e19437a28b8fb2b4a5249fac770/filelock-3.20.3-py3-none-any.whl", 16701, "4b0dda527ee31078689fc205ec4f1c1bf7d56cf88b6dc9426c4f230e46c2dce1"),
            new(PyPi + "e6/ab/fb21f4c939bb440104cc2b396d3be1d9b7a9fd3c6c2a53d98c45b3d7c954/fsspec-2026.2.0-py3-none-any.whl", 202505, "98de475b5cb3bd66bedd5c4679e87b4fdfe1a3bf4d707b151b3c07e58c9a2437"),
            new(PyPi + "62/a1/3d680cbfd5f4b8f15abc1d571870c5fc3e594bb582bc3b64ea099db13e56/jinja2-3.1.6-py3-none-any.whl", 134899, "85ece4451f492d0c13c5dd7c13a64681a86afae63a5f347908daf103ce6d2f67"),
            new(PyPi + "05/73/c4abe620b841b6b791f2edc248f556900667a5a1cf023a6646967ae98335/markupsafe-3.0.3-cp313-cp313-win_amd64.whl", 15113, "9a1abfdc021a164803f4d485104931fb8f8c1efd55bc6b748d2f5774e78b62c5"),
            new(PyPi + "43/e3/7d92a15f894aa0c9c4b49b8ee9ac9850d6e63b03c9c32c0367a13ae62209/mpmath-1.3.0-py3-none-any.whl", 536198, "a0b2b9fe80bbcd81a6647ff13108738cfb482d481d826cc0e02f5b35e5c88d2c"),
            new(PyPi + "9e/c9/b2622292ea83fbb4ec318f5b9ab867d0a28ab43c5717bb85b0a5f6b3b0a4/networkx-3.6.1-py3-none-any.whl", 2068504, "d47fbf302e7d9cbbb9e2555a0d267983d2aa476bac30e90dfbe5669bd57f3762"),
            new(PyPi + "94/b8/f1f62a5e3c0ad2ff1d189590bfa4c46b4f3b6e49cef6f26c6ee4e575394d/setuptools-80.10.2-py3-none-any.whl", 1064234, "95b30ddfb717250edb492926c92b5221f7ef3fbcc2b07579bcd4a27da21d0173"),
            new(PyPi + "a2/09/77d55d46fd61b4a135c444fc97158ef34a095e5681d0a6c10b75bf356191/sympy-1.14.0-py3-none-any.whl", 6299353, "e091cc3e99d2141a0ba2847328f5479b05d94a6635cb96148ccb3f34671bd8f5"),
            new(PyPi + "18/67/36e9267722cc04a6b9f15c7f3441c2363321a3ea07da7ae0c0707beb2a9c/typing_extensions-4.15.0-py3-none-any.whl", 44614, "f0fa19c6845758ab08074a0cfa8b7aecb71c999ca73d62883bc25cc018c4e548"),
        ]),
        new(Silero, [SileroModel], [new("https://models.silero.ai/models/tts/ru/" + SileroModel, 147664410, "04cc9b1d08ce38bcaa9d02c488d80d3e6806d17f7eaa7c95982f36a3cbf50b2e")]),
    ];

    internal static string Root => Path.Combine(AppPaths.AppFolder, "models", "voice", "v1");
    internal static string VoiceModel(string voice) =>
        voice["vits-piper-".Length..].Replace("-int8", "", StringComparison.Ordinal) + ".onnx";
    internal static string RecognizerFolder(VoiceRecognitionMode mode) => mode switch
    {
        VoiceRecognitionMode.Russian => GigaAm,
        VoiceRecognitionMode.Auto => GigaAmMultilingual,
        _ => Whisper,
    };
    internal static bool DetectionReady => Installed("");
    internal static bool RecognitionReady(VoiceRecognitionMode mode) =>
        mode != VoiceRecognitionMode.Groq && DetectionReady && Installed(RecognizerFolder(mode));
    internal static bool SpeechReady => Installed(Denis) && Installed(Lessac);
    internal static bool Ready(VoiceRecognitionMode mode) => !Missing(mode).Any();
    internal static bool SileroReady => Installed(Runtime) && Installed(Silero);
    internal static bool RyanReady => Installed(Ryan);
    internal static int DownloadMb(VoiceRecognitionMode mode) => (int)Math.Ceiling(Missing(mode)
        .Sum(component => component.Assets.Sum(asset => asset.Size)) / 1_000_000d);
    static bool Installed(string name) => (name == GigaAmMultilingual ? Multilingual : Components.Single(component => component.Name == name)).Installed(Root);

    static IEnumerable<Component> Missing(VoiceRecognitionMode mode) => (mode switch
    {
        VoiceRecognitionMode.Auto => Components.Where(component => component.Name is not (Whisper or GigaAm)).Append(Multilingual),
        VoiceRecognitionMode.Groq => Components.Where(component => component.Name is not (Whisper or GigaAm)),
        _ => Components,
    }).Where(component => !component.Installed(Root));

    internal static async Task InstallAsync(VoiceRecognitionMode mode, IProgress<(int Percent, int Attempt)> progress, CancellationToken ct)
    {
        var downloads = Root + ".downloads";
        Directory.CreateDirectory(downloads);
        using var installLock = new FileStream(downloads + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
        var pending = Missing(mode).ToArray();
        using var http = AppNetwork.Client(cookies: false);
        long total = pending.Sum(component => component.Assets.Sum(asset => asset.Size)), received = 0;
        (int Percent, int Attempt) shown = (-1, 0);
        foreach (var component in pending)
        {
            foreach (var asset in component.Assets)
            {
                await ResumableDownload.FetchAsync(http, asset.Url, Path.Combine(downloads, asset.Name),
                    asset.Size, asset.Hash, (count, attempt) =>
                    {
                        var next = ((int)((received + count) * 100 / total), attempt);
                        if (next != shown) progress.Report(shown = next);
                    }, ct, supportsRanges: component.Name != Silero).ConfigureAwait(false);
                received += asset.Size;
            }
            await InstallComponentAsync(component, downloads, ct).ConfigureAwait(false);
            foreach (var asset in component.Assets) File.Delete(Path.Combine(downloads, asset.Name));
        }
    }

    static async Task InstallComponentAsync(Component component, string downloads, CancellationToken ct)
    {
        var stage = Path.Combine(downloads, Guid.NewGuid().ToString("N"));
        var target = Path.Combine(stage, component.Name);
        Directory.CreateDirectory(target);
        try
        {
            foreach (var asset in component.Assets)
            {
                ct.ThrowIfCancellationRequested();
                var archive = Path.Combine(downloads, asset.Name);
                if (asset.Name.EndsWith(".tar.bz2", StringComparison.Ordinal)) await ExtractAsync(component, stage, archive, ct).ConfigureAwait(false);
                else if (asset.Name.EndsWith(".zip", StringComparison.Ordinal)) ZipFile.ExtractToDirectory(archive, target);
                else if (asset.Name.EndsWith(".whl", StringComparison.Ordinal)) ZipFile.ExtractToDirectory(archive, Path.Combine(target, "packages"));
                else File.Copy(archive, Path.Combine(target, asset.Name));
            }
            if (component.Name == Runtime)
            {
                await File.AppendAllTextAsync(Path.Combine(target, PythonPaths), "\r\npackages\r\n", ct).ConfigureAwait(false);
                await SileroSpeech.ValidateRuntimeAsync(Path.Combine(target, "python.exe"), ct).ConfigureAwait(false);
            }
            if (component.Name == Silero)
            {
                try
                {
                    using var speech = new SileroSpeech(Path.Combine(target, SileroModel), verifyArchive: true);
                    if ((await speech.GenerateAsync("Привет!", "xenia", 1, ct).ConfigureAwait(false)).Length == 0)
                        throw new InvalidDataException("Silero не вернул аудио при проверке модели.");
                }
                catch (InvalidDataException)
                {
                    File.Delete(Path.Combine(downloads, SileroModel));
                    throw;
                }
            }
            ct.ThrowIfCancellationRequested();
            if (!component.Installed(stage)) throw new IOException("В голосовом пакете отсутствуют необходимые файлы.");
            Directory.CreateDirectory(Root);
            if (component.Name.Length == 0) File.Move(Path.Combine(stage, Vad), Path.Combine(Root, Vad), overwrite: true);
            else
            {
                var installed = Path.Combine(Root, component.Name);
                if (Directory.Exists(installed)) Directory.Move(installed, Path.Combine(stage, ".old"));
                try { Directory.Move(target, installed); }
                catch
                {
                    var old = Path.Combine(stage, ".old");
                    if (Directory.Exists(old) && !Directory.Exists(installed)) Directory.Move(old, installed);
                    throw;
                }
            }
        }
        finally { Directory.Delete(stage, recursive: true); }
    }

    static async Task ExtractAsync(Component component, string folder, string archive, CancellationToken ct)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"))
        {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            ArgumentList = { "-xf", archive },
        };
        if (component.Name is Whisper or GigaAm)
            foreach (var file in component.Files) start.ArgumentList.Add(component.Name + "/" + file);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить распаковку.");
        try
        {
            var errors = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Не удалось распаковать голосовой пакет. " + await errors.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
    }
}
