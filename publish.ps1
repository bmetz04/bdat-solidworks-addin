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

# Release notes: draft them from the commits since the last publish, then let Ben edit them in Notepad.
# Notes are for the people using BDAT, so only commits that change the add-in itself (src\) are drafted.
# Tests, build scripts and publish tooling stay out.
$addinPaths = @('src', ':(exclude)src/BDAT/Testing', ':(exclude,glob)src/**/*.Testing.cs')
$lastPublish = (git log -1 --format=%H --grep='^Publish BDAT')
$range = if ($lastPublish) { "$lastPublish..HEAD" } else { 'HEAD' }
$draft = @(git log --no-merges --format='- %s' $range -- $addinPaths | Where-Object { $_ -notmatch '^- Publish BDAT' })
if (git status --porcelain -- $addinPaths) { $draft += '- (describe your uncommitted changes to the add-in here)' }
$notesFile = Join-Path $env:TEMP 'BDAT release notes.txt'
$header = @(
    "# Release notes for BDAT $version",
    '# Only list changes a BDAT user will notice in SolidWorks (new buttons, fixes, behaviour changes),',
    '# one "- " line each, in plain words. Leave out tests, build scripts and publishing.',
    '# Lines starting with # are ignored. Save and close Notepad to continue. Leave it empty to cancel.',
    '')
Set-Content -Path $notesFile -Value ($header + $draft) -Encoding UTF8
Write-Host 'Write the release notes in Notepad, then save and close it.'
Start-Process notepad.exe -ArgumentList "`"$notesFile`"" -Wait
$notes = @(Get-Content $notesFile -Encoding UTF8 | Where-Object { $_ -notmatch '^\s*#' }) -join "`r`n"
$notes = $notes.Trim()
if (-not $notes) { Finish 'No release notes, so nothing was published.' 1 }
Write-Host ''
Write-Host "Release notes for $($version):"
Write-Host $notes

# Stamp the version into the DLL, so the BDAT version button shows it.
$assemblyInfo = Join-Path $repo 'build\AssemblyInfo.cs'
$text = [IO.File]::ReadAllText($assemblyInfo)
$text = [Text.RegularExpressions.Regex]::Replace($text, 'AssemblyInformationalVersion\("[^"]*"\)', "AssemblyInformationalVersion(`"$version`")")
[IO.File]::WriteAllText($assemblyInfo, $text)

function Undo-Stamp { git checkout -q -- build/AssemblyInfo.cs }

# Build.
Write-Host ''
Write-Host "Building BDAT $version..."
$env:BDAT_NO_PAUSE = '1'  # build.bat pauses when double-clicked; not when Publish runs it.
& cmd.exe /c "`"$(Join-Path $repo 'build.bat')`""
if ($LASTEXITCODE -ne 0) { Undo-Stamp; Finish 'The build failed (see the errors above). Nothing was published.' 1 }

# Run the automated tests on the new build. Skipped until the tests are in the repo.
$gate = Join-Path $repo 'tests\gate.ps1'
if (Test-Path $gate) {
    Write-Host ''
    Write-Host 'Running the BDAT tests...'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $gate
    if ($LASTEXITCODE -ne 0) { Undo-Stamp; Finish 'The tests failed (see above). Nothing was published.' 1 }
}

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

# Latest notes for the Update BDAT button, plus the full history in RELEASE-NOTES.md.
[IO.File]::WriteAllText((Join-Path $release 'notes.txt'), $notes + "`r`n")
$historyFile = Join-Path $repo 'RELEASE-NOTES.md'
$history = if (Test-Path $historyFile) { [IO.File]::ReadAllText($historyFile) } else { "# BDAT release notes`r`n" }
$marker = $history.IndexOf("`n## ")
$entry = "## $version`r`n`r`n$notes`r`n`r`n"
if ($marker -ge 0) { $history = $history.Substring(0, $marker + 1) + $entry + $history.Substring($marker + 1) }
else { $history = $history.TrimEnd() + "`r`n`r`n" + $entry }
[IO.File]::WriteAllText($historyFile, $history)

# Folders picked in New from EBOM on this PC go into the team list, so nobody else has to pick them.
$pickedFolders = Join-Path $env:LOCALAPPDATA 'BDAT\ebom-folders-picked.csv'
if (Test-Path $pickedFolders) {
    $teamFolders = Join-Path $release 'ebom-folders.csv'
    $merged = @{}
    if (Test-Path $teamFolders) { Import-Csv $teamFolders | Where-Object { $_.'Assembly Number' -and $_.'Bookmark Id' } | ForEach-Object { $merged[$_.'Assembly Number'] = $_ } }
    $added = 0
    Import-Csv $pickedFolders | Where-Object { $_.'Assembly Number' -and $_.'Bookmark Id' } | ForEach-Object {
        $old = $merged[$_.'Assembly Number']
        if (-not $old -or $old.'Bookmark Id' -ne $_.'Bookmark Id') { $added++ }
        $merged[$_.'Assembly Number'] = $_   # a pick replaces the team's entry
    }
    $lines = @('Assembly Number,Bookmark Id,Bookmark Title')
    foreach ($key in ($merged.Keys | Sort-Object)) {
        $row = $merged[$key]
        $lines += (@($row.'Assembly Number', $row.'Bookmark Id', $row.'Bookmark Title') | ForEach-Object {
            $v = [string]$_; if ($v -match '[,"]') { '"' + $v.Replace('"', '""') + '"' } else { $v } }) -join ','
    }
    [IO.File]::WriteAllText($teamFolders, ($lines -join "`r`n") + "`r`n", (New-Object Text.UTF8Encoding($false)))
    if ($added -gt 0) { Write-Host "Shared $added New from EBOM folder(s) with the team (release\ebom-folders.csv)." }
}

git add -A
git commit -q -m "Publish BDAT $version"
git push origin HEAD
if ($LASTEXITCODE -ne 0) { Finish 'Could not push to GitHub. The build is committed on this PC; ask Claude to push it.' 1 }

Finish "Published BDAT $version. Teammates get it with the Update BDAT button in SolidWorks or the desktop shortcut (allow a few minutes for GitHub to catch up)." 0
