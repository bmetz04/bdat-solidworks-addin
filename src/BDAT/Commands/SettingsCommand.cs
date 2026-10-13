using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace BDAT.Commands
{
    /// <summary>
    /// BDAT's settings for this Windows user: usage sharing, Save MCM's McMaster Carr folder, where Open from EBOM reads
    /// the EBOM and the assembly folders it remembers, Waterjet DXF's folder, plus the logs, release notes and updates.
    /// </summary>
    public sealed class SettingsCommand : IBdatCommand
    {
        public string Title { get { return "BDAT Settings"; } }

        public string Hint { get { return "BDAT settings for this PC"; } }

        public bool IsEnabled(ISldWorks swApp) { return true; }

        public void Run(ISldWorks swApp)
        {
            if (Testing.TestMode.Enabled)
            {
                Ui.Tell(swApp, "Test mode: settings aren't shown or changed.", SolidWorks.Interop.swconst.swMessageBoxIcon_e.swMbInformation);
                return;
            }
            bool checkUpdates;
            using (var form = new SettingsForm())
            {
                DialogResult result = form.ShowDialog(Ui.SolidWorksWindow(swApp));
                checkUpdates = form.CheckUpdates;
                if (result == DialogResult.OK) form.Save();
            }
            if (checkUpdates) new UpdateCommand().Run(swApp);
        }
    }

    internal sealed class SettingsForm : Form
    {
        private const int Wide = 470;

        private readonly CheckBox _share;
        private readonly CheckBox _ownEbom;
        private readonly TextBox _ebomCsv;
        private readonly Button _ebomBrowse;
        private readonly TextBox _waterjet;
        private readonly Label _error;

        public SettingsForm()
        {
            ModernUi.Setup(this, "BDAT Settings", false);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                Padding = new Padding(20, 16, 20, 14),
                ColumnCount = 1,
                GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            };
            Controls.Add(layout);

            layout.Controls.Add(ModernUi.Header("BDAT Settings",
                "BDAT " + BuildInfo.ShortVersion + ". Saved for you on this PC.", Wide - 70));

            // Usage sharing
            layout.Controls.Add(Section("Usage sharing"));
            _share = new CheckBox
            {
                Text = "Share usage data to help improve BDAT",
                AutoSize = true,
                Checked = Usage.CanChoose && !Usage.TurnedOff,
                Enabled = Usage.CanChoose,
                Margin = new Padding(0, 2, 0, 0),
            };
            new ToolTip().SetToolTip(_share, "Which BDAT buttons are used, whether they worked, and error messages.\nNever your name, files or part data.");
            layout.Controls.Add(_share);
            if (!Usage.CanChoose) layout.Controls.Add(ModernUi.Note(Usage.WhyNot, new Padding(1, 4, 0, 0)));

            // Save MCM
            layout.Controls.Add(Section("Save MCM"));
            string mcm = SaveMcmCommand.PickedFolderTitle;
            var mcmNote = ModernUi.Note(McMasterText(mcm), new Padding(1, 2, 0, 0));
            mcmNote.MaximumSize = new Size(Wide, 0);
            layout.Controls.Add(mcmNote);
            if (mcm != null)
            {
                LinkLabel forgetMcm = null;
                forgetMcm = ModernUi.Link("Forget my pick", delegate
                {
                    if (Ui.Show(this, "Forget the McMaster Carr folder you picked? Save MCM goes back to the team's folder.",
                            "BDAT Settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    SaveMcmCommand.ForgetPickedFolder();
                    mcmNote.Text = McMasterText(null);
                    forgetMcm.Visible = false;
                }, new Padding(0, 4, 0, 0));
                layout.Controls.Add(forgetMcm);
            }

            // Open from EBOM
            layout.Controls.Add(Section("Open from EBOM"));
            _ownEbom = new CheckBox
            {
                Text = "Use my own EBOM CSV instead of the team EBOM",
                AutoSize = true,
                Checked = Ebom.UseOwnCsv,
                Margin = new Padding(0, 2, 0, 4),
            };
            layout.Controls.Add(_ownEbom);
            _ebomCsv = new TextBox { Text = Ebom.SavedPath ?? "" };
            ModernUi.CueBanner(_ebomCsv, "Pick a .csv downloaded from the Master eBOM");
            _ebomBrowse = ModernUi.Secondary("Browse...");
            _ebomBrowse.Click += delegate { BrowseCsv(); };
            layout.Controls.Add(PathRow(_ebomCsv, _ebomBrowse));
            _ownEbom.CheckedChanged += delegate { ShowEbomChoice(); };
            ShowEbomChoice();

            int picked = EbomFolders.MineCount;
            if (picked > 0)
            {
                LinkLabel forgetFolders = null;
                forgetFolders = ModernUi.Link("Forget the 3DX folders I picked for assemblies (" + picked + ")", delegate
                {
                    if (Ui.Show(this,
                            "Forget the " + picked + " assembly folder(s) picked on this PC? Folders in the team list are still " +
                            "known; Open from EBOM asks again for the rest.",
                            "BDAT Settings", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
                    EbomFolders.ForgetAllMine();
                    forgetFolders.Visible = false;
                }, new Padding(0, 6, 0, 0));
                layout.Controls.Add(forgetFolders);
            }

            // Waterjet DXF
            layout.Controls.Add(Section("Waterjet DXF"));
            layout.Controls.Add(ModernUi.Note("Save DXFs in", new Padding(1, 2, 0, 4)));
            _waterjet = new TextBox { Text = WaterjetDxfCommand.SavedFolder() };
            var waterjetBrowse = ModernUi.Secondary("Browse...");
            waterjetBrowse.Click += delegate { BrowseFolder(); };
            layout.Controls.Add(PathRow(_waterjet, waterjetBrowse));

            // Help
            layout.Controls.Add(Section("Help"));
            var links = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 0) };
            links.Controls.Add(ModernUi.Link("Open BDAT logs", delegate { OpenLogs(); }, new Padding(0, 0, 16, 0)));
            links.Controls.Add(ModernUi.Link("Release notes", delegate { Process.Start(BuildInfo.ReleaseNotesPage); }, new Padding(0, 0, 16, 0)));
            links.Controls.Add(ModernUi.Link("Check for updates", delegate
            {
                // Keeps any changes made here (if they're valid), then checks once this window has closed.
                CheckUpdates = true;
                DialogResult = Check() ? DialogResult.OK : DialogResult.Cancel;
            }, new Padding(0)));
            layout.Controls.Add(links);
            var loaded = ModernUi.Note("Loaded from " + BuildInfo.DllPath, new Padding(1, 6, 0, 0));
            loaded.MaximumSize = new Size(Wide, 0);
            layout.Controls.Add(loaded);

            _error = ModernUi.ErrorLabel();
            _error.MaximumSize = new Size(Wide, 0);
            layout.Controls.Add(_error);

            var cancel = ModernUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            var save = ModernUi.Primary("Save");
            save.Click += delegate { if (Check()) DialogResult = DialogResult.OK; };
            var buttons = ModernUi.ButtonRow(cancel, save);
            buttons.Dock = DockStyle.None;
            buttons.Anchor = AnchorStyles.Right;
            layout.Controls.Add(buttons);

            AcceptButton = save;
            CancelButton = cancel;
        }

        /// <summary>True when "Check for updates" was clicked; the update check runs after this window closes.</summary>
        public bool CheckUpdates { get; private set; }

        /// <summary>Saves everything. Call after the window closed with OK (Check passed).</summary>
        public void Save()
        {
            if (Usage.CanChoose) Usage.TurnedOff = !_share.Checked;

            string csv = _ebomCsv.Text.Trim();
            if (csv.Length > 0) Ebom.SavedPath = csv;
            Ebom.UseOwnCsv = _ownEbom.Checked;

            string folder = _waterjet.Text.Trim();
            if (folder.Length > 0) WaterjetDxfCommand.SaveFolder(folder);
        }

        private bool Check()
        {
            string csv = _ebomCsv.Text.Trim();
            if (_ownEbom.Checked && (csv.Length == 0 || !File.Exists(csv)))
                return Fail("Pick your EBOM CSV, or untick \"Use my own EBOM CSV\".");
            string folder = _waterjet.Text.Trim();
            if (folder.Length > 0 && !Directory.Exists(folder))
                return Fail("The Waterjet DXF folder doesn't exist: " + folder);
            _error.Text = "";
            return true;
        }

        private bool Fail(string message)
        {
            _error.Text = message;
            return false;
        }

        private static string McMasterText(string picked)
        {
            return picked != null
                ? "McMaster Carr parts are saved in \"" + picked + "\", which you picked on this PC."
                : "McMaster Carr parts are saved in the team's McMaster Carr folder.";
        }

        private void ShowEbomChoice()
        {
            _ebomCsv.Enabled = _ownEbom.Checked;
            _ebomBrowse.Enabled = _ownEbom.Checked;
        }

        private void BrowseCsv()
        {
            using (var dialog = new OpenFileDialog { Title = "Pick your EBOM CSV", Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*" })
            {
                string current = _ebomCsv.Text.Trim();
                if (current.Length > 0 && File.Exists(current))
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(current);
                    dialog.FileName = Path.GetFileName(current);
                }
                if (dialog.ShowDialog(this) == DialogResult.OK) _ebomCsv.Text = dialog.FileName;
            }
        }

        private void BrowseFolder()
        {
            using (var dialog = new FolderBrowserDialog { Description = "Where Waterjet DXF saves DXFs", ShowNewFolderButton = true })
            {
                string current = _waterjet.Text.Trim();
                if (Directory.Exists(current)) dialog.SelectedPath = current;
                if (dialog.ShowDialog(this) == DialogResult.OK) _waterjet.Text = dialog.SelectedPath;
            }
        }

        private void OpenLogs()
        {
            string logs = Path.Combine(Path.GetTempPath(), "BDAT");
            try
            {
                Directory.CreateDirectory(logs);
                Process.Start("explorer.exe", "\"" + logs + "\"");
            }
            catch (Exception ex)
            {
                _error.Text = "Couldn't open " + logs + ": " + ex.Message;
            }
        }

        private static Label Section(string text)
        {
            var label = new Label { Text = text, AutoSize = true, Margin = new Padding(0, 14, 0, 4), UseMnemonic = false };
            label.Font = new Font("Segoe UI Semibold", 10.5f);
            return label;
        }

        private static Control PathRow(TextBox box, Button browse)
        {
            var row = new TableLayoutPanel { Width = Wide, Height = 36, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(ModernUi.Framed(box, 34), 0, 0);
            browse.MinimumSize = new Size(90, 34);
            row.Controls.Add(browse, 1, 0);
            return row;
        }
    }
}
