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
            Result result = Name(doc, items, out kept);
            bool sorted = SortByNumber(doc);
            Log("named " + string.Join(", ", result.Names.ToArray()) + (result.Skipped.Count > 0 ? "; skipped " + string.Join(", ", result.Skipped.ToArray()) : ""));

            if (TestMode.Enabled) TestMode.LastNameCutList = result.Names;

            // No pop-up when it all worked (Ben, 2026-10-06): the cut list shows the new names.
            var problems = new List<string>();
            if (result.Failed.Count > 0)
                problems.Add("SolidWorks wouldn't rename " + string.Join(", ", result.Failed.ToArray()) + ", so " +
                    (result.Failed.Count == 1 ? "it keeps its" : "they keep their") + " old name.");
            if (!sorted)
                problems.Add("SolidWorks wouldn't reorder the cut list, so it's still in its old order.");
            if (problems.Count > 0)
                Ui.Tell(swApp, string.Join("\n\n", problems.ToArray()), swMessageBoxIcon_e.swMbWarning);
        }

        internal sealed class Result
        {
            public readonly List<string> Names = new List<string>();
            public readonly List<string> Skipped = new List<string>();
            public readonly List<string> Failed = new List<string>();
        }

        /// <summary>
        /// Items already named with a number (001, 002, ... or longer, digits only) keep their names. The rest get
        /// the numbers after the highest one in use, sheet metal items first and then the rest, each in cut list order.
        /// A number another feature already has (a sketch called 005, say) is skipped, and so is any number SolidWorks
        /// won't take for some other reason.
        /// </summary>
        private static Result Name(IModelDoc2 doc, List<IFeature> items, out int kept)
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

            var result = new Result();
            HashSet<string> taken = OtherFeatureNames(doc);
            int next = highest + 1;
            foreach (IFeature item in SheetMetalFirst(toName))
            {
                string from = item.Name;
                for (int tries = 0; tries < 50; tries++)
                {
                    string to = (next++).ToString("000");
                    if (taken.Contains(to))
                    {
                        result.Skipped.Add(to);
                        continue;
                    }
                    item.Name = to;
                    if (item.Name == to)
                    {
                        result.Names.Add(to);
                        break;
                    }
                    result.Skipped.Add(to);
                }
                if (item.Name == from) result.Failed.Add(from);
            }
            if (toName.Count > 0)
            {
                try { doc.FeatureManager.UpdateFeatureTree(); } catch { }
            }
            return result;
        }

        /// <summary>Sheet metal items first (Ben, 2026-10-06), then everything else, each in the order given.</summary>
        internal static List<IFeature> SheetMetalFirst(List<IFeature> items)
        {
            var ordered = new List<IFeature>();
            foreach (IFeature item in items) if (IsSheetMetal(item)) ordered.Add(item);
            foreach (IFeature item in items) if (!ordered.Contains(item)) ordered.Add(item);
            return ordered;
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

        /// <summary>The names of every feature in the part except the cut list items, nested ones included.</summary>
        internal static HashSet<string> OtherFeatureNames(IModelDoc2 doc)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IFeature feat in OtherFeatures(doc)) names.Add(feat.Name);
            return names;
        }

        /// <summary>Every feature in the part except the cut list items, nested ones (sketches under extrudes...) included.</summary>
        internal static List<IFeature> OtherFeatures(IModelDoc2 doc)
        {
            var found = new List<IFeature>();
            IFeature feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                AddOther(feat, found, 0);
                feat = feat.GetNextFeature() as IFeature;
            }
            return found;
        }

        private static void AddOther(IFeature feat, List<IFeature> found, int depth)
        {
            if (!string.Equals(feat.GetTypeName2(), "CutListFolder", StringComparison.OrdinalIgnoreCase)) found.Add(feat);
            if (depth > 8) return;
            IFeature sub = feat.GetFirstSubFeature() as IFeature;
            while (sub != null)
            {
                AddOther(sub, found, depth + 1);
                sub = sub.GetNextSubFeature() as IFeature;
            }
        }

        /// <summary>
        /// Puts the cut list items in each folder (the cut list and any sub-weldment folders) in number order, with
        /// unnumbered items after them in their current order. SolidWorks has no sort-by-name option for cut lists,
        /// so the items are moved one at a time. True if every folder ended up in order.
        /// </summary>
        internal static bool SortByNumber(IModelDoc2 doc)
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

        internal static void Log(string message)
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
        internal static List<IFeature> CutListItems(IModelDoc2 doc)
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

        internal static bool HasFeature(IModelDoc2 doc, string type)
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
