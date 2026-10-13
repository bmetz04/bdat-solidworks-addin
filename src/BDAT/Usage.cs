using System;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using BDAT.Testing;
using Microsoft.Win32;

namespace BDAT
{
    /// <summary>
    /// Anonymous usage: which button was clicked, whether it worked, the error text if it didn't, how long BDAT
    /// worked on it (time in pop-ups not counted), and the BDAT and SolidWorks versions, sent to Ben's Google Sheet (see usage\SETUP.md).
    /// Never sends names, file or part names, or anything from a part. Each Windows user gets a random id so users
    /// can be counted without knowing who they are.
    ///
    /// Sending happens on a background thread with a short timeout and never shows anything or slows a button down.
    /// If it fails (offline, Google down) the row is dropped.
    ///
    /// Off when: the build has no usage key (only Publish builds on Ben's PC have one, from
    /// %USERPROFILE%\BDAT\usage-key.txt), test mode, the user turned it off from the BDAT version button,
    /// or C:\ProgramData\BDAT\no-usage.txt exists.
    /// </summary>
    internal static class Usage
    {
        private const string KeyResource = "BDAT.usage-key.txt";
        private const string SettingsKey = @"Software\BDAT";
        private const string OffValue = "UsageOff";
        private const string IdValue = "UsageId";
        private const string LogName = "usage";
        private const int MaxErrorLength = 300;

        private static readonly object Gate = new object();
        private static bool _loaded;
        private static string _url;
        private static string _key;
        private static string _solidWorksVersion = "";

        // The button running right now, and the first error message it showed.
        private static string _runningError;

        // Time BDAT spends working on the running button. Paused while any BDAT pop-up is open (Windows Forms says
        // when a modal window opens and closes on this thread), so reading and typing in pop-ups isn't counted.
        private static readonly System.Diagnostics.Stopwatch WorkTimer = new System.Diagnostics.Stopwatch();
        private static bool _running;
        private static bool _watchingPopUps;

        /// <summary>True when this build can send usage at all (it was built with a usage key).</summary>
        public static bool Available
        {
            get { Load(); return _url != null; }
        }

        /// <summary>True when usage is actually being sent from this PC.</summary>
        public static bool On
        {
            get
            {
                if (!Available || TestMode.Enabled) return false;
                if (File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"BDAT\no-usage.txt"))) return false;
                return !TurnedOff;
            }
        }

        /// <summary>The user's own off switch (BDAT version button), per Windows user.</summary>
        public static bool TurnedOff
        {
            get
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                        return key != null && "1".Equals(key.GetValue(OffValue) as string);
                }
                catch (Exception) { return false; }
            }
            set
            {
                try
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                        if (key != null) key.SetValue(OffValue, value ? "1" : "0", RegistryValueKind.String);
                }
                catch (Exception) { }
            }
        }

        public static void SetSolidWorksVersion(string version)
        {
            _solidWorksVersion = version ?? "";
        }

        /// <summary>BDAT loaded in SolidWorks. Counts how many people have it running.</summary>
        public static void Loaded()
        {
            Send("BDAT loaded", "ok", "", 0);
        }

        /// <summary>Call just before a button runs.</summary>
        public static void ButtonStarting()
        {
            _runningError = null;
            if (!_watchingPopUps)
            {
                _watchingPopUps = true;
                Application.EnterThreadModal += delegate { WorkTimer.Stop(); };
                Application.LeaveThreadModal += delegate { if (_running) WorkTimer.Start(); };
            }
            _running = true;
            WorkTimer.Reset();
            WorkTimer.Start();
        }

        /// <summary>BDAT showed an error message. Remembered (the first one) for the button that's running.</summary>
        public static void NoteError(string message)
        {
            if (_runningError == null) _runningError = message ?? "";
        }

        /// <summary>Stops the work timer, e.g. before showing a "failed" message.</summary>
        public static void StopTimer()
        {
            _running = false;
            WorkTimer.Stop();
        }

        /// <summary>Call after a button ran. crash is the exception it threw, if any.</summary>
        public static void ButtonFinished(string button, Exception crash)
        {
            StopTimer();
            long milliseconds = WorkTimer.ElapsedMilliseconds;
            string error = crash != null ? crash.Message : _runningError;
            string result = crash != null ? "crash" : _runningError != null ? "error" : "ok";
            _runningError = null;
            Send(button, result, error, milliseconds);
        }

        private static void Send(string button, string result, string error, long milliseconds)
        {
            if (!On) return;
            var form = new NameValueCollection();
            form["key"] = _key;
            form["id"] = UserId();
            form["button"] = button;
            form["result"] = result;
            form["error"] = Scrub(error);
            form["ms"] = milliseconds.ToString();
            form["bdat"] = BuildInfo.ShortVersion;
            form["sw"] = _solidWorksVersion;
            ThreadPool.QueueUserWorkItem(delegate { Post(form); });
        }

        private static void Post(NameValueCollection form)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string answer;
                using (var web = new QuickWebClient())
                    answer = Encoding.UTF8.GetString(web.UploadValues(_url, "POST", form)).Trim();
                // The script answers "ok"; anything else (wrong key, over the limit, a Google sign-in page) is logged.
                if (answer != "ok")
                    PlatformSave.Log(LogName, "the Sheet refused a row: " + (answer.Length > 200 ? answer.Substring(0, 200) : answer));
            }
            catch (Exception ex)
            {
                // Offline or Google down: drop it. Logged so a broken setup can be spotted.
                PlatformSave.Log(LogName, "send failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Error text without anything that could name a person or a part: file paths, file names and e-mail
        /// addresses are replaced, and it's cut to MaxErrorLength characters.
        /// </summary>
        internal static string Scrub(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string s = Regex.Replace(text, @"[A-Za-z]:\\[^\r\n""'<>|]*", "<path>");
            s = Regex.Replace(s, @"\\\\[^\r\n""'<>|]*", "<path>");
            s = Regex.Replace(s, @"[^\s""'<>|\\/]+\.(sldprt|sldasm|slddrw|prtdot|asmdot|drwdot|x_t|x_b|step|stp|dxf|dwg|csv|txt|log)\b", "<file>", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"[\w.+-]+@[\w-]+\.[\w.-]+", "<email>");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s.Length > MaxErrorLength ? s.Substring(0, MaxErrorLength) : s;
        }

        /// <summary>Random id for this Windows user, made the first time and kept in the registry.</summary>
        private static string UserId()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    string id = key == null ? null : key.GetValue(IdValue) as string;
                    if (string.IsNullOrEmpty(id))
                    {
                        id = Guid.NewGuid().ToString("N").Substring(0, 12);
                        if (key != null) key.SetValue(IdValue, id, RegistryValueKind.String);
                    }
                    return id;
                }
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        /// <summary>Reads the web address and key that build.bat embedded from usage-key.txt (line 1 address, line 2 key).</summary>
        private static void Load()
        {
            lock (Gate)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    using (Stream stream = typeof(Usage).Assembly.GetManifestResourceStream(KeyResource))
                    {
                        if (stream == null) return;
                        using (var reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            string url = (reader.ReadLine() ?? "").Trim();
                            string key = (reader.ReadLine() ?? "").Trim();
                            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && key.Length > 0)
                            {
                                _url = url;
                                _key = key;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    PlatformSave.Log(LogName, "couldn't read the usage key: " + ex.Message);
                }
            }
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
    }
}
