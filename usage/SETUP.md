# BDAT anonymous usage

BDAT can send one row to a Google Sheet each time a button is used: the button, whether it worked, the error text
if it didn't (with paths, file names and e-mail addresses removed), how long it took, the BDAT and SolidWorks
versions, and a random id per Windows user. Nothing else: no names, no file or part names, nothing from a part.

It is sent in the background and never slows a button down. If the PC is offline the row is dropped.

## Who sends it

Only builds made on a PC that has `%USERPROFILE%\BDAT\usage-key.txt` (`C:\Users\bacon\BDAT\usage-key.txt` on
Ben's PC). build.bat embeds that file in BDAT.dll. The file is outside the repo, so the web address and key are
never on GitHub. Builds without it (other PCs, Claude's builds) send nothing.

Nothing is sent until the user agrees: the first time they click a BDAT button, a pop-up asks whether to share
(closing it counts as no). They can change it later on the BDAT version button.

It is also off:
- in test mode, so test runs never reach the Sheet,
- for anyone who clicks **Turn sharing off** on the BDAT version button (per Windows user),
- on any PC with a `C:\ProgramData\BDAT\no-usage.txt` file.

## Setting it up (once)

1. Make a new Google Sheet, e.g. "BDAT usage". Keep it private.
2. In the Sheet: **Extensions > Apps Script**. Replace everything there with `usage/UsageSheet.gs` from this repo.
3. Make up a random key (for example, 32 random letters and numbers) and paste it into `const KEY = '...'`. Save.
4. **Deploy > New deployment**, type **Web app**. Execute as: **Me**. Who has access: **Anyone**. Deploy, allow
   access when Google asks, and copy the **Web app URL** (ends in `/exec`).
5. Make `C:\Users\bacon\BDAT\usage-key.txt` with two lines:
   ```
   https://script.google.com/macros/s/.../exec
   your-random-key
   ```
6. Build BDAT (build.bat). It says "Anonymous usage sharing is built in." when it found the file.

## Daily crash e-mail (optional)

In the Apps Script editor, pick **setUpDailyReport** in the function list next to Run and click **Run** once. Allow
access when Google asks (it now needs to send you e-mail). Every morning around 7 it then e-mails you the crashes and
errors from the last 24 hours, grouped by button and message. No e-mail on days with none. Run **dailyReport** by
hand to try it. To stop it, delete the trigger under **Triggers** (the clock icon).

## If junk shows up

Change `KEY` in the script, then **Deploy > Manage deployments > Edit > Version: New version > Deploy**. Put the new
key in usage-key.txt and publish BDAT again. Older BDAT builds stop being able to add rows until they update.
