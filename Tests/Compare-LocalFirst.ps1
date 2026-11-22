param(
    [string]$BaseCommit = 'eb277ef',
    [Parameter(Mandatory = $true)][string]$Output,
    [switch]$Live
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$report = [IO.Path]::GetFullPath($Output)
$previousMode = $env:SHARP_AGENT_BENCH_LIVE
$runId = [Guid]::NewGuid().ToString('N')
$scratch = Join-Path $workspace '.sharpagent'
$tempRoot = Join-Path $scratch 'tmp'
$temporary = Join-Path $tempRoot $runId
$baseline = Join-Path $temporary 'base'
$lease = $null

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
    foreach ($folder in @($scratch, $tempRoot, $temporary)) {
        if (!(Test-Path -LiteralPath $folder)) { [IO.Directory]::CreateDirectory($folder) | Out-Null }
        $entry = Get-Item -LiteralPath $folder -Force
        if (!$entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Unexpected verification directory: $folder"
        }
    }
    $lease = [IO.File]::Open((Join-Path $temporary '.verification.lock'), 'CreateNew', 'Write', 'None')
    $marker = [Text.Encoding]::ASCII.GetBytes($runId)
    $lease.Write($marker, 0, $marker.Length)
    $lease.Flush()
    $env:SHARP_AGENT_BENCH_LIVE = if ($Live) {'1'} else {'0'}
    [IO.Directory]::CreateDirectory($baseline) | Out-Null
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
    if ($null -ne $lease) {
        $lease.Dispose()
        $resolved = (Resolve-Path -LiteralPath $temporary).ProviderPath
        if ($resolved -ne $temporary -or [IO.Path]::GetDirectoryName($resolved) -ne $tempRoot) {
            throw "Unexpected verification directory: $resolved"
        }
        foreach ($folder in @($scratch, $tempRoot)) {
            if ((Get-Item -LiteralPath $folder -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Linked verification path: $folder"
            }
        }
        $folders = [Collections.Generic.Stack[string]]::new()
        $folders.Push($resolved)
        while ($folders.Count -gt 0) {
            $entry = Get-Item -LiteralPath $folders.Pop() -Force
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked verification path: $($entry.FullName)" }
            foreach ($child in Get-ChildItem -LiteralPath $entry.FullName -Force) {
                if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked verification path: $($child.FullName)" }
                if ($child.PSIsContainer) { $folders.Push($child.FullName) }
            }
        }
        if ([Environment]::UserInteractive) {
            Add-Type -AssemblyName Microsoft.VisualBasic
            [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($resolved,
                [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs, [Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
        }
    }
}
