using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace BDAT
{
    /// <summary>
    /// Which 3DEXPERIENCE bookmark (folder) is which assembly number, e.g. A0704 -> the "A0704" bookmark's id.
    /// The 3DEXPERIENCE connector can't look a bookmark up by name, so each folder is picked once and remembered:
    ///   - team list: release/ebom-folders.csv in the repo, downloaded like the EBOM (wins over the others),
    ///   - this PC: HKCU\Software\BDAT\EbomFolders, plus %LOCALAPPDATA%\BDAT\ebom-folders-picked.csv, which
    ///     Publish BDAT merges into the team list so a folder picked once is known to everyone.
    /// The CSV has the columns Assembly Number, Bookmark Id, Bookmark Title.
    /// </summary>
    internal static class EbomFolders
    {
        public const string Header = "Assembly Number,Bookmark Id,Bookmark Title";

        /// <summary>The team list in the repo (release/ebom-folders.csv on main).</summary>
        public const string TeamUrl = BuildInfo.ReleaseUrl + "ebom-folders.csv";

        private const string KeyPath = @"Software\BDAT\EbomFolders";

        private static string LocalFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BDAT"); }
        }

        /// <summary>Where the last downloaded team list is kept, for when it's offline.</summary>
        public static string TeamCachePath { get { return Path.Combine(LocalFolder, "ebom-folders.csv"); } }

        /// <summary>Folders picked on this PC, for Publish BDAT to merge into the team list.</summary>
        public static string PickedPath { get { return Path.Combine(LocalFolder, "ebom-folders-picked.csv"); } }

        /// <summary>The folder for an assembly number: team list first, then this PC's picks. Null if nobody has picked it.</summary>
        public static Bookmark Find(string assemblyNumber)
        {
            if (string.IsNullOrEmpty(assemblyNumber)) return null;
            Bookmark found;

            bool fresh;
            string team = Ebom.DownloadTeamFile(TeamUrl, TeamCachePath, delegate(string text) { Parse(text); }, out fresh);
            if (team != null)
            {
                try
                {
                    if (Parse(File.ReadAllText(team, Encoding.UTF8)).TryGetValue(assemblyNumber, out found)) return found;
                }
                catch (Exception)
                {
                    // A broken team list: fall back to this PC's picks.
                }
            }

            return Mine().TryGetValue(assemblyNumber, out found) ? found : null;
        }

        /// <summary>Every folder remembered for an assembly number, team list first, then this PC's (no repeats).</summary>
        public static List<Bookmark> Candidates(string assemblyNumber)
        {
            var list = new List<Bookmark>();
            if (string.IsNullOrEmpty(assemblyNumber)) return list;
            Bookmark found;
            bool fresh;
            string team = Ebom.DownloadTeamFile(TeamUrl, TeamCachePath, delegate(string text) { Parse(text); }, out fresh);
            try
            {
                if (team != null && Parse(File.ReadAllText(team, Encoding.UTF8)).TryGetValue(assemblyNumber, out found)) list.Add(found);
            }
            catch (Exception)
            {
                // A broken team list: just this PC's.
            }
            if (Mine().TryGetValue(assemblyNumber, out found) && (list.Count == 0 || list[0].Id != found.Id)) list.Add(found);
            return list;
        }

        /// <summary>
        /// Forgets this PC's folder for an assembly number if it's bookmarkId (e.g. it was deleted in 3DEXPERIENCE).
        /// The team list can only change through Publish BDAT, so a stale team entry is just skipped each time.
        /// </summary>
        public static void Forget(string assemblyNumber, string bookmarkId)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath, true))
                {
                    if (key != null && string.Equals(key.GetValue(assemblyNumber) as string, bookmarkId, StringComparison.Ordinal))
                    {
                        key.DeleteValue(assemblyNumber, false);
                        key.DeleteValue(assemblyNumber + " title", false);
                    }
                }
            }
            catch (Exception)
            {
                // Nothing in the registry to forget.
            }
            try
            {
                if (!File.Exists(PickedPath)) return;
                Dictionary<string, Bookmark> picked = Parse(File.ReadAllText(PickedPath, Encoding.UTF8));
                Bookmark mine;
                if (picked.TryGetValue(assemblyNumber, out mine) && mine.Id == bookmarkId)
                {
                    picked.Remove(assemblyNumber);
                    File.WriteAllText(PickedPath, Format(picked), new UTF8Encoding(false));
                }
            }
            catch (Exception)
            {
                // The picked list can't be changed: the folder is checked again next time anyway.
            }
        }

        /// <summary>Folders picked on this PC: the picked list, plus the registry (the registry wins).</summary>
        private static Dictionary<string, Bookmark> Mine()
        {
            var mine = new Dictionary<string, Bookmark>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(PickedPath)) mine = Parse(File.ReadAllText(PickedPath, Encoding.UTF8));
            }
            catch (Exception)
            {
                // A broken picked list: the registry may still have them.
            }
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (key != null)
                    {
                        foreach (string name in key.GetValueNames())
                        {
                            if (name.EndsWith(" title", StringComparison.OrdinalIgnoreCase)) continue;
                            string id = key.GetValue(name) as string;
                            if (string.IsNullOrEmpty(id)) continue;
                            string title = key.GetValue(name + " title") as string;
                            mine[name] = new Bookmark { Id = id, Title = string.IsNullOrEmpty(title) ? name : title };
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Nothing remembered in the registry.
            }
            return mine;
        }

        /// <summary>Every known folder: this PC's picks, overridden by the team list.</summary>
        public static Dictionary<string, Bookmark> All()
        {
            Dictionary<string, Bookmark> mine = Mine();

            bool fresh;
            string team = Ebom.DownloadTeamFile(TeamUrl, TeamCachePath, delegate(string text) { Parse(text); }, out fresh);
            if (team == null) return mine;
            try
            {
                return Merge(mine, Parse(File.ReadAllText(team, Encoding.UTF8)));
            }
            catch (Exception)
            {
                return mine;
            }
        }

        /// <summary>Remembers a picked folder on this PC and adds it to the picked list for the team.</summary>
        public static void Remember(string assemblyNumber, Bookmark folder)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (key != null)
                    {
                        key.SetValue(assemblyNumber, folder.Id, RegistryValueKind.String);
                        key.SetValue(assemblyNumber + " title", folder.Title ?? "", RegistryValueKind.String);
                    }
                }
            }
            catch (Exception)
            {
                // The picked list below still has it.
            }
            try
            {
                var picked = File.Exists(PickedPath)
                    ? Parse(File.ReadAllText(PickedPath, Encoding.UTF8))
                    : new Dictionary<string, Bookmark>(StringComparer.OrdinalIgnoreCase);
                picked[assemblyNumber] = folder;
                Directory.CreateDirectory(LocalFolder);
                File.WriteAllText(PickedPath, Format(picked), new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // The registry still has it; only sharing it with the team is lost.
            }
        }

        /// <summary>Reads the folder list. Throws InvalidDataException if it isn't one.</summary>
        public static Dictionary<string, Bookmark> Parse(string csv)
        {
            List<string[]> table = Ebom.ReadCsv(csv ?? "");
            if (table.Count == 0 || table[0].Length < 2 || !string.Equals(table[0][0].Trim(), "Assembly Number", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("This isn't the EBOM folder list (it should start with \"" + Header + "\").");
            var folders = new Dictionary<string, Bookmark>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < table.Count; i++)
            {
                string[] cells = table[i];
                string number = cells.Length > 0 ? cells[0].Trim() : "";
                string id = cells.Length > 1 ? cells[1].Trim() : "";
                if (number.Length == 0 || id.Length == 0) continue;
                string title = cells.Length > 2 ? cells[2].Trim() : "";
                folders[number] = new Bookmark { Id = id, Title = title.Length > 0 ? title : number };
            }
            return folders;
        }

        /// <summary>The list as CSV, sorted by assembly number.</summary>
        public static string Format(IDictionary<string, Bookmark> folders)
        {
            var numbers = new List<string>(folders.Keys);
            numbers.Sort(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder(Header + "\r\n");
            foreach (string number in numbers)
                sb.Append(Quote(number)).Append(',').Append(Quote(folders[number].Id)).Append(',').Append(Quote(folders[number].Title)).Append("\r\n");
            return sb.ToString();
        }

        /// <summary>The team list with the picked folders added. A pick replaces the team's entry for the same number.</summary>
        public static Dictionary<string, Bookmark> Merge(IDictionary<string, Bookmark> team, IDictionary<string, Bookmark> picked)
        {
            var merged = new Dictionary<string, Bookmark>(team, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Bookmark> pair in picked) merged[pair.Key] = pair.Value;
            return merged;
        }

        /// <summary>True if the bookmark's title names the assembly number, e.g. "A0704" or "A0704 Bellcranks" for A0704.</summary>
        public static bool TitleMatches(string title, string assemblyNumber)
        {
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(assemblyNumber)) return false;
            int at = title.IndexOf(assemblyNumber, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            // Whole word only: "A07041" isn't A0704.
            int end = at + assemblyNumber.Length;
            bool before = at == 0 || !char.IsLetterOrDigit(title[at - 1]);
            bool after = end == title.Length || !char.IsLetterOrDigit(title[end]);
            return before && after;
        }

        private static string Quote(string value)
        {
            value = value ?? "";
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
