using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace BDAT.Commands
{
    /// <summary>The Save MCM pop-up: confirm the destination, check the name, paste the description.</summary>
    internal sealed class SaveMcmForm : Form
    {
        private readonly TextBox _name;
        private readonly TextBox _description;
        private readonly Label _error;

        public SaveMcmForm(string sourceFile, string defaultName, string defaultDescription, string destination)
        {
            ModernUi.Setup(this, "Save MCM to 3DX", false);
            ClientSize = new Size(560, 460);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), ColumnCount = 1, RowCount = 7 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // title
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // name caption
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // name
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // description caption
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // description
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // error
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons
            Controls.Add(layout);

            layout.Controls.Add(ModernUi.Header("Save to 3DX",
                "From " + sourceFile + "\nInto " + destination, 440), 0, 0);

            layout.Controls.Add(ModernUi.Caption("Name (the McMaster part number)"), 0, 1);
            _name = new TextBox { Text = defaultName };
            layout.Controls.Add(ModernUi.Framed(_name, 34), 0, 2);

            layout.Controls.Add(ModernUi.Caption("Description"), 0, 3);
            _description = new TextBox
            {
                Text = defaultDescription,
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
            };
            layout.Controls.Add(ModernUi.Framed(_description, 140), 0, 4);

            _error = ModernUi.ErrorLabel();
            layout.Controls.Add(_error, 0, 5);

            var cancel = ModernUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            var save = ModernUi.Primary("Save");
            save.Click += OnSave;
            layout.Controls.Add(ModernUi.ButtonRow(cancel, save), 0, 6);

            AcceptButton = save;
            CancelButton = cancel;
        }

        public string PartName { get { return _name.Text.Trim(); } }

        public string Description { get { return _description.Text.Trim(); } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _name.SelectAll();
            _name.Focus();
        }

        private void OnSave(object sender, EventArgs e)
        {
            string error = NameError(PartName);
            if (error != null)
            {
                _error.Text = error;
                _name.Focus();
                return;
            }
            if (Description.Length == 0 &&
                Ui.Show(this, EmptyDescriptionQuestion, Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                _description.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
        }

        internal const string EmptyDescriptionQuestion = "The description is empty. Save without one?";

        /// <summary>Why the name can't be used, or null if it's fine.</summary>
        internal static string NameError(string name)
        {
            if (name.Length == 0) return "Enter a name.";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return "The name can't contain any of these: \\ / : * ? \" < > |";
            return null;
        }
    }
}
