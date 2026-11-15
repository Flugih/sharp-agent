param(
    [Parameter(Mandatory = $true)][ValidateSet('convert','merge','extract','ocr')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [ValidatePattern('^[a-zA-Z0-9_]+(\+[a-zA-Z0-9_]+)*$')][string]$Language = 'rus+eng',
    [ValidateRange(0, 100000)][int]$FirstPage = 0,
    [ValidateRange(0, 100000)][int]$LastPage = 0,
    [string]$Overwrite = 'no'
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'shared.ps1')

function Export-WordPdf([string]$Path, [string]$Output) {
    $word = $null
    $doc = $null
    $alreadyRunning = [bool](Get-Process WINWORD -ErrorAction SilentlyContinue)
    try {
        try { $word = New-Object -ComObject Word.Application } catch { throw 'Install Microsoft Word to convert DOCX to PDF.' }
        $security = $word.AutomationSecurity
        $alerts = $word.DisplayAlerts
        $links = $word.Options.UpdateLinksAtOpen
        $word.AutomationSecurity = 3
        $word.DisplayAlerts = 0
        $word.Options.UpdateLinksAtOpen = $false
        $missing = [Type]::Missing
        $doc = $word.Documents.Open($Path, $false, $true, $false, '', '', $false, '', '', $missing, $missing, $false)
        $doc.ExportAsFixedFormat($Output, 17)
    } finally {
        if ($word) {
            try { if ($doc) { $doc.Close(0) } } finally {
                $word.Options.UpdateLinksAtOpen = $links
                $word.DisplayAlerts = $alerts
                $word.AutomationSecurity = $security
                if (-not $alreadyRunning -and $word.Documents.Count -eq 0) { $word.Quit() }
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($word)
            }
        }
    }
}

$targetPath = Get-PluginTarget $Target $Overwrite
$expected = if ($Mode -in 'convert','merge') { '.pdf' } else { '.txt' }
if ([IO.Path]::GetExtension($targetPath) -ne $expected) { throw ('Choose a ' + $expected + ' target.') }
if ($FirstPage -gt 0 -and $LastPage -gt 0 -and $FirstPage -gt $LastPage) { throw 'FirstPage must precede LastPage.' }
$sourceItem = Get-Item -LiteralPath $Source
$range = @()
if ($FirstPage -gt 0) { $range += @('-f', $FirstPage) }
if ($LastPage -gt 0) { $range += @('-l', $LastPage) }
$stage = New-PluginStage $targetPath
$scratch = $null
try {
    switch ($Mode) {
        'convert' {
            if ($sourceItem.PSIsContainer -or $sourceItem.Extension -ne '.docx') { throw 'Choose a DOCX source.' }
            Export-WordPdf $sourceItem.FullName $stage
        }
        'merge' {
            if (-not $sourceItem.PSIsContainer) { throw 'Choose a folder with the PDFs to merge.' }
            $pages = @(Get-ChildItem -LiteralPath $Source -Filter '*.pdf' -File -Attributes !ReparsePoint |
                Where-Object { $_.FullName -ne $targetPath } | Sort-Object Name | Select-Object -ExpandProperty FullName)
            if ($pages.Count -lt 2) { throw 'The source folder must contain at least two PDFs.' }
            $qpdf = Get-PluginProgram 'qpdf'
            & $qpdf --empty --pages @pages -- $stage
            if ($LASTEXITCODE -notin 0,3) { throw 'qpdf failed. Check whether the source files open.' }
        }
        'extract' {
            if ($sourceItem.PSIsContainer -or $sourceItem.Extension -ne '.pdf') { throw 'Choose a PDF source.' }
            $extract = Get-PluginProgram 'pdftotext'
            & $extract -enc UTF-8 -layout @range $Source $stage
            if ($LASTEXITCODE -ne 0) { throw 'Could not extract PDF text. Check the file or try OCR.' }
        }
        'ocr' {
            if ($sourceItem.PSIsContainer -or $sourceItem.Extension -notmatch '^\.(pdf|png|jpe?g|bmp|tiff?)$') {
                throw 'Choose a PDF, PNG, JPEG, BMP or TIFF source.'
            }
            $ocr = Get-PluginProgram 'tesseract'
            $scratch = New-PluginScratch
            $images = @($sourceItem.FullName)
            if ($sourceItem.Extension -eq '.pdf') {
                $rasterize = Get-PluginProgram 'pdftoppm'
                & $rasterize -r 230 -png @range $Source (Join-Path $scratch 'page')
                if ($LASTEXITCODE -ne 0) { throw 'Could not render PDF pages for OCR.' }
                $images = @(Get-ChildItem -LiteralPath $scratch -Filter '*.png' -File | Sort-Object Name | Select-Object -ExpandProperty FullName)
                if ($images.Count -eq 0) { throw 'No pages found in the requested range.' }
            }
            $output = New-Object IO.StreamWriter($stage, $false, (New-Object Text.UTF8Encoding($false)))
            try {
                $base = Join-Path $scratch 'recognized'
                foreach ($image in $images) {
                    & $ocr $image $base -l $Language
                    if ($LASTEXITCODE -ne 0) { throw 'OCR failed. Check that the requested languages are installed.' }
                    $output.WriteLine([IO.File]::ReadAllText($base + '.txt').Trim())
                    $output.WriteLine()
                }
            } finally { $output.Dispose() }
        }
    }
    Publish-PluginFile $stage $targetPath $Overwrite
} finally {
    if ([IO.File]::Exists($stage)) { [IO.File]::Delete($stage) }
    if ($scratch) { [IO.Directory]::Delete($scratch, $true) }
}
