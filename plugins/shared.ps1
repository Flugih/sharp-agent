function Get-PluginTarget([string]$Path, [string]$Overwrite) {
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.File]::Exists($full) -and $Overwrite -ne 'yes') { throw 'Target exists. Choose a new name or approve overwriting.' }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($full))) { throw 'Create the target folder first.' }
    $full
}

function New-PluginStage([string]$Path) {
    $name = '.sharpagent-' + [Guid]::NewGuid().ToString('N') + [IO.Path]::GetExtension($Path)
    Join-Path ([IO.Path]::GetDirectoryName($Path)) $name
}

function Publish-PluginFile([string]$Stage, [string]$Target, [string]$Overwrite) {
    if ([IO.File]::Exists($Target) -and $Overwrite -eq 'yes') {
        [IO.File]::Replace($Stage, $Target, $null)
    } else {
        [IO.File]::Move($Stage, $Target)
    }
    Write-Output ('Created: ' + $Target)
}

function Get-PluginProgram([string]$Name) {
    $program = Get-Command -Name $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $program) { throw ('Install ' + $Name + ' and add it to PATH, then try again.') }
    $program.Source
}

function New-PluginScratch {
    $root = Join-Path (Split-Path -Parent $PSScriptRoot) 'temp'
    $path = Join-Path $root ('plugin-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($path)
    $path
}
