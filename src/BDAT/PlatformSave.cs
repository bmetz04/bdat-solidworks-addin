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

        /// <summary>Adds the saved document to a bookmark. Null if it worked, otherwise 3DEXPERIENCE's reason.</summary>
        public string AddToBookmark(string bookmarkId, string physicalId)
        {
            object authoring = _connector.Manager("Authoring");
            object result = _connector.Call(authoring, "IEnoSwAuthoring", "AddToBookmark", bookmarkId, new[] { physicalId });
            Log("AddToBookmark(" + bookmarkId + ", " + physicalId + ") returned " + result);
            return BookmarkError(result as string);
        }

        /// <summary>
        /// Reads AddToBookmark's reply, e.g. {"status":"success",...} or {"status":"failure","error":"You do not have
        /// security context to change the content of this Bookmark Folder."}. Null for success, otherwise the error.
        /// </summary>
        internal static string BookmarkError(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return "3DEXPERIENCE didn't answer.";
            if (JsonString(reply, "status") == "success") return null;
            string error = JsonString(reply, "error");
            return string.IsNullOrEmpty(error) ? reply : error;
        }

        // Just enough JSON for the connector's flat replies: the string value of "name", or null.
        private static string JsonString(string json, string name)
        {
            string key = "\"" + name + "\"";
            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;
            int colon = json.IndexOf(':', at + key.Length);
            int open = colon < 0 ? -1 : json.IndexOf('"', colon + 1);
            if (open < 0) return null;
            var sb = new System.Text.StringBuilder();
            for (int i = open + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); continue; }
                if (c == '"') return sb.ToString();
                sb.Append(c);
            }
            return null;
        }

        /// <summary>What to tell people when AddToBookmark is refused.</summary>
        internal static string BookmarkAdvice(string error)
        {
            string advice = "\n\nAdd it by hand: find it in 3DEXPERIENCE, right-click it > Add to Bookmark.";
            if (error != null && error.IndexOf("security context", StringComparison.OrdinalIgnoreCase) >= 0)
                advice = "\n\nThat usually means your 3DEXPERIENCE security context (collaborative space and role, shown at the top " +
                    "of the 3DEXPERIENCE task pane) isn't the team's one, so it was also saved in that space. Switch to the team's " +
                    "collaborative space before saving." + advice;
            return advice;
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

        /// <summary>Keeps the saved file checked out (reserved) to you. True if it's reserved by you afterwards.</summary>
        public bool Reserve(string path)
        {
            if (string.IsNullOrEmpty(path)) { Log("reserve: the document has no file path"); return false; }
            try
            {
                object cache = _connector.Manager("FileCache");
                object before = _connector.Call(cache, "IEnoSwFileCache7", "GetLockStatus", path);
                Log("reserve: lock status before " + before);
                if (before != null && before.ToString() == "lockedByMe") return true;

                object commands = _connector.Manager("UiCommands");
                bool ok = (bool)_connector.Call(commands, "IEnoSwUiCommands", "ReserveFiles", (object)new[] { path });
                object after = _connector.Call(cache, "IEnoSwFileCache7", "GetLockStatus", path);
                Log("reserve: ReserveFiles returned " + ok + ", lock status after " + after);
                return ok && after != null && after.ToString() == "lockedByMe";
            }
            catch (Exception ex)
            {
                Log("reserve failed: " + ex.Message);
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
            // Read what was selected before the dialog: closing it can leave that (or the folder you were browsing)
            // as SelectedBookmarkId, which must not count as a pick.
            string before = connector.Get(chooser, "IEnoSwBookmarkChooser", "SelectedBookmarkId") as string;
            object shown = connector.Call(chooser, "IEnoSwBookmarkChooser2", "ShowDialog2", owner.Handle);
            Log("bookmark-chooser", "\"" + dialogTitle + "\" returned " + (shown == null ? "null" : shown.GetType().Name + " " + shown) +
                ", selected before = " + (before ?? "none"));
            if (ChooserCancelled(shown)) return null;

            var picked = new Bookmark
            {
                Id = connector.Get(chooser, "IEnoSwBookmarkChooser", "SelectedBookmarkId") as string,
                Title = connector.Get(chooser, "IEnoSwBookmarkChooser", "SelectedBookmarkTitle") as string,
            };
            if (string.IsNullOrEmpty(picked.Id)) return null;
            // Same as before the dialog and the dialog didn't say Select: treat it as closed, not picked.
            if (picked.Id == before && !ChooserSelected(shown)) return null;
            if (picked.Title == null) picked.Title = "";
            return picked;
        }

        /// <summary>True when ShowDialog2's result says the chooser was cancelled or closed (false, or Cancel / Abort / No).</summary>
        private static bool ChooserCancelled(object shown)
        {
            if (shown is bool) return !(bool)shown;
            if (shown is DialogResult) return (DialogResult)shown != DialogResult.OK && (DialogResult)shown != DialogResult.Yes;
            if (shown is int || shown is Enum)
            {
                int code = Convert.ToInt32(shown);
                return code == (int)DialogResult.Cancel || code == (int)DialogResult.Abort || code == (int)DialogResult.No;
            }
            return false;
        }

        /// <summary>True when ShowDialog2's result clearly says Select was clicked (true, or OK / Yes).</summary>
        private static bool ChooserSelected(object shown)
        {
            if (shown is bool) return (bool)shown;
            if (shown is int || shown is Enum)
            {
                int code = Convert.ToInt32(shown);
                return code == (int)DialogResult.OK || code == (int)DialogResult.Yes;
            }
            return false;
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
