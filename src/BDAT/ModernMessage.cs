using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BDAT
{
    /// <summary>BDAT's message box, in the same look as its pop-ups (see Ui, which every message goes through).</summary>
    internal sealed class ModernMessage : Form
    {
        private readonly Color _iconColor;
        private readonly string _glyph;

        private ModernMessage(string message, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            ModernUi.Setup(this, string.IsNullOrEmpty(caption) ? "BDAT" : caption, false);
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(380, 0);

            switch (icon)
            {
                case MessageBoxIcon.Error: _iconColor = ModernUi.Error; _glyph = "✕"; break;          // ✕
                case MessageBoxIcon.Warning: _iconColor = Color.FromArgb(196, 120, 0); _glyph = "!"; break;
                case MessageBoxIcon.Question: _iconColor = ModernUi.Accent; _glyph = "?"; break;
                case MessageBoxIcon.None: _glyph = null; break;
                default: _iconColor = ModernUi.Accent; _glyph = "i"; break;
            }

            var layout = new TableLayoutPanel { AutoSize = true, Padding = new Padding(20, 20, 20, 14), ColumnCount = 2, RowCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            Controls.Add(layout);

            var badge = new Panel { Size = new Size(32, 32), Margin = new Padding(0, 0, 14, 0), Visible = _glyph != null };
            badge.Paint += PaintBadge;
            layout.Controls.Add(badge, 0, 0);

            var text = new Label
            {
                Text = message,
                AutoSize = true,
                MaximumSize = new Size(460, 0),
                Font = new Font("Segoe UI", 10.5f),
                Margin = new Padding(0, 5, 0, 0),
                UseMnemonic = false,
            };
            layout.Controls.Add(text, 1, 0);

            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 20, 0, 0) };
            Button accept = null, cancel = null;
            switch (buttons)
            {
                case MessageBoxButtons.OKCancel:
                    cancel = Add(row, ModernUi.Secondary("Cancel"), DialogResult.Cancel);
                    accept = Add(row, ModernUi.Primary("OK"), DialogResult.OK);
                    break;
                case MessageBoxButtons.YesNo:
                    cancel = Add(row, ModernUi.Secondary("No"), DialogResult.No);
                    accept = Add(row, ModernUi.Primary("Yes"), DialogResult.Yes);
                    break;
                case MessageBoxButtons.YesNoCancel:
                    cancel = Add(row, ModernUi.Secondary("Cancel"), DialogResult.Cancel);
                    Add(row, ModernUi.Secondary("No"), DialogResult.No);
                    accept = Add(row, ModernUi.Primary("Yes"), DialogResult.Yes);
                    break;
                case MessageBoxButtons.RetryCancel:
                    cancel = Add(row, ModernUi.Secondary("Cancel"), DialogResult.Cancel);
                    accept = Add(row, ModernUi.Primary("Retry"), DialogResult.Retry);
                    break;
                default:
                    accept = cancel = Add(row, ModernUi.Primary("OK"), DialogResult.OK);
                    break;
            }
            foreach (Control b in row.Controls) ((Button)b).MinimumSize = new Size(96, 34);
            layout.Controls.Add(row, 0, 1);
            layout.SetColumnSpan(row, 2);

            AcceptButton = accept;
            CancelButton = cancel;
        }

        /// <summary>Shows a message the way MessageBox.Show does, centred on owner (or the screen if owner is null).</summary>
        public static DialogResult Show(IWin32Window owner, string message, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            using (var box = new ModernMessage(message, caption, buttons, icon))
            {
                if (owner == null || owner.Handle == IntPtr.Zero)
                {
                    box.StartPosition = FormStartPosition.CenterScreen;
                    return box.ShowDialog();
                }
                return box.ShowDialog(owner);
            }
        }

        private static Button Add(FlowLayoutPanel row, Button button, DialogResult result)
        {
            button.DialogResult = result;
            row.Controls.Add(button);
            return button;
        }

        private void PaintBadge(object sender, PaintEventArgs e)
        {
            if (_glyph == null) return;
            var panel = (Control)sender;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var brush = new SolidBrush(_iconColor))
                e.Graphics.FillEllipse(brush, 0, 0, panel.Width - 1, panel.Height - 1);
            using (var font = new Font("Segoe UI", 13f, FontStyle.Bold))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                e.Graphics.DrawString(_glyph, font, Brushes.White, new RectangleF(0, -1, panel.Width, panel.Height), format);
        }
    }
}
