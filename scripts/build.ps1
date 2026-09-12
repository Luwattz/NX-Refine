param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$NXInstallDir = $env:UGII_BASE_DIR
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($NXInstallDir)) {
    $NXInstallDir = "C:\Program Files\Siemens\NX2312"
}
$msbuild = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe"
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "MSBuild was not found. Install Visual Studio Build Tools with .NET Framework 4.8 targeting tools."
}

& $msbuild "$repositoryRoot\NXRefine.sln" /t:Rebuild /p:Configuration=$Configuration /p:NXInstallDir="$NXInstallDir" /m
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$commands = @(
    "Analyze",
    "AutoSimplify",
    "SmallFaces",
    "RemoveBlends",
    "FillHoles",
    "ClearCavities",
    "RemoveMarkings",
    "RepairSheets",
    "PatchOpenings",
    "Settings",
    "About"
)
$commandProject = Join-Path $repositoryRoot "src\NXRefine.Command\NXRefine.Command.csproj"
foreach ($command in $commands) {
    & $msbuild $commandProject /t:Rebuild /p:Configuration=$Configuration /p:NXInstallDir="$NXInstallDir" /p:AssemblyName="NXRefine.$command" /m
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Built NXRefine.dll, command entry points, and copied them to deploy\application."
