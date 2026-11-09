using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace BDAT.Commands
{
    /// <summary>One file of the open assembly that's only on this PC: the assembly itself, a sub-assembly or a part.</summary>
    internal sealed class AssemblyFile
    {
        public string Path;
        public IModelDoc2 Doc;
        public bool IsAssembly;
        /// <summary>The open (top) assembly.</summary>
        public bool IsTop;
        /// <summary>How deep it sits in the tree (0 for the top). Deeper files are saved first.</summary>
        public int Depth;
        /// <summary>The EBOM row it's saved as, or null to skip it.</summary>
        public EbomRow Row;
        public string Description;

        public string FileName { get { return System.IO.Path.GetFileName(Path); } }
    }

    /// <summary>
    /// Save to EBOM with an assembly open: every file of it that's only on this PC, each given its EBOM row (or skipped).
    /// Pick a row with the same EBOM list as Open from EBOM (double-click a file, or Pick EBOM row). Rows already in 3DX,
    /// or already picked for another file, can't be picked. Files whose name already holds an EBOM number start matched.
    /// </summary>
    internal sealed class SaveAssemblyToEbomForm : Form
    {
        private readonly List<AssemblyFile> _files;
        private readonly string _csv, _source;
        private readonly List<EbomRow> _rows;
        private readonly ListView _list;
        private readonly CheckBox _checkIn;
        private readonly Button _save;
        private readonly Label _error;
        private HashSet<string> _existing; // numbers already in 3DX, kept between pickers so it's only asked once

        public SaveAssemblyToEbomForm(string assemblyName, List<AssemblyFile> files, int alreadyInPlatform, bool fromSelection,
            string csv, string source, List<EbomRow> rows)
        {
            _files = files;
            _csv = csv;
            _source = source;
            _rows = rows;
            ModernUi.Setup(this, "Save to EBOM", true);
            ClientSize = new Size(900, 560);
            MinimumSize = new Size(640, 400);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), ColumnCount = 1, RowCount = 5, BackColor = ModernUi.Page };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // title
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // list
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // note
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // error
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons
            Controls.Add(layout);

            layout.Controls.Add(ModernUi.Header("Save " + assemblyName + " to EBOM",
                "Pick the EBOM row for each file. Each is renamed to its part number, described and saved to 3DX in its folder, " +
                "parts first, so the assembly keeps pointing at them."), 0, 0);

            var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = ModernUi.Line, Margin = new Padding(0) };
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                SmallImageList = new ImageList { ImageSize = new Size(1, 28) },
            };
            _list.Columns.Add("File", 300);
            _list.Columns.Add("Saved as", 150);
            _list.Columns.Add("Description", 400);
            ModernUi.ExplorerTheme(_list);
            _list.DoubleClick += delegate { PickFor(Selected); };
            _list.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) { Skip(Selected); e.Handled = true; }
            };
            _list.Resize += delegate { FitColumns(); };
            frame.Controls.Add(_list);
            layout.Controls.Add(frame, 0, 1);

            string note = "";
            if (alreadyInPlatform > 0)
                note = alreadyInPlatform + " file" + (alreadyInPlatform == 1 ? " is" : "s are") + " already in 3DX and left as " +
                    (alreadyInPlatform == 1 ? "it is" : "they are") + ".";
            if (fromSelection) note = (note + " Only the parts you selected are listed.").Trim();
            layout.Controls.Add(ModernUi.Note(note, new Padding(1, 8, 0, 0)), 0, 2);

            _error = ModernUi.ErrorLabel();
            layout.Controls.Add(_error, 0, 3);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 10, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            var pick = ModernUi.Secondary("Pick EBOM row...");
            pick.Margin = new Padding(0, 0, 8, 0);
            pick.Click += delegate { PickFor(Selected); };
            var skip = ModernUi.Secondary("Skip");
            skip.Margin = new Padding(0, 0, 14, 0);
            skip.Click += delegate { Skip(Selected); };
            _checkIn = new CheckBox { Text = "Check in", AutoSize = true, Checked = NewFromEbomCommand.CheckInPreference, Margin = new Padding(0, 8, 0, 0) };
            new ToolTip().SetToolTip(_checkIn, "Ticked: check each one in after saving.\nUnticked: keep them checked out to you.");
            _checkIn.CheckedChanged += delegate { NewFromEbomCommand.CheckInPreference = _checkIn.Checked; };
            left.Controls.Add(pick);
            left.Controls.Add(skip);
            left.Controls.Add(_checkIn);
            bottom.Controls.Add(left, 0, 0);

            var cancel = ModernUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            _save = ModernUi.Primary("Save to 3DX");
            _save.Click += OnSave;
            bottom.Controls.Add(ModernUi.ButtonRow(cancel, _save), 1, 0);
            layout.Controls.Add(bottom, 0, 4);

            CancelButton = cancel;
            Fill();
        }

        /// <summary>Whether to check them in after saving (otherwise they stay checked out to you).</summary>
        public bool CheckIn { get { return _checkIn.Checked; } }

        private AssemblyFile Selected
        {
            get { return _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as AssemblyFile; }
        }

        private void Fill()
        {
            AssemblyFile keep = Selected;
            _list.BeginUpdate();
            _list.Items.Clear();
            int picked = 0;
            foreach (AssemblyFile f in _files)
            {
                string kind = f.IsTop ? "  (this assembly)" : f.IsAssembly ? "  (sub-assembly)" : "";
                var item = new ListViewItem(new[] { f.FileName + kind, f.Row == null ? "Skip" : f.Row.Number, f.Row == null ? "" : f.Description }) { Tag = f };
                if (f.Row == null) item.ForeColor = ModernUi.Muted; else picked++;
                _list.Items.Add(item);
                if (f == keep) item.Selected = true;
            }
            if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
            _list.EndUpdate();
            _save.Enabled = picked > 0;
            _save.Text = picked == 0 ? "Save to 3DX" : "Save " + picked + " to 3DX";
        }

        private void Skip(AssemblyFile file)
        {
            if (file == null) return;
            file.Row = null;
            file.Description = null;
            _error.Text = "";
            Fill();
        }

        /// <summary>The EBOM list, for parts or assemblies to match the file, with the numbers already used blocked.</summary>
        private void PickFor(AssemblyFile file)
        {
            if (file == null) return;
            var taken = new List<string>();
            foreach (AssemblyFile f in _files)
                if (f != file && f.Row != null) taken.Add(f.Row.Number);
            using (var picker = new NewFromEbomForm(_csv, _source, _rows, file.FileName))
            {
                picker.PickAssembly = file.IsAssembly;
                picker.SkipDetails = true;
                picker.Taken = taken;
                picker.Existing = _existing;
                DialogResult result = picker.ShowDialog(this);
                if (picker.Existing != null) _existing = picker.Existing;
                if (result != DialogResult.OK || picker.Selected == null) return;
                file.Row = picker.Selected;
                file.Description = picker.Description;
            }
            _error.Text = "";
            Fill();
        }

        private void OnSave(object sender, EventArgs e)
        {
            // A known-existing number (from the 3DX check) can't be saved again; the command checks once more, fresh.
            foreach (AssemblyFile f in _files)
            {
                if (f.Row == null || _existing == null || !_existing.Contains(f.Row.Number)) continue;
                _error.Text = f.Row.Number + " is already in 3DX. Pick a different row for " + f.FileName + ", or skip it.";
                return;
            }
            DialogResult = DialogResult.OK;
        }

        private void FitColumns()
        {
            int width = _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[1].Width - 6;
            if (width > 150) _list.Columns[2].Width = width;
        }

        /// <summary>The current EBOM row whose combined part number is in the file's name (e.g. "BR-10101-AA bracket"), or null.</summary>
        internal static EbomRow GuessRow(string fileName, bool isAssembly, IEnumerable<EbomRow> rows)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(fileName ?? "");
            EbomRow best = null;
            foreach (EbomRow r in rows)
            {
                if (r.IsObsolete || r.IsAssembly != isAssembly || string.IsNullOrEmpty(r.Number)) continue;
                if (name.IndexOf(r.Number, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (best == null || r.Number.Length > best.Number.Length) best = r;
            }
            return best;
        }
    }
}
