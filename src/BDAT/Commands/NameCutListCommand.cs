using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT.Commands
{
    /// <summary>
    /// Names the open part's cut list items 001, 002, 003... in the order they appear in the cut list, replacing
    /// whatever names they had. A part with no cut list is made a weldment first, so SolidWorks makes one.
    /// Everything it changes is one undo step where SolidWorks allows it.
    /// </summary>
    public sealed class NameCutListCommand : IBdatCommand
    {
        public string Title { get { return "Name Cut List"; } }

        public string Hint { get { return "Name the cut list items 001, 002, 003... (makes the part a weldment if it has no cut list)"; } }

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
                Ui.Tell(swApp, "Open a part first.", swMessageBoxIcon_e.swMbInformation);
                return;
            }

            object[] bodies = ((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (bodies == null || bodies.Length == 0)
            {
                Ui.Tell(swApp, "This part has no solid bodies, so there's no cut list to name.", swMessageBoxIcon_e.swMbInformation);
                return;
            }

            bool recording = StartUndo(doc);
            bool madeWeldment = false;
            Result result;
            try
            {
                List<IFeature> items = CutListItems(doc);
                if (items.Count == 0)
                {
                    madeWeldment = MakeWeldment(doc);
                    items = CutListItems(doc);
                }
                if (items.Count == 0)
                {
                    Ui.Tell(swApp, madeWeldment
                        ? "BDAT made this part a weldment, but SolidWorks didn't make a cut list for it. Nothing was renamed."
                        : HasFeature(doc, "WeldmentFeature")
                            ? "This part is a weldment, but SolidWorks has no cut list items for it. Nothing was renamed."
                            : "This part has no cut list and BDAT couldn't make it a weldment. Nothing was renamed.",
                        swMessageBoxIcon_e.swMbWarning);
                    return;
                }
                result = Rename(doc, items);
            }
            finally
            {
                if (recording) recording = FinishUndo(doc);
            }

            if (TestMode.Enabled) TestMode.LastNameCutList = result.Names;

            string message = result.Names.Count == 1
                ? "Named the cut list item " + result.Names[0] + "."
                : "Named " + result.Names.Count + " cut list items " + result.Names[0] + " to " + result.Names[result.Names.Count - 1] + ".";
            if (madeWeldment) message = "Made the part a weldment so it has a cut list.\n" + message;
            if (result.Failed.Count > 0)
                message += "\n\nSolidWorks wouldn't rename " + string.Join(", ", result.Failed.ToArray()) +
                    ". Another feature in the part probably already has that name.";
            if (recording) message += "\n\nUndo (Ctrl+Z) puts everything back.";
            Ui.Tell(swApp, message, result.Failed.Count > 0 ? swMessageBoxIcon_e.swMbWarning : swMessageBoxIcon_e.swMbInformation);
        }

        private sealed class Result
        {
            public readonly List<string> Names = new List<string>();
            public readonly List<string> Failed = new List<string>();
        }

        /// <summary>
        /// Renames the items in two passes, first to temporary names and then to 001, 002..., so an item already
        /// called "002" doesn't block another from taking that name.
        /// </summary>
        private static Result Rename(IModelDoc2 doc, List<IFeature> items)
        {
            string tag = "BDAT-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-";
            for (int i = 0; i < items.Count; i++)
                items[i].Name = tag + i;

            var result = new Result();
            for (int i = 0; i < items.Count; i++)
            {
                string name = (i + 1).ToString("000");
                items[i].Name = name;
                if (items[i].Name == name) result.Names.Add(name);
                else result.Failed.Add(name);
            }

            // Anything that couldn't take its number keeps a name rather than the temporary one.
            for (int i = 0; i < items.Count; i++)
            {
                if (!items[i].Name.StartsWith(tag, StringComparison.Ordinal)) continue;
                items[i].Name = "Cut-List-Item" + (i + 1);
            }

            try { doc.FeatureManager.UpdateFeatureTree(); } catch { }
            return result;
        }

        /// <summary>Adds a Weldment feature, which turns the Solid Bodies folder into a cut list. True if it worked.</summary>
        private static bool MakeWeldment(IModelDoc2 doc)
        {
            if (HasFeature(doc, "WeldmentFeature")) return false;
            IFeature weldment = doc.FeatureManager.InsertWeldmentFeature() as IFeature;
            if (weldment == null) return false;
            doc.ForceRebuild3(false);
            return true;
        }

        /// <summary>
        /// The cut list items (including those in sub-weldment folders) in the order the cut list shows them.
        /// The cut list is brought up to date first, and turned on if the part has it switched off.
        /// </summary>
        private static List<IFeature> CutListItems(IModelDoc2 doc)
        {
            var items = new List<IFeature>();
            IFeature feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (string.Equals(feat.GetTypeName2(), "SolidBodyFolder", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateCutList(doc, feat);
                    Collect(feat, items);
                }
                feat = feat.GetNextFeature() as IFeature;
            }
            return items;
        }

        private static void UpdateCutList(IModelDoc2 doc, IFeature solidBodies)
        {
            IBodyFolder folder = solidBodies.GetSpecificFeature2() as IBodyFolder;
            if (folder == null) return;
            try
            {
                // Only a weldment or sheet metal part has a cut list to switch on.
                if (!HasSubFeature(solidBodies, "CutListFolder") && (HasFeature(doc, "WeldmentFeature") || HasFeature(doc, "SheetMetal")))
                    folder.SetAutomaticCutList(true);
                folder.UpdateCutList();
            }
            catch
            {
                // An out-of-date cut list still has items to name.
            }
        }

        private static void Collect(IFeature folder, List<IFeature> items)
        {
            IFeature sub = folder.GetFirstSubFeature() as IFeature;
            while (sub != null)
            {
                string type = sub.GetTypeName2() ?? "";
                if (type.Equals("CutListFolder", StringComparison.OrdinalIgnoreCase)) items.Add(sub);
                else if (type.Equals("SubWeldFolder", StringComparison.OrdinalIgnoreCase)) Collect(sub, items);
                sub = sub.GetNextSubFeature() as IFeature;
            }
        }

        private static bool HasSubFeature(IFeature folder, string type)
        {
            IFeature sub = folder.GetFirstSubFeature() as IFeature;
            while (sub != null)
            {
                if (string.Equals(sub.GetTypeName2(), type, StringComparison.OrdinalIgnoreCase)) return true;
                sub = sub.GetNextSubFeature() as IFeature;
            }
            return false;
        }

        private static bool HasFeature(IModelDoc2 doc, string type)
        {
            IFeature feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (string.Equals(feat.GetTypeName2(), type, StringComparison.OrdinalIgnoreCase)) return true;
                feat = feat.GetNextFeature() as IFeature;
            }
            return false;
        }

        private static bool StartUndo(IModelDoc2 doc)
        {
            try
            {
                doc.Extension.StartRecordingUndoObject();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool FinishUndo(IModelDoc2 doc)
        {
            try
            {
                return doc.Extension.FinishRecordingUndoObject2("Name Cut List", false);
            }
            catch
            {
                return false;
            }
        }
    }
}
