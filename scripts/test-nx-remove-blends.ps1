param(
    [Parameter(Mandatory = $true)][string]$PartPath,
    [string]$NXInstallDir = "C:\Program Files\Siemens\DesigncenterNX2512",
    [int]$MinimumRemoved = 1,
    [ValidateRange(1, 5)][int]$RepeatCount = 1,
    [ValidateRange(0, 1000000)][int]$MinimumSecondRoundRemoved = 0,
    [switch]$CompileOnly
)
$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$partFile = (Resolve-Path -LiteralPath $PartPath).Path
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$managed = Join-Path $NXInstallDir "NXBIN\managed"
$executable = Join-Path $repositoryRoot "artifacts\NxRemoveBlendsBench.exe"
$assembly = Join-Path $repositoryRoot "bin\Release\NXRefine.dll"
& $compiler /nologo /optimize+ /platform:x64 "/out:$executable" "/r:$managed\NXOpen.dll" "/r:$managed\NXOpen.Utilities.dll" (Join-Path $repositoryRoot "tests\NxRemoveBlendsBench.cs")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($CompileOnly) { Write-Host "Compiled NX blend deletion regression."; exit 0 }
if (-not (Test-Path -LiteralPath $assembly)) { throw "Build the Release production DLL first." }
$env:UGII_BASE_DIR = $NXInstallDir
$env:PATH = (Join-Path $NXInstallDir "NXBIN") + ";" + $env:PATH
& (Join-Path $NXInstallDir "NXBIN\run_journal.exe") (Join-Path $repositoryRoot "tests\RunNxRemoveBlendsBench.cs") -args $executable $partFile $assembly $MinimumRemoved $RepeatCount $MinimumSecondRoundRemoved
exit $LASTEXITCODE
