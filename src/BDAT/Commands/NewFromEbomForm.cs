using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BDAT.Commands
{
    /// <summary>
    /// The New from EBOM pop-up: the EBOM as a tree of assemblies (bold, collapsed) with their parts underneath.
    /// Search or filter by area, open an assembly with its ▶ marker, double-click or Right arrow, and pick a row.
    /// A strip under the list says what will be made and where it's saved.
    /// </summary>
    internal sealed class NewFromEbomForm : Form
    {
        // Colours: plain white page, light grey lines, one blue accent (the Windows 11 default).
        private static readonly Color Page = Color.White;
        private static readonly Color Line = Color.FromArgb(224, 224, 224);
        private static readonly Color Muted = Color.FromArgb(96, 96, 96);
        private static readonly Color Accent = Color.FromArgb(0, 95, 184);
        private static readonly Color AccentHover = Color.FromArgb(0, 80, 160);
        private static readonly Color AssemblyBack = Color.FromArgb(244, 247, 251);

        private readonly TextBox _search;
        private readonly ComboBox _area;
        private readonly ListView _list;
        private readonly Label _source;
        private readonly Label _count;
        private readonly Label _pickedNumber;
        private readonly Label _pickedDetails;
        private readonly Button _create;
        private readonly CheckBox _save;
        private readonly Font _bold;
        private readonly Font _title;
        private readonly Font _big;
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
            Font = new Font("Segoe UI", 9.75f);
            BackColor = Page;
            ClientSize = new Size(980, 640);
            MinimumSize = new Size(700, 460);
            _bold = new Font(Font, FontStyle.Bold);
            _title = new Font("Segoe UI Semibold", 15f);
            _big = new Font("Segoe UI Semibold", 12f);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), ColumnCount = 1, RowCount = 6, BackColor = Page };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // title
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // search + filters
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // list
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // count
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // what you picked
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons
            Controls.Add(layout);

            // Title.
            var header = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 12) };
            header.Controls.Add(new Label { Text = "New from EBOM", Font = _title, AutoSize = true, Margin = new Padding(0) });
            header.Controls.Add(new Label
            {
                Text = "Pick a part or assembly. It's created with its EBOM number and description, ready to model.",
                ForeColor = Muted, AutoSize = true, Margin = new Padding(1, 2, 0, 0),
            });
            layout.Controls.Add(header, 0, 0);

            // Search box, area filter, expand and collapse.
            var tools = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, 0, 0, 10) };
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            tools.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var searchFrame = new Panel { Dock = DockStyle.Fill, Height = 34, Padding = new Padding(10, 8, 10, 6), BackColor = Page, Margin = new Padding(0, 0, 10, 0) };
            searchFrame.Paint += delegate(object s, PaintEventArgs e) { DrawFrame(e.Graphics, searchFrame.ClientRectangle, _search.Focused ? Accent : Line); };
            _search = new TextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Page };
            _search.TextChanged += delegate { Fill(); };
            _search.KeyDown += OnSearchKeyDown;
            _search.GotFocus += delegate { searchFrame.Invalidate(); };
            _search.LostFocus += delegate { searchFrame.Invalidate(); };
            _search.HandleCreated += delegate { SetCueBanner(_search, "Search by part number, name, assembly or area"); };
            searchFrame.Controls.Add(_search);
            tools.Controls.Add(searchFrame, 0, 0);

            _area = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, Margin = new Padding(0, 5, 12, 0), FlatStyle = FlatStyle.System };
            _area.SelectedIndexChanged += delegate { _toggled.Clear(); Fill(); };
            tools.Controls.Add(_area, 1, 0);
            tools.Controls.Add(Link("Expand all", delegate { _allOpen = true; _toggled.Clear(); Fill(); }, new Padding(0, 9, 10, 0)), 2, 0);
            tools.Controls.Add(Link("Collapse all", delegate { _allOpen = false; _toggled.Clear(); Fill(); }, new Padding(0, 9, 0, 0)), 3, 0);
            layout.Controls.Add(tools, 0, 1);

            // The list, in a thin frame.
            var listFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Line, Margin = new Padding(0) };
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                GridLines = false,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                // A blank 1-pixel-wide image list is the standard way to give a ListView taller rows.
                SmallImageList = new ImageList { ImageSize = new Size(1, 28) },
            };
            _list.Columns.Add("Part #", 170);
            _list.Columns.Add("Name", 420);
            _list.Columns.Add("Area", 200);
            _list.Columns.Add("Class", 90);
            _list.HandleCreated += delegate { UseExplorerTheme(_list); };
            _list.SelectedIndexChanged += delegate { ShowPicked(); };
            _list.DoubleClick += OnListDoubleClick;
            _list.MouseClick += OnListMouseClick;
            _list.KeyDown += OnListKeyDown;
            _list.Resize += delegate { FitNameColumn(); };
            listFrame.Controls.Add(_list);
            layout.Controls.Add(listFrame, 0, 2);

            _count = new Label { UseMnemonic = false, AutoSize = true, ForeColor = Muted, Margin = new Padding(1, 6, 0, 0) };
            layout.Controls.Add(_count, 0, 3);

            // What you picked, and where it goes.
            var picked = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
            _pickedNumber = new Label { UseMnemonic = false, Font = _big, AutoSize = true, Margin = new Padding(0) };
            _pickedDetails = new Label { UseMnemonic = false, ForeColor = Muted, AutoSize = true, Margin = new Padding(1, 2, 0, 0) };
            picked.Controls.Add(_pickedNumber);
            picked.Controls.Add(_pickedDetails);
            layout.Controls.Add(picked, 0, 4);

            // Buttons: file links on the left, Save / Create / Cancel on the right.
            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 14, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
            left.Controls.Add(Link("Open another EBOM file...", OnChangeFile, new Padding(0, 10, 14, 0)));
            if (NewFromEbomCommand.IsPublishingPc)
            {
                // Only on the PC BDAT is published from: Publish BDAT shares the folders picked there with the team.
                left.Controls.Add(Link("Set up folders...", delegate { NewFromEbomCommand.SetUpFolders(this, _rows); }, new Padding(0, 10, 14, 0)));
            }
            _source = new Label { UseMnemonic = false, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 10, 0, 0) };
            left.Controls.Add(_source);
            bottom.Controls.Add(left, 0, 0);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            var cancel = FlatButton("Cancel", false);
            cancel.DialogResult = DialogResult.Cancel;
            _create = FlatButton("Create", true);
            _create.Enabled = false;
            _create.Click += delegate { if (Selected != null) DialogResult = DialogResult.OK; };
            _save = new CheckBox { Text = "Save to 3DEXPERIENCE", AutoSize = true, Checked = true, Margin = new Padding(0, 10, 16, 0) };
            _save.CheckedChanged += delegate { ShowPicked(); };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_create);
            buttons.Controls.Add(_save);
            bottom.Controls.Add(buttons, 1, 0);
            layout.Controls.Add(bottom, 0, 5);

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
            if (disposing)
            {
                _bold.Dispose();
                _title.Dispose();
                _big.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitNameColumn();
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

            // The area filter: every area in the EBOM, in the order they first appear.
            var areas = new List<string>();
            foreach (EbomGroup g in _groups)
                if (g.Area.Length > 0 && !areas.Contains(g.Area)) areas.Add(g.Area);
            string keep = _area.SelectedIndex > 0 ? _area.SelectedItem as string : null;
            _area.Items.Clear();
            _area.Items.Add(AllAreas);
            foreach (string a in areas) _area.Items.Add(a);
            _area.SelectedIndex = keep != null && areas.Contains(keep) ? _area.Items.IndexOf(keep) : 0;

            Fill();
        }

        private const string AllAreas = "All areas";

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
                    // Sorted after every real assembly number.
                    if (row.IsAssembly) { byNumber["\uFFFF" + row.Number] = new EbomGroup { Number = row.Number, Assembly = row }; continue; }
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
        // Groups the person opened or closed by hand. Cleared when the search, the area or Expand/Collapse all changes.
        private readonly HashSet<EbomGroup> _toggled = new HashSet<EbomGroup>();
        private string _lastSearch = "";
        private bool _allOpen;

        private const string ClosedMarker = "▶  ";   // ▶
        private const string OpenMarker = "▼  ";     // ▼
        private const string NoMarker = "     ";
        private const string Indent = "         ";

        private void Fill()
        {
            if (_search.Text != _lastSearch)
            {
                _lastSearch = _search.Text;
                _toggled.Clear();
            }
            bool searching = _search.Text.Trim().Length > 0;
            string area = _area.SelectedIndex > 0 ? _area.SelectedItem as string : null;
            var matches = new HashSet<EbomRow>(Ebom.Search(_rows, _search.Text, false));

            object selectedTag = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag : null;
            int shown = 0, groupsShown = 0;
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (EbomGroup group in _groups)
            {
                if (area != null && !string.Equals(group.Area, area, StringComparison.OrdinalIgnoreCase)) continue;
                bool headerMatches = !searching || (group.Assembly != null ? matches.Contains(group.Assembly)
                    : GroupTextMatches(group, _search.Text));
                var matchingParts = new List<EbomRow>();
                foreach (EbomRow part in group.Parts)
                    if (matches.Contains(part)) matchingParts.Add(part);
                if (searching && !headerMatches && matchingParts.Count == 0) continue;

                // An assembly that matches shows all its parts; otherwise only the parts that match.
                List<EbomRow> parts = headerMatches ? group.Parts : matchingParts;
                bool open = searching || _allOpen;
                if (_toggled.Contains(group)) open = !open;
                if (group.Parts.Count == 0) open = false;

                _list.Items.Add(HeaderItem(group, open));
                groupsShown++;
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

            int current = 0;
            foreach (EbomRow row in _rows)
                if (!row.IsObsolete) current++;
            if (_list.Items.Count == 0)
                _count.Text = "Nothing matches. Try fewer words, or set the area back to " + AllAreas + ".";
            else if (searching)
                _count.Text = shown + (shown == 1 ? " match" : " matches") + " in " + groupsShown + (groupsShown == 1 ? " assembly" : " assemblies") + ".";
            else
                _count.Text = groupsShown + " assemblies, " + current + " current EBOM rows. Click ▶, double-click or press → to show an assembly's parts.";
            ShowPicked();
        }

        private ListViewItem HeaderItem(EbomGroup group, bool open)
        {
            string marker = group.Parts.Count == 0 ? NoMarker : open ? OpenMarker : ClosedMarker;
            ListViewItem item;
            if (group.Assembly != null)
            {
                string name = group.Assembly.Name + (group.Parts.Count > 0 ? "   (" + group.Parts.Count + ")" : "");
                item = new ListViewItem(new[] { marker + group.Assembly.Number, name, group.Area, group.Assembly.Class }) { Tag = group.Assembly };
            }
            else
            {
                string number = group.Number.Length > 0 ? group.Number : "Other";
                string name = (group.Number.Length > 0 ? NoAssemblyRow : "(no assembly number in EBOM)") + "   (" + group.Parts.Count + ")";
                item = new ListViewItem(new[] { marker + number, name, group.Area, "" }) { Tag = group, ForeColor = Muted };
            }
            item.Font = _bold;
            item.BackColor = AssemblyBack;
            return item;
        }

        /// <summary>The strip under the list: what Create will make, and where it'll be saved.</summary>
        private void ShowPicked()
        {
            EbomRow row = Selected;
            _create.Enabled = row != null;
            if (row == null)
            {
                bool header = _list.SelectedItems.Count > 0;
                _pickedNumber.Text = header ? "Nothing to create here" : "Pick a part or assembly";
                _pickedDetails.Text = header
                    ? "The EBOM has no row for this assembly, so open it and pick one of its parts."
                    : "Search above, or open an assembly to see its parts.";
                _create.Text = "Create";
                return;
            }
            string kind = row.IsAssembly ? "assembly" : "part";
            _create.Text = "Create " + kind;
            _pickedNumber.Text = row.Number + "   " + row.Name;
            string where = row.IsAssembly ? "" : row.Parent.Length > 0 ? " in " + row.AssemblyNumber + " " + row.Parent : row.AssemblyNumber.Length > 0 ? " in " + row.AssemblyNumber : "";
            string save = _save.Checked
                ? (row.AssemblyNumber.Length > 0 ? "Saved to 3DEXPERIENCE in folder " + row.AssemblyNumber + "." : "Saved to 3DEXPERIENCE.")
                : "Left open and unsaved.";
            _pickedDetails.Text = "New " + kind + ", " + row.Area + where + ". " + save;
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
            // The ▶ marker at the start of the Part # column.
            if (e.X - hit.SubItem.Bounds.Left <= TextRenderer.MeasureText(ClosedMarker, _bold).Width + 8) Toggle(GroupOf(hit.Item));
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

        // ---------------------------------------------------------------- looks

        /// <summary>Name takes whatever width the other columns leave.</summary>
        private void FitNameColumn()
        {
            if (_list.Columns.Count < 4) return;
            int others = _list.Columns[0].Width + _list.Columns[2].Width + _list.Columns[3].Width;
            int width = _list.ClientSize.Width - others - 2;
            if (width > 200) _list.Columns[1].Width = width;
        }

        private Button FlatButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(primary ? 140 : 96, 34),
                Padding = new Padding(10, 0, 10, 0),
                Margin = new Padding(8, 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand,
            };
            if (primary)
            {
                b.Font = new Font(Font, FontStyle.Bold);
                b.BackColor = Accent;
                b.ForeColor = Color.White;
                b.FlatAppearance.BorderColor = Accent;
                b.FlatAppearance.MouseOverBackColor = AccentHover;
                b.FlatAppearance.MouseDownBackColor = AccentHover;
                // Greyed out while there's nothing to create.
                b.EnabledChanged += delegate
                {
                    b.BackColor = b.Enabled ? Accent : Color.FromArgb(200, 214, 230);
                    b.FlatAppearance.BorderColor = b.BackColor;
                };
            }
            else
            {
                b.BackColor = Page;
                b.ForeColor = Color.Black;
                b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 243, 243);
            }
            return b;
        }

        private static LinkLabel Link(string text, EventHandler click, Padding margin)
        {
            var link = new LinkLabel
            {
                Text = text,
                AutoSize = true,
                Margin = margin,
                LinkColor = Accent,
                ActiveLinkColor = AccentHover,
                VisitedLinkColor = Accent,
                LinkBehavior = LinkBehavior.HoverUnderline,
            };
            link.LinkClicked += delegate { click(link, EventArgs.Empty); };
            return link;
        }

        private static void DrawFrame(Graphics g, Rectangle r, Color color)
        {
            using (var pen = new Pen(color))
                g.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string appName, string idList);

        // Grey hint text in an empty search box.
        private static void SetCueBanner(TextBox box, string text)
        {
            const int EM_SETCUEBANNER = 0x1501;
            try { SendMessage(box.Handle, EM_SETCUEBANNER, new IntPtr(1), text); }
            catch (Exception) { /* just no hint */ }
        }

        // The Windows Explorer look for the list: soft blue hover and selection instead of the classic dark blue bar.
        private static void UseExplorerTheme(ListView list)
        {
            try { SetWindowTheme(list.Handle, "Explorer", null); }
            catch (Exception) { /* classic look */ }
        }
    }
}
