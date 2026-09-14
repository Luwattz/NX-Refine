param(
    [string]$NXInstallDir = "C:\Program Files\Siemens\DesigncenterNX2512",
    [switch]$CompileOnly
)
$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$managed = Join-Path $NXInstallDir "NXBIN\managed"
$outputDirectory = Join-Path $repositoryRoot "artifacts"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$executable = Join-Path $outputDirectory "NxGapProjectionTests.exe"
$sources = @(
    (Join-Path $repositoryRoot "tests\NxGapProjectionTests.cs"),
    (Join-Path $repositoryRoot "src\NXRefine\Analysis\GapCriteria.cs")
)
& $compiler /nologo /optimize+ /platform:x64 "/out:$executable" "/r:$managed\NXOpen.dll" "/r:$managed\NXOpen.UF.dll" "/r:$managed\NXOpen.Utilities.dll" $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($CompileOnly) { Write-Host "Compiled NX projection integration tests."; exit 0 }
$env:UGII_BASE_DIR = $NXInstallDir
$env:PATH = (Join-Path $NXInstallDir "NXBIN") + ";" + $env:PATH
& (Join-Path $NXInstallDir "NXBIN\run_journal.exe") (Join-Path $repositoryRoot "tests\RunNxGapProjectionJournal.cs") -args $executable
exit $LASTEXITCODE
