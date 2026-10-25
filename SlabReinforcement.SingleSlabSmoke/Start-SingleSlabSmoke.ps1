param(
    [Parameter(Mandatory=$true)][string]$Footprint,
    [Parameter(Mandatory=$true)][string]$Dxf,
    [string]$Template = 'P:\Шаблоны\02_Шаблоны Revit\Шаблон КЖ\Шаблон КЖ (Weandrevit 2021).rte',
    [string]$Baseline,
    [switch]$BackgroundOnly,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$binRoot = Join-Path $PSScriptRoot 'bin\Release\net48'
$productRoot = Join-Path $workspace 'SlabReinforcement\bin\R2023'
$exe = 'C:\Program Files\Autodesk\Revit 2023\Revit.exe'
$requiredPaths = @($Footprint,$Dxf,$exe)
if ($Baseline) { $requiredPaths += $Baseline } else { $requiredPaths += $Template }
foreach ($path in $requiredPaths) { if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing input: $path" } }
$stateRoot = Join-Path $workspace 'artifacts\single-slab-revit'
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$processFile = Join-Path $stateRoot 'last-process-id.txt'
if (Test-Path -LiteralPath $processFile) {
    $previous = Get-Process -Id ([int](Get-Content -LiteralPath $processFile)) -ErrorAction SilentlyContinue
    if ($previous -and $previous.Path -eq $exe) { throw 'Previous single-slab test process is still running.' }
}
$existingRevitIds = @(Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
if (-not $SkipBuild) {
    dotnet build (Join-Path $PSScriptRoot 'SlabReinforcement.SingleSlabSmoke.csproj') -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$runRoot = Join-Path $stateRoot ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $runRoot) { throw 'Run directory already exists.' }
New-Item -ItemType Directory -Path $runRoot | Out-Null
$inputs = @{}
$inputItems = @(@{name='footprint.json';source=$Footprint},@{name='source.dxf';source=$Dxf})
if ($Baseline) { $inputItems += @{name='resume-input.rvt';source=$Baseline} } else { $inputItems += @{name='template.rte';source=$Template} }
foreach ($item in $inputItems) {
    $destination = Join-Path $runRoot $item.name
    Copy-Item -LiteralPath $item.source -Destination $destination
    $hash = (Get-FileHash -LiteralPath $item.source -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $hash) { throw 'Input copy hash mismatch.' }
    $inputs[$item.name] = @{ source=$item.source; copy=$destination; sha256=$hash }
}
if ($BackgroundOnly) { [IO.File]::WriteAllText((Join-Path $runRoot 'background-only.flag'),'BACKGROUND_ONLY_DIAGNOSTIC') }
$dependencies = @{}
foreach ($name in @('SlabReinforcement.Core.dll','SlabReinforcement.Dxf.dll','NetTopologySuite.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Text.Encoding.CodePages.dll')) {
    $source = Join-Path $productRoot $name
    Copy-Item -LiteralPath $source -Destination (Join-Path $binRoot $name) -Force
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath (Join-Path $binRoot $name) -Algorithm SHA256).Hash -ne $hash) { throw "Dependency hash mismatch: $name" }
    $dependencies[$name] = $hash
}
[IO.File]::WriteAllText((Join-Path $binRoot 'run-directory.txt'),$runRoot)
$assembly = Join-Path $binRoot 'SlabReinforcement.SingleSlabSmoke.dll'
$manifest = Join-Path $binRoot 'CodexSingleSlabSmoke.addin'
$manifestText = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns><AddIn Type="Application"><Name>Single slab geometry test</Name><Assembly>$assembly</Assembly><AddInId>06E88F70-091B-44E8-AE73-6135A1D09FC0</AddInId><FullClassName>SlabReinforcement.SingleSlabSmoke.SingleSlabApplication</FullClassName><VendorId>CDXS</VendorId></AddIn></RevitAddIns>
"@
[IO.File]::WriteAllText($manifest,$manifestText)
$journal = Join-Path $binRoot 'single-slab-smoke.txt'
$journalText = @'
' Single slab test journal
' 0:< 'C 24-Oct-2026 03:15:00.000;
' Build: 20220503_1030(x64)
' Branch: RELEASE_2023.0.1
Dim Jrn
Set Jrn = CrsJournalScript
Jrn.Directive "DebugMode", "PerformAutomaticActionInErrorDialog", 0
Jrn.Directive "DebugMode", "PermissiveJournal", 1
Jrn.RibbonEvent "TabActivated:Add-Ins"
'@
[IO.File]::WriteAllText($journal,$journalText,[Text.Encoding]::ASCII)
@{ inputs=$inputs; dependencies=$dependencies; productPath=(Join-Path $productRoot 'SlabReinforcement.dll'); productSha256=(Get-FileHash -LiteralPath (Join-Path $productRoot 'SlabReinforcement.dll') -Algorithm SHA256).Hash; harnessSha256=(Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash; preexistingRevitProcessIds=$existingRevitIds; manifest=$manifest; journal=$journal; executable=$exe } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runRoot 'environment.json') -Encoding utf8
$proc = Start-Process -FilePath $exe -ArgumentList ('"' + $journal + '"') -WorkingDirectory $binRoot -WindowStyle Hidden -PassThru
if ($existingRevitIds -contains $proc.Id) { throw 'Unexpected reuse of a pre-existing Revit process.' }
$proc.Id | Set-Content -LiteralPath $processFile
$proc.Id | Set-Content -LiteralPath (Join-Path $runRoot 'process-id.txt')
$runRoot | Set-Content -LiteralPath (Join-Path $stateRoot 'last-run-directory.txt')
[pscustomobject]@{ ProcessId=$proc.Id; RunDirectory=$runRoot; PreexistingRevitIds=$existingRevitIds }
