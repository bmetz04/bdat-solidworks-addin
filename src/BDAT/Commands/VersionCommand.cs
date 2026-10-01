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
            MessageBox.Show(
                "Running BDAT " + BuildInfo.Version + "\n\nLoaded from:\n" + BuildInfo.DllPath,
                "BDAT version", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
