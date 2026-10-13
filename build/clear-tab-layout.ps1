# Forgets SolidWorks' saved layout of the BDAT tab so new buttons show up.
# SolidWorks saves each CommandManager tab as a fixed list of button slots and puts them back over the tab BDAT builds,
# so after a button is added the last ones go missing. BDAT tries to clear this itself when it starts, but inside
# 3DEXPERIENCE SOLIDWORKS its registry view is separate, and SolidWorks writes the layout again when it closes, so it's
# cleared here, with SolidWorks closed. BDAT Setup.bat (and so Update BDAT) does the same for teammates.
$bdatModule = '{D8D33AC0-63B3-49BC-B09A-E46207CE999B}'
if (Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue) {
    Write-Host 'SolidWorks is open, so the BDAT tab layout was not reset. If a new button is missing, close SolidWorks and build again.' -ForegroundColor Yellow
    exit 0
}
$cleared = 0
Get-ChildItem 'HKCU:\Software\SolidWorks' -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'SOLIDWORKS *' } | ForEach-Object {
    foreach ($ui in @('User Interface\CommandManager', 'Simplified Interface\User Interface\CommandManager')) {
        Get-ChildItem (Join-Path $_.PSPath $ui) -ErrorAction SilentlyContinue | Get-ChildItem -ErrorAction SilentlyContinue | ForEach-Object {
            $owner = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).ModuleName
            if ($owner -and $owner -ieq $bdatModule) { Remove-Item $_.PSPath -Recurse -Force -ErrorAction SilentlyContinue; $cleared++ }
        }
    }
}
Write-Host "Reset the BDAT tab layout ($cleared saved tabs), so every button shows next time SolidWorks starts."
