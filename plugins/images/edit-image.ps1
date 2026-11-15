param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [ValidateSet('png','jpg','bmp','gif','tif')][string]$Format = 'png',
    [ValidateRange(0, 16384)][int]$Width = 0,
    [ValidateRange(0, 16384)][int]$Height = 0,
    [ValidateRange(0, 100000)][int]$X = 0,
    [ValidateRange(0, 100000)][int]$Y = 0,
    [ValidateRange(0, 100000)][int]$CropWidth = 0,
    [ValidateRange(0, 100000)][int]$CropHeight = 0,
    [ValidateSet(0,90,180,270)][int]$Rotate = 0,
    [string]$Overwrite = 'no'
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'shared.ps1')
Add-Type -AssemblyName System.Drawing, System.IO.Compression, System.IO.Compression.FileSystem

function Get-ImageFormat([string]$Extension) {
    switch ($Extension) {
        'png' { [Drawing.Imaging.ImageFormat]::Png }
        { $_ -in 'jpg','jpeg' } { [Drawing.Imaging.ImageFormat]::Jpeg }
        'bmp' { [Drawing.Imaging.ImageFormat]::Bmp }
        'gif' { [Drawing.Imaging.ImageFormat]::Gif }
        { $_ -in 'tif','tiff' } { [Drawing.Imaging.ImageFormat]::Tiff }
        default { throw 'Choose PNG, JPEG, BMP, GIF or TIFF.' }
    }
}

function Write-Image([string]$Path, [IO.Stream]$Output, [Drawing.Imaging.ImageFormat]$Encoder) {
    if (($X -gt 0 -or $Y -gt 0) -and ($CropWidth -eq 0 -or $CropHeight -eq 0)) {
        throw 'Specify CropWidth and CropHeight with X or Y.'
    }
    $original = [Drawing.Image]::FromFile($Path)
    try {
        if ($Rotate -ne 0 -and $CropWidth -eq 0 -and $CropHeight -eq 0) {
            $original.RotateFlip([Drawing.RotateFlipType]("Rotate${Rotate}FlipNone"))
        }
        $w = $original.Width
        $h = $original.Height
        if ($CropWidth -gt 0 -or $CropHeight -gt 0) {
            if ($CropWidth -le 0 -or $CropHeight -le 0 -or $X + $CropWidth -gt $w -or $Y + $CropHeight -gt $h) {
                throw 'Crop rectangle must be inside the source image.'
            }
            $w = $CropWidth
            $h = $CropHeight
        }
        $section = New-Object Drawing.Bitmap $w,$h
        try {
            $drawing = [Drawing.Graphics]::FromImage($section)
            try {
                $drawing.DrawImage($original, (New-Object Drawing.Rectangle 0,0,$w,$h), $X, $Y, $w, $h, [Drawing.GraphicsUnit]::Pixel)
            } finally { $drawing.Dispose() }
            if ($Rotate -ne 0 -and $CropWidth -gt 0) {
                $section.RotateFlip([Drawing.RotateFlipType]("Rotate${Rotate}FlipNone"))
            }
            $factor = 1.0
            if ($Width -gt 0 -and $Height -gt 0) { $factor = [Math]::Min($Width / $section.Width, $Height / $section.Height) }
            elseif ($Width -gt 0) { $factor = $Width / $section.Width }
            elseif ($Height -gt 0) { $factor = $Height / $section.Height }
            $w = [Math]::Max(1, [int][Math]::Round($section.Width * $factor))
            $h = [Math]::Max(1, [int][Math]::Round($section.Height * $factor))
            if ([long]$w * $h -gt 100000000) { throw 'Choose smaller output dimensions.' }
            $scaled = New-Object Drawing.Bitmap $w,$h
            try {
                $drawing = [Drawing.Graphics]::FromImage($scaled)
                try {
                    if ($Encoder.Guid -eq [Drawing.Imaging.ImageFormat]::Jpeg.Guid) { $drawing.Clear([Drawing.Color]::White) }
                    $drawing.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $drawing.DrawImage($section, 0, 0, $w, $h)
                } finally { $drawing.Dispose() }
                $scaled.Save($Output, $Encoder)
            } finally { $scaled.Dispose() }
        } finally { $section.Dispose() }
    } finally { $original.Dispose() }
}

$targetPath = Get-PluginTarget $Target $Overwrite
$stage = New-PluginStage $targetPath
$sourceItem = Get-Item -LiteralPath $Source
$extension = [IO.Path]::GetExtension($targetPath).TrimStart('.').ToLowerInvariant()
try {
    if ($sourceItem.PSIsContainer) {
        if ($extension -ne 'zip') { throw 'Choose a ZIP target for a folder of images.' }
        $images = @(Get-ChildItem -LiteralPath $Source -File -Attributes !ReparsePoint | Where-Object {
            $_.Extension -match '^\.(png|jpe?g|bmp|gif|tiff?)$'
        } | Sort-Object Name)
        if ($images.Count -eq 0) { throw 'The source folder contains no supported images.' }
        $archive = [IO.Compression.ZipFile]::Open($stage, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($image in $images) {
                $entry = $archive.CreateEntry($image.Name + '.' + $Format).Open()
                try {
                    $memory = New-Object IO.MemoryStream
                    try {
                        Write-Image $image.FullName $memory (Get-ImageFormat $Format)
                        $memory.Position = 0
                        $memory.CopyTo($entry)
                    } finally { $memory.Dispose() }
                } finally { $entry.Dispose() }
            }
        } finally { $archive.Dispose() }
    } else {
        $output = [IO.File]::Open($stage, [IO.FileMode]::CreateNew)
        try { Write-Image $Source $output (Get-ImageFormat $extension) } finally { $output.Dispose() }
    }
    Publish-PluginFile $stage $targetPath $Overwrite
} finally {
    if ([IO.File]::Exists($stage)) { [IO.File]::Delete($stage) }
}
