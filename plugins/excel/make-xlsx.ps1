param(
    [string]$Csv,
    [string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$Sheet = 'Sheet1',
    [string]$Delimiter = '',
    [string]$Overwrite = 'no'
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'shared.ps1')
Add-Type -AssemblyName Microsoft.VisualBasic

function Get-ColumnName([int]$index) {
    $name = ''
    for ($index++; $index; $index = [Math]::Floor(($index - 1) / 26)) { $name = [string][char](65 + ($index - 1) % 26) + $name }
    $name
}

if (-not $Csv) {
    if (-not $Source) { throw 'Pass -Csv with the table text or -Source with the path to a .csv file.' }
    $Csv = [IO.File]::ReadAllText((Get-FullPath $Source), [Text.Encoding]::UTF8)
}
$Target = Get-OfficeTarget $Target $Overwrite
if (-not $Delimiter) {
    $first = $Csv.TrimStart().Split("`n")[0]
    $Delimiter = if ($first.Contains(';')) { ';' } elseif ($first.Contains("`t")) { "`t" } else { ',' }
}
$parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser((New-Object IO.StringReader($Csv.Trim())))
$parser.SetDelimiters($Delimiter)
$rows = New-Object Collections.Generic.List[object]
while (-not $parser.EndOfData) { $rows.Add([string[]]$parser.ReadFields()) }
if ($rows.Count -eq 0) { throw 'The table is empty.' }
$columns = ($rows | ForEach-Object { $_.Count } | Measure-Object -Maximum).Maximum
$widths = New-Object int[] $columns
$data = New-Object Text.StringBuilder
for ($r = 0; $r -lt $rows.Count; $r++) {
    [void]$data.Append("<row r=`"$($r + 1)`">")
    $row = $rows[$r]
    for ($c = 0; $c -lt $row.Count; $c++) {
        $value = $row[$c]
        if (-not $value) { continue }
        $widths[$c] = [Math]::Max($widths[$c], $value.Length)
        $ref = (Get-ColumnName $c) + ($r + 1)
        $number = $value -replace '\s'
        if ($r -and $number -match '^-?[0-9]+([.,][0-9]+)?$' -and $number -notmatch '^-?0[0-9]') {
            [void]$data.Append("<c r=`"$ref`"><v>$($number.Replace(',', '.'))</v></c>")
        } else {
            [void]$data.Append("<c r=`"$ref`"$(if (-not $r) { ' s="1"' }) t=`"inlineStr`"><is><t xml:space=`"preserve`">$(Esc $value)</t></is></c>")
        }
    }
    [void]$data.Append('</row>')
}
$cols = -join (0..($columns - 1) | ForEach-Object {
    "<col min=`"$($_ + 1)`" max=`"$($_ + 1)`" width=`"$([Math]::Min(60, [Math]::Max(8, $widths[$_] + 2)))`" customWidth=`"1`"/>" })
$name = ($Sheet -replace '[\[\]:*?/\\]', ' ').Trim()
if (-not $name) { $name = 'Sheet1' }
if ($name.Length -gt 31) { $name = $name.Substring(0, 31) }
$main = 'xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"'
Write-OfficeFile $Target 'xl/' ([ordered]@{
    'workbook.xml' = 'spreadsheetml.sheet.main', '',
        "<workbook $main xmlns:r=`"$OfficeRelations`"><sheets><sheet name=`"$(Esc $name)`" sheetId=`"1`" r:id=`"rId1`"/></sheets></workbook>"
    'worksheets/sheet1.xml' = 'spreadsheetml.worksheet', 'worksheet', ("<worksheet $main xmlns:r=`"$OfficeRelations`">" +
        '<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>' +
        "<cols>$cols</cols><sheetData>$data</sheetData></worksheet>")
    'styles.xml' = 'spreadsheetml.styles', 'styles', ("<styleSheet $main>" +
        '<fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>' +
        '<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>' +
        '<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>' +
        '<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>' +
        '<cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/>' +
        '<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs>' +
        '<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles></styleSheet>')
})
"Excel workbook created: $Target ($($rows.Count) rows)"
