$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$outputDirectory = Join-Path $repositoryRoot "artifacts"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$executable = Join-Path $outputDirectory "GapCriteriaTests.exe"
$sources = @(
    (Join-Path $repositoryRoot "tests\GapCriteriaTests.cs"),
    (Join-Path $repositoryRoot "src\NXRefine\Analysis\GapCriteria.cs"),
    (Join-Path $repositoryRoot "src\NXRefine\Analysis\FaceBoxIndex.cs"),
    (Join-Path $repositoryRoot "src\NXRefine\Analysis\PointQueryCache.cs")
)
& $compiler /nologo /optimize+ "/out:$executable" $sources
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $executable
exit $LASTEXITCODE
