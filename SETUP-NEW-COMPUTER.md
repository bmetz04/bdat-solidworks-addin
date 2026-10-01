# Putting BDAT on a computer

Do this once per computer. After that, the **Update BDAT** shortcut on the desktop is the only thing anyone needs to click.

1. Install **Git for Windows** from https://git-scm.com/download/win (defaults are fine), or run `winget install Git.Git`.
2. Ask Ben to add your GitHub account to the `bdat-solidworks-addin` repo, and accept the invite.
3. Open a Command Prompt and run:
   ```
   git clone https://github.com/bmetz04/bdat-solidworks-addin.git "%USERPROFILE%\BDAT\bdat-solidworks-addin"
   ```
   Git asks you to sign in to GitHub the first time; it remembers you after that.
4. Open that folder and double-click **Update BDAT.bat**. It builds BDAT, registers it with SolidWorks (click **Yes** on the Windows prompt), and puts an **Update BDAT** shortcut on your desktop.
5. Start SolidWorks. BDAT shows up as a tab when a part is open.

## Updating

Close SolidWorks and double-click **Update BDAT** on the desktop. It pulls the latest version, rebuilds, and re-registers. If SolidWorks is still open it waits for you to close it.

If it says it can't pull because files changed, someone edited the code in that folder. Run `git status` there to see what changed, then commit it or undo it with `git checkout -- .`.
