using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT
{
    /// <summary>A 3DEXPERIENCE bookmark (what the team calls a folder).</summary>
    internal sealed class Bookmark
    {
        public string Id;
        public string Title;
    }

    /// <summary>
    /// Saving a SolidWorks document to 3DEXPERIENCE through the connector, shared by Save MCM and New from EBOM:
    /// save it under a given name with no Save to 3DEXPERIENCE window, wait for its id, add it to a bookmark,
    /// then check it in. Every step goes through Connector, so test mode can never reach the platform.
    /// </summary>
    internal sealed class PlatformSave
    {
        // How long to wait for 3DEXPERIENCE to report the saved document's id (saves can finish in the background).
        public const int SaveWaitSeconds = 90;

        private readonly ISldWorks _swApp;
        private readonly Connector _connector;
        private readonly string _logName;

        /// <summary>logName: the log in %TEMP%\BDAT, e.g. "save-mcm" (test runs log to "save-mcm-tests").</summary>
        public PlatformSave(ISldWorks swApp, Connector connector, string logName)
        {
            _swApp = swApp;
            _connector = connector;
            _logName = logName;
        }

        /// <summary>
        /// Saves doc as fileName (e.g. "BR-10101-AA.SLDPRT") in the work folder and uploads it with no window.
        /// If that can't be done it falls back to the connector's own Save to 3DEXPERIENCE window.
        /// </summary>
        public bool Save(IModelDoc2 doc, string fileName)
        {
            if (SaveWithoutDialog(doc, fileName)) return true;

            Log("falling back to SaveNoOption for " + fileName);
            object saver = _connector.Manager("Save");
            IntPtr unknown = Marshal.GetIUnknownForObject(doc);
            try
            {
                return (bool)_connector.Call(saver, "IEnoSwSave", "SaveNoOption", unknown, Path.GetFileNameWithoutExtension(fileName));
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        /// <summary>
        /// Saves the document as fileName in the 3DEXPERIENCE work folder, then has the connector upload that file
        /// with no Save to 3DEXPERIENCE window. Returns false, having changed nothing, if it can't get that far,
        /// so the caller can fall back to the connector's window. If the upload itself fails, it shows the
        /// connector's window for the same file instead.
        /// </summary>
        private bool SaveWithoutDialog(IModelDoc2 doc, string fileName)
        {
            string path;
            try
            {
                string workFolder = WorkFolder();
                if (string.IsNullOrEmpty(workFolder) || !Directory.Exists(workFolder)) { Log("no work folder: " + workFolder); return false; }

                path = Path.Combine(workFolder, fileName);
                bool alreadyThere = string.Equals(doc.GetPathName(), path, StringComparison.OrdinalIgnoreCase);
                if (!alreadyThere && File.Exists(path))
                {
                    // A leftover local copy, e.g. the part was deleted from 3DEXPERIENCE but not from the work folder.
                    // Only overwrite it if it isn't on the platform and isn't open in SolidWorks.
                    if (!string.IsNullOrEmpty(PhysicalId(path))) { Log("already on the platform: " + path); return false; }
                    if (_swApp.GetOpenDocumentByName(path) != null) { Log("work folder copy is open: " + path); return false; }
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
                if (UploadWithSaveApi(path)) { Log("saved without dialog: " + path); return true; }
            }
            catch (Exception ex)
            {
                Log("SaveAPI failed: " + ex.Message);
            }

            object saver = _connector.Manager("Save");
            Log("showing the connector window for " + path);
            return (bool)_connector.Call(saver, "IEnoSwSave5", "SaveFile", path, true);
        }

        /// <summary>
        /// The connector's scripting save (IEnoSwSaveAPI): Prepare then Commit, JSON in and out, no window.
        /// Input is {"version":"1.0","scope":[{"file":"path"}]}; each reply has "status":"OK" or "ERROR" and "errInfo".
        /// </summary>
        private bool UploadWithSaveApi(string path)
        {
            object api = _connector.Manager("SaveAPI");
            string file = path.Replace("\\", "\\\\").Replace("\"", "\\\"");

            string prepared = _connector.Call(api, "IEnoSwSaveAPI", "Prepare",
                "{\"version\":\"1.0\",\"scope\":[{\"file\":\"" + file + "\"}]}") as string;
            Log("SaveAPI Prepare: " + prepared);
            if (prepared == null || prepared.IndexOf("\"OK\"", StringComparison.Ordinal) < 0) return false;

            string committed = _connector.Call(api, "IEnoSwSaveAPI", "Commit", "{\"version\":\"1.0\"}") as string;
            Log("SaveAPI Commit: " + committed);
            return committed != null && committed.IndexOf("\"OK\"", StringComparison.Ordinal) >= 0;
        }

        /// <summary>The work-folder path of fileName if a document by that name is already on the platform, otherwise null.</summary>
        public string ExistingOnPlatform(IModelDoc2 doc, string fileName)
        {
            try
            {
                string workFolder = WorkFolder();
                if (string.IsNullOrEmpty(workFolder)) return null;
                string path = Path.Combine(workFolder, fileName);
                if (string.Equals(doc.GetPathName(), path, StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(path) && !string.IsNullOrEmpty(PhysicalId(path)) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        public string PhysicalId(string path)
        {
            try
            {
                return _connector.Call(_connector.Manager("FileCache"), "IEnoSwFileCache", "GetFilePhysicalId", path) as string;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The saved document's 3DEXPERIENCE id, waiting up to SaveWaitSeconds for it. Null if it never shows up.</summary>
        public string WaitForPhysicalId(IModelDoc2 doc)
        {
            DateTime giveUp = DateTime.Now.AddSeconds(SaveWaitSeconds);
            while (true)
            {
                string path = doc.GetPathName();
                string phid = string.IsNullOrEmpty(path) ? null : PhysicalId(path);
                if (string.IsNullOrEmpty(phid)) phid = doc.Extension.GetPLMID();
                if (!string.IsNullOrEmpty(phid)) return phid;
                if (DateTime.Now > giveUp) return null;
                Application.DoEvents();
                Thread.Sleep(1000);
            }
        }

        /// <summary>Adds the document to the bookmark. Returns the connector's reply (JSON), or null if it threw.</summary>
        public string AddToBookmark(string bookmarkId, string physicalId)
        {
            try
            {
                object authoring = _connector.Manager("Authoring");
                object result = _connector.Call(authoring, "IEnoSwAuthoring", "AddToBookmark", bookmarkId, new[] { physicalId });
                Log("AddToBookmark(" + bookmarkId + ", " + physicalId + ") returned " + result);
                return result == null ? null : result.ToString();
            }
            catch (Exception ex)
            {
                Log("AddToBookmark(" + bookmarkId + ", " + physicalId + ") failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// True if an AddToBookmark reply says the document is in the bookmark, e.g.
        /// {"status":"success","objectsAdded":1,...}. False for an error reply, e.g. a bookmark that was deleted.
        /// </summary>
        public static bool AddedToBookmark(string reply)
        {
            return reply != null && reply.Replace(" ", "").IndexOf("\"status\":\"success\"", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Check in: releases your lock on the saved file. True if it's unlocked afterwards (or was never locked).</summary>
        public bool Unlock(string path)
        {
            if (string.IsNullOrEmpty(path)) { Log("check-in: the document has no file path"); return false; }
            try
            {
                object cache = _connector.Manager("FileCache");
                object before = _connector.Call(cache, "IEnoSwFileCache7", "GetLockStatus", path);
                Log("check-in: lock status before " + before);
                if (before != null && before.ToString() == "notLocked") { Log("check-in: already unlocked"); return true; }

                object commands = _connector.Manager("UiCommands");
                bool ok = (bool)_connector.Call(commands, "IEnoSwUiCommands", "UnreserveFiles", (object)new[] { path });
                object after = _connector.Call(cache, "IEnoSwFileCache7", "GetLockStatus", path);
                Log("check-in: UnreserveFiles returned " + ok + ", lock status after " + after);
                return ok && (after == null || after.ToString() != "lockedByMe");
            }
            catch (Exception ex)
            {
                Log("check-in failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Shows the connector's bookmark picker. Null if cancelled.</summary>
        public static Bookmark ChooseBookmark(Connector connector, IWin32Window owner, string dialogTitle)
        {
            object chooser = connector.Manager("BookmarkChooser");
            connector.Set(chooser, "IEnoSwBookmarkChooser", "DialogTitle", dialogTitle);
            connector.Set(chooser, "IEnoSwBookmarkChooser", "ShowCancelButton", true);
            connector.Set(chooser, "IEnoSwBookmarkChooser", "ShowSelectButton", true);
            connector.Call(chooser, "IEnoSwBookmarkChooser2", "ShowDialog2", owner.Handle);

            var picked = new Bookmark
            {
                Id = connector.Get(chooser, "IEnoSwBookmarkChooser", "SelectedBookmarkId") as string,
                Title = connector.Get(chooser, "IEnoSwBookmarkChooser", "SelectedBookmarkTitle") as string,
            };
            if (string.IsNullOrEmpty(picked.Id)) return null;
            if (picked.Title == null) picked.Title = "";
            return picked;
        }

        private string WorkFolder()
        {
            return _connector.Call(_connector.Manager("Open"), "IEnoSwOpen3", "GetWorkFolder") as string;
        }

        /// <summary>Appends a line to %TEMP%\BDAT\(log name).log, so a failed save can be diagnosed afterwards.</summary>
        public void Log(string message)
        {
            Log(_logName, message);
        }

        public static void Log(string logName, string message)
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "BDAT");
                Directory.CreateDirectory(dir);
                // Test runs get their own log so they never show up as a real save.
                File.AppendAllText(Path.Combine(dir, logName + (TestMode.Enabled ? "-tests.log" : ".log")),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + System.Environment.NewLine);
            }
            catch
            {
                // Logging must never break the save.
            }
        }
    }
}
