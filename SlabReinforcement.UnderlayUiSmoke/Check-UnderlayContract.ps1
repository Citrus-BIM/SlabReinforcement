param([Parameter(Mandatory=$true)][string]$Product,[Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference='Stop'
$productFull=(Resolve-Path -LiteralPath $Product).Path
$productFolder=Split-Path -Parent $productFull
$resolver=[ResolveEventHandler]{param($sender,$eventArgs)
    $name=([Reflection.AssemblyName]::new($eventArgs.Name)).Name+'.dll'
    foreach($folder in @($productFolder,'C:\Program Files\Autodesk\Revit 2023')) {
        $candidate=Join-Path $folder $name
        if(Test-Path -LiteralPath $candidate){return [Reflection.Assembly]::ReflectionOnlyLoadFrom($candidate)}
    }
    return [Reflection.Assembly]::ReflectionOnlyLoad($eventArgs.Name)
}
[AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($resolver)
try {
    $assembly=[Reflection.Assembly]::ReflectionOnlyLoadFrom($productFull)
    $window=$assembly.GetType('SlabReinforcement.SlabReinforcementWPF',$true)
    $required=@('RequestUnderlaySelection','ConsumeUnderlaySelectionRequest','ApplyUnderlay','ResumePreview')
    $methods=@($window.GetMethods([Reflection.BindingFlags]'Public,Instance') | Select-Object -ExpandProperty Name)
    $missing=@($required | Where-Object { $methods -notcontains $_ })
    @{product=$productFull;sha256=(Get-FileHash -LiteralPath $productFull -Algorithm SHA256).Hash;contractSatisfied=($missing.Count -eq 0);missing=$missing;scope='Read-only assembly contract check; no WPF/Revit execution'} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Output -Encoding utf8
} finally {[AppDomain]::CurrentDomain.remove_ReflectionOnlyAssemblyResolve($resolver)}
