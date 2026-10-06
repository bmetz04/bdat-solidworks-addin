using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT.Commands
{
    /// <summary>
    /// Frees cut list numbers that are taken by something with no bodies in it, so they can be used again: other
    /// features named like a cut list number (a sketch called 005, say) and cut list items left empty after their
    /// bodies were deleted. Each gets a name that isn't a number ("Sketch 005", "Empty 005"). Cut list items that
    /// still hold bodies keep their numbers.
    /// </summary>
    public sealed class FreeCutListNumbersCommand : IBdatCommand
    {
        public string Title { get { return "Free Cut List Numbers"; } }

        public string Hint { get { return "Free cut list numbers (001, 002...) held by features or empty cut list items with no bodies"; } }

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

            var claims = new List<IFeature>();
            foreach (IFeature item in NameCutListCommand.CutListItems(doc))
            {
                int n;
                if (NameCutListCommand.IsNumbered(item.Name, out n) && BodyCount(item) == 0) claims.Add(item);
            }
            foreach (IFeature feat in NameCutListCommand.OtherFeatures(doc))
            {
                int n;
                if (NameCutListCommand.IsNumbered(feat.Name, out n)) claims.Add(feat);
            }

            HashSet<string> names = NameCutListCommand.OtherFeatureNames(doc);
            foreach (IFeature item in NameCutListCommand.CutListItems(doc)) names.Add(item.Name);

            var freed = new List<string>();
            var failed = new List<string>();
            foreach (IFeature feat in claims)
            {
                string number = feat.Name;
                string to = FreeName(feat, number, names);
                feat.Name = to;
                if (feat.Name == to)
                {
                    names.Add(to);
                    freed.Add(number + " (now \"" + to + "\")");
                }
                else failed.Add(number);
            }
            if (claims.Count > 0)
            {
                try { doc.FeatureManager.UpdateFeatureTree(); } catch { }
                NameCutListCommand.SortByNumber(doc);
            }
            NameCutListCommand.Log("freed " + string.Join(", ", freed.ToArray()) + (failed.Count > 0 ? "; couldn't free " + string.Join(", ", failed.ToArray()) : ""));
            if (TestMode.Enabled) TestMode.LastNameCutList = freed;

            string message = freed.Count == 0 && failed.Count == 0
                ? "No numbers to free: every cut list number belongs to a cut list item with bodies in it."
                : "";
            if (freed.Count > 0)
                message = "Freed " + (freed.Count == 1 ? "this number" : "these numbers") + ":\n    " + string.Join("\n    ", freed.ToArray());
            if (failed.Count > 0)
                message += (message.Length > 0 ? "\n\n" : "") + "SolidWorks wouldn't rename what holds " + string.Join(", ", failed.ToArray()) + ".";
            Ui.Tell(swApp, message, failed.Count > 0 ? swMessageBoxIcon_e.swMbWarning : swMessageBoxIcon_e.swMbInformation);
        }

        /// <summary>A name that says what the feature is and keeps the old number in it, e.g. "Sketch 005".</summary>
        private static string FreeName(IFeature feat, string number, HashSet<string> names)
        {
            string type = feat.GetTypeName2() ?? "";
            string kind;
            switch (type)
            {
                case "CutListFolder": kind = "Empty"; break;
                case "ProfileFeature": kind = "Sketch"; break;
                case "3DProfileFeature": kind = "3D Sketch"; break;
                case "RefPlane": kind = "Plane"; break;
                case "RefAxis": kind = "Axis"; break;
                case "RefPoint": kind = "Point"; break;
                case "CoordSys": kind = "Coordinate System"; break;
                case "WeldmentFeature": kind = "Weldment"; break;
                default: kind = "Feature"; break;
            }
            string name = kind + " " + number;
            for (int i = 2; names.Contains(name); i++) name = kind + " " + number + " (" + i + ")";
            return name;
        }

        private static int BodyCount(IFeature item)
        {
            try
            {
                IBodyFolder folder = item.GetSpecificFeature2() as IBodyFolder;
                return folder == null ? 1 : folder.GetBodyCount();
            }
            catch
            {
                return 1; // Can't tell: leave it alone.
            }
        }
    }
}
