using System.IO;
using System.Reflection;

namespace BDAT
{
    /// <summary>Which BDAT build is running, and where published builds live.</summary>
    internal static class BuildInfo
    {
        /// <summary>Folder on GitHub that Publish BDAT.bat pushes builds to.</summary>
        public const string ReleaseUrl = "https://raw.githubusercontent.com/bmetz04/bdat-solidworks-addin/main/release/";

        /// <summary>Every published version and what changed in it.</summary>
        public const string ReleaseNotesPage = "https://github.com/bmetz04/bdat-solidworks-addin/blob/main/RELEASE-NOTES.md";

        /// <summary>The installer, which also does updates.</summary>
        public const string SetupUrl = "https://raw.githubusercontent.com/bmetz04/bdat-solidworks-addin/main/BDAT%20Setup.bat";

        /// <summary>Full version stamped in by Publish BDAT, e.g. "v3 (2026-10-01 05:20)". "dev build" for local builds.</summary>
        public static string Version
        {
            get
            {
                object[] attrs = typeof(BuildInfo).Assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (attrs.Length == 0) return "dev build";
                return ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
            }
        }

        /// <summary>Just the version number, e.g. "v3".</summary>
        public static string ShortVersion
        {
            get
            {
                string v = Version;
                int paren = v.IndexOf(" (");
                return paren > 0 ? v.Substring(0, paren) : v;
            }
        }

        public static string DllPath
        {
            get { return Path.GetFullPath(typeof(BuildInfo).Assembly.Location); }
        }
    }
}
