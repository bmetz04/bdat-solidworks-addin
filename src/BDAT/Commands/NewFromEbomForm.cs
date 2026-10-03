using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BDAT.Commands
{
    /// <summary>
    /// The New from EBOM pop-up: the EBOM as a tree of assemblies (bold, collapsed) with their parts underneath.
    /// Search, open an assembly with its ▸ marker, double-click or Right arrow, and pick a row.
    /// </summary>
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
            _list.Columns.Add("Part #", 150);
            _list.Columns.Add("Name", 400);
            _list.Columns.Add("Area", 170);
            _list.Columns.Add("Class", 80);
            _list.SelectedIndexChanged += delegate { _create.Enabled = Selected != null; };
            _list.DoubleClick += OnListDoubleClick;
            _list.MouseClick += OnListMouseClick;
            _list.KeyDown += OnListKeyDown;
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
            _groups = BuildGroups(rows);
            _toggled.Clear();
            if (source == null)
            {
                string when = File.Exists(csvPath) ? File.GetLastWriteTime(csvPath).ToString("yyyy-MM-dd HH:mm") : "?";
                source = Path.GetFileName(csvPath) + " (saved " + when + ")";
            }
            _source.Text = source;
            Fill();
        }

        // ---------------------------------------------------------------- the tree

        /// <summary>One assembly and the parts under it. Assembly is null when the EBOM has no current row for that number.</summary>
        internal sealed class EbomGroup
        {
            public string Number;
            public EbomRow Assembly;
            public readonly List<EbomRow> Parts = new List<EbomRow>();

            public string Area
            {
                get { return Assembly != null ? Assembly.Area : Parts.Count > 0 ? Parts[0].Area : ""; }
            }
        }

        internal const string NoAssemblyRow = "(no assembly row in EBOM)";

        /// <summary>
        /// The current rows as a tree: one group per assembly number (A0101, A0102, ...), in order, each with its parts in
        /// control-number order. Assembly numbers with parts but no current assembly row still get a group. Rows with no
        /// assembly number at all go in a last group with an empty number.
        /// </summary>
        internal static List<EbomGroup> BuildGroups(IEnumerable<EbomRow> rows)
        {
            var byNumber = new SortedDictionary<string, EbomGroup>(StringComparer.Ordinal);
            EbomGroup other = null;
            foreach (EbomRow row in rows)
            {
                if (row.IsObsolete) continue;
                EbomGroup group;
                if (row.AssemblyNumber.Length == 0)
                {
                    if (row.IsAssembly) { byNumber["￿" + row.Number] = new EbomGroup { Number = row.Number, Assembly = row }; continue; }
                    if (other == null) other = new EbomGroup { Number = "" };
                    group = other;
                }
                else if (!byNumber.TryGetValue(row.AssemblyNumber, out group))
                {
                    group = new EbomGroup { Number = row.AssemblyNumber };
                    byNumber[row.AssemblyNumber] = group;
                }
                if (row.IsAssembly && group.Assembly == null) group.Assembly = row;
                else group.Parts.Add(row);
            }
            var groups = new List<EbomGroup>(byNumber.Values);
            foreach (EbomGroup g in groups)
                g.Parts.Sort(delegate(EbomRow a, EbomRow b) { return string.CompareOrdinal(a.ControlNumber + a.Number, b.ControlNumber + b.Number); });
            if (other != null) groups.Add(other);
            return groups;
        }

        private List<EbomGroup> _groups = new List<EbomGroup>();
        // Groups the person opened or closed by hand. Cleared when the search changes.
        private readonly HashSet<EbomGroup> _toggled = new HashSet<EbomGroup>();
        private string _lastSearch = "";

        private const string ClosedMarker = "▸ ";   // ▸
        private const string OpenMarker = "▾ ";     // ▾
        private const string Indent = "      ";

        private void Fill()
        {
            if (_search.Text != _lastSearch)
            {
                _lastSearch = _search.Text;
                _toggled.Clear();
            }
            bool searching = _search.Text.Trim().Length > 0;
            var matches = new HashSet<EbomRow>(Ebom.Search(_rows, _search.Text, false));

            object selectedTag = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag : null;
            int shown = 0;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (EbomGroup group in _groups)
            {
                bool headerMatches = !searching || (group.Assembly != null ? matches.Contains(group.Assembly)
                    : GroupTextMatches(group, _search.Text));
                var matchingParts = new List<EbomRow>();
                foreach (EbomRow part in group.Parts)
                    if (matches.Contains(part)) matchingParts.Add(part);
                if (searching && !headerMatches && matchingParts.Count == 0) continue;

                // An assembly that matches shows all its parts; otherwise only the parts that match.
                List<EbomRow> parts = headerMatches ? group.Parts : matchingParts;
                bool open = searching;
                if (_toggled.Contains(group)) open = !open;
                if (group.Parts.Count == 0) open = false;

                _list.Items.Add(HeaderItem(group, open));
                if (group.Assembly != null && matches.Contains(group.Assembly)) shown++;
                shown += matchingParts.Count;
                if (!open) continue;
                foreach (EbomRow part in parts)
                    _list.Items.Add(new ListViewItem(new[] { Indent + part.Number, part.Name, part.Area, part.Class }) { Tag = part });
            }

            foreach (ListViewItem item in _list.Items)
            {
                if (selectedTag != null && item.Tag == selectedTag)
                {
                    item.Selected = true;
                    item.Focused = true;
                    item.EnsureVisible();
                    break;
                }
            }
            _list.EndUpdate();
            if (_list.SelectedItems.Count == 0 && _list.Items.Count == 1) _list.Items[0].Selected = true;
            _create.Enabled = Selected != null;
            int current = 0;
            foreach (EbomRow row in _rows)
                if (!row.IsObsolete) current++;
            _count.Text = (searching ? shown + " of " + current + " current EBOM rows match" : current + " current EBOM rows, in " + _groups.Count + " assemblies") +
                ". Click ▸ (or press →) to show an assembly's parts.";
        }

        private ListViewItem HeaderItem(EbomGroup group, bool open)
        {
            string marker = group.Parts.Count == 0 ? "   " : open ? OpenMarker : ClosedMarker;
            ListViewItem item;
            if (group.Assembly != null)
                item = new ListViewItem(new[] { marker + group.Assembly.Number, group.Assembly.Name, group.Area, group.Assembly.Class }) { Tag = group.Assembly };
            else
            {
                string number = group.Number.Length > 0 ? group.Number : "Other";
                string name = group.Number.Length > 0 ? NoAssemblyRow : "(no assembly number in EBOM)";
                item = new ListViewItem(new[] { marker + number, name, group.Area, "" }) { Tag = group, ForeColor = SystemColors.GrayText };
            }
            item.Font = _bold;
            return item;
        }

        // A header with no assembly row matches the search by its number, e.g. typing "A0402".
        private static bool GroupTextMatches(EbomGroup group, string text)
        {
            string[] words = (text ?? "").ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string haystack = (group.Number + " " + NoAssemblyRow + " " + group.Area).ToLowerInvariant();
            foreach (string w in words)
                if (haystack.IndexOf(w, StringComparison.Ordinal) < 0) return false;
            return true;
        }

        /// <summary>The group a list row heads, or null if it's a part row.</summary>
        private EbomGroup GroupOf(ListViewItem item)
        {
            if (item == null) return null;
            EbomGroup group = item.Tag as EbomGroup;
            if (group != null) return group;
            EbomRow row = item.Tag as EbomRow;
            if (row == null || item.Text.StartsWith(Indent, StringComparison.Ordinal)) return null;
            foreach (EbomGroup g in _groups)
                if (g.Assembly == row) return g;
            return null;
        }

        private void Toggle(EbomGroup group)
        {
            if (group == null || group.Parts.Count == 0) return;
            if (!_toggled.Remove(group)) _toggled.Add(group);
            Fill();
        }

        private bool IsOpen(ListViewItem header)
        {
            return header.Text.StartsWith(OpenMarker, StringComparison.Ordinal);
        }

        private void OnListMouseClick(object sender, MouseEventArgs e)
        {
            ListViewHitTestInfo hit = _list.HitTest(e.Location);
            if (hit.Item == null || hit.SubItem == null || hit.Item.SubItems.IndexOf(hit.SubItem) != 0) return;
            // The ▸ marker: the first 22 or so pixels of the Part # column.
            if (e.X - hit.SubItem.Bounds.Left <= TextRenderer.MeasureText(ClosedMarker, _bold).Width + 6) Toggle(GroupOf(hit.Item));
        }

        // Double-click: a part is created; an assembly opens or closes (Create or Enter makes the assembly itself).
        private void OnListDoubleClick(object sender, EventArgs e)
        {
            ListViewItem item = _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0];
            EbomGroup group = GroupOf(item);
            if (group != null) { Toggle(group); return; }
            if (Selected != null) DialogResult = DialogResult.OK;
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            ListViewItem item = _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0];
            EbomGroup group = GroupOf(item);
            bool expand = e.KeyCode == Keys.Right || e.KeyCode == Keys.Add || e.KeyCode == Keys.Oemplus;
            bool collapse = e.KeyCode == Keys.Left || e.KeyCode == Keys.Subtract || e.KeyCode == Keys.OemMinus;
            if (!expand && !collapse) return;
            e.Handled = true;
            if (group != null)
            {
                if (expand != IsOpen(item)) Toggle(group);
                return;
            }
            // Left on a part goes up to its assembly.
            if (collapse && item != null)
            {
                for (int i = item.Index - 1; i >= 0; i--)
                {
                    if (GroupOf(_list.Items[i]) == null) continue;
                    _list.Items[i].Selected = true;
                    _list.Items[i].Focused = true;
                    _list.Items[i].EnsureVisible();
                    break;
                }
            }
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
