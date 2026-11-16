param(
    [string]$Markdown,
    [string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$Overwrite = 'no'
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'shared.ps1')

function Get-Runs([string]$text, [bool]$strong) {
    -join $(foreach ($part in [regex]::Split($text, '(\*\*[^*]+\*\*|\*[^*\s][^*]*\*|`[^`]+`)')) {
        if (-not $part) { continue }
        $bold = $strong; $italic = $mono = $false
        if ($part -match '^\*\*(.+)\*\*$') { $part = $Matches[1]; $bold = $true }
        elseif ($part -match '^\*(.+)\*$') { $part = $Matches[1]; $italic = $true }
        elseif ($part -match '^`(.+)`$') { $part = $Matches[1]; $mono = $true }
        $props = ''
        if ($mono) { $props += '<w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:cs="Consolas"/>' }
        if ($bold) { $props += '<w:b/>' }
        if ($italic) { $props += '<w:i/>' }
        if ($props) { $props = "<w:rPr>$props</w:rPr>" }
        "<w:r>$props<w:t xml:space=`"preserve`">$(Esc $part)</w:t></w:r>"
    })
}

function Get-Paragraph([string]$text, [string]$style, [string]$marker) {
    $props = ''
    if ($style) { $props = "<w:pPr><w:pStyle w:val=`"$style`"/></w:pPr>" }
    if ($marker) { $props += "<w:r><w:t xml:space=`"preserve`">$(Esc $marker)</w:t><w:tab/></w:r>" }
    "<w:p>$props$(Get-Runs $text)</w:p>"
}

function Get-Table([string[]]$lines) {
    $rows = @(foreach ($line in $lines) {
        $cells = @($line.Trim().Trim('|').Split('|') | ForEach-Object { $_.Trim() })
        if (($cells -join '') -notmatch '^[-:\s]*$') { , $cells }
    })
    if ($rows.Count -eq 0) { return '' }
    $columns = ($rows | ForEach-Object { $_.Count } | Measure-Object -Maximum).Maximum
    $border = 'w:val="single" w:sz="4" w:space="0" w:color="A6A6A6"'
    $width = [int][Math]::Floor(9355 / $columns)
    $header = $true
    "<w:tbl><w:tblPr><w:tblW w:w=`"5000`" w:type=`"pct`"/><w:tblBorders>" +
        -join ('top', 'left', 'bottom', 'right', 'insideH', 'insideV' | ForEach-Object { "<w:$_ $border/>" }) + '</w:tblBorders>' +
        '<w:tblCellMar><w:left w:w="108" w:type="dxa"/><w:right w:w="108" w:type="dxa"/></w:tblCellMar></w:tblPr><w:tblGrid>' +
        "<w:gridCol w:w=`"$width`"/>" * $columns + '</w:tblGrid>' + -join $(foreach ($row in $rows) {
            '<w:tr>' + -join (0..($columns - 1) | ForEach-Object {
                "<w:tc><w:tcPr><w:tcW w:w=`"$width`" w:type=`"dxa`"/></w:tcPr><w:p><w:pPr><w:spacing w:after=`"0`"/></w:pPr>" +
                "$(Get-Runs $row[$_] $header)</w:p></w:tc>" }) + '</w:tr>'
            $header = $false
        }) + '</w:tbl><w:p/>'
}

if (-not $Markdown) {
    if (-not $Source) { throw 'Pass -Markdown with the document text or -Source with the path to a .md or .txt file.' }
    $Markdown = [IO.File]::ReadAllText((Get-FullPath $Source), [Text.Encoding]::UTF8)
}
$Target = Get-OfficeTarget $Target $Overwrite
$body = New-Object Text.StringBuilder
$paragraph = New-Object Collections.Generic.List[string]
function Close-Paragraph {
    if ($paragraph.Count -gt 0) {
        [void]$body.Append((Get-Paragraph ($paragraph -join ' ')))
        $paragraph.Clear()
    }
}
$lines = $Markdown.Replace("`r`n", "`n").Split("`n")
for ($i = 0; $i -lt $lines.Count;) {
    $trim = $lines[$i++].Trim()
    if ($trim -eq '' -or $trim -match '^(-{3,}|\*{3,}|_{3,})$') { Close-Paragraph; continue }
    if ($trim -eq '---page---') { Close-Paragraph; [void]$body.Append('<w:p><w:r><w:br w:type="page"/></w:r></w:p>'); continue }
    if ($trim.StartsWith('```')) {
        Close-Paragraph
        for (; $i -lt $lines.Count -and -not $lines[$i].Trim().StartsWith('```'); $i++) {
            [void]$body.Append("<w:p><w:pPr><w:pStyle w:val=`"Code`"/></w:pPr><w:r><w:t xml:space=`"preserve`">$(Esc $lines[$i].TrimEnd())</w:t></w:r></w:p>")
        }
        $i++
        continue
    }
    $block = if ($trim -match '^(#{1,3})\s+(.+)$') { Get-Paragraph $Matches[2] ('Heading' + $Matches[1].Length) }
    elseif ($trim.StartsWith('|')) {
        $table = @($trim)
        while ($i -lt $lines.Count -and $lines[$i].Trim().StartsWith('|')) { $table += $lines[$i++].Trim() }
        Get-Table $table
    }
    elseif ($trim -match '^[-*+]\s+(.+)$') { Get-Paragraph $Matches[1] 'ListParagraph' ([string][char]0x2022) }
    elseif ($trim -match '^(\d+[.)])\s+(.+)$') { Get-Paragraph $Matches[2] 'ListParagraph' $Matches[1] }
    if ($null -eq $block) { $paragraph.Add($trim); continue }
    Close-Paragraph
    [void]$body.Append($block)
}
Close-Paragraph

$main = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"'
Write-OfficeFile $Target 'word/' ([ordered]@{
    'document.xml' = 'wordprocessingml.document.main', '', ("<w:document $main><w:body>$body" +
        '<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1134" w:right="850" w:bottom="1134" w:left="1701" w:header="708" w:footer="708" w:gutter="0"/></w:sectPr>' +
        '</w:body></w:document>')
    'styles.xml' = 'wordprocessingml.styles', 'styles', ("<w:styles $main><w:docDefaults><w:rPrDefault><w:rPr>" +
        '<w:rFonts w:ascii="Calibri" w:hAnsi="Calibri" w:eastAsia="Calibri" w:cs="Calibri"/><w:sz w:val="22"/><w:szCs w:val="22"/><w:lang w:val="ru-RU"/>' +
        '</w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="160" w:line="276" w:lineRule="auto"/></w:pPr></w:pPrDefault></w:docDefaults>' +
        '<w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:qFormat/></w:style>' + -join (1..3 | ForEach-Object {
            "<w:style w:type=`"paragraph`" w:styleId=`"Heading$_`"><w:name w:val=`"heading $_`"/><w:basedOn w:val=`"Normal`"/><w:next w:val=`"Normal`"/>" +
            "<w:qFormat/><w:pPr><w:keepNext/><w:spacing w:before=`"300`" w:after=`"120`"/><w:outlineLvl w:val=`"$($_ - 1)`"/></w:pPr>" +
            "<w:rPr><w:b/><w:sz w:val=`"$(36 - 4 * $_)`"/><w:szCs w:val=`"$(36 - 4 * $_)`"/></w:rPr></w:style>" }) +
        '<w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/><w:qFormat/>' +
        '<w:pPr><w:spacing w:after="60"/><w:ind w:left="720" w:hanging="360"/></w:pPr></w:style>' +
        '<w:style w:type="paragraph" w:styleId="Code"><w:name w:val="Code"/><w:basedOn w:val="Normal"/>' +
        '<w:pPr><w:shd w:val="clear" w:color="auto" w:fill="F2F2F2"/><w:spacing w:after="0" w:line="240" w:lineRule="auto"/></w:pPr>' +
        '<w:rPr><w:rFonts w:ascii="Consolas" w:hAnsi="Consolas" w:cs="Consolas"/><w:sz w:val="20"/><w:szCs w:val="20"/></w:rPr></w:style></w:styles>')
})
"Word document created: $Target"
