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
    /// XYZ Planes: type a point, get three planes through it, so the part can be origin-mated in the top level.
    ///
    ///   1. A pop-up asks for X, Y and Z, in the part's units (or with a unit typed after the number, e.g. "2 in").
    ///   2. Three planes are made through that point, parallel to the part's own planes:
    ///        XY, offset from the Front plane by Z
    ///        XZ, offset from the Top plane by Y
    ///        YZ, offset from the Right plane by X
    ///      Each is named with the point, e.g. "XY (10, 20, 30 mm)", so it's easy to pick when mating.
    ///   3. The three planes go in a folder named "Origin (10, 20, 30 mm)".
    /// </summary>
    public sealed class XyzPlanesCommand : IBdatCommand
    {
        public string Title { get { return "XYZ Planes"; } }

        public string Hint { get { return "Make XY, XZ and YZ planes through a point you type in, for origin mates"; } }

        // Plane positions are checked to this many metres after they're made.
        private const double Tolerance = 1e-8;

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
                Ui.Tell(swApp, "Open a part first. XYZ Planes adds the planes to the part you have open.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            LengthUnit unit = DocumentUnit(doc);
            double[] point;
            if (!AskForPoint(swApp, unit, out point)) return;

            // Front, Top and Right, whatever they're called in this part.
            IFeature[] bases = StandardPlanes(doc);
            if (bases == null)
            {
                Ui.Tell(swApp, "Couldn't find this part's Front, Top and Right planes, so no planes were made.", swMessageBoxIcon_e.swMbStop);
                return;
            }

            string label = PointLabel(point, unit);
            var made = new List<IFeature>();
            try
            {
                // Axis 2 (Z) from Front, axis 1 (Y) from Top, axis 0 (X) from Right.
                made.Add(MakePlane(doc, bases[0], 2, point[2], "XY " + label));
                made.Add(MakePlane(doc, bases[1], 1, point[1], "XZ " + label));
                made.Add(MakePlane(doc, bases[2], 0, point[0], "YZ " + label));
            }
            catch (Exception ex)
            {
                // Don't leave one or two planes behind; it's all three or nothing.
                foreach (IFeature f in made) Delete(doc, f);
                doc.ClearSelection2(true);
                Ui.Tell(swApp, "Couldn't make the planes at " + label + ": " + ex.Message, swMessageBoxIcon_e.swMbStop);
                return;
            }

            string folder = PutInFolder(doc, made, "Origin " + label);
            doc.ClearSelection2(true);
            doc.GraphicsRedraw2();

            if (TestMode.Enabled)
            {
                var names = new List<string>();
                foreach (IFeature f in made) names.Add(f.Name);
                TestMode.LastXyzPlanes = new XyzPlanesTestResult { Planes = names.ToArray(), Folder = folder };
            }
        }

        /// <summary>The pop-up (or, in test mode, TestMode.PlaneCoordinates). False if cancelled or invalid.</summary>
        private static bool AskForPoint(ISldWorks swApp, LengthUnit unit, out double[] point)
        {
            point = null;
            if (TestMode.Enabled)
            {
                string[] typed = TestMode.PlaneCoordinates;
                if (typed == null) return false; // Cancel
                string error = ParsePoint(typed[0], typed[1], typed[2], unit, out point);
                if (error == null) return true;
                Ui.Tell(swApp, error, swMessageBoxIcon_e.swMbWarning);
                return false;
            }

            IFrame frame = swApp.Frame() as IFrame;
            var owner = new WindowHandle(frame == null ? IntPtr.Zero : new IntPtr(frame.GetHWndx64()));
            using (var form = new XyzPlanesForm(unit))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return false;
                point = form.Point;
                return true;
            }
        }

        // ---------------------------------------------------------------- planes

        /// <summary>
        /// Front, Top and Right: the part's first three planes, told apart by their normals (Z, Y, X) so a renamed
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
            // Couldn't tell them apart: every SolidWorks part starts Front, Top, Right.
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

        /// <summary>Names the feature, adding " 2", " 3"... if the part already has one with that name.</summary>
        private static void Rename(IModelDoc2 doc, IFeature feature, string name)
        {
            IPartDoc part = (IPartDoc)doc;
            string unique = name;
            for (int n = 2; part.FeatureByName(unique) != null && n < 100; n++) unique = name + " " + n;
            feature.Name = unique;
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

        /// <summary>Puts the planes in a folder of their own. Returns the folder's name, or null if there isn't one.</summary>
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
                return null; // The planes are what matters; a folder is just tidy.
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

        /// <summary>The part's length unit (Tools > Options > Document Properties > Units).</summary>
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
