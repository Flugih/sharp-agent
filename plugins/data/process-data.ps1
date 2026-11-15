param(
    [Parameter(Mandatory = $true)][ValidateSet('convert','report','chart')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [char]$Delimiter = ';',
    [string]$Column,
    [string]$LabelColumn,
    [string]$Overwrite = 'no'
)
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'shared.ps1')
$utf8 = New-Object Text.UTF8Encoding($false)
$invariant = [Globalization.CultureInfo]::InvariantCulture
$targetPath = Get-PluginTarget $Target $Overwrite
$stage = New-PluginStage $targetPath
$extension = [IO.Path]::GetExtension($targetPath).ToLowerInvariant()
$rows = @(switch ([IO.Path]::GetExtension($Source).ToLowerInvariant()) {
    '.csv' { Import-Csv -LiteralPath $Source -Delimiter $Delimiter -Encoding UTF8 }
    '.json' { ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Source, $utf8)) }
    default { throw 'Choose CSV or JSON input.' }
})
if ($rows.Count -eq 0 -or @($rows | Where-Object { $_ -isnot [PSCustomObject] }).Count -gt 0) {
    throw 'Input must contain CSV rows or JSON records.'
}

function Find-Number($Value) {
    $parsed = 0.0
    if ($null -eq $Value) { return $null }
    $text = [Convert]::ToString($Value, $invariant)
    if ([double]::TryParse($text.Trim().Replace(',', '.'), [Globalization.NumberStyles]::Float, $invariant, [ref]$parsed) -and
        -not [double]::IsNaN($parsed) -and -not [double]::IsInfinity($parsed)) { return $parsed }
    $null
}

try {
    if ($Mode -eq 'convert') {
        if ($extension -eq '.json') {
            [IO.File]::WriteAllText($stage, (ConvertTo-Json -InputObject $rows -Depth 32), $utf8)
        } elseif ($extension -eq '.csv') {
            $fields = @($rows | ForEach-Object { $_.PSObject.Properties.Name } | Select-Object -Unique)
            $rows | ForEach-Object {
                $record = [ordered]@{}
                foreach ($field in $fields) {
                    $value = $_.$field
                    $record[$field] = if ($value -is [PSCustomObject] -or $value -is [array]) {
                        ConvertTo-Json -InputObject $value -Depth 32 -Compress
                    } else { $value }
                }
                [PSCustomObject]$record
            } | Export-Csv -LiteralPath $stage -Delimiter $Delimiter -NoTypeInformation -Encoding UTF8
        } else { throw 'Choose a CSV or JSON target.' }
    } else {
        if (-not $Column) { throw 'Specify the numeric column with Column.' }
        $points = @(for ($i = 0; $i -lt $rows.Count; $i++) {
            $number = Find-Number $rows[$i].$Column
            if ($null -eq $number) { continue }
            $label = if ($LabelColumn -and $rows[$i].$LabelColumn) { [string]$rows[$i].$LabelColumn } else { [string]($i + 1) }
            [PSCustomObject]@{ Label = $label; Value = $number }
        })
        if ($points.Count -eq 0) { throw 'The selected column contains no numeric values.' }
        if ($Mode -eq 'report') {
            $summary = $points | Measure-Object -Property Value -Minimum -Maximum -Average -Sum
            $report = [ordered]@{
                Column = $Column; Rows = $rows.Count; Numeric = $summary.Count; MissingOrInvalid = $rows.Count - $summary.Count
                Minimum = $summary.Minimum; Maximum = $summary.Maximum; Mean = $summary.Average; Sum = $summary.Sum
            }
            if ($extension -eq '.json') {
                [IO.File]::WriteAllText($stage, (ConvertTo-Json -InputObject $report), $utf8)
            } elseif ($extension -eq '.md') {
                $lines = @('# Column statistics', '', '| Metric | Value |', '| --- | --- |')
                $lines += foreach ($pair in $report.GetEnumerator()) {
                    '| ' + $pair.Key + ' | ' + [Convert]::ToString($pair.Value, $invariant).Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ') + ' |'
                }
                [IO.File]::WriteAllLines($stage, [string[]]$lines, $utf8)
            } else { throw 'Choose a Markdown or JSON target for the report.' }
        } else {
            if ($extension -ne '.png') { throw 'Choose a PNG target for the chart.' }
            if ($points.Count -gt 100) { throw 'Select up to 100 rows for a readable chart.' }
            Add-Type -AssemblyName System.Windows.Forms, System.Windows.Forms.DataVisualization, System.Drawing
            $chart = New-Object Windows.Forms.DataVisualization.Charting.Chart
            try {
                $chart.Width = 1100
                $chart.Height = 570
                $area = $chart.ChartAreas.Add('Data')
                $area.AxisX.LabelStyle.Angle = -45
                $area.AxisX.MajorGrid.Enabled = $false
                $series = $chart.Series.Add($Column)
                $series.ChartType = [Windows.Forms.DataVisualization.Charting.SeriesChartType]::Column
                $series.Color = [Drawing.Color]::FromArgb(38, 116, 220)
                foreach ($point in $points) { [void]$series.Points.AddXY($point.Label, $point.Value) }
                [void]$chart.Titles.Add($Column)
                $chart.SaveImage($stage, [Windows.Forms.DataVisualization.Charting.ChartImageFormat]::Png)
            } finally { $chart.Dispose() }
        }
    }
    Publish-PluginFile $stage $targetPath $Overwrite
} finally {
    if ([IO.File]::Exists($stage)) { [IO.File]::Delete($stage) }
}
