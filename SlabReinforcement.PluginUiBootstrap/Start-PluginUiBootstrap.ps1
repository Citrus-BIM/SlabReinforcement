param([Parameter(Mandatory=$true)][string]$Model)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$revitExe='C:\Program Files\Autodesk\Revit 2023\Revit.exe'
$productAssembly='C:\Users\Misha\AppData\Roaming\Autodesk\Revit\Addins\2023\RibbonCITRUSData\SlabReinforcement\SlabReinforcement.dll'
foreach($item in @($Model,$revitExe,$productAssembly)){if(-not(Test-Path -LiteralPath $item -PathType Leaf)){throw "Missing input: $item"}}
$productHash=(Get-FileHash -LiteralPath $productAssembly -Algorithm SHA256).Hash
if($productHash -ne '2450739AE616F709BDC9E5F936532E2E8D05D546C77BF58E3E17293BF3320476'){throw 'Installed product DLL differs from the requested exact build.'}
dotnet build (Join-Path $PSScriptRoot 'SlabReinforcement.PluginUiBootstrap.csproj') -c Release --nologo -v minimal
if($LASTEXITCODE -ne 0){throw 'Bootstrap build failed.'}
$stateRoot=Join-Path $workspace 'artifacts\plugin-ui-bootstrap'
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$runRoot=Join-Path $stateRoot ('run-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
if(Test-Path -LiteralPath $runRoot){throw 'Run folder exists.'}
New-Item -ItemType Directory -Path $runRoot | Out-Null
$bootstrapAssembly=Join-Path $runRoot 'SlabReinforcement.PluginUiBootstrap.dll'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\Release\net48\SlabReinforcement.PluginUiBootstrap.dll') -Destination $bootstrapAssembly
[IO.File]::WriteAllText((Join-Path $runRoot 'model-path.txt'),(Resolve-Path -LiteralPath $Model).Path)
$manifest=@"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
 <AddIn Type="Application"><Name>Open actual plugin test model</Name><Assembly>$bootstrapAssembly</Assembly><AddInId>C9A48090-5B4E-440B-A52B-C246C3B4B611</AddInId><FullClassName>SlabReinforcement.PluginUiBootstrap.BootstrapApplication</FullClassName><VendorId>CDXS</VendorId></AddIn>
 <AddIn Type="Command"><Name>Slab Reinforcement installed product test</Name><Text>Армирование плит — установленный плагин</Text><Assembly>$productAssembly</Assembly><AddInId>CA59D453-E093-432B-AB2F-7DE919A991CB</AddInId><FullClassName>SlabReinforcement.SlabReinforcementCommand</FullClassName><VendorId>CDXS</VendorId></AddIn>
</RevitAddIns>
"@
[IO.File]::WriteAllText((Join-Path $runRoot 'PluginUiBootstrap.addin'),$manifest)
$journalText=@'
' Open clean test document for manual execution of the installed product command.
' 0:< 'C 24-Oct-2026 03:15:00.000;
' Build: 20220503_1030(x64)
' Branch: RELEASE_2023.0.1
Dim Jrn
Set Jrn = CrsJournalScript
Jrn.Directive "DebugMode", "PerformAutomaticActionInErrorDialog", 0
Jrn.Directive "DebugMode", "PermissiveJournal", 1
Jrn.RibbonEvent "TabActivated:Add-Ins"
'@
$journal=Join-Path $runRoot 'plugin-ui-bootstrap.txt'
[IO.File]::WriteAllText($journal,$journalText,[Text.Encoding]::ASCII)
$preexisting=@(Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
@{model=(Resolve-Path -LiteralPath $Model).Path;inputSha256=(Get-FileHash -LiteralPath $Model -Algorithm SHA256).Hash;productAssembly=$productAssembly;productSha256=$productHash;preexistingRevitProcessIds=$preexisting;bootstrapOnly='OpenAndActivateDocument, no model writes and no product execution'} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'environment.json') -Encoding utf8
$process=Start-Process -FilePath $revitExe -ArgumentList ('"'+$journal+'"') -WorkingDirectory $runRoot -WindowStyle Hidden -PassThru
$process.Id | Set-Content -LiteralPath (Join-Path $runRoot 'process-id.txt')
$runRoot | Set-Content -LiteralPath (Join-Path $stateRoot 'last-run-directory.txt')
[pscustomobject]@{ProcessId=$process.Id;RunDirectory=$runRoot;ReadyFile=(Join-Path $runRoot 'ready.json');PreexistingRevitIds=$preexisting} | ConvertTo-Json
