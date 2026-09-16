param(
    [Parameter(Mandatory = $true)]
    [string]$PartPath,
    [string]$NXInstallDir = "C:\Program Files\Siemens\DesigncenterNX2512",
    [string]$PluginPath
)
$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $PluginPath) { $PluginPath = Join-Path $repositoryRoot 'deploy\application\NXRefine.dll' }
$PluginPath = (Resolve-Path -LiteralPath $PluginPath).Path
$PartPath = (Resolve-Path -LiteralPath $PartPath).Path
$outputDirectory = Join-Path $repositoryRoot ('artifacts\markings-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDirectory | Out-Null
$fixture = Join-Path $outputDirectory 'fixture.prt'
Copy-Item -LiteralPath $PartPath -Destination $fixture
$originalHash = (Get-FileHash -LiteralPath $PartPath).Hash
$report = Join-Path $outputDirectory 'report.txt'
$testDll = Join-Path $outputDirectory 'NxMarkingsModelRegression.dll'
$managed = Join-Path $NXInstallDir 'NXBIN\managed'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:library /platform:x64 "/out:$testDll" "/r:$managed\NXOpen.dll" "/r:$managed\NXOpen.UF.dll" "/r:$managed\NXOpen.Utilities.dll" (Join-Path $repositoryRoot 'tests\NxMarkingsModelRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Regression compilation failed.' }
$savedPath = $env:PATH
try {
    $env:PATH = (Join-Path $NXInstallDir 'NXBIN') + ';' + $savedPath
    & (Join-Path $NXInstallDir 'NXBIN\run_journal.exe') (Join-Path $repositoryRoot 'tests\RunNxMarkingsModelRegression.cs') -args $testDll $fixture $PluginPath $report
    $journalExit = $LASTEXITCODE
} finally {
    $env:PATH = $savedPath
    if ((Get-FileHash -LiteralPath $PartPath).Hash -ne $originalHash) { throw 'Input part hash changed.' }
}
if (-not (Test-Path -LiteralPath $report)) { throw 'NX did not produce a regression report.' }
$lines = Get-Content -LiteralPath $report
$lines | Write-Output
if ($journalExit -ne 0 -or $lines[-1] -ne 'PASS') { throw "NX regression failed. See $report" }
Write-Output "Report: $report"
