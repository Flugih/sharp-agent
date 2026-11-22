namespace SharpAgent.Tools;

static class WindowsOcr
{
    internal static async Task<string> RecognizeAsync(string image, string? language, string? query, ToolContext context, CancellationToken ct)
    {
        if (language is not (null or "ru" or "en")) throw new ToolException("Для OCR укажите язык ru или en.");
        var prefix = "$image=" + Quote(image) + "\n$language=" + Quote(language ?? "") + "\n$query=" + Quote(query ?? "") + "\n";
        var result = await ShellTools.RunPowerShellAsync(prefix + """
            $ErrorActionPreference='Stop'
            Add-Type -AssemblyName System.Runtime.WindowsRuntime
            $asTask=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
              $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetGenericArguments().Length -eq 1 -and
              $_.GetParameters().Length -eq 1 -and $_.GetParameters()[0].ParameterType.Name.StartsWith('IAsyncOperation')
            } | Select-Object -First 1
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

    static string Quote(string value) => "'" + Regex.Replace(value, "['\u2018\u2019\u201A\u201B]", "$0$0") + "'";
}
