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

                    // The team's top folder: walk up from a folder BDAT already knows.
                    string rootId = null, rootTitle = null;
                    foreach (string id in knownBookmarkIds)
                    {
                        if (string.IsNullOrEmpty(id)) continue;
                        object b = info.Invoke(null, new object[] { server, cc, id });
                        while (b != null)
                        {
                            object parent = Field(b, "parent");
                            if (parent == null) { rootId = Field(b, "physicalId") as string; rootTitle = Field(b, "title") as string; break; }
                            b = parent;
                        }
                        if (!string.IsNullOrEmpty(rootId)) break;
                    }
                    if (string.IsNullOrEmpty(rootId)) { PlatformSave.Log(LogName, "folder search: couldn't find the top folder"); return null; }

                    object result = expand.Invoke(null, new object[] { server, cc, rootId });
                    List<FoundBookmark> tree = Parse(result, rootId, rootTitle);
                    PlatformSave.Log(LogName, "folder search: read " + tree.Count + " folders under " + rootTitle + " (" + rootId + ")");
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
                tree.Add(new FoundBookmark { Id = folder.Key, Title = folder.Value, Path = PathOf(folder.Key, titles, parents) });
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
