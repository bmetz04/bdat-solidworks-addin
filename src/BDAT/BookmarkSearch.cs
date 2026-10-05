using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using BDAT.Testing;

namespace BDAT
{
    /// <summary>A 3DEXPERIENCE bookmark found by BookmarkSearch, with its path, e.g. "Formula UBC Racing > Suspension > A0704".</summary>
    internal sealed class FoundBookmark
    {
        public string Id;
        public string Title;
        public string Path;
        /// <summary>The folder it's in (null for the top folder).</summary>
        public string ParentId;
    }

    /// <summary>
    /// Finds 3DEXPERIENCE bookmarks (folders) by name, e.g. the "A0704" folder for Bellcranks.
    ///
    /// The connector's public API can't search bookmarks, but its own "download bookmark" feature reads a bookmark's
    /// whole folder tree through its internal web services, and this does the same, read-only:
    ///   lock TaskPaneInfra.IEC._serverLock; server = IEC.get_server(); cc = new CookieContainer();
    ///   WSAPI.WebServices.LoginTo3DSpace(server, cc)   (signs in with the session you're already logged in with)
    ///   WSAPI.WebServices.GetBookmarkInfo(server, cc, id)   (a bookmark's parents, to find the team's top folder)
    ///   WSAPI.WebServices.GetBookmarkTree(server, cc, id)   (every folder under it: ids, titles and parent links)
    /// The top folder is found from a bookmark BDAT already knows (McMaster Carr, or any remembered folder).
    /// It only ever reads. These are undocumented, so any failure just means "not found": the caller falls back to the
    /// bookmark picker. Everything is logged to %TEMP%\BDAT\new-from-ebom.log. In test mode it refuses, like Connector.
    /// </summary>
    internal static class BookmarkSearch
    {
        private const string LogName = "new-from-ebom";

        // The folder tree, read once and kept for a few minutes (one web call covers a whole session of saves).
        private static List<FoundBookmark> _tree;
        private static DateTime _treeTime;
        private static readonly TimeSpan TreeLifetime = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Folders named after the assembly number, best match first (exact title before "A0704 Bellcranks").
        /// Empty if none, or if the folders couldn't be read.
        /// </summary>
        public static List<FoundBookmark> Find(string assemblyNumber, IEnumerable<string> knownBookmarkIds)
        {
            var found = new List<FoundBookmark>();
            if (string.IsNullOrEmpty(assemblyNumber)) return found;
            List<FoundBookmark> tree = Tree(knownBookmarkIds);
            Available = tree != null;
            if (tree == null) return found;
            foreach (FoundBookmark b in tree)
                if (TitleIsFor(b.Title, assemblyNumber)) found.Add(b);
            found.Sort(delegate(FoundBookmark x, FoundBookmark y)
            {
                bool xe = string.Equals(x.Title.Trim(), assemblyNumber, StringComparison.OrdinalIgnoreCase);
                bool ye = string.Equals(y.Title.Trim(), assemblyNumber, StringComparison.OrdinalIgnoreCase);
                if (xe != ye) return xe ? -1 : 1;
                return string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);
            });
            PlatformSave.Log(LogName, "folder search for " + assemblyNumber + ": " + found.Count + " match(es)" +
                (found.Count > 0 ? ", first " + found[0].Path + " (" + found[0].Id + ")" : ""));
            return found;
        }

        /// <summary>
        /// True if a folder title is the assembly number, or starts with it and then a space or separator: "A0704",
        /// "a0704", "A0704 Bellcranks", "A0704 - Bellcranks". Not "A07041", and not a part number like "SU-A0704-AA".
        /// </summary>
        internal static bool TitleIsFor(string title, string assemblyNumber)
        {
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(assemblyNumber)) return false;
            string t = title.Trim();
            if (!t.StartsWith(assemblyNumber, StringComparison.OrdinalIgnoreCase)) return false;
            if (t.Length == assemblyNumber.Length) return true;
            char next = t[assemblyNumber.Length];
            return char.IsWhiteSpace(next) || next == '-' || next == '_' || next == ':' || next == '(' || next == '.';
        }

        /// <summary>False if the last attempt to read the folders failed (so "not found" may just mean "couldn't look").</summary>
        public static bool Available { get; private set; }

        /// <summary>Forget the folder tree, e.g. after Ben adds folders in 3DEXPERIENCE.</summary>
        public static void Refresh()
        {
            _tree = null;
        }

        // ---------------------------------------------------------------- reading the tree

        private static List<FoundBookmark> Tree(IEnumerable<string> knownBookmarkIds)
        {
            if (_tree != null && DateTime.Now - _treeTime < TreeLifetime) return _tree;
            TestMode.BlockConnector("BookmarkSearch");
            try
            {
                Assembly infra = Connector.ConnectorAssembly("TaskPaneInfra");
                Assembly wsapi = Connector.ConnectorAssembly("WSAPI");
                Type iec = infra == null ? null : infra.GetType("TaskPaneInfra.IEC");
                Type web = wsapi == null ? null : wsapi.GetType("WSAPI.WebServices");
                if (iec == null || web == null) { PlatformSave.Log(LogName, "folder search: connector web services not found"); return null; }

                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                FieldInfo lockField = iec.GetField("_serverLock", any);
                MethodInfo getServer = iec.GetMethod("get_server", any);
                MethodInfo login = web.GetMethod("LoginTo3DSpace", any);
                MethodInfo info = web.GetMethod("GetBookmarkInfo", any);
                MethodInfo expand = web.GetMethod("GetBookmarkTree", any);
                if (lockField == null || getServer == null || login == null || info == null || expand == null)
                {
                    PlatformSave.Log(LogName, "folder search: this connector version doesn't have the expected web services");
                    return null;
                }

                object serverLock = lockField.GetValue(null) ?? new object();
                lock (serverLock)
                {
                    object server = getServer.Invoke(null, null);
                    if (server == null) { PlatformSave.Log(LogName, "folder search: not connected"); return null; }
                    var cc = new CookieContainer();
                    if (!Succeeded(login.Invoke(null, new object[] { server, cc }))) { PlatformSave.Log(LogName, "folder search: 3DSpace login failed"); return null; }

                    // The team's top folder(s): walk all the way up from each folder BDAT already knows. GetBookmarkInfo
                    // only reports one parent level at a time, so ask again for each parent until there's none.
                    var roots = new Dictionary<string, string>(StringComparer.Ordinal); // id -> title
                    foreach (string start in knownBookmarkIds)
                    {
                        if (string.IsNullOrEmpty(start)) continue;
                        string id = start, title = null;
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        while (id != null && seen.Add(id) && seen.Count < 30)
                        {
                            object b = info.Invoke(null, new object[] { server, cc, id });
                            if (b == null) { id = null; break; }
                            // Use the highest ancestor this answer knows about.
                            object top = b;
                            while (Field(top, "parent") != null) top = Field(top, "parent");
                            string topId = Field(top, "physicalId") as string;
                            title = Field(top, "title") as string;
                            if (string.IsNullOrEmpty(topId) || topId == id) break; // nothing above: id is the top
                            id = topId;
                        }
                        if (!string.IsNullOrEmpty(id) && !roots.ContainsKey(id)) roots[id] = title ?? "";
                    }
                    if (roots.Count == 0) { PlatformSave.Log(LogName, "folder search: couldn't find the top folder"); return null; }

                    var tree = new List<FoundBookmark>();
                    var have = new HashSet<string>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, string> root in roots)
                    {
                        object result = expand.Invoke(null, new object[] { server, cc, root.Key });
                        List<FoundBookmark> part = Parse(result, root.Key, root.Value);
                        PlatformSave.Log(LogName, "folder search: read " + part.Count + " folders under " + root.Value + " (" + root.Key + ")");
                        foreach (FoundBookmark b in part)
                            if (have.Add(b.Id)) tree.Add(b);
                    }
                    _tree = tree;
                    _treeTime = DateTime.Now;
                    return tree;
                }
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                PlatformSave.Log(LogName, "folder search failed: " + inner);
                return null;
            }
        }

        // ---------------------------------------------------------------- making a folder

        /// <summary>
        /// Where a new folder for the assembly number should go: next to the other folders of the same system (A07xx for
        /// A0705), otherwise where most assembly folders are. Null if there are no assembly folders yet (then ask).
        /// </summary>
        public static FoundBookmark SuggestParent(string assemblyNumber, IEnumerable<string> knownBookmarkIds)
        {
            List<FoundBookmark> tree = Tree(knownBookmarkIds);
            return tree == null ? null : SuggestParent(assemblyNumber, tree);
        }

        internal static FoundBookmark SuggestParent(string assemblyNumber, List<FoundBookmark> tree)
        {
            var byId = new Dictionary<string, FoundBookmark>(StringComparer.Ordinal);
            foreach (FoundBookmark b in tree) byId[b.Id] = b;
            string system = assemblyNumber.Length >= 3 ? assemblyNumber.Substring(0, 3) : assemblyNumber; // "A07"
            var votesSame = new Dictionary<string, int>(StringComparer.Ordinal);
            var votesAll = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (FoundBookmark b in tree)
            {
                if (b.ParentId == null || !byId.ContainsKey(b.ParentId) || !LooksLikeAssemblyFolder(b.Title)) continue;
                Vote(votesAll, b.ParentId);
                if (b.Title.Trim().StartsWith(system, StringComparison.OrdinalIgnoreCase)) Vote(votesSame, b.ParentId);
            }
            string best = Top(votesSame) ?? Top(votesAll);
            return best == null ? null : byId[best];
        }

        // "A0704", "A0704 Bellcranks": A, four digits, then the end or a separator.
        private static bool LooksLikeAssemblyFolder(string title)
        {
            string t = (title ?? "").Trim();
            if (t.Length < 5 || char.ToUpperInvariant(t[0]) != 'A') return false;
            for (int i = 1; i < 5; i++) if (!char.IsDigit(t[i])) return false;
            return TitleIsFor(t, t.Substring(0, 5));
        }

        private static void Vote(Dictionary<string, int> votes, string id)
        {
            int n;
            votes.TryGetValue(id, out n);
            votes[id] = n + 1;
        }

        private static string Top(Dictionary<string, int> votes)
        {
            string best = null;
            int most = 0;
            foreach (KeyValuePair<string, int> v in votes)
                if (v.Value > most) { best = v.Key; most = v.Value; }
            return best;
        }

        /// <summary>
        /// Makes a folder (bookmark) titled title inside parentId, the way the connector's own "save folder" does
        /// (WebServices.CreateBookmark with the session's CSRF token). Only called when someone clicks "Make folder".
        /// Returns the new folder, or null with the reason in error.
        /// </summary>
        public static FoundBookmark Create(string parentId, string parentPath, string title, out string error)
        {
            error = null;
            TestMode.BlockConnector("BookmarkSearch.Create");
            try
            {
                Assembly infra = Connector.ConnectorAssembly("TaskPaneInfra");
                Assembly wsapi = Connector.ConnectorAssembly("WSAPI");
                Type iec = infra == null ? null : infra.GetType("TaskPaneInfra.IEC");
                Type web = wsapi == null ? null : wsapi.GetType("WSAPI.WebServices");
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                FieldInfo lockField = iec == null ? null : iec.GetField("_serverLock", any);
                MethodInfo getServer = iec == null ? null : iec.GetMethod("get_server", any);
                MethodInfo login = web == null ? null : web.GetMethod("LoginTo3DSpace", any);
                MethodInfo create = web == null ? null : web.GetMethod("CreateBookmark", any);
                if (lockField == null || getServer == null || login == null || create == null)
                {
                    error = "this version of the 3DEXPERIENCE connector doesn't offer it.";
                    return null;
                }

                object serverLock = lockField.GetValue(null) ?? new object();
                lock (serverLock)
                {
                    object server = getServer.Invoke(null, null);
                    if (server == null) { error = "you're not connected to 3DEXPERIENCE."; return null; }
                    var cc = new CookieContainer();
                    object session = login.Invoke(null, new object[] { server, cc });
                    if (!Succeeded(session)) { error = "couldn't sign in to 3DSpace."; return null; }
                    string csrfName = Property(session, "Item1") as string;
                    string csrfValue = Property(session, "Item2") as string;

                    PlatformSave.Log(LogName, "making folder \"" + title + "\" in " + parentPath + " (" + parentId + ")");
                    object result = create.Invoke(null, new object[] { server, cc, csrfName, csrfValue, parentId, title });
                    string id = CreatedId(result, title);
                    if (id == null)
                    {
                        string message = result == null ? null : Property(result, "message") as string;
                        error = string.IsNullOrEmpty(message) ? "3DEXPERIENCE didn't make it." : message;
                        PlatformSave.Log(LogName, "making folder failed: " + error);
                        return null;
                    }
                    PlatformSave.Log(LogName, "made folder \"" + title + "\" = " + id);
                    Refresh();
                    return new FoundBookmark { Id = id, Title = title, ParentId = parentId, Path = parentPath + " > " + title };
                }
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                PlatformSave.Log(LogName, "making folder failed: " + inner);
                error = inner.Message;
                return null;
            }
        }

        // The new folder's id from CreateBookmark's result: the item whose title matches (as the connector reads it).
        internal static string CreatedId(object result, string title)
        {
            IEnumerable items = result == null ? null : Field(result, "items") as IEnumerable;
            if (items == null) return null;
            foreach (object item in items)
            {
                string t = Field(item, "title") as string;
                string id = Field(item, "id") as string;
                if (!string.IsNullOrEmpty(id) && string.Equals(t, title, StringComparison.OrdinalIgnoreCase)) return id;
            }
            return null;
        }

        /// <summary>
        /// The expand result as folders with paths. Its Results hold folder entries (ResourceId + Ds6w_label) and links
        /// (From = parent id, To = child id), the same way the connector's bookmark download reads it.
        /// </summary>
        internal static List<FoundBookmark> Parse(object result, string rootId, string rootTitle)
        {
            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            var parents = new Dictionary<string, string>(StringComparer.Ordinal);
            IEnumerable results = result == null ? null : Property(result, "Results") as IEnumerable;
            if (results != null)
            {
                foreach (object r in results)
                {
                    string id = Property(r, "ResourceId") as string;
                    string label = Property(r, "Ds6w_label") as string;
                    string from = Property(r, "From") as string;
                    string to = Property(r, "To") as string;
                    if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(label)) titles[id] = label;
                    if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to)) parents[to] = from;
                }
            }
            if (!string.IsNullOrEmpty(rootId) && !titles.ContainsKey(rootId)) titles[rootId] = rootTitle ?? "";

            var tree = new List<FoundBookmark>();
            foreach (KeyValuePair<string, string> folder in titles)
            {
                string parent;
                tree.Add(new FoundBookmark { Id = folder.Key, Title = folder.Value, Path = PathOf(folder.Key, titles, parents), ParentId = parents.TryGetValue(folder.Key, out parent) ? parent : null });
            }
            return tree;
        }

        private static string PathOf(string id, Dictionary<string, string> titles, Dictionary<string, string> parents)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string at = id;
            while (at != null && seen.Add(at) && names.Count < 20)
            {
                string title;
                names.Insert(0, titles.TryGetValue(at, out title) ? title : "?");
                string parent;
                at = parents.TryGetValue(at, out parent) ? parent : null;
            }
            return string.Join(" > ", names.ToArray());
        }

        // LoginTo3DSpace answers true/false in some versions and a (bool, message) pair in others.
        private static bool Succeeded(object answer)
        {
            if (answer == null) return false;
            if (answer is bool) return (bool)answer;
            object first = Property(answer, "Item1");
            return first is bool ? (bool)first : true;
        }

        private static object Property(object target, string name)
        {
            PropertyInfo p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p == null ? null : p.GetValue(target, null);
        }

        private static object Field(object target, string name)
        {
            FieldInfo f = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(target);
            return Property(target, name);
        }
    }
}
