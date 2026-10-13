using System;
using System.Drawing;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace BDAT.Commands
{
    /// <summary>BDAT's settings for this Windows user. For now: whether anonymous usage is shared.</summary>
    public sealed class SettingsCommand : IBdatCommand
    {
        public string Title { get { return "BDAT Settings"; } }

        public string Hint { get { return "BDAT settings for this PC"; } }

        public bool IsEnabled(ISldWorks swApp) { return true; }

        public void Run(ISldWorks swApp)
        {
            if (Testing.TestMode.Enabled)
            {
                Ui.Tell(swApp, "Test mode: settings aren't shown or changed.", SolidWorks.Interop.swconst.swMessageBoxIcon_e.swMbInformation);
                return;
            }
            using (var form = new SettingsForm())
            {
                if (form.ShowDialog(Ui.SolidWorksWindow(swApp)) != DialogResult.OK) return;
                if (Usage.CanChoose) Usage.TurnedOff = !form.ShareUsage;
            }
        }
    }

    internal sealed class SettingsForm : Form
    {
        private readonly CheckBox _share;

        public SettingsForm()
        {
            ModernUi.Setup(this, "BDAT Settings", false);
            ClientSize = new Size(480, 250);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // title
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // share usage
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));  // why it can't change, if it can't
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // buttons
            Controls.Add(layout);

            layout.Controls.Add(ModernUi.Header("BDAT Settings", "Saved for you on this PC.", 400), 0, 0);

            _share = new CheckBox
            {
                Text = "Share usage data to help improve BDAT",
                AutoSize = true,
                Checked = Usage.CanChoose && !Usage.TurnedOff,
                Enabled = Usage.CanChoose,
                Margin = new Padding(0, 8, 0, 0)
            };
            new ToolTip().SetToolTip(_share, "Which BDAT buttons are used, whether they worked, and error messages.\nNever your name, files or part data.");
            layout.Controls.Add(_share, 0, 1);

            if (!Usage.CanChoose)
                layout.Controls.Add(ModernUi.Note(Usage.WhyNot, new Padding(1, 4, 0, 0)), 0, 2);

            var cancel = ModernUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            var save = ModernUi.Primary("Save");
            save.DialogResult = DialogResult.OK;
            layout.Controls.Add(ModernUi.ButtonRow(cancel, save), 0, 3);

            AcceptButton = save;
            CancelButton = cancel;
        }

        public bool ShareUsage
        {
            get { return _share.Checked; }
        }
    }
}
