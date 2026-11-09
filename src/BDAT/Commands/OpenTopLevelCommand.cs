using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT.Commands
{
    /// <summary>
    /// Open Top Level: the vehicle top-level assemblies aren't rows in the EBOM, but they're opened all the time. Every one
    /// has "toplevel" in its name, so this lists what 3DX finds by that name (PlatformParts.FindTopLevels); pick one and
    /// it's downloaded and opened. Only reads from 3DX. The list from the last click shows straight away while a fresh
    /// one is fetched in the background. (It may move into Open from EBOM one day; Ben, 2026-11-09.)
    /// </summary>
    public sealed class OpenTopLevelCommand : IBdatCommand
    {
        public string Title { get { return "Open Top Level"; } }

        public string Hint { get { return "Open one of the vehicle top-level assemblies from 3DX"; } }

        // The last list 3DX gave, so the next click shows it at once. Null until the first search comes back.
        private static List<PlatformParts.Found> _last;

        public bool IsEnabled(ISldWorks swApp)
        {
            return true; // opens a document, so nothing needs to be open
        }

        public void Run(ISldWorks swApp)
        {
            if (TestMode.Enabled) return; // there's nothing to open without 3DX, and tests must never reach it
            IWin32Window owner = Ui.SolidWorksWindow(swApp);
            Connector connector = Connector.Find();
            if (connector == null)
            {
                Ui.Tell(swApp, "Couldn't find the 3DX connector in this SolidWorks session (is the \"3DEXPERIENCE PLM Services\" add-in on?).",
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }
            if (!connector.IsConnected)
            {
                Ui.Tell(swApp, "You're not logged in to 3DX. Log in from the 3DX task pane, then try again.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            PlatformParts.Found pick;
            using (var form = new OpenTopLevelForm(_last))
            {
                DialogResult answer = form.ShowDialog(owner);
                if (form.Latest != null) _last = form.Latest;
                if (answer != DialogResult.OK || form.Selected == null) return;
                pick = form.Selected;
            }

            string label = pick.Title;
            string error;
            ModernUi.BusyWindow busy = ModernUi.Busy(owner, "Opening " + label, "Downloading it from 3DX...");
            string path;
            try { path = PlatformParts.Download(connector, pick.Id, out error); }
            finally { busy.Close(); }
            if (path == null)
            {
                Ui.Tell(swApp, "BDAT couldn't open " + label + ": " + error + "\n\nOpen it from the 3DX task pane instead (search for " + label + ").",
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }
            NewFromEbomCommand.OpenDownloaded(swApp, path, label);
        }
    }

    /// <summary>The Open Top Level pop-up: a searchable list of the top levels 3DX found. Double-click or Open picks one.</summary>
    internal sealed class OpenTopLevelForm : Form
    {
        private readonly TextBox _search;
        private readonly ListView _list;
        private readonly Button _open;
        private readonly Button _refresh;
        private List<PlatformParts.Found> _items;
        private bool _searching;
        private string _error;
        private const string RefreshText = "Check 3DX again";

        /// <summary>The newest list 3DX gave while this was open (null if it didn't answer), to show first next time.</summary>
        public List<PlatformParts.Found> Latest { get; private set; }

        public OpenTopLevelForm(List<PlatformParts.Found> last)
        {
            _items = last;
            ModernUi.Setup(this, "Open Top Level", true);
            ClientSize = new Size(720, 520);
            MinimumSize = new Size(520, 380);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), ColumnCount = 1, RowCount = 4, BackColor = ModernUi.Page };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            layout.Controls.Add(ModernUi.Header("Open Top Level", "Pick a vehicle top level to open it from 3DX."), 0, 0);

            _search = new TextBox();
            _search.TextChanged += delegate { Fill(); };
            _search.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Down || _list.Items.Count == 0) return;
                _list.Focus();
                if (_list.SelectedItems.Count == 0) _list.Items[0].Selected = true;
                e.Handled = true;
            };
            ModernUi.CueBanner(_search, "Search by name or description");
            Panel searchFrame = ModernUi.Framed(_search, 34);
            searchFrame.Margin = new Padding(0, 0, 0, 10);
            layout.Controls.Add(searchFrame, 0, 1);

            var listFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = ModernUi.Line, Margin = new Padding(0) };
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
            _list.Columns.Add("Name", 260);
            _list.Columns.Add("Description", 300);
            _list.Columns.Add("Revision", 90);
            ModernUi.ExplorerTheme(_list);
            _list.SelectedIndexChanged += delegate { _open.Enabled = Selected != null; };
            _list.DoubleClick += delegate { Pick(); };
            _list.Resize += delegate { FitColumns(); };
            listFrame.Controls.Add(_list);
            layout.Controls.Add(listFrame, 0, 2);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 14, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            _refresh = ModernUi.Secondary(RefreshText);
            _refresh.Margin = new Padding(0);
            _refresh.Click += delegate { Search(); };
            bottom.Controls.Add(_refresh, 0, 0);
            var cancel = ModernUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            _open = ModernUi.Primary("Open");
            _open.Enabled = false;
            _open.Click += delegate { Pick(); };
            bottom.Controls.Add(ModernUi.ButtonRow(cancel, _open), 1, 0);
            layout.Controls.Add(bottom, 0, 3);

            AcceptButton = _open;
            CancelButton = cancel;
            Fill();
        }

        /// <summary>The top level picked, or null.</summary>
        public PlatformParts.Found Selected
        {
            get { return _list.SelectedItems.Count == 0 ? null : _list.SelectedItems[0].Tag as PlatformParts.Found; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            FitColumns();
            _search.Focus();
            Search();
        }

        private void Pick()
        {
            if (Selected == null) return;
            DialogResult = DialogResult.OK;
        }

        /// <summary>Asks 3DX for the top levels in the background; the list already shown stays usable meanwhile.</summary>
        private void Search()
        {
            if (_searching) return;
            _searching = true;
            _refresh.Enabled = false;
            _refresh.Text = "Checking 3DX...";
            Fill();
            var worker = new System.ComponentModel.BackgroundWorker();
            string error = null;
            worker.DoWork += delegate(object s, System.ComponentModel.DoWorkEventArgs e) { e.Result = PlatformParts.FindTopLevels(out error); };
            worker.RunWorkerCompleted += delegate(object s, System.ComponentModel.RunWorkerCompletedEventArgs e)
            {
                if (IsDisposed) return;
                _searching = false;
                _refresh.Enabled = true;
                var found = e.Error == null ? e.Result as List<PlatformParts.Found> : null;
                _error = found != null ? null : e.Error != null ? e.Error.Message : error ?? "no answer";
                _refresh.Text = found == null ? "Couldn't check 3DX: try again" : RefreshText;
                if (found != null) { _items = found; Latest = found; }
                Fill();
            };
            worker.RunWorkerAsync();
        }

        private void Fill()
        {
            PlatformParts.Found keep = Selected;
            string[] words = _search.Text.ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            _list.BeginUpdate();
            _list.Items.Clear();
            if (_items != null)
            {
                foreach (PlatformParts.Found f in _items)
                {
                    string haystack = (f.Title + " " + f.Description).ToLowerInvariant();
                    bool match = true;
                    foreach (string w in words) if (haystack.IndexOf(w, StringComparison.Ordinal) < 0) { match = false; break; }
                    if (!match) continue;
                    var item = new ListViewItem(new[] { f.Title, f.Description, f.Revision }) { Tag = f };
                    _list.Items.Add(item);
                    if (keep != null && keep.Id == f.Id) { item.Selected = true; item.Focused = true; }
                }
            }
            if (_list.Items.Count == 0)
            {
                string why = _items == null
                    ? (_searching ? "Looking in 3DX..." : "Couldn't ask 3DX: " + (_error ?? "no answer") + ". Try Check 3DX again.")
                    : _items.Count == 0 ? (_searching ? "Looking in 3DX..." : "3DX has nothing with \"toplevel\" in its name.")
                    : "Nothing matches. Try fewer words.";
                _list.Items.Add(new ListViewItem(new[] { why, "", "" }) { ForeColor = ModernUi.Muted });
            }
            else if (_list.SelectedItems.Count == 0 && _list.Items.Count == 1) _list.Items[0].Selected = true;
            _list.EndUpdate();
            _open.Enabled = Selected != null;
        }

        // Description takes whatever width Name and Revision leave.
        private void FitColumns()
        {
            int width = _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[2].Width - 6;
            if (width > 150) _list.Columns[1].Width = width;
        }
    }
}
