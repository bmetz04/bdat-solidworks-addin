# BDAT tests

Nothing here ever signs in to, opens or saves to 3DEXPERIENCE. BDAT has a **test mode** for that:

- On when the `BDAT_TEST_MODE` environment variable is `1` (the runner sets it on any SolidWorks it starts), or while the test harness runs a command.
- Every dialog is skipped. Its text is recorded, and the dialog is answered from a queue (Yes/OK by default).
- Save MCM runs its pop-up logic, naming and description, records what it would have saved, and stops.
- Any call into the 3DEXPERIENCE connector throws and is counted. Every test fails if that count isn't 0.
- Update BDAT only checks the version. It never downloads or starts the installer.

## Running

```powershell
.\tests\run-tests.ps1                      # unit tests only, no SolidWorks needed
.\tests\run-tests.ps1 -SolidWorks          # also run the SolidWorks tests in an open SolidWorks with no documents
.\tests\run-tests.ps1 -SolidWorks -Launch  # start a SolidWorks for the tests if none is running, close it after
```

Exit codes: 0 passed, 1 failed, 2 build problem, 3 SolidWorks tests couldn't run.

The SolidWorks tests never close a SolidWorks they didn't start. They won't run at all if SolidWorks has a document open.

## What's tested

**Unit tests (no SolidWorks):**
- Save MCM parsing: the name is the part number before the first underscore, and the description is everything after it. Covers no underscore, several underscores, `_murdered` and extra spaces.
- Save MCM name validation.
- The connector refuses to start in test mode.
- The toolbar lists Murder Part, Save MCM, Create Origin, New from EBOM, Update BDAT and BDAT vN, in that order, and every callback name exists.
- New from EBOM reads the EBOM CSV by column heading (quoted commas and line breaks, blank rows skipped), names assemblies from the Assembly column and parts from the component name, finds each part's assembly by control number (not row order), hides obsolete rows, and refuses a CSV with no Combined Part # column.
- Create Origin uses SolidWorks' own X, Y and Z, like a 3D sketch point.
- Create Origin offers mm first (the default), cm, m, in and ft, names the document's units, and tries every quarter-turn rotation for Origin' once each, likely ones first.
- Create Origin reads coordinates in the picked unit or with a typed unit (mm, cm, m, in, ", ft), refuses anything that isn't a number, and formats the point for messages (`(10, -20.5, 0 mm)`).
- Neither the version button nor Update BDAT launches anything.

**SolidWorks tests:**
- Sample parts are made in `%TEMP%\BDAT-tests\<time>`:
  - a McMaster-style part with a cosmetic thread and a `Threads` folder holding a cut
  - its `_murdered` copy
  - a two-body part
- The BDAT tab that SolidWorks has loaded has every button. This check is skipped if the loaded BDAT is an older installed build without test hooks.
- Murder Part:
  - The result is a new unsaved part with the right volume and no cosmetic thread. A single-body part stays one body. A multi-body part may stay multi-body, so for it only the total volume is checked.
  - The original file, its feature tree and its saved state are unchanged.
  - Nothing is left beside the part or in `%TEMP%\BDAT\murder`.
  - Answering No changes nothing.
- Save MCM:
  - The part number and description are filled in, and what's typed in the pop-up is used.
  - The Description property is set on the file and on every configuration.
  - A bad name and an empty description that's answered No both stop it.
  - The view ends up isometric and the freeze bar is at the end of the tree. Save MCM turns on the "Enable Freeze bar" option, and the tests put your setting back afterwards.
  - Save, add to bookmark and check-in are listed as skipped, and nothing reaches the connector. Unit tests check that every way into the connector (Find, Manager, Call, Get, Set) throws in test mode, and that Save MCM's check-in step (Unlock) is refused at the connector and reports "not checked in".
  - The part file is never saved.

- Create Origin, in new unsaved parts and an assembly that are closed without saving:
  - With nothing open the button is greyed out and only says to open a part or assembly.
  - It makes the Origin' coordinate system and X', Y' and Z' planes (each perpendicular to its axis), all in a `New Origin` folder. Origin' is checked to be at the typed point with the part's own X, Y and Z, and every plane to be built on Origin' (not an offset from a standard plane), so it follows when Origin' moves. Each plane's distance to a 3D sketch point is measured with SolidWorks' Measure tool, which proves it goes through the point and is on the right side of the origin. This covers negative numbers, plain numbers read as mm, a 0 coordinate, typed inches and an assembly.
  - Running it twice in the same part keeps every name unique.
  - Cancel, or a box that isn't a number, adds nothing to the part.

**Real parts:** every `.SLDPRT` in `C:\Users\bacon\BDAT\test-parts` is used too. You can change the folder with `-PartsDir`.
- The folder sits outside the repo on purpose, because McMaster's CAD files shouldn't go on GitHub.
- Each part is copied to the run's temp folder, and only the copy is opened.
- Murder Part on each copy: the result opens with solid volume and no cosmetic thread, the copy is untouched, and nothing is left behind. Body count isn't checked.
- Save MCM on each copy: the name and description come from the file name, nothing is saved, and 3DEXPERIENCE is contacted 0 times.
- After the run, the library file itself is unchanged.
- If the folder is missing or empty, these tests are skipped.

## Publishing

`tests\gate.ps1` is the hook for `publish.ps1`. It runs after the build, and a non-zero exit stops the publish. If SolidWorks isn't available it skips the SolidWorks tests with a warning, unless you pass `-RequireSolidWorks`.
