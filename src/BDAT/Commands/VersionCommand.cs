using System.Diagnostics;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace BDAT.Commands
{
    /// <summary>Shows which BDAT version is running. The button text itself is the version number.</summary>
    public sealed class VersionCommand : IBdatCommand
    {
        public string Title { get { return "BDAT " + BuildInfo.ShortVersion; } }

        public string Hint { get { return "Running BDAT " + BuildInfo.Version; } }

        public bool IsEnabled(ISldWorks swApp) { return true; }

        public void Run(ISldWorks swApp)
        {
            string message = "Running BDAT " + BuildInfo.Version + "\n\nLoaded from:\n" + BuildInfo.DllPath;
            if (!Usage.Available)
            {
                DialogResult answer = Ui.Show(null, message + "\n\nOpen the release notes to see what changed in each version?",
                    "BDAT version", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes) Process.Start(BuildInfo.ReleaseNotesPage);
                return;
            }

            bool off = Usage.TurnedOff;
            message += "\n\nAnonymous usage sharing is " + (off ? "off" : "on") + ". It sends which BDAT buttons are used and " +
                "any errors, never names, files or part data.";
            int picked = Ui.Choose(null, message, "BDAT version", MessageBoxIcon.Information,
                "Release notes", off ? "Turn sharing on" : "Turn sharing off", "Close");
            if (picked == 0) Process.Start(BuildInfo.ReleaseNotesPage);
            else if (picked == 1) Usage.TurnedOff = !off;
        }
    }
}
