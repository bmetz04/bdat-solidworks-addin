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
                Session session = Session.Open(out error);
                if (session == null) return null;
                string reply = session.GetText(SearchPath(number));
                string id = IdFromSearch(reply, number);
                PlatformSave.Log(LogName, "open: search for " + number + " = " + (id ?? "no match in: " + Short(reply)));
                if (id == null) error = "3DX's search didn't find " + number + ".";
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
                    error = "3DX didn't download it.";
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

        /// <summary>
        /// A sign-in to 3DSpace through the connector, for requests sent with its DSHttpWebRequest: signed in with the
        /// connector's cookies and sent in the login security context. Read-only use only.
        /// </summary>
        internal sealed class Session
        {
            private object _lock;
            private object _server;
            private Type _serverType;
            private Type _requestType;
            private CookieContainer _cookies;

            /// <summary>Signs in to 3DSpace. Null with the reason in error if it can't.</summary>
            public static Session Open(out string error)
            {
                error = null;
                TestMode.BlockConnector("PlatformParts.Session");
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
                    error = "this version of the 3DX connector doesn't offer a search BDAT can use.";
                    return null;
                }

                var session = new Session();
                session._lock = lockField.GetValue(null) ?? new object();
                session._requestType = requestType;
                lock (session._lock)
                {
                    session._server = getServer.Invoke(null, null);
                    if (session._server == null) { error = "you're not connected to 3DX."; return null; }
                    session._cookies = new CookieContainer();
                    object signedIn = login.Invoke(null, new object[] { session._server, session._cookies });
                    if (signedIn == null) { error = "couldn't sign in to 3DSpace."; return null; }
                    // IEnoServer, the interop interface the server COM object is called through (LoginTo3DSpace's first parameter).
                    session._serverType = login.GetParameters()[0].ParameterType;
                }
                return session;
            }

            /// <summary>The address of one of the connector's services (2 is 3DSpace), or null.</summary>
            public string ServiceUrl(int service)
            {
                lock (_lock)
                    return _serverType.GetMethod("GetServiceUrl").Invoke(_server, new object[] { service }) as string;
            }

            /// <summary>A GET of a 3DSpace path (or a full address) as text. Throws if 3DX answers with an error.</summary>
            public string GetText(string pathOrUrl)
            {
                Reply reply = Send("GET", pathOrUrl, null);
                if (reply.Status >= 400) throw new WebException("3DX answered " + reply.Status + ": " + Short(reply.Text));
                return reply.Text;
            }

            /// <summary>
            /// Sends a request to a 3DSpace path (e.g. "resources/v1/...") or a full address, with body sent as JSON (null
            /// for none). An error answer like 404 comes back as a Reply too; only a broken connection throws.
            /// </summary>
            public Reply Send(string method, string pathOrUrl, string body)
            {
                string url = pathOrUrl;
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    string space = ServiceUrl(2);
                    if (string.IsNullOrEmpty(space)) throw new InvalidOperationException("no 3DSpace address");
                    url = url.StartsWith("/") ? new Uri(space).GetLeftPart(UriPartial.Authority) + url
                        : (space.EndsWith("/") ? space : space + "/") + url;
                }

                lock (_lock)
                {
                    object request = null;
                    foreach (MethodInfo m in _requestType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                        if (m.Name == "Create" && m.GetParameters().Length == 2) request = m.Invoke(null, new object[] { _server, url });
                    if (request == null) throw new InvalidOperationException("no DSHttpWebRequest");
                    _requestType.GetProperty("CookieContainer").SetValue(request, _cookies, null);
                    _requestType.GetProperty("Method").SetValue(request, method, null);

                    // SecurityContext: role.organization.collabspace, as the connector sends it.
                    object sc = _serverType.GetMethod("GetLoginSC").Invoke(_server, null);
                    string context = Field(sc, "mbsRole") + "." + Field(sc, "mbsOrganization") + "." + Field(sc, "mbsCollabSpace");
                    object headers = _requestType.GetProperty("Headers").GetValue(request, null);
                    MethodInfo add = headers.GetType().GetMethod("Add", new[] { typeof(string), typeof(string) });
                    add.Invoke(headers, new object[] { "SecurityContext", WebUtility.UrlEncode(context) });

                    if (body != null)
                    {
                        PropertyInfo contentType = _requestType.GetProperty("ContentType");
                        MethodInfo getStream = _requestType.GetMethod("GetRequestStream", Type.EmptyTypes);
                        if (contentType == null || getStream == null) throw new InvalidOperationException("DSHttpWebRequest can't send a body");
                        contentType.SetValue(request, "application/json", null);
                        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
                        using (var stream = (Stream)getStream.Invoke(request, null))
                            stream.Write(bytes, 0, bytes.Length);
                    }

                    MethodInfo getResponse = null;
                    foreach (MethodInfo m in _requestType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        if (m.Name == "GetResponse" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0) getResponse = m;
                    if (getResponse == null) throw new InvalidOperationException("no GetResponse");
                    try
                    {
                        using (var response = (WebResponse)getResponse.Invoke(request, null))
                            return Reply.From(response);
                    }
                    catch (TargetInvocationException ex)
                    {
                        // A 4xx or 5xx answer is still an answer.
                        var failed = ex.InnerException as WebException;
                        if (failed == null || failed.Response == null) throw;
                        using (failed.Response)
                            return Reply.From(failed.Response);
                    }
                }
            }
        }

        /// <summary>What came back from a request.</summary>
        internal sealed class Reply
        {
            public int Status;
            public string ContentType;
            public byte[] Data;

            public string Text
            {
                get { return Data == null ? "" : System.Text.Encoding.UTF8.GetString(Data); }
            }

            public static Reply From(WebResponse response)
            {
                var http = response as HttpWebResponse;
                var reply = new Reply { Status = http == null ? 200 : (int)http.StatusCode, ContentType = response.ContentType ?? "" };
                using (Stream stream = response.GetResponseStream())
                using (var copy = new MemoryStream())
                {
                    if (stream != null) stream.CopyTo(copy);
                    reply.Data = copy.ToArray();
                }
                return reply;
            }
        }

        internal static string Short(string s)
        {
            return Short(s, 300);
        }

        internal static string Short(string s, int max)
        {
            if (s == null) return "(no reply)";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        internal static object Field(object target, string name)
        {
            FieldInfo f = target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(target);
            PropertyInfo p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return p == null ? null : p.GetValue(target, null);
        }
    }
}
