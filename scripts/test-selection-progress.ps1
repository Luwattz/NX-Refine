param(
    [string]$PluginPath = '',
    [string]$NXInstallDir = 'C:\Program Files\Siemens\DesigncenterNX2512'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $PluginPath) { $PluginPath = Join-Path $repositoryRoot 'bin\Release\NXRefine.dll' }
$output = Join-Path $repositoryRoot 'artifacts\selection-progress-layout'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$executable = Join-Path $output 'SelectionProgressLayout.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$executable" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll (Join-Path $repositoryRoot 'tests\SelectionProgressLayout.cs')
if ($LASTEXITCODE -ne 0) { throw 'Progress layout regression compilation failed.' }
& $executable (Resolve-Path -LiteralPath $PluginPath).Path $output (Join-Path $NXInstallDir 'NXBIN\managed')
if ($LASTEXITCODE -ne 0) { throw 'Progress layout regression failed.' }
Write-Host "Rendered progress states: $output"
