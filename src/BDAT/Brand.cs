using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace BDAT
{
    /// <summary>
    /// The FUBC logo, embedded in BDAT.dll (src/BDAT/Resources, added by build.bat's /resource flags), for pop-up headers.
    /// </summary>
    internal static class Brand
    {
        private static Image _black, _white;

        /// <summary>The black logo on a transparent background, 256 px tall. Null if the resource is missing.</summary>
        public static Image Logo { get { return _black ?? (_black = Load("BDAT.fubc-logo.png")); } }

        /// <summary>The same logo in white, for dark headers.</summary>
        public static Image LogoWhite { get { return _white ?? (_white = Load("BDAT.fubc-logo-white.png")); } }

        /// <summary>A PictureBox showing the logo scaled to the given height, ready to drop into a pop-up's header.</summary>
        public static PictureBox LogoBox(int height, bool white)
        {
            Image image = white ? LogoWhite : Logo;
            var box = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            if (image != null)
            {
                box.Image = Scaled(image, height);
                box.Size = box.Image.Size;
            }
            return box;
        }

        /// <summary>The logo resized smoothly to the given height (PictureBox's own scaling looks jagged on thin lines).</summary>
        public static Image Scaled(Image image, int height)
        {
            int width = (int)System.Math.Round(image.Width * (double)height / image.Height);
            var bmp = new Bitmap(System.Math.Max(width, 1), System.Math.Max(height, 1));
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(image, 0, 0, width, height);
            }
            return bmp;
        }

        private static Image Load(string name)
        {
            Stream stream = typeof(Brand).Assembly.GetManifestResourceStream(name);
            if (stream == null) return null;
            using (stream) return new Bitmap(Image.FromStream(stream));
        }
    }
}
