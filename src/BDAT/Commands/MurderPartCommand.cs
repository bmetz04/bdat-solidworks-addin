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
    ///   1. Delete all cosmetic threads and modeled Thread features (modeled threads become plain cylinders).
    ///   2. Save a copy as &lt;PartName&gt;_murdered.x_t in the same folder as the part.
    ///   3. Reload the original part from disk so the .SLDPRT keeps its threads.
    ///   4. Open the .x_t as a new part and, if it came in as several bodies, try to combine them into one.
    /// </summary>
    public sealed class MurderPartCommand : IBdatCommand
    {
        public string Title { get { return "Murder Part"; } }

        public string Hint { get { return "Remove all threads, export to Parasolid and reopen it as a single dumb solid"; } }

        private const string ExportSuffix = "_murdered";

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

            // We need a folder to put the .x_t in, and the reload in step 3 discards unsaved work.
            string partPath = doc.GetPathName();
            if (string.IsNullOrEmpty(partPath))
            {
                Tell(swApp, "Save the part to a folder first so Murder Part knows where to put the Parasolid.",
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
            int cosmeticCount, modeledCount;
            List<IFeature> threads = FindThreadFeatures(doc, out cosmeticCount, out modeledCount);
            int failedDeletes = DeleteFeatures(doc, threads);
            doc.ForceRebuild3(false);

            // 2. Export to Parasolid next to the original.
            string xtPath = Path.Combine(
                Path.GetDirectoryName(partPath),
                Path.GetFileNameWithoutExtension(partPath) + ExportSuffix + ".x_t");

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

            // 4. Open the Parasolid as a new part.
            int loadErrors = 0;
            object importData = swApp.GetImportFileData(xtPath);
            IModelDoc2 newDoc = swApp.LoadFile4(xtPath, "r", importData, ref loadErrors) as IModelDoc2;
            if (newDoc == null)
            {
                Tell(swApp,
                    "Exported " + xtPath + "\nbut SolidWorks couldn't open it (error code " + loadErrors + ").\n\n" + RestoreNote(originalRestored),
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }

            string bodyNote = MergeBodies(newDoc);

            var summary = new StringBuilder();
            summary.AppendLine("Removed " + cosmeticCount + " cosmetic thread(s) and " + modeledCount + " modeled thread feature(s).");
            if (failedDeletes > 0)
                summary.AppendLine(failedDeletes + " thread(s) couldn't be deleted and may still be in the export.");
            summary.AppendLine();
            summary.AppendLine("Exported to:");
            summary.AppendLine(xtPath);
            summary.AppendLine();
            summary.AppendLine("Opened it as a new part (not saved yet). " + bodyNote);
            summary.AppendLine();
            summary.Append(RestoreNote(originalRestored));
            Tell(swApp, summary.ToString(), swMessageBoxIcon_e.swMbInformation);
        }

        /// <summary>Walks the whole feature tree, including sub-features (where cosmetic threads live).</summary>
        private static List<IFeature> FindThreadFeatures(IModelDoc2 doc, out int cosmeticCount, out int modeledCount)
        {
            var found = new List<IFeature>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            cosmeticCount = 0;
            modeledCount = 0;

            IFeature feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                Visit(feat, found, seen, ref cosmeticCount, ref modeledCount);
                feat = feat.GetNextFeature() as IFeature;
            }
            return found;
        }

        private static void Visit(IFeature feat, List<IFeature> found, HashSet<string> seen,
            ref int cosmeticCount, ref int modeledCount)
        {
            ThreadKind kind = Classify(feat);
            if (kind != ThreadKind.None && seen.Add(feat.Name))
            {
                found.Add(feat);
                if (kind == ThreadKind.Cosmetic) cosmeticCount++;
                else modeledCount++;
            }

            IFeature sub = feat.GetFirstSubFeature() as IFeature;
            while (sub != null)
            {
                Visit(sub, found, seen, ref cosmeticCount, ref modeledCount);
                sub = sub.GetNextSubFeature() as IFeature;
            }
        }

        private enum ThreadKind { None, Cosmetic, Modeled }

        private static ThreadKind Classify(IFeature feat)
        {
            string typeName = feat.GetTypeName2() ?? "";
            if (typeName.Equals("CosmeticThread", StringComparison.OrdinalIgnoreCase))
                return ThreadKind.Cosmetic;
            if (typeName.Equals("Thread", StringComparison.OrdinalIgnoreCase))
                return ThreadKind.Modeled;

            // Fall back to the feature definition in case the type name differs between versions.
            object definition = null;
            try { definition = feat.GetDefinition(); } catch { /* some features have no definition */ }
            if (definition is ICosmeticThreadFeatureData) return ThreadKind.Cosmetic;
            if (definition is IThreadFeatureData) return ThreadKind.Modeled;
            return ThreadKind.None;
        }

        /// <summary>Deletes each feature on its own so one failure doesn't stop the rest. Returns the failure count.</summary>
        private static int DeleteFeatures(IModelDoc2 doc, List<IFeature> features)
        {
            int failed = 0;
            foreach (IFeature feat in features)
            {
                doc.ClearSelection2(true);
                bool selected = feat.Select2(false, -1);
                bool deleted = selected && doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
                if (!deleted) failed++;
            }
            doc.ClearSelection2(true);
            return failed;
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
