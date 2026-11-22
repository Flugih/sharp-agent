param([Parameter(Mandatory = $true)][string]$Source)
$ErrorActionPreference = 'Stop'
$project = Get-Item -LiteralPath $Source
if (-not $project.PSIsContainer) { throw 'Source must be a project folder.' }
$dir = $project.FullName
'Project: ' + $dir
Get-ChildItem -LiteralPath $dir -File | Where-Object {
    $_.Name -match '^(README(\..*)?|package\.json|pyproject\.toml|requirements\.txt|Cargo\.toml|go\.mod|pom\.xml|Makefile|.*\.(sln|csproj))$'
} | Select-Object -First 50 -ExpandProperty Name
$git = Get-Command 'git' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if ($git -and (Test-Path -LiteralPath (Join-Path $dir '.git'))) {
    $ErrorActionPreference = 'Continue'
    $git = $git.Source
    $filters = @(& $git -C $dir config --show-scope --name-only --get-regexp '^filter\..*\.(clean|smudge|process)$' 2>$null) -notmatch '^(global|system)\s'
    if ($LASTEXITCODE -gt 1 -or $filters) { 'Git status skipped: the repository config defines content filters or could not be read.' }
    else {
        & $git -C $dir --no-optional-locks -c core.fsmonitor=false status --short --branch --ignore-submodules=all
        if ($LASTEXITCODE -eq 0) {
            & $git -C $dir --no-optional-locks -c log.showSignature=false --no-pager log -5 --oneline 2>$null
            if ($LASTEXITCODE -ne 0) { 'No commits found.'; $global:LASTEXITCODE = 0 }
        }
    }
}
