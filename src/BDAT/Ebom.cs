using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.Win32;

namespace BDAT
{
    /// <summary>One line of the team's EBOM (the "Master eBOM" Google Sheet, downloaded as CSV).</summary>
    public sealed class EbomRow
    {
        /// <summary>"Combined Part # (Use in 3Dx File Naming)", e.g. BR-10101-AA. Used as the file name.</summary>
        public string Number;
        /// <summary>Assembly, Part or Fastener.</summary>
        public string Class;
        /// <summary>"Area of Commodity", e.g. Brake System.</summary>
        public string Area;
        public string CommodityCode;
        public string ControlNumber;
        public string Revision;
        /// <summary>Current or OBSOLETE.</summary>
        public string Status;
        /// <summary>The sheet's "Assembly" column. Assemblies are named here; on a part it's a qualifier like "Front Right".</summary>
        public string AssemblyColumn;
        /// <summary>The sheet's "Sub-Assembly / Component Name" column.</summary>
        public string ComponentColumn;
        /// <summary>The name of the nearest assembly row above this one (empty for an assembly row itself).</summary>
        public string Parent;

        public bool IsAssembly
        {
            get { return string.Equals(Class, "Assembly", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsObsolete
        {
            get { return string.Equals(Status, "OBSOLETE", StringComparison.OrdinalIgnoreCase); }
        }

        /// <summary>
        /// What the part or assembly is called: the Assembly column for assemblies, the component name for parts,
        /// with the part's qualifier after it, e.g. "Engine Mount Spacer: 7.28 mm (Front Right)".
        /// </summary>
        public string Name
        {
            get
            {
                string main = IsAssembly ? AssemblyColumn : ComponentColumn;
                string other = IsAssembly ? ComponentColumn : AssemblyColumn;
                if (main.Length == 0) return other;
                if (other.Length == 0) return main;
                return main + " (" + other + ")";
            }
        }
    }

    /// <summary>Reads the EBOM CSV and remembers where it is.</summary>
    public static class Ebom
    {
        // Columns are found by their heading, so moving or adding columns in the sheet doesn't break anything.
        // Headings are compared lower-case with spaces and line breaks squashed; a heading matches if it starts with these.
        private const string NumberHeading = "combined part #";

        /// <summary>Reads a CSV file. Throws with a readable message if it isn't the EBOM.</summary>
        public static List<EbomRow> Load(string path)
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        /// <summary>Reads CSV text exported from the EBOM sheet. Rows without a combined part number are skipped.</summary>
        public static List<EbomRow> Parse(string csv)
        {
            List<string[]> table = ReadCsv(csv);
            if (table.Count == 0) throw new InvalidDataException("The EBOM file is empty.");

            string[] headings = table[0];
            int number = Find(headings, NumberHeading, false);
            if (number < 0)
                throw new InvalidDataException("This doesn't look like the EBOM: it has no \"Combined Part #\" column.");
            int cls = Find(headings, "class", true);
            int area = Find(headings, "area of commodity", false);
            int code = Find(headings, "commodity code", false);
            int control = Find(headings, "part control no", false);
            int revision = Find(headings, "revision", true);
            int status = Find(headings, "status", true);
            int assembly = Find(headings, "assembly", true);
            int component = Find(headings, "sub-assembly", false);

            var rows = new List<EbomRow>();
            for (int i = 1; i < table.Count; i++)
            {
                string[] cells = table[i];
                var row = new EbomRow
                {
                    Number = Cell(cells, number),
                    Class = Cell(cells, cls),
                    Area = Cell(cells, area),
                    CommodityCode = Cell(cells, code),
                    ControlNumber = Cell(cells, control),
                    Revision = Cell(cells, revision),
                    Status = Cell(cells, status),
                    AssemblyColumn = Cell(cells, assembly),
                    ComponentColumn = Cell(cells, component),
                };
                if (row.Number.Length == 0) continue;
                rows.Add(row);
            }

            // A part belongs to the assembly whose control number shares all but its last two digits (10101 is in
            // 10100). Row order can't be trusted: obsolete rows sit between current ones and reuse their numbers.
            var assemblies = new Dictionary<string, EbomRow>();
            foreach (EbomRow row in rows)
            {
                if (!row.IsAssembly || row.IsObsolete) continue; // obsolete rows are ignored (Ben, 2026-10-03)
                string group = Group(row.ControlNumber);
                if (group != null && !assemblies.ContainsKey(group)) assemblies[group] = row;
            }
            foreach (EbomRow row in rows)
            {
                EbomRow parent;
                string group = Group(row.ControlNumber);
                row.Parent = !row.IsAssembly && group != null && assemblies.TryGetValue(group, out parent) ? parent.Name : "";
            }
            if (rows.Count == 0) throw new InvalidDataException("The EBOM file has no part numbers in it.");
            return rows;
        }

        /// <summary>
        /// Rows matching every word typed (in any order), searching part number, name, parent assembly and area.
        /// Obsolete rows only if asked for.
        /// </summary>
        public static List<EbomRow> Search(IEnumerable<EbomRow> rows, string text, bool includeObsolete)
        {
            string[] words = (text ?? "").ToLowerInvariant().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var found = new List<EbomRow>();
            foreach (EbomRow row in rows)
            {
                if (row.IsObsolete && !includeObsolete) continue;
                string haystack = (row.Number + " " + row.Name + " " + row.Parent + " " + row.Area + " " + row.Class).ToLowerInvariant();
                bool all = true;
                foreach (string w in words)
                {
                    if (haystack.IndexOf(w, StringComparison.Ordinal) < 0) { all = false; break; }
                }
                if (all) found.Add(row);
            }
            return found;
        }

        /// <summary>"101" for control number 10101: the assembly group. Null if it isn't a number.</summary>
        private static string Group(string controlNumber)
        {
            string n = (controlNumber ?? "").Trim();
            if (n.Length < 3) return null;
            foreach (char c in n)
                if (c < '0' || c > '9') return null;
            return n.Substring(0, n.Length - 2);
        }

        // ---------------------------------------------------------------- the team copy

        /// <summary>The team's EBOM in the repo (release/ebom.csv on main). Replace that file to update it for everyone.</summary>
        public const string TeamUrl = BuildInfo.ReleaseUrl + "ebom.csv";

        /// <summary>Where the last downloaded team copy is kept, so it still works offline.</summary>
        public static string TeamCachePath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BDAT", "ebom.csv");
            }
        }

        /// <summary>
        /// Downloads the team EBOM to TeamCachePath and returns that path. Offline (or if GitHub is slow), returns the copy
        /// downloaded last time, with fresh false. Null if there's neither.
        /// </summary>
        public static string TeamCopy(out bool fresh)
        {
            fresh = false;
            string cache = TeamCachePath;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                using (var web = new QuickWebClient())
                {
                    web.Headers.Add("Cache-Control", "no-cache");
                    // The query string stops GitHub's download cache from handing back an older copy.
                    byte[] data = web.DownloadData(TeamUrl + "?t=" + DateTime.UtcNow.Ticks);
                    Parse(Encoding.UTF8.GetString(data)); // only keep it if it really is the EBOM
                    Directory.CreateDirectory(Path.GetDirectoryName(cache));
                    string temp = cache + ".download";
                    File.WriteAllBytes(temp, data);
                    if (File.Exists(cache)) File.Delete(cache);
                    File.Move(temp, cache);
                    fresh = true;
                }
            }
            catch (Exception)
            {
                // Offline, GitHub down, or no team copy published yet: fall back to last time's.
            }
            return File.Exists(cache) ? cache : null;
        }

        private sealed class QuickWebClient : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);
                if (request != null) request.Timeout = 8000;
                return request;
            }
        }

        // ---------------------------------------------------------------- a CSV picked by hand

        private const string SettingsKey = @"Software\BDAT";
        private const string CsvValue = "EbomCsv";

        /// <summary>The EBOM CSV picked by hand last time on this PC (per Windows user), or null. Only used when there's no team copy.</summary>
        public static string SavedPath
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                    {
                        string path = key == null ? null : key.GetValue(CsvValue) as string;
                        return string.IsNullOrEmpty(path) ? null : path;
                    }
                }
                catch
                {
                    return null;
                }
            }
            set
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key != null) key.SetValue(CsvValue, value ?? "", RegistryValueKind.String);
                }
            }
        }

        // ---------------------------------------------------------------- CSV

        private static int Find(string[] headings, string wanted, bool exact)
        {
            for (int i = 0; i < headings.Length; i++)
            {
                string h = Squash(headings[i]);
                if (exact ? h == wanted : h.StartsWith(wanted, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        private static string Squash(string text)
        {
            var sb = new StringBuilder();
            bool space = false;
            foreach (char c in (text ?? "").Trim().ToLowerInvariant())
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Cell(string[] cells, int index)
        {
            if (index < 0 || index >= cells.Length) return "";
            return (cells[index] ?? "").Trim();
        }

        /// <summary>RFC 4180 CSV: quoted cells may hold commas, "" and line breaks (the EBOM's headings do).</summary>
        internal static List<string[]> ReadCsv(string text)
        {
            var table = new List<string[]>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool quoted = false;
            bool any = false;
            int start = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                any = true;
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else quoted = false;
                    }
                    else cell.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Length = 0; }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString());
                    cell.Length = 0;
                    table.Add(row.ToArray());
                    row.Clear();
                    any = false;
                }
                else cell.Append(c);
            }
            if (any)
            {
                row.Add(cell.ToString());
                table.Add(row.ToArray());
            }
            return table;
        }
    }
}
