using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BDAT.Commands
{
    /// <summary>The Waterjet DXF pop-up: tick the bodies to export and pick the folder the part's DXF folder goes in.</summary>
    internal sealed class WaterjetDxfForm : Form
    {
        private readonly List<WaterjetDxfCommand.BodyPlan> _exportable;
        private readonly CheckedListBox _bodies;
        private readonly TextBox _folder;
        private readonly Label _where;
        private readonly Label _error;
        private readonly string _partName;

        public WaterjetDxfForm(string partName, List<WaterjetDxfCommand.BodyPlan> plans, string folder)
        {
            _partName = partName;
            _exportable = plans.FindAll(p => p.Kind != WaterjetDxfCommand.BodyKind.Skipped);
            List<WaterjetDxfCommand.BodyPlan> skipped = plans.FindAll(p => p.Kind == WaterjetDxfCommand.BodyKind.Skipped);

            ModernUi.Setup(this, "Waterjet DXF", true);
            ClientSize = new Size(600, skipped.Count > 0 ? 560 : 480);
            MinimumSize = new Size(480, 400);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), ColumnCount = 1, RowCount = 9 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // title
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // bodies caption
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // bodies
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // skipped
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // folder caption
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // folder + browse
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // where they'll go
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // error
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons
            Controls.Add(layout);

            layout.Controls.Add(ModernUi.Header("Waterjet DXFs",
                "One DXF per body of " + partName + ": sheet metal as its flat pattern,\n" +
                "flat plates as their top face. Your part isn't changed."), 0, 0);

            layout.Controls.Add(ModernUi.Caption("Bodies to export"), 0, 1);
            _bodies = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                BorderStyle = BorderStyle.FixedSingle,
                IntegralHeight = false,
                Margin = new Padding(0),
            };
            foreach (WaterjetDxfCommand.BodyPlan plan in _exportable)
            {
                string label = plan.Label;
                if (plan.Kind == WaterjetDxfCommand.BodyKind.Plate && !plan.Export)
                    label += "  thicker than " + WaterjetDxfCommand.Millimetres(WaterjetDxfCommand.ThickPlateMetres) + ", is it really a plate?";
                _bodies.Items.Add(label, plan.Export);
            }
            layout.Controls.Add(_bodies, 0, 2);

            if (skipped.Count > 0)
            {
                var text = "Skipped, can't be waterjet:";
                foreach (WaterjetDxfCommand.BodyPlan plan in skipped) text += "\n    " + plan.Label;
                layout.Controls.Add(ModernUi.Note(text, new Padding(0, 8, 0, 0)), 0, 3);
            }

            layout.Controls.Add(ModernUi.Caption("Save in"), 0, 4);
            var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _folder = new TextBox { Text = folder };
            _folder.TextChanged += delegate { ShowWhere(); };
            folderRow.Controls.Add(ModernUi.Framed(_folder, 34), 0, 0);
            var browse = ModernUi.Secondary("Browse...");
            browse.Click += OnBrowse;
            folderRow.Controls.Add(browse, 1, 0);
            layout.Controls.Add(folderRow, 0, 5);

            _where = ModernUi.Note("", new Padding(0, 6, 0, 0));
            layout.Controls.Add(_where, 0, 6);
            ShowWhere();

            _error = ModernUi.ErrorLabel();
            layout.Controls.Add(_error, 0, 7);

            var cancel = ModernUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            var export = ModernUi.Primary("Export DXFs");
            export.Click += OnExport;
            layout.Controls.Add(ModernUi.ButtonRow(cancel, export), 0, 8);

            AcceptButton = export;
            CancelButton = cancel;
        }

        /// <summary>The folder picked; the DXFs go in a subfolder named after the part.</summary>
        public string Folder { get { return _folder.Text.Trim(); } }

        private void ShowWhere()
        {
            _where.Text = Folder.Length == 0 ? "" : "DXFs go in " + Path.Combine(Folder, _partName);
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Pick where the \"" + _partName + "\" DXF folder goes.",
                ShowNewFolderButton = true,
            })
            {
                if (Directory.Exists(Folder)) dialog.SelectedPath = Folder;
                if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
            }
        }

        private void OnExport(object sender, EventArgs e)
        {
            if (_bodies.CheckedIndices.Count == 0)
            {
                _error.Text = "Tick at least one body.";
                return;
            }
            if (Folder.Length == 0 || !Path.IsPathRooted(Folder))
            {
                _error.Text = "Pick a folder.";
                _folder.Focus();
                return;
            }
            if (!Directory.Exists(Folder))
            {
                _error.Text = "That folder doesn't exist.";
                _folder.Focus();
                return;
            }

            for (int i = 0; i < _exportable.Count; i++)
                _exportable[i].Export = _bodies.GetItemChecked(i);
            DialogResult = DialogResult.OK;
        }
    }
}
