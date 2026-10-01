using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace BDAT
{
    /// <summary>
    /// Draws toolbar icons at startup so the add-in ships as a single DLL.
    /// Replace with real PNG strips later if you want proper artwork.
    /// </summary>
    internal static class Icons
    {
        private static readonly int[] Sizes = { 20, 32, 40, 64, 96, 128 };

        private static string IconDir
        {
            get
            {
                string dir = Path.Combine(Path.GetTempPath(), "BDAT", "icons");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>One horizontal strip per size, one cell per command.</summary>
        public static string[] BuildIconStrips(int commandCount)
        {
            var paths = new string[Sizes.Length];
            for (int s = 0; s < Sizes.Length; s++)
            {
                int size = Sizes[s];
                string path = Path.Combine(IconDir, "commands_" + size + ".png");
                using (var bmp = new Bitmap(size * Math.Max(commandCount, 1), size, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    for (int i = 0; i < commandCount; i++)
                        DrawCommandIcon(g, i, new Rectangle(i * size, 0, size, size));
                    bmp.Save(path, ImageFormat.Png);
                }
                paths[s] = path;
            }
            return paths;
        }

        /// <summary>The icon shown for the add-in itself (menu and add-ins list).</summary>
        public static string[] BuildMainIcons()
        {
            var paths = new string[Sizes.Length];
            for (int s = 0; s < Sizes.Length; s++)
            {
                int size = Sizes[s];
                string path = Path.Combine(IconDir, "main_" + size + ".png");
                using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    DrawBadge(g, new Rectangle(0, 0, size, size), Color.FromArgb(30, 60, 120), "B");
                    bmp.Save(path, ImageFormat.Png);
                }
                paths[s] = path;
            }
            return paths;
        }

        private static void DrawCommandIcon(Graphics g, int index, Rectangle cell)
        {
            switch (index)
            {
                case 0: // Murder Part: red circle with a white X
                    DrawMurderIcon(g, cell);
                    break;
                case 1: // Update: green circle with a white down arrow
                    DrawUpdateIcon(g, cell);
                    break;
                case 2: // Version: blue circle with an "i"
                    DrawBadge(g, cell, Color.FromArgb(30, 90, 170), "i");
                    break;
                default:
                    DrawBadge(g, cell, Color.DimGray, (index + 1).ToString());
                    break;
            }
        }

        private static void DrawMurderIcon(Graphics g, Rectangle cell)
        {
            float pad = cell.Width * 0.08f;
            var circle = new RectangleF(cell.X + pad, cell.Y + pad, cell.Width - 2 * pad, cell.Height - 2 * pad);
            using (var fill = new SolidBrush(Color.FromArgb(200, 20, 20)))
                g.FillEllipse(fill, circle);

            float inset = cell.Width * 0.3f;
            using (var pen = new Pen(Color.White, Math.Max(2f, cell.Width * 0.12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, cell.X + inset, cell.Y + inset, cell.Right - inset, cell.Bottom - inset);
                g.DrawLine(pen, cell.Right - inset, cell.Y + inset, cell.X + inset, cell.Bottom - inset);
            }
        }

        private static void DrawUpdateIcon(Graphics g, Rectangle cell)
        {
            float pad = cell.Width * 0.08f;
            var circle = new RectangleF(cell.X + pad, cell.Y + pad, cell.Width - 2 * pad, cell.Height - 2 * pad);
            using (var fill = new SolidBrush(Color.FromArgb(30, 140, 60)))
                g.FillEllipse(fill, circle);

            float cx = cell.X + cell.Width / 2f;
            float top = cell.Y + cell.Height * 0.25f;
            float bottom = cell.Y + cell.Height * 0.72f;
            float wing = cell.Width * 0.2f;
            using (var pen = new Pen(Color.White, Math.Max(2f, cell.Width * 0.11f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLine(pen, cx, top, cx, bottom);
                g.DrawLines(pen, new[] { new PointF(cx - wing, bottom - wing), new PointF(cx, bottom), new PointF(cx + wing, bottom - wing) });
            }
        }

        private static void DrawBadge(Graphics g, Rectangle cell, Color color, string text)
        {
            float pad = cell.Width * 0.08f;
            var rect = new RectangleF(cell.X + pad, cell.Y + pad, cell.Width - 2 * pad, cell.Height - 2 * pad);
            using (var fill = new SolidBrush(color))
                g.FillEllipse(fill, rect);
            using (var font = new Font(FontFamily.GenericSansSerif, cell.Width * 0.45f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, font, Brushes.White, rect, format);
        }
    }
}
