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

            // Opening several files from Explorer should fill one window, not
            // spawn a window each. --new-window opts out.
            if (Array.IndexOf(args, "--new-window") < 0
                && SingleInstance.ForwardToRunningInstance(args)) return;

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
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        // Files waiting to be handed to the page, either from the command line
        // or forwarded here by a second instance.
        private readonly List<string> _pending = new List<string>();

        // Watched documents, keyed by full path (case-insensitive).
        private readonly Dictionary<string, FileSystemWatcher> _watchers =
            new Dictionary<string, FileSystemWatcher>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastSeen =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private CoreWebView2Environment _env;
        private bool _ready;
        private int _unsavedCount;   // reported by the page, used to guard closing

        internal MainForm(string[] args)
        {
            QueueFiles(args);
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
            FormClosing += OnFormClosing;
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
            FlushPending();
        }

        /// <summary>Collects the openable paths out of an argument list.</summary>
        private void QueueFiles(string[] args)
        {
            foreach (string a in args)
            {
                if (string.IsNullOrEmpty(a) || a.StartsWith("-")) continue;
                try { if (File.Exists(a)) _pending.Add(Path.GetFullPath(a)); }
                catch (Exception) { /* unusable path */ }
            }
        }

        private void FlushPending()
        {
            if (!_ready || _pending.Count == 0) return;
            List<string> batch = new List<string>(_pending);
            _pending.Clear();
            SendFiles(batch, true);
        }

        /// <summary>Receives file paths forwarded by a second instance.</summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == SingleInstance.CopyDataMessage && m.LParam != IntPtr.Zero)
            {
                try
                {
                    SingleInstance.CopyData cd = (SingleInstance.CopyData)
                        Marshal.PtrToStructure(m.LParam, typeof(SingleInstance.CopyData));
                    if (cd.cbData > 0 && cd.lpData != IntPtr.Zero)
                    {
                        string payload = Marshal.PtrToStringUni(cd.lpData, cd.cbData / 2);
                        if (payload != null)
                            QueueFiles(payload.TrimEnd('\0').Split('\n'));
                        FlushPending();
                    }
                }
                catch (Exception) { /* malformed message; ignore it */ }

                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                Activate();
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
        }

        // --- bridge ---------------------------------------------------------
        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Log.Write("web message: " + e.WebMessageAsJson);
            Dictionary<string, object> msg;
            try { msg = _json.Deserialize<Dictionary<string, object>>(e.WebMessageAsJson); }
            catch (Exception) { return; }
            if (msg == null || !msg.ContainsKey("type")) return;

            switch (Convert.ToString(msg["type"], CultureInfo.InvariantCulture))
            {
                case "openFiles": ShowOpenFiles(); break;
                case "openFolder": ShowOpenFolder(); break;
                case "unwatchAll": StopWatching(); break;
                case "unwatch": Unwatch(Str(msg, "fullPath")); break;
                case "save": SaveDocument(msg, false); break;
                case "saveAs": SaveDocument(msg, true); break;
                case "dirtyState":
                    try { _unsavedCount = Convert.ToInt32(msg["count"], CultureInfo.InvariantCulture); }
                    catch (Exception) { _unsavedCount = 0; }
                    break;
            }
        }

        /// <summary>Writes a document to disk, prompting for a path when needed.</summary>
        private void SaveDocument(Dictionary<string, object> msg, bool saveAs)
        {
            string docId = Str(msg, "docId");
            string text = Str(msg, "text");
            string path = Str(msg, "fullPath");

            if (saveAs || string.IsNullOrEmpty(path))
            {
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Title = "Save Markdown file";
                    dlg.FileName = Str(msg, "name");
                    dlg.DefaultExt = "md";
                    dlg.AddExtension = true;
                    dlg.OverwritePrompt = true;
                    dlg.Filter = "Markdown files|*.md;*.markdown;*.mdown;*.mkd;*.mdx"
                               + "|Text files|*.txt|All files|*.*";
                    if (!string.IsNullOrEmpty(path))
                    {
                        try { dlg.InitialDirectory = Path.GetDirectoryName(path); }
                        catch (Exception) { }
                    }
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;   // cancelled
                    path = dlg.FileName;
                }
            }

            try
            {
                // No BOM: the convention for Markdown, and what every other tool expects.
                File.WriteAllText(path, text, new UTF8Encoding(false));

                // Record our own write so the watcher does not echo it straight back.
                try { _lastSeen[path] = File.GetLastWriteTimeUtc(path); } catch (Exception) { }
                Watch(path);

                long size = 0;
                try { size = new FileInfo(path).Length; } catch (Exception) { }

                Post(new Dictionary<string, object> {
                    { "type", "saved" },
                    { "docId", docId },
                    { "fullPath", path },
                    { "name", Path.GetFileName(path) },
                    { "size", size }
                });
                Log.Write("saved " + path + " (" + size + " bytes)");
            }
            catch (Exception ex)
            {
                Log.Write("save failed for " + path + ": " + ex.Message);
                Post(new Dictionary<string, object> {
                    { "type", "saveError" }, { "docId", docId }, { "message", ex.Message } });
            }
        }

        private static string Str(Dictionary<string, object> msg, string key)
        {
            object v;
            if (!msg.TryGetValue(key, out v) || v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
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
            const string title = "Choose a folder of Markdown files";
            string folder;

            // Falls back to the legacy tree dialog only if the modern one is unavailable.
            bool modern = FolderPicker.TryShow(this, title, out folder);
            Log.Write("folder picker: modern=" + modern + " folder=" + (folder == null ? "<none>" : folder));
            if (!modern)
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = title;
                    dlg.ShowNewFolderButton = false;
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    folder = dlg.SelectedPath;
                }
            }
            if (string.IsNullOrEmpty(folder)) return;   // cancelled

            var found = new List<string>();
            CollectMarkdown(folder, found, 0);
            if (found.Count == 0)
            {
                Post(new Dictionary<string, object> {
                    { "type", "notice" }, { "text", "No Markdown files in that folder" } });
                return;
            }
            SendFiles(found, true);
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

        /// <summary>Drops the watcher for one file, once the page has closed it.</summary>
        private void Unwatch(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            FileSystemWatcher watcher;
            if (_watchers.TryGetValue(fullPath, out watcher))
            {
                try { watcher.EnableRaisingEvents = false; watcher.Dispose(); }
                catch (Exception) { }
                _watchers.Remove(fullPath);
            }
            _lastSeen.Remove(fullPath);
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

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_unsavedCount > 0 && e.CloseReason != CloseReason.WindowsShutDown)
            {
                DialogResult answer = MessageBox.Show(this,
                    _unsavedCount == 1
                        ? "One file has unsaved changes.\r\n\r\nClose without saving?"
                        : _unsavedCount + " files have unsaved changes.\r\n\r\nClose without saving?",
                    "Markdown Viewer",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) { e.Cancel = true; return; }
            }
            WindowPlacement.Save(this);
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

    // -----------------------------------------------------------------------
    // One window, many files. A second launch hands its arguments to the
    // instance already running and then exits, so double-clicking five .md
    // files in Explorer fills one sidebar instead of opening five windows.
    // -----------------------------------------------------------------------
    internal static class SingleInstance
    {
        internal const int CopyDataMessage = 0x004A;   // WM_COPYDATA
        private const int SW_RESTORE = 9;

        [StructLayout(LayoutKind.Sequential)]
        internal struct CopyData
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr wParam, ref CopyData lParam);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);

        // Held for the lifetime of the first instance.
        private static System.Threading.Mutex _mutex;

        internal static bool ForwardToRunningInstance(string[] args)
        {
            bool createdNew;
            try
            {
                _mutex = new System.Threading.Mutex(true, "Local\\MarkdownViewer.SingleInstance", out createdNew);
            }
            catch (Exception)
            {
                return false;   // cannot arbitrate; just open a window
            }
            if (createdNew) return false;   // we are the first instance

            IntPtr target = FindRunningWindow();
            if (target == IntPtr.Zero)
            {
                // The holder is not showing a window: still starting, or wedged.
                try { _mutex.Close(); } catch (Exception) { }
                _mutex = null;
                return false;
            }

            var paths = new List<string>();
            foreach (string a in args)
            {
                if (string.IsNullOrEmpty(a) || a.StartsWith("-")) continue;
                try { if (File.Exists(a)) paths.Add(Path.GetFullPath(a)); }
                catch (Exception) { }
            }

            if (paths.Count > 0)
            {
                string payload = string.Join("\n", paths.ToArray());
                IntPtr buffer = Marshal.StringToCoTaskMemUni(payload);
                try
                {
                    CopyData cd;
                    cd.dwData = IntPtr.Zero;
                    cd.cbData = (payload.Length + 1) * 2;   // includes the terminator
                    cd.lpData = buffer;
                    SendMessageW(target, CopyDataMessage, IntPtr.Zero, ref cd);
                }
                finally { Marshal.FreeCoTaskMem(buffer); }
            }

            if (IsIconic(target)) ShowWindow(target, SW_RESTORE);
            SetForegroundWindow(target);
            return true;
        }

        /// <summary>
        /// Waits briefly for the other window, which may still be starting up
        /// when several files are opened at once.
        /// </summary>
        private static IntPtr FindRunningWindow()
        {
            Process me = Process.GetCurrentProcess();
            for (int attempt = 0; attempt < 40; attempt++)
            {
                foreach (Process p in Process.GetProcessesByName(me.ProcessName))
                {
                    if (p.Id == me.Id) continue;
                    try { if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle; }
                    catch (Exception) { }
                }
                System.Threading.Thread.Sleep(100);
            }
            return IntPtr.Zero;
        }
    }

    // -----------------------------------------------------------------------
    // The Vista-style folder dialog. FolderBrowserDialog on .NET Framework is
    // still the cramped old tree control, so call IFileOpenDialog directly.
    // -----------------------------------------------------------------------
    internal static class FolderPicker
    {
        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw { }

        // Declared in vtable order: IModalWindow, then IFileDialog as far as
        // GetResult. The members after it are unused and left out on purpose.
        [ComImport, Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            [PreserveSig] int SetFileTypes(uint count, IntPtr types);
            [PreserveSig] int SetFileTypeIndex(uint index);
            [PreserveSig] int GetFileTypeIndex(out uint index);
            [PreserveSig] int Advise(IntPtr events, out uint cookie);
            [PreserveSig] int Unadvise(uint cookie);
            [PreserveSig] int SetOptions(uint options);
            [PreserveSig] int GetOptions(out uint options);
            [PreserveSig] int SetDefaultFolder(IShellItem item);
            [PreserveSig] int SetFolder(IShellItem item);
            [PreserveSig] int GetFolder(out IShellItem item);
            [PreserveSig] int GetCurrentSelection(out IShellItem item);
            [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            [PreserveSig] int GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        }

        /// <summary>
        /// Returns false only when the modern dialog could not be used, so the
        /// caller can fall back. A true result with a null path means the user
        /// cancelled, which must not open a second dialog.
        /// </summary>
        internal static bool TryShow(IWin32Window owner, string title, out string path)
        {
            path = null;
            object dialog = null;
            try
            {
                dialog = new FileOpenDialogRcw();
                IFileDialog fd = (IFileDialog)dialog;

                uint options;
                if (fd.GetOptions(out options) != 0) return false;
                if (fd.SetOptions(options | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM) != 0) return false;
                fd.SetTitle(title);

                if (fd.Show(owner == null ? IntPtr.Zero : owner.Handle) != 0) return true;  // cancelled

                IShellItem item;
                if (fd.GetResult(out item) != 0 || item == null) return true;
                try
                {
                    IntPtr buffer;
                    if (item.GetDisplayName(SIGDN_FILESYSPATH, out buffer) != 0) return true;
                    try { path = Marshal.PtrToStringUni(buffer); }
                    finally { Marshal.FreeCoTaskMem(buffer); }
                }
                finally { Marshal.ReleaseComObject(item); }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (dialog != null) { try { Marshal.ReleaseComObject(dialog); } catch (Exception) { } }
            }
        }
    }
}
