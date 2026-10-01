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
    /// It uses vehicle axes (ISO 8855): X forward, Y to the driver's left, Z up. Which SolidWorks direction is
    /// "forward" is set once, in Forward below.
    ///
    ///   1. A pop-up asks for X (forward), Y (left) and Z (up), in the document's units (or with a unit typed after
    ///      the number, e.g. "2 in").
    ///   2. Three planes through that point, each perpendicular to one vehicle axis and named after it, and each
    ///      offset from the Front, Top or Right plane: X' Plane (at the typed X), Y' Plane (at Y), Z' Plane (at Z).
    ///   3. Three axes where the planes cross: X' Axis, Y' Axis and Z' Axis.
    ///   4. A reference point where they all meet: Origin' Point.
    ///   5. A coordinate system, Origin', on that point with its X along X' Axis and Y along Y' Axis.
    ///   6. All of it in a folder called "New Origin".
    /// Everything is fully defined from the three plane offsets, so to move the origin you edit those distances and
    /// the axes, point and coordinate system follow. Running it again in the same document adds " 2", " 3"... to the
    /// names.
    /// </summary>
    public sealed class CreateOriginCommand : IBdatCommand
    {
        public string Title { get { return "Create Origin"; } }

        public string Hint { get { return "Make a new origin (Origin' coordinate system with vehicle X forward, Y left, Z up, plus planes and axes) at a point you type in"; } }

        /// <summary>Which SolidWorks direction the car's nose points in.</summary>
        internal enum ForwardDirection { PlusZ, PlusX, MinusX, MinusZ }

        /// <summary>FUBC's convention (Ben, 2026-10-01): the nose points at +Z, so the Front view looks at the front of the car.</summary>
        internal const ForwardDirection Forward = ForwardDirection.PlusZ;

        // Positions are checked to this many metres after they're made.
        private const double Tolerance = 1e-8;

        /// <summary>
        /// Vehicle X (forward), Y (left) and Z (up) as SolidWorks directions. Z is always SolidWorks +Y (up), and
        /// Y = Z x X so the frame is right-handed.
        /// </summary>
        internal static double[][] VehicleAxes(ForwardDirection forward)
        {
            double[] x;
            switch (forward)
            {
                case ForwardDirection.PlusX: x = new double[] { 1, 0, 0 }; break;
                case ForwardDirection.MinusX: x = new double[] { -1, 0, 0 }; break;
                case ForwardDirection.MinusZ: x = new double[] { 0, 0, -1 }; break;
                default: x = new double[] { 0, 0, 1 }; break;
            }
            double[] z = { 0, 1, 0 };
            double[] y = Cross(z, x);
            return new[] { x, y, z };
        }

        internal static double[] Cross(double[] a, double[] b)
        {
            return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
        }

        /// <summary>A point typed in vehicle coordinates, as SolidWorks model coordinates (metres).</summary>
        internal static double[] ToModel(double[] vehicle, double[][] axes)
        {
            var p = new double[3];
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 3; k++)
                    p[k] += vehicle[i] * axes[i][k];
            return p;
        }

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
            double[] vehicle;
            if (!AskForPoint(swApp, unit, out vehicle)) return;

            // Front, Top and Right, whatever they're called here, indexed by the SolidWorks axis they're normal to.
            IFeature[] bases = StandardPlanes(doc);
            if (bases == null)
            {
                Ui.Tell(swApp, "Couldn't find the Front, Top and Right planes, so nothing was made.", swMessageBoxIcon_e.swMbStop);
                return;
            }

            double[][] axes = VehicleAxes(Forward);
            double[] point = ToModel(vehicle, axes);
            string label = PointLabel(vehicle, unit);
            var made = new List<IFeature>();
            var result = new CreateOriginTestResult();
            try
            {
                // Planes, each perpendicular to the vehicle axis it's named after.
                string[] planeNames = { "X' Plane", "Y' Plane", "Z' Plane" };
                var planes = new IFeature[3];
                for (int i = 0; i < 3; i++)
                {
                    int swAxis = DominantAxis(axes[i][0], axes[i][1], axes[i][2]);
                    planes[i] = MakePlane(doc, bases[swAxis], swAxis, point[swAxis], planeNames[i]);
                    made.Add(planes[i]);
                }

                // Axes where two planes cross: X' Axis (Z' and Y' Planes), Y' Axis (Z' and X'), Z' Axis (Y' and X').
                IFeature xAxis = MakeAxis(doc, planes[2], planes[1], "X' Axis");
                made.Add(xAxis);
                IFeature yAxis = MakeAxis(doc, planes[2], planes[0], "Y' Axis");
                made.Add(yAxis);
                IFeature zAxis = MakeAxis(doc, planes[1], planes[0], "Z' Axis");
                made.Add(zAxis);

                IFeature refPoint = MakeRefPoint(doc, xAxis, planes[0], point, "Origin' Point");
                made.Add(refPoint);

                IFeature cs = MakeCoordinateSystem(doc, refPoint, xAxis, yAxis, point, axes);
                made.Add(cs);

                result.Planes = new[] { planes[0].Name, planes[1].Name, planes[2].Name };
                result.Axes = new[] { xAxis.Name, yAxis.Name, zAxis.Name };
                result.Point = refPoint.Name;
                result.Origin = cs.Name;

                // The coordinate system shows the axes and the origin; keep the tree's helpers out of the way.
                doc.ClearSelection2(true);
                xAxis.Select2(true, 0);
                yAxis.Select2(true, 0);
                zAxis.Select2(true, 0);
                refPoint.Select2(true, 0);
                doc.BlankRefGeom();
                doc.ClearSelection2(true);
            }
            catch (Exception ex)
            {
                // Don't leave half of it behind; it's everything or nothing.
                for (int i = made.Count - 1; i >= 0; i--) Delete(doc, made[i]);
                doc.ClearSelection2(true);
                Ui.Tell(swApp, "Couldn't make the origin at " + label + ": " + ex.Message, swMessageBoxIcon_e.swMbStop);
                return;
            }

            // No coordinates in the name: they'd be wrong as soon as the origin is moved.
            result.Folder = PutInFolder(doc, made, "New Origin");
            doc.ClearSelection2(true);
            doc.GraphicsRedraw2();

            if (TestMode.Enabled) TestMode.LastCreateOrigin = result;
        }

        /// <summary>The pop-up (or, in test mode, TestMode.OriginCoordinates). Vehicle X, Y, Z in metres. False if cancelled or invalid.</summary>
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

        // ---------------------------------------------------------------- reference geometry

        /// <summary>
        /// Front, Top and Right, indexed by the SolidWorks axis they're normal to (0 Right, 1 Top, 2 Front), told apart
        /// by their normals so a renamed or reordered plane still lands in the right place. Null if they can't be found.
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
                if (byNormal[normalAxis] == null) byNormal[normalAxis] = plane;
            }
            if (byNormal[0] != null && byNormal[1] != null && byNormal[2] != null) return byNormal;
            // Couldn't tell them apart: every SolidWorks part and assembly starts Front (Z), Top (Y), Right (X).
            return new[] { planes[2], planes[1], planes[0] };
        }

        /// <summary>
        /// A plane parallel to basePlane, offset to coordinate on the SolidWorks axis it's normal to. The offset is a
        /// normal distance you can edit later. Checked after it's made: if it went the wrong way it's remade flipped,
        /// so negative coordinates work whatever SolidWorks defaults to.
        /// </summary>
        private static IFeature MakePlane(IModelDoc2 doc, IFeature basePlane, int axis, double coordinate, string name)
        {
            if (Math.Abs(coordinate) < Tolerance)
            {
                IFeature same = InsertPlane(doc, basePlane, (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident, 0);
                if (same == null) throw new InvalidOperationException("SolidWorks didn't make the " + name + ".");
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
            throw new InvalidOperationException("SolidWorks didn't put the " + name + " where it should be.");
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

        /// <summary>A reference axis where two planes cross.</summary>
        private static IFeature MakeAxis(IModelDoc2 doc, IFeature plane1, IFeature plane2, string name)
        {
            IFeature last = doc.FeatureByPositionReverse(0) as IFeature;
            doc.ClearSelection2(true);
            if (!plane1.Select2(false, 0) || !plane2.Select2(true, 0))
                throw new InvalidOperationException("couldn't select the planes for " + name + ".");
            bool ok = doc.InsertAxis2(true);
            doc.ClearSelection2(true);
            IFeature axis = doc.FeatureByPositionReverse(0) as IFeature;
            if (!ok || axis == null || ReferenceEquals(axis, last) || axis.GetTypeName2() != "RefAxis")
                throw new InvalidOperationException("SolidWorks didn't make " + name + ".");
            Rename(doc, axis, name);
            return axis;
        }

        /// <summary>A reference point where an axis meets a plane, checked to be at point.</summary>
        private static IFeature MakeRefPoint(IModelDoc2 doc, IFeature axis, IFeature plane, double[] point, string name)
        {
            IFeature last = doc.FeatureByPositionReverse(0) as IFeature;
            doc.ClearSelection2(true);
            if (!axis.Select2(false, 0) || !plane.Select2(true, 0))
                throw new InvalidOperationException("couldn't select the axis and plane for " + name + ".");
            object made = doc.FeatureManager.InsertReferencePoint((int)swRefPointType_e.swRefPointIntersection, 0, 0, 1);
            doc.ClearSelection2(true);
            object[] features = made as object[];
            IFeature refPoint = features != null && features.Length > 0 ? features[0] as IFeature : null;
            if (refPoint == null) refPoint = doc.FeatureByPositionReverse(0) as IFeature;
            if (refPoint == null || ReferenceEquals(refPoint, last) || refPoint.GetTypeName2() != "RefPoint")
                throw new InvalidOperationException("SolidWorks didn't make " + name + ".");

            IRefPoint data = refPoint.GetSpecificFeature2() as IRefPoint;
            MathPoint where = data == null ? null : data.GetRefPoint() as MathPoint;
            double[] xyz = where == null ? null : where.ArrayData as double[];
            if (xyz == null || xyz.Length < 3 ||
                Math.Abs(xyz[0] - point[0]) > Tolerance || Math.Abs(xyz[1] - point[1]) > Tolerance || Math.Abs(xyz[2] - point[2]) > Tolerance)
            {
                Delete(doc, refPoint);
                throw new InvalidOperationException("SolidWorks didn't put " + name + " where the planes meet.");
            }
            Rename(doc, refPoint, name);
            return refPoint;
        }

        /// <summary>
        /// Origin': origin on the reference point, X along X' Axis, Y along Y' Axis. Which way SolidWorks points an
        /// axis picked from a reference axis isn't certain, so each flip combination is tried until the coordinate
        /// system's X, Y and Z are the vehicle's.
        /// </summary>
        private static IFeature MakeCoordinateSystem(IModelDoc2 doc, IFeature refPoint, IFeature xAxis, IFeature yAxis,
            double[] point, double[][] axes)
        {
            bool[][] flips = { new[] { false, false }, new[] { true, false }, new[] { false, true }, new[] { true, true } };
            foreach (bool[] flip in flips)
            {
                IFeature last = doc.FeatureByPositionReverse(0) as IFeature;
                doc.ClearSelection2(true);
                // Marks: 1 origin, 2 X axis, 4 Y axis.
                if (!refPoint.Select2(true, 1) || !xAxis.Select2(true, 2) || !yAxis.Select2(true, 4))
                    throw new InvalidOperationException("couldn't select the point and axes for the coordinate system.");
                IFeature cs = doc.FeatureManager.InsertCoordinateSystem(flip[0], flip[1], false) as IFeature;
                doc.ClearSelection2(true);
                if (cs == null || ReferenceEquals(cs, last)) continue;
                if (IsVehicleFrame(doc, cs, point, axes))
                {
                    Rename(doc, cs, "Origin'");
                    return cs;
                }
                Delete(doc, cs);
            }
            throw new InvalidOperationException("SolidWorks didn't line the coordinate system up with X forward, Y left, Z up.");
        }

        /// <summary>The coordinate system's origin is at point and its X, Y, Z are the vehicle axes.</summary>
        internal static bool IsVehicleFrame(IModelDoc2 doc, IFeature cs, double[] point, double[][] axes)
        {
            MathTransform t = doc.Extension.GetCoordinateSystemTransformByName(cs.Name) as MathTransform;
            double[] d = t == null ? null : t.ArrayData as double[];
            return d != null && IsFrame(d, point, axes);
        }

        /// <summary>
        /// A coordinate system transform (rotation rows 0-8 are its X, Y, Z directions in the model, translation 9-11
        /// is its origin) matches point and axes.
        /// </summary>
        internal static bool IsFrame(double[] d, double[] point, double[][] axes)
        {
            if (d.Length < 12) return false;
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 3; k++)
                    if (Math.Abs(d[3 * i + k] - axes[i][k]) > 1e-6) return false;
            for (int k = 0; k < 3; k++)
                if (Math.Abs(d[9 + k] - point[k]) > Tolerance) return false;
            return true;
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

        /// <summary>"(10, 20, 30 mm)": the point in the document's units, for messages.</summary>
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
