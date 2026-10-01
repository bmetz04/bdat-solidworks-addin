# Updates BDAT on this computer: pulls the latest code from GitHub, rebuilds, and re-registers it with SolidWorks.
# Run it through "Update BDAT.bat" (or the "Update BDAT" desktop shortcut that the first run creates).
# Git and the build run as you; only the SolidWorks registration step asks for admin rights.

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot

function Finish($message, $code) {
    Write-Host ''
    if ($code -eq 0) { Write-Host $message -ForegroundColor Green } else { Write-Host $message -ForegroundColor Red }
    Write-Host ''
    Read-Host 'Press Enter to close'
    exit $code
}

Write-Host 'BDAT updater' -ForegroundColor Cyan
Write-Host "Folder: $repo"
Write-Host ''

# 1. Make the desktop shortcut if it isn't there yet.
$desktop = [Environment]::GetFolderPath('Desktop')
$link = Join-Path $desktop 'Update BDAT.lnk'
if (-not (Test-Path $link)) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = Join-Path $repo 'Update BDAT.bat'
    $shortcut.WorkingDirectory = $repo
    $shortcut.Description = 'Pull the latest BDAT add-in and install it'
    $shortcut.Save()
    Write-Host "Created the 'Update BDAT' shortcut on your desktop."
}

# 2. SolidWorks locks BDAT.dll while it runs, so it has to be closed.
while (Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue) {
    Write-Host 'SolidWorks is open. Save your work and close it, then press Enter.' -ForegroundColor Yellow
    Read-Host | Out-Null
}

# 3. Pull the latest code.
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Finish 'Git is not installed. Install Git for Windows from https://git-scm.com/download/win (or run: winget install Git.Git), then try again.' 1
}
$before = (git -C $repo rev-parse --short HEAD).Trim()
Write-Host 'Getting the latest version from GitHub...'
git -C $repo pull --ff-only
if ($LASTEXITCODE -ne 0) {
    Finish 'Could not pull the latest version. If you changed files in this folder, commit or undo those changes first (git status shows them).' 1
}
$after = (git -C $repo rev-parse --short HEAD).Trim()
if ($before -eq $after) {
    Write-Host "Already on the latest version ($after). Rebuilding anyway."
} else {
    Write-Host "Updated $before -> $after. Changes:"
    git -C $repo log --oneline "$before..$after"
}
Write-Host ''

# 4. Build.
Write-Host 'Building BDAT...'
& cmd.exe /c "`"$(Join-Path $repo 'build.bat')`""
if ($LASTEXITCODE -ne 0) {
    Finish 'The build failed (see the errors above). BDAT was not changed in SolidWorks.' 1
}
$dll = Join-Path $repo 'src\BDAT\bin\Release\net48\BDAT.dll'
if (-not (Test-Path $dll)) { Finish "Build finished but $dll is missing." 1 }

# 5. Register with SolidWorks (the one step that needs admin; Windows will ask).
Write-Host ''
Write-Host 'Registering BDAT with SolidWorks. Click Yes on the Windows prompt.'
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
try {
    $p = Start-Process -FilePath $regasm -ArgumentList @('/codebase', "`"$dll`"") -Verb RunAs -Wait -PassThru -WindowStyle Hidden
} catch {
    Finish 'Registration was cancelled. The new build is in place but SolidWorks may still use the old registration; run this again and click Yes.' 1
}
if ($p.ExitCode -ne 0) { Finish "Registration failed (RegAsm exit code $($p.ExitCode))." 1 }

Finish "BDAT is up to date ($after). Start SolidWorks to use it." 0
