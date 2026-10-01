# Builds BDAT and the test harness, then runs the BDAT tests. See tests\README.md.
#
#   .\tests\run-tests.ps1                      unit tests only (no SolidWorks)
#   .\tests\run-tests.ps1 -SolidWorks          also the SolidWorks tests, using a SolidWorks that's already
#                                              open with no documents (it never closes or touches your work)
#   .\tests\run-tests.ps1 -SolidWorks -Launch  start a SolidWorks for the tests if none is running, close it after
#
# Exit codes: 0 passed, 1 failed, 2 build/usage problem, 3 SolidWorks tests couldn't run.
# Nothing here signs in to, opens, or saves to 3DEXPERIENCE.

param(
    [switch]$SolidWorks,
    [switch]$Launch,
    [switch]$NoBuild,
    [switch]$Keep,
    [string]$ApiDir = 'C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dllDir = Join-Path $repo 'src\BDAT\bin\Release\net48'
$binDir = Join-Path $PSScriptRoot 'bin'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not $NoBuild) {
    Write-Host 'Building BDAT...'
    $env:BDAT_NO_PAUSE = '1' # build.bat pauses when double-clicked; never here
    & cmd.exe /c "`"$(Join-Path $repo 'build.bat')`" `"$ApiDir`""
    if ($LASTEXITCODE -ne 0) { Write-Host 'BDAT build failed.' -ForegroundColor Red; exit 2 }
}
if (-not (Test-Path (Join-Path $dllDir 'BDAT.dll'))) { Write-Host "No BDAT.dll in $dllDir. Build first." -ForegroundColor Red; exit 2 }

# The harness runs next to its own copy of BDAT.dll, so the build output folder only ever holds the add-in.
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
Copy-Item (Join-Path $dllDir '*.dll') $binDir -Force

Write-Host 'Building the test harness...'
& $csc /nologo /target:exe /platform:x64 /out:"$binDir\BdatTests.exe" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    /r:"$binDir\BDAT.dll" /r:"$binDir\SolidWorks.Interop.sldworks.dll" /r:"$binDir\SolidWorks.Interop.swconst.dll" /r:"$binDir\SolidWorks.Interop.swpublished.dll" `
    "$PSScriptRoot\BdatTests.cs"
if ($LASTEXITCODE -ne 0) { Write-Host 'Test harness build failed.' -ForegroundColor Red; exit 2 }

$harnessArgs = @()
if ($SolidWorks) { $harnessArgs += '--solidworks', '--attach' }
if ($Launch) { $harnessArgs += '--launch' }
if ($Keep) { $harnessArgs += '--keep' }

Write-Host ''
& "$binDir\BdatTests.exe" @harnessArgs
$code = $LASTEXITCODE
if ($code -eq 0) { Write-Host 'All BDAT tests passed.' -ForegroundColor Green }
elseif ($code -eq 3) { Write-Host 'The SolidWorks tests could not run (see above).' -ForegroundColor Yellow }
else { Write-Host 'BDAT tests FAILED.' -ForegroundColor Red }
exit $code
