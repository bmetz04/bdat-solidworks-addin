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
    ///   2. Work out which bookmark is "McMaster Carr". The first time, you pick it once and BDAT remembers it.
    ///   3. Show a pop-up to confirm. For "91251A537_Socket Head Screw" the name is filled in with the part number
    ///      before the first underscore ("91251A537") and the description with the rest ("Socket Head Screw").
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

        // Formula UBC Racing > Vendor CAD > McMaster Carr, so nobody on the team has to pick it.
        // Set this to "" to make everyone pick the bookmark once instead (remembered in the registry).
        private const string KnownBookmarkId = "31D68EF2C00003006ABDD66A000060EA";

        private const string UserKeyPath = @"Software\BDAT";
        private const string BookmarkIdValue = "McMasterBookmarkId";
        private const string BookmarkTitleValue = "McMasterBookmarkTitle";

        // How long to wait for 3DEXPERIENCE to report the saved part's id (saves can finish in the background).
        private const int SaveWaitSeconds = 90;

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
            string sourceFile = SourceFileName(doc);
            string originalPath = doc.GetPathName();

            if (!string.IsNullOrEmpty(originalPath) && !string.IsNullOrEmpty(PhysicalId(connector, originalPath)))
            {
                DialogResult again = MessageBox.Show(owner,
                    "\"" + sourceFile + "\" is already in 3DEXPERIENCE.\n\nSave it as a new part anyway?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (again != DialogResult.Yes) return;
            }

            // 2. Bookmark.
            Bookmark bookmark = ResolveBookmark(connector, swApp, owner);
            if (bookmark == null) return;

            // 3. Confirm, name and description.
            string name, description;
            using (var form = new SaveMcmForm(sourceFile, DefaultName(sourceFile), DefaultDescription(sourceFile), DestinationPath + bookmark.Title))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return;
                name = form.PartName;
                description = form.Description;
            }

            string existing = ExistingOnPlatform(connector, doc, name);
            if (existing != null)
            {
                Tell(swApp, "\"" + name + "\" is already in 3DEXPERIENCE (" + existing + ").\n\n" +
                    "Nothing was saved. Use the existing part, or delete it from 3DEXPERIENCE first.",
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }

            // 4. Description, as custom properties so it goes up with the save. The file-level property becomes the
            //    CAD Family's description and each configuration's property becomes its Physical Product's.
            if (description.Length > 0) SetDescription(doc, description);

            // 5. Isometric view (so the 3DEXPERIENCE thumbnail is isometric) and freeze the whole feature tree.
            string freezeProblem = IsometricAndFreeze(swApp, doc);

            // 6. Save under the new name, then bookmark it.
            bool saved = SaveWithoutDialog(swApp, connector, doc, name);
            if (!saved)
            {
                // Fallback: the connector's own Save to 3DEXPERIENCE window (it pre-selects the bookmark).
                Log("falling back to SaveNoOption for " + name);
                object saver = connector.Manager("Save");
                IntPtr unknown = Marshal.GetIUnknownForObject(doc);
                try
                {
                    saved = (bool)connector.Call(saver, "IEnoSwSave", "SaveNoOption", unknown, name);
                }
                finally
                {
                    Marshal.Release(unknown);
                }
            }
            if (!saved)
            {
                Tell(swApp, "3DEXPERIENCE didn't save \"" + name + "\". Check the 3DEXPERIENCE task pane for details.\n\n" +
                    "If a part with that name is already in your work folder, pick a different name.",
                    swMessageBoxIcon_e.swMbStop);
                return;
            }

            string phid = WaitForPhysicalId(connector, doc);
            if (string.IsNullOrEmpty(phid))
            {
                Tell(swApp, "\"" + name + "\" was sent to 3DEXPERIENCE, but it didn't show up within " + SaveWaitSeconds +
                    " seconds, so it wasn't added to " + bookmark.Title + ".\n\n" +
                    "Once the save finishes, add it to the bookmark by hand (right-click it in 3DEXPERIENCE > Add to Bookmark).",
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }

            object authoring = connector.Manager("Authoring");
            object result = connector.Call(authoring, "IEnoSwAuthoring", "AddToBookmark", bookmark.Id, new[] { phid });
            Log("AddToBookmark(" + bookmark.Id + ", " + phid + ") returned " + result);

            // 7. Check in: unlock the part so it isn't left reserved by you.
            string done = "Saved \"" + name + "\" to 3DEXPERIENCE in " + DestinationPath + bookmark.Title + " and checked it in.";
            swMessageBoxIcon_e icon = swMessageBoxIcon_e.swMbInformation;
            if (!Unlock(connector, doc.GetPathName()))
            {
                done = "Saved \"" + name + "\" to 3DEXPERIENCE in " + DestinationPath + bookmark.Title +
                    ", but couldn't check it in, so it's still locked by you.\n\n" +
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

            var result = new SaveMcmTestResult
            {
                SourceFile = sourceFile,
                Name = name,
                Description = description,
                Destination = DestinationPath + BookmarkTitle,
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

        /// <summary>Releases your lock on the saved file. True if it's unlocked afterwards (or was never locked).</summary>
        private static bool Unlock(Connector connector, string path)
        {
            if (string.IsNullOrEmpty(path)) { Log("check-in: the part has no file path"); return false; }
            try
            {
                object cache = connector.Manager("FileCache");
                object before = connector.Call(cache, "IEnoSwFileCache7", "GetLockStatus", path);
                Log("check-in: lock status before " + before);
                if (before != null && before.ToString() == "notLocked") { Log("check-in: already unlocked"); return true; }

                object commands = connector.Manager("UiCommands");
                bool ok = (bool)connector.Call(commands, "IEnoSwUiCommands", "UnreserveFiles", (object)new[] { path });
                object after = connector.Call(cache, "IEnoSwFileCache7", "GetLockStatus", path);
                Log("check-in: UnreserveFiles returned " + ok + ", lock status after " + after);
                return ok && (after == null || after.ToString() != "lockedByMe");
            }
            catch (Exception ex)
            {
                Log("check-in failed: " + ex.Message);
                return false;
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

        /// <summary>
        /// Saves the part as "name.SLDPRT" in the 3DEXPERIENCE work folder, then has the connector upload that file
        /// with no Save to 3DEXPERIENCE window. Returns false, having changed nothing, if it can't get that far,
        /// so the caller can fall back to the connector's window. If the upload itself fails, it shows the
        /// connector's window for the same file instead.
        /// </summary>
        private static bool SaveWithoutDialog(ISldWorks swApp, Connector connector, IModelDoc2 doc, string name)
        {
            string path;
            try
            {
                string workFolder = connector.Call(connector.Manager("Open"), "IEnoSwOpen3", "GetWorkFolder") as string;
                if (string.IsNullOrEmpty(workFolder) || !Directory.Exists(workFolder)) { Log("no work folder: " + workFolder); return false; }

                path = Path.Combine(workFolder, name + ".SLDPRT");
                bool alreadyThere = string.Equals(doc.GetPathName(), path, StringComparison.OrdinalIgnoreCase);
                if (!alreadyThere && File.Exists(path))
                {
                    // A leftover local copy, e.g. the part was deleted from 3DEXPERIENCE but not from the work folder.
                    // Only overwrite it if it isn't on the platform and isn't open in SolidWorks.
                    if (!string.IsNullOrEmpty(PhysicalId(connector, path))) { Log("already on the platform: " + path); return false; }
                    if (swApp.GetOpenDocumentByName(path) != null) { Log("work folder copy is open: " + path); return false; }
                    Log("overwriting leftover work folder copy: " + path);
                }

                if (!alreadyThere)
                {
                    Log("saving local copy: " + path);
                    int errors = 0, warnings = 0;
                    bool local = doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                        (int)swSaveAsOptions_e.swSaveAsOptions_Silent, null, null, ref errors, ref warnings);
                    if (!local || !File.Exists(path)) { Log("local save failed, error " + errors); return false; }
                }
            }
            catch (Exception ex)
            {
                Log("local save failed: " + ex.Message);
                return false;
            }

            Log("local copy saved, uploading with SaveAPI");
            try
            {
                if (UploadWithSaveApi(connector, path)) { Log("saved without dialog: " + path); return true; }
            }
            catch (Exception ex)
            {
                Log("SaveAPI failed: " + ex.Message);
            }

            object saver = connector.Manager("Save");
            Log("showing the connector window for " + path);
            return (bool)connector.Call(saver, "IEnoSwSave5", "SaveFile", path, true);
        }

        /// <summary>
        /// The connector's scripting save (IEnoSwSaveAPI): Prepare then Commit, JSON in and out, no window.
        /// Input is {"version":"1.0","scope":[{"file":"path"}]}; each reply has "status":"OK" or "ERROR" and "errInfo".
        /// </summary>
        private static bool UploadWithSaveApi(Connector connector, string path)
        {
            object api = connector.Manager("SaveAPI");
            string file = path.Replace("\\", "\\\\").Replace("\"", "\\\"");

            string prepared = connector.Call(api, "IEnoSwSaveAPI", "Prepare",
                "{\"version\":\"1.0\",\"scope\":[{\"file\":\"" + file + "\"}]}") as string;
            Log("SaveAPI Prepare: " + prepared);
            if (prepared == null || prepared.IndexOf("\"OK\"", StringComparison.Ordinal) < 0) return false;

            string committed = connector.Call(api, "IEnoSwSaveAPI", "Commit", "{\"version\":\"1.0\"}") as string;
            Log("SaveAPI Commit: " + committed);
            return committed != null && committed.IndexOf("\"OK\"", StringComparison.Ordinal) >= 0;
        }

        /// <summary>The work-folder path of "name.SLDPRT" if a part by that name is already on the platform, otherwise null.</summary>
        private static string ExistingOnPlatform(Connector connector, IModelDoc2 doc, string name)
        {
            try
            {
                string workFolder = connector.Call(connector.Manager("Open"), "IEnoSwOpen3", "GetWorkFolder") as string;
                if (string.IsNullOrEmpty(workFolder)) return null;
                string path = Path.Combine(workFolder, name + ".SLDPRT");
                if (string.Equals(doc.GetPathName(), path, StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(path) && !string.IsNullOrEmpty(PhysicalId(connector, path)) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        private static string PhysicalId(Connector connector, string path)
        {
            try
            {
                return connector.Call(connector.Manager("FileCache"), "IEnoSwFileCache", "GetFilePhysicalId", path) as string;
            }
            catch
            {
                return null;
            }
        }

        private static string WaitForPhysicalId(Connector connector, IModelDoc2 doc)
        {
            DateTime giveUp = DateTime.Now.AddSeconds(SaveWaitSeconds);
            while (true)
            {
                string path = doc.GetPathName();
                string phid = string.IsNullOrEmpty(path) ? null : PhysicalId(connector, path);
                if (string.IsNullOrEmpty(phid)) phid = doc.Extension.GetPLMID();
                if (!string.IsNullOrEmpty(phid)) return phid;
                if (DateTime.Now > giveUp) return null;
                Application.DoEvents();
                Thread.Sleep(1000);
            }
        }

        private sealed class Bookmark
        {
            public string Id;
            public string Title;
        }

        /// <summary>The McMaster Carr bookmark: built in, remembered, or picked once.</summary>
        private Bookmark ResolveBookmark(Connector connector, ISldWorks swApp, IWin32Window owner)
        {
            if (KnownBookmarkId.Length > 0) return new Bookmark { Id = KnownBookmarkId, Title = BookmarkTitle };

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UserKeyPath))
            {
                string id = key == null ? null : key.GetValue(BookmarkIdValue) as string;
                string title = key == null ? null : key.GetValue(BookmarkTitleValue) as string;
                if (!string.IsNullOrEmpty(id)) return new Bookmark { Id = id, Title = string.IsNullOrEmpty(title) ? BookmarkTitle : title };
            }

            MessageBox.Show(owner,
                "BDAT needs to know which 3DEXPERIENCE bookmark is McMaster Carr. You only do this once.\n\n" +
                "In the next window, open Formula UBC Racing > Vendor CAD, select McMaster Carr and click Select.",
                Title, MessageBoxButtons.OK, MessageBoxIcon.Information);

            object chooser = connector.Manager("BookmarkChooser");
            connector.Set(chooser, "IEnoSwBookmarkChooser", "DialogTitle", "Pick the McMaster Carr bookmark");
            connector.Set(chooser, "IEnoSwBookmarkChooser", "ShowCancelButton", true);
            connector.Set(chooser, "IEnoSwBookmarkChooser", "ShowSelectButton", true);
            connector.Call(chooser, "IEnoSwBookmarkChooser2", "ShowDialog2", owner.Handle);

            var picked = new Bookmark
            {
                Id = connector.Get(chooser, "IEnoSwBookmarkChooser", "SelectedBookmarkId") as string,
                Title = connector.Get(chooser, "IEnoSwBookmarkChooser", "SelectedBookmarkTitle") as string,
            };
            if (string.IsNullOrEmpty(picked.Id)) return null;
            if (string.IsNullOrEmpty(picked.Title)) picked.Title = BookmarkTitle;

            if (!string.Equals(picked.Title, BookmarkTitle, StringComparison.OrdinalIgnoreCase) &&
                MessageBox.Show(owner, "You picked \"" + picked.Title + "\", not \"" + BookmarkTitle + "\". Use it anyway?",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return null;

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UserKeyPath))
            {
                if (key != null)
                {
                    key.SetValue(BookmarkIdValue, picked.Id, RegistryValueKind.String);
                    key.SetValue(BookmarkTitleValue, picked.Title, RegistryValueKind.String);
                }
            }
            Log("McMaster Carr bookmark id = " + picked.Id);
            return picked;
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

        /// <summary>Appends a line to %TEMP%\BDAT\save-mcm.log, so a failed save can be diagnosed afterwards.</summary>
        private static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "BDAT");
                Directory.CreateDirectory(dir);
                // Test runs get their own log so they never show up as a real save.
                File.AppendAllText(Path.Combine(dir, TestMode.Enabled ? "save-mcm-tests.log" : "save-mcm.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + System.Environment.NewLine);
            }
            catch
            {
                // Logging must never break the save.
            }
        }

        private static void Tell(ISldWorks swApp, string message, swMessageBoxIcon_e icon)
        {
            swApp.SendMsgToUser2(message, (int)icon, (int)swMessageBoxBtn_e.swMbOk);
        }
    }
}
