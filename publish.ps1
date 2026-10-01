# Publishes a new BDAT build for the team: commits your changes, builds, and pushes the build to GitHub.
# Teammates get it next time they click "Update BDAT". Run it through "Publish BDAT.bat".

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
Set-Location $repo

function Finish($message, $code) {
    Write-Host ''
    if ($code -eq 0) { Write-Host $message -ForegroundColor Green } else { Write-Host $message -ForegroundColor Red }
    Write-Host ''
    Read-Host 'Press Enter to close'
    exit $code
}

Write-Host 'Publish BDAT' -ForegroundColor Cyan
Write-Host ''

# Older setups put a developer "Update BDAT" shortcut on this user's desktop. BDAT Setup replaces it.
$oldLink = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Update BDAT.lnk'
if (Test-Path $oldLink) {
    $target = (New-Object -ComObject WScript.Shell).CreateShortcut($oldLink).TargetPath
    if ($target -like "$repo*") { Remove-Item $oldLink -Force }
}

# Get anything already on GitHub first.
git pull --ff-only
if ($LASTEXITCODE -ne 0) { Finish 'Could not pull from GitHub. Ask Claude to sort out the repo, then try again.' 1 }

# Build.
Write-Host ''
Write-Host 'Building BDAT...'
& cmd.exe /c "`"$(Join-Path $repo 'build.bat')`""
if ($LASTEXITCODE -ne 0) { Finish 'The build failed (see the errors above). Nothing was published.' 1 }

# Show what is about to be published.
$changes = git status --porcelain -- . ':!release'
$commit = (git rev-parse --short HEAD).Trim()
if ($changes) {
    Write-Host ''
    Write-Host 'These changed files will be published too:'
    $changes | ForEach-Object { Write-Host "  $_" }
}
Write-Host ''
$answer = Read-Host 'Publish this build to the whole team? (y/n)'
if ($answer -notmatch '^[yY]') { Finish 'Cancelled. Nothing was published.' 1 }

if ($changes) {
    git add -A
    git commit -q -m 'Update BDAT source'
    $commit = (git rev-parse --short HEAD).Trim()
}

$version = (Get-Date -Format 'yyyy-MM-dd HH:mm') + " ($commit)"
$release = Join-Path $repo 'release'
if (-not (Test-Path $release)) { New-Item -ItemType Directory -Path $release | Out-Null }
Copy-Item (Join-Path $repo 'src\BDAT\bin\Release\net48\BDAT.dll') $release -Force
Set-Content -Path (Join-Path $release 'version.txt') -Value $version

git add release
git commit -q -m "Publish BDAT $version"
git push origin HEAD
if ($LASTEXITCODE -ne 0) { Finish 'Could not push to GitHub. The build is committed on this PC; run Publish again to retry the push.' 1 }

Finish "Published BDAT $version. Teammates get it next time they click 'Update BDAT' (allow a few minutes for GitHub to catch up)." 0
