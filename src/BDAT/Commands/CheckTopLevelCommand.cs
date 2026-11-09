using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using BDAT.Testing;

namespace BDAT.Commands
{
    /// <summary>
    /// Check Top Level: with a vehicle top level open (its name has "toplevel" in it), lists the assemblies in the same
    /// 3DX folder as the top level, or any folder under it, that aren't anywhere in the top level (directly or inside a
    /// sub-assembly). E.g. everything in the Suspension folder must be in Sus Toplevel (Ben, 2026-11-09). Only reads.
    /// </summary>
    public sealed class CheckTopLevelCommand : IBdatCommand
    {
        public string Title { get { return "Check Top Level"; } }

        public string Hint { get { return "List the assemblies in the top level's 3DX folder that aren't in the top level"; } }

        public bool IsEnabled(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            return doc != null && doc.GetType() == (int)swDocumentTypes_e.swDocASSEMBLY;
        }

        public void Run(ISldWorks swApp)
        {
            IModelDoc2 doc = swApp.ActiveDoc as IModelDoc2;
            if (doc == null || doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                Ui.Tell(swApp, "Open a top-level assembly first.", swMessageBoxIcon_e.swMbWarning);
                return;
            }
            string path = doc.GetPathName();
            string name = ExistingParts.Number(string.IsNullOrEmpty(path) ? doc.GetTitle() : Path.GetFileNameWithoutExtension(path));
            if (!PlatformParts.IsTopLevelName(name))
            {
                Ui.Tell(swApp, name + " isn't a top level (top levels have \"toplevel\" in their name). Open one with Open Top Level, then try again.",
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }
            if (TestMode.Enabled) return; // the folders are in 3DX, and tests must never reach it

            Connector connector = Connector.Find();
            if (connector == null)
            {
                Ui.Tell(swApp, "Couldn't find the 3DX connector in this SolidWorks session (is the \"3DEXPERIENCE PLM Services\" add-in on?).",
                    swMessageBoxIcon_e.swMbWarning);
                return;
            }
            if (!connector.IsConnected)
            {
                Ui.Tell(swApp, "You're not logged in to 3DX. Log in from the 3DX task pane, then try again.", swMessageBoxIcon_e.swMbWarning);
                return;
            }
            var platform = new PlatformSave(swApp, connector, TopLevelCheck.LogName);
            string topId = string.IsNullOrEmpty(path) ? null : platform.PhysicalId(path);
            if (string.IsNullOrEmpty(topId))
            {
                Ui.Tell(swApp, name + " isn't in 3DX, so BDAT can't tell which folder it's in.", swMessageBoxIcon_e.swMbWarning);
                return;
            }

            IWin32Window owner = Ui.SolidWorksWindow(swApp);
            ModernUi.BusyWindow busy = ModernUi.Busy(owner, "Checking " + name, "Reading the assemblies in it...");
            TopLevelCheck.FolderContents contents;
            List<string> ids, titles;
            string error;
            try
            {
                TopLevelCheck.Log("checking " + name + " (" + topId + ")");
                Components((IAssemblyDoc)doc, platform, out ids, out titles);
                TopLevelCheck.Log(name + " has " + titles.Count + " different sub-assemblies");
                busy.Step("Reading its 3DX folder...");
                contents = TopLevelCheck.Read(topId, name, NewFromEbomCommand.SearchStartIds(), out error);
            }
            finally { busy.Close(); }

            if (contents == null)
            {
                Ui.Tell(swApp, "BDAT couldn't read " + name + "'s folder in 3DX: " + error, swMessageBoxIcon_e.swMbWarning);
                return;
            }
            if (contents.Folders.Count == 0)
            {
                Ui.Tell(swApp, "BDAT couldn't find " + name + " in any of the team's 3DX folders, so there's nothing to check it against.", swMessageBoxIcon_e.swMbInformation);
                return;
            }

            var folderNames = new List<string>();
            foreach (FoundBookmark f in contents.Folders) folderNames.Add(f.Title);
            string folders = string.Join(", ", folderNames.ToArray());
            int assemblies = 0;
            foreach (FolderItem i in contents.Items) if (TopLevelCheck.IsAssembly(i)) assemblies++;
            List<FolderItem> missing = TopLevelCheck.Missing(contents.Items, topId, name, ids, titles);
            TopLevelCheck.Log(assemblies + " assemblies in " + folders + ", " + missing.Count + " missing from " + name +
                (missing.Count > 0 ? ": " + string.Join(", ", Titles(missing)) : ""));

            if (missing.Count == 0)
            {
                Ui.Tell(swApp, "All good: every assembly in " + folders + " is in " + name + ".", swMessageBoxIcon_e.swMbInformation);
                return;
            }
            using (var form = new CheckTopLevelForm(name, folders, missing))
                form.ShowDialog(owner);
        }

        private static string[] Titles(List<FolderItem> items)
        {
            var t = new List<string>();
            foreach (FolderItem i in items) t.Add(i.Title);
            return t.ToArray();
        }

        // Every sub-assembly anywhere in the top level (suppressed ones too): its 3DX id where the connector knows it, and its name.
        private static void Components(IAssemblyDoc assembly, PlatformSave platform, out List<string> ids, out List<string> titles)
        {
            ids = new List<string>();
            titles = new List<string>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            object[] all = assembly.GetComponents(false) as object[];
            if (all == null) return;
            foreach (object o in all)
            {
                var c = o as IComponent2;
                if (c == null) continue;
                string p = c.GetPathName();
                if (string.IsNullOrEmpty(p) || !p.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase) || !paths.Add(p)) continue;
                titles.Add(Path.GetFileNameWithoutExtension(p));
                string id = platform.PhysicalId(p);
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
        }
    }

    /// <summary>The Check Top Level result: the assemblies that are in the folder but not in the top level.</summary>
    internal sealed class CheckTopLevelForm : Form
    {
        public CheckTopLevelForm(string topLevel, string folders, List<FolderItem> missing)
        {
            ModernUi.Setup(this, "Check Top Level", true);
            ClientSize = new Size(640, 460);
            MinimumSize = new Size(460, 320);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), ColumnCount = 1, RowCount = 3, BackColor = ModernUi.Page };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            string count = missing.Count == 1 ? "1 assembly is" : missing.Count + " assemblies are";
            layout.Controls.Add(ModernUi.Header("Missing from " + topLevel, count + " in " + folders + " but not in " + topLevel + ".", 580), 0, 0);

            var listFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = ModernUi.Line, Margin = new Padding(0, 10, 0, 0) };
            var list = new ListView
            {
                Dock = DockStyle.Fill,
                View = System.Windows.Forms.View.Details,
                FullRowSelect = true,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                SmallImageList = new ImageList { ImageSize = new Size(1, 28) },
            };
            list.Columns.Add("Name", 240);
            list.Columns.Add("Folder", 340);
            ModernUi.ExplorerTheme(list);
            foreach (FolderItem i in missing)
                list.Items.Add(new ListViewItem(new[] { ExistingParts.Number(i.Title), i.FolderPath ?? "" }));
            list.Resize += delegate
            {
                int width = list.ClientSize.Width - list.Columns[0].Width - 6;
                if (width > 150) list.Columns[1].Width = width;
            };
            listFrame.Controls.Add(list);
            layout.Controls.Add(listFrame, 0, 1);

            var close = ModernUi.Primary("Close");
            close.DialogResult = DialogResult.OK;
            var row = ModernUi.ButtonRow(close);
            row.Margin = new Padding(0, 14, 0, 0);
            row.Dock = DockStyle.Right;
            layout.Controls.Add(row, 0, 2);
            AcceptButton = close;
            CancelButton = close;
        }
    }
}
