$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$output = Join-Path $root 'artifacts/SelectionScanTests.exe'
& $compiler /nologo /target:exe /r:System.Windows.Forms.dll /r:System.Drawing.dll "/out:$output" (Join-Path $root 'src/NXRefine/Core/SelectionScan.cs') (Join-Path $root 'tests/SelectionScanTests.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $output
exit $LASTEXITCODE
