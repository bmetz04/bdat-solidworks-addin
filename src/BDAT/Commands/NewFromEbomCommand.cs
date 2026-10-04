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
    ///   1. Downloads the team's EBOM (release/ebom.csv in the repo, the Google Sheet downloaded as CSV), keeping
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
            EbomRow row = Pick(owner, csv, source, rows, out saveToPlatform);
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
            SetProperties(doc, row);

            if (TestMode.Enabled)
            {
                string description, resolved;
                doc.Extension.get_CustomPropertyManager("").Get4(DescriptionProperty, false, out description, out resolved);
                // Saving needs 3DEXPERIENCE, so test mode only records where it would have gone.
                TestMode.LastNewFromEbom = new NewFromEbomTestResult
                {
                    Number = row.Number,
                    Title = doc.GetTitle(),
                    Description = description,
                    IsAssembly = doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY,
                    Folder = row.AssemblyNumber,
                    FileName = FileName(row),
                };
                return;
            }

            if (saveToPlatform) SaveToPlatform(swApp, owner, doc, row);
        }

        private static EbomRow Pick(IWin32Window owner, string csv, string source, List<EbomRow> rows, out bool saveToPlatform)
        {
            saveToPlatform = false;
            if (TestMode.Enabled)
            {
                foreach (EbomRow r in rows)
                    if (string.Equals(r.Number, TestMode.EbomPick, StringComparison.OrdinalIgnoreCase)) return r;
                return null; // Cancel
            }

            using (var form = new NewFromEbomForm(csv, source, rows))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return null;
                saveToPlatform = form.SaveToPlatform;
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
        private void SaveToPlatform(ISldWorks swApp, IWin32Window owner, IModelDoc2 doc, EbomRow row)
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
                if (folder != null) platform.AddToBookmark(folder.Id, phid);
                done = "Saved " + row.Number + " (" + row.Name + ") to 3DEXPERIENCE" + where + " and checked it in.";
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
                DialogResult go = Ui.Show(owner,
                    "Which 3DEXPERIENCE folder is " + label + "? In the next window, pick the bookmark named " + number +
                    ". BDAT remembers it, and once it's published, nobody on the team has to pick it again.\n\n" +
                    "If there's no folder for it yet, cancel that window to save without one.",
                    Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (go != DialogResult.OK) { cancelled = true; return null; }
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
        /// "Set up folders...": goes through every assembly number with no known folder, in order, and has you pick each
        /// one. Every pick is saved straight away (EbomFolders.Remember), so stopping keeps your progress and running it
        /// again carries on. Publish BDAT then shares the picks with the team.
        /// </summary>
        internal static void SetUpFolders(IWin32Window owner, List<EbomRow> rows)
        {
            const string caption = "Set up folders";
            if (TestMode.Enabled) { Ui.Show(owner, "Set up folders needs 3DEXPERIENCE, so it doesn't run in test mode.", caption, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            Connector connector = Connector.Find();
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
            if (needed.Count == 0)
            {
                Ui.Show(owner, "Every assembly in the EBOM already has its folder.", caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (Ui.Show(owner, needed.Count + " of " + numbers.Count + " assemblies don't have a folder yet. For each one, pick the bookmark " +
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

        private Bookmark AskNoFolder(IWin32Window owner, EbomRow row, string why, out bool cancelled)
        {
            cancelled = Ui.Show(owner, why + "\n\nSave " + row.Number + " to 3DEXPERIENCE without putting it in a folder?",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes;
            return null;
        }

        /// <summary>Description in the file and every configuration (3DEXPERIENCE reads both); Part Number in the file.</summary>
        private static void SetProperties(IModelDoc2 doc, EbomRow row)
        {
            var targets = new List<string> { "" };
            string[] configs = doc.GetConfigurationNames() as string[];
            if (configs != null) targets.AddRange(configs);

            foreach (string config in targets)
            {
                CustomPropertyManager props = doc.Extension.get_CustomPropertyManager(config);
                if (props == null) continue;
                props.Add3(DescriptionProperty, (int)swCustomInfoType_e.swCustomInfoText, row.Name,
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
