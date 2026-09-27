param([Parameter(Mandatory=$true)][string]$Archive)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Drawing
$zip = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Archive))
try {
    $entries = @{}
    foreach ($entry in $zip.Entries) { $entries[$entry.FullName.Replace('\','/')] = $entry }
    foreach ($name in @('manifest.json','README.md','CHANGELOG.md','VERIFICATION.md','icon.png','BepInEx/plugins/EpicLootContainerAccess/EpicLootContainerAccess.dll','BepInEx/plugins/EpicLootContainerAccess/EpicLootContainerAccess.Core.dll')) {
        if (!$entries.ContainsKey($name)) { throw "Missing package entry: $name" }
    }
    if ($entries.Keys | Where-Object { $_ -match '(RuntimeSmoke|Environment.props|assembly_valheim|EpicLoot\.dll|Jotunn\.dll)' }) { throw 'Package contains development or third-party binaries.' }
    $reader = New-Object IO.StreamReader($entries['manifest.json'].Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    if ($manifest.name -ne 'EpicLootContainerAccess' -or $manifest.dependencies.Count -ne 3) { throw 'Invalid manifest.' }
    $stream = $entries['icon.png'].Open()
    try {
        $icon = [Drawing.Image]::FromStream($stream)
        try { if ($icon.Width -ne 256 -or $icon.Height -ne 256) { throw 'Icon must be 256 x 256.' } } finally { $icon.Dispose() }
    } finally { $stream.Dispose() }
    Write-Output 'Package validation passed.'
} finally { $zip.Dispose() }
