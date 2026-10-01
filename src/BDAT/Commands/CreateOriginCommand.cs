using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT.Commands
{
    /// <summary>
    /// Create Origin: type a point, get a new origin there, so a part or sub-assembly can be origin-mated in the top
    /// level. Works in parts and assemblies. (SolidWorks can't move the real origin, so this is the next best thing.)
    ///
    ///   1. A pop-up asks for X, Y and Z, in the document's units (or with a unit typed after the number, e.g. "2 in").
    ///   2. Three planes are made through that point, parallel to the document's own planes:
    ///        X', the Right plane moved to X
    ///        Y', the Top plane moved to Y
    ///        Z', the Front plane moved to Z
    ///   3. A coordinate system called Origin' is made at the point, with its axes along the document's X, Y and Z.
    ///   4. All four go in a folder named with the point, e.g. "Origin' (10, 20, 30 mm)".
    /// Running it again in the same document adds " 2", " 3"... to the names.
    /// </summary>
    public sealed class CreateOriginCommand : IBdatCommand
    {
        public string Title { get { return "Create Origin"; } }

        public string Hint { get { return "Make a new origin (coordinate system and X', Y', Z' planes) at a point you type in, for origin mates"; } }

        // Plane positions are checked to this many metres after they're made.
        private const double Tolerance = 1e-8;

        public bool IsEnabled(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            return IsPartOrAssembly(doc);
        }

        private static bool IsPartOrAssembly(IModelDoc2 doc)
        {
            if (doc == null) return false;
            int type = doc.GetType();
            return type == (int)swDocumentTypes_e.swDocPART || type == (int)swDocumentTypes_e.swDocASSEMBLY;
        }

        public void Run(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            if (!IsPartOrAssembly(doc))
            {
                Ui.Tell(swApp, "Open a part or assembly first. Create Origin adds the new origin to the one you have open.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            LengthUnit unit = DocumentUnit(doc);
            double[] point;
            if (!AskForPoint(swApp, unit, out point)) return;

            // Front, Top and Right, whatever they're called in this part.
            IFeature[] bases = StandardPlanes(doc);
            if (bases == null)
            {
                Ui.Tell(swApp, "Couldn't find the Front, Top and Right planes, so nothing was made.", swMessageBoxIcon_e.swMbStop);
                return;
            }

            string label = PointLabel(point, unit);
            var made = new List<IFeature>();
            IFeature origin;
            try
            {
                // X' from Right (axis 0), Y' from Top (axis 1), Z' from Front (axis 2).
                made.Add(MakePlane(doc, bases[2], 0, point[0], "X'"));
                made.Add(MakePlane(doc, bases[1], 1, point[1], "Y'"));
                made.Add(MakePlane(doc, bases[0], 2, point[2], "Z'"));
                origin = MakeCoordinateSystem(doc, point, made);
            }
            catch (Exception ex)
            {
                // Don't leave half of it behind; it's everything or nothing.
                for (int i = made.Count - 1; i >= 0; i--) Delete(doc, made[i]);
                doc.ClearSelection2(true);
                Ui.Tell(swApp, "Couldn't make the origin at " + label + ": " + ex.Message, swMessageBoxIcon_e.swMbStop);
                return;
            }

            string folder = PutInFolder(doc, made, "Origin' " + label);
            doc.ClearSelection2(true);
            doc.GraphicsRedraw2();

            if (TestMode.Enabled)
            {
                var names = new List<string>();
                foreach (IFeature f in made)
                    if (f.GetTypeName2() == "RefPlane") names.Add(f.Name);
                TestMode.LastCreateOrigin = new CreateOriginTestResult { Planes = names.ToArray(), Origin = origin.Name, Folder = folder };
            }
        }

        /// <summary>The pop-up (or, in test mode, TestMode.OriginCoordinates). False if cancelled or invalid.</summary>
        private static bool AskForPoint(ISldWorks swApp, LengthUnit unit, out double[] point)
        {
            point = null;
            if (TestMode.Enabled)
            {
                string[] typed = TestMode.OriginCoordinates;
                if (typed == null) return false; // Cancel
                string error = ParsePoint(typed[0], typed[1], typed[2], unit, out point);
                if (error == null) return true;
                Ui.Tell(swApp, error, swMessageBoxIcon_e.swMbWarning);
                return false;
            }

            IFrame frame = swApp.Frame() as IFrame;
            var owner = new WindowHandle(frame == null ? IntPtr.Zero : new IntPtr(frame.GetHWndx64()));
            using (var form = new CreateOriginForm(unit))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return false;
                point = form.Point;
                return true;
            }
        }

        // ---------------------------------------------------------------- planes

        /// <summary>
        /// Front, Top and Right: the document's first three planes, told apart by their normals (Z, Y, X) so a renamed
        /// or reordered plane still lands in the right place. Null if they can't be found.
        /// </summary>
        private static IFeature[] StandardPlanes(IModelDoc2 doc)
        {
            var planes = new List<IFeature>();
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null && planes.Count < 3)
            {
                if (f.GetTypeName2() == "RefPlane") planes.Add(f);
                f = f.GetNextFeature() as IFeature;
            }
            if (planes.Count < 3) return null;

            var byNormal = new IFeature[3];
            foreach (IFeature plane in planes)
            {
                double[] t = PlaneTransform(plane);
                if (t == null) continue;
                // Rows 0-2 of the rotation are the plane's x, y and z (normal) directions.
                int normalAxis = DominantAxis(t[6], t[7], t[8]);
                int slot = normalAxis == 2 ? 0 : normalAxis == 1 ? 1 : 2; // Front, Top, Right
                if (byNormal[slot] == null) byNormal[slot] = plane;
            }
            if (byNormal[0] != null && byNormal[1] != null && byNormal[2] != null) return byNormal;
            // Couldn't tell them apart: every SolidWorks part and assembly starts Front, Top, Right.
            return planes.ToArray();
        }

        /// <summary>
        /// A plane parallel to basePlane, at coordinate on the axis it's normal to. Checked after it's made: if the
        /// offset went the wrong way it's remade flipped, so negative coordinates work whatever SolidWorks defaults to.
        /// </summary>
        private static IFeature MakePlane(IModelDoc2 doc, IFeature basePlane, int axis, double coordinate, string name)
        {
            if (Math.Abs(coordinate) < Tolerance)
            {
                IFeature same = InsertPlane(doc, basePlane, (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0);
                if (same == null) throw new InvalidOperationException("SolidWorks didn't make the " + name + " plane.");
                Rename(doc, same, name);
                return same;
            }

            int distance = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Distance;
            int flip = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_OptionFlip;
            foreach (int constraint in new[] { distance, distance | flip })
            {
                IFeature plane = InsertPlane(doc, basePlane, constraint, Math.Abs(coordinate));
                if (plane == null) continue;
                double[] t = PlaneTransform(plane);
                if (t != null && Math.Abs(t[9 + axis] - coordinate) < Tolerance)
                {
                    Rename(doc, plane, name);
                    return plane;
                }
                Delete(doc, plane);
            }
            throw new InvalidOperationException("SolidWorks didn't put the " + name + " plane where it should be.");
        }

        /// <summary>
        /// The Origin' coordinate system at point, axes along the document's. Added to made (with the sketch point it
        /// hangs on, if it needs one). Checked after it's made; throws if it can't be put in the right place.
        /// </summary>
        private static IFeature MakeCoordinateSystem(IModelDoc2 doc, double[] point, List<IFeature> made)
        {
            // SolidWorks 2019 and later can place one by numbers, with nothing for it to depend on.
            IFeature cs = ByNumbers(doc, point);
            if (cs != null && !At(doc, cs, point)) { Delete(doc, cs); cs = null; }
            if (cs != null)
            {
                Rename(doc, cs, "Origin'");
                made.Add(cs);
                return cs;
            }

            // Otherwise on a point in a 3D sketch, which stays hidden next to it.
            IFeature last = doc.FeatureByPositionReverse(0) as IFeature;
            doc.ClearSelection2(true);
            doc.SketchManager.Insert3DSketch(true);
            bool addToDb = doc.SketchManager.AddToDB;
            doc.SketchManager.AddToDB = true; // no snapping to whatever is near
            SketchPoint sketchPoint = doc.SketchManager.CreatePoint(point[0], point[1], point[2]);
            doc.SketchManager.AddToDB = addToDb;
            doc.SketchManager.Insert3DSketch(true);
            IFeature sketch = doc.FeatureByPositionReverse(0) as IFeature;
            if (sketchPoint == null || sketch == null || ReferenceEquals(sketch, last))
                throw new InvalidOperationException("SolidWorks didn't make the point for the coordinate system.");
            made.Add(sketch);
            Rename(doc, sketch, "Origin' point");

            doc.ClearSelection2(true);
            SelectData origin = ((ISelectionMgr)doc.SelectionManager).CreateSelectData() as SelectData;
            origin.Mark = 1; // the coordinate system's origin
            if (!sketchPoint.Select4(false, origin))
                throw new InvalidOperationException("couldn't select the point for the coordinate system.");
            cs = doc.FeatureManager.InsertCoordinateSystem(false, false, false) as IFeature;
            doc.ClearSelection2(true);
            if (cs == null) throw new InvalidOperationException("SolidWorks didn't make the coordinate system.");
            made.Add(cs);
            if (!At(doc, cs, point)) throw new InvalidOperationException("SolidWorks didn't put the coordinate system at the point.");
            Rename(doc, cs, "Origin'");

            sketch.Select2(false, 0);
            doc.BlankSketch(); // hide the point; the coordinate system shows where it is
            doc.ClearSelection2(true);
            return cs;
        }

        /// <summary>
        /// FeatureManager.CreateCoordinateSystemUsingNumericalValues (SolidWorks 2019+), found by name so BDAT still
        /// builds against older SolidWorks. Null if it isn't there or doesn't work.
        /// </summary>
        private static IFeature ByNumbers(IModelDoc2 doc, double[] point)
        {
            try
            {
                System.Reflection.MethodInfo m = typeof(IFeatureManager).GetMethod("CreateCoordinateSystemUsingNumericalValues");
                if (m == null) return null;
                System.Reflection.ParameterInfo[] p = m.GetParameters();
                // (origin defined, x, y, z, rotation defined, angle x, angle y, angle z)
                if (p.Length != 8 || p[0].ParameterType != typeof(bool) || p[4].ParameterType != typeof(bool)) return null;
                IFeature last = doc.FeatureByPositionReverse(0) as IFeature;
                doc.ClearSelection2(true);
                object made = m.Invoke(doc.FeatureManager, new object[] { true, point[0], point[1], point[2], false, 0.0, 0.0, 0.0 });
                IFeature cs = made as IFeature ?? doc.FeatureByPositionReverse(0) as IFeature;
                if (cs == null || ReferenceEquals(cs, last) || cs.GetTypeName2() != "CoordSys") return null;
                return cs;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The coordinate system's origin is at point and its axes are the document's.</summary>
        private static bool At(IModelDoc2 doc, IFeature cs, double[] point)
        {
            MathTransform t = doc.Extension.GetCoordinateSystemTransformByName(cs.Name) as MathTransform;
            double[] d = t == null ? null : t.ArrayData as double[];
            if (d == null || d.Length < 12) return false;
            double[] identity = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            for (int i = 0; i < 9; i++)
                if (Math.Abs(d[i] - identity[i]) > 1e-6) return false;
            // The transform takes the coordinate system into the model, so its translation is where its origin is.
            for (int i = 0; i < 3; i++)
                if (Math.Abs(d[9 + i] - point[i]) > Tolerance) return false;
            return true;
        }

        private static IFeature InsertPlane(IModelDoc2 doc, IFeature basePlane, int constraint, double value)
        {
            IFeature last = doc.FeatureByPositionReverse(0) as IFeature;
            doc.ClearSelection2(true);
            if (!basePlane.Select2(false, 0)) return null;
            object made = doc.FeatureManager.InsertRefPlane(constraint, value, 0, 0, 0, 0);
            doc.ClearSelection2(true);
            if (made == null) return null;
            // InsertRefPlane hands back the RefPlane; the feature is the newest one in the tree.
            IFeature feature = made as IFeature ?? doc.FeatureByPositionReverse(0) as IFeature;
            if (feature == null || ReferenceEquals(feature, last) || feature.GetTypeName2() != "RefPlane") return null;
            return feature;
        }

        /// <summary>The plane's MathTransform array: rotation rows in 0-8, origin (metres) in 9-11.</summary>
        private static double[] PlaneTransform(IFeature plane)
        {
            IRefPlane refPlane = plane.GetSpecificFeature2() as IRefPlane;
            if (refPlane == null) return null;
            MathTransform transform = refPlane.Transform as MathTransform;
            if (transform == null) return null;
            double[] data = transform.ArrayData as double[];
            return data != null && data.Length >= 12 ? data : null;
        }

        private static int DominantAxis(double x, double y, double z)
        {
            double ax = Math.Abs(x), ay = Math.Abs(y), az = Math.Abs(z);
            if (az >= ax && az >= ay) return 2;
            return ay >= ax ? 1 : 0;
        }

        /// <summary>Names the feature, adding " 2", " 3"... if the document already has one with that name.</summary>
        private static void Rename(IModelDoc2 doc, IFeature feature, string name)
        {
            string unique = name;
            for (int n = 2; FeatureByName(doc, unique) != null && n < 100; n++) unique = name + " " + n;
            feature.Name = unique;
        }

        internal static IFeature FeatureByName(IModelDoc2 doc, string name)
        {
            IPartDoc part = doc as IPartDoc;
            if (part != null) return part.FeatureByName(name) as IFeature;
            IAssemblyDoc assembly = doc as IAssemblyDoc;
            return assembly == null ? null : assembly.FeatureByName(name) as IFeature;
        }

        private static void Delete(IModelDoc2 doc, IFeature feature)
        {
            try
            {
                doc.ClearSelection2(true);
                if (feature.Select2(false, 0)) doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed);
            }
            catch
            {
                // Best effort; the error message tells the user what happened.
            }
            doc.ClearSelection2(true);
        }

        /// <summary>Puts the new features in a folder of their own. Returns the folder's name, or null if there isn't one.</summary>
        private static string PutInFolder(IModelDoc2 doc, List<IFeature> planes, string name)
        {
            try
            {
                doc.ClearSelection2(true);
                foreach (IFeature f in planes) f.Select2(true, 0);
                IFeature folder = doc.FeatureManager.InsertFeatureTreeFolder2(
                    (int)swFeatureTreeFolderType_e.swFeatureTreeFolder_Containing) as IFeature;
                if (folder == null) return null;
                Rename(doc, folder, name);
                return folder.Name;
            }
            catch
            {
                return null; // The origin is what matters; a folder is just tidy.
            }
            finally
            {
                doc.ClearSelection2(true);
            }
        }

        // ---------------------------------------------------------------- units and typing

        /// <summary>A length unit: its short name and how many metres one of it is.</summary>
        internal sealed class LengthUnit
        {
            public LengthUnit(string name, double metres) { Name = name; Metres = metres; }
            public string Name { get; private set; }
            public double Metres { get; private set; }
        }

        internal static readonly LengthUnit Millimetres = new LengthUnit("mm", 0.001);
        internal static readonly LengthUnit Inches = new LengthUnit("in", 0.0254);

        // Units that can be typed after a number, longest first so "mm" wins over "m".
        private static readonly KeyValuePair<string, LengthUnit>[] Suffixes =
        {
            new KeyValuePair<string, LengthUnit>("mm", Millimetres),
            new KeyValuePair<string, LengthUnit>("cm", new LengthUnit("cm", 0.01)),
            new KeyValuePair<string, LengthUnit>("in", Inches),
            new KeyValuePair<string, LengthUnit>("ft", new LengthUnit("ft", 0.3048)),
            new KeyValuePair<string, LengthUnit>("m", new LengthUnit("m", 1.0)),
            new KeyValuePair<string, LengthUnit>("\"", Inches),
        };

        /// <summary>The document's length unit (Tools > Options > Document Properties > Units).</summary>
        private static LengthUnit DocumentUnit(IModelDoc2 doc)
        {
            int unit = doc.Extension.GetUserPreferenceInteger(
                (int)swUserPreferenceIntegerValue_e.swUnitsLinear, (int)swUserPreferenceOption_e.swDetailingNoOptionSpecified);
            return UnitFor(unit);
        }

        internal static LengthUnit UnitFor(int swLengthUnit)
        {
            switch ((swLengthUnit_e)swLengthUnit)
            {
                case swLengthUnit_e.swMM: return Millimetres;
                case swLengthUnit_e.swCM: return new LengthUnit("cm", 0.01);
                case swLengthUnit_e.swMETER: return new LengthUnit("m", 1.0);
                case swLengthUnit_e.swINCHES: return Inches;
                case swLengthUnit_e.swFEET: return new LengthUnit("ft", 0.3048);
                case swLengthUnit_e.swFEETINCHES: return Inches;
                case swLengthUnit_e.swANGSTROM: return new LengthUnit("\u00c5", 1e-10);
                case swLengthUnit_e.swNANOMETER: return new LengthUnit("nm", 1e-9);
                case swLengthUnit_e.swMICRON: return new LengthUnit("\u00b5m", 1e-6);
                case swLengthUnit_e.swMIL: return new LengthUnit("mil", 0.0000254);
                case swLengthUnit_e.swUIN: return new LengthUnit("\u00b5in", 0.0000000254);
                default: return Millimetres;
            }
        }

        /// <summary>
        /// One coordinate as typed: a number in the part's units, or a number with a unit after it ("2 in", "50mm").
        /// Empty means 0. Returns null and sets metres, or returns why it can't be read.
        /// </summary>
        internal static string ParseCoordinate(string text, LengthUnit documentUnit, out double metres)
        {
            metres = 0;
            string s = (text ?? "").Trim();
            if (s.Length == 0) return null;

            LengthUnit unit = documentUnit;
            foreach (KeyValuePair<string, LengthUnit> suffix in Suffixes)
            {
                if (s.Length > suffix.Key.Length && s.EndsWith(suffix.Key, StringComparison.OrdinalIgnoreCase))
                {
                    unit = suffix.Value;
                    s = s.Substring(0, s.Length - suffix.Key.Length).Trim();
                    break;
                }
            }

            double value;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
                return "\"" + text.Trim() + "\" isn't a number.";
            if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value * unit.Metres) > 1000)
                return "\"" + text.Trim() + "\" is too far away.";
            metres = value * unit.Metres;
            return null;
        }

        /// <summary>All three coordinates, in metres. Returns null on success, else what's wrong (naming the box).</summary>
        internal static string ParsePoint(string x, string y, string z, LengthUnit documentUnit, out double[] point)
        {
            point = new double[3];
            string[] typed = { x, y, z };
            string[] axes = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                string error = ParseCoordinate(typed[i], documentUnit, out point[i]);
                if (error != null) return axes[i] + ": " + error;
            }
            return null;
        }

        /// <summary>"(10, 20, 30 mm)": the point in the part's units, for plane and folder names.</summary>
        internal static string PointLabel(double[] point, LengthUnit unit)
        {
            return "(" + Number(point[0], unit) + ", " + Number(point[1], unit) + ", " + Number(point[2], unit) + " " + unit.Name + ")";
        }

        internal static string Number(double metres, LengthUnit unit)
        {
            double value = Math.Round(metres / unit.Metres, 4);
            if (value == 0) value = 0; // no "-0"
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private sealed class WindowHandle : IWin32Window
        {
            private readonly IntPtr _handle;
            public WindowHandle(IntPtr handle) { _handle = handle; }
            public IntPtr Handle { get { return _handle; } }
        }
    }
}
