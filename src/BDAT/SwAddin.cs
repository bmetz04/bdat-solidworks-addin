using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using SolidWorks.Interop.swpublished;
using BDAT.Commands;

namespace BDAT
{
    /// <summary>
    /// BDAT add-in entry point. SolidWorks loads this class through COM.
    ///
    /// To add a new macro:
    ///   1. Write a class in Commands/ that implements IBdatCommand.
    ///   2. Add it to the Commands list below.
    ///   3. Add a pair of public callback methods (OnXxx / CanXxx) at the bottom of this file.
    /// </summary>
    [ComVisible(true)]
    [Guid("d8d33ac0-63b3-49bc-b09a-e46207ce999b")]
    [ProgId("BDAT.SwAddin")]
    public partial class SwAddin : ISwAddin
    {
        private const string AddinTitle = "BDAT";
        private const string AddinDescription = "BDAT: FUBC speed-up macros for SolidWorks";
        private const int MainCommandGroupId = 4207;

        // Bump this whenever commands are added, removed or reordered so SolidWorks
        // rebuilds the toolbar instead of reusing its cached copy.
        private const int CommandGroupVersion = 6;

        private ISldWorks _swApp;
        private ICommandManager _cmdMgr;
        private int _addinCookie;

        // One entry per command. Callback names must match public methods on this class.
        private readonly List<CommandEntry> _commands = new List<CommandEntry>
        {
            new CommandEntry(new MurderPartCommand(), "OnMurderPart", "CanMurderPart"),
            new CommandEntry(new SaveMcmCommand(), "OnSaveMcm", "CanSaveMcm"),
            new CommandEntry(new CreateOriginCommand(), "OnCreateOrigin", "CanCreateOrigin", true),
            new CommandEntry(new UpdateCommand(), "OnUpdate", "CanUpdate", true),
            new CommandEntry(new VersionCommand(), "OnVersion", "CanVersion", true),
        };

        #region ISwAddin

        public bool ConnectToSW(object ThisSW, int Cookie)
        {
            _swApp = (ISldWorks)ThisSW;
            _addinCookie = Cookie;
            _swApp.SetAddinCallbackInfo2(0, this, _addinCookie);

            _cmdMgr = _swApp.GetCommandManager(_addinCookie);
            AddCommandManager();
            return true;
        }

        public bool DisconnectFromSW()
        {
            try
            {
                if (_cmdMgr != null) _cmdMgr.RemoveCommandGroup2(MainCommandGroupId, true);
            }
            catch
            {
                // Nothing useful to do while SolidWorks is shutting down.
            }

            if (_cmdMgr != null) Marshal.ReleaseComObject(_cmdMgr);
            _cmdMgr = null;
            if (_swApp != null) Marshal.ReleaseComObject(_swApp);
            _swApp = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            return true;
        }

        #endregion

        #region Toolbar, menu and CommandManager tab

        private void AddCommandManager()
        {
            int errors = 0;

            // Rebuild SolidWorks' cached toolbar whenever this build differs from the one that last ran.
            bool ignorePrevious = !VersionMatches();
            object registryIdsObj;
            if (_cmdMgr.GetGroupDataFromRegistry(MainCommandGroupId, out registryIdsObj))
            {
                int[] knownIds = registryIdsObj as int[];
                if (knownIds == null || knownIds.Length != _commands.Count)
                    ignorePrevious = true;
            }

            if (ignorePrevious) ClearCachedTabs();

            ICommandGroup group = _cmdMgr.CreateCommandGroup2(
                MainCommandGroupId, AddinTitle, AddinDescription, AddinDescription, -1, ignorePrevious, ref errors);

            string[] iconStrips = Icons.BuildIconStrips(_commands.Count);
            string[] mainIcons = Icons.BuildMainIcons();
            group.IconList = iconStrips;
            group.MainIconList = mainIcons;

            int itemType = (int)(swCommandItemType_e.swMenuItem | swCommandItemType_e.swToolbarItem);
            for (int i = 0; i < _commands.Count; i++)
            {
                CommandEntry entry = _commands[i];
                entry.ItemIndex = group.AddCommandItem2(
                    entry.Command.Title, -1, entry.Command.Hint, entry.Command.Title,
                    i, entry.Callback, entry.EnableCallback, i, itemType);
            }

            group.HasToolbar = true;
            group.HasMenu = true;
            group.Activate();

            foreach (CommandEntry entry in _commands)
                entry.CommandId = group.get_CommandID(entry.ItemIndex);

            // Put the buttons on a "BDAT" tab in the CommandManager ribbon when a part is open, and the ones that
            // work in assemblies on a "BDAT" tab when an assembly is open.
            AddCommandTab(swDocumentTypes_e.swDocPART);
            AddCommandTab(swDocumentTypes_e.swDocASSEMBLY);

            SaveVersion();
        }

        private void AddCommandTab(swDocumentTypes_e docType)
        {
            // SolidWorks remembers the tab between sessions, so a saved copy can be missing new buttons.
            // Always replace it with a fresh one.
            CommandTab tab = _cmdMgr.GetCommandTab((int)docType, AddinTitle);
            if (tab != null) _cmdMgr.RemoveCommandTab(tab);

            tab = _cmdMgr.AddCommandTab((int)docType, AddinTitle);

            var ids = new List<int>();
            foreach (CommandEntry entry in _commands)
            {
                if (docType == swDocumentTypes_e.swDocASSEMBLY && !entry.InAssemblies) continue;
                ids.Add(entry.CommandId);
            }

            // Two layers: small buttons with the text beside the icon, stacked two to a column.
            // SolidWorks stacks up to three of these per box, so each pair gets its own box.
            for (int i = 0; i < ids.Count; i += ButtonsPerColumn)
            {
                int count = Math.Min(ButtonsPerColumn, ids.Count - i);
                int[] columnIds = ids.GetRange(i, count).ToArray();
                int[] textTypes = new int[count];
                for (int j = 0; j < count; j++)
                    textTypes[j] = (int)swCommandTabButtonTextDisplay_e.swCommandTabButton_TextHorizontal;

                CommandTabBox box = tab.AddCommandTabBox();
                box.AddCommands(columnIds, textTypes);
            }
        }

        private const int ButtonsPerColumn = 2;

        // SolidWorks saves each CommandManager tab as a fixed list of button slots, e.g.
        // ...\SOLIDWORKS 2025\Simplified Interface\User Interface\CommandManager\PartContext\Tab26\GB0\Btn0..Btn2,
        // and restores that list over the tab we build. When a button is added the old slots point at the wrong
        // buttons and the last one disappears, so forget BDAT's saved tabs whenever the toolbar changes.
        private static void ClearCachedTabs()
        {
            string module = "{" + typeof(SwAddin).GUID.ToString().ToUpperInvariant() + "}";
            try
            {
                using (RegistryKey solidWorks = Registry.CurrentUser.OpenSubKey(@"Software\SolidWorks", true))
                {
                    if (solidWorks == null) return;
                    foreach (string version in solidWorks.GetSubKeyNames())
                    {
                        if (!version.StartsWith("SOLIDWORKS ", StringComparison.OrdinalIgnoreCase)) continue;
                        ClearCachedTabs(solidWorks, version + @"\User Interface\CommandManager", module);
                        ClearCachedTabs(solidWorks, version + @"\Simplified Interface\User Interface\CommandManager", module);
                    }
                }
            }
            catch
            {
                // Worst case the tab shows stale buttons until SolidWorks is restarted.
            }
        }

        private static void ClearCachedTabs(RegistryKey solidWorks, string commandManagerPath, string module)
        {
            using (RegistryKey commandManager = solidWorks.OpenSubKey(commandManagerPath, true))
            {
                if (commandManager == null) return;
                foreach (string context in commandManager.GetSubKeyNames())
                {
                    using (RegistryKey contextKey = commandManager.OpenSubKey(context, true))
                    {
                        if (contextKey == null) continue;
                        foreach (string tab in contextKey.GetSubKeyNames())
                        {
                            using (RegistryKey tabKey = contextKey.OpenSubKey(tab))
                            {
                                object owner = tabKey == null ? null : tabKey.GetValue("ModuleName");
                                if (!(owner is string) || !string.Equals((string)owner, module, StringComparison.OrdinalIgnoreCase)) continue;
                            }
                            contextKey.DeleteSubKeyTree(tab, false);
                        }
                    }
                }
            }
        }

        // The version button's text is the BDAT version, so a new build also needs a fresh toolbar.
        private static string ToolbarVersion
        {
            get { return CommandGroupVersion + "|" + BuildInfo.Version; }
        }

        private static bool VersionMatches()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(UserKeyPath))
            {
                object value = key == null ? null : key.GetValue("ToolbarVersion");
                return value is string && (string)value == ToolbarVersion;
            }
        }

        private static void SaveVersion()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UserKeyPath))
            {
                if (key != null) key.SetValue("ToolbarVersion", ToolbarVersion, RegistryValueKind.String);
            }
        }

        private const string UserKeyPath = @"Software\BDAT";

        #endregion

        #region Command plumbing

        private void Run(IBdatCommand command)
        {
            try
            {
                command.Run(_swApp);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    command.Title + " failed:\n\n" + ex.Message,
                    AddinTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private int CanRun(IBdatCommand command)
        {
            try
            {
                return command.IsEnabled(_swApp) ? 1 : 0;
            }
            catch
            {
                return 0;
            }
        }

        private sealed class CommandEntry
        {
            public CommandEntry(IBdatCommand command, string callback, string enableCallback)
                : this(command, callback, enableCallback, false)
            {
            }

            public CommandEntry(IBdatCommand command, string callback, string enableCallback, bool inAssemblies)
            {
                Command = command;
                Callback = callback;
                EnableCallback = enableCallback;
                InAssemblies = inAssemblies;
            }

            public IBdatCommand Command { get; private set; }
            public string Callback { get; private set; }
            public string EnableCallback { get; private set; }
            /// <summary>Also on the BDAT tab when an assembly is open.</summary>
            public bool InAssemblies { get; private set; }
            public int ItemIndex { get; set; }
            public int CommandId { get; set; }
        }

        #endregion

        #region Callbacks (SolidWorks calls these by name, so they must stay public)

        public void OnMurderPart() { Run(_commands[0].Command); }
        public int CanMurderPart() { return CanRun(_commands[0].Command); }

        public void OnSaveMcm() { Run(_commands[1].Command); }
        public int CanSaveMcm() { return CanRun(_commands[1].Command); }

        public void OnCreateOrigin() { Run(_commands[2].Command); }
        public int CanCreateOrigin() { return CanRun(_commands[2].Command); }

        public void OnUpdate() { Run(_commands[3].Command); }
        public int CanUpdate() { return CanRun(_commands[3].Command); }

        public void OnVersion() { Run(_commands[4].Command); }
        public int CanVersion() { return CanRun(_commands[4].Command); }

        #endregion

        #region COM registration (runs when you call RegAsm)

        private static string AddinKeyPath(Type t) { return @"SOFTWARE\SolidWorks\Addins\{" + t.GUID + "}"; }
        private static string StartupKeyPath(Type t) { return @"Software\SolidWorks\AddInsStartup\{" + t.GUID + "}"; }

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            try
            {
                using (RegistryKey addinKey = Registry.LocalMachine.CreateSubKey(AddinKeyPath(t)))
                {
                    addinKey.SetValue(null, 1, RegistryValueKind.DWord);
                    addinKey.SetValue("Description", AddinDescription);
                    addinKey.SetValue("Title", AddinTitle);
                }

                // Load automatically every time SolidWorks starts.
                using (RegistryKey startupKey = Registry.CurrentUser.CreateSubKey(StartupKeyPath(t)))
                {
                    startupKey.SetValue(null, 1, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("BDAT registration failed: " + ex.Message);
                Console.WriteLine("Run the command prompt as Administrator and try again.");
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            try
            {
                Registry.LocalMachine.DeleteSubKey(AddinKeyPath(t), false);
                Registry.CurrentUser.DeleteSubKey(StartupKeyPath(t), false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("BDAT unregistration failed: " + ex.Message);
            }
        }

        #endregion
    }
}
