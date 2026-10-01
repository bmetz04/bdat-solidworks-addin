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
            Text = "Save MCM to 3DEXPERIENCE";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Font = SystemFonts.MessageBoxFont;
            ClientSize = new Size(520, 360);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 2,
                RowCount = 6,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            Controls.Add(layout);

            layout.Controls.Add(Caption("Save to:"), 0, 0);
            var destinationLabel = new Label { Text = destination, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(3, 6, 3, 6) };
            layout.Controls.Add(destinationLabel, 1, 0);

            layout.Controls.Add(Caption("McMaster file:"), 0, 1);
            layout.Controls.Add(new Label { Text = sourceFile, AutoSize = true, Margin = new Padding(3, 6, 3, 6) }, 1, 1);

            layout.Controls.Add(Caption("Name:"), 0, 2);
            _name = new TextBox { Text = defaultName, Dock = DockStyle.Fill };
            layout.Controls.Add(_name, 1, 2);

            layout.Controls.Add(Caption("Description:"), 0, 3);
            _description = new TextBox
            {
                Text = defaultDescription,
                Multiline = true,
                AcceptsReturn = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                Height = 140,
            };
            layout.Controls.Add(_description, 1, 3);
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _error = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(3, 6, 3, 0) };
            layout.Controls.Add(_error, 1, 4);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var save = new Button { Text = "Save", AutoSize = true };
            save.Click += OnSave;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            layout.Controls.Add(buttons, 0, 5);
            layout.SetColumnSpan(buttons, 2);

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
                MessageBox.Show(this, EmptyDescriptionQuestion, Text,
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

        private static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 6, 8, 6) };
        }
    }
}
