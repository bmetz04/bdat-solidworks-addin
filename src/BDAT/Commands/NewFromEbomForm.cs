using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BDAT.Commands
{
    /// <summary>The New from EBOM pop-up: search the EBOM, pick a row.</summary>
    internal sealed class NewFromEbomForm : Form
    {
        private readonly TextBox _search;
        private readonly ListView _list;
        private readonly Label _source;
        private readonly Label _count;
        private readonly Button _create;
        private readonly CheckBox _save;
        private readonly Font _bold;
        private List<EbomRow> _rows;
        private string _csvPath;

        public NewFromEbomForm(string csvPath, string source, List<EbomRow> rows)
        {
            Text = "New from EBOM";
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(900, 560);
            MinimumSize = new Size(600, 360);
            _bold = new Font(Font, FontStyle.Bold);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            layout.Controls.Add(new Label { Text = "Search:", AutoSize = true, Margin = new Padding(3, 6, 8, 6) }, 0, 0);
            _search = new TextBox { Dock = DockStyle.Fill };
            _search.TextChanged += delegate { Fill(); };
            _search.KeyDown += OnSearchKeyDown;
            layout.Controls.Add(_search, 1, 0);

            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = true,
            };
            _list.Columns.Add("Part #", 120);
            _list.Columns.Add("Name", 280);
            _list.Columns.Add("Assembly", 220);
            _list.Columns.Add("Area", 140);
            _list.Columns.Add("Class", 70);
            _list.SelectedIndexChanged += delegate { _create.Enabled = Selected != null; };
            _list.DoubleClick += delegate { if (Selected != null) DialogResult = DialogResult.OK; };
            layout.Controls.Add(_list, 0, 1);
            layout.SetColumnSpan(_list, 2);

            _count = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 0) };
            layout.Controls.Add(_count, 0, 2);
            layout.SetColumnSpan(_count, 2);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 6, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            var change = new Button { Text = "Open another EBOM file...", AutoSize = true };
            change.Click += OnChangeFile;
            _source = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(6, 8, 3, 0) };
            left.Controls.Add(change);
            left.Controls.Add(_source);
            bottom.Controls.Add(left, 0, 0);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            _create = new Button { Text = "Create", AutoSize = true, Enabled = false };
            _create.Click += delegate { if (Selected != null) DialogResult = DialogResult.OK; };
            _save = new CheckBox { Text = "Save to 3DEXPERIENCE", AutoSize = true, Checked = true, Margin = new Padding(3, 7, 12, 3) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_create);
            buttons.Controls.Add(_save);
            bottom.Controls.Add(buttons, 1, 0);
            layout.Controls.Add(bottom, 0, 3);
            layout.SetColumnSpan(bottom, 2);

            AcceptButton = _create;
            CancelButton = cancel;

            SetSource(csvPath, source, rows);
        }

        /// <summary>The row picked, or null.</summary>
        public EbomRow Selected
        {
            get { return _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as EbomRow; }
        }

        /// <summary>Whether to save the new part to 3DEXPERIENCE straight away, in its assembly's folder.</summary>
        public bool SaveToPlatform
        {
            get { return _save.Checked; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _bold.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _search.Focus();
        }

        /// <summary>source: how to describe it, e.g. "Team EBOM"; null names the file.</summary>
        private void SetSource(string csvPath, string source, List<EbomRow> rows)
        {
            _csvPath = csvPath;
            _rows = rows;
            if (source == null)
            {
                string when = File.Exists(csvPath) ? File.GetLastWriteTime(csvPath).ToString("yyyy-MM-dd HH:mm") : "?";
                source = Path.GetFileName(csvPath) + " (saved " + when + ")";
            }
            _source.Text = source;
            Fill();
        }

        private void Fill()
        {
            List<EbomRow> found = Ebom.Search(_rows, _search.Text, false);
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (EbomRow row in found)
            {
                var item = new ListViewItem(new[] { row.Number, row.Name, row.AssemblyText, row.Area, row.Class }) { Tag = row };
                if (row.IsAssembly) item.Font = _bold;
                _list.Items.Add(item);
            }
            _list.EndUpdate();
            if (_list.Items.Count == 1) _list.Items[0].Selected = true;
            _create.Enabled = Selected != null;
            int current = 0;
            foreach (EbomRow row in _rows)
                if (!row.IsObsolete) current++;
            _count.Text = found.Count + " of " + current + " current EBOM rows";
        }

        // Down arrow from the search box goes into the list, so it all works from the keyboard.
        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Down || _list.Items.Count == 0) return;
            _list.Focus();
            if (_list.SelectedItems.Count == 0) _list.Items[0].Selected = true;
            e.Handled = true;
        }

        private void OnChangeFile(object sender, EventArgs e)
        {
            string path = NewFromEbomCommand.PickCsv(this, _csvPath);
            if (path == null) return;
            List<EbomRow> rows = NewFromEbomCommand.TryLoad(this, path);
            if (rows == null) return;
            SetSource(path, null, rows);
        }
    }
}
