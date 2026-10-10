using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using BDAT.Testing;

namespace BDAT
{
    /// <summary>
    /// Finds a part or assembly in 3DEXPERIENCE by its number and downloads it, for Open from EBOM.
    ///
    /// The connector can download a model by its id (IEnoSwOpen.Download(physical id) returns the local file), but it has
    /// no way to look the id up from a name: its own title search (see ExistingParts) only answers which names exist.
    /// So the id comes from 3DSpace's engineering item search,
    ///   GET resources/v1/modeler/dseng/dseng:EngItem/search?$searchStr=BR-10101-AA
    /// sent through the connector's DSHttpWebRequest with its sign-in and SecurityContext, the same way BookmarkSearch
    /// makes folders. Read-only: it never changes anything in 3DEXPERIENCE.
    /// </summary>
    internal static class PlatformParts
    {
        private const string LogName = "new-from-ebom";

        /// <summary>The physical id of the part or assembly whose title is number, or null with the reason in error.</summary>
        public static string FindId(string number, out string error)
        {
            error = null;
            TestMode.BlockConnector("PlatformParts.FindId");
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
                Type requestType = wsapi == null ? null : wsapi.GetType("WSAPI.DSHttpWebRequest");
                if (lockField == null || getServer == null || login == null || requestType == null)
                {
                    error = "this version of the 3DEXPERIENCE connector doesn't offer a search BDAT can use.";
                    return null;
                }

                object serverLock = lockField.GetValue(null) ?? new object();
                string reply;
                lock (serverLock)
                {
                    object server = getServer.Invoke(null, null);
                    if (server == null) { error = "you're not connected to 3DEXPERIENCE."; return null; }
                    var cc = new CookieContainer();
                    object session = login.Invoke(null, new object[] { server, cc });
                    if (session == null) { error = "couldn't sign in to 3DSpace."; return null; }
                    // IEnoServer, the interop interface the server COM object is called through (LoginTo3DSpace's first parameter).
                    Type serverType = login.GetParameters()[0].ParameterType;
                    reply = Get(requestType, serverType, server, cc, SearchPath(number));
                }

                string id = IdFromSearch(reply, number);
                PlatformSave.Log(LogName, "open: search for " + number + " = " + (id ?? "no match in: " + Short(reply)));
                if (id == null) error = "3DEXPERIENCE's search didn't find " + number + ".";
                return id;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                PlatformSave.Log(LogName, "open: search for " + number + " failed: " + inner);
                error = inner.Message;
                return null;
            }
        }

        /// <summary>Downloads the model with this physical id to the work folder. Its local path, or null.</summary>
        public static string Download(Connector connector, string physicalId, out string error)
        {
            error = null;
            try
            {
                string path = connector.Call(connector.Manager("Open"), "IEnoSwOpen", "Download", physicalId) as string;
                PlatformSave.Log(LogName, "open: downloaded " + physicalId + " to " + (path ?? "nothing"));
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    error = "3DEXPERIENCE didn't download it.";
                    return null;
                }
                return path;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                PlatformSave.Log(LogName, "open: download of " + physicalId + " failed: " + inner);
                error = inner.Message;
                return null;
            }
        }

        internal static string SearchPath(string number)
        {
            return "resources/v1/modeler/dseng/dseng:EngItem/search?$searchStr=" + Uri.EscapeDataString(number) + "&$top=50";
        }

        /// <summary>
        /// The id of the item titled number (with or without .SLDPRT / .SLDASM, any case) in a search reply like
        /// {"member":[{"name":"prd-...","title":"BR-10101-AA","id":"9D4B...",...}]}. Null if none, or if several match.
        /// </summary>
        internal static string IdFromSearch(string json, string number)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var ids = new List<string>();
            foreach (Match m in Regex.Matches(json, "\\{[^{}]*\\}"))
            {
                Match id = Regex.Match(m.Value, "\"id\"\\s*:\\s*\"([^\"]+)\"");
                if (!id.Success) continue;
                bool named = false;
                foreach (string key in new[] { "title", "name" })
                {
                    Match v = Regex.Match(m.Value, "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"");
                    if (v.Success && string.Equals(ExistingParts.Number(v.Groups[1].Value), number, StringComparison.OrdinalIgnoreCase)) named = true;
                }
                if (named && !ids.Contains(id.Groups[1].Value)) ids.Add(id.Groups[1].Value);
            }
            return ids.Count == 1 ? ids[0] : null;
        }

        // A GET through the connector's DSHttpWebRequest, signed in with cc and sent in the login security context.
        private static string Get(Type requestType, Type serverType, object server, CookieContainer cc, string path)
        {
            string url = serverType.GetMethod("GetServiceUrl").Invoke(server, new object[] { 2 }) as string;
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("no 3DSpace address");
            if (!url.EndsWith("/")) url += "/";
            url += path;

            object request = null;
            foreach (MethodInfo m in requestType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                if (m.Name == "Create" && m.GetParameters().Length == 2) request = m.Invoke(null, new object[] { server, url });
            if (request == null) throw new InvalidOperationException("no DSHttpWebRequest");
            requestType.GetProperty("CookieContainer").SetValue(request, cc, null);
            requestType.GetProperty("Method").SetValue(request, "GET", null);

            // SecurityContext: role.organization.collabspace, as the connector sends it.
            object sc = serverType.GetMethod("GetLoginSC").Invoke(server, null);
            string context = Field(sc, "mbsRole") + "." + Field(sc, "mbsOrganization") + "." + Field(sc, "mbsCollabSpace");
            object headers = requestType.GetProperty("Headers").GetValue(request, null);
            MethodInfo add = headers.GetType().GetMethod("Add", new[] { typeof(string), typeof(string) });
            add.Invoke(headers, new object[] { "SecurityContext", WebUtility.UrlEncode(context) });

            MethodInfo getResponse = null;
            foreach (MethodInfo m in requestType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "GetResponse" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0) getResponse = m;
            if (getResponse == null) throw new InvalidOperationException("no GetResponse");
            using (var response = (WebResponse)getResponse.Invoke(request, null))
            using (var reader = new StreamReader(response.GetResponseStream()))
                return reader.ReadToEnd();
        }

        private static string Short(string s)
        {
            if (s == null) return "(no reply)";
            return s.Length <= 300 ? s : s.Substring(0, 300) + "...";
        }

        private static object Field(object target, string name)
        {
            FieldInfo f = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(target);
            PropertyInfo p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p == null ? null : p.GetValue(target, null);
        }
    }
}
