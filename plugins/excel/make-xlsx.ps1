param(
    [string]$Csv,
    [string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$Sheet = 'Sheet1',
    [string]$Delimiter = '',
    [string]$Overwrite = 'no'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem, Microsoft.VisualBasic

function Get-FullPath([string]$path) {
    [IO.Path]::GetFullPath([IO.Path]::Combine((Get-Location).ProviderPath, $path))
}

function Esc([string]$text) { [Security.SecurityElement]::Escape($text) }

function Get-ColumnName([int]$index) {
    $name = ''
    $index++
    while ($index -gt 0) {
        $rest = ($index - 1) % 26
        $name = [string][char](65 + $rest) + $name
        $index = [int][Math]::Floor(($index - 1) / 26)
    }
    $name
}

function Get-Number([string]$value) {
    $clean = $value -replace '[\s ]', ''
    if ($clean -notmatch '^-?\d+([.,]\d+)?$') { return $null }
    if ($clean -match '^-?0\d') { return $null }
    $clean.Replace(',', '.')
}

if (-not $Csv) {
    if (-not $Source) { throw 'Pass -Csv with the table text or -Source with the path to a .csv file.' }
    $Csv = [IO.File]::ReadAllText((Get-FullPath $Source), [Text.Encoding]::UTF8)
}
$Target = Get-FullPath $Target
if ((Test-Path -LiteralPath $Target) -and $Overwrite -ne 'yes') {
    throw "File already exists: $Target. Ask the user and pass -Overwrite yes to replace it, or choose another name."
}
if (-not $Delimiter) {
    $first = $Csv.TrimStart().Split("`n")[0]
    if ($first.Contains(';')) { $Delimiter = ';' }
    elseif ($first.Contains("`t")) { $Delimiter = "`t" }
    else { $Delimiter = ',' }
}

$parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser((New-Object IO.StringReader($Csv.Trim())))
$parser.SetDelimiters($Delimiter)
$parser.HasFieldsEnclosedInQuotes = $true
$rows = New-Object Collections.Generic.List[object]
while (-not $parser.EndOfData) { $rows.Add([string[]]$parser.ReadFields()) }
$parser.Dispose()
if ($rows.Count -eq 0) { throw 'The table is empty.' }

$columns = ($rows | ForEach-Object { $_.Count } | Measure-Object -Maximum).Maximum
$widths = New-Object int[] $columns
$data = New-Object Text.StringBuilder
for ($r = 0; $r -lt $rows.Count; $r++) {
    [void]$data.Append("<row r=`"$($r + 1)`">")
    $row = $rows[$r]
    for ($c = 0; $c -lt $row.Count; $c++) {
        $value = $row[$c]
        if ($value.Length -eq 0) { continue }
        $widths[$c] = [Math]::Max($widths[$c], $value.Length)
        $ref = (Get-ColumnName $c) + ($r + 1)
        $number = $null
        if ($r -gt 0) { $number = Get-Number $value }
        if ($null -ne $number) {
            [void]$data.Append("<c r=`"$ref`"><v>$number</v></c>")
        }
        else {
            $style = ''
            if ($r -eq 0) { $style = ' s="1"' }
            [void]$data.Append("<c r=`"$ref`"$style t=`"inlineStr`"><is><t xml:space=`"preserve`">$(Esc $value)</t></is></c>")
        }
    }
    [void]$data.Append('</row>')
}
$cols = New-Object Text.StringBuilder
for ($c = 0; $c -lt $columns; $c++) {
    $width = [Math]::Min(60, [Math]::Max(8, $widths[$c] + 2))
    [void]$cols.Append("<col min=`"$($c + 1)`" max=`"$($c + 1)`" width=`"$width`" customWidth=`"1`"/>")
}

$name = ($Sheet -replace '[\[\]:*?/\\]', ' ').Trim()
if ($name.Length -eq 0) { $name = 'Sheet1' }
if ($name.Length -gt 31) { $name = $name.Substring(0, 31) }

$main = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
$relations = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
$prolog = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
$parts = [ordered]@{
    '[Content_Types].xml' = "$prolog<Types xmlns=`"http://schemas.openxmlformats.org/package/2006/content-types`">" +
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>' +
        '<Default Extension="xml" ContentType="application/xml"/>' +
        '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>' +
        '<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>' +
        '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>'
    '_rels/.rels' = "$prolog<Relationships xmlns=`"http://schemas.openxmlformats.org/package/2006/relationships`">" +
        "<Relationship Id=`"rId1`" Type=`"$relations/officeDocument`" Target=`"xl/workbook.xml`"/></Relationships>"
    'xl/_rels/workbook.xml.rels' = "$prolog<Relationships xmlns=`"http://schemas.openxmlformats.org/package/2006/relationships`">" +
        "<Relationship Id=`"rId1`" Type=`"$relations/worksheet`" Target=`"worksheets/sheet1.xml`"/>" +
        "<Relationship Id=`"rId2`" Type=`"$relations/styles`" Target=`"styles.xml`"/></Relationships>"
    'xl/workbook.xml' = "$prolog<workbook xmlns=`"$main`" xmlns:r=`"$relations`"><sheets>" +
        "<sheet name=`"$(Esc $name)`" sheetId=`"1`" r:id=`"rId1`"/></sheets></workbook>"
    'xl/worksheets/sheet1.xml' = "$prolog<worksheet xmlns=`"$main`" xmlns:r=`"$relations`">" +
        '<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>' +
        "<cols>$($cols.ToString())</cols><sheetData>$($data.ToString())</sheetData></worksheet>"
    'xl/styles.xml' = "$prolog<styleSheet xmlns=`"$main`">" +
        '<fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>' +
        '<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>' +
        '<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>' +
        '<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>' +
        '<cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>' +
        '<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs>' +
        '<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>'
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
Write-Output "Excel workbook created: $Target ($($rows.Count) rows)"
