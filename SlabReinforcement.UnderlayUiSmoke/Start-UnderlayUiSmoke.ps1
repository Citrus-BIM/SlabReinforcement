param([Parameter(Mandatory=$true)][string]$Baseline,[switch]$SkipBuild,[switch]$BackgroundAudit,[switch]$LayerAlignmentAudit,[switch]$NativeAreaProbe,[switch]$AreaTemplateInventory,[switch]$NativePathProbe,[switch]$NewTemplateNative,[switch]$CleanPluginTest,
    [Alias('NewCurrentPluginPlan')][switch]$CurrentPluginPlan,[string]$ExpectedProductSha,[string]$ProductDirectory)
$ErrorActionPreference='Stop'
$taskModes=@($BackgroundAudit,$LayerAlignmentAudit,$NativeAreaProbe,$AreaTemplateInventory,$NativePathProbe,$NewTemplateNative,$CurrentPluginPlan) | Where-Object {$_.IsPresent}
if($taskModes.Count -gt 1){throw 'Select only one probe mode.'}
if($CleanPluginTest -and -not $NewTemplateNative){throw 'CleanPluginTest requires NewTemplateNative.'}
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stateRoot=Join-Path $workspace 'artifacts\underlay-ui-smoke'
$binRoot=Join-Path $PSScriptRoot 'bin\Release\net48'
$productRoot=Join-Path $workspace 'SlabReinforcement\bin\R2023'
if($ProductDirectory){$productRoot=(Resolve-Path -LiteralPath $ProductDirectory).Path}
$exe='C:\Program Files\Autodesk\Revit 2023\Revit.exe'
foreach($item in @($Baseline,$exe)){if(-not(Test-Path -LiteralPath $item -PathType Leaf)){throw "Missing input: $item"}}
if($CurrentPluginPlan -or $LayerAlignmentAudit){
    if($ExpectedProductSha -notmatch '^[a-fA-F0-9]{64}$'){throw 'CurrentPluginPlan requires the approved final R2023 SHA256 in ExpectedProductSha.'}
    $mainProduct=Join-Path $productRoot 'SlabReinforcement.dll'
    if((Get-FileHash -LiteralPath $mainProduct -Algorithm SHA256).Hash -ne $ExpectedProductSha){throw 'Production R2023 DLL does not match ExpectedProductSha.'}
}
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$pidFile=Join-Path $stateRoot 'last-process-id.txt'
if(Test-Path -LiteralPath $pidFile){$previous=Get-Process -Id ([int](Get-Content -LiteralPath $pidFile)) -ErrorAction SilentlyContinue;if($previous -and $previous.Path -eq $exe){throw 'Prior underlay UI smoke is still running.'}}
if(-not $SkipBuild){dotnet build (Join-Path $PSScriptRoot 'SlabReinforcement.UnderlayUiSmoke.csproj') -c Release "-p:ProductRoot=$productRoot" --nologo -v minimal;if($LASTEXITCODE -ne 0){throw 'Build failed.'}}
$runRoot=Join-Path $stateRoot ('run-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
if(Test-Path -LiteralPath $runRoot){throw 'Run directory exists.'}
New-Item -ItemType Directory -Path $runRoot | Out-Null
[IO.File]::WriteAllText((Join-Path $runRoot 'product-path.txt'),(Join-Path $productRoot 'SlabReinforcement.dll'))
if($CurrentPluginPlan -or $LayerAlignmentAudit){
    if($CurrentPluginPlan){[IO.File]::WriteAllText((Join-Path $runRoot 'current-plugin-plan.flag'),'ACTUAL_PRODUCTION_SESSION_WRITER_API_INTEGRATION_COPY_ONLY_NO_UI_PICKS')}
    [IO.File]::WriteAllText((Join-Path $runRoot 'expected-product-sha256.txt'),$ExpectedProductSha)
    $harnessSource=Join-Path $binRoot 'SlabReinforcement.UnderlayUiSmoke.dll'
    $binRoot=Join-Path $runRoot 'runtime'
    New-Item -ItemType Directory -Path $binRoot | Out-Null
    Copy-Item -LiteralPath $harnessSource -Destination (Join-Path $binRoot 'SlabReinforcement.UnderlayUiSmoke.dll')
    Copy-Item -LiteralPath $mainProduct -Destination (Join-Path $binRoot 'SlabReinforcement.dll')
    if((Get-FileHash -LiteralPath (Join-Path $binRoot 'SlabReinforcement.dll') -Algorithm SHA256).Hash -ne $ExpectedProductSha){throw 'Staged production DLL hash mismatch.'}
    if($LayerAlignmentAudit){[IO.File]::WriteAllText((Join-Path $runRoot 'product-path.txt'),(Join-Path $binRoot 'SlabReinforcement.dll'))}
}
if($LayerAlignmentAudit){[IO.File]::WriteAllText((Join-Path $runRoot 'layer-alignment-audit.flag'),'READ_ONLY_ACTUAL_WPF_CELL_GEOMETRY_NO_WRITES')}
if($BackgroundAudit){[IO.File]::WriteAllText((Join-Path $runRoot 'background-capture-audit.flag'),'READ_ONLY_AUDIT_NO_PRODUCTION_FIXES')}
if($NativeAreaProbe){[IO.File]::WriteAllText((Join-Path $runRoot 'native-area-probe.flag'),'NATIVE_AREA_POC_COPY_ONLY_NO_PRODUCTION_FIXES')}
if($NativePathProbe){[IO.File]::WriteAllText((Join-Path $runRoot 'native-path-probe.flag'),'NATIVE_PATH_POC_ONE_STRAIGHT_SEGMENT_COPY_ONLY')}
if($AreaTemplateInventory){[IO.File]::WriteAllText((Join-Path $runRoot 'area-template-inventory.flag'),'READ_ONLY_TEMPLATE_AND_NATIVE_INSTANCE_METADATA')}
$inputName='input.rvt'
if($NewTemplateNative){
    $inputName='template.rte'
    [IO.File]::WriteAllText((Join-Path $runRoot 'new-template-native.flag'),'NEW_DOCUMENT_FROM_OFFICIAL_TEMPLATE')
    if($CleanPluginTest){[IO.File]::WriteAllText((Join-Path $runRoot 'clean-plugin-test.flag'),'CLEAN_FLOOR_ONLY_FOR_REAL_PLUGIN_UI_TEST')}
    Copy-Item -LiteralPath (Join-Path $workspace 'artifacts\single-slab\footprint.json') -Destination (Join-Path $runRoot 'footprint.json')
}
Copy-Item -LiteralPath $Baseline -Destination (Join-Path $runRoot $inputName)
$baselineSha=(Get-FileHash -LiteralPath $Baseline -Algorithm SHA256).Hash
if((Get-FileHash -LiteralPath (Join-Path $runRoot $inputName) -Algorithm SHA256).Hash -ne $baselineSha){throw 'Input copy mismatch.'}
$dependencies=@{}
foreach($name in @('SlabReinforcement.Core.dll','SlabReinforcement.Dxf.dll','NetTopologySuite.dll','System.Buffers.dll','System.Memory.dll','System.Numerics.Vectors.dll','System.Runtime.CompilerServices.Unsafe.dll','System.Text.Encoding.CodePages.dll')){
    $source=Join-Path $productRoot $name;Copy-Item -LiteralPath $source -Destination (Join-Path $binRoot $name) -Force
    $dependencies[$name]=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    if((Get-FileHash -LiteralPath (Join-Path $binRoot $name) -Algorithm SHA256).Hash -ne $dependencies[$name]){throw "Dependency mismatch: $name"}
}
[IO.File]::WriteAllText((Join-Path $binRoot 'run-directory.txt'),$runRoot)
$assembly=Join-Path $binRoot 'SlabReinforcement.UnderlayUiSmoke.dll'
$manifest=Join-Path $binRoot 'CodexUnderlayUiSmoke.addin'
$manifestText=@"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns><AddIn Type="Application"><Name>Underlay modal smoke</Name><Assembly>$assembly</Assembly><AddInId>62649F5F-C113-47F4-A470-076491A42142</AddInId><FullClassName>SlabReinforcement.UnderlayUiSmoke.UnderlayApplication</FullClassName><VendorId>CDXS</VendorId></AddIn></RevitAddIns>
"@
[IO.File]::WriteAllText($manifest,$manifestText)
$journal=Join-Path $binRoot 'underlay-ui-smoke.txt'
$journalText=@'
' Read-only underlay modal smoke
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
$preexisting=@(Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
@{baseline=(Resolve-Path -LiteralPath $Baseline).Path;baselineSha256=$baselineSha;dependencies=$dependencies;productPath=(Join-Path $productRoot 'SlabReinforcement.dll');productSha256=(Get-FileHash -LiteralPath (Join-Path $productRoot 'SlabReinforcement.dll') -Algorithm SHA256).Hash;harnessSha256=(Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash;preexistingRevitProcessIds=$preexisting;manifest=$manifest;journal=$journal} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'environment.json') -Encoding utf8
$process=Start-Process -FilePath $exe -ArgumentList ('"'+$journal+'"') -WorkingDirectory $binRoot -WindowStyle Hidden -PassThru
if($preexisting -contains $process.Id){throw 'Unexpected process reuse.'}
$process.Id | Set-Content -LiteralPath $pidFile
$process.Id | Set-Content -LiteralPath (Join-Path $runRoot 'process-id.txt')
$runRoot | Set-Content -LiteralPath (Join-Path $stateRoot 'last-run-directory.txt')
[pscustomobject]@{ProcessId=$process.Id;RunDirectory=$runRoot;PreexistingRevitIds=$preexisting}
