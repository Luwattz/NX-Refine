param(
    [string]$PartPath = 'C:\Users\chimp\Desktop\Geometry\housing.prt',
    [ValidateRange(2,24)][int]$Groups = 12,
    [ValidateRange(30,900)][int]$Seconds = 180,
    [string]$NXInstallDir = 'C:\Program Files\Siemens\DesigncenterNX2512',
    [switch]$CompileOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root ('artifacts\blend-batch-probe-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$managed = Join-Path $NXInstallDir 'NXBIN\managed'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $output 'NxBlendBatchProbe.exe'
& $compiler /nologo /target:exe /optimize+ /platform:x64 "/out:$executable" "/r:$managed\NXOpen.dll" "/r:$managed\NXOpen.UF.dll" "/r:$managed\NXOpen.Utilities.dll" /r:System.Core.dll (Join-Path $root 'tests\NxBlendBatchProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native batch probe compilation failed.' }
if ($CompileOnly) { Write-Host $executable; return }
$original = (Resolve-Path -LiteralPath $PartPath).Path
$copy = Join-Path $output ([IO.Path]::GetFileName($original))
Copy-Item -LiteralPath $original -Destination $copy
$before = (Get-FileHash -LiteralPath $original).Hash
$env:UGII_BASE_DIR = $NXInstallDir
$env:PATH = (Join-Path $NXInstallDir 'NXBIN') + ';' + $env:PATH
$report = Join-Path $output 'report.txt'
& (Join-Path $NXInstallDir 'NXBIN\run_dotnet_nxopen.exe') $executable $copy $report $Groups.ToString() $Seconds.ToString()
$result = $LASTEXITCODE
Write-Host "Report: $report"
if ((Get-FileHash -LiteralPath $original).Hash -ne $before) { throw 'Original part hash changed.' }
if ($result -ne 0) { throw "Native probe failed: $result" }
if (-not (Select-String -LiteralPath $report -SimpleMatch 'UNDO_RESTORED=True' -Quiet)) { throw 'Undo verification missing.' }
Write-Host 'Original SHA256 unchanged; copy unsaved; undo verified.'
