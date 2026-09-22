param(
    [string]$NXInstallDir = "C:\Program Files\Siemens\DesigncenterNX2512",
    [string]$BaselineAssembly,
    [switch]$CompileOnly
)
$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$managed = Join-Path $NXInstallDir "NXBIN\managed"
$outputDirectory = Join-Path $repositoryRoot "artifacts"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$executable = Join-Path $outputDirectory "NxClearCavitiesTests.dll"
$assembly = Join-Path $repositoryRoot "bin\Release\NXRefine.dll"
& $compiler /nologo /target:library /optimize+ /platform:x64 "/out:$executable" "/r:$managed\NXOpen.dll" "/r:$managed\NXOpen.UF.dll" "/r:$managed\NXOpen.Utilities.dll" (Join-Path $repositoryRoot "tests\NxClearCavitiesTests.cs")
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($CompileOnly) { exit 0 }
if (-not (Test-Path -LiteralPath $assembly)) { throw "Build the Release production DLL first." }
$env:UGII_BASE_DIR = $NXInstallDir
$env:PATH = (Join-Path $NXInstallDir "NXBIN") + ";" + $env:PATH
$journalArgs = @($executable, $assembly)
if ($BaselineAssembly) { $journalArgs += (Resolve-Path -LiteralPath $BaselineAssembly).Path }
& (Join-Path $NXInstallDir "NXBIN\run_journal.exe") (Join-Path $repositoryRoot "tests\RunNxClearCavitiesTests.cs") -args @journalArgs
exit $LASTEXITCODE
