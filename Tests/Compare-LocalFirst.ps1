param(
    [string]$BaseCommit = 'eb277ef',
    [Parameter(Mandatory = $true)][string]$Output,
    [switch]$Live
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$report = [IO.Path]::GetFullPath($Output)
$previousMode = $env:SHARP_AGENT_BENCH_LIVE
$env:SHARP_AGENT_BENCH_LIVE = if ($Live) {'1'} else {'0'}
$temporary = Join-Path $workspace ('bin\local-first-benchmark-' + [Guid]::NewGuid().ToString('N'))
$baseline = Join-Path $temporary 'base'
New-Item -ItemType Directory -Path $baseline -Force | Out-Null

function Invoke-Native([scriptblock]$Action) {
    & $Action | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE)." }
}

function Invoke-Benchmark([string]$Checkout, [string]$Label) {
    $artifact = Join-Path $temporary $Label
    $testProject = Join-Path $Checkout 'Tests\SharpAgent.Tests.csproj'
    Invoke-Native { dotnet build $testProject -c Release --warnaserror -o $artifact --artifacts-path (Join-Path $artifact 'artifacts') }
    $dataset = Join-Path $temporary "$Label.json"
    $entry = Join-Path $artifact 'SharpAgent.Tests.dll'
    if ($Label -eq 'before') {
        $previousFailures = $env:SHARP_AGENT_BENCH_ALLOW_FAILURES
        $env:SHARP_AGENT_BENCH_ALLOW_FAILURES = '1'
        try { Invoke-Native { dotnet $entry $dataset } }
        finally { $env:SHARP_AGENT_BENCH_ALLOW_FAILURES = $previousFailures }
    } else {
        Invoke-Native { dotnet $entry --benchmark $dataset }
    }
    Get-Content -Raw -LiteralPath $dataset | ConvertFrom-Json
}

function Median([double[]]$Values) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $mid = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$mid] }
    ($sorted[$mid - 1] + $sorted[$mid]) / 2.0
}

try {
    $archive = Join-Path $temporary 'baseline.zip'
    Invoke-Native { git -C $workspace archive --format=zip --output=$archive $BaseCommit }
    Expand-Archive -LiteralPath $archive -DestinationPath $baseline
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LocalBenchmark.cs') -Destination (Join-Path $baseline 'Tests\LocalBenchmark.cs')
    @'
global using System.IO;
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
await LocalBenchmark.RunAsync(Path.Combine(AppContext.BaseDirectory, "fixtures"), args[0]);
'@ | Set-Content -LiteralPath (Join-Path $baseline 'Tests\Program.cs') -Encoding UTF8
    $before = Invoke-Benchmark $baseline 'before'
    $after = Invoke-Benchmark $workspace 'after'
    $rows = foreach ($entry in $before.Results) {
        $next = $after.Results | Where-Object Scenario -eq $entry.Scenario
        $down = if ($Live) {
            if ($null -eq $entry.Input -or $null -eq $next.Input) { $null }
            else { [Math]::Max(0, $entry.Input - $entry.Cached) - [Math]::Max(0, $next.Input - $next.Cached) }
        } else { $entry.EstimatedInput - $next.EstimatedInput }
        $amount = if ($Live) { [Math]::Max(0, $entry.Input - $entry.Cached) } else { $entry.EstimatedInput }
        [pscustomobject]@{
            Scenario = $entry.Scenario; Before = $entry; After = $next
            ReductionPercent = if ($amount -gt 0 -and $null -ne $down) { [Math]::Round(100 * $down / $amount, 2) } else { $null }
        }
    }
    [pscustomobject]@{
        Mode = $after.Mode; Model = $after.Model; Baseline = $BaseCommit
        Metric = if ($Live) {'provider uncached input'} else {'estimated input for deterministic replay'}
        MedianReductionPercent = Median @($rows.ReductionPercent | Where-Object { $null -ne $_ })
        Results = @($rows)
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $report -Encoding UTF8
}
finally {
    $env:SHARP_AGENT_BENCH_LIVE = $previousMode
    $resolved = (Resolve-Path -LiteralPath $temporary).ProviderPath
    $expectedParent = (Resolve-Path -LiteralPath (Join-Path $workspace 'bin')).ProviderPath
    $item = Get-Item -LiteralPath $resolved
    if ($resolved -eq $temporary -and [IO.Path]::GetDirectoryName($resolved) -eq $expectedParent -and
        -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Add-Type -AssemblyName Microsoft.VisualBasic
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($resolved,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs, [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
    }
}
