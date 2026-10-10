// BDAT automated tests. Built and run by tests\run-tests.ps1; see tests\README.md.
//
// Two layers:
//   unit        No SolidWorks needed: Save MCM name/description parsing, name validation, the connector guard,
//               the toolbar command list, Create Origin coordinate parsing, Update BDAT and the version button in test mode.
//   solidworks  Drives a SolidWorks over COM: makes sample parts in a temp folder, checks the BDAT tab on the
//               BDAT that SolidWorks has loaded, and runs Murder Part and Save MCM (from the BDAT.dll under test,
//               in test mode) against the samples, and Create Origin in new parts.
//
// Nothing here ever reaches 3DEXPERIENCE: BDAT's test mode makes any connector call throw and counts it, and
// every test checks the count is still 0. The sample parts are only ever saved to the temp folder.
//
// Must stay C# 5 (built with the csc that ships with Windows).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using BDAT;
using BDAT.Commands;
using BDAT.Testing;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace BdatTests
{
    internal static class Program
    {
        // Exit codes, read by tests\gate.ps1.
        private const int ExitPassed = 0;
        private const int ExitFailed = 1;
        private const int ExitUsage = 2;
        private const int ExitSolidWorksUnavailable = 3;

        private const string BdatClsid = "{D8D33AC0-63B3-49BC-B09A-E46207CE999B}";

        private static readonly List<string> Failures = new List<string>();
        private static int _passed;
        private static int _skipped;

        [STAThread]
        private static int Main(string[] args)
        {
            var options = Options.Parse(args);
            if (options == null)
            {
                Console.WriteLine(Options.Usage);
                return ExitUsage;
            }

            Console.WriteLine("BDAT tests, BDAT.dll " + BuildInfo.Version + " from " + BuildInfo.DllPath);
            Console.WriteLine();

            RunUnitTests();

            if (options.SolidWorks)
            {
                int code = RunSolidWorksTests(options);
                if (code == ExitSolidWorksUnavailable) return Summarize(Failures.Count == 0 ? code : ExitFailed);
            }

            return Summarize(Failures.Count == 0 ? ExitPassed : ExitFailed);
        }

        private static int Summarize(int code)
        {
            Console.WriteLine();
            Console.WriteLine(_passed + " passed, " + Failures.Count + " failed, " + _skipped + " skipped.");
            foreach (string f in Failures) Console.WriteLine("  FAILED " + f);
            if (code == ExitSolidWorksUnavailable) Console.WriteLine("SolidWorks tests did not run (see above).");
            return code;
        }

        // ------------------------------------------------------------------ test plumbing

        private sealed class TestFailure : Exception
        {
            public TestFailure(string message) : base(message) { }
        }

        private sealed class TestSkipped : Exception
        {
            public TestSkipped(string message) : base(message) { }
        }

        private static void Test(string name, Action body)
        {
            TestMode.Begin();
            try
            {
                body();
                if (TestMode.ConnectorAttempts != 0)
                    throw new TestFailure("something tried to reach 3DEXPERIENCE " + TestMode.ConnectorAttempts + " time(s)");
                _passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (TestSkipped ex)
            {
                Skip(name, ex.Message);
            }
            catch (Exception ex)
            {
                string why = ex is TestFailure ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                Failures.Add(name + ": " + why);
                Console.WriteLine("FAIL " + name + ": " + why);
            }
            finally
            {
                TestMode.End();
            }
        }

        private static void Skip(string name, string why)
        {
            _skipped++;
            Console.WriteLine("SKIP " + name + ": " + why);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new TestFailure(message);
        }

        private static void Equal(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                throw new TestFailure(what + ": expected \"" + expected + "\", got \"" + actual + "\"");
        }

        private static void Near(double expected, double actual, double relTolerance, string what)
        {
            if (Math.Abs(expected - actual) > Math.Abs(expected) * relTolerance)
                throw new TestFailure(what + ": expected " + expected.ToString("G6") + ", got " + actual.ToString("G6"));
        }

        // ------------------------------------------------------------------ unit tests (no SolidWorks)

        private static void RunUnitTests()
        {
            Console.WriteLine("== Unit tests (no SolidWorks) ==");

            // Save MCM: Name = McMaster part number (before the first underscore),
            // Description = everything after it. A Murder Part copy's "_murdered" is ignored.
            var parseCases = new[]
            {
                new[] { "91251A540_Socket Head Screw", "91251A540", "Socket Head Screw" },
                new[] { "91251A540_Socket Head Screw.SLDPRT", "91251A540", "Socket Head Screw" },
                new[] { "91251A540", "91251A540", "" },
                new[] { "91251A540_Socket_Head Screw", "91251A540", "Socket_Head Screw" },
                new[] { "91251A540_Socket Head Screw_murdered", "91251A540", "Socket Head Screw" },
                new[] { "91251A540_Socket Head Screw_MURDERED", "91251A540", "Socket Head Screw" },
                new[] { "  91251A540 _  Socket Head Screw  ", "91251A540", "Socket Head Screw" },
            };
            foreach (string[] c in parseCases)
            {
                string[] tc = c;
                Test("Save MCM parses \"" + tc[0] + "\"", delegate
                {
                    // SolidWorks hands Save MCM the file name without its extension.
                    string fileName = tc[0].EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFileNameWithoutExtension(tc[0]) : tc[0];
                    Equal(tc[1], CallSaveMcmParser("DefaultName", fileName), "name");
                    Equal(tc[2], CallSaveMcmParser("DefaultDescription", fileName), "description");
                });
            }

            // BDAT embeds the SolidWorks interop types. A reference to an interop DLL ties BDAT to the exact SolidWorks
            // version it was built with, and setup fails on PCs with an older SolidWorks (RegAsm error 0x80131040).
            Test("BDAT.dll doesn't depend on a SolidWorks interop version", delegate
            {
                foreach (AssemblyName r in typeof(TestMode).Assembly.GetReferencedAssemblies())
                    Check(!r.Name.StartsWith("SolidWorks.Interop", StringComparison.OrdinalIgnoreCase),
                        "BDAT.dll references " + r.FullName + "; build it with /link, not /r");
            });

            Test("Save MCM name validation", delegate
            {
                Check(NameError("91251A540") == null, "a part number should be a valid name");
                Check(NameError("Socket Head Screw") == null, "spaces should be allowed");
                Check(NameError("") != null, "an empty name should be refused");
                Check(NameError("a/b") != null, "a slash should be refused");
                Check(NameError("a:b") != null, "a colon should be refused");
                Check(NameError("a?b") != null, "a question mark should be refused");
            });

            Test("Connector refuses to start in test mode", delegate
            {
                bool threw = false;
                try { BdatStatic("BDAT.Connector", "Find"); }
                catch (TargetInvocationException ex) { threw = ex.InnerException is InvalidOperationException; }
                Check(threw, "Connector.Find() should throw in test mode");
                Check(TestMode.ConnectorAttempts == 1, "the attempt should be counted");
                TestMode.ConnectorAttempts = 0; // expected here; every other test requires 0
            });

            // Save, bookmark and check-in all go through the connector, so if every way into it throws,
            // none of them can reach the platform from a test.
            Test("Every way into the 3DEXPERIENCE connector throws in test mode", delegate
            {
                Type connector = BdatType("BDAT.Connector");
                ConstructorInfo ctor = connector.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).FirstOrDefault();
                Check(ctor != null, "Connector has no constructor to test with");
                object c = ctor.Invoke(new object[ctor.GetParameters().Length]); // no real connector behind it

                var calls = new Dictionary<string, object[]>
                {
                    { "Manager", new object[] { "Save" } },
                    { "Call", new object[] { null, "IEnoSwSave", "SaveNoOption", new object[0] } },
                    { "Get", new object[] { null, "IEnoSwBookmarkChooser", "SelectedBookmarkId" } },
                    { "Set", new object[] { null, "IEnoSwBookmarkChooser", "DialogTitle", "x" } },
                };
                int expected = 0;
                foreach (var call in calls)
                {
                    MethodInfo m = connector.GetMethod(call.Key, BindingFlags.Instance | BindingFlags.Public);
                    if (m == null) continue; // the Save MCM code may not have this one
                    expected++;
                    bool threw = false;
                    try { m.Invoke(c, call.Value); }
                    catch (TargetInvocationException ex) { threw = ex.InnerException is InvalidOperationException; }
                    Check(threw, "Connector." + call.Key + " should throw in test mode");
                }
                Check(expected > 0, "Connector has none of Manager/Call/Get/Set");
                Check(TestMode.ConnectorAttempts == expected, "expected " + expected + " counted attempts, got " + TestMode.ConnectorAttempts);
                TestMode.ConnectorAttempts = 0;
            });

            // Check-in (unlocking the part) is the last step of Save MCM. Run it directly to prove it can't
            // reach the platform in test mode: it must be refused at the connector and report "not checked in".
            Test("Save MCM check-in can't reach 3DEXPERIENCE in test mode", delegate
            {
                // Check-in is PlatformSave.Unlock, shared by Save MCM and New from EBOM.
                Type connector = BdatType("BDAT.Connector");
                Type platformSave = BdatType("BDAT.PlatformSave");
                MethodInfo unlock = platformSave.GetMethod("Unlock",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(string) }, null);
                Check(unlock != null, "PlatformSave has no Unlock(string) check-in step");
                ConstructorInfo ctor = connector.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).First();
                object fake = ctor.Invoke(new object[ctor.GetParameters().Length]);
                object platform = Activator.CreateInstance(platformSave, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null, new object[] { null, fake, "save-mcm" }, null);

                string realLog = Path.Combine(Path.GetTempPath(), "BDAT", "save-mcm.log");
                long logBefore = File.Exists(realLog) ? new FileInfo(realLog).Length : -1;

                object checkedIn;
                try { checkedIn = unlock.Invoke(platform, new object[] { @"C:\BDAT-test\91251A540.SLDPRT" }); }
                catch (TargetInvocationException ex) { checkedIn = ex.InnerException; }
                Check(TestMode.ConnectorAttempts >= 1, "check-in didn't go through the connector guard");
                Check(!(checkedIn is bool) || !(bool)checkedIn, "check-in reported success in test mode");
                TestMode.ConnectorAttempts = 0;

                // The expected refusal stays in the test's own record, not in the user's save-mcm.log.
                long logAfter = File.Exists(realLog) ? new FileInfo(realLog).Length : -1;
                Check(logAfter == logBefore, "the test-mode refusal was written to " + realLog);
            });

            Test("Dialogs are recorded and answered in test mode", delegate
            {
                TestMode.Answers.Enqueue(false);
                Check(Ui.Show(null, "q1", "t", System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.No, "queued No");
                Check(Ui.Show(null, "q2", "t", System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Question) == System.Windows.Forms.DialogResult.Yes, "default Yes");
                Check(TestMode.Messages.Count == 2, "both questions should be recorded");
            });

            Test("Toolbar has the expected BDAT buttons, in order", delegate
            {
                List<string> titles = CommandTitles(new SwAddin());
                Check(titles.Count == 10, "expected 10 buttons, got " + titles.Count + ": " + string.Join(", ", titles.ToArray()));
                Equal("Murder Part", titles[0], "button 1");
                Equal("Save MCM", titles[1], "button 2");
                Equal("Create Origin", titles[2], "button 3");
                Equal("Open from EBOM", titles[3], "button 4");
                Equal("Waterjet DXF", titles[4], "button 5");
                Equal("Name Cut List", titles[5], "button 6");
                Equal("Check Out", titles[6], "button 7");
                Equal("Check In", titles[7], "button 8");
                Equal("Update BDAT", titles[8], "button 9");
                Check(Regex.IsMatch(titles[9], @"^BDAT (v\d+|dev build)$"), "button 10 should be the version (BDAT vN), got \"" + titles[9] + "\"");
            });

            Test("EBOM CSV is read by column heading", delegate
            {
                // Made-up rows in the EBOM's layout (headings with line breaks, columns in the sheet's order).
                string csv = "\uFEFFPerson Responsible,Class,Part Control No.,Commodity Code,\"Assembly/Part #\n (Use in Cost Report)\",Revision,Status," +
                    "\"Combined Part # \r\n(Use in 3Dx FIle Naming)\",Assembly,Area of Commodity,Sub-Assembly / Component Name\r\n" +
                    ",Assembly,10100,BR,A0101,AA,Current,BR-A0101-AA,Balance Bar,Brake System,\r\n" +
                    ",Part,10101,BR,10101,AA,Current,BR-10101-AA,,Brake System,\"Balance Bar, Wilwood \"\"BB\"\"\"\r\n" +
                    ",Part,10102,BR,10102,AA,OBSOLETE,BR-10102-AA,,Brake System,Balance Bar Sleeve\r\n" +
                    ",,,,,,,,,,\r\n" +
                    ",Assembly,21200,DT,A0212,AA,OBSOLETE,DT-A0212-AA,Carburetor,Drivetrain,\r\n" +
                    ",Part,40202,FR,40202,AA,Current,FR-40202-AA,,Frame & Body,Main Element\r\n" +
                    ",Assembly,21200,DT,A0212,AA,Current,DT-A0212-AA,Engine Mounts,Drivetrain,\r\n" +
                    ",Part,21201,DT,21201,AA,Current,DT-21201-AA,Front Right,Drivetrain,Engine Mount Spacer: 7.28 mm\r\n" +
                    ",Assembly,70400,SU,A0704,AA,Current,SU-A0704-AA,Bellcranks,Suspension,\r\n" +
                    ",Part,70501,SU,70501,AA,Current,SU-70501-AA,,Suspension,Bellcrank Bearing";
                List<EbomRow> rows = Ebom.Parse(csv);
                Check(rows.Count == 9, "expected 9 rows (blank one skipped), got " + rows.Count);
                Equal("BR-A0101-AA", rows[0].Number, "assembly number");
                Check(rows[0].IsAssembly, "first row is an assembly");
                Equal("Balance Bar", rows[0].Name, "assembly name comes from the Assembly column");
                Equal("Balance Bar, Wilwood \"BB\"", rows[1].Name, "quoted commas and quotes");
                Equal("Balance Bar", rows[1].Parent, "parent is the assembly above");
                Equal("Brake System", rows[1].Area, "area");
                Check(rows[2].IsObsolete, "OBSOLETE status");
                Equal("", rows[4].Parent, "a part isn't put under the (obsolete) assembly row above it");
                Equal("Engine Mount Spacer: 7.28 mm (Front Right)", rows[6].Name, "a part's Assembly column is a qualifier");
                Equal("Engine Mounts", rows[6].Parent, "parent by control number, obsolete assemblies ignored");

                // Assembly numbers (3DEXPERIENCE folder names) come from the control number, assembly row or not.
                Equal("A0101", rows[0].AssemblyNumber, "an assembly's own number");
                Equal("A0101", rows[1].AssemblyNumber, "a part's assembly number");
                Equal("A0101 Balance Bar", rows[1].AssemblyText, "number and name in the list");
                Equal("A0402", rows[4].AssemblyNumber, "assembly number with no assembly row in the EBOM");
                Equal("A0402", rows[4].AssemblyText, "just the number when the EBOM has no name for it");
                Equal("A0212", rows[6].AssemblyNumber, "a part's assembly number");
                Equal("A0704", rows[8].AssemblyNumber, "a part whose own assembly (A0705) has no row goes under the one above it in the sheet");
                Equal("Bellcranks", rows[8].Parent, "...named after that assembly");
                List<string> missing = Ebom.MissingAssemblies(rows);
                Check(missing.Count == 1 && missing[0] == "A0402 (Frame & Body, 1 part)",
                    "assemblies to add to the EBOM: " + string.Join("; ", missing.ToArray()));
                Equal("FR-40202-AA", Ebom.Search(rows, "a0402", false)[0].Number, "searches the assembly number");
                Equal("BR-10101-AA.SLDPRT", NewFromEbomCommand.FileName(rows[1]), "saved under the combined part number");
                Equal("BR-A0101-AA.SLDASM", NewFromEbomCommand.FileName(rows[0]), "assemblies save as .SLDASM");
                Equal("Front Bellcrank Plate, 6061", NewFromEbomDetailsForm.Clean("  Front Bellcrank Plate,\r\n6061 "),
                    "an edited description is one trimmed line");

                // The pop-up's tree: each assembly number with its current parts under it, obsolete rows left out.
                List<NewFromEbomForm.EbomGroup> groups = NewFromEbomForm.BuildGroups(rows);
                Equal("A0101,A0212,A0402,A0704", string.Join(",", groups.Select(g => g.Number).ToArray()), "groups in assembly-number order");
                Check(groups[3].Assembly == rows[7] && groups[3].Parts.Count == 1 && groups[3].Parts[0] == rows[8], "Bellcranks holds the stray A0705 part");
                Check(groups[0].Assembly == rows[0] && groups[0].Parts.Count == 1 && groups[0].Parts[0] == rows[1], "Balance Bar has its current part only");
                Check(groups[1].Assembly == rows[5], "the current assembly row heads its group, not the obsolete one");
                Check(groups[1].Parts.Count == 1 && groups[1].Parts[0] == rows[6], "Engine Mounts has its part");
                Check(groups[2].Assembly == null && groups[2].Parts.Count == 1 && groups[2].Parts[0] == rows[4], "A0402 has a header with no assembly row");
                Equal("Frame & Body", groups[2].Area, "a header with no assembly row takes its parts' area");

                Check(Ebom.Search(rows, "", false).Count == 7, "obsolete hidden by default");
                Check(Ebom.Search(rows, "", true).Count == 9, "obsolete shown when asked");
                Check(Ebom.Search(rows, "balance BR-10", true).Count == 2, "every word must match, any case");
                Equal("DT-21201-AA", Ebom.Search(rows, "drivetrain spacer", false)[0].Number, "searches area and name");

                bool refused = false;
                try { Ebom.Parse("Name,Number\r\nx,1"); }
                catch (System.IO.InvalidDataException) { refused = true; }
                Check(refused, "a CSV without the Combined Part # column is refused");
            });

            Test("EBOM folder list is read, merged and matched by assembly number", delegate
            {
                string csv = "Assembly Number,Bookmark Id,Bookmark Title\r\n" +
                    "A0704,ID704,A0704\r\n" +
                    "a0101,ID101,\"A0101 Balance Bar, front\"\r\n" +
                    ",IDX,blank number is skipped\r\n" +
                    "A0999,,blank id is skipped\r\n";
                Dictionary<string, Bookmark> team = EbomFolders.Parse(csv);
                Check(team.Count == 2, "expected 2 folders, got " + team.Count);
                Equal("ID704", team["A0704"].Id, "id by assembly number");
                Equal("ID101", team["A0101"].Id, "assembly numbers ignore case");
                Equal("A0101 Balance Bar, front", team["A0101"].Title, "quoted title");

                Dictionary<string, Bookmark> again = EbomFolders.Parse(EbomFolders.Format(team));
                Check(again.Count == 2 && again["A0101"].Title == "A0101 Balance Bar, front", "Format then Parse gives the same list");

                var picked = new Dictionary<string, Bookmark>(StringComparer.OrdinalIgnoreCase);
                picked["A0704"] = new Bookmark { Id = "NEW704", Title = "A0704" };
                picked["A0705"] = new Bookmark { Id = "ID705", Title = "A0705" };
                Dictionary<string, Bookmark> merged = EbomFolders.Merge(team, picked);
                Check(merged.Count == 3, "merge adds new picks");
                Equal("NEW704", merged["A0704"].Id, "a pick replaces the team's entry");
                Equal("ID101", merged["A0101"].Id, "team entries without a pick stay");

                Check(EbomFolders.TitleMatches("A0704", "A0704"), "exact title");
                Check(EbomFolders.TitleMatches("a0704", "A0704"), "any case");
                Check(EbomFolders.TitleMatches("A0704 Bellcranks", "A0704"), "number then name");
                Check(EbomFolders.TitleMatches("Bellcranks (A0704)", "A0704"), "name then number");
                Check(!EbomFolders.TitleMatches("A07041", "A0704"), "a longer number isn't a match");
                Check(!EbomFolders.TitleMatches("Bellcranks", "A0704"), "no number, no match");


                bool refused = false;
                try { EbomFolders.Parse("Name,Id\r\nx,1"); }
                catch (System.IO.InvalidDataException) { refused = true; }
                Check(refused, "a CSV that isn't the folder list is refused");

                // The harness runs from tests\bin, two folders below the repo.
                string teamFile = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "release", "ebom-folders.csv"));
                Check(File.Exists(teamFile) && EbomFolders.Parse(File.ReadAllText(teamFile)) != null, "the team list in the repo can be read");
                // AddToBookmark's replies (the failure is the one 3DEXPERIENCE gave on 2026-10-03).
                Check(PlatformSave.BookmarkError("{\"status\":\"success\",\"objectsAdded\":1,\"objectsAlreadyPresent\":0}") == null, "success is no error");
                Equal("You do not have security context to change the content of this Bookmark Folder.",
                    PlatformSave.BookmarkError("{\"status\":\"failure\",\"error\":\"You do not have security context to change the content of this Bookmark Folder.\"}"),
                    "a refused AddToBookmark is reported, with 3DEXPERIENCE's reason");
                Check(PlatformSave.BookmarkError(null) != null, "no reply is an error");
                Check(PlatformSave.BookmarkAdvice("You do not have security context to change it").Contains("collaborative space"), "security context advice");
            });

            Test("Save MCM folder: confirm question and name check", delegate
            {
                Check(SaveMcmCommand.IsMcMasterFolder("McMaster Carr"), "McMaster Carr");
                Check(SaveMcmCommand.IsMcMasterFolder("Mcmaster Carr"), "Mcmaster Carr (any case)");
                Check(!SaveMcmCommand.IsMcMasterFolder("Vendor CAD"), "another folder isn't McMaster Carr");
                Check(!SaveMcmCommand.IsMcMasterFolder("McMaster Carrots"), "whole words only");

                Bookmark known = new Bookmark { Id = "X", Title = "McMaster Carr" };
                Check(SaveMcmCommand.FolderQuestion("91251A540", known).StartsWith("Save 91251A540 in McMaster Carr?"),
                    "the confirm reads \"Save <name> in McMaster Carr?\"");
                Check(SaveMcmCommand.KnownBookmark() != null, "there's always a McMaster Carr folder to confirm (built in or picked)");
            });

            Test("Folder search matches assembly numbers and reads the folder tree", delegate
            {
                Check(BookmarkSearch.TitleIsFor("A0704", "A0704"), "exact title");
                Check(BookmarkSearch.TitleIsFor(" a0704 ", "A0704"), "any case, spaces trimmed");
                Check(BookmarkSearch.TitleIsFor("A0704 Bellcranks", "A0704"), "number then name");
                Check(BookmarkSearch.TitleIsFor("A0704 - Bellcranks", "A0704"), "number, dash, name");
                Check(!BookmarkSearch.TitleIsFor("A07041", "A0704"), "a longer number isn't it");
                Check(!BookmarkSearch.TitleIsFor("SU-A0704-AA", "A0704"), "a part number isn't a folder for it");
                Check(!BookmarkSearch.TitleIsFor("Bellcranks", "A0704"), "no number, no match");

                // The expand result's shape: folder entries plus From/To links (made-up ids).
                var result = new FakeExpand();
                result.Results.Add(new FakeExpandRow { ResourceId = "S", Ds6w_label = "Suspension" });
                result.Results.Add(new FakeExpandRow { ResourceId = "B", Ds6w_label = "A0704" });
                result.Results.Add(new FakeExpandRow { From = "ROOT", To = "S" });
                result.Results.Add(new FakeExpandRow { From = "S", To = "B" });
                List<FoundBookmark> tree = BookmarkSearch.Parse(result, "ROOT", "Formula UBC Racing");
                Check(tree.Count == 3, "root plus two folders, got " + tree.Count);
                FoundBookmark bell = tree.Find(b => b.Id == "B");
                Check(bell != null && bell.Title == "A0704", "folder title");
                Equal("Formula UBC Racing > Suspension > A0704", bell == null ? "" : bell.Path, "folder path from the links");

                // Where "Make folder" suggests putting A0705: beside the other A07xx folders, not the A01xx ones.
                var folders = new List<FoundBookmark>
                {
                    new FoundBookmark { Id = "ROOT", Title = "Formula UBC Racing", Path = "Formula UBC Racing" },
                    new FoundBookmark { Id = "S", Title = "Suspension", ParentId = "ROOT", Path = "Formula UBC Racing > Suspension" },
                    new FoundBookmark { Id = "BR", Title = "Brakes", ParentId = "ROOT", Path = "Formula UBC Racing > Brakes" },
                    new FoundBookmark { Id = "1", Title = "A0704", ParentId = "S" },
                    new FoundBookmark { Id = "2", Title = "A0706 Uprights", ParentId = "S" },
                    new FoundBookmark { Id = "3", Title = "A0101", ParentId = "BR" },
                    new FoundBookmark { Id = "4", Title = "A0102", ParentId = "BR" },
                    new FoundBookmark { Id = "5", Title = "A0103", ParentId = "BR" },
                };
                FoundBookmark place = BookmarkSearch.SuggestParent("A0705", folders);
                Equal("S", place == null ? "" : place.Id, "a new A07xx folder goes with the other suspension folders");
                place = BookmarkSearch.SuggestParent("A0901", folders);
                Equal("BR", place == null ? "" : place.Id, "a new system's folder goes where most assembly folders are");
                Check(BookmarkSearch.SuggestParent("A0705", folders.GetRange(0, 3)) == null, "no assembly folders yet: ask where");

                var created = new FakeCreateResult();
                created.items.Add(new FakeCreateItem { id = "NEWID", title = "A0705" });
                Equal("NEWID", BookmarkSearch.CreatedId(created, "A0705"), "the new folder's id from CreateBookmark's answer");
                Check(BookmarkSearch.CreatedId(new FakeCreateResult(), "A0705") == null, "no item, no folder");

                Equal("{\"parentId\":\"S\",\"items\":[{\"attributes\":{\"title\":\"A0705\",\"description\":\"Bellcrank \\\"Bearings\\\"\"}}]}",
                    BookmarkSearch.CreatePayload("S", "A0705", "Bellcrank \"Bearings\""), "the make-folder request carries the description, escaped");
                Equal("NEW", BookmarkSearch.CreatedIdFromJson("{\"status\":200,\"items\":[{\"id\":\"NEW\",\"title\":\"A0705\",\"description\":\"x\"}]}", "A0705"),
                    "the new folder's id from the reply");
                Check(BookmarkSearch.CreatedIdFromJson("{\"items\":[]}", "A0705") == null, "no item, no id");

                bool refusedCreate = false;
                try { string err; BookmarkSearch.Create("S", "Suspension", "A0705", out err); }
                catch (InvalidOperationException) { refusedCreate = true; }
                Check(refusedCreate, "making a folder can't reach 3DEXPERIENCE in test mode");
                TestMode.ConnectorAttempts = 0;

                bool refusedExisting = false;
                try { ExistingParts.Exists("BR-10101-AA"); }
                catch (InvalidOperationException) { refusedExisting = true; }
                Check(refusedExisting, "the 'already in 3DEXPERIENCE' check can't reach 3DEXPERIENCE in test mode");
                ExistingParts.SetForTests(new[] { "br-10101-aa" });
                Check(ExistingParts.Check(new[] { "BR-10101-AA", "BR-10102-AA" }, TimeSpan.FromMinutes(5)).Contains("BR-10101-AA"),
                    "existing numbers match whatever case 3DEXPERIENCE answers in");
                ExistingParts.SetForTests(null);
                Equal("br-10101-aa", ExistingParts.Number(" br-10101-aa.SLDPRT "), "a found model name with its extension is the number");
                Equal("BR-A0101-AA", ExistingParts.Number("BR-A0101-AA.sldasm"), "assemblies too");
                TestMode.ConnectorAttempts = 0;

                bool refused = false;
                try { BookmarkSearch.Find("A0704", new[] { "X" }); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused && TestMode.ConnectorAttempts >= 1, "the folder search can't reach 3DEXPERIENCE in test mode");
                TestMode.ConnectorAttempts = 0;
            });

            Test("Open from EBOM finds the part's id in the search reply", delegate
            {
                string reply = "{\"totalItems\":3,\"member\":[" +
                    "{\"name\":\"prd-1\",\"title\":\"BR-10101-AA\",\"id\":\"ID1\",\"type\":\"VPMReference\"}," +
                    "{\"name\":\"prd-2\",\"title\":\"BR-10101-AB\",\"id\":\"ID2\"}," +
                    "{\"name\":\"prd-3\",\"title\":\"BR-10102-AA.SLDPRT\",\"id\":\"ID3\"}]}";
                Equal("ID1", PlatformParts.IdFromSearch(reply, "br-10101-aa"), "the exact number, any case, not a near match");
                Equal("ID3", PlatformParts.IdFromSearch(reply, "BR-10102-AA"), "a title with the file extension still matches");
                Check(PlatformParts.IdFromSearch(reply, "BR-10103-AA") == null, "no match, no id");
                Check(PlatformParts.IdFromSearch("{\"member\":[{\"title\":\"X\",\"id\":\"A\"},{\"title\":\"X\",\"id\":\"B\"}]}", "X") == null,
                    "two different items with the same number: don't guess");
                Check(PlatformParts.IdFromSearch(null, "X") == null, "no reply, no id");
                Equal("resources/v1/modeler/dseng/dseng:EngItem/search?$searchStr=BR-10101-AA&$top=50", PlatformParts.SearchPath("BR-10101-AA"), "search path");

                bool refused = false;
                try { string err; PlatformParts.FindId("BR-10101-AA", out err); }
                catch (InvalidOperationException) { refused = true; }
                Check(refused, "the open search can't reach 3DEXPERIENCE in test mode");
                TestMode.ConnectorAttempts = 0;
            });

            Test("Create Origin reads coordinates", delegate
            {
                CreateOriginCommand.LengthUnit mm = CreateOriginCommand.Millimetres;
                CreateOriginCommand.LengthUnit inch = CreateOriginCommand.Inches;
                var cases = new[]
                {
                    // typed, unit picked in the pop-up, metres
                    Tuple.Create("10", mm, 0.010),
                    Tuple.Create("-2.5", mm, -0.0025),
                    Tuple.Create("", mm, 0.0),
                    Tuple.Create("  0  ", mm, 0.0),
                    Tuple.Create("1", inch, 0.0254),
                    Tuple.Create("2 in", mm, 0.0508),
                    Tuple.Create("2\"", mm, 0.0508),
                    Tuple.Create("50mm", inch, 0.050),
                    Tuple.Create("5 MM", inch, 0.005),
                    Tuple.Create("1.5 cm", mm, 0.015),
                    Tuple.Create("0.1m", mm, 0.1),
                    Tuple.Create("1 ft", mm, 0.3048),
                    Tuple.Create("1e2", mm, 0.1),
                };
                foreach (var c in cases)
                {
                    double metres;
                    string error = CreateOriginCommand.ParseCoordinate(c.Item1, c.Item2, out metres);
                    Check(error == null, "\"" + c.Item1 + "\" should be read, got: " + error);
                    Check(Math.Abs(metres - c.Item3) < 1e-12, "\"" + c.Item1 + "\" in " + c.Item2.Name + ": expected " + c.Item3 + " m, got " + metres);
                }
                foreach (string bad in new[] { "abc", "10 furlongs", "mm", "1..2", "5000 m" })
                {
                    double metres;
                    Check(CreateOriginCommand.ParseCoordinate(bad, mm, out metres) != null, "\"" + bad + "\" should be refused");
                }
                double[] point;
                string pointError = CreateOriginCommand.ParsePoint("1", "two", "3", mm, out point);
                Check(pointError != null && pointError.StartsWith("Y:"), "a bad Y should be reported as Y, got: " + pointError);
            });

            Test("Create Origin uses SolidWorks' own X, Y and Z (like a 3D sketch point)", delegate
            {
                double[][] a = CreateOriginCommand.Axes;
                for (int i = 0; i < 3; i++)
                    for (int k = 0; k < 3; k++)
                        Check(a[i][k] == (i == k ? 1 : 0), "Origin' axis " + i + " should be SolidWorks axis " + i + " (Ben, 2026-10-03)");
            });

            Test("Create Origin formats the point for messages", delegate
            {
                Equal("(10, -20.5, 0 mm)", CreateOriginCommand.PointLabel(new[] { 0.010, -0.0205, -0.0 }, CreateOriginCommand.Millimetres), "mm label");
                Equal("(1, 0.5, -2 in)", CreateOriginCommand.PointLabel(new[] { 0.0254, 0.0127, -0.0508 }, CreateOriginCommand.Inches), "inch label");
                Equal("0.3333", CreateOriginCommand.Number(0.001 / 3, CreateOriginCommand.Millimetres), "rounding");
                Equal("mm", CreateOriginCommand.UnitFor((int)swLengthUnit_e.swMM).Name, "mm part");
                Equal("in", CreateOriginCommand.UnitFor((int)swLengthUnit_e.swINCHES).Name, "inch part");
            });

            Test("Create Origin defaults to mm and names the document's units", delegate
            {
                Check(ReferenceEquals(CreateOriginCommand.Choices[0], CreateOriginCommand.Millimetres), "mm should be the first (default) unit in the pop-up");
                Equal("mm, cm, m, in, ft", string.Join(", ", CreateOriginCommand.Choices.Select(u => u.Name).ToArray()), "units offered");
                Equal("millimetres", CreateOriginCommand.LongName(CreateOriginCommand.UnitFor((int)swLengthUnit_e.swMM)), "mm document");
                Equal("inches", CreateOriginCommand.LongName(CreateOriginCommand.UnitFor((int)swLengthUnit_e.swINCHES)), "inch document");
            });

            Test("Create Origin tries the right rotation first", delegate
            {
                List<double[]> candidates = CreateOriginCommand.RotationCandidates(CreateOriginCommand.Axes);
                Check(candidates.Count == 64, "every quarter-turn combination should be tried, got " + candidates.Count);
                Check(candidates.Select(a => string.Join(",", a.Select(v => Math.Round(v, 6).ToString()).ToArray())).Distinct().Count() == 64, "no combination should be tried twice");
                Check(candidates[0].All(v => v == 0), "no rotation should be tried first: Origin' has SolidWorks' own axes");
            });

            Test("Name Cut List keeps only names in the 001 format", delegate
            {
                int n;
                Check(NameCutListCommand.IsNumbered("001", out n) && n == 1, "001 is numbered");
                Check(NameCutListCommand.IsNumbered("012", out n) && n == 12, "012 is numbered");
                Check(NameCutListCommand.IsNumbered("1234", out n) && n == 1234, "1234 is numbered");
                foreach (string name in new[] { "1", "01", "Cut-List-Item1", "001a", " 001", "0-1", "", null })
                    Check(!NameCutListCommand.IsNumbered(name, out n), "\"" + name + "\" shouldn't count as numbered");
            });

            Test("Name Cut List sorts numbered items first, by number", delegate
            {
                List<string> sorted = NameCutListCommand.SortedNames(new List<string> { "003", "Plate", "001", "010", "002", "Tube" });
                Equal("001,002,003,010,Plate,Tube", string.Join(",", sorted.ToArray()), "sorted order");
            });

            Test("Every toolbar callback exists on SwAddin", delegate
            {
                foreach (string callback in CommandCallbacks(new SwAddin()))
                {
                    MethodInfo m = typeof(SwAddin).GetMethod(callback, BindingFlags.Public | BindingFlags.Instance);
                    Check(m != null, "SolidWorks calls " + callback + "() by name but SwAddin has no public method with that name");
                }
            });

            Test("Version button shows the version and opens nothing when answered No", delegate
            {
                TestMode.Answers.Enqueue(false);
                new VersionCommand().Run(null);
                Check(TestMode.Messages.Count == 1, "expected one message, got " + TestMode.Messages.Count);
                Check(TestMode.Messages[0].Contains(BuildInfo.Version), "the message should show " + BuildInfo.Version);
            });

            Test("Update BDAT in test mode never downloads or starts the installer", delegate
            {
                string installer = Path.Combine(Path.GetTempPath(), "BDAT", "BDAT Update.bat");
                DateTime before = File.Exists(installer) ? File.GetLastWriteTimeUtc(installer) : DateTime.MinValue;
                new UpdateCommand().Run(null); // answers Yes to "Update?" if a newer version is published
                DateTime after = File.Exists(installer) ? File.GetLastWriteTimeUtc(installer) : DateTime.MinValue;
                Check(before == after, "the installer was downloaded");
                Check(TestMode.Messages.Count >= 1, "it should say something (latest / newer / offline)");
                Console.WriteLine("     (" + FirstLine(TestMode.Messages[0]) + ")");
            });
        }

        /// <summary>Save MCM's parsers belong to the Save MCM code, so they're looked up by name rather than compiled against.</summary>
        private static string CallSaveMcmParser(string method, string fileName)
        {
            MethodInfo m = BdatType("BDAT.Commands.SaveMcmCommand").GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(string) }, null);
            if (m == null) throw new TestFailure("SaveMcmCommand has no static " + method + "(string) yet");
            return (string)m.Invoke(null, new object[] { fileName });
        }

        // Save MCM is looked up by name so this harness still builds while Save MCM is being written; its tests
        // then fail with "not there yet" instead of the whole run failing to compile.
        private static Type BdatType(string fullName)
        {
            Type t = typeof(SwAddin).Assembly.GetType(fullName);
            if (t == null) throw new TestFailure("BDAT.dll has no " + fullName + " yet");
            return t;
        }

        private static object BdatStatic(string typeName, string method, params object[] args)
        {
            MethodInfo m = BdatType(typeName).GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (m == null) throw new TestFailure(typeName + " has no static " + method + "()");
            return m.Invoke(null, args);
        }

        private static string NameError(string name)
        {
            return (string)BdatStatic("BDAT.Commands.SaveMcmForm", "NameError", name);
        }

        private static IBdatCommand NewSaveMcm()
        {
            return (IBdatCommand)Activator.CreateInstance(BdatType("BDAT.Commands.SaveMcmCommand"));
        }

        private static IEnumerable<object> CommandEntries(SwAddin addin)
        {
            FieldInfo f = typeof(SwAddin).GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic);
            if (f == null) throw new TestFailure("SwAddin has no _commands list");
            return ((System.Collections.IEnumerable)f.GetValue(addin)).Cast<object>();
        }

        private static object Prop(object o, string name)
        {
            return o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(o, null);
        }

        private static List<string> CommandTitles(SwAddin addin)
        {
            return CommandEntries(addin).Select(e => ((IBdatCommand)Prop(e, "Command")).Title).ToList();
        }

        private static List<string> CommandCallbacks(SwAddin addin)
        {
            var names = new List<string>();
            foreach (object e in CommandEntries(addin))
            {
                names.Add((string)Prop(e, "Callback"));
                names.Add((string)Prop(e, "EnableCallback"));
            }
            return names;
        }

        private static string FirstLine(string s)
        {
            int nl = s.IndexOf('\n');
            return nl < 0 ? s : s.Substring(0, nl).Trim();
        }

        // ------------------------------------------------------------------ SolidWorks tests

        private static int RunSolidWorksTests(Options options)
        {
            Console.WriteLine();
            Console.WriteLine("== SolidWorks tests ==");

            MessageFilter.Register();
            Process launched = null;
            ISldWorks swApp = null;
            bool? freezeBarWasOn = null;
            string work = Path.Combine(Path.GetTempPath(), "BDAT-tests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            try
            {
                string why;
                swApp = Connect(options, out launched, out why);
                if (swApp == null)
                {
                    Console.WriteLine("Can't run the SolidWorks tests: " + why);
                    return ExitSolidWorksUnavailable;
                }
                Console.WriteLine("SolidWorks " + swApp.RevisionNumber() + (launched != null ? " (started for these tests)" : " (attached)"));

                // Save MCM turns on the freeze bar option; put the user's setting back afterwards.
                freezeBarWasOn = swApp.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUserEnableFreezeBar);

                Test("BDAT tab has every button", delegate { CheckToolbar(swApp, options); });
                Test("Create Origin: needs a part or assembly open", delegate { CreateOriginNoPartTest(swApp); });

                Directory.CreateDirectory(work);
                Samples samples = null;
                Test("Make sample parts", delegate { samples = Samples.Get(swApp, work, options.FreshSamples); });
                if (samples == null)
                {
                    Skip("Murder Part / Save MCM", "the sample parts couldn't be made");
                }
                else
                {
                    Test("Murder Part: threaded part", delegate { MurderTest(swApp, samples.Threaded, samples.ThreadedSolidVolume, 1, work); });
                    // Multi-body McMaster parts may stay multi-body (Ben, 2026-10-01): don't require a merge or any
                    // particular body count, just that no geometry is lost and the usual guarantees hold.
                    Test("Murder Part: two-body part", delegate { MurderTest(swApp, samples.TwoBody, samples.TwoBodyVolume, 0, work); });
                    Test("Murder Part: answering No changes nothing", delegate { MurderCancelTest(swApp, samples.Threaded); });
                    Test("Name Cut List: makes a weldment and names the items 001, 002", delegate { NameCutListTest(swApp, samples.TwoBody); });
                    Test("Save MCM: fills in part number and description", delegate
                    {
                        SaveMcmTest(swApp, samples.Threaded, null, null, "91251A540", "Socket Head Screw");
                    });
                    Test("Save MCM: Murder Part copy", delegate
                    {
                        SaveMcmTest(swApp, samples.Murdered, null, null, "91251A540", "Socket Head Screw");
                    });
                    Test("Save MCM: uses what was typed in the pop-up", delegate
                    {
                        SaveMcmTest(swApp, samples.Threaded, "SHCS M5", "Alloy steel socket head screw, M5 x 20 mm", "SHCS M5", "Alloy steel socket head screw, M5 x 20 mm");
                    });
                    Test("Save MCM: refuses a bad name", delegate { SaveMcmRefusedTest(swApp, samples.Threaded, "bad/name", "x", null); });
                    Test("Save MCM: empty description, answer No stops", delegate { SaveMcmRefusedTest(swApp, samples.Threaded, null, "", false); });
                    Test("Save MCM: answering No at \"Save in McMaster Carr?\" stops without the picker", delegate
                    {
                        SaveMcmRefusedTest(swApp, samples.Threaded, null, null, false);
                    });
                    Test("Save MCM: empty description, answer Yes saves without one", delegate
                    {
                        TestMode.Answers.Enqueue(true);
                        SaveMcmTest(swApp, samples.Threaded, null, "", "91251A540", "");
                    });
                }

                Test("Create Origin: makes the origin and planes at the point", delegate
                {
                    CreateOriginTest(swApp, new[] { "10mm", "-20mm", "30mm" }, new[] { 0.010, -0.020, 0.030 }, 1);
                });
                Test("Create Origin: numbers without a unit are mm", delegate
                {
                    CreateOriginTest(swApp, new[] { "40", "-5", "15" }, new[] { 0.040, -0.005, 0.015 }, 1);
                });
                Test("Create Origin: a 0 coordinate and inches", delegate
                {
                    CreateOriginTest(swApp, new[] { "0", "", "-1 in" }, new[] { 0.0, 0.0, -0.0254 }, 1);
                });
                Test("Create Origin: running twice keeps every name unique", delegate
                {
                    CreateOriginTest(swApp, new[] { "5mm", "5mm", "5mm" }, new[] { 0.005, 0.005, 0.005 }, 2);
                });
                Test("Create Origin: in an assembly", delegate
                {
                    CreateOriginTest(swApp, new[] { "-100mm", "250mm", "0.5 in" }, new[] { -0.100, 0.250, 0.0127 }, 1, true);
                });
                Test("Create Origin: Cancel makes nothing", delegate { CreateOriginNothingTest(swApp, null); });
                Test("Create Origin: a bad number makes nothing", delegate { CreateOriginNothingTest(swApp, new[] { "1", "abc", "3" }); });

                RealPartTests(swApp, options, work);
                CutListPartTests(swApp, options, work);
            }
            finally
            {
                if (swApp != null && freezeBarWasOn.HasValue)
                {
                    try { swApp.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swUserEnableFreezeBar, freezeBarWasOn.Value); } catch { }
                }
                if (swApp != null && launched != null) Shutdown(swApp, launched);
                if (!options.Keep) TryDeleteDir(work);
                else Console.WriteLine("Kept the sample parts in " + work);
                MessageFilter.Revoke();
            }
            return 0;
        }

        private static ISldWorks Connect(Options options, out Process launched, out string why)
        {
            launched = null;
            why = null;
            Process[] running = Process.GetProcessesByName("SLDWORKS");

            if (running.Length > 0)
            {
                if (!options.Attach)
                {
                    why = "SolidWorks is already running (" + Describe(running) + "). Close it, or run with -Attach to use it " +
                          "(only works when no documents are open).";
                    return null;
                }
                // SolidWorks shows the active document in its title ("SOLIDWORKS ... - [part.sldprt *]"). If there is
                // one, don't even connect: that's someone's work.
                if (!options.AllowOpenDocuments && running.Any(p => WindowTitle(p).Contains("[")))
                {
                    why = "the running SolidWorks has a document open (" + Describe(running) + "). Save and close it first; " +
                          "the tests won't run next to someone's work.";
                    return null;
                }
                ISldWorks attached = null;
                try { attached = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application"); }
                catch (COMException) { }
                if (attached == null) { why = "couldn't attach to the running SolidWorks"; return null; }
                int open = attached.GetDocumentCount();
                if (open > 0 && !options.AllowOpenDocuments)
                {
                    why = "the running SolidWorks has " + open + " document(s) open. Save and close them first; the tests won't run next to someone's work.";
                    return null;
                }
                return attached;
            }

            if (!options.Launch)
            {
                why = "SolidWorks isn't running and -Launch wasn't given.";
                return null;
            }

            string exe = options.SolidWorksExe ?? FindSolidWorksExe();
            if (exe == null || !File.Exists(exe)) { why = "couldn't find SLDWORKS.exe"; return null; }

            var start = new ProcessStartInfo(exe);
            start.UseShellExecute = false;
            // The BDAT that SolidWorks loads runs in test mode too, so nothing in this SolidWorks can save to 3DEXPERIENCE.
            start.EnvironmentVariables[TestMode.EnvironmentVariable] = "1";
            launched = Process.Start(start);

            DateTime giveUp = DateTime.Now.AddSeconds(options.StartTimeoutSeconds);
            while (DateTime.Now < giveUp)
            {
                if (launched.HasExited) { why = "SolidWorks exited while starting (code " + launched.ExitCode + ")"; return null; }
                try
                {
                    var sw = (ISldWorks)Marshal.GetActiveObject("SldWorks.Application");
                    if (sw != null && sw.StartupProcessCompleted)
                    {
                        sw.Visible = true;
                        return sw;
                    }
                }
                catch (COMException) { }
                Thread.Sleep(2000);
            }
            why = "SolidWorks didn't finish starting within " + options.StartTimeoutSeconds + " s. If it is waiting for a " +
                  "3DEXPERIENCE sign-in, the tests can't use a SolidWorks they start; open SolidWorks yourself and use -Attach.";
            return null;
        }

        private static string Describe(Process[] processes)
        {
            return string.Join("; ", processes.Select(p =>
            {
                string title = WindowTitle(p);
                return "pid " + p.Id + (title.Length > 0 ? " \"" + title + "\"" : "");
            }).ToArray());
        }

        private static string WindowTitle(Process p)
        {
            try { return p.MainWindowTitle ?? ""; }
            catch { return ""; }
        }

        private static string FindSolidWorksExe()
        {
            foreach (string root in new[] { System.Environment.GetEnvironmentVariable("ProgramFiles"), @"C:\Program Files" })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string exe = Path.Combine(root, @"SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe");
                if (File.Exists(exe)) return exe;
            }
            return null;
        }

        private static void Shutdown(ISldWorks swApp, Process launched)
        {
            try { swApp.CloseAllDocuments(true); } catch { } // only our own SolidWorks, which only has test documents
            try { swApp.ExitApp(); } catch { }
            if (!launched.WaitForExit(60000))
            {
                try { launched.Kill(); } catch { }
            }
        }

        // ---- BDAT tab

        private static void CheckToolbar(ISldWorks swApp, Options options)
        {
            object addin = swApp.GetAddInObject(BdatClsid);
            Check(addin != null, "BDAT isn't loaded in this SolidWorks (Tools > Add-Ins)");

            string dll, report;
            try
            {
                dll = (string)addin.GetType().InvokeMember("BdatTestDllPath", BindingFlags.InvokeMethod, null, addin, null);
                report = (string)addin.GetType().InvokeMember("BdatTestToolbar", BindingFlags.InvokeMethod, null, addin, null);
            }
            catch (Exception ex)
            {
                // Before publishing, SolidWorks still has the previously installed BDAT loaded; the button list of
                // the build under test is covered by the unit tests.
                string why = "the BDAT loaded in SolidWorks is an older build without test hooks (probably the installed one), " +
                    "so its tab can't be checked (" + ex.GetType().Name + ")";
                if (options.ExpectLoadedDll != null) throw new TestFailure(why);
                throw new TestSkipped(why);
            }
            Console.WriteLine("     loaded BDAT: " + dll);
            if (options.ExpectLoadedDll != null)
                Check(string.Equals(Path.GetFullPath(options.ExpectLoadedDll), dll, StringComparison.OrdinalIgnoreCase),
                    "SolidWorks loaded " + dll + ", not the build under test " + options.ExpectLoadedDll);
            // It's the build under test, in a SolidWorks started in test mode: run Murder Part inside SolidWorks,
            // the way its button does, rather than from this process.
            if (options.ExpectLoadedDll != null) _inProcessBdat = addin;

            var lines = report.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('|')).ToList();
            Check(lines.Any(l => l[0] == "group" && l[1] == "True"), "the BDAT command group isn't registered");
            Check(lines.Any(l => l[0] == "tab" && l[1] == "True"), "there's no BDAT tab for parts");

            var buttons = lines.Where(l => l.Length == 3).ToList();
            foreach (string expected in new[] { "Murder Part", "Save MCM", "Update BDAT" })
            {
                string[] b = buttons.FirstOrDefault(l => l[0] == expected);
                Check(b != null, "no " + expected + " button");
                Check(b[2] == "True", expected + " isn't on the BDAT tab");
            }
            string[] version = buttons.FirstOrDefault(l => Regex.IsMatch(l[0], @"^BDAT (v\d+|dev build)$"));
            Check(version != null, "no BDAT version button");
            Check(version[2] == "True", version[0] + " isn't on the BDAT tab");
        }

        // ---- Murder Part

        /// <summary>The BDAT loaded in SolidWorks when it's the build under test, else null.</summary>
        private static object _inProcessBdat;

        /// <summary>
        /// Runs Murder Part with the queued answers: inside SolidWorks when the build under test is loaded there,
        /// otherwise from this process. Either way its messages end up in TestMode.Messages.
        /// </summary>
        private static void RunMurderPart(ISldWorks swApp)
        {
            if (_inProcessBdat == null)
            {
                new MurderPartCommand().Run(swApp);
                return;
            }
            string answers = new string(TestMode.Answers.Select(a => a ? 'y' : 'n').ToArray());
            TestMode.Answers.Clear();
            string messages = (string)_inProcessBdat.GetType().InvokeMember("BdatTestRun", BindingFlags.InvokeMethod, null,
                _inProcessBdat, new object[] { "Murder Part", answers });
            if (!string.IsNullOrEmpty(messages)) TestMode.Messages.AddRange(messages.Split('\u001e'));
        }

        private static void MurderTest(ISldWorks swApp, string partPath, double expectedVolume, int expectedBodies, string work)
        {
            string before = Snapshot(partPath);
            IModelDoc2 original = Open(swApp, partPath);
            // After opening: SolidWorks puts its own ~$ lock file beside an open part.
            string[] folderBefore = Directory.GetFiles(Path.GetDirectoryName(partPath));
            List<string> featuresBefore = FeatureNames(original);
            int docsBefore = swApp.GetDocumentCount();
            IModelDoc2 result = null;
            try
            {
                RunMurderPart(swApp);
                result = swApp.ActiveDoc as IModelDoc2;

                Check(TestMode.Messages.Count >= 2, "expected a confirmation and a summary, got " + TestMode.Messages.Count + " message(s)");
                // Not checking the summary wording (how bodies are reported may change); a real failure means no
                // new part opens, which the next check catches.
                Check(result != null && !ReferenceEquals(result, original) && result.GetTitle() != original.GetTitle(),
                    "no new part was opened");
                Check(swApp.GetDocumentCount() == docsBefore + 1, "expected exactly one new document");
                Check(string.IsNullOrEmpty(result.GetPathName()), "the result should be unsaved, but it's at " + result.GetPathName());

                object[] bodies = ((IPartDoc)result).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                int count = bodies == null ? 0 : bodies.Length;
                // expectedBodies 0: any number of bodies is fine (multi-body parts may or may not be merged).
                Check(expectedBodies == 0 ? count >= 1 : count == expectedBodies,
                    "the result should have " + (expectedBodies == 0 ? "at least 1" : expectedBodies.ToString()) + " solid bod" + (expectedBodies == 1 ? "y" : "ies") + ", it has " + count + ". Murder Part said: " + TestMode.Messages.Last().Replace("\n", " "));
                if (expectedVolume > 0) Near(expectedVolume, Volume(result), 0.01, "result volume (thread geometry should be gone)");
                else Check(Volume(result) > 0, "the result has no solid volume");
                Check(!FeatureTypes(result).Contains("CosmeticThread"), "the result still has a cosmetic thread");

                // The original is untouched: same file, same features, not even marked as changed.
                Equal(before, Snapshot(partPath), "original file (size/time/hash)");
                Check(!original.GetSaveFlag(), "the original part was modified in SolidWorks");
                Check(featuresBefore.SequenceEqual(FeatureNames(original)), "the original part's feature tree changed");

                // No leftovers beside the part or in BDAT's temp folder.
                string[] folderAfter = Directory.GetFiles(Path.GetDirectoryName(partPath));
                var extra = folderAfter.Except(folderBefore, StringComparer.OrdinalIgnoreCase).ToList();
                Check(extra.Count == 0, "Murder Part left files beside the part: " + string.Join(", ", extra.Select(Path.GetFileName).ToArray()));
                string murderTemp = Path.Combine(Path.GetTempPath(), "BDAT", "murder");
                string[] leftovers = Directory.Exists(murderTemp) ? Directory.GetFiles(murderTemp) : new string[0];
                Check(leftovers.Length == 0, "temporary files left in " + murderTemp + ": " + string.Join(", ", leftovers.Select(Path.GetFileName).ToArray()));
            }
            finally
            {
                if (result != null && !ReferenceEquals(result, original)) swApp.CloseDoc(result.GetTitle());
                swApp.CloseDoc(original.GetTitle());
            }
        }

        private static void MurderCancelTest(ISldWorks swApp, string partPath)
        {
            string before = Snapshot(partPath);
            IModelDoc2 original = Open(swApp, partPath);
            int docsBefore = swApp.GetDocumentCount();
            try
            {
                TestMode.Answers.Enqueue(false);
                RunMurderPart(swApp);
                Check(TestMode.Messages.Count == 1, "expected only the confirmation, got " + TestMode.Messages.Count + " message(s)");
                Check(swApp.GetDocumentCount() == docsBefore, "a document was opened after answering No");
                Check(!original.GetSaveFlag(), "the original part was modified");
                Equal(before, Snapshot(partPath), "original file");
            }
            finally
            {
                swApp.CloseDoc(original.GetTitle());
            }
        }

        // ---- Name Cut List

        /// <summary>
        /// The two-body sample has no cut list, so Name Cut List must make it a weldment and name its two items
        /// 001 and 002. A second run must rename nothing (both already have numbers). Then one item is given another
        /// name, and a third run must number only that one, as 003. With another feature called 003 the next number
        /// skips to 004. Nothing is saved.
        /// </summary>
        private static void NameCutListTest(ISldWorks swApp, string partPath)
        {
            string before = Snapshot(partPath);
            IModelDoc2 doc = Open(swApp, partPath);
            try
            {
                Check(!FeatureTypes(doc).Contains("CutListFolder"), "the sample already has a cut list");
                var command = new NameCutListCommand();
                Check(command.IsEnabled(swApp), "Name Cut List should be clickable with a part open");

                RunNameCutList(swApp, command, "001,002", "001,002", "run 1");
                Check(FeatureTypes(doc).Contains("WeldmentFeature"), "the part wasn't made a weldment");
                RunNameCutList(swApp, command, "", "001,002", "run 2");

                IFeature first = CutListItems(doc).First();
                first.Name = "Plate";
                RunNameCutList(swApp, command, "003", "002,003", "run 3");

                // A number another feature holds is skipped: with the Weldment feature called 003, Plate gets 004.
                // Free 003 first: SolidWorks quietly refuses a name another feature already has.
                IFeature weldment = Features(doc).First(f => f.GetTypeName2() == "WeldmentFeature");
                first.Name = "Plate";
                weldment.Name = "003";
                Equal("003", weldment.Name, "Weldment feature renamed");
                RunNameCutList(swApp, command, "004", "002,004", "run 4");
                Check(!TestMode.Messages.Any(), "Name Cut List shouldn't pop anything up when it works: " + string.Join(" | ", TestMode.Messages.ToArray()));

            }
            finally
            {
                swApp.CloseDoc(doc.GetTitle());
            }
            Equal(before, Snapshot(partPath), "sample file (Name Cut List must not save)");
        }

        private static void RunNameCutList(ISldWorks swApp, NameCutListCommand command, string expectNamed, string expectTree, string run)
        {
            IModelDoc2 doc = (IModelDoc2)swApp.ActiveDoc;
            TestMode.LastNameCutList = null;
            TestMode.Messages.Clear();
            command.Run(swApp);
            string said = string.Join(" | ", TestMode.Messages.ToArray());
            Check(TestMode.LastNameCutList != null, run + " stopped early: " + said);
            Equal(expectNamed, string.Join(",", TestMode.LastNameCutList.ToArray()), run + " names given");
            List<string> names = CutListItems(doc).Select(f => f.Name).ToList();
            Equal(expectTree, string.Join(",", names.ToArray()), run + " cut list items in the tree");
        }

        // ---- Save MCM

        // ---- Create Origin

        private static void CreateOriginNoPartTest(ISldWorks swApp)
        {
            Check(swApp.GetDocumentCount() == 0, "this test needs SolidWorks with nothing open");
            var command = new CreateOriginCommand();
            Check(!command.IsEnabled(swApp), "Create Origin should be greyed out with nothing open");
            TestMode.OriginCoordinates = new[] { "1", "2", "3" };
            command.Run(swApp);
            Check(TestMode.LastCreateOrigin == null, "Create Origin made something with no part open");
            Check(TestMode.Messages.Count == 1 && TestMode.Messages[0].Contains("Open a part"), "it should say to open a part, got: " + string.Join(" | ", TestMode.Messages.ToArray()));
        }

        /// <summary>
        /// Runs Create Origin runs times in a new part (or assembly) with the typed coordinates, then checks what
        /// it made the last time (see CheckOrigin), and that every plane is built on Origin' so moving Origin' moves them.
        /// </summary>
        private static void CreateOriginTest(ISldWorks swApp, string[] typed, double[] expected, int runs, bool assembly = false)
        {
            IModelDoc2 doc = assembly ? Samples.NewAssembly(swApp) : Samples.NewPart(swApp);
            try
            {
                var command = new CreateOriginCommand();
                Check(command.IsEnabled(swApp), "Create Origin should be enabled with a" + (assembly ? "n assembly" : " part") + " open");
                for (int run = 1; run <= runs; run++)
                {
                    TestMode.LastCreateOrigin = null;
                    TestMode.OriginCoordinates = typed;
                    command.Run(swApp);
                    Check(TestMode.LastCreateOrigin != null, "nothing was made (run " + run + "): " + string.Join(" | ", TestMode.Messages.ToArray()));
                }
                CreateOriginTestResult r = TestMode.LastCreateOrigin;
                Check(TestMode.Messages.Count == 0, "it shouldn't need to say anything, but said: " + string.Join(" | ", TestMode.Messages.ToArray()));

                string suffix = runs > 1 ? " " + runs : "";
                string[] planeNames = { "X' Plane", "Y' Plane", "Z' Plane" };
                for (int i = 0; i < 3; i++)
                {
                    Equal(planeNames[i] + suffix, r.Planes[i], "plane " + (i + 1) + " name");
                    Check(r.PlaneMethods[i] != "offset", r.Planes[i] + " isn't built on Origin' (it's an offset from a standard plane), so it won't follow Origin' when it moves");
                }
                Equal("Origin'" + suffix, r.Origin, "coordinate system name");
                Equal("New Origin" + suffix, r.Folder, "folder name");
                IFeature folder = CreateOriginCommand.FeatureByName(doc, r.Folder);
                Check(folder != null && folder.GetTypeName2() == "FtrFolder", "no \"" + r.Folder + "\" folder in the tree");

                // A point at (50, 50, 50) mm (SolidWorks coordinates) to measure the planes from.
                const double p = 0.050;
                doc.ClearSelection2(true);
                doc.SketchManager.Insert3DSketch(true);
                SketchPoint probe = doc.SketchManager.CreatePoint(p, p, p);
                doc.SketchManager.Insert3DSketch(true);
                Check(probe != null, "couldn't make the 3D sketch point to measure from");

                CheckOrigin(doc, r, expected, probe, p, "");
            }
            finally
            {
                swApp.CloseDoc(doc.GetTitle()); // never saved
            }
        }

        /// <summary>
        /// Origin' is at expected (SolidWorks coordinates, as a 3D sketch point shows them), Origin' has the part's own
        /// X, Y and Z, and each plane goes through the point on the right side of the origin: measured from the
        /// probe at (p, p, p) with SolidWorks' Measure tool, independently of how BDAT places them.
        /// </summary>
        private static void CheckOrigin(IModelDoc2 doc, CreateOriginTestResult r, double[] expected, SketchPoint probe, double p, string when)
        {
            double[] at = expected;
            string atText = "(" + (at[0] * 1000).ToString("0.###") + ", " + (at[1] * 1000).ToString("0.###") + ", " + (at[2] * 1000).ToString("0.###") + ") mm";

            IFeature cs = CreateOriginCommand.FeatureByName(doc, r.Origin);
            Check(cs != null && cs.GetTypeName2() == "CoordSys", "no \"" + r.Origin + "\" coordinate system in the tree");
            MathTransform csTransform = doc.Extension.GetCoordinateSystemTransformByName(r.Origin) as MathTransform;
            double[] d = csTransform == null ? null : csTransform.ArrayData as double[];
            Check(d != null && d.Length >= 12, "couldn't read where " + r.Origin + " is");
            for (int i = 0; i < 3; i++)
                Check(Math.Abs(d[9 + i] - at[i]) < 1e-7, r.Origin + when + " is at (" + (d[9] * 1000).ToString("0.###") + ", " +
                    (d[10] * 1000).ToString("0.###") + ", " + (d[11] * 1000).ToString("0.###") + ") mm, expected " + atText);
            double[] frame = { 1, 0, 0, 0, 1, 0, 0, 0, 1 }; // the part's own X, Y, Z
            for (int i = 0; i < 9; i++)
                Check(Math.Abs(d[i] - frame[i]) < 1e-6, r.Origin + when + " isn't lined up with the part's X, Y, Z: its axes are " +
                    string.Join(", ", d.Take(9).Select(v => Math.Round(v, 3).ToString()).ToArray()));

            int[] modelAxisOf = { 0, 1, 2 }; // X' Plane is at model X, Y' at Y, Z' at Z
            for (int i = 0; i < 3; i++)
            {
                IFeature plane = CreateOriginCommand.FeatureByName(doc, r.Planes[i]);
                Check(plane != null && plane.GetTypeName2() == "RefPlane", "no plane called \"" + r.Planes[i] + "\"");
                double c = at[modelAxisOf[i]];
                double want = Math.Abs(p - c);
                double got = Distance(doc, plane, probe);
                Check(Math.Abs(want - got) < 1e-7, r.Planes[i] + when + " is " + (got * 1000).ToString("0.###") + " mm from (50, 50, 50) mm, expected " +
                    (want * 1000).ToString("0.###") + " mm" + (Math.Abs(p + c - got) < 1e-7 ? " (it's on the wrong side of the origin)" : ""));
            }
        }

        private static void CreateOriginNothingTest(ISldWorks swApp, string[] typed)
        {
            IModelDoc2 doc = Samples.NewPart(swApp);
            try
            {
                int before = FeatureNames(doc).Count;
                TestMode.OriginCoordinates = typed;
                new CreateOriginCommand().Run(swApp);
                Check(TestMode.LastCreateOrigin == null, "something was made");
                Check(FeatureNames(doc).Count == before, "the feature tree changed");
                if (typed != null) Check(TestMode.Messages.Count == 1 && TestMode.Messages[0].Contains("Y:"), "it should say Y isn't a number, got: " + string.Join(" | ", TestMode.Messages.ToArray()));
                else Check(TestMode.Messages.Count == 0, "Cancel shouldn't show anything");
            }
            finally
            {
                swApp.CloseDoc(doc.GetTitle());
            }
        }

        /// <summary>SolidWorks' Measure tool: the distance between a plane and a point.</summary>
        private static double Distance(IModelDoc2 doc, IFeature plane, SketchPoint point)
        {
            doc.ClearSelection2(true);
            Check(plane.Select2(false, 0), "couldn't select " + plane.Name);
            Check(point.Select4(true, null), "couldn't select the 3D sketch point");
            Measure measure = doc.Extension.CreateMeasure();
            bool ok = measure.Calculate(null);
            double d = measure.Distance;
            if (d < 0) d = measure.NormalDistance;
            doc.ClearSelection2(true);
            Check(ok && d >= 0, "couldn't measure from " + plane.Name + " to the point");
            return d;
        }

        // ---- Real parts from the user's test-parts library

        /// <summary>
        /// Runs the checks that hold for any McMaster part on every .SLDPRT in the test-parts folder. The parts are
        /// copied to this run's temp folder first and only the copies are opened, so the library is never touched.
        /// </summary>
        private static void RealPartTests(ISldWorks swApp, Options options, string work)
        {
            if (string.IsNullOrEmpty(options.PartsDir) || !Directory.Exists(options.PartsDir))
            {
                Skip("Real parts", "no test-parts folder" + (options.PartsDir == null ? "" : " at " + options.PartsDir));
                return;
            }
            string cutListDir = Path.GetFullPath(Path.Combine(options.PartsDir, CutListPartsFolder)) + Path.DirectorySeparatorChar;
            string[] parts = Directory.GetFiles(options.PartsDir, "*.sldprt", SearchOption.AllDirectories)
                .Where(f => !Path.GetFileName(f).StartsWith("~$"))
                .Where(f => !Path.GetFullPath(f).StartsWith(cutListDir, StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f).ToArray();
            if (parts.Length == 0)
            {
                Skip("Real parts", options.PartsDir + " has no .SLDPRT files yet");
                return;
            }

            foreach (string original in parts)
            {
                string name = Path.GetFileName(original);
                // One folder per part, so "nothing left beside the part" only sees this part.
                string dir = Path.Combine(work, "real", Path.GetFileNameWithoutExtension(name));
                Directory.CreateDirectory(dir);
                string copy = Path.Combine(dir, name);
                File.Copy(original, copy);
                File.SetAttributes(copy, FileAttributes.Normal);
                string libraryBefore = Snapshot(original);

                Test("Real part " + name + ": Murder Part", delegate { MurderTest(swApp, copy, 0, 0, work); });
                Test("Real part " + name + ": Save MCM", delegate
                {
                    string baseName = Path.GetFileNameWithoutExtension(name);
                    SaveMcmTest(swApp, copy, null, null,
                        CallSaveMcmParser("DefaultName", baseName), CallSaveMcmParser("DefaultDescription", baseName));
                });
                Test("Real part " + name + ": library file untouched", delegate { Equal(libraryBefore, Snapshot(original), "library file"); });
            }
        }

        // ---- Cut list parts from the user's test-parts library

        /// <summary>Weldment and sheet metal test parts live in this subfolder of test-parts, apart from the McMaster parts.</summary>
        private const string CutListPartsFolder = "cut-list";

        /// <summary>
        /// Runs Name Cut List and Waterjet DXF on every .SLDPRT in test-parts\cut-list (the tricky parts made for these
        /// buttons). Each runs on its own fresh copy; the saved part is never opened. Nothing is saved.
        /// </summary>
        private static void CutListPartTests(ISldWorks swApp, Options options, string work)
        {
            string dir = string.IsNullOrEmpty(options.PartsDir) ? null : Path.Combine(options.PartsDir, CutListPartsFolder);
            if (dir == null || !Directory.Exists(dir))
            {
                Skip("Cut list parts", "no " + CutListPartsFolder + " folder in test-parts");
                return;
            }
            string[] parts = Directory.GetFiles(dir, "*.sldprt").Where(f => !Path.GetFileName(f).StartsWith("~$")).OrderBy(f => f).ToArray();
            if (parts.Length == 0)
            {
                Skip("Cut list parts", dir + " has no .SLDPRT files yet");
                return;
            }

            foreach (string original in parts)
            {
                string name = Path.GetFileName(original);
                string libraryBefore = Snapshot(original);
                Test("Cut list part " + name + ": Name Cut List", delegate { NameCutListPartTest(swApp, CopyForTest(original, work, "namecutlist")); });
                Test("Cut list part " + name + ": Waterjet DXF", delegate { WaterjetPartTest(swApp, CopyForTest(original, work, "waterjet"), work); });
                Test("Cut list part " + name + ": library file untouched", delegate { Equal(libraryBefore, Snapshot(original), "library file"); });
            }
        }

        private static string CopyForTest(string original, string work, string purpose)
        {
            string dir = Path.Combine(work, "cut-list", Path.GetFileNameWithoutExtension(original) + "-" + purpose);
            Directory.CreateDirectory(dir);
            string copy = Path.Combine(dir, Path.GetFileName(original));
            File.Copy(original, copy, true);
            File.SetAttributes(copy, FileAttributes.Normal);
            return copy;
        }

        /// <summary>
        /// After Name Cut List every cut list item has a number, no number is used twice, and the cut list is in number
        /// order. It must say nothing when it all worked. A second run must change nothing.
        /// </summary>
        private static void NameCutListPartTest(ISldWorks swApp, string partPath)
        {
            string before = Snapshot(partPath);
            IModelDoc2 doc = Open(swApp, partPath);
            try
            {
                var command = new NameCutListCommand();
                TestMode.LastNameCutList = null;
                TestMode.Messages.Clear();
                command.Run(swApp);
                string said = string.Join(" | ", TestMode.Messages.ToArray());
                Check(TestMode.LastNameCutList != null, "Name Cut List stopped early: " + said);
                Check(TestMode.Messages.Count == 0, "Name Cut List said something, so part of it didn't work: " + said);

                List<string> names = CutListItems(doc).Select(f => f.Name).ToList();
                Check(names.Count > 0, "the part has no cut list items after Name Cut List");
                int n;
                List<string> unnumbered = names.Where(x => !NameCutListCommand.IsNumbered(x, out n)).ToList();
                Check(unnumbered.Count == 0, "items left without a number: " + string.Join(", ", unnumbered.ToArray()));
                List<string> twice = names.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
                Check(twice.Count == 0, "numbers used twice: " + string.Join(", ", twice.ToArray()));
                Equal(string.Join(",", NameCutListCommand.SortedNames(names).ToArray()), string.Join(",", names.ToArray()), "cut list order");

                TestMode.LastNameCutList = null;
                command.Run(swApp);
                Check(TestMode.LastNameCutList != null && TestMode.LastNameCutList.Count == 0,
                    "a second run renamed " + (TestMode.LastNameCutList == null ? "(stopped early)" : string.Join(", ", TestMode.LastNameCutList.ToArray())));
                Equal(string.Join(",", names.ToArray()), string.Join(",", CutListItems(doc).Select(f => f.Name).ToArray()), "cut list after a second run");
            }
            finally
            {
                swApp.CloseDoc(doc.GetTitle());
            }
            Equal(before, Snapshot(partPath), "part file (Name Cut List must not save)");
        }

        /// <summary>
        /// Waterjet DXF must save at least one DXF, report no body it couldn't export, write only the files it lists,
        /// and leave the part unchanged.
        /// </summary>
        private static void WaterjetPartTest(ISldWorks swApp, string partPath, string work)
        {
            string before = Snapshot(partPath);
            string outDir = Path.Combine(Path.GetDirectoryName(partPath), "dxf");
            Directory.CreateDirectory(outDir);
            IModelDoc2 doc = Open(swApp, partPath);
            try
            {
                TestMode.LastWaterjet = null;
                TestMode.Messages.Clear();
                TestMode.WaterjetFolder = outDir;
                new WaterjetDxfCommand().Run(swApp);
                string said = string.Join(" | ", TestMode.Messages.ToArray());
                Console.WriteLine("     " + said.Replace("\r", "").Replace("\n", " / "));
                Check(TestMode.LastWaterjet != null, "Waterjet DXF stopped early: " + said);
                Check(TestMode.LastWaterjet.Count > 0, "no DXFs were saved: " + said);
                Check(!said.Contains("Couldn't export"), "some bodies couldn't be exported: " + said);
                string[] files = Directory.GetFiles(outDir, "*.dxf", SearchOption.AllDirectories);
                Check(files.Length == TestMode.LastWaterjet.Count,
                    "it reported " + TestMode.LastWaterjet.Count + " DXFs but wrote " + files.Length);
                foreach (string f in files) Check(new FileInfo(f).Length > 0, Path.GetFileName(f) + " is empty");
            }
            finally
            {
                TestMode.WaterjetFolder = null;
                swApp.CloseDoc(doc.GetTitle());
            }
            Equal(before, Snapshot(partPath), "part file (Waterjet DXF must not save)");
        }

        private static void SaveMcmTest(ISldWorks swApp, string partPath, string typedName, string typedDescription,
            string expectedName, string expectedDescription)
        {
            string before = Snapshot(partPath);
            IModelDoc2 doc = Open(swApp, partPath);
            try
            {
                IBdatCommand command = NewSaveMcm();
                Check(command.IsEnabled(swApp), "Save MCM should be enabled with a part open");
                TestMode.SaveMcmName = typedName;
                TestMode.SaveMcmDescription = typedDescription;

                // Note what isometric looks like, then turn the part to the front so the command has to change it.
                double[] isometric = ViewRotation(doc, "*Isometric", swStandardViews_e.swIsometricView);
                double[] front = ViewRotation(doc, "*Front", swStandardViews_e.swFrontView);
                Check(!SameRotation(isometric, front), "couldn't set up the view check");

                command.Run(swApp);

                SaveMcmTestResult r = TestMode.LastSaveMcm;
                Check(r != null, "Save MCM stopped early: " + string.Join(" | ", TestMode.Messages.ToArray()));
                Equal(expectedName, r.Name, "name");
                Equal(expectedDescription, r.Description, "description");
                Check(r.Destination.EndsWith("McMaster Carr", StringComparison.OrdinalIgnoreCase), "destination should be the McMaster Carr bookmark, got " + r.Destination);

                // Steps: the local ones ran, the platform ones (including check-in) were skipped.
                List<string> steps = StepsOf(r);
                string stepList = string.Join(", ", steps.ToArray());
                Check(steps.Contains("isometric"), "the view wasn't set to isometric (steps: " + stepList + ")");
                Check(steps.Contains("freeze"), "the feature tree wasn't frozen (steps: " + stepList + ")");
                Check(steps.Contains("skipped: check in"), "check-in should be listed as skipped in test mode (steps: " + stepList + ")");
                Check(!steps.Any(s => s.IndexOf("check", StringComparison.OrdinalIgnoreCase) >= 0 && !s.StartsWith("skipped:")),
                    "a check-in step ran in test mode (steps: " + stepList + ")");

                Check(SameRotation(isometric, CurrentRotation(doc)), "the part isn't shown in the isometric view");
                CheckFrozenToEnd(doc);

                // The file-level property (CAD Family) and each configuration's (Physical Product).
                var scopes = new List<string> { "" };
                string[] configs = doc.GetConfigurationNames() as string[];
                if (configs != null) scopes.AddRange(configs);
                foreach (string scope in scopes)
                {
                    string where = scope.Length == 0 ? "file" : "configuration \"" + scope + "\"";
                    string property = DescriptionProperty(doc, scope);
                    if (expectedDescription.Length > 0) Equal(expectedDescription, property, "Description custom property (" + where + ")");
                    else Check(string.IsNullOrEmpty(property), "an empty description shouldn't set the " + where + " property");
                }

                Equal(before, Snapshot(partPath), "the part file (test mode must not save)");
            }
            finally
            {
                swApp.CloseDoc(doc.GetTitle()); // closes without saving
            }
        }

        /// <summary>SaveMcmTestResult.Steps, or nothing if this BDAT.dll predates it.</summary>
        private static List<string> StepsOf(SaveMcmTestResult r)
        {
            FieldInfo f = typeof(SaveMcmTestResult).GetField("Steps");
            object steps = f == null ? null : f.GetValue(r);
            return steps == null ? new List<string>() : ((IEnumerable<string>)steps).ToList();
        }

        private static double[] ViewRotation(IModelDoc2 doc, string viewName, swStandardViews_e view)
        {
            doc.ShowNamedView2(viewName, (int)view);
            return CurrentRotation(doc);
        }

        private static double[] CurrentRotation(IModelDoc2 doc)
        {
            IModelView view = doc.ActiveView as IModelView;
            if (view == null) throw new TestFailure("the part has no view");
            double[] data = ((MathTransform)view.Orientation3).ArrayData as double[];
            return data.Take(9).ToArray();
        }

        private static bool SameRotation(double[] a, double[] b)
        {
            for (int i = 0; i < 9; i++)
                if (Math.Abs(a[i] - b[i]) > 1e-4) return false;
            return true;
        }

        /// <summary>The freeze bar is at the end: every feature that makes geometry is frozen.</summary>
        private static void CheckFrozenToEnd(IModelDoc2 doc)
        {
            Check(doc.FeatureManager.GetFreezeLocation() != null,
                "there's no freeze bar position (is Tools > Options > General > Enable Freeze bar on?)");
            var geometry = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Extrusion", "Boss", "BaseBody", "Cut", "ICE", "CosmeticThread" };
            var modelling = Features(doc).Where(f => geometry.Contains(f.GetTypeName2())).ToList();
            Check(modelling.Count > 0, "the sample part has no features to freeze");
            var unfrozen = modelling.Where(f => !f.IsFrozen()).Select(f => f.Name).ToList();
            Check(unfrozen.Count == 0, "the freeze bar isn't at the end of the tree; not frozen: " + string.Join(", ", unfrozen.ToArray()));
        }

        private static void SaveMcmRefusedTest(ISldWorks swApp, string partPath, string typedName, string typedDescription, bool? answer)
        {
            IModelDoc2 doc = Open(swApp, partPath);
            try
            {
                TestMode.SaveMcmName = typedName;
                TestMode.SaveMcmDescription = typedDescription;
                if (answer.HasValue) TestMode.Answers.Enqueue(answer.Value);
                NewSaveMcm().Run(swApp);
                Check(TestMode.LastSaveMcm == null, "Save MCM should have stopped, but would have saved \"" +
                    (TestMode.LastSaveMcm == null ? "" : TestMode.LastSaveMcm.Name) + "\"");
                Check(TestMode.Messages.Count >= 1, "it should have told the user why");
                Check(string.IsNullOrEmpty(DescriptionProperty(doc)), "the Description property shouldn't be set");
            }
            finally
            {
                swApp.CloseDoc(doc.GetTitle());
            }
        }

        private static string DescriptionProperty(IModelDoc2 doc, string configuration = "")
        {
            CustomPropertyManager props = doc.Extension.get_CustomPropertyManager(configuration);
            string value, resolved;
            bool wasResolved;
            props.Get5("Description", false, out value, out resolved, out wasResolved);
            return value ?? "";
        }

        // ---- SolidWorks helpers

        private static IModelDoc2 Open(ISldWorks swApp, string path)
        {
            int errors = 0, warnings = 0;
            var doc = swApp.OpenDoc6(path, (int)swDocumentTypes_e.swDocPART, (int)swOpenDocOptions_e.swOpenDocOptions_Silent,
                "", ref errors, ref warnings) as IModelDoc2;
            if (doc == null) throw new TestFailure("couldn't open " + path + " (error " + errors + ")");
            int activateErrors = 0;
            swApp.ActivateDoc3(doc.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref activateErrors);
            return doc;
        }

        internal static double Volume(IModelDoc2 doc)
        {
            double total = 0;
            object[] bodies = ((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            if (bodies == null) return 0;
            foreach (object b in bodies)
            {
                double[] props = ((IBody2)b).GetMassProperties(1.0) as double[];
                if (props != null && props.Length > 3) total += props[3];
            }
            return total;
        }

        private static List<string> FeatureNames(IModelDoc2 doc)
        {
            return Features(doc).Select(f => f.Name).ToList();
        }

        internal static List<string> FeatureTypes(IModelDoc2 doc)
        {
            return Features(doc).Select(f => f.GetTypeName2()).ToList();
        }

        /// <summary>
        /// The cut list items, read from the Solid Bodies (cut list) folder. SolidWorks also lists each item as a
        /// top-level feature in a weldment, so Features() sees every item twice.
        /// </summary>
        private static List<IFeature> CutListItems(IModelDoc2 doc)
        {
            var items = new List<IFeature>();
            foreach (IFeature folder in Features(doc).Where(f => f.GetTypeName2() == "SolidBodyFolder"))
            {
                IFeature sub = folder.GetFirstSubFeature() as IFeature;
                while (sub != null)
                {
                    if (sub.GetTypeName2() == "CutListFolder") items.Add(sub);
                    sub = sub.GetNextSubFeature() as IFeature;
                }
            }
            return items;
        }

        private static List<IFeature> Features(IModelDoc2 doc)
        {
            var all = new List<IFeature>();
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null)
            {
                all.Add(f);
                IFeature sub = f.GetFirstSubFeature() as IFeature;
                while (sub != null)
                {
                    all.Add(sub);
                    sub = sub.GetNextSubFeature() as IFeature;
                }
                f = f.GetNextFeature() as IFeature;
            }
            return all;
        }

        /// <summary>Size, write time and SHA-256 of a file, to prove it wasn't touched.</summary>
        private static string Snapshot(string path)
        {
            var info = new FileInfo(path);
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) // SolidWorks keeps open parts open
                return info.Length + "|" + info.LastWriteTimeUtc.Ticks + "|" + BitConverter.ToString(sha.ComputeHash(stream));
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>The sample parts, made fresh for each run in the run's temp folder.</summary>
    // Stand-ins for the connector's JsCVServletExpandV2Result, which BookmarkSearch reads by property name.
    public sealed class FakeExpand
    {
        private readonly List<FakeExpandRow> _results = new List<FakeExpandRow>();
        public List<FakeExpandRow> Results { get { return _results; } }
    }

    public sealed class FakeExpandRow
    {
        public string ResourceId { get; set; }
        public string Ds6w_label { get; set; }
        public string From { get; set; }
        public string To { get; set; }
    }

    // Stand-ins for JsCreateBookmarkResult (items is a field, as in the connector) and its Item.
    public sealed class FakeCreateResult
    {
        public List<FakeCreateItem> items = new List<FakeCreateItem>();
    }

    public sealed class FakeCreateItem
    {
        public string id;
        public string title;
    }

    internal sealed class Samples
    {
        private const double Radius = 0.005;    // 10 mm diameter
        private const double Length = 0.020;    // 20 mm long
        private const double HoleRadius = 0.002;
        private const double StubLength = 0.010;

        /// <summary>McMaster-style: a cylinder with a cosmetic thread, and a "Threads" folder holding a cut.</summary>
        public string Threaded;
        /// <summary>The same part saved as a Murder Part copy name.</summary>
        public string Murdered;
        /// <summary>Two touching bodies, which Murder Part must combine.</summary>
        public string TwoBody;

        /// <summary>The threaded part's volume with everything in the Threads folder removed.</summary>
        public double ThreadedSolidVolume { get { return Math.PI * Radius * Radius * Length; } }
        public double TwoBodyVolume { get { return Math.PI * Radius * Radius * (Length + StubLength); } }

        /// <summary>Bump when Create makes different parts, so the saved copies are built again.</summary>
        private const int SampleVersion = 1;

        private static Samples At(string folder)
        {
            var s = new Samples();
            s.Threaded = Path.Combine(folder, "91251A540_Socket Head Screw.SLDPRT");
            s.Murdered = Path.Combine(folder, "91251A540_Socket Head Screw_murdered.SLDPRT");
            s.TwoBody = Path.Combine(folder, "TwoBody.SLDPRT");
            return s;
        }

        private string[] Files { get { return new[] { Threaded, Murdered, TwoBody }; } }

        /// <summary>
        /// The sample parts, copied into this run's folder from the saved set in %LOCALAPPDATA%\BDAT\test-samples.
        /// The saved set is built the first time (or again with fresh), so later runs skip building them.
        /// </summary>
        public static Samples Get(ISldWorks swApp, string work, bool fresh)
        {
            string saved = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "BDAT", "test-samples", "v" + SampleVersion);
            Samples cached = At(saved);
            if (fresh || !cached.Files.All(File.Exists))
            {
                if (Directory.Exists(saved)) Directory.Delete(saved, true);
                Directory.CreateDirectory(saved);
                Console.WriteLine("     building the sample parts in " + saved);
                try { Create(swApp, saved); }
                catch
                {
                    try { Directory.Delete(saved, true); } catch { }
                    throw;
                }
            }
            else Console.WriteLine("     reusing the sample parts saved in " + saved + " (--fresh-samples builds them again)");

            Directory.CreateDirectory(work);
            Samples s = At(work);
            string[] from = cached.Files, to = s.Files;
            for (int i = 0; i < from.Length; i++)
            {
                File.Copy(from[i], to[i], true);
                File.SetAttributes(to[i], FileAttributes.Normal);
            }
            return s;
        }

        public static Samples Create(ISldWorks swApp, string folder)
        {
            Samples s = At(folder);

            IModelDoc2 doc = NewPart(swApp);
            try
            {
                IFeature plane = FirstPlane(doc);
                Extrude(doc, plane, Radius, Length, false, true);

                // The "modeled thread": a cut kept in a Threads folder, the way McMaster ships them.
                IFeature cut = Cut(doc, plane, HoleRadius);
                doc.ClearSelection2(true);
                cut.Select2(false, 0);
                IFeature threads = doc.FeatureManager.InsertFeatureTreeFolder2(
                    (int)swFeatureTreeFolderType_e.swFeatureTreeFolder_Containing) as IFeature;
                if (threads == null) throw new Exception("couldn't make the Threads folder");
                threads.Name = "Threads";

                AddCosmeticThread(doc);
                doc.ForceRebuild3(false);

                List<string> types = Program.FeatureTypes(doc);
                if (!types.Contains("CosmeticThread")) throw new Exception("the sample has no cosmetic thread (types: " + string.Join(",", types.ToArray()) + ")");
                double hollow = Math.PI * (Radius * Radius - HoleRadius * HoleRadius) * Length;
                double v = Program.Volume(doc);
                if (Math.Abs(v - hollow) > hollow * 0.01) throw new Exception("the sample's volume is " + v + ", expected " + hollow);

                SaveAs(doc, s.Threaded);
                SaveAs(doc, s.Murdered, true);
            }
            finally
            {
                swApp.CloseDoc(doc.GetTitle());
            }

            doc = NewPart(swApp);
            try
            {
                IFeature plane = FirstPlane(doc);
                Extrude(doc, plane, Radius, Length, false, false);
                Extrude(doc, plane, Radius, StubLength, true, false);
                object[] bodies = ((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
                if (bodies == null || bodies.Length != 2) throw new Exception("the two-body sample has " + (bodies == null ? 0 : bodies.Length) + " bodies");
                SaveAs(doc, s.TwoBody);
            }
            finally
            {
                swApp.CloseDoc(doc.GetTitle());
            }
            return s;
        }

        /// <summary>Another part template: one beside the missing default, else SolidWorks' own. Null if there's none.</summary>
        private static string FallbackPartTemplate(string missingDefault)
        {
            try
            {
                string dir = string.IsNullOrEmpty(missingDefault) ? null : Path.GetDirectoryName(missingDefault);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string[] near = Directory.GetFiles(dir, "*.prtdot");
                    if (near.Length > 0) return near[0];
                }
            }
            catch (Exception)
            {
                // Fall through to SolidWorks' own template.
            }
            string ownTemplate = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
                @"SOLIDWORKS\SOLIDWORKS 2025\templates\Part.prtdot");
            return File.Exists(ownTemplate) ? ownTemplate : null;
        }

        internal static IModelDoc2 NewPart(ISldWorks swApp)
        {
            string template = swApp.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplatePart);
            if (string.IsNullOrEmpty(template) || !File.Exists(template))
            {
                // The default can point at a template that's been renamed or not synced from 3DEXPERIENCE yet.
                // Any part template will do for sample parts, so use another one rather than fail.
                string fallback = FallbackPartTemplate(template);
                if (fallback == null) throw new Exception("no default part template (" + template + ")");
                Console.WriteLine("     (default part template missing, using " + fallback + ")");
                template = fallback;
            }
            var doc = swApp.NewDocument(template, 0, 0, 0) as IModelDoc2;
            if (doc == null) throw new Exception("couldn't make a new part");
            doc.SketchManager.AddToDB = true; // no snapping while sketching
            return doc;
        }

        internal static IModelDoc2 NewAssembly(ISldWorks swApp)
        {
            string template = swApp.GetUserPreferenceStringValue((int)swUserPreferenceStringValue_e.swDefaultTemplateAssembly);
            if (string.IsNullOrEmpty(template) || !File.Exists(template)) throw new Exception("no default assembly template (" + template + ")");
            var doc = swApp.NewDocument(template, 0, 0, 0) as IModelDoc2;
            if (doc == null) throw new Exception("couldn't make a new assembly");
            doc.SketchManager.AddToDB = true;
            return doc;
        }

        /// <summary>The first reference plane (Front Plane), found by type so it works in any language.</summary>
        private static IFeature FirstPlane(IModelDoc2 doc)
        {
            IFeature f = doc.FirstFeature() as IFeature;
            while (f != null && f.GetTypeName2() != "RefPlane") f = f.GetNextFeature() as IFeature;
            if (f == null) throw new Exception("no reference plane in the template");
            return f;
        }

        private static void SketchCircle(IModelDoc2 doc, IFeature plane, double radius)
        {
            doc.ClearSelection2(true);
            plane.Select2(false, 0);
            doc.SketchManager.InsertSketch(true);
            doc.SketchManager.CreateCircleByRadius(0, 0, 0, radius);
            doc.SketchManager.InsertSketch(true);
            doc.ClearSelection2(true);
            IFeature sketch = doc.FeatureByPositionReverse(0) as IFeature;
            sketch.Select2(false, 0);
        }

        private static void Extrude(IModelDoc2 doc, IFeature plane, double radius, double depth, bool flip, bool merge)
        {
            SketchCircle(doc, plane, radius);
            IFeature f = doc.FeatureManager.FeatureExtrusion2(true, false, flip,
                (int)swEndConditions_e.swEndCondBlind, 0, depth, 0, false, false, false, false, 0, 0,
                false, false, false, false, merge, true, true,
                (int)swStartConditions_e.swStartSketchPlane, 0, false) as IFeature;
            if (f == null) throw new Exception("extrude failed");
        }

        private static IFeature Cut(IModelDoc2 doc, IFeature plane, double radius)
        {
            SketchCircle(doc, plane, radius);
            IFeature f = doc.FeatureManager.FeatureCut4(false, false, false,
                (int)swEndConditions_e.swEndCondThroughAll, (int)swEndConditions_e.swEndCondThroughAll, 0, 0,
                false, false, false, false, 0, 0, false, false, false, false, false, true, true, false, false, false,
                (int)swStartConditions_e.swStartSketchPlane, 0, false, false) as IFeature;
            if (f == null) throw new Exception("cut failed");
            return f;
        }

        /// <summary>Cosmetic thread on the outer circular edge of the cylinder.</summary>
        private static void AddCosmeticThread(IModelDoc2 doc)
        {
            IEdge edge = null;
            object[] bodies = ((IPartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[];
            foreach (object b in bodies ?? new object[0])
            {
                foreach (object e in (object[])((IBody2)b).GetEdges())
                {
                    ICurve curve = ((IEdge)e).GetCurve() as ICurve;
                    if (curve == null || !curve.IsCircle()) continue;
                    double[] p = curve.CircleParams as double[];
                    if (p != null && Math.Abs(p[6] - Radius) < 1e-6) { edge = (IEdge)e; break; }
                }
                if (edge != null) break;
            }
            if (edge == null) throw new Exception("no outer circular edge for the cosmetic thread");

            doc.ClearSelection2(true);
            ((IEntity)edge).Select4(false, null);
            IFeature thread = doc.FeatureManager.InsertCosmeticThread3(
                (int)swCosmeticStandardType_e.swStandardType_StandardNone, "", "", 2 * Radius * 0.84,
                (int)swCosmeticEndConditions_e.swEndConditionBlind, Length / 2, "M10 test thread") as IFeature;
            if (thread == null)
            {
                doc.ClearSelection2(true);
                ((IEntity)edge).Select4(false, null);
                thread = doc.FeatureManager.InsertCosmeticThread2(
                    (short)swCosmeticThreadType_e.swApplyCosmeticThread_Blind, 2 * Radius * 0.84, Length / 2, "M10 test thread") as IFeature;
            }
            if (thread == null) throw new Exception("couldn't add a cosmetic thread");
            doc.ClearSelection2(true);
        }

        private static void SaveAs(IModelDoc2 doc, string path, bool copy = false)
        {
            int errors = 0, warnings = 0;
            int options = (int)swSaveAsOptions_e.swSaveAsOptions_Silent;
            if (copy) options |= (int)swSaveAsOptions_e.swSaveAsOptions_Copy;
            bool ok = doc.Extension.SaveAs3(path, (int)swSaveAsVersion_e.swSaveAsCurrentVersion, options, null, null, ref errors, ref warnings);
            if (!ok || !File.Exists(path)) throw new Exception("couldn't save " + path + " (error " + errors + ")");
        }
    }

    internal sealed class Options
    {
        public bool SolidWorks;
        public bool Attach;
        public bool AllowOpenDocuments;
        public bool Launch;
        public bool Keep;
        public bool FreshSamples;
        public string PartsDir;
        public string SolidWorksExe;
        public string ExpectLoadedDll;
        public int StartTimeoutSeconds = 240;

        public const string Usage =
            "BdatTests.exe [--solidworks [--launch] [--attach [--allow-open-docs]] [--sw-exe PATH]\n" +
            "              [--expect-loaded-dll PATH] [--start-timeout SECONDS] [--keep] [--fresh-samples]]\n" +
            "  (no options)   unit tests only, no SolidWorks\n" +
            "  --solidworks   also run the SolidWorks tests\n" +
            "  --launch       start a SolidWorks for the tests when none is running, and close it afterwards\n" +
            "  --attach       use an already running SolidWorks (refused if it has documents open)\n" +
            "  --keep         keep the sample parts folder\n" +
            "  --fresh-samples  build the sample parts again instead of reusing the saved ones\n" +
            "Exit codes: 0 passed, 1 failed, 2 bad arguments, 3 SolidWorks not available.";

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--solidworks": o.SolidWorks = true; break;
                    case "--launch": o.Launch = true; break;
                    case "--attach": o.Attach = true; break;
                    case "--allow-open-docs": o.AllowOpenDocuments = true; break;
                    case "--keep": o.Keep = true; break;
                    case "--fresh-samples": o.FreshSamples = true; break;
                    case "--parts-dir": if (++i >= args.Length) return null; o.PartsDir = args[i]; break;
                    case "--sw-exe": if (++i >= args.Length) return null; o.SolidWorksExe = args[i]; break;
                    case "--expect-loaded-dll": if (++i >= args.Length) return null; o.ExpectLoadedDll = args[i]; break;
                    case "--start-timeout": if (++i >= args.Length) return null; o.StartTimeoutSeconds = int.Parse(args[i]); break;
                    default: return null;
                }
            }
            return o;
        }
    }

    /// <summary>Retries COM calls SolidWorks rejects while it's busy, instead of failing them.</summary>
    [ComImport, Guid("00000016-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IOleMessageFilter
    {
        [PreserveSig] int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);
        [PreserveSig] int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);
        [PreserveSig] int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
    }

    internal sealed class MessageFilter : IOleMessageFilter
    {
        [DllImport("Ole32.dll")]
        private static extern int CoRegisterMessageFilter(IOleMessageFilter newFilter, out IOleMessageFilter oldFilter);

        public static void Register()
        {
            IOleMessageFilter old;
            CoRegisterMessageFilter(new MessageFilter(), out old);
        }

        public static void Revoke()
        {
            IOleMessageFilter old;
            CoRegisterMessageFilter(null, out old);
        }

        public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) { return 0; }

        public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
        {
            const int SERVERCALL_RETRYLATER = 2;
            return dwRejectType == SERVERCALL_RETRYLATER && dwTickCount < 120000 ? 250 : -1;
        }

        public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) { return 2; }
    }
}
