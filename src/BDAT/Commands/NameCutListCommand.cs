using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT.Commands
{
    /// <summary>
    /// Names the open part's cut list items 001, 002, 003... Items that already have a number keep it; the rest get
    /// the numbers after the highest one in use, sheet metal items first and then the others, each in cut list order.
    /// A part with no cut list is made a weldment first, so SolidWorks makes one. SolidWorks can't undo renames, so it
    /// asks before changing anything and shows exactly which names will change. Afterwards the cut list is sorted by
    /// number.
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

            // SolidWorks keeps feature renames off its undo list, so Ctrl+Z can't take them back (it undoes whatever
            // came before instead). Everything is shown and confirmed before it's changed.
            bool madeWeldment = false;
            List<IFeature> items = CutListItems(doc);
            if (items.Count == 0)
            {
                if (!HasFeature(doc, "WeldmentFeature") && !Ui.AskYesNo(swApp,
                        "This part has no cut list. BDAT will add a Weldment feature so SolidWorks makes one, " +
                        "then number the cut list items 001, 002, 003...\n\nGo ahead?"))
                    return;
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

            int kept;
            List<Rename> plan = Plan(items, out kept);
            if (plan.Count > 0 && !Ui.AskYesNo(swApp, ConfirmText(plan, kept, madeWeldment)))
            {
                if (madeWeldment)
                    Ui.Tell(swApp, "Nothing was renamed. The Weldment feature BDAT added is still in the tree; " +
                        "delete it if you don't want it.", swMessageBoxIcon_e.swMbInformation);
                return;
            }
            Result result = Apply(doc, plan);
            result.Kept = kept;
            bool sorted = SortByNumber(doc);

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
            if (result.Names.Count > 0)
                message += "\n\nCtrl+Z can't undo a rename in SolidWorks. To change a name back, rename it in the cut list.";
            message += sorted
                ? "\n\nThe cut list is sorted by number."
                : "\n\nSolidWorks wouldn't reorder the cut list, so it's still in its old order.";
            Ui.Tell(swApp, message, result.Failed.Count > 0 ? swMessageBoxIcon_e.swMbWarning : swMessageBoxIcon_e.swMbInformation);
        }

        private sealed class Result
        {
            public readonly List<string> Names = new List<string>();
            public readonly List<string> Failed = new List<string>();
            public int Kept;
        }

        private sealed class Rename
        {
            public IFeature Item;
            public string From;
            public string To;
        }

        /// <summary>
        /// Items already named with a number (001, 002, ... or longer, digits only) keep their names. The rest get
        /// the numbers after the highest one in use, sheet metal items first and then the rest, each in cut list order.
        /// </summary>
        private static List<Rename> Plan(List<IFeature> items, out int kept)
        {
            kept = 0;
            int highest = 0;
            var toName = new List<IFeature>();
            foreach (IFeature item in items)
            {
                int number;
                if (IsNumbered(item.Name, out number))
                {
                    kept++;
                    highest = Math.Max(highest, number);
                }
                else toName.Add(item);
            }

            // Sheet metal items take the first new numbers (Ben, 2026-10-06), then everything else, each in cut list order.
            var ordered = new List<IFeature>();
            foreach (IFeature item in toName) if (IsSheetMetal(item)) ordered.Add(item);
            foreach (IFeature item in toName) if (!ordered.Contains(item)) ordered.Add(item);

            var plan = new List<Rename>();
            int next = highest + 1;
            foreach (IFeature item in ordered)
                plan.Add(new Rename { Item = item, From = item.Name, To = (next++).ToString("000") });
            return plan;
        }

        /// <summary>True when the cut list item holds sheet metal bodies.</summary>
        private static bool IsSheetMetal(IFeature item)
        {
            try
            {
                IBodyFolder folder = item.GetSpecificFeature2() as IBodyFolder;
                object[] bodies = folder == null ? null : folder.GetBodies() as object[];
                if (bodies == null) return false;
                foreach (object o in bodies)
                {
                    IBody2 body = o as IBody2;
                    if (body != null && body.IsSheetMetal()) return true;
                }
            }
            catch
            {
                // Can't tell: number it with the rest.
            }
            return false;
        }

        private static string ConfirmText(List<Rename> plan, int kept, bool madeWeldment)
        {
            const int shown = 15;
            var lines = new List<string>();
            if (madeWeldment) lines.Add("BDAT made the part a weldment, so it now has a cut list.\n");
            lines.Add("Rename " + (plan.Count == 1 ? "this cut list item" : "these " + plan.Count + " cut list items") + "?\n");
            for (int i = 0; i < plan.Count && i < shown; i++)
                lines.Add("    " + plan[i].From + "  →  " + plan[i].To);
            if (plan.Count > shown) lines.Add("    ... and " + (plan.Count - shown) + " more");
            if (kept > 0) lines.Add("\n" + kept + (kept == 1 ? " item already has a number and stays" : " items already have numbers and stay") + " as is.");
            lines.Add("\nCtrl+Z can't undo a rename in SolidWorks, so check the list first.");
            return string.Join("\n", lines.ToArray());
        }

        private static Result Apply(IModelDoc2 doc, List<Rename> plan)
        {
            var result = new Result();
            foreach (Rename r in plan)
            {
                r.Item.Name = r.To;
                if (r.Item.Name == r.To) result.Names.Add(r.To);
                else result.Failed.Add(r.To);
            }
            if (plan.Count > 0)
            {
                try { doc.FeatureManager.UpdateFeatureTree(); } catch { }
            }
            return result;
        }

        /// <summary>
        /// Puts the cut list items in each folder (the cut list and any sub-weldment folders) in number order, with
        /// unnumbered items after them in their current order. SolidWorks has no sort-by-name option for cut lists,
        /// so the items are moved one at a time. True if every folder ended up in order.
        /// </summary>
        private static bool SortByNumber(IModelDoc2 doc)
        {
            bool ok = true;
            IFeature feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (string.Equals(feat.GetTypeName2(), "SolidBodyFolder", StringComparison.OrdinalIgnoreCase))
                    ok &= SortFolder(doc, feat);
                feat = feat.GetNextFeature() as IFeature;
            }
            try { doc.FeatureManager.UpdateFeatureTree(); } catch { }
            return ok;
        }

        private static bool SortFolder(IModelDoc2 doc, IFeature folder)
        {
            bool ok = true;
            var items = new List<IFeature>();
            IFeature sub = folder.GetFirstSubFeature() as IFeature;
            while (sub != null)
            {
                string type = sub.GetTypeName2() ?? "";
                if (type.Equals("CutListFolder", StringComparison.OrdinalIgnoreCase)) items.Add(sub);
                else if (type.Equals("SubWeldFolder", StringComparison.OrdinalIgnoreCase)) ok &= SortFolder(doc, sub);
                sub = sub.GetNextSubFeature() as IFeature;
            }
            if (items.Count < 2) return ok;

            List<string> wanted = SortedNames(items);
            List<string> current = ItemNames(folder);
            Log(folder.Name + ": order " + string.Join(", ", current.ToArray()) + ", wanted " + string.Join(", ", wanted.ToArray()));
            if (SameOrder(current, wanted)) return ok;

            // Last wanted item first: put each one before whatever is first now, so the list builds up from the top.
            // Only ReorderFeature2 moves cut list items; ReorderFeature returns False, or True without moving them.
            try
            {
                for (int i = wanted.Count - 1; i >= 0; i--)
                {
                    string first = ItemNames(folder)[0];
                    if (first == wanted[i]) continue;
                    Log("move " + wanted[i] + " before " + first + ": " + Reorder2(doc, wanted[i], first, (int)swMoveLocation_e.swMoveBefore));
                }
            }
            catch (Exception ex)
            {
                Log("moving failed: " + ex.Message);
            }
            current = ItemNames(folder);
            Log("after: " + string.Join(", ", current.ToArray()));
            return ok && SameOrder(current, wanted);
        }

        /// <summary>
        /// IModelDocExtension.ReorderFeature2, called late-bound so BDAT still builds against API libraries that
        /// don't have it.
        /// </summary>
        private static bool Reorder2(IModelDoc2 doc, string move, string target, int location)
        {
            object extension = doc.Extension;
            object result = extension.GetType().InvokeMember("ReorderFeature2", System.Reflection.BindingFlags.InvokeMethod,
                null, extension, new object[] { move, target, location });
            return result is bool && (bool)result;
        }

        private static List<string> ItemNames(IFeature folder)
        {
            var names = new List<string>();
            IFeature sub = folder.GetFirstSubFeature() as IFeature;
            while (sub != null)
            {
                if (string.Equals(sub.GetTypeName2(), "CutListFolder", StringComparison.OrdinalIgnoreCase)) names.Add(sub.Name);
                sub = sub.GetNextSubFeature() as IFeature;
            }
            return names;
        }

        private static bool SameOrder(List<string> a, List<string> b)
        {
            return string.Join("|", a.ToArray()) == string.Join("|", b.ToArray());
        }

        private static void Log(string message)
        {
            PlatformSave.Log("namecutlist", message);
        }

        /// <summary>Numbered names in number order, then the rest in the order given.</summary>
        internal static List<string> SortedNames(List<IFeature> items)
        {
            return SortedNames(items.ConvertAll(f => f.Name));
        }

        internal static List<string> SortedNames(List<string> names)
        {
            var numbered = new List<KeyValuePair<int, string>>();
            var rest = new List<string>();
            foreach (string name in names)
            {
                int n;
                if (IsNumbered(name, out n)) numbered.Add(new KeyValuePair<int, string>(n, name));
                else rest.Add(name);
            }
            // Stable sort by number (List.Sort isn't stable, so break ties on the original position).
            var indexed = new List<KeyValuePair<int, KeyValuePair<int, string>>>();
            for (int i = 0; i < numbered.Count; i++) indexed.Add(new KeyValuePair<int, KeyValuePair<int, string>>(i, numbered[i]));
            indexed.Sort((a, b) => a.Value.Key != b.Value.Key ? a.Value.Key.CompareTo(b.Value.Key) : a.Key.CompareTo(b.Key));
            var result = indexed.ConvertAll(x => x.Value.Value);
            result.AddRange(rest);
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
