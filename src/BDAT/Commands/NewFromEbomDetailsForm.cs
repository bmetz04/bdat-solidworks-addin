using System;
using System.Drawing;
using System.Windows.Forms;

namespace BDAT.Commands
{
    /// <summary>
    /// New from EBOM, step 2: check what's about to be made, change its description if you want, and choose whether it's
    /// saved to 3DEXPERIENCE and checked in. The part number is fixed (it's the EBOM's); the description starts as the
    /// EBOM name.
    /// </summary>
    internal sealed class NewFromEbomDetailsForm : Form
    {
        private readonly EbomRow _row;
        private readonly TextBox _description;
        private readonly CheckBox _save;
        private readonly CheckBox _checkIn;
        private readonly Label _where;
        private readonly Label _error;

        public NewFromEbomDetailsForm(EbomRow row)
        {
            _row = row;
            string kind = row.IsAssembly ? "assembly" : "part";
            ModernUi.Setup(this, "Open from EBOM", false);
            ClientSize = new Size(560, 480);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), ColumnCount = 1, RowCount = 9 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // title
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // number caption
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // number
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // description caption
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // description
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // save / check in
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // where it goes
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // error
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons
            Controls.Add(layout);

            string context = row.Area + (row.IsAssembly ? "" : row.Parent.Length > 0 ? ", in " + row.AssemblyNumber + " " + row.Parent : "") + ".";
            layout.Controls.Add(ModernUi.Header("New " + kind, context, 440), 0, 0);

            layout.Controls.Add(ModernUi.Caption("Part number (file name)"), 0, 1);
            var numberFont = new Font("Segoe UI Semibold", 12f);
            var number = new Label { Text = row.Number, Font = numberFont, AutoSize = true, Margin = new Padding(0, 0, 0, 2), UseMnemonic = false };
            number.Disposed += delegate { numberFont.Dispose(); };
            layout.Controls.Add(number, 0, 2);

            layout.Controls.Add(ModernUi.Caption("Description"), 0, 3);
            _description = new TextBox { Text = row.Name, Multiline = true, AcceptsReturn = false, ScrollBars = ScrollBars.Vertical };
            layout.Controls.Add(ModernUi.Framed(_description, 90), 0, 4);

            var options = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
            _save = new CheckBox { Text = "Save to 3DX", AutoSize = true, Checked = true, Margin = new Padding(0, 0, 18, 0) };
            _checkIn = new CheckBox { Text = "Check in", AutoSize = true, Checked = NewFromEbomCommand.CheckInPreference, Margin = new Padding(0) };
            new ToolTip().SetToolTip(_checkIn, "Ticked: check it in after saving, so it isn't left reserved by you.\nUnticked: keep it checked out to you, to carry on modelling it.");
            _save.CheckedChanged += delegate { _checkIn.Enabled = _save.Checked; ShowWhere(); };
            _checkIn.CheckedChanged += delegate { NewFromEbomCommand.CheckInPreference = _checkIn.Checked; ShowWhere(); };
            options.Controls.Add(_save);
            options.Controls.Add(_checkIn);
            layout.Controls.Add(options, 0, 5);

            _where = ModernUi.Note("", new Padding(1, 4, 0, 0));
            _where.MaximumSize = new Size(520, 0);
            layout.Controls.Add(_where, 0, 6);

            _error = ModernUi.ErrorLabel();
            layout.Controls.Add(_error, 0, 7);

            var cancel = ModernUi.Secondary("Back");
            cancel.DialogResult = DialogResult.Cancel;
            var create = ModernUi.Primary("Create " + kind);
            create.Click += OnCreate;
            var buttons = ModernUi.ButtonRow(cancel, create);
            buttons.Controls.Add(ModernUi.Link("Use the EBOM's", delegate { _description.Text = row.Name; _description.Focus(); }, new Padding(0, 10, 12, 0)));
            layout.Controls.Add(buttons, 0, 8);

            AcceptButton = create;
            CancelButton = cancel;
            ShowWhere();
        }

        /// <summary>The description to use (trimmed).</summary>
        public string Description
        {
            get { return Clean(_description.Text); }
        }

        /// <summary>Whether to save it to 3DEXPERIENCE straight away, in its assembly's folder.</summary>
        public bool SaveToPlatform
        {
            get { return _save.Checked; }
        }

        /// <summary>Whether to check it in after saving (otherwise it stays checked out to you).</summary>
        public bool CheckIn
        {
            get { return _checkIn.Checked; }
        }

        /// <summary>One line, trimmed: Description is a single-line property in SolidWorks and 3DEXPERIENCE.</summary>
        internal static string Clean(string text)
        {
            return (text ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        }

        /// <summary>"Saved to 3DEXPERIENCE in folder A0704 and checked in.", or the other cases.</summary>
        internal static string SaveText(EbomRow row, bool save, bool checkIn)
        {
            if (!save) return "Left open and unsaved.";
            string where = row.AssemblyNumber.Length > 0 ? "Saved to 3DX in folder " + row.AssemblyNumber : "Saved to 3DX";
            return where + (checkIn ? " and checked in." : ", kept checked out to you.");
        }

        private void ShowWhere()
        {
            _where.Text = SaveText(_row, _save.Checked, _checkIn.Checked);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _description.Focus();
            _description.SelectAll();
        }

        private void OnCreate(object sender, EventArgs e)
        {
            if (Description.Length == 0)
            {
                _error.Text = "Enter a description, or click \"Use the EBOM's\".";
                _description.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        }
    }
}
