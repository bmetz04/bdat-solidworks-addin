# Builds BDAT and the test harness, then runs the BDAT tests. See tests\README.md.
#
#   .\tests\run-tests.ps1                      unit tests only (no SolidWorks)
#   .\tests\run-tests.ps1 -SolidWorks          also the SolidWorks tests, using a SolidWorks that's already
#                                              open with no documents (it never closes or touches your work)
#   .\tests\run-tests.ps1 -SolidWorks -Launch  start a SolidWorks for the tests if none is running, close it after
#   ... -Launch -UseTestBuild                  make that SolidWorks load the BDAT under test instead of the
#                                              installed one, so the BDAT tab check covers this build
#
# Exit codes: 0 passed, 1 failed, 2 build/usage problem, 3 SolidWorks tests couldn't run.
# Nothing here signs in to, opens, or saves to 3DEXPERIENCE.

param(
    [switch]$SolidWorks,
    [switch]$Launch,
    [switch]$NoBuild,
    [switch]$Keep,
    [switch]$UseTestBuild,
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

# -UseTestBuild: for this run only, register the build under test for this Windows user (HKCU, no admin).
# HKCU\Software\Classes wins over the machine-wide registration, so the SolidWorks the tests start loads it.
# It's removed again in finally, so the next SolidWorks loads the installed BDAT as usual.
$override = 'HKCU:\Software\Classes\CLSID\{D8D33AC0-63B3-49BC-B09A-E46207CE999B}'
if ($UseTestBuild) {
    if (-not $Launch) { Write-Host '-UseTestBuild needs -Launch (an open SolidWorks has already loaded its BDAT).' -ForegroundColor Red; exit 2 }
    if (Test-Path $override) { Write-Host "$override already exists (left by an earlier run?). Not touching it; remove it first." -ForegroundColor Red; exit 2 }
    # A fresh folder per run: SolidWorks keeps the DLL it loaded locked until it exits.
    $addinDir = Join-Path $binDir ('addin-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $addinDir | Out-Null
    Copy-Item (Join-Path $dllDir '*.dll') $addinDir -Force
    $dll = Join-Path $addinDir 'BDAT.dll'
    $inproc = "$override\InprocServer32"
    New-Item -Path $inproc -Force | Out-Null
    Set-ItemProperty $override '(default)' 'BDAT.SwAddin'
    Set-ItemProperty $inproc '(default)' 'mscoree.dll'
    Set-ItemProperty $inproc 'ThreadingModel' 'Both'
    Set-ItemProperty $inproc 'Class' 'BDAT.SwAddin'
    Set-ItemProperty $inproc 'Assembly' ([Reflection.AssemblyName]::GetAssemblyName($dll).FullName)
    Set-ItemProperty $inproc 'RuntimeVersion' 'v4.0.30319'
    Set-ItemProperty $inproc 'CodeBase' ('file:///' + $dll.Replace([char]92, [char]47))
    # The machine-wide registration has a per-version subkey (e.g. InprocServer32\0.1.0.0) that .NET prefers;
    # without our own copy of it, the machine-wide one shows through and the installed DLL loads instead.
    $version = [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
    New-Item -Path "$inproc\$version" -Force | Out-Null
    foreach ($name in 'Class', 'Assembly', 'RuntimeVersion', 'CodeBase') {
        Set-ItemProperty "$inproc\$version" $name (Get-ItemProperty $inproc).$name
    }
    $harnessArgs += '--expect-loaded-dll', $dll
    Write-Host "SolidWorks will load the test build from $addinDir for this run."
}

Write-Host ''
try {
    & "$binDir\BdatTests.exe" @harnessArgs
    $code = $LASTEXITCODE
}
finally {
    if ($UseTestBuild -and (Test-Path $override)) {
        Remove-Item -LiteralPath $override -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $override) { Write-Host "Couldn't remove $override. Delete it so SolidWorks loads the installed BDAT again." -ForegroundColor Red }
        else { Write-Host 'Removed the per-user test registration; SolidWorks will load the installed BDAT again.' }
    }
}
if ($code -eq 0) { Write-Host 'All BDAT tests passed.' -ForegroundColor Green }
elseif ($code -eq 3) { Write-Host 'The SolidWorks tests could not run (see above).' -ForegroundColor Yellow }
else { Write-Host 'BDAT tests FAILED.' -ForegroundColor Red }
exit $code
