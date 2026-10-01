# BDAT for SolidWorks 2025

A SolidWorks add-in that puts FUBC speed-up macros on a **BDAT** toolbar, menu, and CommandManager tab. Every macro is its own button.

## Commands

### Murder Part
Turns the open part into a dumb solid with no threads. Multi-body parts stay multi-body.

**The part you have open is never changed.** Murder Part first asks you to confirm, naming the part and what it will remove. All the deleting happens on a hidden temporary copy.

1. Saves a copy of the part as it is right now (including unsaved changes) to `%TEMP%\BDAT\murder` and opens it invisibly. Your open part, its file and its saved/unsaved state are left exactly as they were.
2. On the copy, deletes every feature folder with "thread" in its name (like the **Threads** folder on McMaster-Carr parts) with everything inside it, plus every cosmetic thread and modeled **Thread** feature elsewhere in the tree.
3. Exports the copy to Parasolid, then closes the copy without saving.
4. Opens the Parasolid as a new part (not saved yet), then deletes the temporary files. Nothing is written next to your part.

### Update BDAT
Checks GitHub for a newer published BDAT. If there is one, it downloads it now (Windows asks for admin once) and installs it as soon as you close SolidWorks, so the next time you open SolidWorks you're on the new version.

### BDAT vN
Shows which version is running; click it for the full version and where it was loaded from. The number goes up by one every time Publish BDAT runs. Local builds show "dev build".

## Getting it and keeping it updated

Teammates: close SolidWorks and double-click **BDAT Setup.bat**. It downloads the latest build, registers it, and adds an **Update BDAT** desktop shortcut for later updates. See [SETUP-NEW-COMPUTER.md](SETUP-NEW-COMPUTER.md).

To release a new version to the team, run **Publish BDAT.bat** on the developer PC. It gives the build the next version number (v1, v2, ...), opens Notepad with draft release notes for you to edit, and teammates pick it up with the **Update BDAT** button. All past notes are in [RELEASE-NOTES.md](RELEASE-NOTES.md).

## Build (once, on Windows)

Run `build.bat`. It uses the C# compiler that ships with Windows, so no Visual Studio or .NET SDK is needed.
If SolidWorks isn't in the default folder, pass its api\redist folder: `build.bat "D:\Your\Path\SOLIDWORKS\api\redist"`.
Keep the code C# 5 compatible (no `$"..."`, `?.`, `=>` members or `nameof`) so this keeps working.

Alternative: with Visual Studio 2022 or the .NET SDK installed, `dotnet build src\BDAT\BDAT.csproj -c Release`.

## Install

1. Close SolidWorks.
2. Right-click `install.bat` and choose **Run as administrator**.
3. Start SolidWorks. BDAT appears under **Tools > Add-Ins** (ticked to load at startup) and as a **BDAT** tab when a part is open.

To remove it, run `uninstall.bat` as administrator.

After rebuilding, you don't need to reinstall unless the DLL moved. Just close and reopen SolidWorks.

## Adding a new macro

1. Add a class in `src/BDAT/Commands/` that implements `IBdatCommand`.
2. Add it to the `_commands` list in `SwAddin.cs`.
3. Add an `OnXxx` / `CanXxx` callback pair at the bottom of `SwAddin.cs`.
4. Bump `CommandGroupVersion` in `SwAddin.cs` so SolidWorks refreshes the toolbar.
