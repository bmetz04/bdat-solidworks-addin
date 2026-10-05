using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT.Commands
{
    /// <summary>
    /// Names the open part's cut list items 001, 002, 003... in the order they appear in the cut list. Items that
    /// already have a number keep it; the rest get the numbers after the highest one in use. A part with no cut list
    /// is made a weldment first, so SolidWorks makes one.
    /// SolidWorks doesn't put renames made by an add-in on its undo list (Ctrl+Z skips them and undoes earlier
    /// features instead), so nothing here is grouped for undo and the message doesn't offer it.
    /// </summary>
    public sealed class NameCutListCommand : IBdatCommand
    {
        public string Title { get { return "Name Cut List"; } }

        public string Hint { get { return "Number the cut list items 001, 002, 003... that don't have a number yet (makes the part a weldment if it has no cut list)"; } }

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

            bool madeWeldment = false;
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
            Result result = Rename(doc, items);

            if (TestMode.Enabled) TestMode.LastNameCutList = result.Names;

            string message;
            if (result.Names.Count == 0 && result.Failed.Count == 0)
                message = "All " + result.Kept + " cut list items already have numbers, so nothing was renamed.";
            else if (result.Names.Count == 0)
                message = "No cut list items were renamed.";
            else
            {
                message = result.Names.Count == 1
                    ? "Named a cut list item " + result.Names[0] + "."
                    : "Named " + result.Names.Count + " cut list items " + result.Names[0] + " to " + result.Names[result.Names.Count - 1] + ".";
                if (result.Kept > 0)
                    message += "\n" + result.Kept + (result.Kept == 1 ? " item already had a number and was" : " items already had numbers and were") + " left as is.";
            }
            if (madeWeldment) message = "Made the part a weldment so it has a cut list.\n" + message;
            if (result.Failed.Count > 0)
                message += "\n\nSolidWorks wouldn't use " + string.Join(", ", result.Failed.ToArray()) +
                    ". Another feature in the part probably already has that name.";
            Ui.Tell(swApp, message, result.Failed.Count > 0 ? swMessageBoxIcon_e.swMbWarning : swMessageBoxIcon_e.swMbInformation);
        }

        private sealed class Result
        {
            public readonly List<string> Names = new List<string>();
            public readonly List<string> Failed = new List<string>();
            public int Kept;
        }

        /// <summary>
        /// Items already named with a number (001, 002, ... or longer, digits only) keep their names. The rest get
        /// the numbers after the highest one in use, in cut list order, so new items go on the end of the list.
        /// </summary>
        private static Result Rename(IModelDoc2 doc, List<IFeature> items)
        {
            var result = new Result();
            int highest = 0;
            var toName = new List<IFeature>();
            foreach (IFeature item in items)
            {
                int number;
                if (IsNumbered(item.Name, out number))
                {
                    result.Kept++;
                    highest = Math.Max(highest, number);
                }
                else toName.Add(item);
            }

            int next = highest + 1;
            foreach (IFeature item in toName)
            {
                string name = next.ToString("000");
                next++;
                item.Name = name;
                if (item.Name == name) result.Names.Add(name);
                else result.Failed.Add(name);
            }

            if (toName.Count > 0)
            {
                try { doc.FeatureManager.UpdateFeatureTree(); } catch { }
            }
            return result;
        }

        /// <summary>True for a name in the 001, 002, ... format: three or more digits and nothing else.</summary>
        internal static bool IsNumbered(string name, out int number)
        {
            number = 0;
            if (name == null || name.Length < 3 || name.Length > 9) return false;
            foreach (char c in name)
                if (c < '0' || c > '9') return false;
            number = int.Parse(name);
            return true;
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
    }
}
