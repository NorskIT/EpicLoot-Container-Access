param([string]$Name = ('smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss')), [switch]$IncludeDrawers)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$environment = Get-Content -LiteralPath (Join-Path $root 'Environment.props')
$gamePath = [string]$environment.Project.PropertyGroup.VALHEIM_INSTALL
$bepPath = [string]$environment.Project.PropertyGroup.BEPINEX_PATH
$epicDll = [string]$environment.Project.PropertyGroup.EPICLOOT_DLL
if ($Name -notmatch '^[a-zA-Z0-9-]+$') { throw 'Invalid test directory name.' }
$stage = Join-Path $root "artifacts/$Name"
if (Test-Path -LiteralPath $stage) { throw 'Use a fresh test directory.' }
dotnet build (Join-Path $root 'tests/RuntimeSmoke/RuntimeSmoke.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Smoke build failed.' }
New-Item -ItemType Directory -Force "$stage/BepInEx/core", "$stage/BepInEx/plugins/ECA", "$stage/saves" | Out-Null
Copy-Item -Path (Join-Path $bepPath 'core/*') -Destination "$stage/BepInEx/core"
foreach ($name in @('Jotunn.dll', 'JsonDotNET.dll')) {
    $dependency = Get-ChildItem -LiteralPath (Join-Path $bepPath 'plugins') -Recurse -Filter $name | Select-Object -First 1
    if ($dependency) { Copy-Item -LiteralPath $dependency.Directory.FullName -Destination (Join-Path "$stage/BepInEx/plugins" $dependency.Directory.Name) -Recurse }
}
# JsonDotNET's plugin filename may differ; copy its declared package, including Newtonsoft.
$jsonPackage = Join-Path $bepPath 'plugins/ValheimModding-JsonDotNET'
if (Test-Path -LiteralPath $jsonPackage) { Copy-Item -LiteralPath $jsonPackage -Destination "$stage/BepInEx/plugins/JsonDotNET" -Recurse }
Copy-Item -LiteralPath (Split-Path $epicDll -Parent) -Destination "$stage/BepInEx/plugins/EpicLoot" -Recurse
if ($IncludeDrawers) {
    $drawerPackage = Join-Path $bepPath 'plugins/Ross-RossItemDrawers'
    if (!(Test-Path -LiteralPath $drawerPackage)) { throw 'RossItemDrawers is not installed at the reference path.' }
    Copy-Item -LiteralPath $drawerPackage -Destination "$stage/BepInEx/plugins/RossItemDrawers" -Recurse
}
Copy-Item -Path (Join-Path $root 'src/Plugin/bin/Release/net481/EpicLootContainerAccess*.dll') -Destination "$stage/BepInEx/plugins/ECA"
Copy-Item -LiteralPath (Join-Path $root 'tests/RuntimeSmoke/bin/Release/net481/EpicLootContainerAccess.RuntimeSmoke.dll') -Destination "$stage/BepInEx/plugins/ECA"
$priorTarget = $env:DOORSTOP_TARGET_ASSEMBLY
$priorOutput = $env:ECA_SMOKE_OUTPUT
try {
    $env:DOORSTOP_TARGET_ASSEMBLY = "$stage/BepInEx/core/BepInEx.Preloader.dll"
    $env:ECA_SMOKE_OUTPUT = $stage
    $process = Start-Process -FilePath (Join-Path $gamePath 'valheim.exe') -ArgumentList @('-batchmode', '-nographics', '-savedir', ('"' + "$stage/saves" + '"'), '-logFile', ('"' + "$stage/unity.log" + '"'), '--doorstop-enabled', 'true', '--doorstop-target-assembly', ('"' + "$stage/BepInEx/core/BepInEx.Preloader.dll" + '"')) -WorkingDirectory $stage -WindowStyle Hidden -PassThru
    $process.Id | Set-Content -LiteralPath "$stage/process-id.txt"
    Write-Output "Isolated smoke process $($process.Id): $stage"
} finally {
    $env:DOORSTOP_TARGET_ASSEMBLY = $priorTarget
    $env:ECA_SMOKE_OUTPUT = $priorOutput
}
