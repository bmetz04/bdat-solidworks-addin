using System;
using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT
{
    /// <summary>
    /// Hooks the test runner (tests\BdatTests.exe) calls over COM on the BDAT that SolidWorks has loaded, through
    /// ISldWorks.GetAddInObject. The toolbar ones only read; BdatTestRun only works in a test-mode SolidWorks.
    /// </summary>
    public partial class SwAddin
    {
        /// <summary>
        /// One line per BDAT command: "title|command id|on BDAT tab". The first line is "group|registered"
        /// and "tab|found" so a missing group or tab shows up too.
        /// </summary>
        public string BdatTestToolbar()
        {
            var report = new StringBuilder();
            bool groupRegistered = _cmdMgr != null && _cmdMgr.GetCommandGroup(MainCommandGroupId) != null;
            report.AppendLine("group|" + groupRegistered);

            var onTab = new HashSet<int>();
            CommandTabProbe(onTab, report);

            foreach (CommandEntry entry in _commands)
                report.AppendLine(entry.Command.Title + "|" + entry.CommandId + "|" + onTab.Contains(entry.CommandId));
            return report.ToString();
        }

        /// <summary>
        /// Runs one BDAT command inside SolidWorks, the way its button does, and returns every message it would
        /// have shown, separated by the record separator character (U+001E). Only works in a SolidWorks started in test mode (BDAT_TEST_MODE=1);
        /// anywhere else it refuses. answers: one character per question, 'y' or 'n' (empty means Yes to all).
        /// </summary>
        public string BdatTestRun(string title, string answers)
        {
            if (System.Environment.GetEnvironmentVariable(TestMode.EnvironmentVariable) != "1")
                throw new InvalidOperationException("BdatTestRun only works in a SolidWorks started in BDAT test mode.");

            TestMode.Reset();
            foreach (char a in answers ?? "") TestMode.Answers.Enqueue(a == 'y' || a == 'Y');
            foreach (CommandEntry entry in _commands)
            {
                if (entry.Command.Title != title) continue;
                entry.Command.Run(_swApp);
                if (TestMode.ConnectorAttempts != 0)
                    throw new InvalidOperationException(title + " tried to reach 3DX " + TestMode.ConnectorAttempts + " time(s).");
                return string.Join("\u001e", TestMode.Messages.ToArray());
            }
            throw new ArgumentException("No BDAT command called " + title);
        }

        /// <summary>Where the loaded BDAT.dll lives, so the runner can tell the build under test from an installed one.</summary>
        public string BdatTestDllPath()
        {
            return BuildInfo.DllPath;
        }

        private void CommandTabProbe(HashSet<int> onTab, StringBuilder report)
        {
            CommandTab tab = _cmdMgr == null ? null
                : _cmdMgr.GetCommandTab((int)swDocumentTypes_e.swDocPART, AddinTitle);
            report.AppendLine("tab|" + (tab != null));
            if (tab == null) return;

            object[] boxes = tab.CommandTabBoxes() as object[];
            if (boxes == null) return;
            foreach (object item in boxes)
            {
                CommandTabBox box = item as CommandTabBox;
                if (box == null) continue;
                object ids, styles;
                box.GetCommands(out ids, out styles);
                int[] idArray = ids as int[];
                if (idArray == null) continue;
                foreach (int id in idArray) onTab.Add(id);
            }
        }
    }
}
