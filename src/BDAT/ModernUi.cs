using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BDAT
{
    /// <summary>
    /// The look shared by BDAT's pop-ups: white page, Segoe UI, a title with a one-line hint, framed text boxes, small
    /// blue links and one blue main button. Plain WinForms, so it works on any PC SolidWorks runs on.
    /// </summary>
    internal static class ModernUi
    {
        public static readonly Color Page = Color.White;
        public static readonly Color Line = Color.FromArgb(224, 224, 224);
        public static readonly Color Muted = Color.FromArgb(96, 96, 96);
        public static readonly Color Accent = Color.FromArgb(0, 95, 184);
        public static readonly Color AccentHover = Color.FromArgb(0, 80, 160);
        public static readonly Color AccentDisabled = Color.FromArgb(200, 214, 230);
        public static readonly Color Error = Color.FromArgb(196, 43, 28);

        /// <summary>Window basics: title bar text, font, white page, centred on SolidWorks.</summary>
        public static void Setup(Form form, string caption, bool resizable)
        {
            form.Text = caption;
            form.FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
            form.MaximizeBox = resizable;
            form.MinimizeBox = false;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.CenterParent;
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.AutoScaleDimensions = new SizeF(96f, 96f);
            form.Font = new Font("Segoe UI", 9.75f);
            form.BackColor = Page;
        }

        /// <summary>The big title and a grey one-line hint under it.</summary>
        public static Control Header(string title, string hint)
        {
            var header = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 14) };
            var titleFont = new Font("Segoe UI Semibold", 15f);
            var titleLabel = new Label { Text = title, Font = titleFont, AutoSize = true, Margin = new Padding(0), UseMnemonic = false };
            titleLabel.Disposed += delegate { titleFont.Dispose(); };
            header.Controls.Add(titleLabel);
            if (!string.IsNullOrEmpty(hint)) header.Controls.Add(Note(hint, new Padding(1, 2, 0, 0)));
            return header;
        }

        /// <summary>A field's name, above its box.</summary>
        public static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 10, 0, 4), UseMnemonic = false };
        }

        /// <summary>Grey explanatory text.</summary>
        public static Label Note(string text, Padding margin)
        {
            return new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = margin, UseMnemonic = false };
        }

        /// <summary>Red text under a field, empty until there's something wrong.</summary>
        public static Label ErrorLabel()
        {
            return new Label { AutoSize = true, ForeColor = Error, Margin = new Padding(0, 8, 0, 0), UseMnemonic = false };
        }

        /// <summary>A borderless text box in a thin frame that turns blue while you're typing in it.</summary>
        public static Panel Framed(TextBox box, int height)
        {
            var frame = new Panel { Dock = DockStyle.Fill, Height = height, Padding = new Padding(10, box.Multiline ? 6 : 8, 6, 4), BackColor = Page, Margin = new Padding(0) };
            box.BorderStyle = BorderStyle.None;
            box.BackColor = Page;
            box.Dock = DockStyle.Fill;
            frame.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(box.Focused ? Accent : Line))
                    e.Graphics.DrawRectangle(pen, 0, 0, frame.Width - 1, frame.Height - 1);
            };
            box.GotFocus += delegate { frame.Invalidate(); };
            box.LostFocus += delegate { frame.Invalidate(); };
            frame.Click += delegate { box.Focus(); };
            frame.Controls.Add(box);
            return frame;
        }

        /// <summary>The one blue button: what the pop-up is for. Grey-blue while disabled.</summary>
        public static Button Primary(string text)
        {
            var b = BaseButton(text, 140);
            b.Font = new Font("Segoe UI", 9.75f, FontStyle.Bold);
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = Accent;
            b.FlatAppearance.MouseOverBackColor = AccentHover;
            b.FlatAppearance.MouseDownBackColor = AccentHover;
            b.EnabledChanged += delegate
            {
                b.BackColor = b.Enabled ? Accent : AccentDisabled;
                b.FlatAppearance.BorderColor = b.BackColor;
            };
            return b;
        }

        /// <summary>A plain outlined button, e.g. Cancel.</summary>
        public static Button Secondary(string text)
        {
            var b = BaseButton(text, 96);
            b.BackColor = Page;
            b.ForeColor = Color.Black;
            b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 243, 243);
            return b;
        }

        private static Button BaseButton(string text, int minWidth)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(minWidth, 34),
                Padding = new Padding(10, 0, 10, 0),
                Margin = new Padding(8, 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand,
            };
        }

        /// <summary>Buttons in a row on the right, the first one rightmost (pass Cancel first, then the main button).</summary>
        public static FlowLayoutPanel ButtonRow(params Control[] rightToLeft)
        {
            var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0, 16, 0, 0) };
            foreach (Control c in rightToLeft) row.Controls.Add(c);
            return row;
        }

        /// <summary>A small blue link for the less-used actions.</summary>
        public static LinkLabel Link(string text, EventHandler click, Padding margin)
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
                UseMnemonic = false,
            };
            link.LinkClicked += delegate { click(link, EventArgs.Empty); };
            return link;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string appName, string idList);

        /// <summary>Grey hint text shown in an empty text box.</summary>
        public static void CueBanner(TextBox box, string text)
        {
            const int EM_SETCUEBANNER = 0x1501;
            EventHandler set = delegate
            {
                try { SendMessage(box.Handle, EM_SETCUEBANNER, new IntPtr(1), text); }
                catch (Exception) { /* just no hint */ }
            };
            if (box.IsHandleCreated) set(box, EventArgs.Empty);
            else box.HandleCreated += set;
        }

        /// <summary>The Windows Explorer look for a list: soft blue hover and selection.</summary>
        public static void ExplorerTheme(Control list)
        {
            EventHandler set = delegate
            {
                try { SetWindowTheme(list.Handle, "Explorer", null); }
                catch (Exception) { /* classic look */ }
            };
            if (list.IsHandleCreated) set(list, EventArgs.Empty);
            else list.HandleCreated += set;
        }
    }
}
