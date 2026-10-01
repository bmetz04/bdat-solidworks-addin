using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace BDAT.Commands
{
    /// <summary>
    /// Murder Part: strip every thread from the active part, export it to Parasolid,
    /// then open the Parasolid as a new dumb-solid part.
    ///
    /// Steps:
    ///   1. Delete every feature folder with "thread" in its name (e.g. McMaster's "Threads" folder)
    ///      together with everything inside it, plus any cosmetic threads and Thread features elsewhere.
    ///   2. Export to a temporary .x_t (in %TEMP%\BDAT, never next to the part).
    ///   3. Reload the original part from disk so the .SLDPRT keeps its threads.
    ///   4. Open the .x_t as a new unsaved part, combine its bodies into one if needed,
    ///      then delete the temporary .x_t.
    /// </summary>
    public sealed class MurderPartCommand : IBdatCommand
    {
        public string Title { get { return "Murder Part"; } }

        public string Hint { get { return "Remove all threads, export to Parasolid and reopen it as a single dumb solid"; } }

        private const string ExportSuffix = "_murdered";
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
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocPART)
            {
                Tell(swApp, "Open a part first.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            // The reload in step 3 restores the part from disk, so it has to be saved there.
            string partPath = doc.GetPathName();
            if (string.IsNullOrEmpty(partPath))
            {
                Tell(swApp, "Save the part first. Murder Part reloads the original from disk afterwards so it keeps its threads.",
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }

            if (doc.GetSaveFlag())
            {
                int answer = swApp.SendMsgToUser2(
                    "This part has unsaved changes. Save them and continue?",
                    (int)swMessageBoxIcon_e.swMbQuestion, (int)swMessageBoxBtn_e.swMbOkCancel);
                if (answer != (int)swMessageBoxResult_e.swMbHitOk) return;

                int saveErr = 0, saveWarn = 0;
                doc.Save3((int)swSaveAsOptions_e.swSaveAsOptions_Silent, ref saveErr, ref saveWarn);
                if (doc.GetSaveFlag())
                {
                    Tell(swApp, "The part wasn't saved, so nothing was changed.", swMessageBoxIcon_e.swMbWarning);
                    return;
                }
            }

            // 1. Delete threads.
            ThreadScan scan = FindThreads(doc);
            int failedDeletes = DeleteFeatures(doc, scan.FeatureNames);
            DeleteFolders(doc, scan.FolderNames);
            doc.ForceRebuild3(false);

            // 2. Export to a temporary Parasolid, out of the part's folder.
            CleanTempDir();
            string xtPath = Path.Combine(TempDir, Path.GetFileNameWithoutExtension(partPath) + ExportSuffix + ".x_t");

            int errors = 0, warnings = 0;
            bool exported = doc.Extension.SaveAs3(
                xtPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy),
                null, null, ref errors, ref warnings);

            // 3. Put the original back the way it is on disk (threads and all).
            int reloadResult = doc.ReloadOrReplace(false, partPath, true);
            bool originalRestored = reloadResult == (int)swComponentReloadError_e.swReloadOkay;

            if (!exported || !File.Exists(xtPath))
            {
                Tell(swApp,
                    "Parasolid export failed (error code " + errors + ").\n\n" + RestoreNote(originalRestored),
                    swMessageBoxIcon_e.swMbStop);
                return;
            }

            // 4. Open the Parasolid as a new part, then throw the temporary file away.
            int loadErrors = 0;
            object importData = swApp.GetImportFileData(xtPath);
            IModelDoc2 newDoc = swApp.LoadFile4(xtPath, "r", importData, ref loadErrors) as IModelDoc2;
            TryDelete(xtPath);
            if (newDoc == null)
            {
                Tell(swApp,
                    "SolidWorks couldn't open the exported Parasolid (error code " + loadErrors + ").\n\n" + RestoreNote(originalRestored),
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }

            string bodyNote = MergeBodies(newDoc);

            var summary = new StringBuilder();
            if (scan.FolderNames.Count > 0)
                summary.AppendLine("Deleted the " + string.Join(", ", scan.FolderNames.ToArray()) + " folder(s) and everything in them.");
            summary.AppendLine("Removed " + scan.FeatureNames.Count + " thread feature(s) in total.");
            if (failedDeletes > 0)
                summary.AppendLine(failedDeletes + " feature(s) couldn't be deleted, so some thread geometry may remain.");
            if (scan.FeatureNames.Count == 0)
                summary.AppendLine("No threads were found, so the part was exported as it is.");
            summary.AppendLine();
            summary.AppendLine("Opened the result as a new part. " + bodyNote);
            summary.AppendLine("It isn't saved anywhere yet; use Save As to keep it.");
            summary.AppendLine();
            summary.Append(RestoreNote(originalRestored));
            Tell(swApp, summary.ToString(), swMessageBoxIcon_e.swMbInformation);
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

        /// <summary>Combines multiple imported solid bodies into one when they touch.</summary>
        private static string MergeBodies(IModelDoc2 newDoc)
        {
            IPartDoc part = newDoc as IPartDoc;
            object[] bodies = part == null ? null : part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            int count = bodies == null ? 0 : bodies.Length;

            if (count <= 1) return "It is a single solid body.";

            try
            {
                IFeature combine = newDoc.FeatureManager.InsertCombineFeature(
                    (int)swBodyOperationType_e.SWBODYADD, null, bodies) as IFeature;
                newDoc.ForceRebuild3(false);
                object[] after = part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                int afterCount = after == null ? 0 : after.Length;
                if (combine != null && afterCount == 1)
                    return "It came in as " + count + " bodies, which were combined into one.";
                return "It came in as " + count + " bodies. They don't all touch, so " + afterCount + " separate bodies remain.";
            }
            catch
            {
                return "It came in as " + count + " bodies and combining them failed, so they were left separate.";
            }
        }

        /// <summary>Clears leftovers from earlier runs (e.g. a file SolidWorks still had locked last time).</summary>
        private static void CleanTempDir()
        {
            try
            {
                foreach (string file in Directory.GetFiles(TempDir, "*.x_t"))
                    TryDelete(file);
            }
            catch
            {
                // Not worth failing the command over.
            }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* cleaned up on the next run */ }
        }

        private static string RestoreNote(bool originalRestored)
        {
            return originalRestored
                ? "The original part was reloaded from disk, so it still has its threads."
                : "Couldn't reload the original part. It is still open with threads removed, so close it without saving to keep its threads.";
        }

        private static void Tell(ISldWorks swApp, string message, swMessageBoxIcon_e icon)
        {
            swApp.SendMsgToUser2(message, (int)icon, (int)swMessageBoxBtn_e.swMbOk);
        }
    }
}
