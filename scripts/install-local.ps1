param(
    [Parameter(Mandatory = $true)]
    [string]$CustomDirectoryFile
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$deployDirectory = Join-Path $repositoryRoot "deploy"
$resolvedDeployDirectory = (Resolve-Path -LiteralPath $deployDirectory).Path
$bitmapDirectory = Join-Path $deployDirectory "application"
$resolvedBitmapDirectory = (Resolve-Path -LiteralPath $bitmapDirectory).Path
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

$userBitmapPath = [Environment]::GetEnvironmentVariable("UGII_BITMAP_PATH", "User")
$bitmapEntries = @()
if (-not [string]::IsNullOrWhiteSpace($userBitmapPath)) {
    $bitmapEntries = @($userBitmapPath -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}
if (-not ($bitmapEntries | Where-Object { $_ -ieq $resolvedBitmapDirectory })) {
    $bitmapEntries = @($resolvedBitmapDirectory) + $bitmapEntries
    [Environment]::SetEnvironmentVariable("UGII_BITMAP_PATH", ($bitmapEntries -join ';'), "User")
}

Write-Host "Registered $resolvedDeployDirectory in $CustomDirectoryFile."
Write-Host "Registered $resolvedBitmapDirectory in the user UGII_BITMAP_PATH. Restart NX to load the ribbon and icons."
