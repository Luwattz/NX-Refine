param(
    [string]$NXInstallDir = 'C:\Program Files\Siemens\DesigncenterNX2512',
    [string]$PluginPath = '',
    [switch]$CompileOnly
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $NXInstallDir 'NXBIN\managed'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $repositoryRoot 'artifacts\NxBlendCollectorRegression.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$output" "/r:$managed\NXOpen.dll" "/r:$managed\NXOpen.UF.dll" "/r:$managed\NXOpen.Utilities.dll" /r:System.Core.dll (Join-Path $repositoryRoot 'tests\RunNxBlendCollectorRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Blend collector regression compilation failed.' }
if ($CompileOnly) { Write-Host "Compiled: $output"; return }
if (-not $PluginPath) { $PluginPath = Join-Path $repositoryRoot 'bin\Release\NXRefine.dll' }
$plugin = (Resolve-Path -LiteralPath $PluginPath).Path
$env:UGII_BASE_DIR = $NXInstallDir
$env:PATH = (Join-Path $NXInstallDir 'NXBIN') + ';' + $env:PATH
# Use the Framework loader matching the production assemblies, not the
# managed_core compiler used for C# journals in NX 2512.
& (Join-Path $NXInstallDir 'NXBIN\run_dotnet_nxopen.exe') $output $plugin
if ($LASTEXITCODE -ne 0) { throw "NX blend collector regression failed: $LASTEXITCODE" }
