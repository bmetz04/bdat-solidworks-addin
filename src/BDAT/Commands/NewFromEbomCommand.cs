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
    /// New from EBOM: pick a row of the team's EBOM and get a new, unsaved part (or assembly, for an Assembly row)
    /// already named and described from it.
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
    ///      a known folder is just confirmed. "Set up folders..." in the pop-up picks them all in one go.
    /// </summary>
    public sealed class NewFromEbomCommand : IBdatCommand
    {
        public string Title { get { return "New from EBOM"; } }

        public string Hint { get { return "Start a new part or assembly from a row of the EBOM, named and described for you"; } }

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

            bool saveToPlatform;
            string description;
            EbomRow row = Pick(owner, csv, source, rows, out saveToPlatform, out description);
            if (row == null) return;

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

            if (saveToPlatform) SaveToPlatform(swApp, owner, doc, row, description);
        }

        private static EbomRow Pick(IWin32Window owner, string csv, string source, List<EbomRow> rows, out bool saveToPlatform, out string description)
        {
            saveToPlatform = false;
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
                description = form.Description;
                return form.Selected;
            }
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
        private void SaveToPlatform(ISldWorks swApp, IWin32Window owner, IModelDoc2 doc, EbomRow row, string description)
        {
            string fileName = FileName(row);
            string notSaved = "\n\n" + row.Number + " is open but not saved. Save it to 3DEXPERIENCE by hand, or close it.";

            Connector connector = Connector.Find();
            if (connector == null)
            {
                Ui.Tell(swApp, "Couldn't find the 3DEXPERIENCE connector in this SolidWorks session " +
                    "(is the \"3DEXPERIENCE PLM Services\" add-in on?)." + notSaved, swMessageBoxIcon_e.swMbWarning);
                return;
            }
            if (!connector.IsConnected)
            {
                Ui.Tell(swApp, "You're not logged in to 3DEXPERIENCE." + notSaved, swMessageBoxIcon_e.swMbWarning);
                return;
            }

            var platform = new PlatformSave(swApp, connector, LogName);
            string existing = platform.ExistingOnPlatform(doc, fileName);
            if (existing != null)
            {
                Ui.Tell(swApp, row.Number + " is already in 3DEXPERIENCE (" + existing + "), so the new one wasn't saved.\n\n" +
                    "Open the existing one instead, and close this new one without saving.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            bool cancelled;
            Bookmark folder = ResolveFolder(connector, owner, row, out cancelled);
            if (cancelled)
            {
                Ui.Tell(swApp, "Nothing was saved." + notSaved, swMessageBoxIcon_e.swMbInformation);
                return;
            }

            if (!platform.Save(doc, fileName))
            {
                Ui.Tell(swApp, "3DEXPERIENCE didn't save " + row.Number + ". Check the 3DEXPERIENCE task pane for details." + notSaved,
                    swMessageBoxIcon_e.swMbStop);
                return;
            }

            string where = folder == null ? "" : " in " + FolderLabel(row);
            string done;
            swMessageBoxIcon_e icon = swMessageBoxIcon_e.swMbInformation;
            string phid = folder == null ? null : platform.WaitForPhysicalId(doc);
            if (folder != null && string.IsNullOrEmpty(phid))
            {
                done = "Saved " + row.Number + " to 3DEXPERIENCE, but it didn't show up within " + PlatformSave.SaveWaitSeconds +
                    " seconds, so it wasn't put in " + FolderLabel(row) + ".\n\nOnce the save finishes, add it by hand " +
                    "(right-click it in 3DEXPERIENCE > Add to Bookmark).";
                icon = swMessageBoxIcon_e.swMbWarning;
                where = "";
            }
            else
            {
                string refused = folder == null ? null : platform.AddToBookmark(folder.Id, phid);
                if (refused != null)
                {
                    done = "Saved " + row.Number + " (" + description + ") to 3DEXPERIENCE and checked it in, but it couldn't be put in " +
                        FolderLabel(row) + ":\n\n" + refused + PlatformSave.BookmarkAdvice(refused);
                    icon = swMessageBoxIcon_e.swMbWarning;
                }
                else
                    done = "Saved " + row.Number + " (" + description + ") to 3DEXPERIENCE" + where + " and checked it in.";
            }

            if (!platform.Unlock(doc.GetPathName()))
            {
                done += "\n\nIt couldn't be checked in, so it's still locked by you. Unlock it from the 3DEXPERIENCE task pane " +
                    "(right-click it > Unlock).";
                icon = swMessageBoxIcon_e.swMbWarning;
            }
            Ui.Tell(swApp, done, icon);
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

            Bookmark known = EbomFolders.Find(number);
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
                        ? "There's no folder named " + number + " in 3DEXPERIENCE yet."
                        : "BDAT couldn't search the 3DEXPERIENCE folders just now.";
                    int choice = Ui.Choose(owner,
                        "Where should " + row.Number + " go? It belongs in " + label + ".\n\n" + why + "\n\n" +
                        "BDAT can make the " + number + " folder for you, or you can pick an existing folder, or save it " +
                        "without a folder for now.",
                        Title, MessageBoxIcon.Warning, "Make folder " + number, "Pick a folder...", "Save without folder", "Cancel");
                    if (choice == 0)
                    {
                        Bookmark made = MakeFolder(connector, owner, number, label);
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
        /// True when this BDAT runs from a copy of the repo that has publish.ps1 (the PC BDAT is published from), not from a
        /// teammate's install. "Set up folders..." only shows there, since that's where Publish BDAT picks up the folders.
        /// </summary>
        internal static bool IsPublishingPc
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(typeof(NewFromEbomCommand).Assembly.Location);
                    for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                    {
                        // .git is a folder in a normal clone and a file in a git worktree.
                        string git = Path.Combine(dir, ".git");
                        if (File.Exists(Path.Combine(dir, "publish.ps1")) && (Directory.Exists(git) || File.Exists(git))) return true;
                        dir = Path.GetDirectoryName(dir);
                    }
                }
                catch (Exception)
                {
                    // Can't tell: treat it as a teammate's PC.
                }
                return false;
            }
        }

        /// <summary>
        /// "Set up folders...": goes through every assembly number with no known folder, in order, and has you pick each
        /// one. Every pick is saved straight away (EbomFolders.Remember), so stopping keeps your progress and running it
        /// again carries on. Publish BDAT then shares the picks with the team.
        /// </summary>
        internal static void SetUpFolders(IWin32Window owner, List<EbomRow> rows)
        {
            // It runs from a link in the pop-up, where an error would otherwise vanish without a word.
            PlatformSave.Log(LogName, "set up folders: started");
            try
            {
                SetUpFoldersSteps(owner, rows);
            }
            catch (Exception ex)
            {
                PlatformSave.Log(LogName, "set up folders failed: " + ex);
                Ui.Show(owner, "Set up folders stopped with an error:\n\n" + ex.Message + "\n\nThe details are in %TEMP%\\BDAT\\new-from-ebom.log.",
                    "Set up folders", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void SetUpFoldersSteps(IWin32Window owner, List<EbomRow> rows)
        {
            const string caption = "Set up folders";
            if (TestMode.Enabled) { Ui.Show(owner, "Set up folders needs 3DEXPERIENCE, so it doesn't run in test mode.", caption, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            Connector connector = Connector.Find();
            PlatformSave.Log(LogName, "set up folders: connector " + (connector == null ? "not found" : connector.IsConnected ? "connected" : "not logged in"));
            if (connector == null || !connector.IsConnected)
            {
                Ui.Show(owner, connector == null ? "Couldn't find the 3DEXPERIENCE connector (is the \"3DEXPERIENCE PLM Services\" add-in on?)."
                    : "You're not logged in to 3DEXPERIENCE. Log in from the 3DEXPERIENCE task pane, then try again.",
                    caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var numbers = new List<string>();
            foreach (NewFromEbomForm.EbomGroup group in NewFromEbomForm.BuildGroups(rows))
            {
                // Only real assembly numbers (an assembly row with no number is listed by its part number instead).
                if (group.Number.Length == 0 || (group.Assembly != null && group.Assembly.AssemblyNumber != group.Number)) continue;
                numbers.Add(group.Number);
                labels[group.Number] = group.Number + (group.Assembly != null && group.Assembly.Name.Length > 0 ? " (" + group.Assembly.Name + ")" : "");
            }
            List<string> needed = EbomFolders.StillNeeded(numbers, EbomFolders.All());
            PlatformSave.Log(LogName, "set up folders: " + needed.Count + " of " + numbers.Count + " assemblies need a folder");
            if (needed.Count == 0)
            {
                Ui.Show(owner, "Every assembly in the EBOM already has its folder.", caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // First, find as many as possible by name: one folder named exactly after the assembly number.
            BookmarkSearch.Refresh();
            var matched = new List<KeyValuePair<string, FoundBookmark>>();
            List<string> startIds = SearchStartIds();
            foreach (string number in needed)
            {
                List<FoundBookmark> hits = BookmarkSearch.Find(number, startIds);
                if (hits.Count == 1 || (hits.Count > 1 && string.Equals(hits[0].Title.Trim(), number, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(hits[1].Title.Trim(), number, StringComparison.OrdinalIgnoreCase)))
                    matched.Add(new KeyValuePair<string, FoundBookmark>(number, hits[0]));
            }
            if (matched.Count > 0)
            {
                var list = new System.Text.StringBuilder();
                for (int i = 0; i < matched.Count && i < 12; i++) list.Append("\n  " + labels[matched[i].Key] + "  →  " + matched[i].Value.Path);
                if (matched.Count > 12) list.Append("\n  ... and " + (matched.Count - 12) + " more");
                if (Ui.Show(owner, "Found " + matched.Count + " of " + needed.Count + " folders by name:" + list + "\n\nUse them?",
                        caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    foreach (KeyValuePair<string, FoundBookmark> m in matched)
                    {
                        EbomFolders.Remember(m.Key, new Bookmark { Id = m.Value.Id, Title = m.Value.Title });
                        PlatformSave.Log(LogName, "set up folder for " + m.Key + " found by name = " + m.Value.Id + " (" + m.Value.Path + ")");
                        needed.Remove(m.Key);
                    }
                }
            }
            if (needed.Count == 0)
            {
                Ui.Show(owner, "Every assembly now has its folder. Run Publish BDAT to share them with the team.", caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (Ui.Show(owner, needed.Count + " of " + numbers.Count + " assemblies still don't have a folder. For each one, pick the bookmark " +
                    "named after it (e.g. A0704 for Bellcranks). Cancel the picker to skip one or stop; what you've picked is kept.\n\n" +
                    "Afterwards, Publish BDAT shares them with the team.", caption, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;

            int done = 0, skipped = 0;
            for (int i = 0; i < needed.Count; i++)
            {
                string number = needed[i];
                Bookmark picked = PlatformSave.ChooseBookmark(connector, owner, "Pick " + labels[number] + "  (" + (i + 1) + " of " + needed.Count + ")");
                if (picked == null)
                {
                    if (Ui.Show(owner, "No folder picked for " + labels[number] + ".\n\nSkip it and go on to the next one? (No stops here.)",
                            caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) break;
                    skipped++;
                    continue;
                }
                if (picked.Title.Length == 0) picked.Title = number;
                if (!EbomFolders.TitleMatches(picked.Title, number) &&
                    Ui.Show(owner, "You picked \"" + picked.Title + "\", which isn't named " + number + ". Use it for " + number + " anyway?\n\n" +
                        "No picks again.", caption, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    i--; // pick this one again
                    continue;
                }
                EbomFolders.Remember(number, picked);
                PlatformSave.Log(LogName, "set up folder for " + number + " = " + picked.Id + " (" + picked.Title + ")");
                done++;
            }
            int left = needed.Count - done;
            Ui.Show(owner, "Picked " + done + " folder" + (done == 1 ? "" : "s") + (skipped > 0 ? ", skipped " + skipped : "") + ". " +
                (left > 0 ? left + " still to do; run Set up folders again to carry on. " : "") +
                (done > 0 ? "Run Publish BDAT to share them with the team." : ""), caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// "Make folder": makes a bookmark named after the assembly number, where the other assembly folders of that system
        /// are (you confirm the place, or choose another), and remembers it. Null if it wasn't made (back to the choices).
        /// </summary>
        private Bookmark MakeFolder(Connector connector, IWin32Window owner, string number, string label)
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
            }

            string error;
            FoundBookmark made = BookmarkSearch.Create(parentId, parentPath, number, out error);
            if (made == null)
            {
                Ui.Show(owner, "Couldn't make the " + number + " folder: " + error + "\n\nYou can make it in 3DEXPERIENCE yourself, or pick a folder instead.",
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return null;
            }
            var folder = new Bookmark { Id = made.Id, Title = made.Title };
            EbomFolders.Remember(number, folder);
            return folder;
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
            cancelled = Ui.Show(owner, why + "\n\nSave " + row.Number + " to 3DEXPERIENCE without putting it in a folder?",
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
                Ui.Show(owner, "Couldn't read " + Path.GetFileName(path) + ": " + why, "New from EBOM",
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
