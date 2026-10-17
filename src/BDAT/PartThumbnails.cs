using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BDAT.Testing;

namespace BDAT
{
    /// <summary>
    /// Pictures of parts and assemblies for the Open from EBOM preview. Read-only: it never changes anything in 3DEXPERIENCE.
    ///
    /// First choice is the copy already in this PC's 3DEXPERIENCE work folder: every SOLIDWORKS file carries a preview
    /// picture, and Windows hands it out through the same thumbnail service Explorer uses (no SolidWorks call, no network).
    /// Otherwise, for a part that's in 3DEXPERIENCE, it asks the platform for its thumbnail: the engineering item search
    /// PlatformParts already uses, then the item's details, then 3DX's own search, taking the first picture address it
    /// finds. Which of those the team's platform answers isn't known yet, so the first lookup in a SolidWorks session
    /// writes what each one returned to %TEMP%\BDAT\new-from-ebom.log.
    ///
    /// Pictures found are kept for the rest of the SolidWorks session, so going back to a part is instant.
    /// </summary>
    internal static class PartThumbnails
    {
        private const string LogName = "new-from-ebom";
        private const int ThumbSize = 256;

        private static readonly Dictionary<string, Image> _found = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _gate = new object();

        /// <summary>The picture already found for this number in this SolidWorks session, or null.</summary>
        public static Image Cached(string number)
        {
            lock (_gate)
            {
                Image image;
                return _found.TryGetValue(number, out image) ? image : null;
            }
        }

        /// <summary>
        /// The picture of row: from the work folder copy, else (when inPlatform) from 3DEXPERIENCE. Null if there's none.
        /// Slow (it may go to 3DEXPERIENCE), so it's called from a background thread; that thread must be STA.
        /// </summary>
        public static Image Load(string workFolder, EbomRow row, bool inPlatform)
        {
            Image image = Cached(row.Number);
            if (image != null) return image;
            string local = LocalFile(workFolder, row.Number);
            if (local != null) image = ShellThumbnail(local);
            if (image == null && inPlatform && !TestMode.Enabled) image = FromPlatform(row.Number);
            if (image != null)
                lock (_gate) _found[row.Number] = image;
            return image;
        }

        /// <summary>The 3DEXPERIENCE work folder (where Open downloads to), or null. Call on SolidWorks' own thread.</summary>
        public static string WorkFolder()
        {
            if (TestMode.Enabled) return null;
            try
            {
                Connector connector = Connector.Find();
                if (connector == null) return null;
                return connector.Call(connector.Manager("Open"), "IEnoSwOpen3", "GetWorkFolder") as string;
            }
            catch (Exception ex)
            {
                Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                PlatformSave.Log(LogName, "preview: no work folder: " + inner.Message);
                return null;
            }
        }

        /// <summary>The copy of number in the work folder (BR-10101-AA.SLDPRT or .SLDASM), or null.</summary>
        internal static string LocalFile(string workFolder, string number)
        {
            if (string.IsNullOrEmpty(workFolder) || string.IsNullOrEmpty(number) || !Directory.Exists(workFolder)) return null;
            foreach (string extension in new[] { ".SLDPRT", ".SLDASM" })
            {
                string path = Path.Combine(workFolder, number + extension);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        // ---------------------------------------------------------------- the work folder copy

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, int flags, out IntPtr bitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext,
            [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        private const int ThumbnailOnly = 0x08;   // SIIGBF_THUMBNAILONLY: no file-type icon instead
        private const int BiggerSizeOk = 0x01;    // SIIGBF_BIGGERSIZEOK

        /// <summary>The file's preview picture as Explorer shows it, on white. Null if Windows has none for it.</summary>
        internal static Image ShellThumbnail(string path)
        {
            IShellItemImageFactory item = null;
            IntPtr handle = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out item);
                var size = new NativeSize { Width = ThumbSize, Height = ThumbSize };
                int hr = item.GetImage(size, ThumbnailOnly | BiggerSizeOk, out handle);
                if (hr != 0 || handle == IntPtr.Zero)
                {
                    PlatformSave.Log(LogName, "preview: no Windows thumbnail for " + path + " (0x" + hr.ToString("X8") + ")");
                    return null;
                }
                return OnWhite(handle);
            }
            catch (Exception ex)
            {
                PlatformSave.Log(LogName, "preview: Windows thumbnail of " + path + " failed: " + ex.Message);
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero) DeleteObject(handle);
                if (item != null) Marshal.ReleaseComObject(item);
            }
        }

        // Image.FromHbitmap drops the alpha channel, so a see-through thumbnail comes out on black. Read the pixels as
        // premultiplied ARGB instead (unless nothing in them is see-through) and draw them onto white.
        private static Image OnWhite(IntPtr handle)
        {
            using (Bitmap raw = Image.FromHbitmap(handle))
            {
                var result = new Bitmap(raw.Width, raw.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(result))
                {
                    g.Clear(Color.White);
                    if (Image.GetPixelFormatSize(raw.PixelFormat) != 32)
                    {
                        g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                        return result;
                    }
                    var rect = new Rectangle(0, 0, raw.Width, raw.Height);
                    BitmapData bits = raw.LockBits(rect, ImageLockMode.ReadOnly, raw.PixelFormat);
                    try
                    {
                        byte[] pixels = new byte[Math.Abs(bits.Stride) * bits.Height];
                        Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length);
                        bool usesAlpha = false;
                        for (int i = 3; i < pixels.Length; i += 4)
                            if (pixels[i] != 0) { usesAlpha = true; break; }
                        if (!usesAlpha)
                        {
                            g.DrawImage(raw, 0, 0, raw.Width, raw.Height);
                            return result;
                        }
                        using (var argb = new Bitmap(bits.Width, bits.Height, bits.Stride, PixelFormat.Format32bppPArgb, bits.Scan0))
                            g.DrawImage(argb, 0, 0, raw.Width, raw.Height);
                    }
                    finally
                    {
                        raw.UnlockBits(bits);
                    }
                }
                return result;
            }
        }

        // ---------------------------------------------------------------- 3DEXPERIENCE

        private static PlatformParts.Session _session;
        private static DateTime _sessionAt;
        private static bool _explored;
        private static int _hits, _misses;
        private static readonly object _platformGate = new object();

        /// <summary>The thumbnail 3DEXPERIENCE keeps for number, or null.</summary>
        private static Image FromPlatform(string number)
        {
            lock (_platformGate)
            {
                // Three misses and never a hit: this platform doesn't hand out thumbnails BDAT can find, so stop asking.
                if (_hits == 0 && _misses >= 3) return null;
                try
                {
                    PlatformParts.Session session = SignedIn();
                    if (session == null) return null;

                    string search = session.GetText(PlatformParts.SearchPath(number));
                    string id = PlatformParts.IdFromSearch(search, number);
                    if (!_explored) Explore(session, search);
                    if (id == null)
                    {
                        PlatformSave.Log(LogName, "preview: 3DX search didn't find " + number);
                        return null;
                    }

                    Image image = Fetch(session, ImageAddress(search, id), "search");
                    if (image == null)
                    {
                        PlatformParts.Reply details = session.Send("GET", "resources/v1/modeler/dseng/dseng:EngItem/" + id + "?$mask=dseng:EngItemMask.Details", null);
                        if (!_explored) PlatformSave.Log(LogName, "preview: item details " + details.Status + ": " + PlatformParts.Short(details.Text, 2000));
                        if (details.Status < 400) image = Fetch(session, ImageAddress(details.Text, id), "details");
                    }
                    if (image == null) image = FromFederatedSearch(session, number, id);
                    _explored = true;

                    if (image == null) _misses++; else _hits++;
                    PlatformSave.Log(LogName, "preview: " + number + (image == null ? " has no thumbnail BDAT could find" : " thumbnail from 3DX"));
                    return image;
                }
                catch (Exception ex)
                {
                    Exception inner = ex is TargetInvocationException && ex.InnerException != null ? ex.InnerException : ex;
                    PlatformSave.Log(LogName, "preview: 3DX thumbnail of " + number + " failed: " + inner);
                    _session = null;
                    _explored = true;
                    _misses++;
                    return null;
                }
            }
        }

        // One sign-in, reused for ten minutes, so clicking down the list doesn't sign in again for every row.
        private static PlatformParts.Session SignedIn()
        {
            if (_session != null && DateTime.Now - _sessionAt < TimeSpan.FromMinutes(10)) return _session;
            string error;
            _session = PlatformParts.Session.Open(out error);
            _sessionAt = DateTime.Now;
            if (_session == null) PlatformSave.Log(LogName, "preview: couldn't sign in to 3DX: " + error);
            return _session;
        }

        // The first lookup in a session logs what the platform offers, to see where its thumbnails come from.
        private static void Explore(PlatformParts.Session session, string search)
        {
            var log = new StringBuilder("preview: first 3DX lookup this session.");
            log.Append(Environment.NewLine).Append("  search reply: ").Append(PlatformParts.Short(search, 2000));
            for (int service = 0; service <= 12; service++)
            {
                try
                {
                    string url = session.ServiceUrl(service);
                    if (!string.IsNullOrEmpty(url)) log.Append(Environment.NewLine).Append("  service ").Append(service).Append(": ").Append(url);
                }
                catch (Exception)
                {
                    // Not a service this connector knows.
                }
            }
            try
            {
                Assembly wsapi = Connector.ConnectorAssembly("WSAPI");
                if (wsapi != null)
                {
                    foreach (Type type in wsapi.GetTypes())
                        foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                            if (Regex.IsMatch(m.Name, "thumb|preview|image", RegexOptions.IgnoreCase))
                                log.Append(Environment.NewLine).Append("  connector method: ").Append(type.FullName).Append(".").Append(m);
                }
            }
            catch (Exception ex)
            {
                log.Append(Environment.NewLine).Append("  couldn't list connector methods: ").Append(ex.Message);
            }
            PlatformSave.Log(LogName, log.ToString());
        }

        /// <summary>
        /// The first picture address in a JSON reply: a value under a key like "thumbnail" or "preview_url", or a
        /// {"name":"preview_url","value":"..."} pair. File-type icons don't count. Null if there's none.
        /// </summary>
        internal static string ImageAddress(string json, string id)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var patterns = new[]
            {
                "\"name\"\\s*:\\s*\"[^\"]*(?:thumbnail|preview|image)[^\"]*\"\\s*,\\s*\"value\"\\s*:\\s*\"([^\"]+)\"",
                "\"[^\"]*(?:thumbnail|preview|image)[^\"]*\"\\s*:\\s*\"([^\"]+)\"",
            };
            foreach (string pattern in patterns)
            {
                foreach (Match m in Regex.Matches(json, pattern, RegexOptions.IgnoreCase))
                {
                    string value = m.Groups[1].Value.Replace("\\/", "/").Replace("\\u0026", "&");
                    if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !value.StartsWith("/")) continue;
                    if (value.IndexOf("/icons/", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    return value;
                }
            }
            return null;
        }

        // 3DX's own search (the one behind the search bar), which returns a preview_url for each result.
        private static Image FromFederatedSearch(PlatformParts.Session session, string number, string id)
        {
            string space = session.ServiceUrl(2) ?? "";
            string search = null;
            for (int service = 0; service <= 12 && search == null; service++)
            {
                try
                {
                    string url = session.ServiceUrl(service);
                    if (!string.IsNullOrEmpty(url) && url.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0) search = url;
                }
                catch (Exception)
                {
                    // Not a service this connector knows.
                }
            }
            if (search == null)
            {
                if (!_explored) PlatformSave.Log(LogName, "preview: the connector has no 3DX search address");
                return null;
            }

            // The tenant is the first part of the 3DSpace address, e.g. R1132100123456 from r1132100123456-eu1-space....
            string tenant = "";
            Match host = Regex.Match(space, "//([^-./]+)");
            if (host.Success) tenant = host.Groups[1].Value.ToUpperInvariant();
            string body = "{\"label\":\"bdat-preview\",\"locale\":\"en\",\"select_predicate\":[\"ds6w:label\",\"physicalid\",\"preview_url\"]," +
                "\"with_indexing_date\":false,\"with_nls\":false,\"with_synthesis\":false," +
                "\"query\":\"" + number.Replace("\"", "") + "\",\"order_by\":\"desc\",\"order_field\":\"relevance\"," +
                "\"nresults\":10,\"start\":\"0\",\"source\":[\"3dspace\"],\"tenant\":\"" + tenant + "\"}";
            PlatformParts.Reply reply = session.Send("POST", search.TrimEnd('/') + "/search?xrequestedwith=xmlhttprequest", body);
            if (!_explored) PlatformSave.Log(LogName, "preview: 3DX search " + search + " answered " + reply.Status + ": " + PlatformParts.Short(reply.Text, 2000));
            if (reply.Status >= 400) return null;

            // Prefer the result that is this item.
            string text = reply.Text;
            int at = text.IndexOf(id, StringComparison.OrdinalIgnoreCase);
            string address = null;
            if (at >= 0)
            {
                int start = text.LastIndexOf("\"attributes\"", at, StringComparison.Ordinal);
                if (start >= 0)
                {
                    int end = text.IndexOf("\"attributes\"", at, StringComparison.Ordinal);
                    address = ImageAddress(end > start ? text.Substring(start, end - start) : text.Substring(start), id);
                }
            }
            if (address == null) address = ImageAddress(text, id);
            return Fetch(session, address, "3DX search");
        }

        private static Image Fetch(PlatformParts.Session session, string address, string from)
        {
            if (address == null) return null;
            PlatformParts.Reply reply = session.Send("GET", address, null);
            if (!_explored || _hits == 0)
                PlatformSave.Log(LogName, "preview: picture from " + from + " " + address + " answered " + reply.Status + " " + reply.ContentType + " (" + (reply.Data == null ? 0 : reply.Data.Length) + " bytes)");
            if (reply.Status >= 400 || reply.Data == null || reply.Data.Length == 0) return null;
            try
            {
                using (var stream = new MemoryStream(reply.Data))
                using (Image picture = Image.FromStream(stream))
                    return new Bitmap(picture);
            }
            catch (ArgumentException)
            {
                return null; // not a picture (e.g. a sign-in page)
            }
        }
    }
}
