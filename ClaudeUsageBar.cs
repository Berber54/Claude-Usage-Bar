// ClaudeUsageBar - Claude token usage in the system tray:
//   icon 1: skinny vertical battery gauge (Task Manager-style), orange fill, depletes top-down
//   icon 2: large percentage digits on a Claude-grey badge with an orange outline
//   left-click on either icon opens a custom dark flyout dashboard anchored to the tray
// Compile with build.bat (uses the csc.exe built into Windows, no SDK needed).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeUsageBar
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new TrayApp());
        }
    }

    class UsageWindow
    {
        public string Key;              // JSON key, e.g. "five_hour"
        public string Label;            // human label, e.g. "Session (5-hour)"
        public double Utilization;      // percent used, 0-100
        public bool HasUtilization;
        public DateTime? ResetsAt;      // UTC
    }

    // Everything parsed out of one usage API response.
    class UsageSnapshot
    {
        public List<UsageWindow> Windows = new List<UsageWindow>();
        public UsageWindow Find(string key)
        {
            foreach (UsageWindow w in Windows) if (w.Key == key) return w;
            return null;
        }
    }

    class TrayApp : ApplicationContext
    {
        // ---- appearance ----
        internal static readonly Color FILL     = Color.FromArgb(217, 119, 87);   // Claude orange
        internal static readonly Color FILL_LOW = Color.FromArgb(235, 100, 45);   // deeper orange under 15%
        static readonly Color CASE_CLR = Color.FromArgb(215, 215, 220);  // light case, visible on dark taskbar
        static readonly Color BODY_BG  = Color.FromArgb(45, 45, 50);

        // ---- polling ----
        const int POLL_MINUTES = 5;               // be gentle: endpoint rate-limits hard
        const string USAGE_URL = "https://api.anthropic.com/api/oauth/usage";
        // Rotated on 429: the endpoint rate-limits unknown user agents aggressively.
        static readonly string[] USER_AGENTS = {
            "claude-cli/2.1.0 (external, cli)",
            "claude-code/2.1.0"
        };

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        NotifyIcon trayBar, trayPct;
        Form sync;                      // hidden form used to marshal back to the UI thread
        System.Windows.Forms.Timer timer;
        Icon barIcon, pctIcon;
        IntPtr barH = IntPtr.Zero, pctH = IntPtr.Zero;
        FlyoutForm flyout;

        internal UsageWindow fiveHour, sevenDay;
        internal List<UsageWindow> windows = new List<UsageWindow>();
        internal string errorMsg = "starting...";
        string credPathUsed = null;
        internal DateTime lastSuccessUtc = DateTime.MinValue;
        internal DateTime nextFetchUtc = DateTime.MinValue;
        int backoffMinutes = POLL_MINUTES;
        int uaIndex = 0;
        bool fetching = false;
        bool showWeekly = false;
        bool showPct = true;

        static string ConfigDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageBar"); } }
        static string ConfigFile { get { return Path.Combine(ConfigDir, "config.txt"); } }
        static string OverrideFile { get { return Path.Combine(ConfigDir, "override.txt"); } }

        public TrayApp()
        {
            bool firstRun = !File.Exists(ConfigFile);
            LoadConfig();

            // The Run entry stores an absolute path; rewrite it so startup keeps
            // working after the exe is moved or rebuilt somewhere else.
            if (IsStartupEnabled()) SetStartup(true);

            sync = new Form();
            IntPtr force = sync.Handle;  // force handle creation for BeginInvoke

            trayBar = MakeIcon();
            trayPct = MakeIcon();
            UpdateIcons();
            trayBar.Visible = true;
            trayPct.Visible = showPct;

            if (firstRun)
                trayBar.ShowBalloonTip(8000, "Claude Usage Bar",
                    "Running. New tray icons start in the ^ overflow next to the clock - drag them onto the taskbar to pin them.",
                    ToolTipIcon.Info);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 15000; // check every 15s whether a fetch is due
            timer.Tick += delegate { MaybeFetch(); };
            timer.Start();
            MaybeFetch();
        }

        NotifyIcon MakeIcon()
        {
            var n = new NotifyIcon();
            n.ContextMenuStrip = BuildMenu();
            n.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleFlyout();
            };
            n.DoubleClick += delegate { showWeekly = !showWeekly; SaveConfig(); UpdateIcons(); };
            return n;
        }

        // ------------------------------------------------ fetch ------------------------------------------------

        void MaybeFetch()
        {
            if (fetching || DateTime.UtcNow < nextFetchUtc) return;
            fetching = true;
            ThreadPool.QueueUserWorkItem(delegate { FetchWorker(); });
        }

        void FetchWorker()
        {
            UsageSnapshot snap = null;
            string err = null;
            bool rateLimited = false;

            // Manual override: put a number 0-100 (percent REMAINING) in override.txt
            try
            {
                if (File.Exists(OverrideFile))
                {
                    double v;
                    if (double.TryParse(File.ReadAllText(OverrideFile).Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v))
                    {
                        snap = new UsageSnapshot();
                        var fh = new UsageWindow();
                        fh.Key = "five_hour"; fh.Label = WindowLabel("five_hour");
                        fh.Utilization = 100.0 - v; fh.HasUtilization = true;
                        snap.Windows.Add(fh);
                        FinishFetch(snap, null, false);
                        return;
                    }
                }
            }
            catch { }

            try
            {
                string token = ReadAccessToken();
                if (token == null)
                {
                    err = "No Claude credentials found. Checked: " + string.Join("; ", CredentialCandidates())
                        + ". Fix: install Claude Code, run 'claude' and log in once - or create override.txt (see README).";
                }
                else
                {
                    string json = HttpGet(USAGE_URL, token, USER_AGENTS[uaIndex]);
                    var ser = new JavaScriptSerializer();
                    var root = ser.DeserializeObject(json) as Dictionary<string, object>;
                    if (root != null)
                    {
                        snap = ParseSnapshot(root);
                        if (snap.Windows.Count == 0)
                        {
                            snap = null;
                            err = "Unexpected API response: " + Snippet(json);
                        }
                    }
                    else err = "Could not parse API response: " + Snippet(json);
                }
            }
            catch (WebException wex)
            {
                int status = 0; string body = null;
                var resp = wex.Response as HttpWebResponse;
                if (resp != null)
                {
                    status = (int)resp.StatusCode;
                    try { using (var sr = new StreamReader(resp.GetResponseStream())) body = sr.ReadToEnd(); }
                    catch { }
                }
                if (status == 429)
                {
                    rateLimited = true;
                    uaIndex = (uaIndex + 1) % USER_AGENTS.Length;   // rotate UA for next attempt
                    err = "Rate limited (HTTP 429) by the usage API - will retry with backoff. Showing last known value.";
                }
                else if (status == 401 || status == 403)
                {
                    err = "Auth failed (HTTP " + status + ") using token from " + credPathUsed + ". Token likely expired - open Claude Code once to refresh it. " + Snippet(body);
                }
                else if (status != 0)
                {
                    err = "HTTP " + status + ": " + Snippet(body);
                }
                else err = "Network error: " + wex.Message;
            }
            catch (Exception ex) { err = ex.Message; }

            FinishFetch(snap, err, rateLimited);
        }

        static string Snippet(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
        }

        void FinishFetch(UsageSnapshot snap, string err, bool rateLimited)
        {
            try
            {
                sync.BeginInvoke((MethodInvoker)delegate
                {
                    if (snap != null && snap.Windows.Count > 0)
                    {
                        UsageWindow nf = snap.Find("five_hour"), ns = snap.Find("seven_day");
                        // Keep the last known value for any window missing from this response.
                        if (nf == null && fiveHour != null) snap.Windows.Insert(0, fiveHour);
                        if (ns == null && sevenDay != null) snap.Windows.Add(sevenDay);
                        if (nf != null) fiveHour = nf; else if (fiveHour == null) fiveHour = snap.Find("five_hour");
                        if (ns != null) sevenDay = ns; else if (sevenDay == null) sevenDay = snap.Find("seven_day");
                        windows = snap.Windows;
                        errorMsg = null;
                        lastSuccessUtc = DateTime.UtcNow;
                        backoffMinutes = POLL_MINUTES;
                    }
                    else
                    {
                        errorMsg = err;
                        if (rateLimited) backoffMinutes = Math.Min(backoffMinutes * 2, 60);
                    }
                    nextFetchUtc = DateTime.UtcNow.AddMinutes(backoffMinutes);
                    fetching = false;
                    UpdateIcons();
                    if (flyout != null && flyout.Visible) flyout.Relayout();
                });
            }
            catch { fetching = false; }
        }

        // Parses every usage window in the response. The "limits" array is the
        // richest source - it carries model-scoped weekly windows (e.g. Fable)
        // that have no top-level key - so prefer it, falling back to the plain
        // five_hour/seven_day objects for older response shapes.
        static UsageSnapshot ParseSnapshot(Dictionary<string, object> root)
        {
            var snap = new UsageSnapshot();

            object lo;
            if (root.TryGetValue("limits", out lo))
            {
                var arr = lo as object[];
                if (arr != null)
                    foreach (object item in arr)
                        AddLimit(snap, item as Dictionary<string, object>);
            }

            if (snap.Windows.Count == 0)
            {
                string[] preferred = { "five_hour", "seven_day" };
                foreach (string k in preferred) AddWindow(snap, root, k);
                foreach (KeyValuePair<string, object> kv in root)
                {
                    if (Array.IndexOf(preferred, kv.Key) >= 0) continue;
                    AddWindow(snap, root, kv.Key);
                }
            }
            return snap;
        }

        // One entry of the "limits" array, e.g.:
        //   {kind:"weekly_scoped", percent:55, resets_at:"...",
        //    scope:{model:{display_name:"Fable"}}}
        static void AddLimit(UsageSnapshot snap, Dictionary<string, object> d)
        {
            if (d == null) return;
            var w = new UsageWindow();
            object p;
            if (d.TryGetValue("percent", out p) && p != null)
            {
                try
                {
                    w.Utilization = Convert.ToDouble(p, CultureInfo.InvariantCulture);
                    w.HasUtilization = true;
                }
                catch { }
            }
            object r;
            if (d.TryGetValue("resets_at", out r) && r is string)
            {
                DateTime dt;
                if (DateTime.TryParse((string)r, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                    w.ResetsAt = dt;
            }
            if (!w.HasUtilization && w.ResetsAt == null) return;

            string model = null;
            object so;
            if (d.TryGetValue("scope", out so))
            {
                var scope = so as Dictionary<string, object>;
                if (scope != null)
                {
                    object mo;
                    if (scope.TryGetValue("model", out mo))
                    {
                        var md = mo as Dictionary<string, object>;
                        if (md != null)
                        {
                            object dn;
                            if (md.TryGetValue("display_name", out dn)) model = dn as string;
                        }
                    }
                }
            }

            object ko;
            string kind = d.TryGetValue("kind", out ko) ? ko as string : null;
            if (kind == "session") { w.Key = "five_hour"; w.Label = "Session (5-hour)"; }
            else if (kind == "weekly_all") { w.Key = "seven_day"; w.Label = "Weekly (all models)"; }
            else if (!string.IsNullOrEmpty(model)) { w.Key = "weekly_" + model.ToLowerInvariant(); w.Label = "Weekly (" + model + ")"; }
            else if (!string.IsNullOrEmpty(kind)) { w.Key = kind; w.Label = WindowLabel(kind); }
            else { w.Key = "unknown"; w.Label = "Other limit"; }
            snap.Windows.Add(w);
        }

        static void AddWindow(UsageSnapshot snap, Dictionary<string, object> root, string key)
        {
            object o;
            if (!root.TryGetValue(key, out o)) return;
            var d = o as Dictionary<string, object>;
            if (d == null) return;
            if (!d.ContainsKey("utilization") && !d.ContainsKey("resets_at")) return;
            var w = new UsageWindow();
            w.Key = key;
            w.Label = WindowLabel(key);
            object u;
            if (d.TryGetValue("utilization", out u) && u != null)
            {
                try
                {
                    w.Utilization = Convert.ToDouble(u, CultureInfo.InvariantCulture);
                    w.HasUtilization = true;
                }
                catch { }
            }
            object r;
            if (d.TryGetValue("resets_at", out r) && r is string)
            {
                DateTime dt;
                if (DateTime.TryParse((string)r, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                    w.ResetsAt = dt;
            }
            snap.Windows.Add(w);
        }

        static string WindowLabel(string key)
        {
            string k = key.ToLowerInvariant();
            if (k == "five_hour") return "Session (5-hour)";
            if (k == "seven_day") return "Weekly (all models)";
            if (k == "seven_day_opus") return "Weekly (Opus)";
            if (k == "seven_day_sonnet") return "Weekly (Sonnet)";
            if (k.Contains("fable"))
            {
                if (k.StartsWith("five_hour")) return "Session (Claude Fable)";
                if (k.StartsWith("seven_day")) return "Weekly (Claude Fable)";
                return "Claude Fable";
            }
            string p = k.Replace("five_hour", "session").Replace("seven_day", "weekly").Replace('_', ' ').Trim();
            if (p.Length == 0) return key;
            return char.ToUpperInvariant(p[0]) + p.Substring(1);
        }

        static string HttpGet(string url, string bearer, string ua)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = 20000;
            req.Accept = "application/json";
            req.UserAgent = ua;
            req.Headers["Authorization"] = "Bearer " + bearer;
            req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd();
        }

        static string[] CredentialCandidates()
        {
            var list = new List<string>();
            string env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrEmpty(env)) list.Add(Path.Combine(env, ".credentials.json"));
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            list.Add(Path.Combine(up, ".claude", ".credentials.json"));
            string app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            list.Add(Path.Combine(app, "Claude", ".credentials.json"));
            string lapp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            list.Add(Path.Combine(lapp, "Claude", ".credentials.json"));
            list.Add(Path.Combine(lapp, "AnthropicClaude", ".credentials.json"));
            return list.ToArray();
        }

        string ReadAccessToken()
        {
            foreach (string path in CredentialCandidates())
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var ser = new JavaScriptSerializer();
                    var root = ser.DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                    if (root == null) continue;
                    object o;
                    if (!root.TryGetValue("claudeAiOauth", out o)) continue;
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    object t;
                    if (!d.TryGetValue("accessToken", out t)) continue;
                    string tok = t as string;
                    if (!string.IsNullOrEmpty(tok)) { credPathUsed = path; return tok; }
                }
                catch { }
            }
            return null;
        }

        // ------------------------------------------------ icons ------------------------------------------------

        double RemainingPercent()
        {
            UsageWindow w = showWeekly ? sevenDay : fiveHour;
            if (w == null || !w.HasUtilization) return -1;
            double rem = 100.0 - w.Utilization;
            if (rem < 0) rem = 0;
            if (rem > 100) rem = 100;
            return rem;
        }

        // The window whose reset time drives the time icon and flyout countdown.
        internal UsageWindow PrimaryWindow()
        {
            UsageWindow p = showWeekly ? sevenDay : fiveHour;
            if (p == null) p = fiveHour != null ? fiveHour : sevenDay;
            if (p == null && windows.Count > 0) p = windows[0];
            return p;
        }

        void SetIcon(NotifyIcon n, Bitmap bmp, ref Icon iconRef, ref IntPtr hRef)
        {
            IntPtr h = bmp.GetHicon();
            Icon ic = Icon.FromHandle(h);
            n.Icon = ic;
            if (iconRef != null) iconRef.Dispose();
            if (hRef != IntPtr.Zero) DestroyIcon(hRef);
            iconRef = ic; hRef = h;
        }

        void UpdateIcons()
        {
            double rem = RemainingPercent();

            using (Bitmap b = RenderBattery(rem)) SetIcon(trayBar, b, ref barIcon, ref barH);
            using (Bitmap b = RenderPercent(rem)) SetIcon(trayPct, b, ref pctIcon, ref pctH);

            string txt;
            if (rem >= 0)
                txt = "Claude " + (showWeekly ? "weekly" : "5h") + ": " + (int)Math.Round(rem) + "% left";
            else if (errorMsg != null)
                txt = "Claude: " + errorMsg;
            else
                txt = "Claude usage";
            if (txt.Length > 63) txt = txt.Substring(0, 60) + "...";   // NotifyIcon tooltip limit
            trayBar.Text = txt;
            trayPct.Text = txt;
        }

        // Skinny vertical battery gauge (Task Manager-style), fills bottom-up.
        static Bitmap RenderBattery(double rem)
        {
            var bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;

                Rectangle body = new Rectangle(10, 4, 12, 27);  // tall and narrow
                using (GraphicsPath p = Rounded(body, 3))
                {
                    using (var b = new SolidBrush(BODY_BG)) g.FillPath(b, p);
                    using (var pen = new Pen(CASE_CLR, 2f)) g.DrawPath(pen, p);
                }
                Rectangle nub = new Rectangle(13, 0, 6, 4);     // terminal nub on top
                using (var b = new SolidBrush(CASE_CLR)) g.FillRectangle(b, nub);

                if (rem >= 0)
                {
                    Rectangle inner = new Rectangle(body.X + 2, body.Y + 2, body.Width - 4, body.Height - 4);
                    int fh = (int)Math.Round(inner.Height * rem / 100.0);
                    if (rem > 0 && fh < 2) fh = 2;
                    if (fh > 0)
                    {
                        Rectangle fill = new Rectangle(inner.X, inner.Bottom - fh, inner.Width, fh);
                        Color c = rem <= 15 ? FILL_LOW : FILL;
                        using (GraphicsPath p = Rounded(fill, 2))
                        using (var b = new SolidBrush(c)) g.FillPath(b, p);
                    }
                }
                else
                {
                    using (var f = new Font("Segoe UI", 13f, FontStyle.Bold))
                    using (var b = new SolidBrush(FILL))
                    {
                        var sf = new StringFormat();
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        g.DrawString("!", f, b, new RectangleF(0, 2, 32, 28), sf);
                    }
                }
            }
            return bmp;
        }

        // Large digits like the Windows battery percentage readout, on a
        // Claude-grey badge with a Claude-orange outline so the readout is
        // recognizably Claude's while the digits stay high-contrast.
        static Bitmap RenderPercent(double rem)
        {
            var bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                // Badge fills the whole 32px canvas and the digits are sized down a
                // touch, so the number gets padding on every side and stays readable.
                Color outline = (rem >= 0 && rem <= 15) ? FILL_LOW : FILL;
                using (GraphicsPath p = Rounded(new Rectangle(0, 0, 31, 31), 8))
                {
                    using (var b = new SolidBrush(BODY_BG)) g.FillPath(b, p);
                    using (var pen = new Pen(outline, 2f)) g.DrawPath(pen, p);
                }

                string txt = rem >= 0 ? ((int)Math.Round(rem)).ToString() : "!";
                float size = txt.Length >= 3 ? 15f : (txt.Length == 2 ? 22f : 25f);
                // GenericTypographic removes GDI+'s built-in text padding, which otherwise
                // makes two large digits "not fit" in 32px and wrap - clipping the second digit.
                var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                using (var f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.White))
                    g.DrawString(txt, f, b, new RectangleF(0, 0, 32, 32), sf);
            }
            return bmp;
        }

        internal static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ------------------------------------------------ flyout ------------------------------------------------

        internal void ToggleFlyout()
        {
            if (flyout == null) flyout = new FlyoutForm(this);
            if (flyout.Visible) { flyout.HideFlyout(); return; }
            // Clicking the tray icon while the flyout is open deactivates (and hides) it
            // just before this click arrives - swallow the click so it toggles, not reopens.
            if ((DateTime.UtcNow - flyout.LastHiddenUtc).TotalMilliseconds < 300) return;
            flyout.ShowFlyout();
        }

        internal static string FormatCountdown(TimeSpan t)
        {
            if (t.TotalSeconds <= 0) return "any moment now";
            if (t.TotalDays >= 1) return string.Format("{0}d {1}h {2}m", (int)t.TotalDays, t.Hours, t.Minutes);
            if (t.TotalHours >= 1) return string.Format("{0}h {1:00}m {2:00}s", t.Hours, t.Minutes, t.Seconds);
            if (t.TotalMinutes >= 1) return string.Format("{0}m {1:00}s", t.Minutes, t.Seconds);
            return t.Seconds + "s";
        }

        internal static string FormatResetAt(DateTime utc)
        {
            DateTime l = utc.ToLocalTime();
            DateTime now = DateTime.Now;
            if (l.Date == now.Date) return "today at " + l.ToString("HH:mm");
            if (l.Date == now.Date.AddDays(1)) return "tomorrow at " + l.ToString("HH:mm");
            return l.ToString("ddd d MMM") + " at " + l.ToString("HH:mm");
        }

        // ------------------------------------------------ menu / config ------------------------------------------------

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Refresh now", null, delegate { nextFetchUtc = DateTime.MinValue; MaybeFetch(); });
            menu.Items.Add("Details", null, delegate { ToggleFlyout(); });

            var pctItem = new ToolStripMenuItem("Show percentage icon");
            pctItem.Checked = showPct;
            pctItem.Click += delegate
            {
                showPct = !showPct;
                pctItem.Checked = showPct;
                trayPct.Visible = showPct;
                SaveConfig();
            };
            menu.Items.Add(pctItem);

            var weeklyItem = new ToolStripMenuItem("Show weekly limit instead of 5-hour");
            weeklyItem.Checked = showWeekly;
            weeklyItem.Click += delegate
            {
                showWeekly = !showWeekly;
                weeklyItem.Checked = showWeekly;
                SaveConfig();
                UpdateIcons();
            };
            menu.Items.Add(weeklyItem);

            var startItem = new ToolStripMenuItem("Start with Windows");
            startItem.Checked = IsStartupEnabled();
            startItem.Click += delegate
            {
                SetStartup(!IsStartupEnabled());
                startItem.Checked = IsStartupEnabled();
            };
            menu.Items.Add(startItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApp(); });
            return menu;
        }

        void ExitApp()
        {
            timer.Stop();
            if (flyout != null) flyout.Dispose();
            trayBar.Visible = false; trayPct.Visible = false;
            trayBar.Dispose(); trayPct.Dispose();
            if (barIcon != null) barIcon.Dispose();
            if (pctIcon != null) pctIcon.Dispose();
            if (barH != IntPtr.Zero) DestroyIcon(barH);
            if (pctH != IntPtr.Zero) DestroyIcon(pctH);
            ExitThread();
        }

        static bool IsStartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return k != null && k.GetValue("ClaudeUsageBar") != null;
        }

        static void SetStartup(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (on) k.SetValue("ClaudeUsageBar", "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue("ClaudeUsageBar", false);
            }
        }

        void LoadConfig()
        {
            try
            {
                if (File.Exists(ConfigFile))
                {
                    foreach (string line in File.ReadAllLines(ConfigFile))
                    {
                        string[] kv = line.Split(new char[] { '=' }, 2);
                        if (kv.Length != 2) continue;
                        if (kv[0] == "weekly") showWeekly = kv[1] == "1";
                        else if (kv[0] == "pct") showPct = kv[1] == "1";
                    }
                }
            }
            catch { }
        }

        void SaveConfig()
        {
            try
            {
                Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(ConfigFile,
                    "weekly=" + (showWeekly ? "1" : "0") + "\r\n" +
                    "pct=" + (showPct ? "1" : "0") + "\r\n");
            }
            catch { }
        }
    }

    // Borderless dark flyout dashboard anchored to the tray, in the style of the
    // Windows 11 volume/battery flyouts: rounded corners (via DWM on Win11, a
    // Region fallback elsewhere), drop shadow, closes on focus loss or Escape.
    class FlyoutForm : Form
    {
        const int PAD = 20;
        const int FLY_W = 360;
        const int CORNER = 10;
        static readonly Color BG    = Color.FromArgb(45, 45, 50);     // #2D2D32
        static readonly Color TRACK = Color.FromArgb(62, 62, 68);
        static readonly Color CREAM = Color.FromArgb(242, 240, 230);
        static readonly Color MUTED = Color.FromArgb(160, 158, 152);
        static readonly Color EDGE  = Color.FromArgb(70, 70, 76);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWA_BORDER_COLOR = 34;
        const int DWMWCP_ROUND = 2;

        TrayApp app;
        System.Windows.Forms.Timer tick;   // drives the live countdown while visible
        Font fTitle, fBig, fCap, fBody, fBodyB, fSmall;
        bool dwmRound;
        public DateTime LastHiddenUtc = DateTime.MinValue;

        public FlyoutForm(TrayApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = BG;
            Width = FLY_W;
            Height = 200;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "Claude usage";

            fTitle = new Font("Segoe UI Semibold", 13f);
            fBig   = new Font("Segoe UI Semibold", 21f);
            fCap   = new Font("Segoe UI", 8f, FontStyle.Bold);
            fBody  = new Font("Segoe UI", 9.75f);
            fBodyB = new Font("Segoe UI Semibold", 9.75f);
            fSmall = new Font("Segoe UI", 8.25f);

            tick = new System.Windows.Forms.Timer();
            tick.Interval = 1000;
            tick.Tick += delegate { Invalidate(); };

            Deactivate += delegate { HideFlyout(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) HideFlyout(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tick != null) tick.Dispose();
                fTitle.Dispose(); fBig.Dispose(); fCap.Dispose();
                fBody.Dispose(); fBodyB.Dispose(); fSmall.Dispose();
            }
            base.Dispose(disposing);
        }

        public void ShowFlyout()
        {
            Relayout();
            PositionNearTray();
            ApplyRounding();
            Show();
            Activate();
            tick.Start();
        }

        public void HideFlyout()
        {
            if (!Visible) return;
            tick.Stop();
            Hide();
            LastHiddenUtc = DateTime.UtcNow;
        }

        void ApplyRounding()
        {
            int pref = DWMWCP_ROUND;
            try { dwmRound = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4) == 0; }
            catch { dwmRound = false; }
            if (dwmRound)
            {
                Region = null;
                int border = (EDGE.B << 16) | (EDGE.G << 8) | EDGE.R;   // COLORREF is 0x00BBGGRR
                try { DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, 4); } catch { }
            }
            else
            {
                using (GraphicsPath p = TrayApp.Rounded(new Rectangle(0, 0, Width, Height), CORNER))
                    Region = new Region(p);
            }
        }

        // Recomputes the content height (data may have changed) and repaints.
        public void Relayout()
        {
            int h;
            using (Graphics g = CreateGraphics()) h = DoLayout(g, false);
            if (Height != h)
            {
                Height = h;
                if (Visible)
                {
                    if (!dwmRound)
                        using (GraphicsPath p = TrayApp.Rounded(new Rectangle(0, 0, Width, Height), CORNER))
                            Region = new Region(p);
                    PositionNearTray();
                }
            }
            Invalidate();
        }

        // Anchors the flyout to the tray corner of whichever screen edge holds the
        // taskbar (detected by comparing the screen bounds to its working area),
        // clamped to stay fully on-screen at any resolution.
        void PositionNearTray()
        {
            Point cur = Cursor.Position;
            Screen scr = Screen.FromPoint(cur);
            Rectangle wa = scr.WorkingArea, b = scr.Bounds;
            int m = 12;
            int x = cur.X - Width / 2;
            int y;
            if (wa.Bottom < b.Bottom) y = wa.Bottom - Height - m;        // taskbar at bottom
            else if (wa.Top > b.Top) y = wa.Top + m;                     // taskbar at top
            else y = cur.Y - Height / 2;                                 // taskbar left/right
            if (wa.Left > b.Left) x = wa.Left + m;                       // taskbar at left
            else if (wa.Right < b.Right) x = wa.Right - Width - m;       // taskbar at right
            if (x < wa.Left + m) x = wa.Left + m;
            if (x > wa.Right - Width - m) x = wa.Right - Width - m;
            if (y < wa.Top + m) y = wa.Top + m;
            if (y > wa.Bottom - Height - m) y = wa.Bottom - Height - m;
            Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DoLayout(e.Graphics, true);
        }

        // Single layout routine: measures when draw=false (returns total height),
        // paints when draw=true, so measurement and drawing can never disagree.
        int DoLayout(Graphics g, bool draw)
        {
            if (draw)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            }
            int w = Width - PAD * 2;
            int y = 16;

            DrawLeft(g, draw, "Claude usage", fTitle, CREAM, y);
            y += 36;

            UsageWindow prim = app.PrimaryWindow();
            if (prim != null && prim.ResetsAt != null)
            {
                DrawLeft(g, draw, "CREDITS BACK TO 100%", fCap, MUTED, y);
                y += 20;
                DrawLeft(g, draw, TrayApp.FormatCountdown(prim.ResetsAt.Value - DateTime.UtcNow), fBig, TrayApp.FILL, y);
                y += 44;
                DrawLeft(g, draw, TrayApp.FormatResetAt(prim.ResetsAt.Value), fBody, CREAM, y);
                y += 26;
                y = Divider(g, draw, y);
            }

            if (app.windows.Count == 0)
            {
                DrawLeft(g, draw, "Loading usage data...", fBody, MUTED, y);
                y += 26;
            }

            foreach (UsageWindow uw in app.windows)
            {
                if (!uw.HasUtilization && uw.ResetsAt == null) continue;   // nothing to show
                DrawLeft(g, draw, uw.Label, fBody, CREAM, y);
                double rem = 100.0 - uw.Utilization;
                if (rem < 0) rem = 0;
                if (rem > 100) rem = 100;
                if (uw.HasUtilization)
                    DrawRight(g, draw, (int)Math.Round(rem) + "% left", fBodyB, rem <= 15 ? TrayApp.FILL_LOW : TrayApp.FILL, y, w);
                y += 24;
                if (uw.HasUtilization)
                {
                    if (draw)
                    {
                        var track = new Rectangle(PAD, y, w, 8);
                        using (GraphicsPath p = TrayApp.Rounded(track, 4))
                        using (var b = new SolidBrush(TRACK)) g.FillPath(b, p);
                        int fw = (int)Math.Round(w * rem / 100.0);
                        if (rem > 0 && fw < 8) fw = 8;
                        if (fw > 0)
                        {
                            var fill = new Rectangle(PAD, y, fw, 8);
                            using (GraphicsPath p = TrayApp.Rounded(fill, 4))
                            using (var b = new SolidBrush(rem <= 15 ? TrayApp.FILL_LOW : TrayApp.FILL)) g.FillPath(b, p);
                        }
                    }
                    y += 15;
                }
                if (uw.ResetsAt != null)
                {
                    DrawLeft(g, draw, "resets " + TrayApp.FormatResetAt(uw.ResetsAt.Value), fSmall, MUTED, y);
                    y += 17;
                }
                y += 9;
            }

            y = Divider(g, draw, y);

            string foot = app.lastSuccessUtc != DateTime.MinValue
                ? "Updated " + app.lastSuccessUtc.ToLocalTime().ToString("HH:mm") + "   |   next check " + app.nextFetchUtc.ToLocalTime().ToString("HH:mm")
                : "Waiting for first update...";
            DrawLeft(g, draw, foot, fSmall, MUTED, y);
            y += 18;

            if (app.errorMsg != null)
            {
                SizeF sz = g.MeasureString(app.errorMsg, fSmall, w);
                if (draw)
                    using (var b = new SolidBrush(TrayApp.FILL_LOW))
                        g.DrawString(app.errorMsg, fSmall, b, new RectangleF(PAD, y, w, sz.Height + 2));
                y += (int)Math.Ceiling(sz.Height) + 4;
            }

            y += 12;

            if (draw && !dwmRound)   // Win10 fallback: draw our own subtle border
                using (var pen = new Pen(EDGE))
                using (GraphicsPath p = TrayApp.Rounded(new Rectangle(0, 0, Width - 1, y - 1), CORNER))
                    g.DrawPath(pen, p);

            return y;
        }

        void DrawLeft(Graphics g, bool draw, string s, Font f, Color c, int y)
        {
            if (!draw) return;
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, PAD, y);
        }

        void DrawRight(Graphics g, bool draw, string s, Font f, Color c, int y, int w)
        {
            if (!draw) return;
            var sf = new StringFormat();
            sf.Alignment = StringAlignment.Far;
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, new RectangleF(PAD, y, w, 26), sf);
        }

        int Divider(Graphics g, bool draw, int y)
        {
            y += 3;
            if (draw) using (var pen = new Pen(TRACK)) g.DrawLine(pen, PAD, y, Width - PAD, y);
            return y + 13;
        }
    }
}
