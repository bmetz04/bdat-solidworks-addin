using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using BDAT.Testing;

namespace BDAT
{
    /// <summary>An item in a 3DX folder: its title, physical id, and the folder it's in.</summary>
    internal sealed class FolderItem
    {
        public string Id, Title, Type, Usage, Extension, FolderId, FolderPath;
    }

    /// <summary>
    /// What Check Top Level reads from 3DX, read-only, through the connector's own (undocumented) web services in
    /// WSAPI.WebServices, the same way BookmarkSearch reads the folder tree:
    ///   CVServletNavigateBookmarksOfObject(server, cc, phid, withParents)  the folders an item is in
    ///   GetBookmarkTree(server, cc, folderId)                              every folder under a folder
    ///   GetBookmarksItems(server, cc, folderIds)                           what's in each of those folders
    /// Everything is logged to %TEMP%\BDAT\check-top-level.log. In test mode it refuses, like Connector.
    /// </summary>
    internal static class TopLevelCheck
    {
        public const string LogName = "check-top-level";

        /// <summary>The folders (with all their subfolders) that hold the item, and every item in them.</summary>
        internal sealed class FolderContents
        {
            public List<FoundBookmark> Folders = new List<FoundBookmark>();   // the folders the top level is in
            public List<FoundBookmark> AllFolders = new List<FoundBookmark>(); // those plus every subfolder
            public List<FolderItem> Items = new List<FolderItem>();
        }

        /// <summary>
        /// The folders 3DX has the item with this physical id in, their subfolders, and everything in all of them.
        /// Null with the reason in error if 3DX couldn't be asked.
        /// </summary>
        public static FolderContents Read(string physicalId, out string error)
        {
            error = null;
            TestMode.BlockConnector("TopLevelCheck");
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
                MethodInfo foldersOf = web == null ? null : web.GetMethod("CVServletNavigateBookmarksOfObject", any);
                MethodInfo tree = web == null ? null : web.GetMethod("GetBookmarkTree", any);
                MethodInfo items = web == null ? null : web.GetMethod("GetBookmarksItems", any);
                if (lockField == null || getServer == null || login == null || foldersOf == null || tree == null || items == null)
                {
                    Log("this connector version doesn't have the expected web services");
                    error = "this version of the 3DX connector doesn't offer a way for BDAT to read folders.";
                    return null;
                }

                object serverLock = lockField.GetValue(null) ?? new object();
                lock (serverLock)
                {
                    object server = getServer.Invoke(null, null);
                    if (server == null) { error = "you're not connected to 3DX."; return null; }
                    var cc = new CookieContainer();
                    if (!Succeeded(login.Invoke(null, new object[] { server, cc }))) { error = "couldn't sign in to 3DSpace."; return null; }

                    var contents = new FolderContents();

                    // 1. The folders the top level is in.
                    object found = foldersOf.Invoke(null, new object[] { server, cc, physicalId, false });
                    contents.Folders = FoldersFrom(found);
                    var names = new List<string>();
                    foreach (FoundBookmark f in contents.Folders) names.Add(f.Title + " (" + f.Id + ")");
                    Log("folders holding " + physicalId + ": " + (names.Count == 0 ? "none" : string.Join(", ", names.ToArray())));
                    if (contents.Folders.Count == 0) return contents;

                    // 2. Those folders and every folder under them.
                    var have = new HashSet<string>(StringComparer.Ordinal);
                    foreach (FoundBookmark folder in contents.Folders)
                    {
                        List<FoundBookmark> under;
                        try { under = BookmarkSearch.Parse(tree.Invoke(null, new object[] { server, cc, folder.Id }), folder.Id, folder.Title); }
                        catch (TargetInvocationException ex)
                        {
                            Log("couldn't read the folders under " + folder.Title + ": " + (ex.InnerException ?? ex).Message);
                            under = new List<FoundBookmark> { new FoundBookmark { Id = folder.Id, Title = folder.Title, Path = folder.Title } };
                        }
                        foreach (FoundBookmark b in under)
                            if (have.Add(b.Id)) contents.AllFolders.Add(b);
                    }
                    Log("read " + contents.AllFolders.Count + " folder(s) including subfolders");

                    // 3. Everything in them, in one call.
                    Type listType = items.GetParameters()[2].ParameterType;
                    object ids = MakeIdList(listType, contents.AllFolders);
                    object reply = items.Invoke(null, new object[] { server, cc, ids });
                    contents.Items = ItemsFrom(reply, contents.AllFolders);
                    Log("found " + contents.Items.Count + " item(s) in them");
                    int shown = 0;
                    foreach (FolderItem i in contents.Items)
                    {
                        if (shown++ >= 400) { Log("  ..."); break; }
                        Log("  " + i.Title + " | type " + i.Type + " | usage " + i.Usage + " | ext " + i.Extension + " | " + i.FolderPath + " | " + i.Id +
                            (IsAssembly(i) ? " | assembly" : ""));
                    }
                    return contents;
                }
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                Log("reading the folders failed: " + inner);
                error = inner.Message;
                return null;
            }
        }

        // GetBookmarksItems takes the folder ids as a List<string> (or, just in case, a string[]).
        private static object MakeIdList(Type listType, List<FoundBookmark> folders)
        {
            var ids = new List<string>();
            foreach (FoundBookmark f in folders) ids.Add(f.Id);
            if (listType.IsArray) return ids.ToArray();
            if (listType.IsAssignableFrom(typeof(List<string>))) return ids;
            var list = (IList)Activator.CreateInstance(listType);
            foreach (string id in ids) list.Add(id);
            return list;
        }

        /// <summary>The folders in CVServletNavigateBookmarksOfObject's answer: a list of objects with id and title.</summary>
        internal static List<FoundBookmark> FoldersFrom(object answer)
        {
            var folders = new List<FoundBookmark>();
            var have = new HashSet<string>(StringComparer.Ordinal);
            IEnumerable list = answer as IEnumerable;
            if (list == null) return folders;
            foreach (object o in list)
            {
                if (o == null) continue;
                string id = Field(o, "id") as string;
                string title = Field(o, "title") as string ?? "";
                if (string.IsNullOrEmpty(id) || !have.Add(id)) continue;
                folders.Add(new FoundBookmark { Id = id, Title = title, Path = title });
            }
            return folders;
        }

        /// <summary>
        /// The items in GetBookmarksItems' answer. Its Results hold objects (ResourceId, Ds6w_label, Ds6w_type...) and
        /// links (From = folder id, To = item id). A folder's own entry (its id is one of the folders) is left out.
        /// </summary>
        internal static List<FolderItem> ItemsFrom(object answer, List<FoundBookmark> folders)
        {
            var byFolder = new Dictionary<string, FoundBookmark>(StringComparer.Ordinal);
            foreach (FoundBookmark f in folders) byFolder[f.Id] = f;
            var objects = new Dictionary<string, FolderItem>(StringComparer.Ordinal);
            var order = new List<string>();
            var folderOf = new Dictionary<string, string>(StringComparer.Ordinal);
            IEnumerable results = answer == null ? null : Field(answer, "Results") as IEnumerable;
            if (results == null) return new List<FolderItem>();
            foreach (object r in results)
            {
                if (r == null) continue;
                string from = Field(r, "From") as string;
                string to = Field(r, "To") as string;
                if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(to))
                {
                    if (byFolder.ContainsKey(from) && !folderOf.ContainsKey(to)) folderOf[to] = from;
                    continue;
                }
                string id = Field(r, "ResourceId") as string;
                if (string.IsNullOrEmpty(id) || byFolder.ContainsKey(id) || objects.ContainsKey(id)) continue;
                objects[id] = new FolderItem
                {
                    Id = id,
                    Title = Field(r, "Ds6w_label") as string ?? "",
                    Type = Field(r, "Ds6w_type") as string ?? "",
                    Usage = Field(r, "Bo_plmentity_v_usage") as string ?? "",
                    Extension = Field(r, "Ds6w_docextension") as string ?? "",
                };
                order.Add(id);
            }
            var found = new List<FolderItem>();
            foreach (string id in order)
            {
                FolderItem item = objects[id];
                string folderId;
                FoundBookmark folder;
                if (folderOf.TryGetValue(id, out folderId) && byFolder.TryGetValue(folderId, out folder))
                {
                    item.FolderId = folder.Id;
                    item.FolderPath = folder.Path;
                }
                found.Add(item);
            }
            return found;
        }

        /// <summary>
        /// True for a SOLIDWORKS assembly: a physical product that isn't a 3D part, drawing or document. If 3DX gives
        /// the file extension, that decides it.
        /// </summary>
        internal static bool IsAssembly(FolderItem item)
        {
            if (item == null) return false;
            string ext = (item.Extension ?? "").Trim().TrimStart('.');
            if (ext.Length > 0) return ext.Equals("SLDASM", StringComparison.OrdinalIgnoreCase);
            string title = item.Title ?? "";
            if (title.EndsWith(".SLDASM", StringComparison.OrdinalIgnoreCase)) return true;
            if (title.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase) || title.EndsWith(".SLDDRW", StringComparison.OrdinalIgnoreCase)) return false;
            if ((item.Usage ?? "").IndexOf("3DPart", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            string type = item.Type ?? "";
            return type.IndexOf("VPMReference", StringComparison.OrdinalIgnoreCase) >= 0
                || type.Equals("Physical Product", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The assemblies in the folders that aren't anywhere in the top level, sorted by folder then name, each once.
        /// An item counts as in it if its id or its name matches a component (any revision). The top level itself, and
        /// any other top level, are left out.
        /// </summary>
        internal static List<FolderItem> Missing(List<FolderItem> items, string topLevelId, string topLevelTitle,
            ICollection<string> componentIds, ICollection<string> componentTitles)
        {
            var ids = new HashSet<string>(componentIds ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string t in componentTitles ?? new string[0]) titles.Add(ExistingParts.Number(t));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<FolderItem>();
            foreach (FolderItem i in items ?? new List<FolderItem>())
            {
                if (!IsAssembly(i)) continue;
                string name = ExistingParts.Number(i.Title);
                if (string.Equals(i.Id, topLevelId, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(name, ExistingParts.Number(topLevelTitle), StringComparison.OrdinalIgnoreCase)) continue;
                if (PlatformParts.IsTopLevelName(name)) continue;
                if (ids.Contains(i.Id) || titles.Contains(name)) continue;
                if (!seen.Add(name)) continue;
                missing.Add(i);
            }
            missing.Sort(delegate(FolderItem a, FolderItem b)
            {
                int c = string.Compare(a.FolderPath ?? "", b.FolderPath ?? "", StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
            });
            return missing;
        }

        public static void Log(string message)
        {
            PlatformSave.Log(LogName, message);
        }

        // LoginTo3DSpace answers true/false in some versions and a (bool, message) pair in others.
        private static bool Succeeded(object answer)
        {
            if (answer == null) return false;
            if (answer is bool) return (bool)answer;
            object first = Field(answer, "Item1");
            return first is bool ? (bool)first : true;
        }

        private static object Field(object target, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo p = target.GetType().GetProperty(name, flags);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(target, null);
            FieldInfo f = target.GetType().GetField(name, flags);
            return f == null ? null : f.GetValue(target);
        }
    }
}
