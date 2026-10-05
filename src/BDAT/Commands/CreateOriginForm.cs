using System;
using System.Drawing;
using System.Windows.Forms;

namespace BDAT.Commands
{
    /// <summary>The Create Origin pop-up: X, Y and Z of the new origin, and the units they're in.</summary>
    internal sealed class CreateOriginForm : Form
    {
        private readonly TextBox[] _boxes = new TextBox[3];
        private readonly Label[] _unitLabels = new Label[3];
        private readonly ComboBox _units;
        private readonly Label _error;

        public CreateOriginForm(CreateOriginCommand.LengthUnit documentUnit)
        {
            ModernUi.Setup(this, "Create Origin", false);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                Padding = new Padding(20, 16, 20, 14),
                ColumnCount = 3,
                RowCount = 7,
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(layout);

            Control header = ModernUi.Header("Create Origin",
                "Makes a new origin here, for origin mates in the top level: the Origin' coordinate\n" +
                "system, with X', Y' and Z' planes built on it. X, Y and Z are the part's own, the same\n" +
                "as a 3D sketch point's. To move it later, edit Origin'.");
            layout.Controls.Add(header, 0, 0);
            layout.SetColumnSpan(header, 3);

            layout.Controls.Add(FieldName("Units"), 0, 1);
            _units = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160, Margin = new Padding(0, 4, 10, 4) };
            foreach (CreateOriginCommand.LengthUnit unit in CreateOriginCommand.Choices) _units.Items.Add(unit.Name);
            _units.SelectedIndex = 0; // mm
            _units.SelectedIndexChanged += delegate { ShowUnit(); };
            layout.Controls.Add(_units, 1, 1);
            layout.Controls.Add(ModernUi.Note("This document is in " + CreateOriginCommand.LongName(documentUnit) + ".", new Padding(0, 8, 0, 4)), 2, 1);

            string[] axes = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                layout.Controls.Add(FieldName(axes[i]), 0, i + 2);
                _boxes[i] = new TextBox { Text = "0" };
                Panel frame = ModernUi.Framed(_boxes[i], 32);
                frame.Dock = DockStyle.None;
                frame.Width = 160;
                frame.Margin = new Padding(0, 4, 10, 4);
                layout.Controls.Add(frame, 1, i + 2);
                _unitLabels[i] = ModernUi.Note("", new Padding(0, 10, 0, 4));
                layout.Controls.Add(_unitLabels[i], 2, i + 2);
            }
            ShowUnit();

            _error = ModernUi.ErrorLabel();
            _error.MaximumSize = new Size(420, 0);
            layout.Controls.Add(_error, 0, 5);
            layout.SetColumnSpan(_error, 3);

            var cancel = ModernUi.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            var ok = ModernUi.Primary("Create origin");
            ok.Click += OnOk;
            FlowLayoutPanel buttons = ModernUi.ButtonRow(cancel, ok);
            layout.Controls.Add(buttons, 0, 6);
            layout.SetColumnSpan(buttons, 3);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>The point, in metres, once OK was pressed.</summary>
        public double[] Point { get; private set; }

        /// <summary>The units picked in the pop-up.</summary>
        public CreateOriginCommand.LengthUnit Unit
        {
            get { return CreateOriginCommand.Choices[Math.Max(0, _units.SelectedIndex)]; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _boxes[0].SelectAll();
            _boxes[0].Focus();
        }

        private static Label FieldName(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 10, 14, 4), UseMnemonic = false };
        }

        private void ShowUnit()
        {
            foreach (Label label in _unitLabels) if (label != null) label.Text = Unit.Name;
        }

        private void OnOk(object sender, EventArgs e)
        {
            double[] point;
            string error = CreateOriginCommand.ParsePoint(_boxes[0].Text, _boxes[1].Text, _boxes[2].Text, Unit, out point);
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
