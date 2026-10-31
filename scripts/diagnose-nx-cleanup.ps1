param(
    [Parameter(Mandatory = $true)][string]$PartPath,
    [ValidateSet('audit', 'holes-scan', 'holes', 'hole-walls', 'hole-neighbors', 'hole-recovery', 'hole-circles', 'hole-picks', 'hole-targets', 'hole-target-recovery', 'mesh', 'probes', 'split-probes', 'blends', 'blends-direct')]
    [string]$Mode = 'audit',
    [string]$BodyTag = '',
    [ValidateRange(1, 100)][int]$MaximumGroups = 8,
    [string]$NXInstallDir = 'C:\Program Files\Siemens\DesigncenterNX2512',
    [string]$PluginPath = '',
    [string]$ProbePath = '',
    [switch]$CompileOnly
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repositoryRoot ('artifacts\cleanup-diagnosis-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$managed = Join-Path $NXInstallDir 'NXBIN\managed'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable = Join-Path $output 'NxCleanupDiagnosis.exe'
& $compiler /nologo /target:exe /optimize+ /platform:x64 "/out:$executable" "/r:$managed\NXOpen.dll" "/r:$managed\NXOpen.UF.dll" "/r:$managed\NXOpen.Utilities.dll" /r:System.Core.dll (Join-Path $repositoryRoot 'tests\NxCleanupDiagnosis.cs')
if ($LASTEXITCODE -ne 0) { throw 'Diagnosis compilation failed.' }
if ($CompileOnly) { Write-Host "Compiled: $executable"; return }
if (-not $PluginPath) { $PluginPath = Join-Path $repositoryRoot 'bin\Release\NXRefine.dll' }
$plugin = (Resolve-Path -LiteralPath $PluginPath).Path
$source = (Resolve-Path -LiteralPath $PartPath).Path
$copy = Join-Path $output ([IO.Path]::GetFileName($source))
Copy-Item -LiteralPath $source -Destination $copy
$beforeHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$report = Join-Path $output 'report.txt'
$env:UGII_BASE_DIR = $NXInstallDir
$env:PATH = (Join-Path $NXInstallDir 'NXBIN') + ';' + $env:PATH
if (-not $BodyTag) { $BodyTag = 'all' }
$arguments = @($executable, $copy, $plugin, $report, $Mode, $BodyTag, $MaximumGroups.ToString())
if($ProbePath) { $arguments += (Resolve-Path -LiteralPath $ProbePath).Path }
# NX 2512 compiles C# journals with managed_core references. Run the compiled
# Framework executable through NX's matching loader to avoid mixed NXOpen types.
& (Join-Path $NXInstallDir 'NXBIN\run_dotnet_nxopen.exe') @arguments
$result = $LASTEXITCODE
$afterHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
Write-Host "Report: $report"
if ($beforeHash -ne $afterHash) { throw 'Source part hash changed during diagnosis.' }
if ($result -ne 0) { throw "NX diagnosis failed with exit code $result. See $report" }
if (-not (Select-String -LiteralPath $report -SimpleMatch 'UNDO_RESTORED=True' -Quiet)) {
    throw 'Diagnosis did not finish with verified undo restoration.'
}
Write-Host 'Original SHA256 unchanged; input not saved; undo verified.'
