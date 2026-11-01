param(
    [string]$NXInstallDir = 'C:\Program Files\Siemens\DesigncenterNX2512',
    [string]$PluginPath = '',
    [string]$PartPath = 'C:\Users\chimp\Desktop\Geometry\housing.prt',
    [int]$Seconds = 90,
    [int]$SourceLimit = 64,
    [switch]$CompileOnly
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $NXInstallDir 'NXBIN\managed'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputRoot = Join-Path $repositoryRoot ('artifacts\blend-grouped-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputRoot | Out-Null
$executable = Join-Path $outputRoot 'NxBlendGroupedValidationRegression.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$executable" "/r:$managed\NXOpen.dll" "/r:$managed\NXOpen.UF.dll" "/r:$managed\NXOpen.Utilities.dll" /r:System.Core.dll (Join-Path $repositoryRoot 'tests\NxBlendGroupedValidationRegression.cs')
if ($LASTEXITCODE -ne 0) { throw 'Grouped validation regression compilation failed.' }
if ($CompileOnly) { Write-Host "Compiled: $executable"; return }
if (-not $PluginPath) { $PluginPath = Join-Path $repositoryRoot 'bin\Release\NXRefine.dll' }
$plugin = (Resolve-Path -LiteralPath $PluginPath).Path
$inputPart = (Resolve-Path -LiteralPath $PartPath).Path
$originalHash = (Get-FileHash -LiteralPath $inputPart -Algorithm SHA256).Hash
$copy = Join-Path $outputRoot 'housing-copy.prt'
Copy-Item -LiteralPath $inputPart -Destination $copy
$env:UGII_BASE_DIR = $NXInstallDir
$env:PATH = (Join-Path $NXInstallDir 'NXBIN') + ';' + $env:PATH
$report = Join-Path $outputRoot 'report.txt'
try {
    & (Join-Path $NXInstallDir 'NXBIN\run_dotnet_nxopen.exe') $executable $plugin $copy $report $Seconds $SourceLimit
    $nativeExit = $LASTEXITCODE
} finally {
    $finalHash = (Get-FileHash -LiteralPath $inputPart -Algorithm SHA256).Hash
    $copyHash = (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash
    [pscustomobject]@{ OriginalSHA256 = $originalHash; OriginalUnchanged = ($originalHash -eq $finalHash); CopyNotSaved = ($originalHash -eq $copyHash); Report = $report } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'verification.json')
    Write-Host "Report: $report"
    if ($originalHash -ne $finalHash -or $originalHash -ne $copyHash) { throw 'Input part hash changed.' }
}
if ($nativeExit -ne 0) { throw "NX grouped validation regression failed: $nativeExit" }
Get-Content -LiteralPath $report
