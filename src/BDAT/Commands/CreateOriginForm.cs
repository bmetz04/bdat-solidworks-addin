using System;
using System.Drawing;
using System.Windows.Forms;

namespace BDAT.Commands
{
    /// <summary>The Create Origin pop-up: X, Y and Z of the new origin.</summary>
    internal sealed class CreateOriginForm : Form
    {
        private readonly CreateOriginCommand.LengthUnit _unit;
        private readonly TextBox[] _boxes = new TextBox[3];
        private readonly Label _error;

        public CreateOriginForm(CreateOriginCommand.LengthUnit unit)
        {
            _unit = unit;
            Text = "Create Origin";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Font = SystemFonts.MessageBoxFont;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                Padding = new Padding(12),
                ColumnCount = 3,
                RowCount = 6,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(layout);

            var intro = new Label
            {
                Text = "Makes a new origin here, for origin mates in the top level:\nthe Origin' coordinate system with planes and axes.\nVehicle axes: X forward, Y left, Z up.\nTo move it later, edit the three planes' distances.",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 10),
            };
            layout.Controls.Add(intro, 0, 0);
            layout.SetColumnSpan(intro, 3);

            string[] axes = { "X (forward):", "Y (left):", "Z (up):" };
            for (int i = 0; i < 3; i++)
            {
                layout.Controls.Add(new Label { Text = axes[i], AutoSize = true, Margin = new Padding(3, 6, 8, 6) }, 0, i + 1);
                _boxes[i] = new TextBox { Text = "0", Width = 140 };
                layout.Controls.Add(_boxes[i], 1, i + 1);
                layout.Controls.Add(new Label { Text = unit.Name, AutoSize = true, Margin = new Padding(3, 6, 3, 6) }, 2, i + 1);
            }

            _error = new Label { AutoSize = true, ForeColor = Color.Firebrick, Margin = new Padding(3, 6, 3, 0), MaximumSize = new Size(300, 0) };
            layout.Controls.Add(_error, 0, 4);
            layout.SetColumnSpan(_error, 3);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            var ok = new Button { Text = "Create origin", AutoSize = true };
            ok.Click += OnOk;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            layout.Controls.Add(buttons, 0, 5);
            layout.SetColumnSpan(buttons, 3);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>The point, in metres, once OK was pressed.</summary>
        public double[] Point { get; private set; }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _boxes[0].SelectAll();
            _boxes[0].Focus();
        }

        private void OnOk(object sender, EventArgs e)
        {
            double[] point;
            string error = CreateOriginCommand.ParsePoint(_boxes[0].Text, _boxes[1].Text, _boxes[2].Text, _unit, out point);
            if (error != null)
            {
                _error.Text = error;
                int bad = error.StartsWith("Y") ? 1 : error.StartsWith("Z") ? 2 : 0;
                _boxes[bad].SelectAll();
                _boxes[bad].Focus();
                return;
            }
            Point = point;
            DialogResult = DialogResult.OK;
        }
    }
}
