using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT.Commands
{
    /// <summary>
    /// Waterjet DXF: one DXF per body that can be cut on the waterjet, in a folder named after the part.
    ///
    /// The part the user has open is never modified: the bodies are only looked at, and the exporting happens on a
    /// hidden temporary copy. Nothing goes to 3DEXPERIENCE; the DXFs are plain files in a folder the user picks.
    ///
    /// Which bodies can be waterjet:
    ///   - sheet metal bodies: exported as their flat pattern (outline and holes, no bend lines),
    ///   - solid plates: bodies with two parallel flat faces (top and bottom) and every other face straight through
    ///     between them, so the cut is the same all the way down. The top face is exported.
    /// Anything else (chamfers, fillets on the faces, pockets, steps, threads, ...) is skipped and listed, with why.
    ///
    /// Steps:
    ///   1. Sort the open part's visible solid bodies into the two kinds above, or skipped.
    ///   2. Pop-up: tick the bodies to export and pick the folder (remembered for next time).
    ///   3. Save a copy of the part as it is right now to %TEMP%\BDAT\waterjet and open it invisibly.
    ///   4. On the copy, export each ticked body to "&lt;folder&gt;\&lt;part&gt;\&lt;part&gt; - &lt;name&gt;.dxf",
    ///      then close the copy without saving and delete it.
    /// Bodies are named after their cut list item when the part has cut lists (sheet metal and weldments always do),
    /// otherwise after the body. Identical bodies in one cut list item give one DXF, with the quantity shown.
    /// Each run is logged to %TEMP%\BDAT\waterjet.log.
    /// </summary>
    public sealed class WaterjetDxfCommand : IBdatCommand
    {
        public string Title { get { return "Waterjet DXF"; } }

        public string Hint { get { return "Save a DXF of every flat plate and sheet metal flat pattern in this part, for the waterjet"; } }

        private const string WorkCopySuffix = "_bdat_waterjet";
        private const string UserKeyPath = @"Software\BDAT";
        private const string FolderValue = "WaterjetFolder";

        /// <summary>Plates thicker than this start unticked: they're usually turned or machined parts that happen to be prismatic.</summary>
        internal const double ThickPlateMetres = 0.0254;

        // Sheet metal export options (ExportToDWG2's SheetMetalOptions bits): 1 = flat pattern geometry,
        // 16 = merge coplanar faces. No bend lines, sketches, hidden edges, forming tools or library features.
        private const int SheetMetalOptions = 1 | 16;

        // Angles are compared through dot products of unit vectors; heights in metres.
        private const double ParallelTolerance = 1e-6;   // 1 - |cos| for a face to count as top or bottom
        private const double PerpendicularTolerance = 1e-3; // |cos| for a face to count as straight through
        private const double HeightTolerance = 1e-5;     // 0.01 mm

        private static string TempDir
        {
            get
            {
                string dir = Path.Combine(Path.GetTempPath(), "BDAT", "waterjet");
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
                Ui.Tell(swApp, "Open a part first.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            string partName = BaseName(doc);

            // 1. Sort the bodies. Read-only.
            Log("---- " + partName);
            List<BodyPlan> plans = Classify(doc);
            foreach (BodyPlan p in plans) Log("body " + p.BodyName + " -> " + p.Label);
            List<BodyPlan> exportable = plans.FindAll(p => p.Kind != BodyKind.Skipped);
            if (exportable.Count == 0)
            {
                var none = new StringBuilder();
                none.AppendLine(plans.Count == 0
                    ? "\"" + partName + "\" has no solid bodies."
                    : "None of the bodies in \"" + partName + "\" can be waterjet:");
                AppendSkipped(none, plans);
                none.AppendLine();
                none.Append("BDAT exports sheet metal (as its flat pattern) and flat plates whose sides go straight through.");
                Ui.Tell(swApp, none.ToString().TrimEnd(), swMessageBoxIcon_e.swMbWarning);
                return;
            }

            // 2. Pick bodies and folder.
            string parentFolder;
            if (!Ask(swApp, partName, plans, out parentFolder)) return;
            List<BodyPlan> chosen = exportable.FindAll(p => p.Export);
            if (chosen.Count == 0) return;

            string outDir = Path.Combine(parentFolder, partName);
            try
            {
                Directory.CreateDirectory(outDir);
            }
            catch (Exception ex)
            {
                Ui.Tell(swApp, "Couldn't make the folder " + outDir + ":\n\n" + ex.Message, swMessageBoxIcon_e.swMbStop);
                return;
            }

            // 3. Copy the part as it is now (the Copy option leaves the open document and its file exactly as they were).
            CleanTempDir();
            string workPath = Path.Combine(TempDir, partName + WorkCopySuffix + ".SLDPRT");
            int errors = 0, warnings = 0;
            bool copied = doc.Extension.SaveAs3(
                workPath,
                (int)swSaveAsVersion_e.swSaveAsCurrentVersion,
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent | swSaveAsOptions_e.swSaveAsOptions_Copy),
                null, null, ref errors, ref warnings);
            if (!copied || !File.Exists(workPath))
            {
                Ui.Tell(swApp, "Couldn't make a working copy of the part (error code " + errors + "). No DXFs were made.",
                    swMessageBoxIcon_e.swMbStop);
                return;
            }

            var written = new List<string>();
            var failed = new List<string>();
            swApp.DocumentVisible(false, (int)swDocumentTypes_e.swDocPART);
            IModelDoc2 work = null;
            try
            {
                int openErr = 0, openWarn = 0;
                work = swApp.OpenDoc6(workPath, (int)swDocumentTypes_e.swDocPART,
                    (int)swOpenDocOptions_e.swOpenDocOptions_Silent, "", ref openErr, ref openWarn) as IModelDoc2;
                if (work == null)
                {
                    Ui.Tell(swApp, "Couldn't open the working copy (error code " + openErr + "). No DXFs were made.",
                        swMessageBoxIcon_e.swMbStop);
                    return;
                }

                // 4. Export, on the copy only.
                ExportAll(work, workPath, partName, outDir, chosen, written, failed);
            }
            finally
            {
                if (work != null) swApp.CloseDoc(work.GetTitle());
                swApp.DocumentVisible(true, (int)swDocumentTypes_e.swDocPART);
                TryDelete(workPath);
            }

            Report(swApp, partName, outDir, plans, written, failed);
        }

        #region Sorting the bodies

        internal enum BodyKind { SheetMetal, Plate, Skipped }

        internal sealed class BodyPlan
        {
            /// <summary>The cut list item's name, or the body's when it isn't in a cut list.</summary>
            public string Name;
            /// <summary>The body exported (the first one, when a cut list item holds several).</summary>
            public string BodyName;
            /// <summary>The cut list item, or null.</summary>
            public string CutList;
            /// <summary>How many bodies the cut list item holds.</summary>
            public int Count = 1;
            public BodyKind Kind;
            /// <summary>Plate thickness in metres (0 for sheet metal).</summary>
            public double Thickness;
            /// <summary>Why it was skipped.</summary>
            public string Reason;
            /// <summary>Ticked in the pop-up.</summary>
            public bool Export;

            /// <summary>The line shown in the pop-up and the summary.</summary>
            public string Label
            {
                get
                {
                    string name = Count > 1 ? Name + " x" + Count : Name;
                    if (Kind == BodyKind.SheetMetal) return name + "  (sheet metal, flat pattern)";
                    if (Kind == BodyKind.Plate) return name + "  (" + Millimetres(Thickness) + " plate)";
                    return name + ": " + Reason;
                }
            }
        }

        private static List<BodyPlan> Classify(IModelDoc2 doc)
        {
            var plans = new List<BodyPlan>();
            Dictionary<string, string> cutLists = CutListNames(doc);
            foreach (IBody2 body in SolidBodies(doc))
            {
                string cutList;
                cutLists.TryGetValue(body.Name, out cutList);

                // A second body in the same cut list item is a copy of the first: count it instead of exporting it again.
                BodyPlan same = cutList == null ? null : plans.Find(p => p.CutList == cutList);
                if (same != null)
                {
                    same.Count++;
                    continue;
                }

                var plan = new BodyPlan { Name = cutList ?? body.Name, BodyName = body.Name, CutList = cutList };
                if (body.IsSheetMetal())
                {
                    plan.Kind = BodyKind.SheetMetal;
                    plan.Export = true;
                }
                else
                {
                    PlateCheck check = CheckPlate(body);
                    if (check.Reason == null)
                    {
                        plan.Kind = BodyKind.Plate;
                        plan.Thickness = check.Thickness;
                        plan.Export = check.Thickness <= ThickPlateMetres + HeightTolerance;
                    }
                    else
                    {
                        plan.Kind = BodyKind.Skipped;
                        plan.Reason = check.Reason;
                    }
                }
                plans.Add(plan);
            }
            return plans;
        }

        /// <summary>
        /// Body name to cut list item name, for every body in a cut list. Cut list items live in folders under the
        /// Solid Bodies folder (and sub-weldment folders), so this walks folders only.
        /// </summary>
        private static Dictionary<string, string> CutListNames(IModelDoc2 doc)
        {
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                IFeature feat = doc.FirstFeature() as IFeature;
                while (feat != null)
                {
                    CollectCutLists(feat, names);
                    feat = feat.GetNextFeature() as IFeature;
                }
            }
            catch (Exception ex)
            {
                Log("couldn't read the cut lists: " + ex.Message);
            }
            return names;
        }

        private static void CollectCutLists(IFeature feat, Dictionary<string, string> names)
        {
            string type = feat.GetTypeName2() ?? "";
            if (type.Equals("CutListFolder", StringComparison.OrdinalIgnoreCase))
            {
                IBodyFolder folder = feat.GetSpecificFeature2() as IBodyFolder;
                object[] bodies = folder == null ? null : folder.GetBodies() as object[];
                if (bodies != null)
                    foreach (object o in bodies)
                    {
                        IBody2 body = o as IBody2;
                        if (body != null && !names.ContainsKey(body.Name)) names.Add(body.Name, feat.Name);
                    }
            }
            if (!type.EndsWith("Folder", StringComparison.OrdinalIgnoreCase)) return;

            IFeature sub = feat.GetFirstSubFeature() as IFeature;
            while (sub != null)
            {
                CollectCutLists(sub, names);
                sub = sub.GetNextSubFeature() as IFeature;
            }
        }

        private static List<IBody2> SolidBodies(IModelDoc2 doc)
        {
            var bodies = new List<IBody2>();
            object[] all = ((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (all == null) return bodies;
            foreach (object o in all)
            {
                IBody2 body = o as IBody2;
                if (body != null) bodies.Add(body);
            }
            return bodies;
        }

        internal sealed class PlateCheck
        {
            /// <summary>Null if the body is a plate.</summary>
            public string Reason;
            public double Thickness;
            /// <summary>The faces on the top side, to export.</summary>
            public readonly List<IFace2> TopFaces = new List<IFace2>();
        }

        /// <summary>
        /// A plate has two parallel flat sides and every other face perpendicular to them. The biggest flat face sets
        /// the direction; flat faces facing along it must sit at exactly two heights (top and bottom), and every other
        /// face's normal, sampled across it, must be at right angles to it.
        /// </summary>
        internal static PlateCheck CheckPlate(IBody2 body)
        {
            var check = new PlateCheck();
            object[] faceObjects = body.GetFaces() as object[];
            var faces = new List<IFace2>();
            if (faceObjects != null)
                foreach (object o in faceObjects)
                {
                    IFace2 f = o as IFace2;
                    if (f != null) faces.Add(f);
                }

            IFace2 biggest = null;
            double biggestArea = 0;
            foreach (IFace2 face in faces)
            {
                ISurface surface = face.GetSurface() as ISurface;
                if (surface == null || !surface.IsPlane()) continue;
                double area = face.GetArea();
                if (area > biggestArea) { biggestArea = area; biggest = face; }
            }
            if (biggest == null)
            {
                check.Reason = "no flat faces";
                return check;
            }

            double[] axis = Unit(biggest.Normal as double[]);
            if (axis == null)
            {
                check.Reason = "couldn't read its faces";
                return check;
            }
            double topHeight = Height(biggest, axis);

            var capHeights = new List<double>();
            var caps = new List<IFace2>();
            var capHeightOf = new List<double>();
            foreach (IFace2 face in faces)
            {
                ISurface surface = face.GetSurface() as ISurface;
                if (surface == null)
                {
                    check.Reason = "couldn't read its faces";
                    return check;
                }

                if (surface.IsPlane())
                {
                    double[] n = Unit(face.Normal as double[]);
                    if (n == null) { check.Reason = "couldn't read its faces"; return check; }
                    double cos = Dot(n, axis);
                    if (1 - Math.Abs(cos) <= ParallelTolerance)
                    {
                        double h = Height(face, axis);
                        caps.Add(face);
                        capHeightOf.Add(h);
                        if (!capHeights.Exists(x => Math.Abs(x - h) <= HeightTolerance)) capHeights.Add(h);
                    }
                    else if (Math.Abs(cos) > PerpendicularTolerance)
                    {
                        check.Reason = "has a slanted face (chamfer, draft or angled cut)";
                        return check;
                    }
                }
                else if (!StraightThrough(face, surface, axis))
                {
                    check.Reason = "has curved faces that don't go straight through (fillets, chamfered holes, threads, ...)";
                    return check;
                }
            }

            if (capHeights.Count > 2)
            {
                check.Reason = "isn't the same thickness everywhere (pockets or steps)";
                return check;
            }
            if (capHeights.Count < 2)
            {
                check.Reason = "has no flat bottom opposite its top";
                return check;
            }

            check.Thickness = Math.Abs(capHeights[0] - capHeights[1]);
            for (int i = 0; i < caps.Count; i++)
                if (Math.Abs(capHeightOf[i] - topHeight) <= HeightTolerance) check.TopFaces.Add(caps[i]);
            return check;
        }

        /// <summary>
        /// True if the (curved) face's normal is at right angles to axis everywhere it's sampled, i.e. the face is a
        /// wall running straight between top and bottom, like a through hole or a rounded outside corner.
        /// </summary>
        private static bool StraightThrough(IFace2 face, ISurface surface, double[] axis)
        {
            if (surface.IsSphere() || surface.IsTorus() || surface.IsCone()) return false;

            double[] uv = face.GetUVBounds() as double[];
            if (uv == null || uv.Length < 4) return false;
            double[] fractions = { 0.1, 0.5, 0.9 };
            int checkedPoints = 0;
            foreach (double fu in fractions)
                foreach (double fv in fractions)
                {
                    double u = uv[0] + (uv[1] - uv[0]) * fu;
                    double v = uv[2] + (uv[3] - uv[2]) * fv;
                    double[] du = surface.Evaluate(u, v, 1, 0) as double[];
                    double[] dv = surface.Evaluate(u, v, 0, 1) as double[];
                    if (du == null || dv == null || du.Length < 6 || dv.Length < 6) return false;
                    double[] normal = Unit(Cross(
                        new[] { du[3], du[4], du[5] },
                        new[] { dv[3], dv[4], dv[5] }));
                    if (normal == null) continue; // a degenerate point, e.g. a seam pole
                    if (Math.Abs(Dot(normal, axis)) > PerpendicularTolerance) return false;
                    checkedPoints++;
                }
            return checkedPoints > 0;
        }

        /// <summary>How far along axis the flat face sits.</summary>
        private static double Height(IFace2 face, double[] axis)
        {
            double[] p = face.GetClosestPointOn(0, 0, 0) as double[];
            return p == null || p.Length < 3 ? 0 : p[0] * axis[0] + p[1] * axis[1] + p[2] * axis[2];
        }

        private static double Dot(double[] a, double[] b)
        {
            return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        }

        private static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }

        private static double[] Unit(double[] v)
        {
            if (v == null || v.Length < 3) return null;
            double length = Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
            if (length < 1e-12) return null;
            return new[] { v[0] / length, v[1] / length, v[2] / length };
        }

        #endregion

        #region Pop-up

        private static bool Ask(ISldWorks swApp, string partName, List<BodyPlan> plans, out string folder)
        {
            folder = null;
            if (TestMode.Enabled)
            {
                folder = TestMode.WaterjetFolder ?? Path.Combine(TempDir, "test-output");
                TestMode.Record("BDAT", "Waterjet DXF pop-up for " + partName);
                return true;
            }

            using (var form = new WaterjetDxfForm(partName, plans, SavedFolder()))
            {
                if (form.ShowDialog(Ui.SolidWorksWindow(swApp)) != DialogResult.OK) return false;
                folder = form.Folder;
            }
            SaveFolder(folder);
            return true;
        }

        private static string SavedFolder()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UserKeyPath))
                {
                    string saved = key == null ? null : key.GetValue(FolderValue) as string;
                    if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved)) return saved;
                }
            }
            catch (Exception)
            {
                // Fall through to the default.
            }
            return System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        }

        private static void SaveFolder(string folder)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UserKeyPath))
                {
                    if (key != null) key.SetValue(FolderValue, folder, RegistryValueKind.String);
                }
            }
            catch (Exception)
            {
                // Only means it isn't remembered.
            }
        }

        #endregion

        #region Exporting (on the hidden copy)

        private static void ExportAll(IModelDoc2 work, string workPath, string partName, string outDir,
            List<BodyPlan> chosen, List<string> written, List<string> failed)
        {
            IPartDoc part = (IPartDoc)work;
            var bodies = new Dictionary<string, IBody2>(StringComparer.OrdinalIgnoreCase);
            foreach (IBody2 body in SolidBodies(work))
                if (!bodies.ContainsKey(body.Name)) bodies.Add(body.Name, body);

            Dictionary<string, IFeature> flatPatterns = FlatPatternsByBody(work, bodies);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (BodyPlan plan in chosen)
            {
                string dxf = Path.Combine(outDir, UniqueName(partName + " - " + plan.Name, usedNames) + ".dxf");
                IBody2 body;
                if (!bodies.TryGetValue(plan.BodyName, out body))
                {
                    failed.Add(plan.Name + ": not found in the working copy");
                    Log(plan.BodyName + ": not in the copy's bodies (" + string.Join(", ", new List<string>(bodies.Keys).ToArray()) + ")");
                    continue;
                }

                bool ok;
                work.ClearSelection2(true);
                if (plan.Kind == BodyKind.SheetMetal)
                {
                    IFeature flat;
                    if (!flatPatterns.TryGetValue(plan.BodyName, out flat))
                    {
                        Log(plan.BodyName + ": no flat pattern matched (found " + flatPatterns.Count + ")");
                        failed.Add(plan.Name + ": couldn't find its flat pattern");
                        continue;
                    }
                    bool selected = flat.Select2(false, -1);
                    ok = selected && part.ExportToDWG2(dxf, workPath,
                        (int)swExportToDWG_e.swExportToDWG_ExportSheetMetal, true, null, false, false, SheetMetalOptions, null);
                    Log(plan.BodyName + ": sheet metal, flat pattern " + flat.Name + ", selected " + selected + ", exported " + ok + " -> " + dxf);
                }
                else
                {
                    PlateCheck check = CheckPlate(body);
                    if (check.Reason != null || check.TopFaces.Count == 0)
                    {
                        Log(plan.BodyName + ": plate check on the copy said " + (check.Reason ?? "no top faces"));
                        failed.Add(plan.Name + ": couldn't find its top face");
                        continue;
                    }
                    bool selected = true;
                    for (int i = 0; i < check.TopFaces.Count; i++)
                        selected &= ((IEntity)check.TopFaces[i]).Select4(i > 0, null);
                    ok = selected && part.ExportToDWG2(dxf, workPath,
                        (int)swExportToDWG_e.swExportToDWG_ExportSelectedFacesOrLoops, true, null, false, false, 0, null);
                    Log(plan.BodyName + ": plate, " + check.TopFaces.Count + " top face(s), selected " + selected + ", exported " + ok + " -> " + dxf);
                }
                work.ClearSelection2(true);

                if (ok && File.Exists(dxf)) written.Add(Path.GetFileName(dxf));
                else failed.Add(plan.Name + ": SolidWorks couldn't export it" + (ok ? " (no file was written)" : ""));
            }
        }

        /// <summary>
        /// Each sheet metal body's Flat-Pattern feature. Matched through the flat pattern's fixed face; if that can't be
        /// read and there's only one of each, they go together.
        /// </summary>
        private static Dictionary<string, IFeature> FlatPatternsByBody(IModelDoc2 doc, Dictionary<string, IBody2> bodies)
        {
            var result = new Dictionary<string, IFeature>(StringComparer.OrdinalIgnoreCase);
            var unmatched = new List<IFeature>();

            IFeature feat = doc.FirstFeature() as IFeature;
            while (feat != null)
            {
                if (string.Equals(feat.GetTypeName2(), "FlatPattern", StringComparison.OrdinalIgnoreCase))
                {
                    string bodyName = FlatPatternBodyName(doc, feat);
                    if (bodyName != null && !result.ContainsKey(bodyName)) result.Add(bodyName, feat);
                    else unmatched.Add(feat);
                }
                feat = feat.GetNextFeature() as IFeature;
            }

            var sheetMetal = new List<string>();
            foreach (KeyValuePair<string, IBody2> pair in bodies)
                if (pair.Value.IsSheetMetal() && !result.ContainsKey(pair.Key)) sheetMetal.Add(pair.Key);
            if (sheetMetal.Count == 1 && unmatched.Count == 1) result.Add(sheetMetal[0], unmatched[0]);
            return result;
        }

        private static string FlatPatternBodyName(IModelDoc2 doc, IFeature flatPattern)
        {
            IFlatPatternFeatureData data = null;
            bool accessing = false;
            try
            {
                data = flatPattern.GetDefinition() as IFlatPatternFeatureData;
                if (data == null) return null;
                accessing = data.AccessSelections(doc, null);
                IFace2 fixedFace = data.FixedFace as IFace2;
                IBody2 body = fixedFace == null ? null : fixedFace.GetBody() as IBody2;
                return body == null ? null : body.Name;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (accessing) data.ReleaseSelectionAccess();
            }
        }

        private static string UniqueName(string name, HashSet<string> used)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            string candidate = name;
            for (int n = 2; !used.Add(candidate); n++) candidate = name + " (" + n + ")";
            return candidate;
        }

        #endregion

        #region Summary

        private static void Report(ISldWorks swApp, string partName, string outDir, List<BodyPlan> plans,
            List<string> written, List<string> failed)
        {
            var msg = new StringBuilder();
            if (written.Count > 0)
            {
                msg.AppendLine("Saved " + written.Count + " DXF" + (written.Count == 1 ? "" : "s") + " in " + outDir + ":");
                foreach (string name in written) msg.AppendLine("    " + name);
            }
            else
            {
                msg.AppendLine("No DXFs were saved.");
            }

            if (failed.Count > 0)
            {
                msg.AppendLine();
                msg.AppendLine("Couldn't export:");
                foreach (string f in failed) msg.AppendLine("    " + f);
            }

            List<BodyPlan> unticked = plans.FindAll(p => p.Kind != BodyKind.Skipped && !p.Export);
            if (unticked.Count > 0)
            {
                msg.AppendLine();
                msg.AppendLine("Not ticked:");
                foreach (BodyPlan p in unticked) msg.AppendLine("    " + p.Label);
            }

            if (plans.Exists(p => p.Kind == BodyKind.Skipped))
            {
                msg.AppendLine();
                msg.AppendLine("Skipped (can't be waterjet):");
                AppendSkipped(msg, plans);
            }

            msg.AppendLine();
            msg.Append("Your part \"" + partName + "\" was not changed.");

            if (TestMode.Enabled)
            {
                TestMode.Record("BDAT", msg.ToString());
                TestMode.LastWaterjet = written;
                return;
            }

            if (written.Count == 0)
            {
                Ui.Tell(swApp, msg.ToString(), swMessageBoxIcon_e.swMbWarning);
                return;
            }
            int choice = Ui.Choose(Ui.SolidWorksWindow(swApp), msg.ToString(), "BDAT", MessageBoxIcon.Information, "Open folder", "OK");
            if (choice == 0)
            {
                try { Process.Start("explorer.exe", "\"" + outDir + "\""); }
                catch (Exception) { /* the path is in the message */ }
            }
        }

        private static void AppendSkipped(StringBuilder msg, List<BodyPlan> plans)
        {
            foreach (BodyPlan p in plans)
                if (p.Kind == BodyKind.Skipped) msg.AppendLine("    " + p.Label);
        }

        internal static string Millimetres(double metres)
        {
            return (metres * 1000).ToString("0.###", System.Globalization.CultureInfo.CurrentCulture) + " mm";
        }

        #endregion

        /// <summary>The part's file name without extension, or its window title if it has never been saved.</summary>
        private static string BaseName(IModelDoc2 doc)
        {
            string path = doc.GetPathName();
            string name = string.IsNullOrEmpty(path) ? doc.GetTitle() : Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".sldprt", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 7);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        private static void Log(string message)
        {
            PlatformSave.Log("waterjet", message);
        }

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

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch { /* cleaned up on the next run */ }
        }
    }
}
