using System;
using System.Drawing;
using System.Windows.Forms;

namespace BDAT.Commands
{
    /// <summary>
    /// New from EBOM, step 2: check what's about to be made and change its description if you want. The part number
    /// is fixed (it's the EBOM's); the description starts as the EBOM name.
    /// </summary>
    internal sealed class NewFromEbomDetailsForm : Form
    {
        private readonly TextBox _description;
        private readonly Label _error;

        /// <param name="where">e.g. "Saved to 3DEXPERIENCE in folder A0704." or "Left open and unsaved."</param>
        public NewFromEbomDetailsForm(EbomRow row, string where)
        {
            string kind = row.IsAssembly ? "assembly" : "part";
            ModernUi.Setup(this, "New from EBOM", false);
            ClientSize = new Size(560, 430);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), ColumnCount = 1, RowCount = 7 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // title
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // number caption
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // number
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // description caption
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // description
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // error
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons
            Controls.Add(layout);

            string context = row.Area + (row.IsAssembly ? "" : row.Parent.Length > 0 ? ", in " + row.AssemblyNumber + " " + row.Parent : "");
            layout.Controls.Add(ModernUi.Header("New " + kind, context + ". " + where, 440), 0, 0);

            layout.Controls.Add(ModernUi.Caption("Part number (file name)"), 0, 1);
            var numberFont = new Font("Segoe UI Semibold", 12f);
            var number = new Label { Text = row.Number, Font = numberFont, AutoSize = true, Margin = new Padding(0, 0, 0, 2), UseMnemonic = false };
            number.Disposed += delegate { numberFont.Dispose(); };
            layout.Controls.Add(number, 0, 2);

            layout.Controls.Add(ModernUi.Caption("Description"), 0, 3);
            _description = new TextBox { Text = row.Name, Multiline = true, AcceptsReturn = false, ScrollBars = ScrollBars.Vertical };
            layout.Controls.Add(ModernUi.Framed(_description, 90), 0, 4);

            _error = ModernUi.ErrorLabel();
            layout.Controls.Add(_error, 0, 5);

            var cancel = ModernUi.Secondary("Back");
            cancel.DialogResult = DialogResult.Cancel;
            var create = ModernUi.Primary("Create " + kind);
            create.Click += OnCreate;
            var buttons = ModernUi.ButtonRow(cancel, create);
            buttons.Controls.Add(ModernUi.Link("Use the EBOM's", delegate { _description.Text = row.Name; _description.Focus(); }, new Padding(0, 10, 12, 0)));
            layout.Controls.Add(buttons, 0, 6);

            AcceptButton = create;
            CancelButton = cancel;
        }

        /// <summary>The description to use (trimmed).</summary>
        public string Description
        {
            get { return Clean(_description.Text); }
        }

        /// <summary>One line, trimmed: Description is a single-line property in SolidWorks and 3DEXPERIENCE.</summary>
        internal static string Clean(string text)
        {
            return (text ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
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
