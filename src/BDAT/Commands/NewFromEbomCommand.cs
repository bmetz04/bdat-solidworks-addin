using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
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
    ///   2. A pop-up lists the EBOM, searchable by part number, name, assembly or area. Obsolete rows are hidden
    ///      unless "Show obsolete" is ticked.
    ///   3. Creates a new document from SolidWorks' default part or assembly template, titled with the combined
    ///      part number (e.g. BR-10101-AA, the EBOM's "Use in 3Dx File Naming" column), so that's the name it's
    ///      saved under, and sets the Description and Part Number properties (Description in every configuration too).
    ///
    /// It only reads the EBOM and never saves anything: saving to 3DEXPERIENCE is still done by hand.
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

            EbomRow row = Pick(owner, csv, source, rows);
            if (row == null) return;

            if (row.IsObsolete &&
                !Ui.AskYesNo(swApp, row.Number + " is marked OBSOLETE in the EBOM. Make it anyway?"))
                return;

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
                TestMode.LastNewFromEbom = new NewFromEbomTestResult
                {
                    Number = row.Number,
                    Title = doc.GetTitle(),
                    Description = description,
                    IsAssembly = doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY,
                };
            }
        }

        private static EbomRow Pick(IWin32Window owner, string csv, string source, List<EbomRow> rows)
        {
            if (TestMode.Enabled)
            {
                foreach (EbomRow r in rows)
                    if (string.Equals(r.Number, TestMode.EbomPick, StringComparison.OrdinalIgnoreCase)) return r;
                return null; // Cancel
            }

            using (var form = new NewFromEbomForm(csv, source, rows))
            {
                return form.ShowDialog(owner) == DialogResult.OK ? form.Selected : null;
            }
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
