param(
    [string]$PartPath = 'C:\Users\chimp\Desktop\Geometry\housing.prt',
    [ValidateRange(1,100)][int]$MaximumRegions = 30,
    [ValidateRange(0,20)][int]$MaximumTrials = 4,
    [ValidateRange(10,600)][int]$TimeLimitSeconds = 120,
    [switch]$RequirePrimaryFailure,
    [string]$NXInstallDir = 'C:\Program Files\Siemens\DesigncenterNX2512'
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repository ('artifacts\blend-narrow-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$executable = Join-Path $output 'NxBlendNarrowProbe.exe'
$managed = Join-Path $NXInstallDir 'NXBIN\managed'
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe /platform:x64 /optimize+ "/out:$executable" "/r:$managed\NXOpen.dll" "/r:$managed\NXOpen.UF.dll" "/r:$managed\NXOpen.Utilities.dll" /r:System.Core.dll (Join-Path $repository 'tests\NxBlendNarrowProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Narrow probe compilation failed.' }
$source = (Resolve-Path -LiteralPath $PartPath).Path
$before = (Get-FileHash -LiteralPath $source).Hash
$copy = Join-Path $output ([IO.Path]::GetFileName($source))
Copy-Item -LiteralPath $source -Destination $copy
$report = Join-Path $output 'report.txt'
$env:UGII_BASE_DIR = $NXInstallDir
$env:PATH = (Join-Path $NXInstallDir 'NXBIN') + ';' + $env:PATH
& (Join-Path $NXInstallDir 'NXBIN\run_dotnet_nxopen.exe') $executable $copy $report $MaximumRegions $MaximumTrials $TimeLimitSeconds $RequirePrimaryFailure.IsPresent.ToString()
$result = $LASTEXITCODE
if ((Get-FileHash -LiteralPath $source).Hash -ne $before) { throw 'Original part hash changed.' }
Write-Host "Report: $report"
if ($result -ne 0) { throw "Narrow probe failed: $result" }
if (-not (Select-String -LiteralPath $report -SimpleMatch 'UNDO_RESTORED=True' -Quiet)) { throw 'Missing verified undo.' }
Write-Host 'Source hash unchanged; all trials undone; input not saved.'
