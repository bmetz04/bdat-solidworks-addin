using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
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
    ///   1. A pop-up asks for X (forward), Y (left) and Z (up), in mm unless another unit is picked (it also shows
    ///      the document's units), or with a unit typed after the number, e.g. "2 in".
    ///   2. Origin', a coordinate system placed at that point by numbers, turned so its axes are the vehicle's.
    ///   3. Three planes built from Origin', each perpendicular to one vehicle axis and named after it: X' Plane,
    ///      Y' Plane and Z' Plane.
    ///   4. All of it in a folder called "New Origin".
    /// To move the origin, edit Origin' (its X, Y, Z) and the planes follow. Running it again in the same document adds
    /// " 2", " 3"... to the names.
    /// </summary>
    public sealed class CreateOriginCommand : IBdatCommand
    {
        public string Title { get { return "Create Origin"; } }

        public string Hint { get { return "Make a new origin (Origin' coordinate system with vehicle X forward, Y left, Z up, plus X', Y', Z' planes) at a point you type in"; } }

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

            LengthUnit unit;
            double[] vehicle;
            if (!AskForPoint(swApp, DocumentUnit(doc), out vehicle, out unit)) return;

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
                // 1. Origin', placed by numbers.
                IFeature cs = MakeOrigin(doc, point, axes);
                made.Add(cs);
                result.Origin = cs.Name;

                // 2. Planes built from Origin', each perpendicular to the vehicle axis it's named after.
                string[] planeNames = { "X' Plane", "Y' Plane", "Z' Plane" };
                result.Planes = new string[3];
                result.PlaneMethods = new string[3];
                for (int i = 0; i < 3; i++)
                {
                    int swAxis = DominantAxis(axes[i][0], axes[i][1], axes[i][2]);
                    IFeature plane = MakePlane(doc, cs, bases[swAxis], swAxis, point[swAxis], planeNames[i], out result.PlaneMethods[i]);
                    made.Add(plane);
                    result.Planes[i] = plane.Name;
                }
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
        private static bool AskForPoint(ISldWorks swApp, LengthUnit documentUnit, out double[] point, out LengthUnit unit)
        {
            point = null;
            unit = Millimetres; // FUBC works in mm; numbers without a unit are mm unless another unit is picked
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
            using (var form = new CreateOriginForm(documentUnit))
            {
                if (form.ShowDialog(owner) != DialogResult.OK) return false;
                point = form.Point;
                unit = form.Unit;
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
        /// Last resort, when a plane can't be tied to Origin': a plane parallel to basePlane, offset to coordinate on the SolidWorks axis it's normal to. The offset is a
        /// normal distance you can edit later. Checked after it's made: if it went the wrong way it's remade flipped,
        /// so negative coordinates work whatever SolidWorks defaults to.
        /// </summary>
        private static IFeature MakeOffsetPlane(IModelDoc2 doc, IFeature basePlane, int axis, double coordinate, string name)
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

        /// <summary>
        /// Origin': a coordinate system placed by numbers (Edit Feature shows its X, Y, Z and rotation), so moving it is
        /// one edit. The rotation that turns SolidWorks' axes into the vehicle's is a combination of quarter turns, but
        /// how SolidWorks orders its three angles isn't documented, so likely combinations are tried first and every
        /// one is checked against the coordinate system SolidWorks actually made.
        /// </summary>
        private static IFeature MakeOrigin(IModelDoc2 doc, double[] point, double[][] axes)
        {
            MethodInfo create = typeof(IFeatureManager).GetMethod("CreateCoordinateSystemUsingNumericalValues");
            if (create == null || create.GetParameters().Length != 8)
                throw new InvalidOperationException("this needs SolidWorks 2019 or later (coordinate systems by numbers).");

            foreach (double[] angles in RotationCandidates(axes))
            {
                IFeature last = doc.FeatureByPositionReverse(0) as IFeature;
                doc.ClearSelection2(true);
                object made;
                try
                {
                    made = create.Invoke(doc.FeatureManager,
                        new object[] { true, point[0], point[1], point[2], true, angles[0], angles[1], angles[2] });
                }
                catch (TargetInvocationException)
                {
                    continue;
                }
                IFeature cs = made as IFeature ?? doc.FeatureByPositionReverse(0) as IFeature;
                if (cs == null || ReferenceEquals(cs, last) || cs.GetTypeName2() != "CoordSys") continue;
                if (IsVehicleFrame(doc, cs, point, axes))
                {
                    Rename(doc, cs, "Origin'");
                    return cs;
                }
                Delete(doc, cs);
            }
            throw new InvalidOperationException("SolidWorks didn't line the coordinate system up with X forward, Y left, Z up.");
        }

        /// <summary>
        /// Every combination of quarter turns about X, Y and Z (radians), the ones that give the vehicle axes under
        /// the usual angle orders first.
        /// </summary>
        internal static List<double[]> RotationCandidates(double[][] axes)
        {
            double[] quarter = { 0, Math.PI / 2, Math.PI, -Math.PI / 2 };
            var likely = new List<double[]>();
            var rest = new List<double[]>();
            foreach (double a in quarter)
                foreach (double b in quarter)
                    foreach (double c in quarter)
                    {
                        double[] angles = { a, b, c };
                        double[,] xyz = Multiply(Multiply(Rotation(0, a), Rotation(1, b)), Rotation(2, c));
                        double[,] zyx = Multiply(Multiply(Rotation(2, c), Rotation(1, b)), Rotation(0, a));
                        bool match = MatchesAxes(xyz, axes, false) || MatchesAxes(xyz, axes, true) ||
                                     MatchesAxes(zyx, axes, false) || MatchesAxes(zyx, axes, true);
                        (match ? likely : rest).Add(angles);
                    }
            likely.AddRange(rest);
            return likely;
        }

        /// <summary>A rotation by angle about axis 0 (X), 1 (Y) or 2 (Z), for column vectors.</summary>
        private static double[,] Rotation(int axis, double angle)
        {
            double c = Math.Round(Math.Cos(angle)), s = Math.Round(Math.Sin(angle));
            if (axis == 0) return new[,] { { 1, 0, 0 }, { 0, c, -s }, { 0, s, c } };
            if (axis == 1) return new[,] { { c, 0, s }, { 0, 1, 0 }, { -s, 0, c } };
            return new[,] { { c, -s, 0 }, { s, c, 0 }, { 0, 0, 1 } };
        }

        private static double[,] Multiply(double[,] a, double[,] b)
        {
            var m = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    for (int k = 0; k < 3; k++)
                        m[i, j] += a[i, k] * b[k, j];
            return m;
        }

        /// <summary>The matrix's columns (or rows) are the vehicle X, Y and Z.</summary>
        private static bool MatchesAxes(double[,] m, double[][] axes, bool rows)
        {
            for (int i = 0; i < 3; i++)
                for (int k = 0; k < 3; k++)
                    if (Math.Abs((rows ? m[i, k] : m[k, i]) - axes[i][k]) > 1e-9) return false;
            return true;
        }

        /// <summary>
        /// A plane through Origin', perpendicular to one vehicle axis, built from Origin' so it follows when Origin' is
        /// edited. Tries, in order: coincident with Origin's own plane (SolidWorks 2022 and later list them under the
        /// coordinate system), then through Origin' parallel to the matching Front/Top/Right plane. Only if SolidWorks
        /// takes neither does it fall back to a plain offset from that plane. Every plane is checked after it's made.
        /// </summary>
        private static IFeature MakePlane(IModelDoc2 doc, IFeature origin, IFeature basePlane, int swAxis, double coordinate,
            string name, out string how)
        {
            int coincident = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Coincident;
            int parallel = (int)swRefPlaneReferenceConstraints_e.swRefPlaneReferenceConstraint_Parallel;

            IFeature originPlane = CoordinateSystemPlane(origin, swAxis);
            if (originPlane != null)
            {
                IFeature plane = TryPlane(doc, swAxis, coordinate, delegate
                {
                    return originPlane.Select2(false, 0)
                        ? doc.FeatureManager.InsertRefPlane(coincident, 0, 0, 0, 0, 0) : null;
                });
                if (plane != null) { how = "on Origin' plane"; Rename(doc, plane, name); return plane; }
            }

            IFeature through = TryPlane(doc, swAxis, coordinate, delegate
            {
                return origin.Select2(false, 0) && basePlane.Select2(true, 1)
                    ? doc.FeatureManager.InsertRefPlane(coincident, 0, parallel, 0, 0, 0) : null;
            });
            if (through != null) { how = "through Origin'"; Rename(doc, through, name); return through; }

            how = "offset";
            return MakeOffsetPlane(doc, basePlane, swAxis, coordinate, name);
        }

        private delegate object PlaneMaker();

        /// <summary>Makes a plane with maker; keeps it only if it's at coordinate and perpendicular to swAxis.</summary>
        private static IFeature TryPlane(IModelDoc2 doc, int swAxis, double coordinate, PlaneMaker maker)
        {
            IFeature last = doc.FeatureByPositionReverse(0) as IFeature;
            object made;
            doc.ClearSelection2(true);
            try { made = maker(); }
            catch { made = null; }
            doc.ClearSelection2(true);
            if (made == null) return null;
            IFeature plane = made as IFeature ?? doc.FeatureByPositionReverse(0) as IFeature;
            if (plane == null || ReferenceEquals(plane, last) || plane.GetTypeName2() != "RefPlane") return null;
            double[] t = PlaneTransform(plane);
            if (t != null && DominantAxis(t[6], t[7], t[8]) == swAxis && Math.Abs(t[9 + swAxis] - coordinate) < Tolerance)
                return plane;
            Delete(doc, plane);
            return null;
        }

        /// <summary>The coordinate system's own plane perpendicular to swAxis, if SolidWorks lists them under it.</summary>
        private static IFeature CoordinateSystemPlane(IFeature cs, int swAxis)
        {
            try
            {
                for (IFeature sub = cs.GetFirstSubFeature() as IFeature; sub != null; sub = sub.GetNextSubFeature() as IFeature)
                {
                    if (!(sub.GetSpecificFeature2() is IRefPlane)) continue;
                    double[] t = PlaneTransform(sub);
                    if (t != null && DominantAxis(t[6], t[7], t[8]) == swAxis) return sub;
                }
            }
            catch
            {
                // Older SolidWorks: no planes under coordinate systems.
            }
            return null;
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

        /// <summary>The units the pop-up offers. mm first: it's the default.</summary>
        internal static readonly LengthUnit[] Choices =
        {
            Millimetres, new LengthUnit("cm", 0.01), new LengthUnit("m", 1.0), Inches, new LengthUnit("ft", 0.3048),
        };

        /// <summary>"millimetres", "inches"...: a unit's name for a sentence.</summary>
        internal static string LongName(LengthUnit unit)
        {
            switch (unit.Name)
            {
                case "mm": return "millimetres";
                case "cm": return "centimetres";
                case "m": return "metres";
                case "in": return "inches";
                case "ft": return "feet";
                default: return unit.Name;
            }
        }

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
        /// One coordinate as typed: a number in the picked units (mm by default), or a number with a unit after it ("2 in", "50mm").
        /// Empty means 0. Returns null and sets metres, or returns why it can't be read.
        /// </summary>
        internal static string ParseCoordinate(string text, LengthUnit defaultUnit, out double metres)
        {
            metres = 0;
            string s = (text ?? "").Trim();
            if (s.Length == 0) return null;

            LengthUnit unit = defaultUnit;
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
        internal static string ParsePoint(string x, string y, string z, LengthUnit defaultUnit, out double[] point)
        {
            point = new double[3];
            string[] typed = { x, y, z };
            string[] axes = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                string error = ParseCoordinate(typed[i], defaultUnit, out point[i]);
                if (error != null) return axes[i] + ": " + error;
            }
            return null;
        }

        /// <summary>"(10, 20, 30 mm)": the point in the picked units, for messages.</summary>
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
