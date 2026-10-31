param(
    [Parameter(Mandatory = $true)][string]$PartPath,
    [Parameter(Mandatory = $true)][string]$BodyTag,
    [string]$NXInstallDir = 'C:\Program Files\Siemens\DesigncenterNX2512',
    [string]$PluginPath = '',
    [ValidateSet(0, 1)][int]$ExpectedPartialStatus = 0,
    [switch]$AllReviewed,
    [string]$GroupsPath = '',
    [int]$ExpectedRemovedGroups = -1,
    [switch]$CompileOnly
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repositoryRoot ('artifacts\fill-holes-apply-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$managed = Join-Path $NXInstallDir 'NXBIN\managed'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $output 'NxFillHolesApplyRegression.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$executable" "/r:$managed\NXOpen.dll" "/r:$managed\NXOpen.UF.dll" "/r:$managed\NXOpen.Utilities.dll" /r:System.Core.dll /r:System.Windows.Forms.dll (Join-Path $repositoryRoot 'tests\NxFillHolesApplyRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fill Holes Apply regression compilation failed.' }
if ($CompileOnly) { Write-Host "Compiled: $executable"; return }
if (-not $PluginPath) { $PluginPath = Join-Path $repositoryRoot 'bin\Release\NXRefine.dll' }
$plugin = (Resolve-Path -LiteralPath $PluginPath).Path
$source = (Resolve-Path -LiteralPath $PartPath).Path
$before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$copy = Join-Path $output ([IO.Path]::GetFileName($source))
Copy-Item -LiteralPath $source -Destination $copy
$report = Join-Path $output 'report.txt'
$env:UGII_BASE_DIR = $NXInstallDir
$env:PATH = (Join-Path $NXInstallDir 'NXBIN') + ';' + $env:PATH
$mode = if ($AllReviewed) { 'all-reviewed' } else { 'small' }
$nxArguments = @($executable, $copy, $plugin, $report, $BodyTag, $ExpectedPartialStatus, $mode)
if ($GroupsPath) { $nxArguments += (Resolve-Path -LiteralPath $GroupsPath).Path; $nxArguments += $ExpectedRemovedGroups.ToString() }
& (Join-Path $NXInstallDir 'NXBIN\run_dotnet_nxopen.exe') @nxArguments
$result = $LASTEXITCODE
Write-Host "Report: $report"
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $before) { throw 'Source part hash changed.' }
if ($result -ne 0) { throw "Fill Holes Apply regression failed with exit code $result." }
if (-not (Select-String -LiteralPath $report -SimpleMatch 'UNDO_RESTORED=True' -Quiet)) { throw 'Undo was not verified.' }
Write-Host 'PASS: callback results, body consistency, undo; source hash unchanged; input not saved.'
