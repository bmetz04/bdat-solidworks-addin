using System.Collections.Generic;
using System.Text;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace BDAT
{
    /// <summary>
    /// Read-only hooks the test runner (tests\BdatTests.exe) calls over COM on the BDAT that SolidWorks has
    /// loaded, through ISldWorks.GetAddInObject. They report what is on the toolbar and change nothing.
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
