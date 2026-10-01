using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace BDAT.Commands
{
    /// <summary>
    /// Checks GitHub for a newer published BDAT and, if there is one, starts the installer in "staged" mode.
    /// SolidWorks has BDAT.dll locked, so the installer downloads now, waits in the background for SolidWorks
    /// to close, then installs. The new version loads the next time SolidWorks starts.
    /// </summary>
    public sealed class UpdateCommand : IBdatCommand
    {
        private const string Caption = "Update BDAT";

        public string Title { get { return "Update BDAT"; } }

        public string Hint { get { return "Get the latest published BDAT. It installs when you close SolidWorks."; } }

        public bool IsEnabled(ISldWorks swApp) { return true; }

        public void Run(ISldWorks swApp)
        {
            string running = BuildInfo.Version;
            string latest;
            string notes = "";
            string installer = Path.Combine(Path.GetTempPath(), "BDAT", "BDAT Update.bat");
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (WebClient web = new WebClient())
                {
                    web.Headers.Add("Cache-Control", "no-cache");
                    web.Encoding = System.Text.Encoding.UTF8;
                    // The query string stops GitHub's download cache from handing back an older build.
                    string nocache = "?t=" + DateTime.UtcNow.Ticks;
                    latest = web.DownloadString(BuildInfo.ReleaseUrl + "version.txt" + nocache).Trim();
                    Directory.CreateDirectory(Path.GetDirectoryName(installer));
                    web.DownloadFile(BuildInfo.SetupUrl + nocache, installer);
                    try { notes = web.DownloadString(BuildInfo.ReleaseUrl + "notes.txt" + nocache).Trim(); }
                    catch (WebException) { notes = ""; } // Builds published before release notes existed.
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Couldn't check for updates. Check your internet connection.\n\n" + ex.Message,
                    Caption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (string.Equals(latest, running, StringComparison.Ordinal))
            {
                MessageBox.Show("You already have the latest BDAT (" + running + ").",
                    Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (notes.Length > 1500) notes = notes.Substring(0, 1500) + "\n...";
            string whatsNew = notes.Length > 0 ? "What's new in " + latest + ":\n" + notes + "\n\n" : "";

            DialogResult answer = MessageBox.Show(
                "Update BDAT from " + running + " to " + latest + "?\n\n" + whatsNew +
                "Windows will ask for admin rights. The update downloads now and installs as soon as you close " +
                "SolidWorks, so the new version is there the next time you open it.",
                Caption, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;

            ProcessStartInfo start = new ProcessStartInfo(installer, "staged");
            start.UseShellExecute = true;
            start.Verb = "runas";
            start.WindowStyle = ProcessWindowStyle.Minimized;
            try
            {
                Process.Start(start);
            }
            catch (Win32Exception ex)
            {
                if (ex.NativeErrorCode == 1223) return; // Clicked No on the admin prompt.
                throw;
            }

            MessageBox.Show(
                "BDAT " + latest + " will install when you close SolidWorks. Keep working; there's nothing else to do.\n\n" +
                "A minimized \"BDAT\" window waits on the taskbar until then. Don't close it.",
                Caption, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
