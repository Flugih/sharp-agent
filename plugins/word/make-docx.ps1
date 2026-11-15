param(
    [string]$Markdown,
    [string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$Overwrite = 'no'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

function Get-FullPath([string]$path) {
    [IO.Path]::GetFullPath([IO.Path]::Combine((Get-Location).ProviderPath, $path))
}

function Esc([string]$text) { [Security.SecurityElement]::Escape($text) }

function Get-Runs([string]$text, [bool]$strong = $false) {
    $sb = New-Object Text.StringBuilder
    foreach ($part in [regex]::Split($text, '(\*\*[^*]+\*\*|\*[^*\s][^*]*\*|`[^`]+`)')) {
        if ($part.Length -eq 0) { continue }
        $bold = $strong; $italic = $false; $mono = $false
        if ($part -match '^\*\*(.+)\*\*$') { $part = $Matches[1]; $bold = $true }
        elseif ($part -match '^\*(.+)\*$') { $part = $Matches[1]; $italic = $true }
        elseif ($part -match '^`(.+)`$') { $part = $Matches[1]; $mono = $true }
        $props = ''
        if ($mono) { $props += '<w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:cs="Consolas"/>' }
        if ($bold) { $props += '<w:b/>' }
        if ($italic) { $props += '<w:i/>' }
        if ($props) { $props = "<w:rPr>$props</w:rPr>" }
        [void]$sb.Append("<w:r>$props<w:t xml:space=`"preserve`">$(Esc $part)</w:t></w:r>")
    }
    $sb.ToString()
}

function Get-Paragraph([string]$text, [string]$style = '', [string]$marker = '') {
    $props = ''
    if ($style) { $props = "<w:pPr><w:pStyle w:val=`"$style`"/></w:pPr>" }
    if ($marker) { $props += "<w:r><w:t xml:space=`"preserve`">$(Esc $marker)</w:t><w:tab/></w:r>" }
    "<w:p>$props$(Get-Runs $text)</w:p>"
}

function Get-Table([string[]]$lines) {
    $rows = @()
    foreach ($line in $lines) {
        $cells = @($line.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() })
        if (($cells -join '') -match '^[-:\s]*$') { continue }
        $rows += , $cells
    }
    if ($rows.Count -eq 0) { return '' }
    $columns = ($rows | ForEach-Object { $_.Count } | Measure-Object -Maximum).Maximum
    $border = 'w:val="single" w:sz="4" w:space="0" w:color="A6A6A6"'
    $sb = New-Object Text.StringBuilder
    [void]$sb.Append("<w:tbl><w:tblPr><w:tblW w:w=`"5000`" w:type=`"pct`"/><w:tblBorders><w:top $border/><w:left $border/>")
    [void]$sb.Append("<w:bottom $border/><w:right $border/><w:insideH $border/><w:insideV $border/></w:tblBorders>")
    [void]$sb.Append('<w:tblCellMar><w:left w:w="108" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr><w:tblGrid>')
    $width = [int][Math]::Floor(9355 / $columns)
    for ($c = 0; $c -lt $columns; $c++) { [void]$sb.Append("<w:gridCol w:w=`"$width`"/>") }
    [void]$sb.Append('</w:tblGrid>')
    $header = $true
    foreach ($row in $rows) {
        [void]$sb.Append('<w:tr>')
        for ($c = 0; $c -lt $columns; $c++) {
            $cell = ''
            if ($c -lt $row.Count) { $cell = $row[$c] }
            [void]$sb.Append("<w:tc><w:tcPr><w:tcW w:w=`"$width`" w:type=`"dxa`"/></w:tcPr>")
            [void]$sb.Append("<w:p><w:pPr><w:spacing w:after=`"0`"/></w:pPr>$(Get-Runs $cell $header)</w:p></w:tc>")
        }
        [void]$sb.Append('</w:tr>')
        $header = $false
    }
    [void]$sb.Append('</w:tbl><w:p/>')
    $sb.ToString()
}

if (-not $Markdown) {
    if (-not $Source) { throw 'Pass -Markdown with the document text or -Source with the path to a .md or .txt file.' }
    $Markdown = [IO.File]::ReadAllText((Get-FullPath $Source), [Text.Encoding]::UTF8)
}
$Target = Get-FullPath $Target
if ((Test-Path -LiteralPath $Target) -and $Overwrite -ne 'yes') {
    throw "File already exists: $Target. Ask the user and pass -Overwrite yes to replace it, or choose another name."
}

$body = New-Object Text.StringBuilder
$paragraph = New-Object Collections.Generic.List[string]
function Close-Paragraph {
    if ($paragraph.Count -gt 0) {
        [void]$body.Append((Get-Paragraph ($paragraph -join ' ')))
        $paragraph.Clear()
    }
}

$lines = $Markdown.Replace("`r`n", "`n").Split("`n")
$i = 0
while ($i -lt $lines.Count) {
    $trim = $lines[$i].Trim()
    $i++
    if ($trim -eq '') { Close-Paragraph; continue }
    if ($trim -eq '---page---') { Close-Paragraph; [void]$body.Append('<w:p><w:r><w:br w:type="page"/></w:r></w:p>'); continue }
    if ($trim -match '^(-{3,}|\*{3,}|_{3,})$') { Close-Paragraph; continue }
    if ($trim.StartsWith('```')) {
        Close-Paragraph
        while ($i -lt $lines.Count -and -not $lines[$i].Trim().StartsWith('```')) {
            [void]$body.Append("<w:p><w:pPr><w:pStyle w:val=`"Code`"/></w:pPr><w:r><w:t xml:space=`"preserve`">$(Esc $lines[$i].TrimEnd())</w:t></w:r></w:p>")
            $i++
        }
        $i++
        continue
    }
    if ($trim -match '^(#{1,3})\s+(.+)$') {
        Close-Paragraph
        [void]$body.Append((Get-Paragraph $Matches[2] ('Heading' + $Matches[1].Length)))
        continue
    }
    if ($trim.StartsWith('|')) {
        Close-Paragraph
        $table = @($trim)
        while ($i -lt $lines.Count -and $lines[$i].Trim().StartsWith('|')) { $table += $lines[$i].Trim(); $i++ }
        [void]$body.Append((Get-Table $table))
        continue
    }
    if ($trim -match '^[-*+]\s+(.+)$') {
        Close-Paragraph
        [void]$body.Append((Get-Paragraph $Matches[1] 'ListParagraph' ([string][char]0x2022)))
        continue
    }
    if ($trim -match '^(\d+[.)])\s+(.+)$') {
        Close-Paragraph
        [void]$body.Append((Get-Paragraph $Matches[2] 'ListParagraph' $Matches[1]))
        continue
    }
    $paragraph.Add($trim)
}
Close-Paragraph

$heading = { param($id, $name, $size, $level)
    "<w:style w:type=`"paragraph`" w:styleId=`"$id`"><w:name w:val=`"$name`"/><w:basedOn w:val=`"Normal`"/><w:next w:val=`"Normal`"/>" +
    "<w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=`"300`" w:after=`"120`"/><w:outlineLvl w:val=`"$level`"/></w:pPr>" +
    "<w:rPr><w:b/><w:sz w:val=`"$size`"/><w:szCs w:val=`"$size`"/></w:rPr></w:style>"
}
$main = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"'
$relations = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
$prolog = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
$parts = [ordered]@{
    '[Content_Types].xml' = "$prolog<Types xmlns=`"http://schemas.openxmlformats.org/package/2006/content-types`">" +
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>' +
        '<Default Extension="xml" ContentType="application/xml"/>' +
        '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>' +
        '<Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/></Types>'
    '_rels/.rels' = "$prolog<Relationships xmlns=`"http://schemas.openxmlformats.org/package/2006/relationships`">" +
        "<Relationship Id=`"rId1`" Type=`"$relations/officeDocument`" Target=`"word/document.xml`"/></Relationships>"
    'word/_rels/document.xml.rels' = "$prolog<Relationships xmlns=`"http://schemas.openxmlformats.org/package/2006/relationships`">" +
        "<Relationship Id=`"rId1`" Type=`"$relations/styles`" Target=`"styles.xml`"/></Relationships>"
    'word/document.xml' = "$prolog<w:document $main><w:body>$($body.ToString())" +
        '<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1134" w:right="850" w:bottom="1134" w:left="1701" w:header="708" w:footer="708" w:gutter="0"/></w:sectPr>' +
        '</w:body></w:document>'
    'word/styles.xml' = "$prolog<w:styles $main><w:docDefaults><w:rPrDefault><w:rPr>" +
        '<w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:szCs w:val="22"/><w:lang w:val="ru-RU"/>' +
        '</w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="276" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>' +
        '<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>' +
        (& $heading 'Heading1' 'heading 1' 32 0) + (& $heading 'Heading2' 'heading 2' 28 1) + (& $heading 'Heading3' 'heading 3' 24 2) +
        '<w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/><w:qFormat/>' +
        '<w:pPr><w:spacing w:after="60"/><w:ind w:left="720" w:hanging="360"/></w:pPr></w:style>' +
        '<w:style w:type="paragraph" w:styleId="Code"><w:name w:val="Code"/><w:basedOn w:val="Normal"/>' +
        '<w:pPr><w:shd w:val="clear" w:color="auto" w:fill="F2F2F2"/><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr>' +
        '<w:rPr><w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:cs="Consolas"/><w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:style></w:styles>'
}

$folder = [IO.Path]::GetDirectoryName($Target)
if ($folder) { [void][IO.Directory]::CreateDirectory($folder) }
if (Test-Path -LiteralPath $Target) { Remove-Item -LiteralPath $Target -Force }
$zip = [IO.Compression.ZipFile]::Open($Target, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($part in $parts.GetEnumerator()) {
        $writer = New-Object IO.StreamWriter($zip.CreateEntry($part.Key).Open(), (New-Object Text.UTF8Encoding($false)))
        $writer.Write($part.Value)
        $writer.Dispose()
    }
}
finally {
    $zip.Dispose()
}
Write-Output "Word document created: $Target"
