param(
    [string]$Baseline,
    [switch]$SkipBuild,
    [switch]$ReaderBaseline,
    [switch]$PathBoundaryProbe,
    [switch]$ProductionPathBands
)
$ErrorActionPreference='Stop'
$workspacePath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(-not $Baseline){$Baseline=Join-Path $workspacePath 'artifacts\single-slab-revit\run-20261024-130245\single-slab-input.rvt'}
$revitExe='C:\Program Files\Autodesk\Revit 2023\Revit.exe'
$binPath=Join-Path $PSScriptRoot 'bin\Release\net48'
foreach($requiredPath in @($Baseline,$revitExe)){if(-not(Test-Path -LiteralPath $requiredPath -PathType Leaf)){throw "Missing input: $requiredPath"}}
$runsPath=Join-Path $workspacePath 'artifacts\zone-path-smoke'
New-Item -ItemType Directory -Path $runsPath -Force | Out-Null
$lastPidPath=Join-Path $runsPath 'last-process-id.txt'
if(Test-Path -LiteralPath $lastPidPath){$prior=Get-Process -Id ([int](Get-Content -LiteralPath $lastPidPath)) -ErrorAction SilentlyContinue;if($prior -and $prior.Path -eq $revitExe){throw 'Prior zone-path harness is still running.'}}
if(-not $SkipBuild){
    $baselineProperty=if($ReaderBaseline){'true'}else{'false'}
    dotnet build (Join-Path $PSScriptRoot 'SlabReinforcement.ZonePathSmoke.csproj') -c Release --nologo -v minimal "-p:ReaderBaseline=$baselineProperty"
    if($LASTEXITCODE -ne 0){throw 'Harness build failed.'}
}
$runPath=Join-Path $runsPath ('run-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
if(Test-Path -LiteralPath $runPath){throw 'Run directory already exists.'}
New-Item -ItemType Directory -Path $runPath | Out-Null
if($PathBoundaryProbe){[IO.File]::WriteAllText((Join-Path $runPath 'path-boundary-probe.flag'),'Opt-in disposable native boundary experiment')}
if($ProductionPathBands){[IO.File]::WriteAllText((Join-Path $runPath 'production-path-bands.flag'),'Opt-in production writer persistence verification')}
Copy-Item -LiteralPath $Baseline -Destination (Join-Path $runPath 'input.rvt')
$baselineHash=(Get-FileHash -LiteralPath $Baseline -Algorithm SHA256).Hash
if((Get-FileHash -LiteralPath (Join-Path $runPath 'input.rvt') -Algorithm SHA256).Hash -ne $baselineHash){throw 'Copy verification failed.'}
[IO.File]::WriteAllText((Join-Path $binPath 'run-directory.txt'),$runPath)
$assemblyPath=Join-Path $binPath 'SlabReinforcement.ZonePathSmoke.dll'
$manifestPath=Join-Path $binPath 'CodexZonePathSmoke.addin'
$manifest=@"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns><AddIn Type="Application"><Name>Zone Path regression</Name><Assembly>$assemblyPath</Assembly><AddInId>A6EA7456-B985-41B5-9D62-3D2BB2B5EA02</AddInId><FullClassName>SlabReinforcement.ZonePathSmoke.ZonePathApplication</FullClassName><VendorId>CDXS</VendorId></AddIn></RevitAddIns>
"@
[IO.File]::WriteAllText($manifestPath,$manifest)
$journalPath=Join-Path $binPath 'zone-path-smoke.txt'
$journal=@'
' Zone Path test journal
' 0:< 'C 25-Oct-2026 03:15:00.000;
' Build: 20220503_1030(x64)
' Branch: RELEASE_2023.0.1
Dim Jrn
Set Jrn = CrsJournalScript
Jrn.Directive "DebugMode", "PerformAutomaticActionInErrorDialog", 0
Jrn.Directive "DebugMode", "PermissiveJournal", 1
Jrn.RibbonEvent "TabActivated:Add-Ins"
'@
[IO.File]::WriteAllText($journalPath,$journal,[Text.Encoding]::ASCII)
$existingIds=@(Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
@{baseline=$Baseline;baselineSha256=$baselineHash;harnessSha256=(Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash;readerBaseline=[bool]$ReaderBaseline;pathBoundaryProbe=[bool]$PathBoundaryProbe;preexistingRevitIds=$existingIds} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runPath 'environment.json') -Encoding utf8
$process=Start-Process -FilePath $revitExe -ArgumentList ('"'+$journalPath+'"') -WorkingDirectory $binPath -WindowStyle Hidden -PassThru
if($existingIds -contains $process.Id){throw 'Unexpected reuse of an existing Revit process.'}
$process.Id | Set-Content -LiteralPath $lastPidPath
$process.Id | Set-Content -LiteralPath (Join-Path $runPath 'process-id.txt')
$runPath | Set-Content -LiteralPath (Join-Path $runsPath 'last-run-directory.txt')
[pscustomobject]@{ProcessId=$process.Id;RunDirectory=$runPath;ReaderBaseline=[bool]$ReaderBaseline}
