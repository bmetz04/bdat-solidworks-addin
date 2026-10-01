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
            DialogResult answer = Ui.Show(null,
                "Running BDAT " + BuildInfo.Version + "\n\nLoaded from:\n" + BuildInfo.DllPath +
                "\n\nOpen the release notes to see what changed in each version?",
                "BDAT version", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer == DialogResult.Yes) Process.Start(BuildInfo.ReleaseNotesPage);
        }
    }
}
