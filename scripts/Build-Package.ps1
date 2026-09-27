param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
dotnet build (Join-Path $root 'src/Plugin/Plugin.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
if (!$SkipTests) {
    dotnet test (Join-Path $root 'tests/Core.Tests/Core.Tests.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
$manifest = Get-Content -LiteralPath (Join-Path $root 'packaging/manifest.json') -Raw | ConvertFrom-Json
$stage = Join-Path $root ('artifacts/package-' + [Guid]::NewGuid().ToString('N'))
$plugins = Join-Path $stage 'BepInEx/plugins/EpicLootContainerAccess'
New-Item -ItemType Directory -Force $plugins | Out-Null
foreach ($name in @('EpicLootContainerAccess.dll', 'EpicLootContainerAccess.Core.dll')) {
    Copy-Item -LiteralPath (Join-Path $root "src/Plugin/bin/Release/net481/$name") -Destination $plugins
}
foreach ($name in @('README.md', 'CHANGELOG.md', 'VERIFICATION.md')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $stage }
Copy-Item -LiteralPath (Join-Path $root 'packaging/manifest.json') -Destination $stage
Add-Type -AssemblyName System.Drawing
$source = [Drawing.Image]::FromFile((Join-Path $root 'images/epicloot-container-access.png'))
$bitmap = New-Object Drawing.Bitmap 256,256
$graphics = [Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.Clear([Drawing.Color]::FromArgb(7, 24, 42))
    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $scale = [Math]::Min(256.0 / $source.Width, 256.0 / $source.Height)
    $width = [int]($source.Width * $scale); $height = [int]($source.Height * $scale)
    $graphics.DrawImage($source, [int]((256-$width)/2), [int]((256-$height)/2), $width, $height)
    $bitmap.Save((Join-Path $root 'packaging/icon.png'), [Drawing.Imaging.ImageFormat]::Png)
} finally { $graphics.Dispose(); $bitmap.Dispose(); $source.Dispose() }
Copy-Item -LiteralPath (Join-Path $root 'packaging/icon.png') -Destination $stage
$archive = Join-Path $root "artifacts/EpicLootContainerAccess-$($manifest.version_number).zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -Force
& (Join-Path $PSScriptRoot 'Validate-Package.ps1') -Archive $archive
Write-Output "Test package: $archive"
