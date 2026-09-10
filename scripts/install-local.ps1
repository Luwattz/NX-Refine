param(
    [Parameter(Mandatory = $true)]
    [string]$CustomDirectoryFile
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$deployDirectory = Join-Path $repositoryRoot "deploy"
$resolvedDeployDirectory = (Resolve-Path -LiteralPath $deployDirectory).Path
$parent = Split-Path -Parent $CustomDirectoryFile
if (-not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent | Out-Null
}
if (-not (Test-Path -LiteralPath $CustomDirectoryFile)) {
    New-Item -ItemType File -Path $CustomDirectoryFile | Out-Null
}
$lines = Get-Content -LiteralPath $CustomDirectoryFile
if ($lines -notcontains $resolvedDeployDirectory) {
    Add-Content -LiteralPath $CustomDirectoryFile -Value $resolvedDeployDirectory
}
Write-Host "Registered $resolvedDeployDirectory in $CustomDirectoryFile. Restart NX to load the ribbon."

