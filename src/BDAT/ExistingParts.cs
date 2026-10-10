using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using BDAT.Testing;

namespace BDAT
{
    /// <summary>
    /// Which part numbers are already in 3DEXPERIENCE, anywhere on the platform (not just this PC's work folder).
    ///
    /// Uses the same title search the connector's own Save window uses to warn about names that are taken:
    ///   WSAPI.WebServices.searchTitles(server, cc, "VPMReference", kind, names)
    /// which searches 3DSpace for SOLIDWORKS models whose model name (the file name) is one of names, in batches, and
    /// returns the ones it found. kind "3DPart" finds parts; anything else finds assemblies. Read-only. It's internal
    /// to the connector, so a failure just means "couldn't check" (null), never "nothing exists".
    /// </summary>
    internal static class ExistingParts
    {
        private const string LogName = "new-from-ebom";

        // The last full check, kept for a few minutes so reopening the pop-up is instant.
        private static HashSet<string> _known;
        private static DateTime _knownTime;
        private static readonly object _gate = new object();

        /// <summary>
        /// The numbers (any case) that already exist in 3DEXPERIENCE as parts or assemblies, or null if it couldn't check.
        /// Safe to call from a background thread. Uses the cached answer if it's less than maxAge old.
        /// </summary>
        public static HashSet<string> Check(ICollection<string> numbers, TimeSpan maxAge)
        {
            lock (_gate)
            {
                if (_known != null && DateTime.Now - _knownTime < maxAge) return _known;
            }
            TestMode.BlockConnector("ExistingParts");
            try
            {
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!Search(numbers, "3DPart", found) || !Search(numbers, "Assembly", found)) return null;
                PlatformSave.Log(LogName, "existing check: " + found.Count + " of " + numbers.Count + " EBOM numbers are already in 3DX");
                lock (_gate)
                {
                    _known = found;
                    _knownTime = DateTime.Now;
                }
                return found;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                PlatformSave.Log(LogName, "existing check failed: " + inner);
                return null;
            }
        }

        /// <summary>True if this one number is in 3DEXPERIENCE, false if not, null if it couldn't check. Asks the platform now.</summary>
        public static bool? Exists(string number)
        {
            TestMode.BlockConnector("ExistingParts");
            try
            {
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var one = new[] { number };
                if (!Search(one, "3DPart", found) || !Search(one, "Assembly", found)) return null;
                if (found.Count > 0) Remember(number);
                return found.Count > 0;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                PlatformSave.Log(LogName, "existing check for " + number + " failed: " + inner);
                return null;
            }
        }

        /// <summary>Adds a number BDAT has just saved, so it shows as existing without another search.</summary>
        public static void Remember(string number)
        {
            lock (_gate)
            {
                if (_known != null) _known.Add(number);
            }
        }

        private static bool Search(ICollection<string> numbers, string kind, HashSet<string> found)
        {
            Assembly infra = Connector.ConnectorAssembly("TaskPaneInfra");
            Assembly wsapi = Connector.ConnectorAssembly("WSAPI");
            Type iec = infra == null ? null : infra.GetType("TaskPaneInfra.IEC");
            Type web = wsapi == null ? null : wsapi.GetType("WSAPI.WebServices");
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            FieldInfo lockField = iec == null ? null : iec.GetField("_serverLock", any);
            MethodInfo getServer = iec == null ? null : iec.GetMethod("get_server", any);
            MethodInfo search = web == null ? null : web.GetMethod("searchTitles", any);
            if (lockField == null || getServer == null || search == null)
            {
                PlatformSave.Log(LogName, "existing check: this connector version doesn't have the title search");
                return false;
            }

            // The model name may be stored with or without the file extension, so ask for both.
            string extension = kind == "3DPart" ? ".SLDPRT" : ".SLDASM";
            var names = new List<string>();
            foreach (string n in numbers) { names.Add(n); names.Add(n + extension); }

            object serverLock = lockField.GetValue(null) ?? new object();
            bool anyAnswer = false;
            // By model name (type "VPMReference"), which is what finds them on the team's platform. (Searching by title,
            // type "VPMReference " with a space, never found anything more, so it's left out: it doubled the wait.)
            foreach (string type in new[] { "VPMReference" })
            {
                object answer;
                lock (serverLock)
                {
                    object server = getServer.Invoke(null, null);
                    if (server == null) { PlatformSave.Log(LogName, "existing check: not connected"); return false; }
                    // A fresh cookie container makes searchTitles sign in to 3DSpace itself with the current session.
                    answer = search.Invoke(null, new object[] { server, new CookieContainer(), type, kind, names.ToArray() });
                }
                if (answer == null) { PlatformSave.Log(LogName, "existing check: " + kind + " search by " + (type.EndsWith(" ") ? "title" : "model name") + " failed"); continue; }
                anyAnswer = true;
                int before = found.Count;
                var sample = new List<string>();
                foreach (object title in (IEnumerable)answer)
                {
                    string t = Number(title as string);
                    if (t.Length == 0) continue;
                    found.Add(t);
                    if (sample.Count < 3) sample.Add(t);
                }
                PlatformSave.Log(LogName, "existing check: " + kind + " search by " + (type.EndsWith(" ") ? "title" : "model name") +
                    " found " + (found.Count - before) + " new" + (sample.Count > 0 ? ", e.g. " + string.Join(", ", sample.ToArray()) : ""));
            }
            return anyAnswer;
        }

        /// <summary>"br-10101-aa.sldprt" or "BR-10101-AA" -> "BR-10101-AA" (case is ignored when comparing anyway).</summary>
        internal static string Number(string title)
        {
            string t = (title ?? "").Trim();
            foreach (string ext in new[] { ".SLDPRT", ".SLDASM" })
                if (t.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - ext.Length);
            return t;
        }

        /// <summary>For tests: what Check would answer if the platform said these exist.</summary>
        internal static void SetForTests(IEnumerable<string> existing)
        {
            lock (_gate)
            {
                _known = existing == null ? null : new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                _knownTime = DateTime.Now;
            }
        }
    }
}
