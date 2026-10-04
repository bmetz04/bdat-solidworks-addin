using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT.Commands
{
    /// <summary>
    /// Save MCM: save the open McMaster-Carr part to 3DEXPERIENCE, in Formula UBC Racing > Vendor CAD > McMaster Carr.
    ///
    /// Steps:
    ///   1. Find the 3DEXPERIENCE connector and make sure you're logged in.
    ///   2. Show a pop-up to confirm. For "91251A537_Socket Head Screw" the name is filled in with the part number
    ///      before the first underscore ("91251A537") and the description with the rest ("Socket Head Screw").
    ///   3. Confirm the folder, the same way New from EBOM does: "Save 91251A537 in McMaster Carr?". Yes uses the
    ///      known McMaster Carr bookmark, No (or no known bookmark) opens the picker and remembers the pick.
    ///   4. Put the description in the part's "Description" custom property, file-level (CAD Family) and on every
    ///      configuration (Physical Product).
    ///   5. Set the view to isometric (that becomes the 3DEXPERIENCE thumbnail) and freeze the whole feature tree.
    ///   6. Save the part to 3DEXPERIENCE under the new name, then add it to the McMaster Carr bookmark.
    ///   7. Check it in (unlock it) so it isn't left reserved by you.
    /// </summary>
    public sealed class SaveMcmCommand : IBdatCommand
    {
        public string Title { get { return "Save MCM"; } }

        public string Hint { get { return "Save this McMaster-Carr part to 3DEXPERIENCE in Vendor CAD > McMaster Carr"; } }

        private const string DestinationPath = "Formula UBC Racing > Vendor CAD > ";
        private const string BookmarkTitle = "McMaster Carr";
        private const string DescriptionProperty = "Description";

        // Formula UBC Racing > Vendor CAD > McMaster Carr, so nobody on the team has to pick it. A folder picked with
        // "No" at the confirm is remembered in the registry and wins over this. "" makes everyone pick it once.
        private const string KnownBookmarkId = "31D68EF2C00003006ABDD66A000060EA";

        private const string UserKeyPath = @"Software\BDAT";
        private const string BookmarkIdValue = "McMasterBookmarkId";
        private const string BookmarkTitleValue = "McMasterBookmarkTitle";

        public bool IsEnabled(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            return doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocPART;
        }

        public void Run(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                Tell(swApp, "Open a McMaster-Carr part first.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            // Test mode (tests\run-tests.ps1): steps 3 to 5 only. Never touches 3DEXPERIENCE.
            if (TestMode.Enabled)
            {
                RunTest(swApp, doc);
                return;
            }

            // 1. Connector.
            Connector connector = Connector.Find();
            if (connector == null)
            {
                Tell(swApp, "Couldn't find the 3DEXPERIENCE connector in this SolidWorks session.\n\n" +
                    "Make sure the \"3DEXPERIENCE PLM Services\" add-in is on (Tools > Add-Ins), then try again.",
                    swMessageBoxIcon_e.swMbStop);
                return;
            }
            if (!connector.IsConnected)
            {
                Tell(swApp, "You're not logged in to 3DEXPERIENCE. Log in from the 3DEXPERIENCE task pane, then try again.",
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }

            IWin32Window owner = SolidWorksWindow(swApp);
            var platform = new PlatformSave(swApp, connector, LogName);
            string sourceFile = SourceFileName(doc);
            string originalPath = doc.GetPathName();

            if (!string.IsNullOrEmpty(originalPath) && !string.IsNullOrEmpty(platform.PhysicalId(originalPath)))
            {
                DialogResult again = MessageBox.Show(owner,
                    "\"" + sourceFile + "\" is already in 3DEXPERIENCE.\n\nSave it as a new part anyway?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (again != DialogResult.Yes) return;
            }

            // 2. Confirm, name and description.
            Bookmark known = KnownBookmark();
            string name, description;
            using (var form = new SaveMcmForm(sourceFile, DefaultName(sourceFile), DefaultDescription(sourceFile),
                DestinationPath + (known == null ? BookmarkTitle : known.Title)))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return;
                name = form.PartName;
                description = form.Description;
            }

            string existing = platform.ExistingOnPlatform(doc, name + ".SLDPRT");
            if (existing != null)
            {
                Tell(swApp, "\"" + name + "\" is already in 3DEXPERIENCE (" + existing + ").\n\n" +
                    "Nothing was saved. Use the existing part, or delete it from 3DEXPERIENCE first.",
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }

            // 3. Folder: "Save 91251A537 in McMaster Carr?" Yes, or No to pick another.
            Bookmark bookmark = ConfirmFolder(connector, owner, name, known);
            if (bookmark == null)
            {
                Tell(swApp, "Nothing was saved.", swMessageBoxIcon_e.swMbInformation);
                return;
            }

            // 4. Description, as custom properties so it goes up with the save. The file-level property becomes the
            //    CAD Family's description and each configuration's property becomes its Physical Product's.
            if (description.Length > 0) SetDescription(doc, description);

            // 5. Isometric view (so the 3DEXPERIENCE thumbnail is isometric) and freeze the whole feature tree.
            string freezeProblem = IsometricAndFreeze(swApp, doc);

            // 6. Save under the new name, then bookmark it.
            // (If it can't save without a window it falls back to the connector's own, which pre-selects the bookmark.)
            bool saved = platform.Save(doc, name + ".SLDPRT");
            if (!saved)
            {
                Tell(swApp, "3DEXPERIENCE didn't save \"" + name + "\". Check the 3DEXPERIENCE task pane for details.\n\n" +
                    "If a part with that name is already in your work folder, pick a different name.",
                    swMessageBoxIcon_e.swMbStop);
                return;
            }

            string phid = platform.WaitForPhysicalId(doc);
            if (string.IsNullOrEmpty(phid))
            {
                Tell(swApp, "\"" + name + "\" was sent to 3DEXPERIENCE, but it didn't show up within " + PlatformSave.SaveWaitSeconds +
                    " seconds, so it wasn't added to " + bookmark.Title + ".\n\n" +
                    "Once the save finishes, add it to the bookmark by hand (right-click it in 3DEXPERIENCE > Add to Bookmark).",
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }

            string refused = platform.AddToBookmark(bookmark.Id, phid);

            // A folder that was deleted or moved (a stale id) won't take the part: offer to pick McMaster Carr again and
            // retry. A security context refusal is about your 3DEXPERIENCE role, so picking again wouldn't help.
            if (refused != null && refused.IndexOf("security context", StringComparison.OrdinalIgnoreCase) < 0 &&
                Ui.Show(owner, "\"" + name + "\" was saved, but it couldn't be put in " + bookmark.Title + ":\n\n" + refused +
                    "\n\nThe folder may have been deleted or moved. Pick the McMaster Carr folder now?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                Bookmark repicked = PickFolder(connector, owner);
                if (repicked != null)
                {
                    refused = platform.AddToBookmark(repicked.Id, phid);
                    bookmark = repicked;
                }
            }

            // 7. Check in: unlock the part so it isn't left reserved by you.
            string done = "Saved \"" + name + "\" to 3DEXPERIENCE in " + DestinationPath + bookmark.Title + " and checked it in.";
            swMessageBoxIcon_e icon = swMessageBoxIcon_e.swMbInformation;
            if (refused != null)
            {
                done = "Saved \"" + name + "\" to 3DEXPERIENCE and checked it in, but it couldn't be put in " + DestinationPath +
                    bookmark.Title + ":\n\n" + refused + PlatformSave.BookmarkAdvice(refused);
                icon = swMessageBoxIcon_e.swMbWarning;
            }
            if (!platform.Unlock(doc.GetPathName()))
            {
                done += "\n\nIt couldn't be checked in, so it's still locked by you. " +
                    "Unlock it from the 3DEXPERIENCE task pane (right-click it > Unlock).";
                icon = swMessageBoxIcon_e.swMbWarning;
            }
            if (freezeProblem != null)
            {
                done += "\n\nThe feature tree wasn't frozen: " + freezeProblem;
                icon = swMessageBoxIcon_e.swMbWarning;
            }
            Tell(swApp, done, icon);
        }

        /// <summary>
        /// Test mode: steps 3 to 5 exactly as above (pop-up answers come from TestMode), then stop and record what
        /// would have been saved. Saving, bookmarking and checking in need 3DEXPERIENCE, so they're never run.
        /// </summary>
        private void RunTest(ISldWorks swApp, IModelDoc2 doc)
        {
            string sourceFile = SourceFileName(doc);
            string name = (TestMode.SaveMcmName ?? DefaultName(sourceFile)).Trim();
            string description = (TestMode.SaveMcmDescription ?? DefaultDescription(sourceFile)).Trim();

            string error = SaveMcmForm.NameError(name);
            if (error != null)
            {
                Ui.Tell(swApp, error, swMessageBoxIcon_e.swMbWarning);
                return;
            }
            if (description.Length == 0 &&
                Ui.Show(null, SaveMcmForm.EmptyDescriptionQuestion, Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            // The folder confirm, answered from TestMode.Answers. No would open the picker, which needs 3DEXPERIENCE.
            Bookmark known = KnownBookmark();
            if (known == null || Ui.Show(null, FolderQuestion(name, known), Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Ui.Tell(swApp, "Test mode: the McMaster Carr folder picker needs 3DEXPERIENCE, so it isn't opened.", swMessageBoxIcon_e.swMbInformation);
                return;
            }

            var result = new SaveMcmTestResult
            {
                SourceFile = sourceFile,
                Name = name,
                Description = description,
                Destination = DestinationPath + known.Title,
            };
            if (description.Length > 0)
            {
                SetDescription(doc, description);
                result.Steps.Add("description");
            }

            string freezeProblem = IsometricAndFreeze(swApp, doc);
            result.Steps.Add("isometric");
            if (freezeProblem != null)
            {
                Ui.Tell(swApp, "The feature tree wasn't frozen: " + freezeProblem, swMessageBoxIcon_e.swMbWarning);
                return;
            }
            result.Steps.Add("freeze");

            result.Steps.Add("skipped: save to 3DEXPERIENCE");
            result.Steps.Add("skipped: add to bookmark");
            result.Steps.Add("skipped: check in");
            TestMode.LastSaveMcm = result;
        }

        /// <summary>Step 5: isometric view (it becomes the 3DEXPERIENCE thumbnail), then freeze the whole tree.
        /// Returns null on success, otherwise why the tree wasn't frozen.</summary>
        private static string IsometricAndFreeze(ISldWorks swApp, IModelDoc2 doc)
        {
            doc.ShowNamedView2("*Isometric", (int)swStandardViews_e.swIsometricView);
            doc.ViewZoomtofit2();
            return FreezeAll(swApp, doc);
        }

        /// <summary>Moves the freeze bar to the bottom of the tree. Returns null on success, otherwise what went wrong.</summary>
        private static string FreezeAll(ISldWorks swApp, IModelDoc2 doc)
        {
            try
            {
                // The freeze bar only exists when it's turned on in System Options > General.
                if (!swApp.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUserEnableFreezeBar))
                    swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUserEnableFreezeBar, true);

                int status = doc.FeatureManager.EditFreeze2(
                    (int)swMoveFreezeBarTo_e.swMoveFreezeBarToEnd, "", true, true);
                Log("EditFreeze2 returned " + status);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>The McMaster part number: "91251A537_Socket Head Screw" -> "91251A537".</summary>
        internal static string DefaultName(string fileName)
        {
            string name = WithoutMurderSuffix(fileName);
            int underscore = name.IndexOf('_');
            if (underscore > 0) name = name.Substring(0, underscore);
            return name.Trim();
        }

        /// <summary>The rest of the file name: "91251A537_Socket Head Screw" -> "Socket Head Screw". Empty if there's no underscore.</summary>
        internal static string DefaultDescription(string fileName)
        {
            string name = WithoutMurderSuffix(fileName);
            int underscore = name.IndexOf('_');
            return underscore >= 0 ? name.Substring(underscore + 1).Trim() : "";
        }

        private static string WithoutMurderSuffix(string fileName)
        {
            const string suffix = "_murdered";
            return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - suffix.Length)
                : fileName;
        }

        private static string SourceFileName(IModelDoc2 doc)
        {
            string path = doc.GetPathName();
            string name = string.IsNullOrEmpty(path) ? doc.GetTitle() : Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 7);
            return name;
        }

        private static void SetDescription(IModelDoc2 doc, string description)
        {
            var targets = new System.Collections.Generic.List<string> { "" };
            string[] configs = doc.GetConfigurationNames() as string[];
            if (configs != null) targets.AddRange(configs);

            foreach (string config in targets)
            {
                CustomPropertyManager props = doc.Extension.get_CustomPropertyManager(config);
                if (props == null) continue;
                props.Add3(DescriptionProperty, (int)swCustomInfoType_e.swCustomInfoText, description,
                    (int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            }
        }

        /// <summary>The McMaster Carr bookmark: one picked on this PC, else the built-in one. Null if neither.</summary>
        internal static Bookmark KnownBookmark()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UserKeyPath))
                {
                    string id = key == null ? null : key.GetValue(BookmarkIdValue) as string;
                    string title = key == null ? null : key.GetValue(BookmarkTitleValue) as string;
                    if (!string.IsNullOrEmpty(id)) return new Bookmark { Id = id, Title = string.IsNullOrEmpty(title) ? BookmarkTitle : title };
                }
            }
            catch (Exception)
            {
                // Nothing picked on this PC.
            }
            return KnownBookmarkId.Length > 0 ? new Bookmark { Id = KnownBookmarkId, Title = BookmarkTitle } : null;
        }

        internal static string FolderQuestion(string name, Bookmark folder)
        {
            return "Save " + name + " in " + folder.Title + "?\n\nNo picks a different folder.";
        }

        /// <summary>
        /// Like New from EBOM: a known folder is confirmed with "Save 91251A537 in McMaster Carr?" (Yes uses it, No opens
        /// the picker); with no known folder the picker opens straight away. Null if cancelled.
        /// </summary>
        private Bookmark ConfirmFolder(Connector connector, IWin32Window owner, string name, Bookmark known)
        {
            if (known != null)
            {
                DialogResult use = Ui.Show(owner, FolderQuestion(name, known), Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (use == DialogResult.Yes) return known;
                if (use != DialogResult.No) return null;
            }
            else if (Ui.Show(owner,
                "Which 3DEXPERIENCE folder is McMaster Carr? In the next window, open Formula UBC Racing > Vendor CAD and pick " +
                "McMaster Carr. BDAT remembers it.", Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            {
                return null;
            }
            return PickFolder(connector, owner);
        }

        /// <summary>The picker titled "Pick McMaster Carr", with the New from EBOM name check. Remembers the pick. Null if cancelled.</summary>
        private Bookmark PickFolder(Connector connector, IWin32Window owner)
        {
            Bookmark picked = PlatformSave.ChooseBookmark(connector, owner, "Pick " + BookmarkTitle);
            if (picked == null) return null;
            if (picked.Title.Length == 0) picked.Title = BookmarkTitle;

            if (!IsMcMasterFolder(picked.Title) &&
                Ui.Show(owner, "You picked \"" + picked.Title + "\", which isn't named " + BookmarkTitle + ". Use it for McMaster parts from now on?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                return null;

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UserKeyPath))
            {
                if (key != null)
                {
                    key.SetValue(BookmarkIdValue, picked.Id, RegistryValueKind.String);
                    key.SetValue(BookmarkTitleValue, picked.Title, RegistryValueKind.String);
                }
            }
            Log("McMaster Carr folder = " + picked.Id + " (" + picked.Title + ")");
            return picked;
        }

        /// <summary>True if a bookmark title names the McMaster Carr folder, in any case ("Mcmaster Carr" counts).</summary>
        internal static bool IsMcMasterFolder(string title)
        {
            return EbomFolders.TitleMatches(title, BookmarkTitle);
        }

        private static IWin32Window SolidWorksWindow(ISldWorks swApp)
        {
            IFrame frame = swApp.Frame() as IFrame;
            return new WindowHandle(frame == null ? IntPtr.Zero : new IntPtr(frame.GetHWndx64()));
        }

        private sealed class WindowHandle : IWin32Window
        {
            private readonly IntPtr _handle;
            public WindowHandle(IntPtr handle) { _handle = handle; }
            public IntPtr Handle { get { return _handle; } }
        }

        private const string LogName = "save-mcm";

        /// <summary>Appends a line to %TEMP%\BDAT\save-mcm.log, so a failed save can be diagnosed afterwards.</summary>
        private static void Log(string message)
        {
            PlatformSave.Log(LogName, message);
        }

        private static void Tell(ISldWorks swApp, string message, swMessageBoxIcon_e icon)
        {
            swApp.SendMsgToUser2(message, (int)icon, (int)swMessageBoxBtn_e.swMbOk);
        }
    }
}
