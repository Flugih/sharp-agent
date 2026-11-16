param(
    [Parameter(Mandatory = $true)][ValidateSet('convert','audio','transcribe')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [ValidatePattern('^\d+(\.\d+)?$')][string]$Start = '0',
    [ValidatePattern('^\d+(\.\d+)?$')][string]$Duration = '0',
    [ValidatePattern('^(auto|[a-z]{2,3})$')][string]$Language = 'auto',
    [string]$Overwrite = 'no'
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'shared.ps1')
$targetPath = Get-PluginTarget $Target $Overwrite
$sourceItem = Get-Item -LiteralPath $Source
$audio = '^\.(wav|mp3|m4a|flac|ogg|opus|aac)$'
$media = $audio + '|^\.(mp4|mkv|webm|mov|avi|m4v|mpg|mpeg|ts)$'
if ($sourceItem.PSIsContainer -or $sourceItem.Extension -notmatch $media) { throw 'Choose a local audio or video file.' }
$extension = [IO.Path]::GetExtension($targetPath)
if (($Mode -eq 'transcribe' -and $extension -ne '.txt') -or ($Mode -eq 'audio' -and $extension -notmatch $audio) -or
    ($Mode -eq 'convert' -and $extension -notmatch $media)) {
    throw 'The output extension does not match the selected operation.'
}
$ffmpeg = Get-PluginProgram 'ffmpeg'
$stage = New-PluginStage $targetPath
$scratch = $null
$invariant = [Globalization.CultureInfo]::InvariantCulture
$options = @('-nostdin', '-hide_banner', '-loglevel', 'error', '-n', '-protocol_whitelist', 'file,pipe',
    '-format_whitelist', 'mov,mp4,m4a,3gp,3g2,mj2,matroska,webm,avi,mpeg,mpegts,wav,mp3,aac,flac,ogg')
if ([double]::Parse($Start, $invariant) -gt 0) { $options += '-ss', $Start }
$options += '-i', $sourceItem.FullName
if ([double]::Parse($Duration, $invariant) -gt 0) { $options += '-t', $Duration }
if ($Mode -ne 'convert' -or $extension -match $audio) { $options += '-map', '0:a:0', '-vn' }
try {
    if ($Mode -eq 'transcribe') {
        $whisper = Get-PluginProgram 'whisper-cli'
        $model = Join-Path $PSScriptRoot 'ggml-model.bin'
        if (-not [IO.File]::Exists($model)) { throw 'Put a multilingual whisper.cpp model at media/ggml-model.bin in the plugin folder.' }
        $scratch = New-PluginScratch
        $wav = Join-Path $scratch 'speech.wav'
        & $ffmpeg @options -ac 1 -ar 16000 -c:a pcm_s16le $wav
        if ($LASTEXITCODE -ne 0) { throw 'FFmpeg could not decode the audio track.' }
        & $whisper -m $model -f $wav -l $Language -nt -otxt -of ([IO.Path]::ChangeExtension($stage, [NullString]::Value))
        if ($LASTEXITCODE -ne 0) { throw 'Speech recognition failed. Check the model and try a shorter excerpt.' }
    } else {
        & $ffmpeg @options $stage
        if ($LASTEXITCODE -ne 0) { throw 'FFmpeg could not process the file. Check the format and time range.' }
    }
    Publish-PluginFile $stage $targetPath $Overwrite
} finally {
    if ([IO.File]::Exists($stage)) { [IO.File]::Delete($stage) }
    if ($scratch) { [IO.Directory]::Delete($scratch, $true) }
}
