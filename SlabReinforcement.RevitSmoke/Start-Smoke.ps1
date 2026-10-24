param(
    [string]$SourceModel = 'C:\Users\Misha\Desktop\Армирование плит\Тест.rvt',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runRoot = Join-Path $workspace 'artifacts\revit-smoke'
$binRoot = Join-Path $PSScriptRoot 'bin\Release\net48'
$executable = 'C:\Program Files\Autodesk\Revit 2023\Revit.exe'
if (-not (Test-Path -LiteralPath $SourceModel -PathType Leaf)) { throw 'Source model is missing.' }
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Revit 2023 is not installed.' }
if (-not $SkipBuild) {
    dotnet build (Join-Path $PSScriptRoot 'SlabReinforcement.RevitSmoke.csproj') -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Smoke build failed.' }
}
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$processFile = Join-Path $runRoot 'process-id.txt'
if (Test-Path -LiteralPath $processFile) {
    $previous = Get-Process -Id ([int](Get-Content -LiteralPath $processFile)) -ErrorAction SilentlyContinue
    if ($previous -and $previous.Path -eq $executable) { throw 'The previous test Revit process is still running.' }
}
$productRoot = Join-Path $workspace 'SlabReinforcement\bin\R2023'
$runtimeNames = @('SlabReinforcement.Core.dll', 'SlabReinforcement.Dxf.dll', 'NetTopologySuite.dll', 'System.Buffers.dll', 'System.Memory.dll', 'System.Numerics.Vectors.dll', 'System.Runtime.CompilerServices.Unsafe.dll', 'System.Text.Encoding.CodePages.dll')
foreach ($name in $runtimeNames + @('SlabReinforcement.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $productRoot $name) -PathType Leaf)) { throw "Build the R2023 product first: missing $name" }
}
$productDependencies = @{}
foreach ($name in $runtimeNames) {
    $from = Join-Path $productRoot $name
    $to = Join-Path $binRoot $name
    Copy-Item -LiteralPath $from -Destination $to -Force
    $expected = (Get-FileHash -LiteralPath $from -Algorithm SHA256).Hash
    if ((Get-FileHash -LiteralPath $to -Algorithm SHA256).Hash -ne $expected) { throw "Runtime dependency copy mismatch: $name" }
    $productDependencies[$name] = $expected
}
if (Test-Path -LiteralPath (Join-Path $runRoot 'run.log')) {
    $archive = Join-Path $runRoot ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $archive | Out-Null
    foreach ($name in @('run.log', 'progress.json', 'results.json', 'completed.flag', 'environment.json', 'render-results.json', 'render-completed.flag')) {
        $path = Join-Path $runRoot $name
        if (Test-Path -LiteralPath $path) { Move-Item -LiteralPath $path -Destination $archive }
    }
    foreach ($demo in Get-ChildItem -LiteralPath $runRoot -File -Filter 'MVP-*') {
        Move-Item -LiteralPath $demo.FullName -Destination $archive
    }
}
$renderFlag = Join-Path $runRoot 'render-only.flag'
if (Test-Path -LiteralPath $renderFlag) { Remove-Item -LiteralPath $renderFlag }
$copy = Join-Path $runRoot 'input.rvt'
if ([IO.Path]::GetFullPath($SourceModel) -eq [IO.Path]::GetFullPath($copy)) { throw 'Source must be distinct from the temporary test copy.' }
Copy-Item -LiteralPath $SourceModel -Destination $copy -Force
$assembly = Join-Path $binRoot 'SlabReinforcement.RevitSmoke.dll'
$manifest = Join-Path $binRoot 'CodexSlabReinforcementSmoke.addin'
$content = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns><AddIn Type="Application"><Name>Slab reinforcement temporary smoke test</Name><Assembly>$assembly</Assembly><AddInId>DB0C7F38-BC2A-431F-A857-AD910EE6E4AE</AddInId><FullClassName>SlabReinforcement.RevitSmoke.SmokeApplication</FullClassName><VendorId>CDXS</VendorId><VendorDescription>Local temporary smoke tests</VendorDescription></AddIn></RevitAddIns>
"@
[IO.File]::WriteAllText($manifest, $content)
$journal = Join-Path $binRoot 'smoke.txt'
$journalText = @'
' Slab reinforcement local test journal
' 0:< 'C 24-Oct-2026 03:15:00.000;
' Build: 20220503_1030(x64)
' Branch: RELEASE_2023.0.1
Dim Jrn
Set Jrn = CrsJournalScript
Jrn.Directive "DebugMode", "PerformAutomaticActionInErrorDialog", 0
Jrn.Directive "DebugMode", "PermissiveJournal", 1
Jrn.RibbonEvent "TabActivated:Add-Ins"
'@
[IO.File]::WriteAllText($journal, $journalText, [Text.Encoding]::ASCII)
$binaries = @{}
foreach ($name in @('SlabReinforcement.RevitSmoke.dll', 'SlabReinforcement.Core.dll', 'SlabReinforcement.Dxf.dll')) {
    $binaries[$name] = (Get-FileHash -LiteralPath (Join-Path $binRoot $name) -Algorithm SHA256).Hash
}
@{ source = $SourceModel; sha256 = (Get-FileHash -LiteralPath $SourceModel -Algorithm SHA256).Hash; copy = $copy; manifest = $manifest; journal = $journal; executable = $executable; binarySha256 = $binaries; productPath = (Join-Path $productRoot 'SlabReinforcement.dll'); productSha256 = (Get-FileHash -LiteralPath (Join-Path $productRoot 'SlabReinforcement.dll') -Algorithm SHA256).Hash; productDependencies = $productDependencies } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runRoot 'environment.json') -Encoding utf8
$process = Start-Process -FilePath $executable -ArgumentList ('"' + $journal + '"') -WorkingDirectory $binRoot -WindowStyle Hidden -PassThru
$process.Id | Set-Content -LiteralPath $processFile
[pscustomobject]@{ ProcessId = $process.Id; Results = (Join-Path $runRoot 'results.json'); Log = (Join-Path $runRoot 'run.log') }
