<# : This line hides the batch part below from PowerShell.
@echo off
REM BDAT installer and updater for SolidWorks. Double-click to install or update BDAT.
REM Downloads the latest published build, so no Git, compiler or GitHub account is needed.
REM The BDAT "Update BDAT" button runs this with "staged": it then waits for SolidWorks to close instead of asking.
set "BDAT_SELF=%~f0"
set "BDAT_ARGS=%*"
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Invoke-Expression ([IO.File]::ReadAllText($env:BDAT_SELF))"
exit /b
#>

$ErrorActionPreference = 'Stop'
$self = $env:BDAT_SELF
$baseUrl = 'https://raw.githubusercontent.com/bmetz04/bdat-solidworks-addin/main/release'
$installDir = Join-Path $env:ProgramData 'BDAT'
$staged = "$env:BDAT_ARGS" -match 'staged'

function Finish($message, $code) {
    if ($staged) {
        # The window is minimized, so show the result as a popup instead.
        Add-Type -AssemblyName System.Windows.Forms
        $icon = if ($code -eq 0) { 'Information' } else { 'Error' }
        [System.Windows.Forms.MessageBox]::Show($message, 'BDAT update', 'OK', $icon) | Out-Null
        exit $code
    }
    Write-Host ''
    if ($code -eq 0) { Write-Host $message -ForegroundColor Green } else { Write-Host $message -ForegroundColor Red }
    Write-Host ''
    Read-Host 'Press Enter to close'
    exit $code
}

# Needs admin to install into ProgramData and register with SolidWorks. Re-launch elevated if needed.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Asking Windows for admin rights. Click Yes.'
    try {
        if ($env:BDAT_ARGS) { Start-Process -FilePath $self -ArgumentList $env:BDAT_ARGS -Verb RunAs }
        else { Start-Process -FilePath $self -Verb RunAs }
    } catch { Finish 'BDAT needs admin rights to install. Run this again and click Yes.' 1 }
    exit 0
}

Write-Host 'BDAT setup' -ForegroundColor Cyan
Write-Host ''

# Download the latest build.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$web = New-Object Net.WebClient
$web.Headers.Add('Cache-Control', 'no-cache')
$tmp = Join-Path $env:TEMP ('BDAT-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    Write-Host 'Downloading the latest BDAT...'
    # The query string stops GitHub's download cache from handing back an older build.
    $nocache = '?t=' + [DateTime]::UtcNow.Ticks
    $latest = $web.DownloadString("$baseUrl/version.txt$nocache").Trim()
    $web.DownloadFile("$baseUrl/BDAT.dll$nocache", (Join-Path $tmp 'BDAT.dll'))
} catch {
    Finish "Could not download BDAT. Check your internet connection. ($($_.Exception.Message))" 1
}
$versionFile = Join-Path $installDir 'version.txt'
$current = ''
if (Test-Path $versionFile) { $current = (Get-Content $versionFile -Raw).Trim() }
if ($current -eq $latest) { Write-Host "You already have the latest version ($latest). Reinstalling it anyway." }
elseif ($current) { Write-Host "Updating $current -> $latest" }
else { Write-Host "Installing $latest" }

# SolidWorks locks BDAT.dll while it runs.
if ($staged) {
    while (Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue) {
        Write-Host ''
        Write-Host "BDAT $latest is downloaded. It installs as soon as you close SolidWorks." -ForegroundColor Yellow
        Write-Host 'Leave this window open.'
        Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue | Wait-Process
        Start-Sleep -Seconds 3
    }
} else {
    while (Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue) {
        Write-Host 'SolidWorks is open. Save your work and close it, then press Enter.' -ForegroundColor Yellow
        Read-Host | Out-Null
    }
}

# Install the files.
if (-not (Test-Path $installDir)) { New-Item -ItemType Directory -Path $installDir | Out-Null }
try { Copy-Item (Join-Path $tmp 'BDAT.dll') $installDir -Force }
catch { Finish "Could not replace BDAT.dll. Make sure SolidWorks is fully closed, then run Update BDAT again. ($($_.Exception.Message))" 1 }

# FUBC drawing sheet formats. The FUBC Drawing template points "Use different sheet format" at this folder, so sheets
# added after the first get the short title block. The path must stay the same on every PC.
$templateDir = Join-Path $installDir 'Sheet Formats'
if (-not (Test-Path $templateDir)) { New-Item -ItemType Directory -Path $templateDir | Out-Null }
foreach ($name in @('FUBC B Continuation.slddrt')) {
    try { $web.DownloadFile("$baseUrl/sheet-formats/$([Uri]::EscapeDataString($name))$nocache", (Join-Path $templateDir $name)) }
    catch { Write-Host "Could not download $name. Extra drawing sheets will not get the FUBC title block until the next update. ($($_.Exception.Message))" -ForegroundColor Yellow }
}

# BDAT needs the SolidWorks interop DLLs next to it. Copy them from this PC's own SolidWorks install.
$redist = $null
$candidates = @('C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist')
Get-ChildItem 'HKLM:\SOFTWARE\SolidWorks' -ErrorAction SilentlyContinue | ForEach-Object {
    $setup = Get-ItemProperty (Join-Path $_.PSPath 'Setup') -ErrorAction SilentlyContinue
    if ($setup -and $setup.'SolidWorks Folder') { $candidates += (Join-Path $setup.'SolidWorks Folder' 'api\redist') }
}
foreach ($c in $candidates) { if (Test-Path (Join-Path $c 'SolidWorks.Interop.sldworks.dll')) { $redist = $c; break } }
if ($redist) {
    Copy-Item (Join-Path $redist 'SolidWorks.Interop.*.dll') $installDir -Force
} else {
    Write-Host 'Could not find the SolidWorks api\redist folder. Continuing; BDAT will use the copies SolidWorks registered.' -ForegroundColor Yellow
}

# Register with SolidWorks.
Write-Host 'Registering BDAT with SolidWorks...'
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
& $regasm /codebase (Join-Path $installDir 'BDAT.dll') | Out-Host
if ($LASTEXITCODE -ne 0) { Finish "Registering BDAT failed (RegAsm exit code $LASTEXITCODE)." 1 }
Set-Content -Path $versionFile -Value $latest

# Keep a copy of this script and put an "Update BDAT" shortcut on every user's desktop.
$updater = Join-Path $installDir 'Update BDAT.bat'
if ($self -ne $updater) { Copy-Item $self $updater -Force }
$link = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Update BDAT.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($link)
$shortcut.TargetPath = $updater
$shortcut.WorkingDirectory = $installDir
$shortcut.Description = 'Install the latest BDAT add-in for SolidWorks'
$shortcut.Save()

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Finish "BDAT $latest is installed. Start SolidWorks to use it. To update later, close SolidWorks and double-click 'Update BDAT' on the desktop." 0
