# Test gate for Publish BDAT: run after build.bat, publish only if this exits 0.
#
# Always runs the unit tests against the BDAT.dll that was just built. If SolidWorks is open with no documents,
# it also runs the SolidWorks tests in it (it never closes SolidWorks or touches open work). If SolidWorks isn't
# available, the SolidWorks tests are skipped with a warning, unless -RequireSolidWorks is given.
#
#   -Build              build BDAT first (publish.ps1 has already built it, so it doesn't pass this)
#   -RequireSolidWorks  fail if the SolidWorks tests can't run
#   -Launch             start a SolidWorks for the tests when none is running

param(
    [switch]$Build,
    [switch]$RequireSolidWorks,
    [switch]$Launch
)

$runArgs = @{ SolidWorks = $true }
if (-not $Build) { $runArgs.NoBuild = $true }
if ($Launch) { $runArgs.Launch = $true }

Write-Host ''
Write-Host 'Running the BDAT tests before publishing...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'run-tests.ps1') @runArgs
$code = $LASTEXITCODE

if ($code -eq 3 -and -not $RequireSolidWorks) {
    Write-Host 'Unit tests passed, but the SolidWorks tests were skipped. To run them, open SolidWorks with no documents and publish again.' -ForegroundColor Yellow
    exit 0
}
exit $code
