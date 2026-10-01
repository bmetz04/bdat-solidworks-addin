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

# Work out the next version number from the last published one ("v3 (...)" -> v4).
$versionFile = Join-Path $repo 'release\version.txt'
$number = 1
if (Test-Path $versionFile) {
    $last = (Get-Content $versionFile -Raw).Trim()
    if ($last -match '^v(\d+)') { $number = [int]$Matches[1] + 1 } else { $number = 2 }
}
$version = "v$number (" + (Get-Date -Format 'yyyy-MM-dd HH:mm') + ')'
$changes = git status --porcelain -- . ':!release'

# Stamp the version into the DLL, so the BDAT version button shows it.
$assemblyInfo = Join-Path $repo 'build\AssemblyInfo.cs'
$text = [IO.File]::ReadAllText($assemblyInfo)
$text = [Text.RegularExpressions.Regex]::Replace($text, 'AssemblyInformationalVersion\("[^"]*"\)', "AssemblyInformationalVersion(`"$version`")")
[IO.File]::WriteAllText($assemblyInfo, $text)

function Undo-Stamp { git checkout -q -- build/AssemblyInfo.cs }

# Build.
Write-Host ''
Write-Host "Building BDAT $version..."
& cmd.exe /c "`"$(Join-Path $repo 'build.bat')`""
if ($LASTEXITCODE -ne 0) { Undo-Stamp; Finish 'The build failed (see the errors above). Nothing was published.' 1 }

# Show what is about to be published.
if ($changes) {
    Write-Host ''
    Write-Host 'These changed files will be published too:'
    $changes | ForEach-Object { Write-Host "  $_" }
}
Write-Host ''
$answer = Read-Host "Publish BDAT $version to the whole team? (y/n)"
if ($answer -notmatch '^[yY]') { Undo-Stamp; Finish 'Cancelled. Nothing was published.' 1 }

$release = Join-Path $repo 'release'
if (-not (Test-Path $release)) { New-Item -ItemType Directory -Path $release | Out-Null }
Copy-Item (Join-Path $repo 'src\BDAT\bin\Release\net48\BDAT.dll') $release -Force
Set-Content -Path $versionFile -Value $version

git add -A
git commit -q -m "Publish BDAT $version"
git push origin HEAD
if ($LASTEXITCODE -ne 0) { Finish 'Could not push to GitHub. The build is committed on this PC; ask Claude to push it.' 1 }

Finish "Published BDAT $version. Teammates get it with the Update BDAT button in SolidWorks or the desktop shortcut (allow a few minutes for GitHub to catch up)." 0
