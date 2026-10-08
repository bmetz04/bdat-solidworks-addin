# BDAT release notes

Newest first. Publish BDAT adds an entry here every time a new version goes out.

## v8 (2026-10-07 20:11)

**New from EBOM**
- Finds, makes or lets you pick the 3DEXPERIENCE folder before it makes the part, so cancelling at that step makes nothing. It also checks you're logged in to 3DEXPERIENCE first.
- Closing or cancelling a folder picker never makes a folder.
- Folders it makes get the assembly's name as their description (for example A0101 is described as "Balance Bar").
- If a folder it remembered was deleted in 3DEXPERIENCE, it notices, searches for the right one again, and offers to make or pick one if it can't find it.
- Two new buttons: Open team EBOM opens the team's Google Sheet, and Check 3DEXPERIENCE again re-checks which parts already exist.
- The existing-part check is about twice as fast, runs fresh every time you open it, shows "Checking 3DEXPERIENCE..." while it runs, and says when it last checked.

## v7 (2026-10-05 18:37)

- New button, **Waterjet DXF**: saves one DXF per cut list item that can be waterjet cut, into a folder named after the part wherever you choose. Bent sheet metal comes out as its flat pattern, and items thicker than 25.4 mm are left out. If the part has a configuration called "Waterjet", the DXFs come from that configuration. Your open part isn't changed.
- New button, **Name Cut List**: numbers the cut list items 001, 002, 003..., sheet metal first, then sorts the cut list by number. Items that already have a number keep it, and numbers already used by other features are skipped. A part with no cut list gets a Weldment feature first. Ctrl+Z can't undo the renames.
- New from EBOM greys out part numbers that already exist in 3DEXPERIENCE, so they can't be created twice.
- Long headings in BDAT pop-ups now wrap instead of running off the edge.

## v6 (2026-10-04 23:11)

- New button, **New from EBOM**: pick any part or assembly from the team EBOM (read live from the Google Sheet), check the description, and it's created with its EBOM number, ready to model.
- New from EBOM can save straight to 3DEXPERIENCE: it finds the assembly's folder by name, or offers to make it, pick another, or save without a folder. A Check in toggle chooses whether the part is checked in afterwards.
- New button, **Create Origin**: adds an offset origin with its own X, Y and Z planes, in parts and assemblies.
- Every BDAT pop-up has a new, cleaner look with the FUBC logo.
- Save MCM asks you to confirm the McMaster Carr folder before saving.
- The BDAT tab shows the same buttons in parts and assemblies, two rows high.
- New drawings can use the FUBC continuation sheet format.
- BDAT now tells you when 3DEXPERIENCE refuses to add a part to a folder, instead of saying it worked.

## v5 (2026-10-01 12:15)

- New Save MCM button: saves McMaster-Carr parts to 3DEXPERIENCE under Vendor CAD > McMaster Carr.
- Murder Part no longer merges bodies, so multi-body parts stay multi-body.

## v4 (2026-09-30 22:26)

- Update BDAT always gets the newest build, even right after a publish.

## v3 (2026-09-30 22:11)

- Fixed the BDAT tab only showing Murder Part. It now rebuilds itself so new buttons always appear.

## v2 (2026-09-30 22:07)

- New Update BDAT button: gets the latest version and installs it when you close SolidWorks.
- New version button that shows which BDAT version you are running.

## v1 (2026-09-30 21:55)

- First version for the team: the Murder Part button removes all threads (including McMaster-Carr Threads folders) and reopens the part as a single dumb solid.
