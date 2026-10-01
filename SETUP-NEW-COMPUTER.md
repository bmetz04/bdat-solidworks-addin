# Putting BDAT on a computer

1. Get **BDAT Setup.bat** (Ben can send it to you, or download it from https://github.com/bmetz04/bdat-solidworks-addin/raw/main/BDAT%20Setup.bat with right-click > Save link as).
2. Close SolidWorks and double-click **BDAT Setup.bat**. Click **Yes** when Windows asks for admin rights.
   If Windows shows "Windows protected your PC", click **More info** > **Run anyway**.
3. Start SolidWorks. BDAT shows up as a tab when a part is open.

No Git, compiler or GitHub account needed. Setup installs BDAT into `C:\ProgramData\BDAT` and puts an **Update BDAT** shortcut on the desktop.

## Updating

Close SolidWorks and double-click **Update BDAT** on the desktop.

## Publishing a new version (Ben)

Ben's PC has the code checked out at `C:\Users\bacon\BDAT\bdat-solidworks-addin`. After changing the code there, double-click **Publish BDAT.bat** in that folder. It builds BDAT, asks to confirm, and pushes the build to the `release` folder on GitHub, which is where everyone's **Update BDAT** downloads from.
