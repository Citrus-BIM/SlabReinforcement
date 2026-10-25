param([Parameter(Mandatory=$true)][string]$Model)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$revitExe='C:\Program Files\Autodesk\Revit 2025\Revit.exe'
$productAssembly=Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2025\RibbonCITRUSData\SlabReinforcement\SlabReinforcement.dll'
foreach($item in @($Model,$revitExe,$productAssembly)){if(-not(Test-Path -LiteralPath $item -PathType Leaf)){throw "Missing input: $item"}}
$productHash=(Get-FileHash -LiteralPath $productAssembly -Algorithm SHA256).Hash
if($productHash -ne '5956EF335DFD2F562C9AE3840584151E4BBE48931FC3E0ED93BE0F71A2E9864D'){throw 'Installed product DLL differs from exact requested build'}
dotnet build (Join-Path $PSScriptRoot 'SlabReinforcement.PluginUi2025.csproj') -c Release --nologo -v minimal
if($LASTEXITCODE -ne 0){throw 'Bootstrap build failed'}
$stateRoot=Join-Path $workspace 'artifacts\plugin-ui-bootstrap-2025'
New-Item -ItemType Directory -Path $stateRoot -Force | Out-Null
$runRoot=Join-Path $stateRoot ('run-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
if(Test-Path -LiteralPath $runRoot){throw 'Run folder exists'}
New-Item -ItemType Directory -Path $runRoot | Out-Null
$assembly=Join-Path $runRoot 'SlabReinforcement.PluginUi2025.dll'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\SlabReinforcement.PluginUi2025.dll') -Destination $assembly
[IO.File]::WriteAllText((Join-Path $runRoot 'model-path.txt'),(Resolve-Path -LiteralPath $Model).Path)
$manifest=@"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
 <AddIn Type="Application"><Name>Open actual plugin test model 2025</Name><Assembly>$assembly</Assembly><AddInId>773FC032-7A0D-4AB7-B9D6-9BA2592D4B43</AddInId><FullClassName>SlabReinforcement.PluginUi2025.BootstrapApplication</FullClassName><VendorId>CDXS</VendorId></AddIn>
 <AddIn Type="Command"><Name>Slab Reinforcement installed product test</Name><Text>Армирование плит — установленный плагин</Text><Assembly>$productAssembly</Assembly><AddInId>CA59D453-E093-432B-AB2F-7DE919A991CB</AddInId><FullClassName>SlabReinforcement.SlabReinforcementCommand</FullClassName><VendorId>CDXS</VendorId></AddIn>
 <AddIn Type="Command"><Name>Read-only actual plugin result audit</Name><Text>Проверить результат — без изменений</Text><Assembly>$assembly</Assembly><AddInId>DA521EEB-694B-4F8F-BA7A-DB57CFB3D45B</AddInId><FullClassName>SlabReinforcement.PluginUi2025.AuditCommand</FullClassName><VendorId>CDXS</VendorId></AddIn>
</RevitAddIns>
"@
[IO.File]::WriteAllText((Join-Path $runRoot 'PluginUi2025.addin'),$manifest)
$journalText=@'
' Open test document; actual installed product is executed manually through Revit UI.
' 0:< 'C 24-Oct-2026 03:15:00.000;
Dim Jrn
Set Jrn = CrsJournalScript
Jrn.Directive "DebugMode", "PerformAutomaticActionInErrorDialog", 0
Jrn.Directive "DebugMode", "PermissiveJournal", 1
Jrn.RibbonEvent "TabActivated:Add-Ins"
'@
$journal=Join-Path $runRoot 'plugin-ui-2025.txt'
[IO.File]::WriteAllText($journal,$journalText,[Text.Encoding]::ASCII)
$preexisting=@(Get-Process -Name Revit -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
@{model=(Resolve-Path -LiteralPath $Model).Path;inputSha256=(Get-FileHash -LiteralPath $Model).Hash;productAssembly=$productAssembly;productSha256=$productHash;preexistingRevitProcessIds=$preexisting;bootstrapOnly='OpenAndActivateDocument; no model writes and no product execution';auditCommand='Read-only collectors and centreline readback only'} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'environment.json') -Encoding utf8
$process=Start-Process -FilePath $revitExe -ArgumentList ('"'+$journal+'"') -WorkingDirectory $runRoot -WindowStyle Hidden -PassThru
$process.Id | Set-Content -LiteralPath (Join-Path $runRoot 'process-id.txt')
$runRoot | Set-Content -LiteralPath (Join-Path $stateRoot 'last-run-directory.txt')
[pscustomobject]@{ProcessId=$process.Id;RunDirectory=$runRoot;ReadyFile=(Join-Path $runRoot 'ready.json');PreexistingRevitIds=$preexisting} | ConvertTo-Json
