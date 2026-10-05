# BDAT for SolidWorks 2025

A SolidWorks add-in that puts FUBC speed-up macros on a **BDAT** toolbar, menu, and CommandManager tab. Every macro is its own button. The tab shows the same buttons in parts and assemblies; a button that can't run in the open document (Murder Part and Save MCM in an assembly) is greyed out.

## Commands

### Murder Part
Turns the open part into a dumb solid with no threads. Multi-body parts stay multi-body.

**The part you have open is never changed.** Murder Part first asks you to confirm, naming the part and what it will remove. All the deleting happens on a hidden temporary copy.

1. Saves a copy of the part as it is right now (including unsaved changes) to `%TEMP%\BDAT\murder` and opens it invisibly. Your open part, its file and its saved/unsaved state are left exactly as they were.
2. On the copy, deletes every feature folder with "thread" in its name (like the **Threads** folder on McMaster-Carr parts) with everything inside it, plus every cosmetic thread and modeled **Thread** feature elsewhere in the tree.
3. Exports the copy to Parasolid, then closes the copy without saving.
4. Opens the Parasolid as a new part (not saved yet), then deletes the temporary files. Nothing is written next to your part.

### Save MCM
Saves the open McMaster-Carr part to 3DEXPERIENCE in **Formula UBC Racing > Vendor CAD > McMaster Carr**. You need to be logged in to 3DEXPERIENCE.

1. Opens a pop-up to confirm. For `91251A537_Socket Head Screw`, **Name** is filled in with the part number before the first underscore (`91251A537`) and **Description** with the rest (`Socket Head Screw`). A Murder Part copy's `_murdered` is dropped. Edit either before saving.
2. Puts the description in the part's `Description` custom property, both file-level (the CAD Family in 3DEXPERIENCE) and on every configuration (the Physical Product).
3. Sets the view to isometric (that's the 3DEXPERIENCE thumbnail) and freezes the whole feature tree. The freeze bar only exists when **Enable Freeze bar** is ticked in System Options > General, so if it's off Save MCM turns it on, and it stays on afterwards.
4. Saves the part to 3DEXPERIENCE under that name, adds it to the McMaster Carr bookmark, and checks it in (unlocks it).

The first time you use it, it asks you to pick the McMaster Carr bookmark once and remembers it (`HKCU\Software\BDAT\McMasterBookmarkId`). Once the id is known it can be built in (`KnownBookmarkId` in `SaveMcmCommand.cs`) so nobody has to pick it.

It uses the API of the "3DEXPERIENCE PLM Services" connector add-in (see `Connector.cs`), because the official SolidWorks API can't choose a bookmark when saving.

### Create Origin
Makes a new origin at a point you type in, so a part or sub-assembly can be origin-mated in the top-level assembly. Works in parts and assemblies. SolidWorks can't move the real origin, so this adds the next best thing.

It uses SolidWorks' own **X, Y and Z**, the same ones a 3D sketch point's coordinates are measured along (X normal to Right, Y normal to Top, Z normal to Front), so numbers copied from a 3D sketch land where you expect.

1. A pop-up asks for **X**, **Y** and **Z**. They're in **mm** unless you pick another unit in the pop-up, which also says what units the document is in. You can also type a unit after a number (`2 in`, `50 mm`). An empty box is 0.
2. It makes the coordinate system **Origin'** at that point, placed by numbers (its X, Y, Z), with its axes along the part's X, Y and Z.
3. Built on Origin': **X' Plane**, **Y' Plane** and **Z' Plane**, each through Origin' and perpendicular to the axis it's named after.
4. It all goes in a folder called **New Origin**. Running it again adds ` 2` to the names so they stay unique.

**To move it**, edit Origin' (right-click it, Edit Feature) and change its numbers; the planes follow.

In the top level, mate Origin' to the assembly's origin or coordinate system (one coordinate system mate), or mate the planes to the assembly's planes.

### New from EBOM
Starts a new part or assembly from a row of the team's EBOM (the Master eBOM Google Sheet), already named and described. Works whatever is open, or with nothing open.

1. It reads the EBOM straight from the Master eBOM Google Sheet every time you click it (the sheet is published to the web as CSV: File > Share > Publish to web; the link is `TeamUrl` in `Ebom.cs`), and keeps a copy in `%LOCALAPPDATA%\BDAT\ebom.csv` for when you're offline. **To update the EBOM for everyone, just edit the sheet**; Google can take a few minutes to refresh the published copy. Nothing in BDAT needs changing. (Anyone with the link can read the published sheet; Ben confirmed nothing in it is confidential.) With no team copy and no internet, it asks you to pick a CSV and remembers it (`HKCU\Software\BDAT\EbomCsv`).
2. A pop-up lists the EBOM. Type to search part number, name, assembly or area (every word must match). Only current rows are listed; obsolete ones never show, since their numbers have been reused. It's a tree: each assembly is a bold, shaded row showing how many parts it has, closed to start with (**Expand all** / **Collapse all** open or close them all, and the area box shows just one area). Click its â–¸, double-click it or press â†’ to show its parts underneath (â† or double-click again closes it). A part's assembly is its assembly number: `A` plus the control number without its last two digits, padded to four (parts 70401 to 70499 are in A0704). Where the sheet has no current row for a part's own assembly number (e.g. 70501 with no A0705 row), the part goes under the current assembly it sits beneath in the sheet, if that's in the same system (A0704 Bellcranks), and saves to that assembly's folder. Only if there's none does it get a grey header like `A0402 (no assembly row in EBOM)`, which can be opened but not created. While you search, matching parts show under their assemblies, already open, and an assembly that matches shows all its parts.
3. Pick a row: the strip under the list says what it will make and which 3DEXPERIENCE folder it goes in. Click **Create part** / **Create assembly** (or press Enter; double-clicking a part also creates it). An **Assembly** row makes an assembly; a **Part** or **Fastener** row makes a part. It uses SolidWorks' default part or assembly template (Tools > Options > System Options > Default Templates).
4. The new document is titled with the combined part number (e.g. `BR-10101-AA`), so that's the name it saves under. Its `Description` property (file and every configuration) is the EBOM name, and `Part Number` is the combined part number.
5. With **Save to 3DEXPERIENCE** ticked (the default), it then saves it to 3DEXPERIENCE as e.g. `BR-10101-AA.SLDPRT` (the description goes up with it), adds it to its assembly's folder and checks it in, the same way Save MCM does. Folders are bookmarks named by assembly number, e.g. `A0704` for Bellcranks. When BDAT already knows the folder, it just asks "Save BR-70401-AA in A0704 (Bellcranks)?" (No picks a different one). When it doesn't, it searches the team's 3DEXPERIENCE folders for one named A0704 (or "A0704 something") and asks "Found the folder for A0704 (Bellcranks): Formula UBC Racing > ... > A0704. Save it there?". If there's no such folder, it says so and offers **Make folder A0704** (BDAT makes it in 3DEXPERIENCE, next to the other folders of that system, e.g. the other A07xx ones; you confirm the place or choose another), **Pick a folder...** (the bookmark picker, with a warning if what you pick isn't named A0704), **Save without folder** or **Cancel**. Either way the folder is remembered. (The connector has no official folder search, so `BookmarkSearch.cs` reads the folder tree the way the connector's own "download bookmark" does, read-only, starting from the McMaster Carr folder; if a 3DEXPERIENCE update breaks that, BDAT just falls back to the picker.) Cancel the picker to save without a folder.
   - Where the folders are kept: the team list `release/ebom-folders.csv` (Assembly Number, Bookmark Id, Bookmark Title), downloaded like the EBOM and used first; then this PC's picks (`HKCU\Software\BDAT\EbomFolders`, and `%LOCALAPPDATA%\BDAT\ebom-folders-picked.csv`). **Publish BDAT** merges the picks on the PC it runs on into the team list (a pick replaces the team's entry), so publish from the PC where the folders were picked. Untick **Save to 3DEXPERIENCE** to just get the open, unsaved document; untick **Check in** to keep it checked out (reserved) to you after saving, to carry on modelling it (remembered for next time). If it's already in 3DEXPERIENCE, or you're not logged in, nothing is saved and the document stays open. Log: `%TEMP%\BDAT\new-from-ebom.log`.

It never changes the Google Sheet.

### Update BDAT
Checks GitHub for a newer published BDAT. If there is one, it downloads it now (Windows asks for admin once) and installs it as soon as you close SolidWorks, so the next time you open SolidWorks you're on the new version.

### BDAT vN
Shows which version is running; click it for the full version and where it was loaded from. The number goes up by one every time Publish BDAT runs. Local builds show "dev build".

## Getting it and keeping it updated

Teammates: close SolidWorks and double-click **BDAT Setup.bat**. It downloads the latest build, registers it, and adds an **Update BDAT** desktop shortcut for later updates. It also installs the FUBC drawing sheet formats to `C:\ProgramData\BDAT\Sheet Formats` (from `release/sheet-formats`), which the FUBC Drawing template uses for sheets after the first. See [SETUP-NEW-COMPUTER.md](SETUP-NEW-COMPUTER.md).

To release a new version to the team, run **Publish BDAT.bat** on the developer PC. It gives the build the next version number (v1, v2, ...), opens Notepad with draft release notes for you to edit, and teammates pick it up with the **Update BDAT** button. All past notes are in [RELEASE-NOTES.md](RELEASE-NOTES.md).

## Build (once, on Windows)

Run `build.bat`. It uses the C# compiler that ships with Windows, so no Visual Studio or .NET SDK is needed.
If SolidWorks isn't in the default folder, pass its api\redist folder: `build.bat "D:\Your\Path\SOLIDWORKS\api\redist"`.
Keep the code C# 5 compatible (no `$"..."`, `?.`, `=>` members or `nameof`) so this keeps working.

Alternative: with Visual Studio 2022 or the .NET SDK installed, `dotnet build src\BDAT\BDAT.csproj -c Release`.

## Install

1. Close SolidWorks.
2. Right-click `install.bat` and choose **Run as administrator**.
3. Start SolidWorks. BDAT appears under **Tools > Add-Ins** (ticked to load at startup) and as a **BDAT** tab when a part or assembly is open.

To remove it, run `uninstall.bat` as administrator.

After rebuilding, you don't need to reinstall unless the DLL moved. Just close and reopen SolidWorks.

## Adding a new macro

1. Add a class in `src/BDAT/Commands/` that implements `IBdatCommand`.
2. Add it to the `_commands` list in `SwAddin.cs`.
3. Add an `OnXxx` / `CanXxx` callback pair at the bottom of `SwAddin.cs`.
4. Bump `CommandGroupVersion` in `SwAddin.cs` so SolidWorks refreshes the toolbar.
