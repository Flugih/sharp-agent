param([Parameter(Mandatory = $true)][string]$Source)
$ErrorActionPreference = 'Stop'

$project = Get-Item -LiteralPath $Source
if (-not $project.PSIsContainer) { throw 'Source must be a project folder.' }
Write-Output ('Project: ' + $project.FullName)
Get-ChildItem -LiteralPath $project.FullName -File | Where-Object {
    $_.Name -match '^(README(\..*)?|package\.json|pyproject\.toml|requirements\.txt|Cargo\.toml|go\.mod|pom\.xml|Makefile|.*\.(sln|csproj))$'
} | Select-Object -First 50 -ExpandProperty Name

$git = Get-Command 'git' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($git -and (Test-Path -LiteralPath (Join-Path $project.FullName '.git'))) {
    & $git.Source -C $project.FullName --no-optional-locks -c core.fsmonitor=false status --short --branch
    if ($LASTEXITCODE -eq 0) {
        & $git.Source -C $project.FullName --no-optional-locks --no-pager log -5 --oneline 2>$null
        if ($LASTEXITCODE -ne 0) { Write-Output 'No commits found.' }
    }
}
