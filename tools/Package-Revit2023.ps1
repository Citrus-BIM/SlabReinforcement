#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Destination,
    [string]$DemoModelPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
$sourceRoot = Join-Path $projectRoot 'SlabReinforcement\bin\R2023'
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $artifactRoot 'mvp-revit2023-u-only'
}
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$artifactPrefix = $artifactRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $destinationRoot.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package output must be a new directory under this workspace artifacts directory. Installation paths are not accepted.'
}
if (Test-Path -LiteralPath $destinationRoot) {
    throw "Destination already exists; no files were changed: $destinationRoot"
}

# Explicit runtime allowlist: no Revit API, debug symbols, settings, test runners or arbitrary stale DLLs.
$runtimeFiles = @(
    'SlabReinforcement.dll',
    'SlabReinforcement.Core.dll',
    'SlabReinforcement.Dxf.dll',
    'NetTopologySuite.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll',
    'System.Text.Encoding.CodePages.dll'
)
# Third-party notices accompany the runtime as embedded resources in Core/Dxf.
$documents = @(
    'docs\deployment\revit2023-mvp.md',
    'docs\validation\2026-10-24-mvp-recheck.md',
    'docs\validation\2026-10-24-revit-smoke.md',
    'docs\validation\2026-10-24-mvp-engineering-scope.md',
    'docs\validation\2026-10-24-mvp-core-audit.md',
    'docs\research\2026-10-24-slab-zone-reinforcement-guidance.md'
)
$folderName = 'SlabReinforcement.MVP.2023'
$manifestName = 'SlabReinforcement.MVP.2023.addin'
$addInId = '3342DBE0-455B-42F4-9F8D-41C42E938DF5'
$entryPoint = 'SlabReinforcement.SlabReinforcementCommand'
$copies = @()
foreach ($relative in $runtimeFiles) {
    $copies += [pscustomobject]@{ Source = (Join-Path $sourceRoot $relative); Relative = (Join-Path $folderName $relative) }
}
foreach ($relative in $documents) {
    $copies += [pscustomobject]@{ Source = (Join-Path $projectRoot $relative); Relative = $relative }
}
if (-not [string]::IsNullOrWhiteSpace($DemoModelPath)) {
    $demoSource = [IO.Path]::GetFullPath($DemoModelPath)
    if ([IO.Path]::GetExtension($demoSource) -ine '.rvt') { throw 'DemoModelPath must name an existing .rvt file.' }
    $copies += [pscustomobject]@{ Source = $demoSource; Relative = 'demo\MVP-demo.rvt' }
}

# Complete preflight before creating any output. Packaging neither builds nor installs the add-in.
foreach ($copy in $copies) {
    if (-not (Test-Path -LiteralPath $copy.Source -PathType Leaf)) { throw "Required file is missing: $($copy.Source)" }
}
$commandSource = [IO.File]::ReadAllText((Join-Path $projectRoot 'SlabReinforcement\SlabReinforcementCommand.cs'))
if ($commandSource -notmatch 'namespace\s+SlabReinforcement\b' -or
    $commandSource -notmatch 'class\s+SlabReinforcementCommand\s*:\s*IExternalCommand\b') {
    throw 'The command entry point no longer matches the package manifest.'
}
$assemblyVersions = [ordered]@{}
foreach ($relative in $runtimeFiles | Where-Object { $_.EndsWith('.dll', [StringComparison]::OrdinalIgnoreCase) }) {
    $assemblyName = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $sourceRoot $relative))
    if ($assemblyName.Name -ne [IO.Path]::GetFileNameWithoutExtension($relative)) { throw "Unexpected assembly identity: $relative" }
    $assemblyVersions[$relative] = $assemblyName.Version.ToString()
}
$sourceInventory = @($copies | ForEach-Object {
    $item = Get-Item -LiteralPath $_.Source
    [pscustomobject]@{
        path = $_.Relative.Replace('\', '/')
        length = $item.Length
        sourceLastWriteUtc = $item.LastWriteTimeUtc.ToString('o')
        sha256 = (Get-FileHash -LiteralPath $_.Source -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})

New-Item -ItemType Directory -Path $destinationRoot | Out-Null
foreach ($copy in $copies) {
    $target = Join-Path $destinationRoot $copy.Relative
    $parent = [IO.Path]::GetDirectoryName($target)
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
    [IO.File]::Copy($copy.Source, $target, $false)
}
$utf8 = New-Object Text.UTF8Encoding($false)
$manifest = @"
<?xml version="1.0" encoding="utf-8" standalone="no"?>
<RevitAddIns>
  <AddIn Type="Command">
    <Assembly>.\$folderName\SlabReinforcement.dll</Assembly>
    <AddInId>$addInId</AddInId>
    <FullClassName>$entryPoint</FullClassName>
    <Text>Армирование плит — MVP</Text>
    <VendorId>CTRS</VendorId>
    <VendorDescription>Local slab reinforcement MVP</VendorDescription>
    <VisibilityMode>NotVisibleInFamily</VisibilityMode>
    <VisibilityMode>NotVisibleWhenNoActiveDocument</VisibilityMode>
    <AllowLoadIntoExistingSession>false</AllowLoadIntoExistingSession>
  </AddIn>
</RevitAddIns>
"@
[IO.File]::WriteAllText((Join-Path $destinationRoot $manifestName), $manifest, $utf8)
$readme = @'
# Армирование плит — локальный пакет для Revit 2023, П-образное обрамление

[Установка, запуск и демонстрационная модель](docs/deployment/revit2023-mvp.md).
[Повторная проверка и исправления](docs/validation/2026-10-24-mvp-recheck.md).

Установка выполняется вручную. Пакет не изменяет установленный CITRUS.
Состав и SHA256 исходных файлов записаны в package-info.json; SHA256 файлов пакета — в SHA256SUMS.txt.
Наличие PACKAGE_READY.txt означает завершённую упаковку, а не прохождение испытаний Revit или расчётную готовность плиты.
'@
[IO.File]::WriteAllText((Join-Path $destinationRoot 'README-RU.md'), $readme, $utf8)
$metadata = [ordered]@{
    packagedUtc = [DateTime]::UtcNow.ToString('o')
    targetRevit = 2023
    configuration = 'R2023'
    perimeterMode = 'U-only'
    sourceBuild = 'SlabReinforcement/bin/R2023'
    entryPoint = $entryPoint
    addInId = $addInId
    installed = $false
    buildOrTestsRunByPackager = $false
    demoIncluded = (-not [string]::IsNullOrWhiteSpace($DemoModelPath))
    assemblyVersions = $assemblyVersions
    copiedFiles = $sourceInventory
}
[IO.File]::WriteAllText((Join-Path $destinationRoot 'package-info.json'), ($metadata | ConvertTo-Json -Depth 6), $utf8)

# Compare all copied bytes to the preflight snapshot; a concurrent build must not yield a mixed package.
foreach ($record in $sourceInventory) {
    $targetHash = (Get-FileHash -LiteralPath (Join-Path $destinationRoot $record.path) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($targetHash -ne $record.sha256) { throw "Source changed during packaging or copy verification failed: $($record.path). The partial output is preserved; use a new destination." }
}
$parsedManifest = [xml][IO.File]::ReadAllText((Join-Path $destinationRoot $manifestName))
if ($parsedManifest.RevitAddIns.AddIn.Type -ne 'Command' -or $parsedManifest.RevitAddIns.AddIn.FullClassName -ne $entryPoint -or
    -not (Test-Path -LiteralPath (Join-Path $destinationRoot $parsedManifest.RevitAddIns.AddIn.Assembly) -PathType Leaf)) {
    throw 'Manifest validation failed. The partial output is preserved.'
}
$hashLines = @(Get-ChildItem -LiteralPath $destinationRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($destinationRoot.Length + 1).Replace('\', '/')
    (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $relative
})
[IO.File]::WriteAllLines((Join-Path $destinationRoot 'SHA256SUMS.txt'), $hashLines, $utf8)
[IO.File]::WriteAllText((Join-Path $destinationRoot 'PACKAGE_READY.txt'), 'Packaging and SHA256 verification completed. No installation, build or Revit test was performed by this script.', $utf8)
Write-Output "Package ready: $destinationRoot"
