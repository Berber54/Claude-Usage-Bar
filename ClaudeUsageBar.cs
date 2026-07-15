// ClaudeUsageBar - Claude token usage in the system tray:
//   icon 1: skinny vertical battery gauge (Task Manager-style), orange fill, depletes top-down
//   icon 2: large percentage digits (like the Windows battery percentage)
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
        public double Utilization;      // percent used, 0-100
        public DateTime? ResetsAt;      // UTC
    }

    class TrayApp : ApplicationContext
    {
        // ---- appearance ----
        static readonly Color FILL     = Color.FromArgb(217, 119, 87);   // Claude orange
        static readonly Color FILL_LOW = Color.FromArgb(235, 100, 45);   // deeper orange under 15%
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
        Icon barIcon, pctIcon; IntPtr barH = IntPtr.Zero, pctH = IntPtr.Zero;

        UsageWindow fiveHour, sevenDay;
        string errorMsg = "starting...";
        string credPathUsed = null;
        DateTime lastSuccessUtc = DateTime.MinValue;
        DateTime nextFetchUtc = DateTime.MinValue;
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
                    "Running. New tray icons start in the ^ overflow next to the clock - drag both onto the taskbar to pin them.",
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
                if (e.Button == MouseButtons.Left) ShowDetails();
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
            UsageWindow fh = null, sd = null;
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
                        fh = new UsageWindow(); fh.Utilization = 100.0 - v;
                        FinishFetch(fh, null, null, false);
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
                        fh = ParseWindow(root, "five_hour");
                        sd = ParseWindow(root, "seven_day");
                        if (fh == null && sd == null) err = "Unexpected API response: " + Snippet(json);
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

            FinishFetch(fh, sd, err, rateLimited);
        }

        static string Snippet(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
        }

        void FinishFetch(UsageWindow fh, UsageWindow sd, string err, bool rateLimited)
        {
            try
            {
                sync.BeginInvoke((MethodInvoker)delegate
                {
                    if (fh != null || sd != null)
                    {
                        if (fh != null) fiveHour = fh;
                        if (sd != null) sevenDay = sd;
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
                });
            }
            catch { fetching = false; }
        }

        static UsageWindow ParseWindow(Dictionary<string, object> root, string key)
        {
            object o;
            if (!root.TryGetValue(key, out o)) return null;
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            var w = new UsageWindow();
            object u;
            if (d.TryGetValue("utilization", out u) && u != null)
                w.Utilization = Convert.ToDouble(u, CultureInfo.InvariantCulture);
            object r;
            if (d.TryGetValue("resets_at", out r) && r is string)
            {
                DateTime dt;
                if (DateTime.TryParse((string)r, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out dt))
                    w.ResetsAt = dt;
            }
            return w;
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
            if (w == null) return -1;
            double rem = 100.0 - w.Utilization;
            if (rem < 0) rem = 0;
            if (rem > 100) rem = 100;
            return rem;
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

        // Large digits like the Windows battery percentage readout.
        static Bitmap RenderPercent(double rem)
        {
            var bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                string txt;
                Color clr = Color.White;
                if (rem >= 0)
                {
                    txt = ((int)Math.Round(rem)).ToString();
                    if (rem <= 15) clr = FILL_LOW;
                }
                else { txt = "!"; clr = FILL; }

                float size = txt.Length >= 3 ? 17f : (txt.Length == 2 ? 24f : 27f);
                // GenericTypographic removes GDI+'s built-in text padding, which otherwise
                // makes two large digits "not fit" in 32px and wrap - clipping the second digit.
                var sf = (StringFormat)StringFormat.GenericTypographic.Clone();
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;
                using (var f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    using (var shadow = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                        g.DrawString(txt, f, shadow, new RectangleF(1, 1, 32, 32), sf);
                    using (var b = new SolidBrush(clr))
                        g.DrawString(txt, f, b, new RectangleF(0, 0, 32, 32), sf);
                }
            }
            return bmp;
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
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

        // ------------------------------------------------ details ------------------------------------------------

        void ShowDetails()
        {
            var sb = new System.Text.StringBuilder();
            if (fiveHour != null)
                sb.AppendLine("5-hour window: " + Math.Round(100 - fiveHour.Utilization) + "% left" + ResetStr(fiveHour));
            if (sevenDay != null)
                sb.AppendLine("Weekly: " + Math.Round(100 - sevenDay.Utilization) + "% left" + ResetStr(sevenDay));
            if (lastSuccessUtc != DateTime.MinValue)
                sb.AppendLine("Updated " + lastSuccessUtc.ToLocalTime().ToString("HH:mm") + (credPathUsed != null ? " (token: " + credPathUsed + ")" : ""));
            if (errorMsg != null) sb.AppendLine(errorMsg);
            if (sb.Length == 0) sb.AppendLine("Loading...");
            trayBar.ShowBalloonTip(10000, "Claude usage remaining", sb.ToString(), errorMsg == null ? ToolTipIcon.None : ToolTipIcon.Warning);
        }

        static string ResetStr(UsageWindow w)
        {
            if (w.ResetsAt == null) return "";
            return " (resets " + w.ResetsAt.Value.ToLocalTime().ToString("ddd HH:mm") + ")";
        }

        // ------------------------------------------------ menu / config ------------------------------------------------

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("Refresh now", null, delegate { nextFetchUtc = DateTime.MinValue; MaybeFetch(); });
            menu.Items.Add("Details", null, delegate { ShowDetails(); });

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
}
