// ---------------------------------------------------------------------------
// Markdown Viewer — Windows desktop shell
//
// Hosts the web app in a WebView2 window and serves it from a virtual origin
// (https://mdviewer.local/). That origin is a normal secure context, so
// localStorage persists between runs — no local HTTP server required.
//
// File watching is done natively with FileSystemWatcher and pushed to the page,
// so auto-reload works for anything opened through the app or passed on the
// command line.
//
// Targets .NET Framework 4.8 and is written to C# 5, so it compiles with the
// csc.exe that ships with Windows — no SDK, no toolchain install.
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MarkdownViewer
{
    // -----------------------------------------------------------------------
    // Entry point
    // -----------------------------------------------------------------------
    internal static class Program
    {
        internal const string VirtualHost = "mdviewer.local";
        internal const string StartUrl = "https://mdviewer.local/index.html";

        [STAThread]
        private static void Main(string[] args)
        {
            // Both hooks must be in place before any WebView2 type is resolved.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEmbeddedAssembly;
            NativeLibraries.Prepare();
            Launch(args);
        }

        // Kept out of Main so the JIT cannot touch WebView2 types before the
        // assembly resolver above is attached.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Launch(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm(args));
        }

        private static Assembly ResolveEmbeddedAssembly(object sender, ResolveEventArgs e)
        {
            string simpleName = new AssemblyName(e.Name).Name;
            using (Stream s = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("lib." + simpleName + ".dll"))
            {
                if (s == null) return null;
                byte[] bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
                return Assembly.Load(bytes);
            }
        }
    }

    // -----------------------------------------------------------------------
    // WebView2Loader.dll is native, so it has to exist on disk. Unpack it next
    // to the user data folder and load it before WebView2 P/Invokes into it.
    // -----------------------------------------------------------------------
    internal static class NativeLibraries
    {
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectoryW(string path);

        internal static void Prepare()
        {
            try
            {
                string dir = Path.Combine(Paths.DataFolder, "runtime");
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, "WebView2Loader.dll");

                using (Stream s = Assembly.GetExecutingAssembly()
                           .GetManifestResourceStream("lib.WebView2Loader.dll"))
                {
                    if (s != null)
                    {
                        byte[] bytes = new byte[s.Length];
                        s.Read(bytes, 0, bytes.Length);
                        // Rewrite only when it differs, so an in-use copy is left alone.
                        if (!File.Exists(target) || new FileInfo(target).Length != bytes.Length)
                        {
                            try { File.WriteAllBytes(target, bytes); }
                            catch (IOException) { /* already loaded by another instance */ }
                        }
                    }
                }

                SetDllDirectoryW(dir);
                LoadLibraryW(target);
            }
            catch (Exception)
            {
                // Fall through: a machine-wide WebView2Loader may still resolve.
            }
        }
    }

    internal static class Log
    {
        private static readonly bool Enabled =
            Environment.GetEnvironmentVariable("MDVIEWER_DEBUG") == "1";

        internal static void Write(string message)
        {
            if (!Enabled) return;
            try
            {
                File.AppendAllText(Path.Combine(Paths.DataFolder, "debug.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + "  " + message + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }

    internal static class Paths
    {
        private static string _data;

        internal static string DataFolder
        {
            get
            {
                if (_data == null)
                {
                    _data = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "MarkdownViewer");
                    Directory.CreateDirectory(_data);
                }
                return _data;
            }
        }

        internal static string ExeFolder
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        }
    }

    // -----------------------------------------------------------------------
    // Serves the web app: files on disk next to the exe win, so the app can be
    // edited in place; otherwise the copies embedded in the exe are used.
    // -----------------------------------------------------------------------
    internal static class Content
    {
        // Only the app's own files may be read from disk, so dropping the exe
        // into a folder never exposes that folder's other contents to the page.
        private static bool IsAppFile(string clean)
        {
            return clean == "index.html"
                || clean.StartsWith("assets/", StringComparison.Ordinal)
                || clean.StartsWith("vendor/", StringComparison.Ordinal);
        }

        internal static byte[] Get(string relativePath)
        {
            string clean = relativePath.Replace('\\', '/').TrimStart('/');
            if (clean.Length == 0) clean = "index.html";
            if (clean.IndexOf("..", StringComparison.Ordinal) >= 0) return null;
            if (!IsAppFile(clean)) return null;

            try
            {
                string onDisk = Path.GetFullPath(Path.Combine(Paths.ExeFolder, clean.Replace('/', '\\')));
                if (onDisk.StartsWith(Paths.ExeFolder, StringComparison.OrdinalIgnoreCase) && File.Exists(onDisk))
                    return File.ReadAllBytes(onDisk);
            }
            catch (Exception) { /* fall back to the embedded copy */ }

            using (Stream s = Assembly.GetExecutingAssembly()
                       .GetManifestResourceStream("web." + clean.Replace('/', '.')))
            {
                if (s == null) return null;
                byte[] bytes = new byte[s.Length];
                s.Read(bytes, 0, bytes.Length);
                return bytes;
            }
        }

        internal static string MimeFor(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": case ".mjs": return "text/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".woff2": return "font/woff2";
                case ".woff": return "font/woff";
                case ".ico": return "image/x-icon";
                case ".md": case ".markdown": case ".txt": return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }
    }

    // -----------------------------------------------------------------------
    // Main window
    // -----------------------------------------------------------------------
    internal sealed class MainForm : Form
    {
        private readonly WebView2 _web = new WebView2();
        private readonly string[] _startupArgs;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        // Watched documents, keyed by full path (case-insensitive).
        private readonly Dictionary<string, FileSystemWatcher> _watchers =
            new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastSeen =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private CoreWebView2Environment _env;
        private bool _ready;

        internal MainForm(string[] args)
        {
            _startupArgs = args;
            _json.MaxJsonLength = int.MaxValue;

            Text = "Markdown Viewer";
            MinimumSize = new Size(520, 400);
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(0x1B, 0x1A, 0x18);
            AllowDrop = false; // the page handles drops itself

            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.icon"))
                    if (s != null) Icon = new Icon(s);
            }
            catch (Exception) { /* the default icon is fine */ }

            WindowPlacement.Restore(this);

            _web.Dock = DockStyle.Fill;
            _web.DefaultBackgroundColor = Color.FromArgb(0x1B, 0x1A, 0x18);
            Controls.Add(_web);

            Load += OnLoad;
            FormClosing += delegate { WindowPlacement.Save(this); };
        }

        private async void OnLoad(object sender, EventArgs e)
        {
            try
            {
                var options = new CoreWebView2EnvironmentOptions();
                _env = await CoreWebView2Environment.CreateAsync(
                    null, Path.Combine(Paths.DataFolder, "WebView2"), options);
                await _web.EnsureCoreWebView2Async(_env);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Markdown Viewer needs the Microsoft Edge WebView2 runtime, which could not be started.\r\n\r\n"
                    + "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and try again.\r\n\r\n"
                    + ex.Message,
                    "Markdown Viewer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }

            CoreWebView2 core = _web.CoreWebView2;

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;

            core.AddWebResourceRequestedFilter("https://" + Program.VirtualHost + "/*",
                CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;
            core.WebMessageReceived += OnWebMessageReceived;
            core.DocumentTitleChanged += OnDocumentTitleChanged;
            core.NewWindowRequested += OnNewWindowRequested;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;

            Log.Write("environment ready; exe folder = " + Paths.ExeFolder);
            core.Navigate(Program.StartUrl);
        }

        // --- serving -------------------------------------------------------
        private void OnWebResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string path;
            try { path = new Uri(e.Request.Uri).AbsolutePath; }
            catch (UriFormatException) { return; }

            byte[] data = Content.Get(Uri.UnescapeDataString(path));
            Log.Write("resource " + path + " -> " + (data == null ? "404" : data.Length + " bytes"));
            if (data == null)
            {
                e.Response = _env.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            e.Response = _env.CreateWebResourceResponse(
                new MemoryStream(data), 200, "OK",
                "Content-Type: " + Content.MimeFor(path) + "\r\nCache-Control: no-cache");
        }

        // --- external links -------------------------------------------------
        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            e.Handled = true;
            OpenExternally(e.Uri);
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs e)
        {
            Log.Write("navigating to " + e.Uri);
            if (e.Uri.StartsWith("https://" + Program.VirtualHost + "/", StringComparison.OrdinalIgnoreCase)) return;
            if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            OpenExternally(e.Uri);
        }

        private static void OpenExternally(string uri)
        {
            if (!uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                && !uri.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return;
            try { Process.Start(uri); }
            catch (Exception) { /* no handler registered */ }
        }

        private void OnDocumentTitleChanged(object sender, object e)
        {
            string t = _web.CoreWebView2.DocumentTitle;
            Text = string.IsNullOrEmpty(t) ? "Markdown Viewer" : t;
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            Log.Write("navigation completed: success=" + e.IsSuccess + " status=" + e.WebErrorStatus);
            if (!e.IsSuccess) Text = "Markdown Viewer - failed to load (" + e.WebErrorStatus + ")";
            if (_ready) return;
            _ready = true;

            Post(new Dictionary<string, object> { { "type", "hostReady" } });

            List<string> files = new List<string>();
            foreach (string a in _startupArgs)
            {
                if (string.IsNullOrEmpty(a) || a.StartsWith("-")) continue;
                if (File.Exists(a)) files.Add(Path.GetFullPath(a));
            }
            if (files.Count > 0) SendFiles(files, true);
        }

        // --- bridge ---------------------------------------------------------
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> msg;
            try { msg = _json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
            catch (Exception) { return; }
            if (msg == null || !msg.ContainsKey("type")) return;

            switch (Convert.ToString(msg["type"], CultureInfo.InvariantCulture))
            {
                case "openFiles": ShowOpenFiles(); break;
                case "openFolder": ShowOpenFolder(); break;
                case "unwatchAll": StopWatching(); break;
            }
        }

        private void Post(Dictionary<string, object> payload)
        {
            if (_web.CoreWebView2 == null) return;
            try { _web.CoreWebView2.PostWebMessageAsJson(_json.Serialize(payload)); }
            catch (Exception) { /* window closing */ }
        }

        private static readonly string[] MarkdownExtensions =
            { ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdx", ".qmd", ".rmd", ".txt" };

        private static bool IsMarkdown(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(MarkdownExtensions, ext) >= 0;
        }

        private void ShowOpenFiles()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "Open Markdown files";
                dlg.Multiselect = true;
                dlg.Filter = "Markdown files|*.md;*.markdown;*.mdown;*.mkd;*.mkdn;*.mdx;*.qmd;*.rmd"
                           + "|Text files|*.txt|All files|*.*";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                SendFiles(new List<string>(dlg.FileNames), true);
            }
        }

        private void ShowOpenFolder()
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Choose a folder of Markdown files";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                var found = new List<string>();
                CollectMarkdown(dlg.SelectedPath, found, 0);
                if (found.Count == 0)
                {
                    Post(new Dictionary<string, object> {
                        { "type", "notice" }, { "text", "No Markdown files in that folder" } });
                    return;
                }
                SendFiles(found, true);
            }
        }

        private static void CollectMarkdown(string dir, List<string> into, int depth)
        {
            if (depth > 6 || into.Count > 2000) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir))
                    if (IsMarkdown(f)) into.Add(f);
                foreach (string d in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(d);
                    if (name.StartsWith(".") || name == "node_modules") continue;
                    if ((File.GetAttributes(d) & FileAttributes.Hidden) != 0) continue;
                    CollectMarkdown(d, into, depth + 1);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        /// <summary>Reads files, sends them to the page, and starts watching them.</summary>
        private void SendFiles(List<string> paths, bool activate)
        {
            string root = CommonRoot(paths);
            var payload = new List<object>();

            foreach (string p in paths)
            {
                string text;
                try { text = File.ReadAllText(p); }
                catch (Exception) { continue; }

                string rel = p;
                if (root != null && p.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    rel = p.Substring(root.Length).TrimStart('\\', '/');

                payload.Add(new Dictionary<string, object> {
                    { "path", rel.Replace('\\', '/') },
                    { "fullPath", p },
                    { "text", text },
                    { "size", new FileInfo(p).Length }
                });
                Watch(p);
            }

            if (payload.Count == 0) return;
            Post(new Dictionary<string, object> {
                { "type", "open" }, { "files", payload }, { "activate", activate } });
        }

        /// <summary>Longest shared directory prefix, so the sidebar shows short paths.</summary>
        private static string CommonRoot(List<string> paths)
        {
            if (paths.Count == 0) return null;
            string root = Path.GetDirectoryName(paths[0]);
            for (int i = 1; i < paths.Count && root != null; i++)
            {
                string dir = Path.GetDirectoryName(paths[i]);
                while (root.Length > 0 && !(dir + "\\").StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    string parent = Path.GetDirectoryName(root);
                    if (parent == null || parent == root) return null;
                    root = parent;
                }
            }
            return root;
        }

        // --- watching -------------------------------------------------------
        private void Watch(string fullPath)
        {
            if (_watchers.ContainsKey(fullPath)) return;
            try
            {
                var w = new FileSystemWatcher(Path.GetDirectoryName(fullPath), Path.GetFileName(fullPath));
                w.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                FileSystemEventHandler onChange = delegate { QueueReload(fullPath); };
                w.Changed += onChange;
                w.Created += onChange;
                w.Renamed += delegate { QueueReload(fullPath); };
                w.EnableRaisingEvents = true;
                _watchers[fullPath] = w;
            }
            catch (Exception) { /* unwatchable location; the document still opens */ }
        }

        private void StopWatching()
        {
            foreach (FileSystemWatcher w in _watchers.Values)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch (Exception) { }
            }
            _watchers.Clear();
            _lastSeen.Clear();
        }

        /// <summary>
        /// Editors touch a file several times per save, so coalesce the burst and
        /// let the writer finish before reading.
        /// </summary>
        private void QueueReload(string fullPath)
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(fullPath); }
            catch (Exception) { return; }

            DateTime seen;
            if (_lastSeen.TryGetValue(fullPath, out seen) && seen == stamp) return;
            _lastSeen[fullPath] = stamp;

            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    var t = new Timer();
                    t.Interval = 120;
                    t.Tick += delegate
                    {
                        t.Stop();
                        t.Dispose();
                        PushUpdate(fullPath);
                    };
                    t.Start();
                });
            }
            catch (InvalidOperationException) { /* form went away */ }
        }

        private void PushUpdate(string fullPath)
        {
            string text = null;
            for (int attempt = 0; attempt < 4 && text == null; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var sr = new StreamReader(fs, Encoding.UTF8, true))
                        text = sr.ReadToEnd();
                }
                catch (IOException) { System.Threading.Thread.Sleep(60); }
                catch (Exception) { return; }
            }
            if (text == null) return;

            long size = 0;
            try { size = new FileInfo(fullPath).Length; } catch (Exception) { }

            Post(new Dictionary<string, object> {
                { "type", "update" }, { "fullPath", fullPath }, { "text", text }, { "size", size } });
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            StopWatching();
            base.OnFormClosed(e);
        }
    }

    // -----------------------------------------------------------------------
    // Remembers where the window was
    // -----------------------------------------------------------------------
    internal static class WindowPlacement
    {
        private static string File_ { get { return Path.Combine(Paths.DataFolder, "window.txt"); } }

        internal static void Restore(Form f)
        {
            f.Size = new Size(1280, 860);
            CentreOnWorkingArea(f);
            try
            {
                if (!File.Exists(File_)) return;
                string[] p = File.ReadAllText(File_).Split(',');
                if (p.Length < 5) return;

                var bounds = new Rectangle(
                    int.Parse(p[0], CultureInfo.InvariantCulture),
                    int.Parse(p[1], CultureInfo.InvariantCulture),
                    int.Parse(p[2], CultureInfo.InvariantCulture),
                    int.Parse(p[3], CultureInfo.InvariantCulture));

                // Only reuse the saved rectangle if a screen still covers it.
                if (bounds.Width >= 520 && bounds.Height >= 400
                    && Screen.AllScreens.Length > 0
                    && IsMostlyVisible(bounds))
                {
                    f.Bounds = bounds;
                }
                if (p[4] == "1") f.WindowState = FormWindowState.Maximized;
            }
            catch (Exception) { /* first run, or a corrupt file */ }
        }

        private static bool IsMostlyVisible(Rectangle bounds)
        {
            foreach (Screen s in Screen.AllScreens)
            {
                Rectangle i = Rectangle.Intersect(s.WorkingArea, bounds);
                if (i.Width > 200 && i.Height > 150) return true;
            }
            return false;
        }

        private static void CentreOnWorkingArea(Form f)
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            f.Location = new Point(
                wa.X + Math.Max(0, (wa.Width - f.Width) / 2),
                wa.Y + Math.Max(0, (wa.Height - f.Height) / 2));
        }

        internal static void Save(Form f)
        {
            try
            {
                bool max = f.WindowState == FormWindowState.Maximized;
                Rectangle b = max ? f.RestoreBounds : f.Bounds;
                File.WriteAllText(File_, string.Join(",", new string[] {
                    b.X.ToString(CultureInfo.InvariantCulture),
                    b.Y.ToString(CultureInfo.InvariantCulture),
                    b.Width.ToString(CultureInfo.InvariantCulture),
                    b.Height.ToString(CultureInfo.InvariantCulture),
                    max ? "1" : "0" }));
            }
            catch (Exception) { /* not worth bothering the user about */ }
        }
    }
}
