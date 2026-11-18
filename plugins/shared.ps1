$OfficeRelations = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'

function Get-PluginTarget([string]$Path, [string]$Overwrite) {
    $full = [IO.Path]::GetFullPath($Path)
    if ([IO.File]::Exists($full) -and $Overwrite -ne 'yes') { throw 'Target exists. Choose a new name or approve overwriting.' }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($full))) { throw 'Create the target folder first.' }
    $full
}

function New-PluginStage([string]$Path) {
    Join-Path ([IO.Path]::GetDirectoryName($Path)) ('.sharpagent-' + [Guid]::NewGuid().ToString('N') + [IO.Path]::GetExtension($Path))
}

function Publish-PluginFile([string]$Stage, [string]$Target, [string]$Overwrite) {
    if ([IO.File]::Exists($Target) -and $Overwrite -eq 'yes') { [IO.File]::Replace($Stage, $Target, [NullString]::Value) }
    else { [IO.File]::Move($Stage, $Target) }
    $global:LASTEXITCODE = 0
    'Created: ' + $Target
}

function Get-PluginProgram([string]$Name) {
    $program = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $program) { throw "Install $Name and add it to PATH, then try again." }
    $program.Source
}

function New-PluginScratch {
    $path = Join-Path (Split-Path -Parent $PSScriptRoot) ('temp\plugin-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($path)
    $path
}

function Get-FullPath([string]$Path) { [IO.Path]::GetFullPath([IO.Path]::Combine((Get-Location).ProviderPath, $Path)) }

function Esc([string]$Text) { [Security.SecurityElement]::Escape($Text) }

function Get-OfficeTarget([string]$Path, [string]$Overwrite) {
    $full = Get-FullPath $Path
    if ((Test-Path -LiteralPath $full) -and $Overwrite -ne 'yes') {
        throw "File already exists: $full. Ask the user and pass -Overwrite yes to replace it, or choose another name."
    }
    $full
}

function Write-OfficeFile([string]$Target, [string]$Folder, $Parts, [string]$Overwrite) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $prolog = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
    $rels = "$prolog<Relationships xmlns=`"http://schemas.openxmlformats.org/package/2006/relationships`">"
    $names = @($Parts.Keys)
    $files = [ordered]@{
        '[Content_Types].xml' = "$prolog<Types xmlns=`"http://schemas.openxmlformats.org/package/2006/content-types`">" +
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>' +
            '<Default Extension="xml" ContentType="application/xml"/>' + -join ($names | ForEach-Object {
                "<Override PartName=`"/$Folder$_`" ContentType=`"application/vnd.openxmlformats-officedocument.$($Parts[$_][0])+xml`"/>" }) +
            '</Types>'
        '_rels/.rels' = "$rels<Relationship Id=`"rId1`" Type=`"$OfficeRelations/officeDocument`" Target=`"$Folder$($names[0])`"/></Relationships>"
        "${Folder}_rels/$($names[0]).rels" = $rels + -join (1..($names.Count - 1) | ForEach-Object {
            "<Relationship Id=`"rId$_`" Type=`"$OfficeRelations/$($Parts[$names[$_]][1])`" Target=`"$($names[$_])`"/>" }) + '</Relationships>'
    }
    foreach ($name in $names) { $files[$Folder + $name] = $prolog + $Parts[$name][2] }
    $directory = [IO.Path]::GetDirectoryName($Target)
    if ($directory) { [void][IO.Directory]::CreateDirectory($directory) }
    $stage = New-PluginStage $Target
    try {
        $zip = [IO.Compression.ZipFile]::Open($stage, [IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in $files.GetEnumerator()) {
                $writer = New-Object IO.StreamWriter($zip.CreateEntry($file.Key).Open(), (New-Object Text.UTF8Encoding($false)))
                try { $writer.Write($file.Value) } finally { $writer.Dispose() }
            }
        } finally { $zip.Dispose() }
        $null = Publish-PluginFile $stage $Target $Overwrite
    } finally { if ([IO.File]::Exists($stage)) { [IO.File]::Delete($stage) } }
}
