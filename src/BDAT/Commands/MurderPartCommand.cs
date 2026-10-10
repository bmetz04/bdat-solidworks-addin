using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace BDAT.Commands
{
    /// <summary>
    /// Murder Part: make a thread-free Parasolid version of the active part and open it as a new part.
    ///
    /// The part the user has open is never modified: all the deleting happens on a hidden temporary copy.
    ///
    /// Steps:
    ///   1. Ask the user to confirm, showing which part and what will be removed.
    ///   2. Save a copy of the part as it is right now (including unsaved changes) to %TEMP%\BDAT\murder
    ///      and open that copy invisibly.
    ///   3. On the copy, delete every feature folder with "thread" in its name (e.g. McMaster's "Threads" folder)
    ///      with everything inside it, plus any cosmetic threads and Thread features elsewhere.
    ///   4. Export the copy to a temporary .x_t, then close the copy without saving.
    ///   5. Open the .x_t as a new unsaved part (multi-body parts stay multi-body), then delete the temporary files.
    /// </summary>
    public sealed class MurderPartCommand : IBdatCommand
    {
        public string Title { get { return "Murder Part"; } }

        public string Hint { get { return "Make a thread-free copy of this part (the original isn't changed)"; } }

        private const string ExportSuffix = "_murdered";
        private const string WorkCopySuffix = "_bdat_workcopy";
        private const string FolderTypeName = "FtrFolder";
        private const string FolderEndTag = "___EndTag___";

        private static string TempDir
        {
            get
            {
                string dir = Path.Combine(Path.GetTempPath(), "BDAT", "murder");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public bool IsEnabled(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            return doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocPART;
        }

        public void Run(ISldWorks swApp)
        {
            try
            {
                RunLogged(swApp);
            }
            catch (Exception ex)
            {
                // BDAT shows the message; the log keeps the details so a teammate's report can be diagnosed.
                Log("FAILED: " + ex);
                throw;
            }
        }

        private void RunLogged(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                Tell(swApp, "Open a part first.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            string baseName = BaseName(doc);
            Log("---- " + baseName + "  (BDAT " + BuildInfo.Version + ", SolidWorks " + swApp.RevisionNumber() + ")");
            Log("part file: " + doc.GetPathName());

            // 1. Confirm. Scanning is read-only, so the original is still untouched at this point.
            ThreadScan preview = FindThreads(doc);
            Log("found " + preview.FolderNames.Count + " thread folder(s), " + preview.FeatureNames.Count + " thread feature(s)");
            if (!Confirm(swApp, doc, preview))
            {
                Log("cancelled");
                return;
            }

            CleanTempDir();
            // A unique stamp in the temp names so a copy or result still open from an earlier run can't clash.
            string stamp = DateTime.Now.ToString("HHmmss");
            string workPath = Path.Combine(TempDir, baseName + WorkCopySuffix + stamp + ".SLDPRT");
            string xtPath = Path.Combine(TempDir, baseName + ExportSuffix + ".x_t");
            if (IsOpen(swApp, baseName + ExportSuffix))
                xtPath = Path.Combine(TempDir, baseName + ExportSuffix + "_" + stamp + ".x_t");

            // 2. Copy the part as it is now (the Copy option leaves the open document, its file and its
            //    saved/unsaved state exactly as they were), then open the copy where nobody can see or save it.
            int errors = 0, warnings = 0;
            bool copied = doc.Extension.SaveAs3(
                workPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy),
                null, null, ref errors, ref warnings);
            Log("working copy saved " + copied + ", exists " + File.Exists(workPath) + ", errors " + errors + ", warnings " + warnings + " -> " + workPath);
            if (!copied || !File.Exists(workPath))
            {
                Tell(swApp, "Couldn't make a working copy of the part (error code " + errors + "). Nothing was changed.",
                    swMessageBoxIcon_e.swMbStop);
                return;
            }

            ThreadScan scan;
            int failedDeletes;
            bool exported = false;
            swApp.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);
            IModelDoc2 work = null;
            try
            {
                int openErr = 0, openWarn = 0;
                work = swApp.OpenDoc6(workPath, (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref openErr, ref openWarn) as IModelDoc2;
                Log("working copy opened " + (work != null) + ", errors " + openErr + ", warnings " + openWarn);
                if (work == null)
                {
                    Tell(swApp, "Couldn't open the working copy (error code " + openErr + "). Nothing was changed.",
                        swMessageBoxIcon_e.swMbStop);
                    return;
                }

                // 3. Delete threads, on the copy only.
                scan = FindThreads(work);
                Log("copy has " + scan.FolderNames.Count + " thread folder(s) [" + string.Join(", ", scan.FolderNames.ToArray())
                    + "], " + scan.FeatureNames.Count + " thread feature(s) [" + string.Join(", ", scan.FeatureNames.ToArray()) + "]");
                failedDeletes = DeleteFeatures(work, scan.FeatureNames);
                DeleteFolders(work, scan.FolderNames);
                bool rebuilt = work.ForceRebuild3(false);
                Log("deleted, " + failedDeletes + " couldn't be deleted, rebuild " + rebuilt);

                // 4. Export the copy to Parasolid.
                exported = work.Extension.SaveAs3(
                    xtPath,
                    (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                    (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy),
                    null, null, ref errors, ref warnings);
                Log("Parasolid export " + exported + ", exists " + File.Exists(xtPath) + ", errors " + errors + ", warnings " + warnings + " -> " + xtPath);
            }
            finally
            {
                // Close the copy without saving, whatever happened above, and always show new windows again
                // (otherwise every part opened afterwards would stay invisible).
                if (work != null)
                {
                    try { swApp.CloseDoc(work.GetTitle()); }
                    catch (Exception ex) { Log("closing the working copy failed: " + ex.Message); }
                }
                swApp.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART);
                TryDelete(workPath);
            }

            if (!exported || !File.Exists(xtPath))
            {
                Tell(swApp, "Parasolid export failed (error code " + errors + "). Your part wasn't changed.",
                    swMessageBoxIcon_e.swMbStop);
                return;
            }

            // 5. Open the Parasolid as a new part, then throw the temporary file away.
            int loadErrors;
            IModelDoc2 newDoc = OpenParasolid(swApp, xtPath, out loadErrors);
            if (newDoc == null)
            {
                // Keep the file so it can still be opened by hand.
                Tell(swApp, "SolidWorks couldn't open the exported Parasolid (error code " + loadErrors + "). Your part wasn't changed.\n\n" +
                    "The thread-free file is here if you want to open it yourself with File > Open:\n" + xtPath,
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }
            TryDelete(xtPath);

            var summary = new StringBuilder();
            if (scan.FolderNames.Count > 0)
                summary.AppendLine("Deleted the " + string.Join(", ", scan.FolderNames.ToArray()) + " folder(s) and everything in them.");
            summary.AppendLine("Removed " + scan.FeatureNames.Count + " thread feature(s) in total.");
            if (failedDeletes > 0)
                summary.AppendLine(failedDeletes + " feature(s) couldn't be deleted, so some thread geometry may remain.");
            if (scan.FeatureNames.Count == 0)
                summary.AppendLine("No threads were found, so the part was exported as it is.");
            summary.AppendLine();
            summary.AppendLine("Opened the result as a new part.");
            summary.AppendLine("It isn't saved anywhere yet; use Save As to keep it.");
            summary.AppendLine();
            summary.Append("Your original part \"" + baseName + "\" was not changed.");
            Log("done");
            Tell(swApp, summary.ToString(), swMessageBoxIcon_e.swMbInformation);
        }

        private static bool Confirm(ISldWorks swApp, IModelDoc2 doc, ThreadScan preview)
        {
            var msg = new StringBuilder();
            msg.AppendLine("Murder \"" + BaseName(doc) + "\"?");
            msg.AppendLine();
            msg.AppendLine("This opens a NEW part: a thread-free Parasolid version of this one.");
            msg.AppendLine("Your original part and its file are not changed.");
            msg.AppendLine();
            if (preview.FolderNames.Count > 0)
                msg.AppendLine("Will remove the " + string.Join(", ", preview.FolderNames.ToArray()) + " folder(s) and everything in them.");
            if (preview.FeatureNames.Count > 0)
                msg.AppendLine("Thread features found: " + preview.FeatureNames.Count + ".");
            else
                msg.AppendLine("No threads were found, so it would just be converted to a dumb solid.");

            return Ui.AskYesNo(swApp, msg.ToString());
        }

        /// <summary>The part's file name without extension, or its window title if it has never been saved.</summary>
        private static string BaseName(IModelDoc2 doc)
        {
            string path = doc.GetPathName();
            string name = string.IsNullOrEmpty(path) ? doc.GetTitle() : Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 7);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private sealed class ThreadScan
        {
            public readonly List<string> FeatureNames = new List<string>();
            public readonly List<string> FolderNames = new List<string>();
        }

        /// <summary>
        /// Collects, in feature-tree order, every feature inside a thread folder plus every cosmetic
        /// thread or Thread feature anywhere in the tree (including sub-features, where cosmetic threads live).
        /// </summary>
        private static ThreadScan FindThreads(IModelDoc2 doc)
        {
            var scan = new ThreadScan();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IFeature feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (IsThreadFolder(feat))
                {
                    scan.FolderNames.Add(feat.Name);
                    CollectFolder(feat, scan, seen);
                }
                else
                {
                    Visit(feat, scan, seen);
                }
                feat = feat.GetNextFeature() as IFeature;
            }
            return scan;
        }

        private static bool IsThreadFolder(IFeature feat)
        {
            string name = feat.Name ?? "";
            return string.Equals(feat.GetTypeName2(), FolderTypeName, StringComparison.OrdinalIgnoreCase)
                && name.IndexOf(FolderEndTag, StringComparison.Ordinal) < 0
                && name.IndexOf("thread", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void CollectFolder(IFeature folderFeat, ThreadScan scan, HashSet<string> seen)
        {
            IFeatureFolder folder = folderFeat.GetSpecificFeature2() as IFeatureFolder;
            object[] contents = folder == null ? null : folder.GetFeatures() as object[];
            if (contents == null) return;

            foreach (object item in contents)
            {
                IFeature inner = item as IFeature;
                if (inner == null) continue;
                if (string.Equals(inner.GetTypeName2(), FolderTypeName, StringComparison.OrdinalIgnoreCase))
                {
                    CollectFolder(inner, scan, seen); // nested folder: take its contents too
                    if (!scan.FolderNames.Contains(inner.Name)) scan.FolderNames.Add(inner.Name);
                }
                else if (seen.Add(inner.Name))
                {
                    scan.FeatureNames.Add(inner.Name);
                }
            }
        }

        private static void Visit(IFeature feat, ThreadScan scan, HashSet<string> seen)
        {
            if (IsThreadFeature(feat) && seen.Add(feat.Name))
                scan.FeatureNames.Add(feat.Name);

            IFeature sub = feat.GetFirstSubFeature() as IFeature;
            while (sub != null)
            {
                Visit(sub, scan, seen);
                sub = sub.GetNextSubFeature() as IFeature;
            }
        }

        private static bool IsThreadFeature(IFeature feat)
        {
            string typeName = feat.GetTypeName2() ?? "";
            if (typeName.Equals("CosmeticThread", StringComparison.OrdinalIgnoreCase)
                || typeName.Equals("Thread", StringComparison.OrdinalIgnoreCase))
                return true;

            // Fall back to the feature definition in case the type name differs between versions.
            object definition = null;
            try { definition = feat.GetDefinition(); } catch { /* some features have no definition */ }
            return definition is ICosmeticThreadFeatureData || definition is IThreadFeatureData;
        }

        /// <summary>
        /// Deletes features newest-first so children go before their parents. Features are looked up by
        /// name each time because deleting one can take others with it. Returns how many couldn't be deleted.
        /// </summary>
        private static int DeleteFeatures(IModelDoc2 doc, List<string> names)
        {
            IPartDoc part = (IPartDoc)doc;
            int failed = 0;
            for (int i = names.Count - 1; i >= 0; i--)
            {
                IFeature feat = part.FeatureByName(names[i]) as IFeature;
                if (feat == null) continue; // already removed along with something else

                bool deleted = DeleteOne(doc, feat, (int)swDeleteSelectionOptions_e.swDelete_Absorbed);
                if (!deleted)
                {
                    // Something outside the folder depends on it (e.g. a chamfer on a thread edge): take that too.
                    feat = part.FeatureByName(names[i]) as IFeature;
                    deleted = feat == null || DeleteOne(doc, feat,
                        (int)(swDeleteSelectionOptions_e.swDelete_Absorbed | swDeleteSelectionOptions_e.swDelete_Children));
                }
                if (!deleted) failed++;
            }
            doc.ClearSelection2(true);
            return failed;
        }

        /// <summary>Removes the (now empty) thread folders themselves, innermost first.</summary>
        private static void DeleteFolders(IModelDoc2 doc, List<string> folderNames)
        {
            IPartDoc part = (IPartDoc)doc;
            for (int i = folderNames.Count - 1; i >= 0; i--)
            {
                IFeature folder = part.FeatureByName(folderNames[i]) as IFeature;
                if (folder != null) DeleteOne(doc, folder, 0);
            }
            doc.ClearSelection2(true);
        }

        private static bool DeleteOne(IModelDoc2 doc, IFeature feat, int options)
        {
            doc.ClearSelection2(true);
            return feat.Select2(false, -1) && doc.Extension.DeleteSelection2(options);
        }

        /// <summary>Clears leftovers from earlier runs (e.g. a file SolidWorks still had locked last time).</summary>
        private static void CleanTempDir()
        {
            try
            {
                foreach (string file in Directory.GetFiles(TempDir))
                    TryDelete(file);
            }
            catch
            {
                // Not worth failing the command over.
            }
        }

        /// <summary>
        /// Imports the Parasolid as a new part. Importing makes the part from the default part template without asking, so
        /// while it runs this makes sure SolidWorks won't stop to ask for a template and, if the default part template is
        /// missing (common on PCs that only use 3DX templates), points it at SolidWorks' own part template. Both settings are
        /// put back afterwards. Tries LoadFile4 first, then OpenDoc6. Null if neither works.
        /// </summary>
        private static IModelDoc2 OpenParasolid(ISldWorks swApp, string xtPath, out int loadErrors)
        {
            int templatePref = (int)swUserPreferenceStringValue_e.swDefaultTemplatePart;
            int alwaysDefaultPref = (int)swUserPreferenceToggle_e.swAlwaysUseDefaultTemplates;
            string template = swApp.GetUserPreferenceStringValue(templatePref);
            bool alwaysDefault = swApp.GetUserPreferenceToggle(alwaysDefaultPref);
            bool templateOk = !string.IsNullOrEmpty(template) && File.Exists(template);
            Log("default part template \"" + template + "\" exists " + templateOk + ", always use default templates " + alwaysDefault);

            string fallback = templateOk ? null : FindSolidWorksPartTemplate(swApp);
            if (fallback != null)
            {
                Log("using " + fallback + " for the import");
                swApp.SetUserPreferenceStringValue(templatePref, fallback);
            }
            if (!alwaysDefault) swApp.SetUserPreferenceToggle(alwaysDefaultPref, true);

            IModelDoc2 newDoc = null;
            loadErrors = 0;
            try
            {
                object importData = swApp.GetImportFileData(xtPath);
                newDoc = swApp.LoadFile4(xtPath, "r", importData, ref loadErrors) as IModelDoc2;
                Log("LoadFile4 opened " + (newDoc != null) + ", errors " + loadErrors);
                if (newDoc == null)
                {
                    int openErr = 0, openWarn = 0;
                    newDoc = swApp.OpenDoc6(xtPath, (int)swDocumentTypes_e.swDocPART,
                        (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref openErr, ref openWarn) as IModelDoc2;
                    Log("OpenDoc6 opened " + (newDoc != null) + ", errors " + openErr + ", warnings " + openWarn);
                }
            }
            finally
            {
                if (fallback != null) swApp.SetUserPreferenceStringValue(templatePref, template ?? "");
                if (!alwaysDefault) swApp.SetUserPreferenceToggle(alwaysDefaultPref, false);
            }
            return newDoc;
        }

        /// <summary>The Part template that ships with SolidWorks, or null if none is found.</summary>
        private static string FindSolidWorksPartTemplate(ISldWorks swApp)
        {
            var candidates = new List<string>();
            try
            {
                // Folders listed in Tools > Options > File Locations > Document Templates.
                string folders = swApp.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swFileLocationsDocumentTemplates) ?? "";
                foreach (string folder in folders.Split(';'))
                {
                    if (folder.Trim().Length == 0 || !Directory.Exists(folder.Trim())) continue;
                    candidates.AddRange(Directory.GetFiles(folder.Trim(), "*.prtdot"));
                }
                string exe = swApp.GetExecutablePath();
                // Usually the install folder, but allow for the path of sldworks.exe itself.
                string exeDir = string.IsNullOrEmpty(exe) ? null
                    : exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(exe) : exe;
                if (!string.IsNullOrEmpty(exeDir))
                {
                    candidates.Add(Path.Combine(exeDir, @"lang\english\Tutorial\part.prtdot"));
                    candidates.Add(Path.Combine(exeDir, @"lang\english\Tutorial\Part_MM.prtdot"));
                }
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string[] versionDirs = Directory.Exists(Path.Combine(programData, "SOLIDWORKS"))
                    ? Directory.GetDirectories(Path.Combine(programData, "SOLIDWORKS"), "SOLIDWORKS*") : new string[0];
                foreach (string dir in versionDirs)
                    candidates.Add(Path.Combine(dir, @"templates\Part.prtdot"));
            }
            catch (Exception ex)
            {
                Log("looking for a part template failed: " + ex.Message);
            }
            foreach (string c in candidates)
                if (File.Exists(c)) return c;
            Log("no part template found in " + candidates.Count + " place(s)");
            return null;
        }

        /// <summary>True if a document with this title (with or without extension) is already open.</summary>
        private static bool IsOpen(ISldWorks swApp, string title)
        {
            try
            {
                IModelDoc2 open = swApp.GetFirstDocument() as IModelDoc2;
                while (open != null)
                {
                    string t = open.GetTitle() ?? "";
                    if (t.Equals(title, StringComparison.OrdinalIgnoreCase)
                        || t.StartsWith(title + ".", StringComparison.OrdinalIgnoreCase))
                        return true;
                    open = open.GetNext() as IModelDoc2;
                }
            }
            catch (Exception)
            {
                // Fall back to the usual name.
            }
            return false;
        }

        /// <summary>Appends a line to %TEMP%\BDAT\murder-part.log.</summary>
        private static void Log(string message)
        {
            PlatformSave.Log("murder-part", message);
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* cleaned up on the next run */ }
        }


        private static void Tell(ISldWorks swApp, string message, swMessageBoxIcon_e icon)
        {
            Ui.Tell(swApp, message, icon);
        }
    }
}
