using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT.Commands
{
    /// <summary>
    /// Open from EBOM (it was New from EBOM): pick a row of the team's EBOM. If that part or assembly is in 3DEXPERIENCE
    /// it's downloaded and opened (PlatformParts); if it isn't, you're asked whether to make it, and then you get a new,
    /// unsaved part (or assembly, for an Assembly row) already named and described from it, as below.
    ///
    ///   1. Downloads the team's EBOM straight from the Master eBOM Google Sheet (published to the web as CSV), keeping
    ///      a copy for when it's offline. With neither, it asks for a CSV and remembers it for this Windows user.
    ///   2. A pop-up lists the EBOM's current rows, searchable by part number, name, assembly or area. Obsolete rows
    ///      never show: their numbers have been reused by current parts.
    ///   3. Creates a new document from SolidWorks' default part or assembly template, titled with the combined
    ///      part number (e.g. BR-10101-AA, the EBOM's "Use in 3Dx File Naming" column), so that's the name it's
    ///      saved under, and sets the Description and Part Number properties (Description in every configuration too).
    ///   4. Unless "Save to 3DEXPERIENCE" is unticked: saves it to 3DEXPERIENCE under that name (the description goes
    ///      up with it), adds it to its assembly's folder and checks it in. Folders are bookmarks named by assembly
    ///      number (A0704 for parts 70401 to 70499), which comes from the part's own control number, so it works even
    ///      where the EBOM has no assembly row. Each folder is picked once and then known to the whole team (EbomFolders);
    ///      a known folder is just confirmed; an unknown one is searched for by name, or made, or picked.
    /// </summary>
    public sealed class NewFromEbomCommand : IBdatCommand
    {
        public string Title { get { return "Open from EBOM"; } }

        public string Hint { get { return "Open a part or assembly of the EBOM from 3DX, or start it new, named and described for you"; } }

        internal const string DescriptionProperty = "Description";
        internal const string NumberProperty = "Part Number";

        public bool IsEnabled(ISldWorks swApp)
        {
            return true; // makes a new document, so nothing needs to be open
        }

        public void Run(ISldWorks swApp)
        {
            IWin32Window owner = SolidWorksWindow(swApp);

            string csv, source;
            if (TestMode.Enabled)
            {
                csv = TestMode.EbomCsvPath;
                source = "test EBOM";
            }
            else
            {
                bool fresh;
                csv = Ebom.TeamCopy(out fresh);
                source = csv == null ? null
                    : "Team EBOM" + (fresh ? "" : " (offline copy from " + File.GetLastWriteTime(csv).ToString("yyyy-MM-dd HH:mm") + ")");
                if (csv == null && Ebom.SavedPath != null && File.Exists(Ebom.SavedPath)) csv = Ebom.SavedPath;
            }

            if (csv == null || !File.Exists(csv))
            {
                if (TestMode.Enabled)
                {
                    Ui.Tell(swApp, "No EBOM file.", swMessageBoxIcon_e.swMbWarning);
                    return;
                }
                Ui.Show(owner,
                    "Couldn't download the team EBOM (are you online?), and there's no copy on this PC yet.\n\n" +
                    "To use a copy of your own: in Google Sheets, open the Master eBOM and use File > Download > " +
                    "Comma-separated values (.csv), then pick that file in the next window.",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                csv = PickCsv(owner, Ebom.SavedPath);
                if (csv == null) return;
                Ebom.SavedPath = csv;
            }

            List<EbomRow> rows = TryLoad(owner, csv);
            if (rows == null) return;

            bool saveToPlatform, checkIn, openRequested;
            string description;
            EbomRow row = Pick(owner, csv, source, rows, out saveToPlatform, out checkIn, out openRequested, out description);
            if (row == null) return;

            if (!TestMode.Enabled)
            {
                // Open (or a last check before making anything: someone may have just made it, in which case it's opened).
                // When you said "Make it?" Yes, this check already started in the background (Prefetch).
                bool? exists = PrefetchedExists(row);
                if (openRequested || exists == true)
                {
                    if (exists != false)
                    {
                        OpenFromPlatform(swApp, row, exists == true);
                        return;
                    }
                    // Not in 3DEXPERIENCE: offer to make it, the same way the pop-up does.
                    if (Ui.Show(owner, row.Number + " (" + row.Name + ") isn't in 3DX yet.\n\nMake it as a new " +
                            (row.IsAssembly ? "assembly" : "part") + "?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                        return;
                    BookmarkSearch.RefreshInBackground(SearchStartIds()); // the folder read runs while you name it
                    if (!NewFromEbomForm.AskDetails(owner, row, out description, out saveToPlatform, out checkIn)) return;
                }
            }

            // Decide where it goes in 3DEXPERIENCE before making anything, so cancelling here leaves nothing behind.
            Connector connector = null;
            Bookmark folder = null;
            if (saveToPlatform)
            {
                connector = Connector.Find();
                if (connector == null)
                {
                    Ui.Tell(swApp, "Couldn't find the 3DX connector in this SolidWorks session (is the \"3DEXPERIENCE PLM Services\" " +
                        "add-in on?), so nothing was made.\n\nUntick Save to 3DX to make it without saving.", swMessageBoxIcon_e.swMbWarning);
                    return;
                }
                if (!connector.IsConnected)
                {
                    Ui.Tell(swApp, "You're not logged in to 3DX, so nothing was made.\n\nLog in from the 3DX task pane, " +
                        "or untick Save to 3DX to make it without saving.", swMessageBoxIcon_e.swMbWarning);
                    return;
                }
                bool cancelled;
                folder = ResolveFolder(connector, owner, row, out cancelled);
                if (cancelled) return; // nothing made
            }

            swDocumentTypes_e type = row.IsAssembly ? swDocumentTypes_e.swDocASSEMBLY : swDocumentTypes_e.swDocPART;
            string template = swApp.GetUserPreferenceStringValue((int)(row.IsAssembly
                ? swUserPreferenceStringValue_e.swDefaultTemplateAssembly
                : swUserPreferenceStringValue_e.swDefaultTemplatePart));
            if (string.IsNullOrEmpty(template) || !File.Exists(template))
            {
                Ui.Tell(swApp, "SolidWorks has no default " + (row.IsAssembly ? "assembly" : "part") + " template set, so nothing was made. " +
                    "Set one in Tools > Options > System Options > Default Templates.", swMessageBoxIcon_e.swMbStop);
                return;
            }

            IModelDoc2 doc = swApp.NewDocument(template, 0, 0, 0) as IModelDoc2;
            if (doc == null || doc.GetType() != (int)type)
            {
                Ui.Tell(swApp, "SolidWorks didn't make the new " + (row.IsAssembly ? "assembly" : "part") + " from " + template + ".", swMessageBoxIcon_e.swMbStop);
                return;
            }

            doc.SetTitle2(row.Number);
            SetProperties(doc, row, description);

            if (TestMode.Enabled)
            {
                string savedDescription, resolved;
                doc.Extension.get_CustomPropertyManager("").Get4(DescriptionProperty, false, out savedDescription, out resolved);
                // Saving needs 3DEXPERIENCE, so test mode only records where it would have gone.
                TestMode.LastNewFromEbom = new NewFromEbomTestResult
                {
                    Number = row.Number,
                    Title = doc.GetTitle(),
                    Description = savedDescription,
                    IsAssembly = doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY,
                    Folder = row.AssemblyNumber,
                    FileName = FileName(row),
                };
                return;
            }

            if (saveToPlatform) SaveToPlatform(swApp, connector, doc, row, description, checkIn, folder);
        }

        private static EbomRow Pick(IWin32Window owner, string csv, string source, List<EbomRow> rows, out bool saveToPlatform, out bool checkIn,
            out bool openRequested, out string description)
        {
            saveToPlatform = false;
            checkIn = true;
            openRequested = false; // test mode always creates
            description = null;
            if (TestMode.Enabled)
            {
                foreach (EbomRow r in rows)
                {
                    if (!string.Equals(r.Number, TestMode.EbomPick, StringComparison.OrdinalIgnoreCase)) continue;
                    description = NewFromEbomDetailsForm.Clean(TestMode.EbomDescription ?? r.Name);
                    return r;
                }
                return null; // Cancel
            }

            using (var form = new NewFromEbomForm(csv, source, rows))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return null;
                saveToPlatform = form.SaveToPlatform;
                checkIn = form.CheckIn;
                openRequested = form.OpenRequested;
                description = form.Description;
                return form.Selected;
            }
        }

        /// <summary>
        /// Finds the row's part or assembly in 3DEXPERIENCE, downloads it and opens it. Only reads from 3DEXPERIENCE.
        /// known: the title search found it, so not finding its id is a problem to report rather than "it isn't there".
        /// </summary>
        private void OpenFromPlatform(ISldWorks swApp, EbomRow row, bool known)
        {
            string label = row.Number + " (" + row.Name + ")";
            Connector connector = Connector.Find();
            if (connector == null || !connector.IsConnected)
            {
                Ui.Tell(swApp, (connector == null
                    ? "Couldn't find the 3DX connector in this SolidWorks session (is the \"3DEXPERIENCE PLM Services\" add-in on?)"
                    : "You're not logged in to 3DX") + ", so " + label + " wasn't opened.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            string error;
            Cursor.Current = Cursors.WaitCursor;
            string id = PlatformParts.FindId(row.Number, out error);
            string path = id == null ? null : PlatformParts.Download(connector, id, out error);
            Cursor.Current = Cursors.Default;
            if (path == null)
            {
                Ui.Tell(swApp, (known ? label + " is in 3DX, but BDAT couldn't open it: " : "BDAT couldn't open " + label + ": ") + error +
                    "\n\nOpen it from the 3DX task pane instead (search for " + row.Number + ").", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            int type = path.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocASSEMBLY
                : path.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocDRAWING : (int)swDocumentTypes_e.swDocPART;
            int errors = 0, warnings = 0;
            IModelDoc2 doc = swApp.OpenDoc6(path, type, (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref errors, ref warnings) as IModelDoc2;
            if (doc == null)
            {
                Ui.Tell(swApp, "3DX downloaded " + label + ", but SolidWorks didn't open it (error " + errors + ").\n\n" + path,
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }
            int activateErrors = 0;
            swApp.ActivateDoc3(doc.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
            PlatformSave.Log(LogName, "open: opened " + row.Number + " from " + path);
        }

        /// <summary>The file name it's saved under: the combined part number, e.g. BR-10101-AA.SLDPRT.</summary>
        internal static string FileName(EbomRow row)
        {
            return row.Number + (row.IsAssembly ? ".SLDASM" : ".SLDPRT");
        }

        // ---------------------------------------------------------------- 3DEXPERIENCE

        private const string LogName = "new-from-ebom";

        /// <summary>
        /// Saves the new document to 3DEXPERIENCE as e.g. BR-10101-AA (the Description properties go up with it), puts it
        /// in its assembly's folder (the bookmark named by the assembly number, e.g. A0101), and checks it in.
        /// If anything stops it, the document stays open and unsaved, and it says why.
        /// </summary>
        private void SaveToPlatform(ISldWorks swApp, Connector connector, IModelDoc2 doc, EbomRow row, string description, bool checkIn, Bookmark folder)
        {
            // A small window while it works, so the wait for 3DX doesn't look like SolidWorks has frozen.
            ModernUi.BusyWindow busy = ModernUi.Busy(SolidWorksWindow(swApp), "Saving " + row.Number + " to 3DX...", "Saving the file...");
            try
            {
                string fileName = FileName(row);
                string notSaved = "\n\n" + row.Number + " is open but not saved. Save it to 3DX by hand, or close it.";

                var platform = new PlatformSave(swApp, connector, LogName);
                string existing = platform.ExistingOnPlatform(doc, fileName);
                if (existing != null)
                {
                    busy.Close(); Ui.Tell(swApp, row.Number + " is already in 3DX (" + existing + "), so the new one wasn't saved.\n\n" +
                        "Open the existing one instead, and close this new one without saving.", swMessageBoxIcon_e.swMbWarning);
                    return;
                }

                busy.Step("Uploading it to 3DX (about 10 seconds)...");
                bool savedOk = platform.Save(doc, fileName);
                if (savedOk) ExistingParts.Remember(row.Number);
                if (!savedOk)
                {
                    busy.Close(); Ui.Tell(swApp, "3DX didn't save " + row.Number + ". Check the 3DX task pane for details." + notSaved,
                        swMessageBoxIcon_e.swMbStop);
                    return;
                }

                string where = folder == null ? "" : " in " + FolderLabel(row);
                string done;
                swMessageBoxIcon_e icon = swMessageBoxIcon_e.swMbInformation;
                busy.Step(folder == null ? "Finishing..." : "Putting it in " + FolderLabel(row) + "...");
                string phid = folder == null ? null : platform.WaitForPhysicalId(doc);
                if (folder != null && string.IsNullOrEmpty(phid))
                {
                    done = "Saved " + row.Number + " to 3DX, but it didn't show up within " + PlatformSave.SaveWaitSeconds +
                        " seconds, so it wasn't put in " + FolderLabel(row) + ".\n\nOnce the save finishes, add it by hand " +
                        "(right-click it in 3DX > Add to Bookmark).";
                    icon = swMessageBoxIcon_e.swMbWarning;
                    where = "";
                }
                else
                {
                    string refused = folder == null ? null : platform.AddToBookmark(folder.Id, phid);
                    if (refused != null)
                    {
                        done = "Saved " + row.Number + " (" + description + ") to 3DX, but it couldn't be put in " +
                            FolderLabel(row) + ":\n\n" + refused + PlatformSave.BookmarkAdvice(refused);
                        icon = swMessageBoxIcon_e.swMbWarning;
                    }
                    else
                        done = "Saved " + row.Number + " (" + description + ") to 3DX" + where + ".";
                }

                if (checkIn)
                {
                    if (platform.Unlock(doc.GetPathName())) done += "\n\nChecked in.";
                    else
                    {
                        done += "\n\nIt couldn't be checked in, so it's still locked by you. Unlock it from the 3DX task pane " +
                            "(right-click it > Unlock).";
                        icon = swMessageBoxIcon_e.swMbWarning;
                    }
                }
                else
                {
                    // Keep it checked out (reserved) to you, to carry on modelling it.
                    if (platform.Reserve(doc.GetPathName(), true)) done += "\n\nKept checked out to you. Check it in from the 3DX task pane when you're done.";
                    else
                    {
                        done += "\n\nIt couldn't be kept checked out to you. Reserve it from the 3DX task pane (right-click it > Reserve).";
                        icon = swMessageBoxIcon_e.swMbWarning;
                    }
                }
                busy.Close(); Ui.Tell(swApp, done, icon);
            }
            finally
            {
                busy.Close();
            }
        }

        /// <summary>The pop-up's "Check in" box, remembered for this Windows user (ticked unless you've unticked it).</summary>
        internal static bool CheckInPreference
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\BDAT"))
                    {
                        object value = key == null ? null : key.GetValue("EbomCheckIn");
                        return !(value is string) || (string)value != "0";
                    }
                }
                catch (Exception)
                {
                    return true;
                }
            }
            set
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\BDAT"))
                    {
                        if (key != null) key.SetValue("EbomCheckIn", value ? "1" : "0", RegistryValueKind.String);
                    }
                }
                catch (Exception)
                {
                    // Just not remembered.
                }
            }
        }

        private static string FolderLabel(EbomRow row)
        {
            string name = row.IsAssembly ? row.Name : row.Parent;
            return row.AssemblyNumber + (name.Length > 0 ? " (" + name + ")" : "");
        }

        /// <summary>
        /// The folder for the row's assembly number. A known one (team list or this PC, see EbomFolders) is confirmed with
        /// "Save BR-70401-AA in A0704 (Bellcranks)?": Yes uses it, No opens the picker. An unknown one is picked now and
        /// remembered for the team. Null with cancelled false means save without a folder.
        /// </summary>
        private Bookmark ResolveFolder(Connector connector, IWin32Window owner, EbomRow row, out bool cancelled)
        {
            cancelled = false;
            string number = row.AssemblyNumber;
            string label = FolderLabel(row);
            if (number.Length == 0) return AskNoFolder(owner, row, "It has no assembly number in the EBOM.", out cancelled);

            // A remembered folder is only used if it's still in 3DEXPERIENCE (it may have been deleted or moved), so the
            // folder tree must be fresh: read again unless Prefetch just read it in the background.
            if (!BookmarkSearch.ReadWithin(TimeSpan.FromMinutes(2))) BookmarkSearch.Refresh();
            Bookmark known = null;
            foreach (Bookmark candidate in EbomFolders.Candidates(number))
            {
                bool? alive = BookmarkSearch.StillExists(candidate.Id, SearchStartIds());
                if (alive == false)
                {
                    EbomFolders.Forget(number, candidate.Id);
                    continue;
                }
                known = candidate; // still there, or couldn't check (carry on as before)
                break;
            }
            if (known != null)
            {
                string title = string.Equals(known.Title, number, StringComparison.OrdinalIgnoreCase) ? label : known.Title + " (" + label + ")";
                DialogResult use = Ui.Show(owner, "Save " + row.Number + " in " + title + "?\n\nNo picks a different folder.",
                    Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (use == DialogResult.Yes) return known;
                if (use != DialogResult.No) { cancelled = true; return null; }
            }
            else
            {
                // Not known yet: look for a folder named after it in 3DEXPERIENCE. If there isn't one, you can make it
                // and search again, pick another folder, or save without one.
                while (true)
                {
                    List<FoundBookmark> hits = BookmarkSearch.Find(number, SearchStartIds());
                    if (hits.Count > 0)
                    {
                        FoundBookmark best = hits[0];
                        string others = hits.Count > 1 ? "\n\n" + (hits.Count - 1) + " other folder" + (hits.Count == 2 ? " is" : "s are") + " named like it. No lets you pick." : "";
                        DialogResult use = Ui.Show(owner, "Found the folder for " + label + ":\n\n" + best.Path + "\n\nSave " + row.Number + " there?" + others,
                            Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                        if (use == DialogResult.Yes)
                        {
                            var folder = new Bookmark { Id = best.Id, Title = best.Title };
                            EbomFolders.Remember(number, folder);
                            PlatformSave.Log(LogName, "folder for " + number + " found by name = " + best.Id + " (" + best.Path + ")");
                            return folder;
                        }
                        if (use != DialogResult.No) { cancelled = true; return null; }
                        break; // No: pick a different one
                    }

                    string why = BookmarkSearch.Available
                        ? "There's no folder named " + number + " in 3DX yet."
                        : "BDAT couldn't search the 3DX folders just now.";
                    int choice = Ui.Choose(owner,
                        "Where should " + row.Number + " go? It belongs in " + label + ".\n\n" + why + "\n\n" +
                        "BDAT can make the " + number + " folder for you, or you can pick an existing folder, or save it " +
                        "without a folder for now.",
                        Title, MessageBoxIcon.Warning, "Make folder " + number, "Pick a folder...", "Save without folder", "Cancel");
                    if (choice == 0)
                    {
                        Bookmark made = MakeFolder(connector, owner, number, label, row.IsAssembly ? row.Name : row.Parent);
                        if (made != null) return made;
                        continue; // back to the choices
                    }
                    if (choice == 1) break;
                    if (choice == 2) return null; // save without a folder
                    cancelled = true;
                    return null;
                }
            }

            Bookmark picked = PlatformSave.ChooseBookmark(connector, owner, "Pick " + label);
            if (picked == null) return AskNoFolder(owner, row, "No folder was picked.", out cancelled);
            if (picked.Title.Length == 0) picked.Title = number;

            if (!EbomFolders.TitleMatches(picked.Title, number) &&
                Ui.Show(owner, "You picked \"" + picked.Title + "\", which isn't named " + number + ". Use it for " + number + " from now on?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            {
                cancelled = true;
                return null;
            }

            EbomFolders.Remember(number, picked);
            PlatformSave.Log(LogName, "folder for " + number + " = " + picked.Id + " (" + picked.Title + ")");
            return picked;
        }

        /// <summary>
        /// "Make folder": makes a bookmark named after the assembly number, where the other assembly folders of that system
        /// are (you confirm the place, or choose another), and remembers it. Null if it wasn't made (back to the choices).
        /// </summary>
        private Bookmark MakeFolder(Connector connector, IWin32Window owner, string number, string label, string assemblyName)
        {
            string parentId = null, parentPath = null;
            FoundBookmark suggested = BookmarkSearch.SuggestParent(number, SearchStartIds());
            if (suggested != null)
            {
                int where = Ui.Choose(owner, "Make a folder named " + number + " for " + label + " in:\n\n" + suggested.Path +
                    "\n\nThat's where the other assembly folders like it are.", Title, MessageBoxIcon.Question,
                    "Make it here", "Choose another place...", "Back");
                if (where == 2) return null;
                if (where == 0) { parentId = suggested.Id; parentPath = suggested.Path; }
            }
            if (parentId == null)
            {
                Bookmark place = PlatformSave.ChooseBookmark(connector, owner, "Pick where to make the " + number + " folder");
                if (place == null) return null;
                parentId = place.Id;
                parentPath = string.IsNullOrEmpty(place.Title) ? "the folder you picked" : place.Title;
                // Say exactly where before making anything, so a closed or mistaken pick never makes a folder.
                if (Ui.Choose(owner, "Make a folder named " + number + " for " + label + " in:\n\n" + parentPath + "?",
                        Title, MessageBoxIcon.Question, "Make it here", "Back") != 0)
                    return null;
            }

            string error;
            // The folder's description is the assembly's name, e.g. "Balance Bar" for A0101.
            FoundBookmark made = BookmarkSearch.Create(parentId, parentPath, number, string.IsNullOrEmpty(assemblyName) ? null : assemblyName, out error);
            if (made == null)
            {
                Ui.Show(owner, "Couldn't make the " + number + " folder: " + error + "\n\nYou can make it in 3DX yourself, or pick a folder instead.",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            var folder = new Bookmark { Id = made.Id, Title = made.Title };
            EbomFolders.Remember(number, folder);
            return folder;
        }

        // ---------------------------------------------------------------- reading ahead

        private static string _prefetchNumber;
        private static DateTime _prefetchTime;
        private static System.Threading.Tasks.Task<bool?> _prefetchExists;

        /// <summary>
        /// Starts the slow 3DX look-ups for making this row on background threads, as soon as you say "Make it?" Yes, so
        /// they run while you're still in the naming pop-up: the last "is it in 3DX already?" check and a fresh read of
        /// the folder tree. Read-only. The same checks still happen; they're just usually finished by the time you click.
        /// </summary>
        internal static void Prefetch(EbomRow row)
        {
            if (TestMode.Enabled || row == null) return;
            string number = row.Number;
            _prefetchNumber = number;
            _prefetchTime = DateTime.Now;
            bool isAssembly = row.IsAssembly;
            // A full check from the pop-up in the last minute already answers it; otherwise search just this kind.
            bool? recent = ExistingParts.Recently(number, TimeSpan.FromMinutes(1));
            _prefetchExists = recent.HasValue
                ? System.Threading.Tasks.Task.FromResult<bool?>(recent)
                : System.Threading.Tasks.Task.Run<bool?>(delegate { return ExistingParts.Exists(number, isAssembly); });
            BookmarkSearch.RefreshInBackground(SearchStartIds());
        }

        // The background "already in 3DX?" answer for this number if one was started, otherwise asked now.
        private static bool? PrefetchedExists(EbomRow row)
        {
            string number = row.Number;
            System.Threading.Tasks.Task<bool?> task = _prefetchExists;
            // Only a check for this number, started in the last few minutes (an older answer could be out of date).
            bool mine = task != null && string.Equals(_prefetchNumber, number, StringComparison.OrdinalIgnoreCase) &&
                DateTime.Now - _prefetchTime < TimeSpan.FromMinutes(3);
            _prefetchExists = null;
            _prefetchNumber = null;
            if (!mine)
            {
                bool? recent = ExistingParts.Recently(number, TimeSpan.FromMinutes(1));
                return recent ?? ExistingParts.Exists(number, row.IsAssembly);
            }
            try { return task.Result; }
            catch (Exception) { return ExistingParts.Exists(number, row.IsAssembly); }
        }

        /// <summary>Folders BDAT already knows, to find the team's top folder from: McMaster Carr, then every remembered one.</summary>
        private static List<string> SearchStartIds()
        {
            var ids = new List<string> { SaveMcmCommand.KnownBookmarkId };
            foreach (Bookmark b in EbomFolders.All().Values)
                if (!ids.Contains(b.Id)) ids.Add(b.Id);
            return ids;
        }

        private Bookmark AskNoFolder(IWin32Window owner, EbomRow row, string why, out bool cancelled)
        {
            cancelled = Ui.Show(owner, why + "\n\nSave " + row.Number + " to 3DX without putting it in a folder?",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes;
            return null;
        }

        /// <summary>Description in the file and every configuration (3DEXPERIENCE reads both); Part Number in the file.</summary>
        private static void SetProperties(IModelDoc2 doc, EbomRow row, string description)
        {
            var targets = new List<string> { "" };
            string[] configs = doc.GetConfigurationNames() as string[];
            if (configs != null) targets.AddRange(configs);

            foreach (string config in targets)
            {
                CustomPropertyManager props = doc.Extension.get_CustomPropertyManager(config);
                if (props == null) continue;
                props.Add3(DescriptionProperty, (int)swCustomInfoType_e.swCustomInfoText, description,
                    (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
                if (config.Length == 0)
                    props.Add3(NumberProperty, (int)swCustomInfoType_e.swCustomInfoText, row.Number,
                        (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            }
        }

        /// <summary>Asks for the EBOM CSV. Null if cancelled.</summary>
        internal static string PickCsv(IWin32Window owner, string current)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "Pick the EBOM (downloaded from Google Sheets as .csv)";
                dialog.Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*";
                if (!string.IsNullOrEmpty(current))
                {
                    try
                    {
                        string folder = Path.GetDirectoryName(current);
                        if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
                    }
                    catch (ArgumentException)
                    {
                        // A bad saved path: start wherever Windows likes.
                    }
                }
                return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        /// <summary>Reads the CSV, or says why it can't and returns null.</summary>
        internal static List<EbomRow> TryLoad(IWin32Window owner, string path)
        {
            try
            {
                return Ebom.Load(path);
            }
            catch (Exception ex)
            {
                string why = ex is IOException && !(ex is FileNotFoundException)
                    ? "it's open in another program (close Excel and try again)." : ex.Message;
                Ui.Show(owner, "Couldn't read " + Path.GetFileName(path) + ": " + why, "Open from EBOM",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
        }

        private static IWin32Window SolidWorksWindow(ISldWorks swApp)
        {
            IFrame frame = swApp.Frame() as IFrame;
            return new WindowHandle(frame == null ? IntPtr.Zero : new IntPtr(frame.GetHWndx64()));
        }

        private sealed class WindowHandle : IWin32Window
        {
            private readonly IntPtr _handle;
            public WindowHandle(IntPtr handle) { _handle = handle; }
            public IntPtr Handle { get { return _handle; } }
        }
    }
}
