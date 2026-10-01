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
- The toolbar lists Murder Part, Save MCM, Update BDAT and BDAT vN, in that order, and every callback name exists.
- Neither the version button nor Update BDAT launches anything.

**SolidWorks tests:**
- Sample parts are made in `%TEMP%\BDAT-tests\<time>`:
  - a McMaster-style part with a cosmetic thread and a `Threads` folder holding a cut
  - its `_murdered` copy
  - a two-body part
- The BDAT tab that SolidWorks has loaded has every button. This check is skipped if the loaded BDAT is an older installed build without test hooks.
- Murder Part:
  - The result is a new unsaved part with a single body, the right volume and no cosmetic thread.
  - The original file, its feature tree and its saved state are unchanged.
  - Nothing is left beside the part or in `%TEMP%\BDAT\murder`.
  - Answering No changes nothing.
- Save MCM:
  - The part number and description are filled in, and what's typed in the pop-up is used.
  - The Description property is set on the file and on every configuration.
  - A bad name and an empty description that's answered No both stop it.
  - The view ends up isometric and the freeze bar is at the end of the tree. "Enable Freeze bar" must be on in Tools > Options > General.
  - Save, add to bookmark and check-in are listed as skipped, and nothing reaches the connector. A separate unit test checks that every way into the connector (Find, Manager, Call, Get, Set) throws in test mode.
  - The part file is never saved.

## Publishing

`tests\gate.ps1` is the hook for `publish.ps1`. It runs after the build, and a non-zero exit stops the publish. If SolidWorks isn't available it skips the SolidWorks tests with a warning, unless you pass `-RequireSolidWorks`.
