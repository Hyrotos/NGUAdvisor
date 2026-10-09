using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace NGUAdvisorCompanion;

/// <summary>
/// The companion window: a WebView2 filling the form that renders the Focus web UI, plus a pipe client
/// that relays injector snapshots into the page and page commands back out.
///
///   pipe line (JSON snapshot)  ->  PostWebMessageAsJson  ->  page window message
///   page postMessage (command) ->  WebMessageReceived    ->  pipe line
/// </summary>
public sealed class MainForm : Form
{
    private readonly WebView2 _web = new();
    // "NGUAdvisorUI" / "NGUAdvisorUICmd" for the live instance; both suffixed for a bench. Passed as a
    // PAIR rather than letting PipeClient derive the second — see AdvisorInstance for the aliasing.
    private readonly PipeClient _pipe = new(AdvisorInstance.SnapshotPipe, AdvisorInstance.CommandPipe);
    private volatile bool _webReady;
    private volatile string _latestLine;     // last snapshot, flushed once the page is ready
    private volatile string _latestStatus;   // A4: last connection-status payload, flushed once the page is ready
    private string _pendingLine;             // A6: newest snapshot awaiting the UI thread (Interlocked, not volatile)
    private int _drainPosted;                // A6: 0/1 — a coalescing drain is already queued on the UI thread

    public MainForm()
    {
        Text = "NGU Advisor";
        // Proton/Wine can mis-handle raw GDI HICON handles during window creation. Keep the icon if we can,
        // but avoid the GetHicon/DestroyIcon path entirely because it is the direct source of the invalid
        // X11 window resource errors.
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("appicon.png");
            if (s != null)
            {
                using var iconStream = new MemoryStream();
                s.CopyTo(iconStream);
                iconStream.Position = 0;
                using var ico = new System.Drawing.Icon(iconStream, 32, 32);
                Icon = (System.Drawing.Icon)ico.Clone();
            }
        }
        catch { /* icon is cosmetic */ }
        MinimumSize = new Size(900, 600);
        RestoreGeometry();                                  // J3 — sets size/position (defaults if none saved)

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        _pipe.LineReceived += OnPipeLine;
        _pipe.ConnectionChanged += OnConnectionChanged;

        Load += async (_, _) => { EnsureOnScreen(); await InitWebAsync(); };
        FormClosing += (_, _) => SaveGeometry();            // J3 — before the handle goes away
        FormClosed += (_, _) => _pipe.Dispose();

        // J3, and this is the part that makes it actually work: the COMMON shutdown is the game exiting,
        // which Program.WatchGame turns into Environment.Exit(0) — that never raises FormClosing. Saving on
        // FormClosing alone would silently lose the geometry on the dominant path. So persist as soon as the
        // user finishes a drag/resize (WM_EXITSIZEMOVE) or changes window state; by the time the game exits
        // the file is already current. Writes are trivial and only happen when the user actually moved it.
        ResizeEnd += (_, _) => SaveGeometry();
        Resize += (_, _) =>
        {
            if (WindowState == _lastState) return;
            _lastState = WindowState;
            SaveGeometry();
        };
        _lastState = WindowState;
    }

    private FormWindowState _lastState;

    // ---- window geometry (J3) ----

    // User-local, and deliberately NOT in the game or injector directories (same rule as the WebView2
    // user-data folder). LocalAppData rather than %TEMP% so Disk Cleanup / Storage Sense can't wipe it.
    private static string GeometryPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
        return Path.Combine(root, "NGUAdvisorCompanion", "window.json");
    }

    private sealed class Geometry
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int W { get; set; }
        public int H { get; set; }
        public bool Max { get; set; }
    }

    private void RestoreGeometry()
    {
        // Defaults first: they stand whenever there is nothing saved, or anything at all goes wrong below.
        Width = 1280;
        Height = 860;
        StartPosition = FormStartPosition.CenterScreen;
        try
        {
            var path = GeometryPath();
            if (!File.Exists(path)) return;
            var g = JsonSerializer.Deserialize<Geometry>(File.ReadAllText(path));
            if (g == null || g.W <= 0 || g.H <= 0) return;

            StartPosition = FormStartPosition.Manual;
            Bounds = ClampToWorkAreas(new Rectangle(g.X, g.Y, g.W, g.H), WorkAreas(), MinimumSize);
            // A saved Minimized state is deliberately NOT restored as minimized: the injector auto-launches
            // this window, and one that starts minimized reads as "the companion failed to open".
            if (g.Max) WindowState = FormWindowState.Maximized;
        }
        catch { /* missing/garbage state file — keep the defaults */ }
    }

    private void SaveGeometry()
    {
        try
        {
            // RestoreBounds is the normal-state rectangle while maximized or minimized. Persisting Bounds
            // instead would bring the window back screen-sized but not maximized.
            var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (r.Width <= 0 || r.Height <= 0) return;
            var g = new Geometry
            {
                X = r.X, Y = r.Y, W = r.Width, H = r.Height,
                Max = WindowState == FormWindowState.Maximized,
            };
            var path = GeometryPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Write-then-replace. Environment.Exit can kill this process mid-call on the game-exit path, and
            // a half-written window.json would restore garbage; a replace can only ever leave the old file.
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(g));
            File.Move(tmp, path, true);
        }
        catch { /* geometry is a convenience — it must never fail or delay a close */ }
    }

    // Primary display FIRST: ClampToWorkAreas treats workAreas[0] as the fallback when the saved rectangle
    // overlaps no display at all.
    private static Rectangle[] WorkAreas()
    {
        var screens = Screen.AllScreens;
        var areas = new List<Rectangle>(screens.Length);
        foreach (var s in screens) if (s.Primary) areas.Add(s.WorkingArea);
        foreach (var s in screens) if (!s.Primary) areas.Add(s.WorkingArea);
        return areas.ToArray();
    }

    /// <summary>
    /// Pull a saved rectangle back onto a real desktop. The monitor it was saved on may have been unplugged,
    /// rearranged or resized; without this the window restores to coordinates no display covers and is
    /// invisible with no way to get it back. <paramref name="workAreas"/> must have the primary display first.
    /// </summary>
    internal static Rectangle ClampToWorkAreas(Rectangle want, Rectangle[] workAreas, Size min)
    {
        if (workAreas == null || workAreas.Length == 0) return want;   // headless/no displays: nothing to do
        if (want.Width < min.Width) want.Width = min.Width;
        if (want.Height < min.Height) want.Height = min.Height;

        // Already entirely on the desktop? Leave it exactly where the user put it. Work areas never overlap,
        // so the intersections sum cleanly — which means a window deliberately STRADDLING two monitors is
        // recognised as fully visible and is not snapped onto one of them.
        long covered = 0;
        foreach (var wa in workAreas)
        {
            var seen = Rectangle.Intersect(wa, want);
            covered += (long)seen.Width * seen.Height;
        }
        if (covered == (long)want.Width * want.Height) return want;

        // Host it on the display it overlaps most; overlapping none (monitor gone) falls back to the primary.
        var host = workAreas[0];
        long bestArea = 0;
        foreach (var wa in workAreas)
        {
            var hit = Rectangle.Intersect(wa, want);
            long area = (long)hit.Width * hit.Height;
            if (area > bestArea) { bestArea = area; host = wa; }
        }

        // Shrink to fit, THEN slide inside. Sliding first would leave an oversized window hanging off the
        // far edge with no pass left to correct it.
        if (want.Width > host.Width) want.Width = host.Width;
        if (want.Height > host.Height) want.Height = host.Height;
        if (want.Right > host.Right) want.X = host.Right - want.Width;
        if (want.Bottom > host.Bottom) want.Y = host.Bottom - want.Height;
        if (want.X < host.X) want.X = host.X;
        if (want.Y < host.Y) want.Y = host.Y;
        return want;
    }

    // Belt and braces once the handle exists: if a DPI rescale or a display change between construction and
    // show has left the window off every desktop, put it back. Cheap insurance against shipping an invisible
    // window, which is the only way J3 can actually hurt.
    private void EnsureOnScreen()
    {
        try
        {
            if (WindowState != FormWindowState.Normal) return;
            var areas = WorkAreas();
            foreach (var wa in areas) if (wa.IntersectsWith(Bounds)) return;
            Bounds = ClampToWorkAreas(Bounds, areas, MinimumSize);
        }
        catch { }
    }

    private async Task InitWebAsync()
    {
        try
        {
            // Keep WebView2's user-data folder out of the game/injector directories.
            var userData = Path.Combine(Path.GetTempPath(), "NGUAdvisorCompanion");
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2;

            // Serve wwwroot over a virtual https origin so window messaging works with a real origin
            // and the same index.html still opens standalone in a browser for design iteration.
            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping(
                "nguadvisor.local", wwwroot, CoreWebView2HostResourceAccessKind.Allow);

            core.WebMessageReceived += OnWebMessage;
            // Only post after the page (and its message listener) has loaded; flush the last snapshot
            // so the first frame is never dropped while the page was still parsing.
            core.NavigationCompleted += (_, _) =>
            {
                _webReady = true;
                // A4: flush the buffered connection state BEFORE the snapshot. A connected:true that landed
                // while the page was still parsing used to be discarded and never re-sent; now that the page
                // starts in an explicit "connecting" state that would strand it on a spinner forever.
                var status = _latestStatus;
                if (status != null) { try { core.PostWebMessageAsJson(status); } catch { } }
                var buffered = _latestLine;
                if (buffered != null) { try { core.PostWebMessageAsJson(buffered); } catch { } }
            };
            // Quick win #20: mirror the page's <title> into the window/taskbar caption. The page only rewrites
            // its title on pipe transitions, so this is an EXTRA taskbar-visible signal — it does NOT cover A1
            // (an advisor that stalls with the pipe still up changes nothing here).
            core.DocumentTitleChanged += (_, _) =>
            {
                try { var t = core.DocumentTitle; if (!string.IsNullOrWhiteSpace(t)) Text = t; } catch { }
            };
            core.Settings.AreDefaultContextMenusEnabled = false;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
#endif
            core.Navigate("https://nguadvisor.local/index.html");

            _pipe.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Failed to initialize WebView2.\n\n" + ex.Message +
                "\n\nThe WebView2 runtime ships with Windows 11; on older systems install the Evergreen runtime.",
                "NGU Advisor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- injector -> page ----

    // A6 — newest-wins, mirroring the producer. UiBridge.Publish swaps a single _latest ref rather than
    // queueing, so the injector always transmits the newest snapshot; the consumer used to do the opposite and
    // gave every line its own BeginInvoke. A UI thread that fell behind then rendered a BACKLOG of stale
    // frames one at a time, each looking current. Here a late drain always renders the newest line and the
    // intermediate ones are dropped — which is exactly what a 1 Hz full-state snapshot stream wants.
    private void OnPipeLine(string line)
    {
        if (line == null || IsDisposed) return;
        Interlocked.Exchange(ref _pendingLine, line);
        // At most one drain in flight. The drain clears this flag FIRST (before taking the pending line), so
        // a line arriving mid-drain always causes a fresh post instead of being stranded until the next tick.
        if (Interlocked.Exchange(ref _drainPosted, 1) != 0) return;
        try
        {
            BeginInvoke(() =>
            {
                Interlocked.Exchange(ref _drainPosted, 0);
                var newest = Interlocked.Exchange(ref _pendingLine, null);
                if (newest == null) return;                // a racing drain already took it
                _latestLine = newest;                      // still the first-frame buffer for NavigationCompleted
                var core = _web.CoreWebView2;
                if (core == null || !_webReady) return;
                try { core.PostWebMessageAsJson(newest); } // line is compact JSON from the injector
                catch { /* malformed line — skip it, keep the stream alive */ }
            });
        }
        catch { Interlocked.Exchange(ref _drainPosted, 0); /* form closing mid-post */ }
    }

    private void OnConnectionChanged(bool connected)
    {
        if (IsDisposed) return;
        var payload = "{\"type\":\"status\",\"connected\":" + (connected ? "true" : "false") + "}";
        // A4: buffer it the way _latestLine already is. The old `if (!_webReady) return` dropped any
        // transition that arrived before NavigationCompleted and nothing ever re-sent it.
        _latestStatus = payload;
        try
        {
            BeginInvoke(() =>
            {
                var core = _web.CoreWebView2;
                if (core == null || !_webReady) return;    // NavigationCompleted will flush _latestStatus
                try { core.PostWebMessageAsJson(payload); } catch { }
            });
        }
        catch { }
    }

    // ---- page -> commands ----

    private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // The page posts commands as a STRING (postMessage(JSON.stringify(obj))), so read the raw
        // string first; WebMessageAsJson would re-quote it into a double-encoded JSON literal.
        string msg = null;
        try { msg = e.TryGetWebMessageAsString(); } catch { }
        if (string.IsNullOrWhiteSpace(msg))
        {
            try { msg = e.WebMessageAsJson; } catch { }
        }
        if (string.IsNullOrWhiteSpace(msg) || msg == "null") return;
        if (TryHandleLocal(msg)) return;   // Timeline-Editor commands are handled in-process (profile files)
        _pipe.Send(msg);                    // everything else -> injector
    }

    // ---- companion-local commands (Timeline Editor reads/writes the profile file directly) ----

    private bool TryHandleLocal(string msg)
    {
        try
        {
            using var doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("cmd", out var cmdEl) || cmdEl.ValueKind != JsonValueKind.String) return false;
            switch (cmdEl.GetString())
            {
                case "loadTimelines":
                    PostTimelines(GetStr(root, "dir"), GetStr(root, "name"));
                    return true;
                case "editBreakpointTime":
                    EditBreakpointTime(GetStr(root, "dir"), GetStr(root, "name"), GetStr(root, "system"),
                                       GetInt(root, "index", -1), GetInt(root, "sec", -1));
                    return true;
                case "deleteBreakpoint":
                    DeleteBreakpoint(GetStr(root, "dir"), GetStr(root, "name"), GetStr(root, "system"),
                                     GetInt(root, "index", -1));
                    return true;
                case "addBreakpoint":
                    AddBreakpoint(GetStr(root, "dir"), GetStr(root, "name"), GetStr(root, "system"),
                                  GetInt(root, "sec", -1), GetStr(root, "payload"), GetStr(root, "challenge"),
                                  GetStr(root, "target"));
                    return true;
                case "editBreakpoint":
                    EditBreakpoint(GetStr(root, "dir"), GetStr(root, "name"), GetStr(root, "system"),
                                   GetInt(root, "index", -1), GetInt(root, "sec", -1), GetStr(root, "payload"),
                                   GetStr(root, "challenge"), GetStr(root, "target"));
                    return true;
                case "setChallenges":
                    SetChallenges(GetStr(root, "dir"), GetStr(root, "name"), GetStrArray(root, "entries"));
                    return true;
                case "loadLog":
                    LoadLog(GetStr(root, "dir"), GetStr(root, "type"));
                    return true;
                case "openProfileFolder":
                    OpenProfileFolder(GetStr(root, "dir"), GetStr(root, "name"));
                    return true;
                case "focusWindow":
                    FocusWindow();
                    return true;
                default:
                    return false;
            }
        }
        catch { return false; }
    }

    private static string GetStr(JsonElement o, string k) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement o, string k, int dflt) =>
        o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : dflt;

    private static string[] GetStrArray(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var v) || v.ValueKind != JsonValueKind.Array) return new string[0];
        var list = new List<string>();
        foreach (var e in v.EnumerateArray())
            if (e.ValueKind == JsonValueKind.String) list.Add(e.GetString());
        return list.ToArray();
    }

    private void EditBreakpointTime(string dir, string name, string system, int index, int sec)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(system) || index < 0 || sec < 0) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string payload;
            try { payload = ProfileService.EditBreakpointTime(dir, name, system, index, sec); }
            catch (Exception ex) { payload = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message }); }
            PostToWeb(payload);
        });
    }

    private void DeleteBreakpoint(string dir, string name, string system, int index)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(system) || index < 0) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string payload;
            try { payload = ProfileService.DeleteBreakpoint(dir, name, system, index); }
            catch (Exception ex) { payload = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message }); }
            PostToWeb(payload);
        });
    }

    private void AddBreakpoint(string dir, string name, string system, int sec, string payload, string challenge, string target)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(system) || sec < 0) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string result;
            try { result = ProfileService.AddBreakpoint(dir, name, system, sec, payload, challenge, target); }
            catch (Exception ex) { result = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message }); }
            PostToWeb(result);
        });
    }

    private void EditBreakpoint(string dir, string name, string system, int index, int sec, string payload, string challenge, string target)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(system) || index < 0 || sec < 0) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string result;
            try { result = ProfileService.EditBreakpoint(dir, name, system, index, sec, payload, challenge, target); }
            catch (Exception ex) { result = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message }); }
            PostToWeb(result);
        });
    }

    // The advisor's log files (allow-list — blocks path traversal via the `type` field).
    private static readonly HashSet<string> _logTypes = new HashSet<string> { "inject", "debug", "loot", "combat", "cards", "pitspin" };

    private void LoadLog(string dir, string type)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(type) || !_logTypes.Contains(type)) return;
        Task.Run(() =>
        {
            string text;
            try
            {
                var path = Path.Combine(dir, type + ".log");
                if (!File.Exists(path)) { text = "(no " + type + ".log yet)"; }
                else
                {
                    // Shared read — the advisor holds these open for writing; tail the last ~64KB.
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        const long cap = 64 * 1024;
                        if (fs.Length > cap) fs.Seek(-cap, SeekOrigin.End);
                        using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                    }
                    if (text.Length == 0) text = "(" + type + ".log is empty)";
                }
            }
            catch (Exception ex) { text = "(could not read " + type + ".log: " + ex.Message + ")"; }
            PostToWeb(JsonSerializer.Serialize(new { type = "log", name = type, text }));
        });
    }

    private void SetChallenges(string dir, string name, string[] entries)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string result;
            try { result = ProfileService.SetChallenges(dir, name, entries); }
            catch (Exception ex) { result = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message }); }
            PostToWeb(result);
        });
    }

    private void PostTimelines(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
        if (name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.Contains("..")) return;  // path safety
        Task.Run(() =>
        {
            string payload;
            try
            {
                if (!File.Exists(Path.Combine(dir, name + ".json"))) return;
                payload = ProfileService.BuildTimelinesJson(dir, name);
            }
            catch (Exception ex)
            {
                payload = JsonSerializer.Serialize(new { type = "timelines", profile = name, error = ex.Message });
            }
            PostToWeb(payload);
        });
    }

    // Open the profiles folder in Explorer, selecting the named profile's file when it exists. Companion-local
    // (the injector has no business shelling out for a UI affordance). Outcome is posted back so the page can
    // show a message instead of the click appearing to do nothing.
    private void OpenProfileFolder(string dir, string name)
    {
        string error = null;
        try
        {
            if (string.IsNullOrEmpty(dir)) error = "No profile folder is known yet.";
            else if (!Directory.Exists(dir)) error = "Profile folder not found: " + dir;
            else
            {
                // MUST normalise first. The advisor builds its data dir from "%userprofile%/AppData/LocalLow",
                // so the path it reports mixes separators (C:\Users\x/AppData/LocalLow\NGUAdvisor\profiles).
                // Every file API tolerates that, but explorer.exe's /select does NOT — it silently gives up and
                // opens the Desktop instead. GetFullPath canonicalises the separators.
                dir = Path.GetFullPath(dir);
                string file = null;
                if (!string.IsNullOrEmpty(name) && name.IndexOf('/') < 0 && name.IndexOf('\\') < 0 && !name.Contains(".."))
                {
                    var cand = Path.Combine(dir, name + ".json");
                    if (File.Exists(cand)) file = cand;
                }
                // /select needs explorer.exe directly; a bare folder opens through the shell.
                var psi = file != null
                    ? new ProcessStartInfo("explorer.exe", "/select,\"" + file + "\"")
                    : new ProcessStartInfo(dir) { UseShellExecute = true };
                Process.Start(psi);
            }
        }
        catch (Exception ex) { error = ex.Message; }
        PostToWeb(JsonSerializer.Serialize(new { type = "profileFolder", ok = error == null, error }));
    }

    // Bring the companion forward when an in-game hotkey asks for a view (F9). Windows may refuse to steal
    // focus from the foreground game, in which case the taskbar button flashes instead — still a signal.
    private void FocusWindow()
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                try
                {
                    if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
                    Activate();
                    BringToFront();
                }
                catch { }
            });
        }
        catch { }
    }

    private void PostToWeb(string json)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                var core = _web.CoreWebView2;
                if (core == null || !_webReady) return;
                try { core.PostWebMessageAsJson(json); } catch { }
            });
        }
        catch { }
    }
}
