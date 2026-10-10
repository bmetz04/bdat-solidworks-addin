using System.IO;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT.Commands
{
    /// <summary>
    /// Check Out: locks (reserves) the open part or assembly in 3DEXPERIENCE so you can change it, the same as
    /// right-click > Lock in the 3DEXPERIENCE task pane. Only the open document is locked, not the parts inside an assembly.
    /// </summary>
    public sealed class CheckOutCommand : IBdatCommand
    {
        public string Title { get { return "Check Out"; } }

        public string Hint { get { return "Lock this part or assembly in 3DEXPERIENCE so you can edit it"; } }

        public bool IsEnabled(ISldWorks swApp) { return CheckInOut.IsPartOrAssembly(swApp); }

        public void Run(ISldWorks swApp)
        {
            IModelDoc2 doc;
            PlatformSave platform = CheckInOut.Start(swApp, Title, out doc);
            if (platform == null) return;

            string path = doc.GetPathName();
            string name = Path.GetFileNameWithoutExtension(path);
            string before = platform.LockStatus(path);
            if (before == "lockedByMe")
            {
                Ui.Tell(swApp, name + " is already checked out to you.", swMessageBoxIcon_e.swMbInformation);
                return;
            }
            if (before != null && before != "notLocked")
            {
                Ui.Tell(swApp, name + " is checked out by someone else, so it can't be checked out to you.\n\n" +
                    "The 3DEXPERIENCE task pane shows who has it.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            if (platform.Reserve(path))
                Ui.Tell(swApp, "Checked out " + name + ". Click Check In when you're done.", swMessageBoxIcon_e.swMbInformation);
            else
                Ui.Tell(swApp, "3DEXPERIENCE didn't check out " + name + ".\n\n" +
                    "If someone saved a newer version, reload it from the 3DEXPERIENCE task pane first, then try again.",
                    swMessageBoxIcon_e.swMbWarning);
        }
    }

    /// <summary>
    /// Check In: saves your changes to the open part or assembly to 3DEXPERIENCE (no Save window), then unlocks it,
    /// the same as Save to 3DEXPERIENCE followed by right-click > Unlock in the task pane.
    /// </summary>
    public sealed class CheckInCommand : IBdatCommand
    {
        public string Title { get { return "Check In"; } }

        public string Hint { get { return "Save your changes to 3DEXPERIENCE and unlock this part or assembly"; } }

        public bool IsEnabled(ISldWorks swApp) { return CheckInOut.IsPartOrAssembly(swApp); }

        public void Run(ISldWorks swApp)
        {
            IModelDoc2 doc;
            PlatformSave platform = CheckInOut.Start(swApp, Title, out doc);
            if (platform == null) return;

            string path = doc.GetPathName();
            string name = Path.GetFileNameWithoutExtension(path);
            string before = platform.LockStatus(path);
            if (before == "notLocked")
            {
                Ui.Tell(swApp, name + " isn't checked out, so there's nothing to check in.", swMessageBoxIcon_e.swMbInformation);
                return;
            }
            if (before != null && before != "lockedByMe")
            {
                Ui.Tell(swApp, name + " is checked out by someone else, so only they can check it in.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            // Unlocking with unsaved changes would leave them out of 3DEXPERIENCE, so save them first.
            if (doc.GetSaveFlag())
            {
                if (Ui.Show(Ui.SolidWorksWindow(swApp), name + " has changes that aren't in 3DEXPERIENCE yet.\n\nSave them and check it in?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                if (!platform.SaveChanges(doc))
                {
                    Ui.Tell(swApp, "3DEXPERIENCE didn't save " + name + ", so it's still checked out to you.\n\n" +
                        "Save it from the 3DEXPERIENCE task pane, then click Check In again.", swMessageBoxIcon_e.swMbStop);
                    return;
                }
            }

            if (platform.Unlock(path))
                Ui.Tell(swApp, "Checked in " + name + ".", swMessageBoxIcon_e.swMbInformation);
            else
                Ui.Tell(swApp, "3DEXPERIENCE didn't check in " + name + ", so it's still checked out to you.\n\n" +
                    "Unlock it from the 3DEXPERIENCE task pane (right-click it > Unlock).", swMessageBoxIcon_e.swMbWarning);
        }
    }

    /// <summary>What Check Out and Check In share: the open document, the connector and the login check.</summary>
    internal static class CheckInOut
    {
        private const string LogName = "check-in-out";

        public static bool IsPartOrAssembly(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            if (doc == null) return false;
            int type = doc.GetType();
            return type == (int)swDocumentTypes_e.swDocPART || type == (int)swDocumentTypes_e.swDocASSEMBLY;
        }

        /// <summary>
        /// The PlatformSave to lock or unlock the open document with, or null (having told the user why) if there's
        /// no part or assembly open, it isn't in 3DEXPERIENCE, or the connector isn't there or logged in.
        /// </summary>
        public static PlatformSave Start(ISldWorks swApp, string title, out IModelDoc2 doc)
        {
            doc = IsPartOrAssembly(swApp) ? (IModelDoc2)swApp.ActiveDoc : null;
            if (doc == null)
            {
                Ui.Tell(swApp, "Open a part or assembly first.", swMessageBoxIcon_e.swMbWarning);
                return null;
            }

            // Locking and unlocking are writes to 3DEXPERIENCE, which tests must never do.
            if (TestMode.Enabled)
            {
                Ui.Tell(swApp, "Test mode: " + title + " needs 3DEXPERIENCE, so nothing was done.", swMessageBoxIcon_e.swMbInformation);
                return null;
            }

            Connector connector = Connector.Find();
            if (connector == null)
            {
                Ui.Tell(swApp, "Couldn't find the 3DEXPERIENCE connector in this SolidWorks session.\n\n" +
                    "Make sure the \"3DEXPERIENCE PLM Services\" add-in is on (Tools > Add-Ins), then try again.",
                    swMessageBoxIcon_e.swMbStop);
                return null;
            }
            if (!connector.IsConnected)
            {
                Ui.Tell(swApp, "You're not logged in to 3DEXPERIENCE. Log in from the 3DEXPERIENCE task pane, then try again.",
                    swMessageBoxIcon_e.swMbWarning);
                return null;
            }

            var platform = new PlatformSave(swApp, connector, LogName);
            string path = doc.GetPathName();
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(platform.PhysicalId(path)))
            {
                Ui.Tell(swApp, "This isn't in 3DEXPERIENCE yet. Save it to 3DEXPERIENCE first.", swMessageBoxIcon_e.swMbWarning);
                return null;
            }
            return platform;
        }
    }
}
