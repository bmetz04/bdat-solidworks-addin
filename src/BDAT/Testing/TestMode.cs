using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("BdatTests")]

namespace BDAT.Testing
{
    /// <summary>
    /// Lets tests\run-tests.ps1 drive the BDAT commands without anyone clicking through dialogs, and without
    /// anything ever reaching 3DEXPERIENCE.
    ///
    /// Test mode is on when the BDAT_TEST_MODE environment variable is "1" (set by the test runner on the
    /// SolidWorks it starts), or while the test harness has called Begin(). In test mode:
    ///   - every dialog is skipped: its text is recorded in Messages and it is answered from Answers
    ///     (Yes/OK when the queue is empty),
    ///   - Save MCM runs its pop-up, naming and description logic, records the outcome in LastSaveMcm and stops,
    ///   - any attempt to reach the 3DEXPERIENCE connector throws and is counted in ConnectorAttempts,
    ///   - Update BDAT never downloads or starts the installer,
    ///   - XYZ Planes takes its X, Y and Z from PlaneCoordinates instead of the pop-up.
    /// </summary>
    public static class TestMode
    {
        public const string EnvironmentVariable = "BDAT_TEST_MODE";

        private static bool _begun;

        public static bool Enabled
        {
            get { return _begun || Environment.GetEnvironmentVariable(EnvironmentVariable) == "1"; }
        }

        /// <summary>Turns test mode on for this process and clears everything recorded so far.</summary>
        public static void Begin()
        {
            _begun = true;
            Reset();
        }

        public static void End()
        {
            _begun = false;
            Reset();
        }

        /// <summary>Clears recorded messages, queued answers and results between tests.</summary>
        public static void Reset()
        {
            Messages.Clear();
            Answers.Clear();
            SaveMcmName = null;
            SaveMcmDescription = null;
            LastSaveMcm = null;
            ConnectorAttempts = 0;
            InstallerLaunches = 0;
            PlaneCoordinates = null;
            LastXyzPlanes = null;
        }

        /// <summary>Text of every dialog that would have been shown, oldest first.</summary>
        public static readonly List<string> Messages = new List<string>();

        /// <summary>Answers to Yes/No (or OK/Cancel) questions, in order. Empty means Yes.</summary>
        public static readonly Queue<bool> Answers = new Queue<bool>();

        /// <summary>What to type in Save MCM's Name box. Null keeps the name it fills in.</summary>
        public static string SaveMcmName;

        /// <summary>What to type in Save MCM's Description box. Null keeps the description it fills in.</summary>
        public static string SaveMcmDescription;

        /// <summary>What Save MCM would have sent to 3DEXPERIENCE on its last run, or null if it stopped early.</summary>
        public static SaveMcmTestResult LastSaveMcm;

        /// <summary>What to type in the XYZ Planes pop-up's X, Y and Z boxes. Null means Cancel.</summary>
        public static string[] PlaneCoordinates;

        /// <summary>The planes XYZ Planes made on its last run, or null if it made none.</summary>
        public static XyzPlanesTestResult LastXyzPlanes;

        /// <summary>How many times something tried to use the 3DEXPERIENCE connector. Must stay 0.</summary>
        public static int ConnectorAttempts;

        /// <summary>How many times Update BDAT would have started the installer.</summary>
        public static int InstallerLaunches;

        internal static bool NextAnswer()
        {
            return Answers.Count == 0 || Answers.Dequeue();
        }

        internal static void Record(string caption, string message)
        {
            Messages.Add("[" + caption + "] " + message);
        }

        /// <summary>Call before touching the 3DEXPERIENCE connector. In test mode it throws, so the test fails.</summary>
        internal static void BlockConnector(string what)
        {
            if (!Enabled) return;
            ConnectorAttempts++;
            throw new InvalidOperationException("BDAT test mode: " + what + " tried to reach 3DEXPERIENCE. Tests must never do that.");
        }
    }

    /// <summary>What XYZ Planes made, in test mode.</summary>
    public sealed class XyzPlanesTestResult
    {
        /// <summary>The XY, XZ and YZ planes' names, in that order.</summary>
        public string[] Planes;

        /// <summary>The folder they were put in, or null if there isn't one.</summary>
        public string Folder;
    }

    /// <summary>What Save MCM would have saved, in test mode.</summary>
    public sealed class SaveMcmTestResult
    {
        public string SourceFile;
        public string Name;
        public string Description;
        public string Destination;

        /// <summary>
        /// The steps it ran, in order, e.g. "description", "isometric", "freeze". Steps that need 3DEXPERIENCE
        /// (save, bookmark, check-in) are listed as "skipped: ..." because test mode never runs them.
        /// </summary>
        public readonly List<string> Steps = new List<string>();
    }
}
