# BDAT for SolidWorks 2025

A SolidWorks add-in that puts FUBC speed-up macros on a **BDAT** toolbar, menu, and CommandManager tab. Every macro is its own button.

## Commands

### Murder Part
Turns the open part into a single dumb solid with no threads.

1. Deletes every cosmetic thread and every modeled **Thread** feature (modeled threads become plain cylinders).
2. Exports a Parasolid copy as `<PartName>_murdered.x_t` in the same folder as the part.
3. Reloads the original `.SLDPRT` from disk, so the original keeps its threads.
4. Opens the `.x_t` as a new part (not saved yet). If it imports as several bodies, it tries to combine them into one.

If the part has unsaved changes, it asks to save them first.

## Getting it and keeping it updated

See [SETUP-NEW-COMPUTER.md](SETUP-NEW-COMPUTER.md) to put BDAT on a computer. After that, close SolidWorks and click the **Update BDAT** desktop shortcut whenever you want the latest version. It pulls from GitHub, rebuilds, and re-registers in one go.

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
