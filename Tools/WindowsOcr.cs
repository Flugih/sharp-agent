namespace SharpAgent.Tools;

static class WindowsOcr
{
    const string Runtime = """
        $ErrorActionPreference='Stop'
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $methods=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {$_.Name -eq 'AsTask' -and $_.GetParameters().Length -eq 1}
        $asTask=$methods | Where-Object {$_.IsGenericMethodDefinition -and $_.GetGenericArguments().Length -eq 1 -and
          $_.GetParameters()[0].ParameterType.Name.StartsWith('IAsyncOperation')} | Select-Object -First 1
        function Await($operation,$type) {
          $task=$asTask.MakeGenericMethod($type).Invoke($null,@($operation))
          $task.GetAwaiter().GetResult()
        }
        $decoderType=[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
        $bitmapType=[Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
        $format=[Windows.Graphics.Imaging.BitmapPixelFormat, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
        $alpha=[Windows.Graphics.Imaging.BitmapAlphaMode, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
        $engineType=[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
        $resultType=[Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
        """;

    internal static async Task<string> RecognizeAsync(string image, string? language, string? query, ToolContext context, CancellationToken ct)
    {
        if (language is not (null or "ru" or "en")) throw new ToolException("Для OCR укажите язык ru или en.");
        var prefix = "$image=" + Quote(image) + "\n$language=" + Quote(language ?? "") + "\n$query=" + Quote(query ?? "") + "\n";
        var result = await ShellTools.RunPowerShellAsync(prefix + Runtime + "\n" + """
            $file=[IO.File]::OpenRead($image)
            $stream=[IO.WindowsRuntimeStreamExtensions]::AsRandomAccessStream($file)
            $decoder=Await ($decoderType::CreateAsync($stream)) $decoderType
            $bitmap=Await ($decoder.GetSoftwareBitmapAsync($format::Bgra8,$alpha::Premultiplied)) $bitmapType
            $langPattern=if ($language) {'^'+$language+'(?:-|$)'} else {'^(ru|en)(?:-|$)'}
            $languages=@($engineType::AvailableRecognizerLanguages | Where-Object {$_.LanguageTag -match $langPattern} | Select-Object -First 3)
            if (-not $languages.Count) { throw 'Install a matching Windows OCR language in Windows language settings.' }
            $seen=@{}
            $count=0
            foreach($lang in $languages) {
              $engine=$engineType::TryCreateFromLanguage($lang)
              if (-not $engine) { continue }
              $recognized=Await ($engine.RecognizeAsync($bitmap)) $resultType
              foreach($line in $recognized.Lines) {
                if ($query -and $line.Text.IndexOf($query,[StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
                $word=$line.Words | Select-Object -First 1
                if (-not $word) { continue }
                $rect=$word.BoundingRect
                $key=[string][int]$rect.Y+'|'+$line.Text
                if ($seen.ContainsKey($key)) { continue }
                $seen[$key]=$true
                $text=$line.Text
                if ($text.Length -gt 180) {$text=$text.Substring(0,180)}
                Write-Output ('{0} x={1} y={2}: {3}' -f $lang.LanguageTag,[int]($rect.X+$rect.Width/2),[int]($rect.Y+$rect.Height/2),$text)
                $count++
                if ($count -ge 24) { break }
              }
              if ($count -ge 24) { break }
            }
            $bitmap.Dispose()
            $stream.Dispose()
            $file.Dispose()
            """, context, ct, 30, saveLog: false);
        if (ToolRegistry.CommandFailed(result)) throw new ToolException("Не удалось распознать текст через Windows OCR:\n" + AgentRunner.Clip(result, 1000));
        return "Распознанный текст и координаты первого слова каждой строки. Распознавание может ошибаться.\n" + result.Split('\n', 2).Last().Trim();
    }

    internal static async Task<string> ReadPdfAsync(string path, ToolContext context, CancellationToken ct)
    {
        var language = SharpAgent.Ui.UiLanguage.Current.Value.IsRussian ? "ru" : "en";
        var result = await ShellTools.RunPowerShellAsync("$path=" + Quote(path) + "\n$language=" + Quote(language) + "\n" + Runtime + "\n" + """
            $action=$methods | Where-Object {-not $_.IsGenericMethod -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'} | Select-Object -First 1
            $pdfType=[Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType=WindowsRuntime]
            $memoryType=[Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]
            $writerType=[Windows.Storage.Streams.DataWriter, Windows.Storage.Streams, ContentType=WindowsRuntime]
            $optionsType=[Windows.Data.Pdf.PdfPageRenderOptions, Windows.Data.Pdf, ContentType=WindowsRuntime]
            $lang=$engineType::AvailableRecognizerLanguages | Where-Object {$_.LanguageTag.StartsWith($language)} | Select-Object -First 1
            $engine=if($lang) {$engineType::TryCreateFromLanguage($lang)} else {$engineType::TryCreateFromUserProfileLanguages()}
            if (-not $engine) { throw 'No Windows OCR language is available.' }
            if ((Get-Item -LiteralPath $path).Length -gt 12000000) { throw 'PDF exceeds the OCR size limit.' }
            $source=$memoryType::new()
            $writer=$writerType::new($source)
            try {
              $writer.WriteBytes([IO.File]::ReadAllBytes($path))
              [void](Await ($writer.StoreAsync()) ([uint32]))
              [void]$writer.DetachStream()
            } finally {$writer.Dispose()}
            $source.Seek(0)
            $pdf=Await ($pdfType::LoadFromStreamAsync($source)) $pdfType
            $retained=@($source)
            for ($i=0;$i -lt [Math]::Min(8,$pdf.PageCount);$i++) {
              $page=$pdf.GetPage($i)
              $image=$memoryType::new()
              $retained+=@($page,$image)
              $opts=$optionsType::new()
              $size=$page.Size
              $scale=1600/[Math]::Max($size.Width,$size.Height)
              $opts.DestinationWidth=[Math]::Max(1,[int]($size.Width*$scale))
              $opts.DestinationHeight=[Math]::Max(1,[int]($size.Height*$scale))
              $task=$action.Invoke($null,@($page.RenderToStreamAsync($image,$opts)))
              [void]$task.GetAwaiter().GetResult()
              $image.Seek(0)
              $decoder=Await ($decoderType::CreateAsync($image)) $decoderType
              $bitmap=Await ($decoder.GetSoftwareBitmapAsync($format::Bgra8,$alpha::Premultiplied)) $bitmapType
              try {
                $recognized=Await ($engine.RecognizeAsync($bitmap)) $resultType
                [Console]::WriteLine(('=== PDF {0} ===' -f ($i+1)))
                $recognized.Lines | Select-Object -First 160 | ForEach-Object {[Console]::WriteLine($_.Text)}
              } finally {$bitmap.Dispose()}
            }
            # Keep the WinRT PDF stream alive through shutdown of the bounded worker.
            [GC]::KeepAlive($retained)
            [Console]::Out.Flush()
            [Environment]::Exit(0)
            """, context, ct, 55, saveLog: false);
        if (ToolRegistry.CommandFailed(result)) throw new ToolException(Tr("Windows OCR для PDF недоступен.\n", "Windows PDF OCR is unavailable.\n") +
            AgentRunner.Clip(result, 1_200));
        return result.Contains("=== PDF ", StringComparison.Ordinal) ? result[result.IndexOf("=== PDF ", StringComparison.Ordinal)..].Trim() : "";
    }

    static string Quote(string value) => "'" + Regex.Replace(value, "['\u2018\u2019\u201A\u201B]", "$0$0") + "'";
}
