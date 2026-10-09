using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using NGUAdvisor.AllocationProfiles;
using NGUAdvisor.AllocationProfiles.RebirthStuff;
using NGUAdvisor.Managers;
using UnityEngine;
using Application = UnityEngine.Application;

namespace NGUAdvisor
{
    public class Main : MonoBehaviour
    {
        // INVARIANT (why caching the Character root once is safe):
        // NGU Idle keeps ONE Character MonoBehaviour alive for the whole process. Its save/load path,
        // Character.saveLoad.loadintoGame (see LoadQuicksave, ~line 707), deserializes INTO that existing
        // instance rather than reconstructing it, so the reference resolved once here never goes stale on an
        // in-game save reload. This cached root (and InventoryController below, plus every manager's cached
        // sub-controller) therefore stays valid for the session. The only thing that WOULD invalidate it is a
        // full scene teardown / new Character within the same process — which does not happen in NGU — and our
        // own hot-reload, which discards these statics wholesale on a fresh assembly load anyway. Do not
        // "fix" the caching into live lookups without first confirming that invariant no longer holds.
        //
        // WHAT CHANGED (weld fix, audit 01 §5 step 1): the ~22 managers and breakpoint classes that used to
        // mirror this line with their own `static readonly Character _character = Main.Character;` now hold a
        // PROPERTY instead. The caching invariant above is untouched — there is still exactly one resolved
        // root — but the Unity read no longer happens in each of those types' INITIALIZERS. That is what made
        // them impossible to name headlessly and what made an initializer throw permanent for the process.
        // This field is now the ONLY remaining type-init Character capture in the tree, and it is the one
        // place the capture belongs: Main is the Unity entry point and cannot be loaded headlessly anyway.
        public static readonly Character Character = FindObjectOfType<Character>();
        public static readonly InventoryController InventoryController = Character.inventoryController;
        public static StreamWriter OutputWriter;
        public static StreamWriter LootWriter;
        public static StreamWriter CombatWriter;
        public static StreamWriter PitSpinWriter;
        public static StreamWriter CardsWriter;
        public static StreamWriter DebugWriter;
        private static CustomAllocation _profile;
        public static CustomAllocation Profile => _profile;
        private float _timeLeft = 10.0f;
        private static GUIStyle _overlayStyle;
        private static float _overlayStyleScale = -1f;
        // NGU Advisor's own product version (SemVer). Bump by hand only at real milestones; the per-build
        // identity is the auto BuildTag below, so this no longer needs touching every compile.
        public const string Version = "2.4.1";
        // "dev" or "public", baked in at compile time from <AdvisorChannel> in NGUAdvisor.csproj — the
        // ONE line that differs between the two repos (see that property's comment for why it is not a
        // #if and not a hand-edited const here beside Version).
        //
        // Unreadable or absent answers "dev", so the build footer keeps its stamps and its drift
        // warning. A wrong "public" would take the stale-UI instrument away from a developer without
        // saying anything, and that instrument exists because a reviewed toggle once sat six days out
        // of the running app.
        private static string _channel;
        public static string Channel
        {
            get
            {
                if (_channel != null) return _channel;
                _channel = "dev";
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    foreach (System.Reflection.AssemblyMetadataAttribute a in
                             asm.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false))
                    {
                        if (a.Key == "Channel" && !string.IsNullOrEmpty(a.Value)) { _channel = a.Value; break; }
                    }
                }
                catch { }
                return _channel;
            }
        }

        // What a human is shown: "2.3.0" on public, "2.3.0-dev" everywhere else. The two repos ship
        // the SAME release number and the suffix is DERIVED from the channel, so Version stays one
        // digit-for-digit identical line in both trees and there is exactly one thing to bump at a
        // release. Hardcoding "2.3.0-dev" here would have put a second version carrier back in the
        // file — the shape b2abcf0 had to reconcile after two shipped binaries disagreed.
        //
        // AssemblyVersion / AssemblyFileVersion in Properties/AssemblyInfo.cs stay NUMERIC (2.3.*,
        // 2.3.0.0) and carry no suffix: those fields cannot hold one, and a dev build is the same
        // release, not a different one. There is deliberately no AssemblyInformationalVersion carrying
        // "2.3.0-dev" — it would need the number a second time, in the csproj, which is the duplicate
        // this whole arrangement exists to avoid.
        public static string DisplayVersion => Version + (Channel == "public" ? "" : "-" + Channel);
        // Build stamp, derived automatically from the hot-reload assembly identity (NGUAdvisor.r<yyMMddHHmmss>,
        // the unique per-compile name that already exists for Mono byte-load dedup). Replaces the old
        // hand-bumped codename — every compile yields a unique, sortable id (yyMMdd-HHmm) with zero edits.
        private static string _buildTag;
        public static string BuildTag
        {
            get
            {
                if (_buildTag != null) return _buildTag;
                try
                {
                    var name = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
                    int i = name.IndexOf(".r", StringComparison.Ordinal);
                    var digits = i >= 0 ? new string(name.Substring(i + 2).Where(char.IsDigit).ToArray()) : "";
                    _buildTag = digits.Length >= 10 ? $"{digits.Substring(0, 6)}-{digits.Substring(6, 4)}" : "dev";
                }
                catch { _buildTag = "dev"; }
                return _buildTag;
            }
        }
        // -1 = unknown/unseeded. MUST NOT default to 0: statics reset on advisor reload, and a 0
        // baseline made SetResnipe read any real zone as "new zone fightable" — wiping the
        // completed snipe mid-run (user-reported). SetResnipe re-seeds from the current best zone.
        private static int _furthestZone = -1;

        // Latch for the "titan fight refused" line: the routing check runs every tick and the refusal
        // is a STATE, so it is said on transition. -1 = nothing currently refused.
        private static int _titanBlockedIdx = -1;

        // Highest zone that already armed a "new zone fightable" re-snipe this run. Fightability is
        // measured in CURRENT gear, but the snipe itself runs in the gold loadout — when the gold
        // gear couldn't clear the zone, the ratchet dropped back and the trigger re-fired forever
        // (user-reported infinite swap loop). Each zone arms the trigger ONCE; resets with the run.
        private static int _lastNewZoneTrigger = -1;

        private static string _dir;
        private static string _profilesDir;

        private static bool _tempSwapped = false;

        // FileSystemWatcher events fire on background ThreadPool threads. Their handlers must NOT touch
        // Unity/WinForms objects (doing so hard-crashes the game). Instead they set these flags, which the
        // main-thread Update() drains. See the deferred handling in Update().
        private static volatile bool _reloadAllocationPending;
        private static volatile bool _reloadSettingsPending;

        // MAIN-THREAD RULE: WinForms handlers (profile Switch/Apply buttons etc.) must NEVER call
        // LoadAllocation directly — allocation work touches Unity objects and hard-crashes off the
        // Unity thread (user-reported: dashboard Switch crash). They request; Update() drains.
        public static void RequestAllocationReload() => _reloadAllocationPending = true;
        // Same deferral for a full config re-read (settings + form + allocation), mirroring ConfigWatcher.
        public static void RequestSettingsReload() => _reloadSettingsPending = true;
        // And for a state export asked for from outside the game (StateExport.RequestPath).
        private static volatile bool _stateExportPending;
        public static void RequestStateExport() => _stateExportPending = true;

        // HOT RELOAD (F5 / companion "hotReloadAdvisor") — swaps the PAYLOAD DLL, not the settings.
        // Deferred through a flag for the same reason as the two above, only more so: the reload tears
        // this very component down (Loader.Unload -> writers closed, UiBridge disposed, GameObject
        // destroyed) and immediately byte-loads a replacement. Doing that from inside a UI-command drain
        // would dispose the bridge mid-iteration, and doing it from the middle of Update() would leave
        // the rest of Update() running against a torn-down Main. Update() drains this FIRST and returns.
        private static volatile bool _hotReloadPending;
        public static void RequestHotReload() => _hotReloadPending = true;

        public static FileSystemWatcher ConfigWatcher;
        public static FileSystemWatcher AllocationWatcher;
        public static FileSystemWatcher ZoneWatcher;

        public static bool IgnoreNextChange { get; set; }

        public static SavedSettings Settings;

        private static void WriterLog(StreamWriter writer, string msg)
        {
            var formattedDate = $"{DateTime.Now.ToShortDateString()}-{DateTime.Now.ToShortTimeString()} ({Math.Floor(Character.rebirthTime.totalseconds)}s)";
            writer.WriteLine($"{formattedDate}: {msg}");
        }

        public static void Log(string msg) => WriterLog(OutputWriter, msg);

        // In-memory mirror of loot.log for the LOGS reader (ring, newest first — same pattern as
        // the advisor feed). File writes are unchanged.
        public static readonly System.Collections.Generic.List<string> LootFeed
            = new System.Collections.Generic.List<string>();

        public static void LogLoot(string msg)
        {
            WriterLog(LootWriter, msg);
            try
            {
                // A kill's drops can arrive as one multi-line message — one ring entry per line.
                foreach (var line in (msg ?? "").Split('\n'))
                {
                    var t = line.Trim();
                    if (t.Length == 0) continue;
                    LootFeed.Insert(0, $"{DateTime.Now:HH:mm} {t}");
                }
                if (LootFeed.Count > 400) LootFeed.RemoveRange(400, LootFeed.Count - 400);
            }
            catch { }
        }

        public static void LogCombat(string msg) => WriterLog(CombatWriter, msg);

        public static void LogPitSpin(string msg) => WriterLog(PitSpinWriter, msg);

        public static void LogCard(string msg) => WriterLog(CardsWriter, msg);

        public static void LogDebug(string msg) => WriterLog(DebugWriter, msg);

        public static string GetSettingsDir() => _dir;

        public static string GetLogDir() => Path.Combine(_dir, "logs");

        public static string GetProfilesDir() => _profilesDir;

        // Safe item-name lookup for the gear editor (the game knows every item's name by id).
        public static string ItemName(int id)
        {
            try
            {
                if (id <= 0) return "";
                return InventoryController.itemInfo.itemName[id];
            }
            catch { return "?"; }
        }

        // C1 naming convention (user-approved): collapse the game's stacked "Ascended Ascended ..."
        // prefixes to "Ascended x{n} ..." from the second repetition onward. Counts at runtime, so it
        // adapts to any chain depth. Applied everywhere the advisor renders item names.
        public static string CollapseAscended(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            int n = 0, pos = 0;
            while (name.Length - pos >= 9 && string.CompareOrdinal(name, pos, "Ascended ", 0, 9) == 0)
            {
                n++;
                pos += 9;
            }
            return n < 2 ? name : $"Ascended x{n} {name.Substring(pos)}";
        }

        public static string ItemNameNice(int id) => CollapseAscended(ItemName(id));

        public void Unload()
        {
            // Every step individually guarded: a single throw here used to ABORT the bootstrap's
            // reload half-done (form closed, new payload never loaded — user-reported). Worse, the
            // old catch called LogDebug AFTER DebugWriter.Close(), so the logging itself threw.
            // Writers close LAST; nothing below may escape.
            void Try(Action a) { try { a(); } catch { } }

            Try(() => CancelInvoke("AutomationRoutine"));
            Try(() => CancelInvoke("MonitorLog"));
            Try(() => CancelInvoke("QuickStuff"));
            Try(() => CancelInvoke("SetResnipe"));
            Try(() => CancelInvoke("ShowBoostProgress"));
            Try(() => CancelInvoke("UiBridgeTick"));

            Try(() => ConfigWatcher.Dispose());
            Try(() => AllocationWatcher.Dispose());
            Try(() => ZoneWatcher.Dispose());

            // Dispose the UI bridge before the writers close: it pokes its own pipe so the accept
            // thread unblocks (Mono can't interrupt a native WaitForConnection) and joins it.
            Try(() => { if (_uiBridge != null) _uiBridge.Dispose(); _uiBridge = null; });

            Try(() => LootWriter.Close());
            Try(() => CombatWriter.Close());
            Try(() => PitSpinWriter.Close());
            Try(() => CardsWriter.Close());
            Try(() => DebugWriter.Close());
            Try(() => OutputWriter.Close());
        }
		static void RollIfLarge(string path, long maxBytes = 8L * 1024 * 1024)
		{
			try
			{
				var fi = new FileInfo(path);
				if (!fi.Exists || fi.Length < maxBytes) return;
				var prev = path + ".1";
				if (File.Exists(prev)) File.Delete(prev);
				File.Move(path, prev);
			}
			catch { /* best-effort, same as the folder migration above */ }
		}

        public void Start()
        {
            try
            {
                // GetFullPath canonicalises the separators: the literal below uses forward slashes, so without
                // it every path derived from _dir is mixed (C:\Users\x/AppData/LocalLow\NGUAdvisor). File APIs
                // don't care, but anything we hand to the shell does — explorer.exe /select just opens the
                // Desktop when given one.
                _dir = Path.GetFullPath(Path.Combine(Environment.ExpandEnvironmentVariables("%userprofile%/AppData/LocalLow"), "NGUAdvisor"));
                if (!Directory.Exists(_dir))
                    Directory.CreateDirectory(_dir);

                // One-time migration: the product was renamed from "NGUInjector" to NGU Advisor. Move any
                // settings/profiles/logs the user already had in LocalLow\NGUInjector into the new folder.
                // Merge per-entry (don't gate on the new folder being absent) because Run NGU Advisor.bat
                // may have already created it holding only injector-path.txt.
                try
                {
                    var oldDir = Path.Combine(Environment.ExpandEnvironmentVariables("%userprofile%/AppData/LocalLow"), "NGUInjector");
                    if (Directory.Exists(oldDir) && !string.Equals(oldDir, _dir, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var d in Directory.GetDirectories(oldDir))
                        {
                            var dest = Path.Combine(_dir, Path.GetFileName(d));
                            if (!Directory.Exists(dest)) Directory.Move(d, dest);
                        }
                        foreach (var f in Directory.GetFiles(oldDir))
                        {
                            var dest = Path.Combine(_dir, Path.GetFileName(f));
                            if (!File.Exists(dest)) File.Move(f, dest);
                        }
                    }
                }
                catch { /* best-effort; a fresh install just starts clean in the new folder */ }

                var logDir = Path.Combine(_dir, "logs");
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);
					RollIfLarge(Path.Combine(logDir, "inject.log"));
					RollIfLarge(Path.Combine(logDir, "loot.log"));
					RollIfLarge(Path.Combine(logDir, "combat.log"));
					RollIfLarge(Path.Combine(logDir, "debug.log"));
					
					OutputWriter  = new StreamWriter(Path.Combine(logDir, "inject.log"), true) { AutoFlush = true };
					LootWriter    = new StreamWriter(Path.Combine(logDir, "loot.log"), true)   { AutoFlush = true };
					CombatWriter  = new StreamWriter(Path.Combine(logDir, "combat.log"), true) { AutoFlush = true };
					PitSpinWriter = new StreamWriter(Path.Combine(logDir, "pitspin.log"), true) { AutoFlush = true };
					CardsWriter = new StreamWriter(Path.Combine(logDir, "cards.log"), true) { AutoFlush = true };
					DebugWriter   = new StreamWriter(Path.Combine(logDir, "debug.log"), true)  { AutoFlush = true };
					
					OutputWriter.WriteLine($"===== SESSION START {DateTime.Now:yyyy-MM-dd HH:mm:ss} v{DisplayVersion} build {BuildTag} =====");
					DebugWriter.WriteLine($"===== SESSION START {DateTime.Now:yyyy-MM-dd HH:mm:ss} v{DisplayVersion} build {BuildTag} =====");
					
					
                // Health probe: if debug.log stays empty even of this line, the writer itself is broken
                // and every "Advisor ... failed" message has been invisible.
                LogDebug($"debug.log writer alive (v{DisplayVersion} build {BuildTag})");

                _profilesDir = Path.Combine(_dir, "profiles");
                if (!Directory.Exists(_profilesDir))
                    Directory.CreateDirectory(_profilesDir);

                // Install the embedded goal-loadout presets before profiles are listed/loaded. Missing files
                // are written; a preset we installed and the user has not touched is refreshed to the shipped
                // version; a preset the user edited is preserved. See Managers/PresetInstallPlan.
                Managers.PresetInstaller.Install(_profilesDir);

                var oldPath = Path.Combine(_dir, "allocation.json");
                var newPath = Path.Combine(_profilesDir, "default.json");

                if (File.Exists(oldPath) && !File.Exists(newPath))
                    File.Move(oldPath, newPath);
            }
            catch (Exception e)
            {
                LogDebug(e.Message);
                LogDebug(e.StackTrace);
                Loader.Unload();
                return;
            }

            try
            {
                Log("Injected");
                LogLoot("Starting Loot Writer");
                LogCombat("Starting Combat Writer");
                LockManager.ReleaseLock();

                Settings = new SavedSettings(_dir);

                if (!Settings.LoadSettings())
                {
                    var temp = new SavedSettings(null);

                    Settings.MassUpdate(temp);

                    Log($"Created default settings");
                }

                Settings.SetSaveDisabled(true);

                if (string.IsNullOrEmpty(Settings.AllocationFile))
                    Settings.AllocationFile = "default";

                Settings.SetSaveDisabled(false);

                LoadAllocation();

                ZoneWatcher = new FileSystemWatcher
                {
                    Path = _dir,
                    Filter = "zoneOverride.json",
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                ZoneWatcher.Changed += (sender, args) =>
                {
                    Log(_dir);
                    ZoneStatHelper.CreateOverrides(_dir);
                };

                ConfigWatcher = new FileSystemWatcher
                {
                    Path = _dir,
                    Filter = "settings.json",
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                ConfigWatcher.Changed += (sender, args) =>
                {
                    if (IgnoreNextChange)
                    {
                        IgnoreNextChange = false;
                        return;
                    }
                    // Defer to the main thread (touches Settings/WinForms/Unity).
                    _reloadSettingsPending = true;
                };

                AllocationWatcher = new FileSystemWatcher
                {
                    Path = _profilesDir,
                    Filter = "*.json",
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                // These fire on background threads; defer the actual work (which reloads allocations and
                // touches Unity/WinForms) to the main thread via flags drained in Update().
                AllocationWatcher.Changed += (sender, args) => { _reloadAllocationPending = true; };

                Settings.SaveSettings();
                Settings.LoadSettings();

                ZoneStatHelper.CreateOverrides(_dir);

                // Fail-closed game-version gate (P0-3): if the game build changed out from under a
                // previous baseline, hold automation in observe-only (a patch can silently move the
                // values we read). Reads/HUD stay live; see CompatibilityGate.
                Managers.CompatibilityGate.Initialize(_dir);

                // CONSTANT CAPTURE IS RETIRED — the CALL is gone, the CLASS is deliberately kept.
                //
                // Managers/ConstantCapture.cs was a TEMPORARY INSTRUMENT (audit/decisions/constant-capture-spec.md)
                // whose own comment said "remove once audit/08-captured-constants.md is written". 08 is written,
                // and so are 11, 16 and 19 — the instrument produced all four and has nothing left to measure.
                //
                // WHY IT HAD TO STOP SHIPPING. It is not a correctness risk and never was; it is a dev
                // instrument running on players' machines. Measured on the operator's own install, 2026-08-07:
                // 358 lines and ~60 KB per launch, 3,222 lines over 9 launches, 9.2% of a 5.9 MB inject.log —
                // spent re-deriving constants that are already recorded in the audit corpus.
                //
                // WHY THE CLASS STAYS. It is the only way those constants get re-measured if a game patch
                // moves them: every value it reads is scene-serialized, so the decompile shows the declaration
                // and never the number. CompatibilityGate above DETECTS a changed game build; this is what
                // RE-MEASURES after one. Deleting the file would trade a 558-line dormant asset for a rewrite.
                // To re-arm for one session, restore `Managers.ConstantCapture.Run();` on the line below —
                // it must stay here, before the InvokeRepeating block, so nothing has started ticking on top
                // of it, and out of any static constructor (a throw in a type-initializer that has captured
                // Main.Character poisons the type for the whole process — 01-architecture-decision §4.3).

                InvokeRepeating("AutomationRoutine", 0.0f, 10.0f);
                InvokeRepeating("MonitorLog", 0.0f, 1f);
                InvokeRepeating("QuickStuff", 0.0f, .5f);
                InvokeRepeating("ShowBoostProgress", 0.0f, 60.0f);
                InvokeRepeating("SetResnipe", 0f, 1f);

                // Out-of-process modern UI (M1): start the headless snapshot publisher and push ~1/s
                // on the Unity main thread. The companion WebView2 host renders it read-only.
                _uiBridge = new UiBridge();
                _uiBridge.Start();
                InvokeRepeating("UiBridgeTick", 1f, 1f);
                MaybeLaunchCompanion();
            }
            catch (Exception e)
            {
                LogDebug(e.ToString());
                LogDebug(e.StackTrace);
                if (e.InnerException != null) LogDebug(e.InnerException.ToString());
            }
        }

        // Auto-launch the out-of-process companion UI (gated by a setting, single-instance, best-effort).
        private void MaybeLaunchCompanion()
        {
            if (Settings == null || !Settings.LaunchCompanion) return;   // opt-in gate; F1 bypasses it via LaunchCompanionNow
            LaunchCompanionNow();
        }

        // Launch the companion window unconditionally (the F1 hotkey path — an explicit user request, so it
        // skips the LaunchCompanion opt-in gate). Single-instance + best-effort, same as auto-launch.
        private void LaunchCompanionNow()
        {
            try
            {
                // Already running? The companion holds a named single-instance mutex — per instance, so a
                // bench advisor does not read the LIVE companion's mutex and conclude it already has a UI.
                try { using (System.Threading.Mutex.OpenExisting(Managers.AdvisorInstance.CompanionMutex)) { LogDebug("Companion already running; skip auto-launch."); return; } }
                catch { /* not running -> launch */ }
                var exe = FindCompanionExe();
                if (exe == null) { LogDebug("Companion exe not found; skip auto-launch."); return; }
                // UseShellExecute=true matches the injector's existing proven Process.Start pattern in the Mono domain.
                // Direct process launch, no shell indirection. Under Proton/Wine, a shell-based start can
                // create a bad/invalid window handle and the companion never gets a valid X11 window.
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    // argv[0] is the game's PID, so the companion exits when NGU closes (lifecycle fix):
                    // the advisor runs in-process, so our PID IS the game's. argv[1] is the instance id,
                    // appended ONLY when there is one — an old companion.exe meeting a new advisor DLL
                    // across a partial deploy must still see exactly the one argument it expects, and a
                    // default-instance launch is then byte-identical to what it has always been. The id
                    // is sanitised to [A-Za-z0-9_-] (AdvisorInstance.Sanitize), so it needs no quoting.
                    Arguments = System.Diagnostics.Process.GetCurrentProcess().Id.ToString() +
                                (Managers.AdvisorInstance.Id.Length == 0 ? "" : " " + Managers.AdvisorInstance.Id),
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal
                });
                Log("Auto-launched companion UI.");
            }
            catch (Exception e) { LogDebug("Companion launch failed: " + e.Message); }
        }

        // The companion ships in <injectorDir>\companion\; the injector dir is in injector-path.txt.
        private string FindCompanionExe()
        {
            try
            {
                var pathFile = Path.Combine(_dir, "injector-path.txt");
                if (!File.Exists(pathFile)) return null;
                var dir = (File.ReadAllText(pathFile) ?? "").Trim();
                if (dir.Length == 0) return null;
                var cand = Path.Combine(dir, "companion", "NGUAdvisorCompanion.exe");
                return File.Exists(cand) ? cand : null;
            }
            catch { return null; }
        }

        // Publishes an advisor-state snapshot to the out-of-process UI once a second (main thread).
        private void UiBridgeTick()
        {
            // GrowthTracker sampled off the WinForms status pump (SettingsForm), which the 2.0.0 companion
            // migration deleted — taking the tracker's ONLY feeder with it. _samples stayed empty, so
            // GrowthTracker.Rate() bailed on "< 2 samples" and every growth chip published rate 0 /
            // ready:false. This tick is the pump's successor (main thread, once a second) and Tick()
            // self-throttles to one sample a minute, so it restores the original cadence exactly.
            // Kept outside the _uiBridge null-check, in its own try, so history builds regardless.
            try { GrowthTracker.Tick(); }
            catch (Exception e) { LogDebug("GrowthTracker tick: " + e.Message); }

            // A state export asked for by dropping the request file. Polled here rather than per
            // frame: once a second is plenty, and this tick is already on the main thread.
            try
            {
                if (Managers.StateExport.Requested())
                {
                    Log("State export requested from disk");
                    RequestStateExport();
                }
            }
            catch (Exception e) { LogDebug("State export poll: " + e.Message); }

            try { if (_uiBridge != null) _uiBridge.Publish(_timeLeft); }
            catch (Exception e) { LogDebug("UiBridge tick: " + e.Message); }
        }

        // Out-of-process modern UI bridge (M1): headless snapshot publisher over named pipe "NGUAdvisorUI".
        private UiBridge _uiBridge;

        // Retained no-op: the legacy WinForms form is gone, but SavedSettings still calls this on save.
        // The companion re-reads state from the snapshot stream, so nothing needs to happen here.
        public static void UpdateForm(SavedSettings newSettings) { }

        public void Update()
        {
            // FIRST, and it returns: a hot reload destroys this component and starts a fresh payload, so
            // nothing below may run afterwards. Loader.Unload() deactivates the GameObject before
            // destroying it, which also suppresses this frame's LateUpdate on the old Main.
            if (_hotReloadPending)
            {
                _hotReloadPending = false;
                PerformHotReload();
                return;
            }

            // Drain deferred file-watcher work on the main thread (see the watcher handlers). Doing this
            // off-thread previously crashed the game (e.g. digger menu UI refresh from a background thread).
            if (_reloadSettingsPending)
            {
                _reloadSettingsPending = false;
                try { Settings.LoadSettings(); LoadAllocation(); }
                catch (Exception e) { LogDebug($"Deferred settings reload failed: {e.Message}"); }
            }
            if (_reloadAllocationPending)
            {
                _reloadAllocationPending = false;
                try { LoadAllocation(); }
                catch (Exception e) { LogDebug($"Deferred allocation reload failed: {e.Message}"); }
            }
            if (_stateExportPending)
            {
                _stateExportPending = false;
                try { Managers.StateExport.Write(); }
                catch (Exception e) { LogDebug($"Deferred state export failed: {e.Message}"); }
            }

            // Apply any commands the out-of-process UI sent (drained on the main thread, per-command guarded).
            if (_uiBridge != null) _uiBridge.DrainCommands();

            _timeLeft -= Time.deltaTime;   // consumed by OnGUI + UiBridgeTick

            if (Input.GetKeyDown(KeyCode.F1))
                LaunchCompanionNow();   // open the companion window (relaunches it if it was closed)

            if (Input.GetKeyDown(KeyCode.F2))
                Settings.GlobalEnabled = !Settings.GlobalEnabled;

            if (Input.GetKeyDown(KeyCode.F3))
                QuickSave();

            if (Input.GetKeyDown(KeyCode.F7))
                QuickLoad();

            // F5 = hot-reload the advisor from disk (browser-refresh convention). Requests only; the
            // teardown happens at the top of the NEXT Update, off this frame's stack.
            if (Input.GetKeyDown(KeyCode.F5))
                RequestHotReload();

            // F9 kept its old meaning — "open the profile editor" — now that the editor lives in the
            // companion: open the window if it is closed, then ask the page to show the Profile Editor.
            if (Input.GetKeyDown(KeyCode.F9))
            {
                LaunchCompanionNow();
                if (_uiBridge != null) _uiBridge.RequestView("profileEditor");
            }

            if (Input.GetKeyDown(KeyCode.F10))
                Managers.GearOptimizerDiagnostic.Run();

            if (Input.GetKeyDown(KeyCode.F8))
            {
                if (Settings.QuickLoadout.Length > 0)
                {
                    if (_tempSwapped)
                    {
                        Log("Restoring Previous Loadout");
                        LoadoutManager.RestoreTempLoadout();
                    }
                    else
                    {
                        Log("Equipping Quick Loadout");
                        // SaveTempLoadout stays OUTSIDE the gate deliberately. During No Equipment the
                        // ChangeGear below is ignored, so _tempLoadout simply records the gear that is
                        // still on — and the F8 that follows restores it through Cause.Restore, whose
                        // set-equality early-out (LoadoutManager.cs:50) makes it a no-op rather than an
                        // allocation reset. Skipping the save instead would leave _tempLoadout holding
                        // a loadout from BEFORE the challenge, which is the stale case worth avoiding.
                        // _tempSwapped (:605) toggles unconditionally for gear, diggers and beards
                        // together, and diggers/beards are NOT gated — so the hotkey stays coherent.
                        LoadoutManager.SaveTempLoadout();
                        // Cause.UserHotkey: a deliberate keypress, NOT the advisor deciding to churn.
                        // [OPERATOR] RULING: ignored during No Equipment, with a line every press.
                        // The gate and the line both live at the choke point (LoadoutManager.ChangeGear),
                        // not here, so any future hotkey caller inherits them.
                        LoadoutManager.ChangeGear(Settings.QuickLoadout, GearChangeGate.Cause.UserHotkey);
                    }
                }

                if (Settings.QuickDiggers.Length > 0)
                {
                    if (_tempSwapped)
                    {
                        Log("Equipping Previous Diggers");
                        DiggerManager.RestoreTempDiggers();
                        DiggerManager.RecapDiggers();
                    }
                    else
                    {
                        Log("Equipping Quick Diggers");
                        DiggerManager.SaveTempDiggers();
                        DiggerManager.EquipDiggers(Settings.QuickDiggers);
                        DiggerManager.RecapDiggers();
                    }
                }

                if (Settings.QuickBeards.Length > 0)
                {
                    if (_tempSwapped)
                    {
                        Log("Equipping Previous Beards");
                        BeardManager.RestoreTempBeards();
                    }
                    else
                    {
                        Log("Equipping Quick Beards");
                        BeardManager.SaveTempBeards();
                        BeardManager.EquipBeards(Settings.QuickBeards);
                    }
                }

                _tempSwapped = !_tempSwapped;
            }

            // F11 = dump currently-equipped item ids (moved off F5, which is now the hot reload).
            if (Input.GetKeyDown(KeyCode.F11))
                DumpEquipped();
        }

        // Hot-reload the payload from disk. Only works when the session was started via
        // "Run NGU Advisor.bat": that injects NGUAdvisorBootstrap, which byte-loads NGUAdvisor.dll and is
        // the only thing that can replace it live. The payload and the bootstrap are separate assemblies
        // and the payload cannot reference the bootstrap, so the call goes out by reflection: walk the
        // loaded assemblies for "NGUAdvisorBootstrap" and invoke NGUAdvisorBootstrap.Boot.Reload().
        // MUST NOT THROW — this runs inside the Unity update loop.
        private static void PerformHotReload()
        {
            System.Reflection.MethodInfo reload = null;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "NGUAdvisorBootstrap") continue;
                    reload = asm.GetType("NGUAdvisorBootstrap.Boot")?.GetMethod("Reload");
                    break;
                }
            }
            catch (Exception e) { try { LogDebug($"Hot reload: bootstrap lookup failed: {e.Message}"); } catch { } }

            if (reload == null)
            {
                // Hand-injected / unusual load: no bootstrap in the domain, so there is nothing that can
                // swap the DLL. Say so and do nothing else — a silent no-op is worse than no hotkey.
                try { Log("Hot reload UNAVAILABLE: no NGUAdvisorBootstrap in this session. Start the game with 'Run NGU Advisor.bat' to enable it (this build needs a full restart to change)."); } catch { }
                return;
            }

            // LOG BEFORE THE TEARDOWN. Boot.Reload() calls our own Loader.Unload(), which closes every
            // StreamWriter — WriterLog has no null/closed guard, so anything logged after this point
            // throws. Boot.Reload is itself fully guarded and reports to bootstrap.log.
            try { Log($"Hot reload: unloading build {BuildTag} and byte-loading NGUAdvisor.dll from disk (see bootstrap.log)..."); } catch { }

            try { reload.Invoke(null, null); }
            catch { /* writers are gone by now; bootstrap.log has the detail */ }
        }

        public void LateUpdate() => SnipeZone();

        public float NakedAdventurePower() => InventoryController.adventureAttackBonus();

        public float CubePower() => InventoryController.cubePower();

        public float NakedAdventureToughness() => InventoryController.adventureDefenseBonus();

        public float CubeToughness() => InventoryController.cubeToughness();

        public long TotalNudeEnergyCap()
        {
            var num = (double)
                // Base Energy Cap
                Character.capEnergy

                // Perk Modifier
                * Character.adventureController.itopod.totalEnergyCapBonus()

                // MacGuffin Modifier
                * Character.inventory.macguffinBonuses[1];

            // Quirk Modifier
            num *= Character.beastQuestPerkController.totalEnergyCapBonus();

            // Wish modifier
            num *= Character.wishesController.totalEnergyCapBonus();

            if (num < 1.0)
                num = 1.0;

            return num >= Character.hardCap() ? Character.hardCap() : (long)num;
        }

        public long TotalNudeMagicCap()
        {
            var num = (double)
                // Base Magic Cap
                Character.magic.capMagic

                // Perk Modifier
                * Character.adventureController.itopod.totalMagicCapBonus()

                // MacGuffin Modifier
                * Character.inventory.macguffinBonuses[3];

            // Quirk Modifier
            num *= Character.beastQuestPerkController.totalMagicCapBonus();

            // Wish modifier
            num *= Character.wishesController.totalMagicCapBonus();

            if (num < 1.0)
                num = 1.0;

            return num >= Character.hardCap() ? Character.hardCap() : (long)num;
        }

        public double TotalNudeEnergyPower()
        {
            var num = (double)Character.energyPower * Character.adventureController.itopod.totalEnergyPowerBonus();
            num *= Character.inventory.macguffinBonuses[0];
            num *= Character.beastQuestPerkController.totalEnergyPowerBonus();
            num *= Character.wishesController.totalEnergyPowerBonus();
            if (num < 1.0)
                num = 1.0;

            if (num >= Character.hardCapPowBar())
                num = Character.hardCapPowBar();

            return num;
        }

        public double TotalNudeMagicPower()
        {
            var num = (double)Character.magic.magicPower * Character.adventureController.itopod.totalMagicPowerBonus();
            num *= Character.inventory.macguffinBonuses[2];
            num *= Character.beastQuestPerkController.totalMagicPowerBonus();
            num *= Character.wishesController.totalMagicPowerBonus();

            if (num < 1.0)
                num = 1.0;

            if (num >= Character.hardCapPowBar())
                num = Character.hardCapPowBar();

            return num;
        }

        public double TotalNudeEnergyBar()
        {
            var num = (double)Character.energyBars * Character.adventureController.itopod.totalEnergyBarBonus();
            num *= Character.beastQuestPerkController.totalEnergyBarBonus();
            num *= Character.wishesController.totalEnergyBarBonus();
            num *= Character.inventory.macguffinBonuses[6];

            if (num < 1.0)
                num = 1.0;

            if (num > Character.hardCapPowBar())
                num = Character.hardCapPowBar();

            return num;
        }

        public double TotalNudeMagicBar()
        {
            var num = (double)Character.magic.magicPerBar * Character.adventureController.itopod.totalMagicBarBonus();
            num *= Character.beastQuestPerkController.totalMagicBarBonus();
            num *= Character.wishesController.totalMagicBarBonus();
            num *= Character.inventory.macguffinBonuses[7];

            if (num < 1.0)
                num = 1.0;

            if (num > Character.hardCapPowBar())
                num = Character.hardCapPowBar();

            return num;
        }

        private void QuickSave()
        {
            Log("Writing quicksave and json");
            var data = Character.importExport.getBase64Data();
            using (var writer = new StreamWriter(Path.Combine(_dir, "NGUSave.txt")))
                writer.WriteLine(data);

            data = JsonUtility.ToJson(Character.importExport.gameStateToData());
            using (var writer = new StreamWriter(Path.Combine(_dir, "NGUSave.json")))
                writer.WriteLine(data);

            // Base Power
            Log($"Base Power: {NakedAdventurePower()}");
            // Base Toughness
            Log($"Base Toughness: {NakedAdventureToughness()}");
            // Cube Power
            Log($"Cube Power: {CubePower()}");
            // Cube Toughness
            Log($"Cube Power: {CubeToughness()}");
            // Nude Energy Cap
            Log($"Nude Energy Cap: {TotalNudeEnergyCap()}");
            // Nude Magic Cap
            Log($"Nude Magic Cap: {TotalNudeMagicCap()}");
            // Nude Energy Power
            Log($"Nude Energy Power: {TotalNudeEnergyPower()}");
            // Nude Magic Power
            Log($"Nude Magic Power: {TotalNudeMagicPower()}");
            // Nude Energy Bars
            Log($"Nude Energy Bars: {TotalNudeEnergyBar()}");
            // Nude Magic Bars
            Log($"Nude Magic Bars: {TotalNudeMagicBar()}");

            Character.saveLoad.saveGamestateToSteamCloud();
        }

        private void QuickLoad()
        {
            var filename = Path.Combine(_dir, "NGUSave.txt");
            if (!File.Exists(filename))
            {
                Log("Quicksave doesn't exist");
                return;
            }

            var saveTime = File.GetLastWriteTime(filename);
            var s = DateTime.Now.Subtract(saveTime);
            var secDiff = (int)s.TotalSeconds;
            if (secDiff > 120)
            {
                var diff = saveTime.GetPrettyDate();

                var confirmResult = MessageBox.Show($"Last quicksave was {diff}. Are you sure you want to load?",
                    "Load Quicksave"
                    , MessageBoxButtons.YesNo);

                if (confirmResult == DialogResult.No)
                    return;
            }

            Log("Loading quicksave");
            string base64Data;
            try
            {
                base64Data = File.ReadAllText(filename);
            }
            catch (Exception e)
            {
                LogDebug($"Failed to read quicksave: {e.Message}");
                return;
            }

            try
            {
                var saveDataFromString = Character.importExport.getSaveDataFromString(base64Data);
                var dataFromString = Character.importExport.getDataFromString(base64Data);

                if ((dataFromString == null || dataFromString.version < 361) &&
                    Application.platform != RuntimePlatform.WindowsEditor)
                {
                    Log("Bad save version");
                    return;
                }

                if (dataFromString.version > Character.getVersion())
                {
                    Log("Bad save version");
                    return;
                }

                Character.saveLoad.loadintoGame(saveDataFromString);
            }
            catch (Exception e)
            {
                LogDebug($"Failed to load quicksave: {e.Message}");
            }
        }

        // Stuff on a very short timer
        private void QuickStuff()
        {
            try
            {
                if (!Settings.GlobalEnabled || !Managers.CompatibilityGate.ActionsAllowed)
                    return;

                var needsAllocation = false;
                if (Character.bossID == 0)
                    needsAllocation = true;
                // A GEAR SWAP ZEROES EVERY ALLOCATION (LoadoutManager.ChangeGear has to, or the game
                // silently reverts the swap), and nothing asked for them back: the re-seat was left to
                // "whichever timer fires first", and the only one that does outside the first boss of a
                // run is the 10 s AutomationRoutine. So every swap -- a gold snipe is two, there and
                // back -- left energy, magic and R3 idle for up to ten seconds (user-reported
                // 2026-10-09). The stamp ChangeGear already arms is the request; the pass that
                // completes clears it (CustomAllocation.ReportReseatAfterGearSwap).
                if (LoadoutManager.AllocationClearedAt.HasValue)
                    needsAllocation = true;

                if (Settings.AutoFight || Settings.MoneyPitRunMode)
                {
                    var bc = Character.bossController;
                    if (!bc.isFighting && !bc.nukeBoss)
                    {
                        var canNuke = bc.character.attack / 5.0 > bc.character.bossDefense && bc.character.defense / 5.0 > bc.character.bossAttack;
                        var shouldNuke = !MoneyPitManager.NeedsGold() || Character.rebirthTime.totalseconds > 180.0;
                        if (canNuke && shouldNuke)
                        {
                            bc.startNuke();
                        }
                        else if (shouldNuke || Character.bossID < 29)
                        {
                            double characterDamage = (bc.character.attack - bc.character.bossDefense - bc.character.bossRegen) * 0.02;
                            double bossDamage = (bc.character.bossAttack - bc.character.defense - bc.character.hpRegen) * 0.02;

                            bool doFight;

                            if (characterDamage <= 0)
                            {
                                // Character does no damage - don't fight
                                doFight = false;
                            }
                            else if (bossDamage <= 0)
                            {
                                // Boss does no damage - fight
                                doFight = true;
                            }
                            else if (bc.character.curHP == bc.character.maxHP)
                            {
                                // Character is at full HP - there is no use for waiting
                                doFight = true;
                            }
                            else
                            {
                                double characterAttacksToKill = Math.Ceiling(bc.character.bossCurHP / characterDamage);
                                double bossAttacksToKill = Math.Ceiling(bc.character.curHP / bossDamage);

                                // Boss attack logic executes first, so fight only if the character will kill the boss in fewer attacks than the boss will kill the character
                                doFight = characterAttacksToKill < bossAttacksToKill;
                            }

                            if (doFight)
                            {
                                bc.beginFight();
                                bc.stopButton.gameObject.SetActive(true);
                            }
                        }
                    }
                }

                if (Settings.MoneyPitRunMode && Character.machine.realBaseGold <= 0.0 && MoneyPitManager.NeedsLowerTier())
                {
                    if (Character.buttons.bloodMagic.interactable)
                    {
                        var tier = MoneyPitManager.ShockwaveTier();

                        var startIndex = 0;
                        if (tier == 1e15 && Character.realGold >= 1e18)
                            startIndex = 4;
                        else if (tier == 1e13 && Character.realGold >= 1e15)
                            startIndex = 3;

                        if (startIndex > 0)
                        {
                            Character.removeMostMagic();
                            for (var i = startIndex; i < Character.bloodMagicController.ritualsUnlocked(); i++)
                                Character.bloodMagicController.bloodMagics[i].cap();
                        }
                    }
                }

                if (needsAllocation)
                    _profile.DoAllocations();

                QuestManager.ManageQuests();

                // ADVISOR PIT OWNS AUTOMATIC THROW TIMING (slice 7.6C2A-1). Both callers reach the same
                // CheckMoneyPit(), and without this guard they RACE — one that the standard path wins
                // essentially every time, because it polls here every 0.5s while the advisor evaluates at
                // most once every 60s (AdvisorApply: 30s Tick throttle, then ApplyPit's own 60s throttle).
                // So the moment the pit came off cooldown with gold past the manual threshold, this caller
                // threw — straight through an advisor that was deliberately HOLDING for a reward tier
                // ("WAIT — 1e15 in ~8m", MoneyPitManager.AdvisorPlan). The cooldown never arbitrated that;
                // it only made the loser's later call a no-op. The advisor's hold is a pure recomputed
                // function with no latch, so there is nothing this path could have read to know better —
                // it has to yield instead.
                //
                // RUNTIME PRIORITY, NOT SETTINGS NORMALIZATION: AutoMoneyPit stays exactly as the user saved
                // it. Both switches remain legally true at once; that combination now means "standard auto is
                // configured, and the advisor is currently driving". Nothing here writes a setting.
                //
                // Not gated: Throw Now (PitPanel calls AdvisorThrow directly — an explicit click is explicit
                // intent), and Daily Spin below, which is a different game system (dailyController, its own
                // cooldown, no advisor policy at all) and stays deliberately OUTSIDE this guard.
                if (Settings.AutoMoneyPit && !Settings.AdvisorPit)
                    MoneyPitManager.CheckMoneyPit();

                if (Settings.AutoSpin)
                    MoneyPitManager.DoDailySpin();

                // Manual %-cap auto-swap runs ONLY when the advisor isn't managing blood. When
                // CastBloodSpells (advisor) is on it fully owns the spell toggles via AdvisorApply.ApplyBlood
                // and ignores these page caps — otherwise this every-tick clamp would fight the advisor's
                // 60s routing and pin the spells to the manual caps (user-reported).
                //
                // SpaghettiThreshold is no longer stranded by that. It used to be read ONLY here, so
                // with CastBloodSpells on the number a user configured was read by nobody and silently
                // did nothing; BloodPlanner.DcBelowZoneRec now honours it as the same cap. The other two
                // thresholds still live only on this path — the advisor derives NUMBER and Counterfeit
                // Gold from its own routing rather than from a percentage.
                if (Settings.AutoSpellSwap && !Settings.CastBloodSpells)
                {
                    var spaghetti = (int)Math.Round((Character.bloodMagicController.lootBonus() - 1) * 100);
                    var counterfeit = (int)Math.Round((Character.bloodMagicController.goldBonus() - 1) * 100);
                    double number = Character.bloodMagic.rebirthPower;
                    Character.bloodMagic.rebirthAutoSpell = Settings.BloodNumberThreshold > 0 && number < Settings.BloodNumberThreshold;
                    Character.bloodMagic.goldAutoSpell = Settings.CounterfeitThreshold > 0 && counterfeit < Settings.CounterfeitThreshold;
                    Character.bloodMagic.lootAutoSpell = Settings.SpaghettiThreshold > 0 && spaghetti < Settings.SpaghettiThreshold;
                    Character.bloodSpells.updateGoldToggleState();
                    Character.bloodSpells.updateLootToggleState();
                    Character.bloodSpells.updateRebirthToggleState();

                    // The 0.5s writer of the three blood toggles. AdvisorApply writes the same three
                    // from its own thresholds every 60s, so this one wins roughly 120 : 1 — which makes
                    // it the one that is actually in effect, and the other very nearly decorative.
                    // Both are in the ledger so the pair reads Contested rather than one of them
                    // silently appearing to be the only writer.
                    var bOn = new System.Collections.Generic.List<string>();
                    if (Character.bloodMagic.rebirthAutoSpell) bOn.Add("Rebirth");
                    if (Character.bloodMagic.lootAutoSpell) bOn.Add("Loot");
                    if (Character.bloodMagic.goldAutoSpell) bOn.Add("Gold");
                    Managers.WriteLedger.Record("blood.spells.quick",
                        bOn.Count > 0 ? string.Join(" + ", bOn.ToArray()) : "all off",
                        "threshold check — number, counterfeit gold and spaghetti",
                        Managers.ChallengeOverlay.Segment,
                        "Recomputed every 0.5 seconds from your three blood thresholds",
                        "AdvisorApply writes the same three toggles every 60 seconds",
                        "At 120 : 1 this is the writer whose answer you are living with");
                }

                WishManager.UpdateWishMenu();
            }
            catch (Exception e)
            {
                LogDebug(e.Message);
                LogDebug(e.StackTrace);
            }
        }

        // Runs every 10 seconds, our main loop
        private void AutomationRoutine()
        {
            try
            {
                if (!Settings.GlobalEnabled || !Managers.CompatibilityGate.ActionsAllowed)
                {
                    _timeLeft = 10f;
                    return;
                }

                if (Settings.ManageInventory && !InventoryController.midDrag)
                {
                    ih[] converted = Character.inventory.GetConvertedInventory().ToArray();
                    ih[] boostSlots = InventoryManager.GetBoostSlots(converted);
                    InventoryManager.EnsureFiltered(converted);
                    InventoryManager.ManageConvertibles(converted);
                    InventoryManager.MergeEquipped(converted);
                    InventoryManager.MergeInventory(converted);
                    InventoryManager.MergeBoosts(converted);
                    InventoryManager.MergeGuffs(converted);
                    InventoryManager.BoostInventory(boostSlots);
                    InventoryManager.BoostInfinityCube();
                    InventoryManager.ManageBoostConversion(boostSlots);
                    InventoryController.updateInventory();
                }

                if (Settings.Autosave && Character.settings.dailySaveRewardTime.totalseconds >= 82800.0)
                {
                    Character.settings.dailySaveRewardTime.reset();
                    Character.addAP(200);
                    var customPath = $"{Application.persistentDataPath}/NGUSave-Build-{Character.getVersion()}-{DateTime.Now:MMMM-dd-HH-mm} (advisor).txt";
                    PlayerPrefs.SetString("savedPath", customPath);
                    Character.lastTime = Epoch.Current();
                    var data = Character.importExport.getBase64Data();
                    using (var writer = new StreamWriter(customPath))
                        writer.WriteLine(data);
                }

                ZoneHelpers.RefreshTitanSnapshots();
                if (Settings.ManageTitans || Settings.NeedsGoldSwap())
                {
                    if (ZoneHelpers.AnyTitansSpawningSoon() != LockManager.HasTitanLock())
                        LockManager.TryTitanSwap();
                }
                else if (LockManager.HasTitanLock())
                {
                    LockManager.TryTitanSwap();
                }

                if (Settings.ManageYggdrasil && Character.buttons.yggdrasil.interactable)
                {
                    YggdrasilManager.ManageYggHarvest();
                    YggdrasilManager.CheckFruits();
                }

                // Advisor auto-apply (Phase B): opt-in per-system application of advisor recs.
                // Before the AutoBuy block, which can return early.
                AdvisorApply.Tick();

                if (Settings.AutoBuyEM || Settings.AutoBuyAdventure)
                {
                    // We haven't unlocked custom purchases yet (a PERMANENT unlock — does NOT re-lock on
                    // Evil, so gate on all-time highestBoss, not the difficulty-local boss).
                    if (Character.highestBoss < 17)
                        return;

                    long total = 0;

                    var buyEnergy = false;
                    var buyR3 = false;
                    var buyMagic = false;

                    var buyPower = false;
                    var buyToughness = false;
                    var buyHP = false;
                    var buyRegen = false;

                    var ePurchase = Character.energyPurchases;
                    var mPurchase = Character.magicPurchases;
                    var r3Purchase = Character.res3Purchases;

                    if (Settings.AutoBuyEM)
                    {
                        var energy = ePurchase.customAllCost() > 0;
                        var r3 = Character.res3.res3On && r3Purchase.customAllCost() > 0;
                        // magic RESOURCE is permanent (only Augs/AT/TM/Blood re-lock on Evil) — highestBoss.
                        var magic = Character.highestBoss >= 37 && mPurchase.customAllCost() > 0;

                        if (energy)
                            total += ePurchase.customAllCost();

                        if (magic)
                            total += mPurchase.customAllCost();

                        if (r3)
                            total += r3Purchase.customAllCost();

                        buyEnergy = energy;
                        buyR3 = r3;
                        buyMagic = magic;
                    }

                    var aPurchase = Character.adventurePurchases;
                    long power = aPurchase.customPowerCost(Character.settings.customPowerInput);
                    long toughness = aPurchase.customToughnessCost(Character.settings.customToughnessInput);
                    long health = aPurchase.customHPCost(Character.settings.customHPInput);
                    long regen = aPurchase.customRegenCost(Character.settings.customRegenInput);

                    if (Settings.AutoBuyAdventure)
                    {
                        buyPower = power > 0;
                        buyToughness = toughness > 0;
                        buyHP = health > 3; // UI does NOT allow you to set HP purchase to less than 10 (for 3xp)
                        buyRegen = regen > 0;

                        total += (buyPower ? power : 0)
                            + (buyToughness ? toughness : 0)
                            + (buyHP ? health : 0)
                            + (buyRegen ? regen : 0);
                    }

                    if (total > 0)
                    {
                        double numPurchases = Math.Floor((double)(Character.realExp / total));
                        numPurchases = Math.Min(numPurchases, 10);

                        if (numPurchases > 0)
                        {
                            var t = string.Empty;
                            if (buyEnergy)
                                t += "/exp";

                            if (buyMagic)
                                t += "/magic";

                            if (buyR3)
                                t += "/res3";

                            if (buyPower)
                                t += "/power";

                            if (buyToughness)
                                t += "/tougness";

                            if (buyHP)
                                t += "/hp";

                            if (buyHP)
                                t += "/regen";

                            t = t.Substring(1);

                            Log($"Buying {numPurchases} {t} purchases");
                            for (var i = 0; i < numPurchases; i++)
                            {
                                if (buyEnergy)
                                    ePurchase.CallMethod("buyCustomAll");

                                if (buyMagic)
                                    mPurchase.CallMethod("buyCustomAll");

                                if (buyR3)
                                    r3Purchase.CallMethod("buyCustomAll");

                                if (buyPower)
                                    aPurchase.CallMethod("buyCustomPower");

                                if (buyToughness)
                                    aPurchase.CallMethod("buyCustomToughness");

                                if (buyHP)
                                    aPurchase.CallMethod("buyCustomHP");
                            }
                        }
                    }
                }

                _profile.DoAllocations();

                _profile.CastBloodSpells();

                if (Settings.AutoQuest && Character.buttons.beast.interactable)
                {
                    // Only build the converted inventory snapshot when it will actually be used
                    if (!InventoryController.midDrag)
                    {
                        ih[] converted = Character.inventory.GetConvertedInventory().ToArray();
                        InventoryManager.ManageQuestItems(converted);
                    }
                    QuestManager.PerformSlowActions();
                }

                if (Character.adventure.zone >= 1000)
                    ITOPODManager.UpdateMaxFloor();

                if (!Settings.AutoRebirth || !_profile.DoRebirth())
                {
                    if (Settings.MoneyPitRunMode && MoneyPitRunRebirth.RebirthAvailable())
                        BaseRebirth.DoRebirth();
                }

                if (Settings.ManageMayo)
                    CardManager.CheckManas();
                if (Settings.TrashCards)
                    CardManager.TrashCards();
                if (Settings.AutoCastCards)
                    CardManager.CastCards();
                if (Settings.CardSortEnabled && Settings.CardSortOrder.Length > 0)
                    CardManager.SortCards();

                if (Settings.ManageCooking)
                    CookingManager.ManageFood();

                if (Settings.ManageTitans)
                {
                    for (int i = 6; i <= 12; i++)
                    {
                        if (!Settings.TitanSwapTargets[i])
                            continue;

                        var version = ZoneHelpers.TitanVersion(i);
                        while (version < 4)
                        {
                            if (ZoneHelpers.AutokillAvailable(i, version + 1))
                                version++;
                            else
                                break;
                        }

                        if (Settings.TitanCombatMode == 4)
                        {
                            while (version > 0)
                            {
                                if (ZoneHelpers.AutokillAvailable(i, version))
                                    break;
                                version--;
                            }
                            if (version <= 0)
                                Settings.TitanSwapTargets[i] = false;
                        }

                        if (version > 0)
                            ZoneHelpers.SetTitanVersion(i, version);
                    }
                }

            }
            catch (Exception e)
            {
                LogDebug(e.Message);
                LogDebug(e.StackTrace);
            }
            _timeLeft = 10f;
        }

        public static void LoadAllocation()
        {
            _profile = new CustomAllocation(_profilesDir, Settings.AllocationFile);
            try
            {
                _profile.ReloadAllocation();
            }
            catch (Exception e)
            {
                LogDebug(e.Message);
            }
        }

        // The zone the tempZone chain (audit/40 §2 R10) WOULD route if it were reached:
        // gear hunt > advisor drop farm > Target ITOPOD > Settings.SnipeZone. Extracted so the SEVEN
        // gates that return above it can name what they are displacing without restating the rule —
        // a second copy would be free to drift, and reporting the wrong displaced zone is worse than
        // reporting none. -1 means "no intent": combat is off, so nothing here would route anyway.
        //
        // ⚠ internal, NOT private, FOR audit/40 §3 item 7. QuestManager.UpdateShouldQuest held a
        // SECOND COPY of this chain's Target ITOPOD row and had drifted from it twice — it knew
        // neither the gear-hunt row nor the drop-farm row 271f5f8 added above it, so quests took R7
        // and pre-empted both, one row above the row 271f5f8 fixed. The copy is deleted and the
        // question is asked HERE instead. That consumer legitimately wants the INTENT rather than
        // the routed zone: it IS R7, which sits above R10, so asking what actually routed would make
        // it read its own output. Item 7 changed no line of the chain itself — it changed only who
        // may call it. (The chain WAS changed afterwards, by the overload below; see there.)
        //
        // The parameterless form is the one QuestManager wants: it is a consumer of the ANSWER and
        // has no use for what Target ITOPOD discarded. `out _` keeps that call site unchanged.
        internal static int ResolveIntentZone() => ResolveIntentZone(out _);

        // OVERLOAD, so the R10 rule stays one copy. `discardedByItopod` is the written zone target
        // Settings.AdventureTargetITOPOD threw away this pass, or -1 when it threw nothing away.
        //
        // audit/40 §3 item 3's SURVIVING HALF. 271f5f8 rescued the advisor's own drop farm from the
        // toggle; §6.1 recorded on purpose that nothing else was rescued — "Target ITOPOD keeps its
        // meaning everywhere else, including for the boost farm, a wider change than the defect
        // justified". That precedence is deliberate and is NOT changed here. The SILENCE was never
        // deliberate: AdvisorApply.cs:1175-1179 writes the boost-farm zone and logs
        // "Advisor: farm zone -> X", the row below discards it, and nothing says so — the identical
        // shape that cost a full run of farming that never left the ITOPOD (audit/41 §2.1).
        //
        // Answered HERE rather than at the call site because this method IS the rule. A second copy
        // of "did the toggle win?" would be free to drift from the one that decides it, and a line
        // naming the wrong discarded zone is worse than no line at all (see the header above).
        //
        // ⚠ THIS IS WHERE THE R10 TERNARY BECAME A THREE-BRANCH LADDER. The old single line
        // `return Settings.AdventureTargetITOPOD ? 1000 : Settings.SnipeZone;` cannot report which
        // zone it discarded, because it never names one. Same precedence, same two outcomes — the
        // ladder only adds the assignment. QuestStandDownTests pins the new shape; see its comment.
        private static int ResolveIntentZone(out int discardedByItopod)
        {
            discardedByItopod = -1;
            if (!Settings.CombatEnabled) return -1;
            if (GearHunter.Active && GearHunter.ZoneReachable()) return Settings.GearHuntZone;
            if (Settings.AdvisorZones && Managers.FarmVenue.DropFarmActive
                && Settings.SnipeZone >= 0 && Settings.SnipeZone < 1000) return Settings.SnipeZone;
            if (!Settings.AdventureTargetITOPOD) return Settings.SnipeZone;
            // Only a zone the character could actually be standing in counts as displaced. -1 is the
            // SavedSettings sentinel (SavedSettings.cs:13) and 1000 IS the ITOPOD, so neither is a
            // contention — reporting either would be the "silence ≠ zero" error in reverse.
            if (Settings.SnipeZone >= 0 && Settings.SnipeZone < 1000) discardedByItopod = Settings.SnipeZone;
            return 1000;
        }

        // audit/40 §3 items 1, 2, 4, 5 and 6: every override in SnipeZone() discards or rewrites the
        // written zone target without a word, and the advisor's own "farm zone -> X" line reads as a
        // statement of fact when it is only a statement of intent. NOTE, DO NOT DECIDE — the chain
        // is unchanged; see ZoneRouting's header.
        //
        // Latched on three ints before any string exists: this sits on the every-frame path.
        private static void NoteRouting(Managers.ZoneRouting.Cause cause, int intended, int routed)
        {
            try
            {
                if (!Managers.ZoneRouting.ShouldSurface(cause, intended, routed,
                                                       out var previous, out var previousSpoke)) return;
                var text = Managers.ZoneRouting.Describe(previous, previousSpoke, cause,
                    intended, intended < 0 ? null : ZonePhaseReader.ZoneName(intended),
                    routed, routed < 0 ? null : ZonePhaseReader.ZoneName(routed));
                Managers.ZoneRouting.Spoke(!string.IsNullOrEmpty(text));
                if (string.IsNullOrEmpty(text)) return;
                // Record() logs as well as feeding the overlay, so this is one line, not two.
                ChallengeOverlay.Record("ZONE", text, Managers.ZoneRouting.Reason(previous, cause));
            }
            catch { }
        }

        private void SnipeZone()
        {
            try
            {
                CombatHelpers.IsCurrentlyGoldSniping = false;
                CombatHelpers.IsCurrentlyQuesting = false;
                CombatHelpers.IsCurrentlyAdventuring = false;
                CombatHelpers.IsCurrentlyFightingTitan = false;

                if (!Settings.GlobalEnabled || !Managers.CompatibilityGate.ActionsAllowed)
                    return;

                if (!Character.buttons.adventure.interactable)
                    return;

                CombatManager.UpdateFightTimer(Time.deltaTime);

                // At most ONE routing note per pass. A gate that takes routing notes and returns; a
                // gate that DECLINES records its cause here and lets a later owner overwrite it,
                // because "the gold snipe found nothing" is not the interesting fact once a titan
                // owns the zone. The tail note (Cause.None) is what says an override released.
                var routeCause = Managers.ZoneRouting.Cause.None;
                int routeIntent = -1;

                // If tm ever drops to 0, reset our gold loadout stuff (the "rebirth" snipe trigger —
                // gated by its S3 toggle in manual mode; advisor always re-snipes here).
                if (Character.machine.realBaseGold == 0.0 && Settings.GoldSnipeComplete)
                {
                    ResetFurthestZone();   // one owner for the per-run snipe state, not two in lockstep
                    Settings.TitanMoneyDone = new bool[ZoneHelpers.TitanZones.Length];
                    if (Settings.AdvisorGold || Settings.SnipeOnRebirth)
                    {
                        Log("Time Machine Gold is 0. Lets reset gold snipe zone.");
                        Settings.GoldSnipeComplete = false;
                        LastSnipeTrigger = "rebirth (TM empty)";
                    }
                }

                // Pit run logic
                if (MoneyPitManager.ShockwaveTier() <= 1e18 && MoneyPitManager.MoneyPitReady() && !MoneyPitManager.NeedsRebirth())
                {
                    // routed = -1 on purpose: the pit run alternates zone 0 and the ITOPOD WITHIN one
                    // hold (NeedsGold flips), and naming either would re-emit the line on every flip.
                    NoteRouting(Managers.ZoneRouting.Cause.PitRun, ResolveIntentZone(), -1);
                    if (MoneyPitManager.NeedsGold())
                    {
                        CombatManager.DoZone(0);
                    }
                    else // To avoid getting more gold
                    {
                        CombatHelpers.IsCurrentlyAdventuring = true;
                        CombatManager.DoZone(1000); // Checks fight timer and gold lock
                        ITOPODManager.Update();
                    }
                    return;
                }
                // This logic should trigger only if Time Machine is ready
                else if (Character.buttons.brokenTimeMachine.interactable && !Character.challenges.timeMachineChallenge.inChallenge)
                {
                    if (Character.machine.realBaseGold == 0.0)
                    {
                        NoteRouting(Managers.ZoneRouting.Cause.TimeMachineEmpty, ResolveIntentZone(), 0);
                        CombatManager.DoZone(0);
                        return;
                    }

                    // Go to our gold loadout zone next to get a high gold drop
                    if (Settings.ManageGoldLoadouts && !Settings.GoldSnipeComplete)
                    {
                        // Could be busy with other actions
                        if (LockManager.HasGoldLock() || LockManager.CanSwap())
                        {
                            UpdateFurthestZone();
                            if (_furthestZone >= 0)
                            {
                                NoteRouting(Managers.ZoneRouting.Cause.GoldSnipe, ResolveIntentZone(), _furthestZone);
                                CombatHelpers.IsCurrentlyGoldSniping = true;
                                CombatManager.DoZone(_furthestZone);
                                return;
                            }
                            // No fightable zone right now — fall through to normal routing (ITOPOD)
                            // instead of parking in the Safe Zone. audit/40 §3 item 6: a deliberate
                            // decline that says nothing is indistinguishable from a gate that never
                            // fired, so it is recorded and reported at the tail.
                            routeCause = Managers.ZoneRouting.Cause.GoldSnipeNoZone;
                        }
                    }
                }

                if (Settings.ManageTitans && LockManager.HasTitanLock())
                {
                    int? titanZone = ZoneHelpers.GetHighestSpawningTitanZone();
                    int titanIdx = titanZone.HasValue
                        ? Array.IndexOf(ZoneHelpers.TitanZones, titanZone.Value) : -1;
                    // A fight the game will not let us win is not a fight to route into. Today this is
                    // T4/UUG without the Ring of Apathy worn: EnemyAI sets invincible=true and doubles
                    // its own damage growth, so walking in is an unattended death loop. The gear swap
                    // has already run by here and would have equipped it if it could, so this only
                    // fires when it genuinely cannot. Falling through routes to quests/ITOPOD as usual
                    // — no stall state, and the reason is said once per transition rather than per tick.
                    if (titanIdx >= 0 && ZoneHelpers.TitanFightBlocked(titanIdx, out var blockedWhy))
                    {
                        if (_titanBlockedIdx != titanIdx)
                        {
                            _titanBlockedIdx = titanIdx;
                            Log($"NOT fighting titan {titanIdx + 1} (zone {titanZone.Value}): {blockedWhy}.");
                        }
                    }
                    else
                    {
                        _titanBlockedIdx = -1;
                        if (titanZone.HasValue && !ZoneHelpers.AutokillAvailable(titanIdx))
                        {
                            NoteRouting(Managers.ZoneRouting.Cause.Titan, ResolveIntentZone(), titanZone.Value);
                            CombatHelpers.IsCurrentlyFightingTitan = true;
                            CombatManager.DoZone(titanZone.Value);
                            return;
                        }
                    }
                }

                int questZone = QuestManager.IsQuesting();
                if (questZone >= 0)
                {
                    NoteRouting(Managers.ZoneRouting.Cause.Quest, ResolveIntentZone(), questZone);
                    CombatHelpers.IsCurrentlyQuesting = true;
                    CombatManager.DoZone(questZone);
                    return;
                }

                if (Settings.GoldCBlockMode)
                {
                    if (!Character.buttons.brokenTimeMachine.interactable || Character.challenges.timeMachineChallenge.inChallenge)
                    {
                        UpdateFurthestZone();
                        if (_furthestZone >= 0)
                        {
                            NoteRouting(Managers.ZoneRouting.Cause.CBlockGold, ResolveIntentZone(), _furthestZone);
                            CombatHelpers.IsCurrentlyAdventuring = true; // Not equipping gold loadout
                            CombatManager.DoZone(_furthestZone);
                            return;
                        }
                        // Nothing fightable yet (fresh challenge rebirth, moves locked) — fall
                        // through to normal routing (ITOPOD) until stats support an idle zone.
                        routeCause = Managers.ZoneRouting.Cause.CBlockNoZone;
                    }
                }

                if (!Settings.CombatEnabled)
                    return;

                // GEAR HUNT outranks ITOPOD targeting (user-reported: Target ITOPOD silently
                // overrode the hunted stage — the hunt toggle IS the routing intent while on).
                //
                // AN ADVISOR DROP FARM OUTRANKS IT FOR THE SAME REASON, and this is the SECOND time
                // the same defect has been reported: audit/40 §3 item 3 recorded that gear hunt was
                // fixed for exactly this and "the farm zone was not". Observed live 2026-08-05 —
                // SnipeZone 20, the advisor logging "rare farm -> Chocolate World", and the character
                // adventuring the ITOPOD the whole time because _adventureTargetItopod was left true
                // from an earlier session. The write happened, was announced, and was discarded one
                // line later with nothing said.
                //
                // GATED NARROWLY, ON PURPOSE. Only a farm the advisor is actively driving wins
                // (FarmVenue.DropFarmActive, raised by the gear farm / rare track / IDLE phase and
                // handed back on the ITOPOD phase and the boost fall-through). Target ITOPOD keeps
                // its meaning everywhere else, including for the boost farm, which is a broader
                // change than this defect justifies. The SnipeZone < 1000 test makes a stale flag
                // unable to do anything except pick the zone the advisor already wrote.
                //
                // THE RULE ITSELF NOW LIVES IN ResolveIntentZone() so the seven gates above can name
                // what they displace from the same source. One copy, on purpose: a second would be
                // free to drift, and a displacement line naming the wrong zone is worse than none.
                int tempZone = ResolveIntentZone(out var discardedByItopod);
                if (tempZone < 1000 && !CombatManager.IsZoneUnlocked(tempZone))
                {
                    // audit/40 §3 item 4: this rewrite has never said a word either way, so a locked
                    // target and a honoured one produce the same silence.
                    int lockedTarget = tempZone;
                    tempZone = Settings.AllowZoneFallback ? ZoneHelpers.GetMaxReachableZone(false) : 1000;
                    routeCause = Managers.ZoneRouting.Cause.UnlockFallback;
                    routeIntent = lockedTarget;
                }

                // EVIL CLIMB pushes boss numbers to unlock T7 — that means adventuring the highest clearable
                // zone (bosses + gold + the digger/aug income they feed), NEVER the ITOPOD, which pushes no
                // boss and drops no gold. Target ITOPOD is a steady-state XP toggle and wrong during a climb:
                // honoring it parked us in the ITOPOD after one kill and gross gold (the digger budget)
                // collapsed (user-caught). Farm the furthest clearable zone for the whole climb; normal
                // ITOPOD routing resumes automatically the moment the segment changes. Segment is only ever
                // set under AutoProfile, so manual runs are untouched. Gear hunt (tempZone < 1000) still wins.
                if (tempZone >= 1000 && ChallengeOverlay.Segment == "EVIL CLIMB")
                {
                    UpdateFurthestZone();
                    if (_furthestZone >= 0)
                    {
                        // audit/40 §3 item 5: sound reason, recorded in the comment above, never
                        // surfaced at runtime — including when it overrides an EXPLICIT ITOPOD choice.
                        NoteRouting(Managers.ZoneRouting.Cause.EvilClimb, tempZone, _furthestZone);
                        CombatHelpers.IsCurrentlyAdventuring = true;
                        CombatManager.DoZone(_furthestZone);
                        return;
                    }
                    // Nothing clearable yet (fresh climb rebirth, stats too low) — fall through to normal
                    // routing (ITOPOD) until stats support an idle zone.
                    routeCause = Managers.ZoneRouting.Cause.EvilClimbNoZone;
                    routeIntent = -1;
                }

                // No Time Machine (locked early / TM challenge) and headed to the ITOPOD: ITOPOD enemies
                // drop NO gold, and without gold the Augments that drive Power/Toughness stall. While we
                // can't afford two of the cheapest augment upgrade, farm the best clearable gold zone
                // instead; the moment gold recovers, normal ITOPOD routing resumes.
                bool tmUnavailable = !Character.buttons.brokenTimeMachine.interactable
                    || Character.challenges.timeMachineChallenge.inChallenge;
                if (tempZone >= 1000 && tmUnavailable && OptimizationAdvisor.GoldStarvedForAugs(Character, 2.0))
                {
                    UpdateFurthestZone();
                    if (_furthestZone >= 0)
                    {
                        NoteRouting(Managers.ZoneRouting.Cause.GoldStarvedAugs, tempZone, _furthestZone);
                        CombatHelpers.IsCurrentlyAdventuring = true;
                        CombatManager.DoZone(_furthestZone);
                        return;
                    }
                    // Same silent decline as R5/R8/R12, and the only one audit/40 §3 item 6 did not
                    // list — it has neither a comment nor a line today.
                    routeCause = Managers.ZoneRouting.Cause.GoldStarvedNoZone;
                    routeIntent = -1;
                }

                // CLAIMED LAST, so anything more specific keeps the pass. R11/R12/R13 and the two
                // hand-backs are facts about THIS frame; the toggle discard is a standing state, and
                // the latch reports it the moment the transient clears (the frame after a decline is
                // either an owner returning with its own line, or this). One note per pass is the
                // rule set at the top of this method.
                if (routeCause == Managers.ZoneRouting.Cause.None && discardedByItopod >= 0)
                {
                    routeCause = Managers.ZoneRouting.Cause.TargetItopod;
                    routeIntent = discardedByItopod;
                }

                NoteRouting(routeCause,
                    routeCause == Managers.ZoneRouting.Cause.None ? tempZone : routeIntent,
                    tempZone);

                CombatHelpers.IsCurrentlyAdventuring = true;
                CombatManager.DoZone(tempZone);

                if (tempZone >= 1000)
                    ITOPODManager.Update();
            }
            catch (Exception e)
            {
                LogDebug(e.Message);
                LogDebug(e.StackTrace);
            }
        }

        private void DumpEquipped()
        {
            var list = new List<int>
            {
                Character.inventory.head.id,
                Character.inventory.chest.id,
                Character.inventory.legs.id,
                Character.inventory.boots.id,
                Character.inventory.weapon.id
            };

            if (InventoryController.weapon2Unlocked())
                list.Add(Character.inventory.weapon2.id);

            foreach (var acc in Character.inventory.accs)
                list.Add(acc.id);

            list.RemoveAll(x => x == 0);
            var items = $"[{string.Join(", ", list)}]";

            Log($"Equipped Items: {items}");
            Clipboard.SetText(items);
        }

        public void OnGUI()
        {
            if (Settings.DisableOverlay)
                return;
            float scale = UnityEngine.Screen.height / 900f;
            float offset = 10f * scale;
            float width = 200f * scale;
            float height = 40f * scale;
            // Cache the GUIStyle instead of allocating one on every OnGUI event (fires multiple times per frame)
            if (_overlayStyle == null || _overlayStyleScale != scale)
            {
                _overlayStyle = new GUIStyle("label")
                {
                    fontSize = Mathf.CeilToInt(10 * scale)
                };
                _overlayStyleScale = scale;
            }
            var style = _overlayStyle;
            var autoState = !Managers.CompatibilityGate.ActionsAllowed
                ? "OBSERVE-ONLY (game build changed - see log)"
                : (Settings.GlobalEnabled ? "Active" : "Inactive");
            GUI.Label(new Rect(offset, 0 * offset, width * 2f, height), $"Automation - {autoState}", style);
            GUI.Label(new Rect(offset, 1 * offset, width, height), $"Next Loop - {_timeLeft:00.0}s", style);
            GUI.Label(new Rect(offset, 2 * offset, width, height), $"Profile - {Settings.AllocationFile}", style);
            GUI.Label(new Rect(offset, 3 * offset, width, height), $"Action - {LockManager.GetLockTypeName()}", style);
            var prog = Managers.ProgressionAnalyzer.Detect();
            GUI.Label(new Rect(offset, 4 * offset, width * 1.5f, height), $"Stage - {prog.Label}", style);
            if (prog.Known)
                GUI.Label(new Rect(offset, 5 * offset, width * 2f, height), $"Goal - {prog.NextGoal}", style);
        }

        public void MonitorLog()
        {
            var bLog = Character.adventureController.log;
            var log = bLog.GetFieldValue<PlayerLog, List<string>>("Eventlog");
            for (var i = log.Count - 1; i >= 0; i--)
            {
                var line = log[i];
                if (!line.Contains("dropped")) continue;
                if (line.Contains("gold")) continue;
                var lower = line.ToLower();
                if (lower.Contains("special boost")) continue;
                if (lower.Contains("toughness boost")) continue;
                if (lower.Contains("power boost")) continue;
                if (line.EndsWith("<b></b>")) continue;
                var result = line;
                if (result.Contains("\n"))
                    result = result.Split('\n').Last();

                var sb = new StringBuilder(result);
                sb.Replace("<color=blue>", "");
                sb.Replace("<b>", "");
                sb.Replace("</color>", "");
                sb.Replace("</b>", "");

                LogLoot(sb.ToString());
                log[i] = $"{line}<b></b>";
            }
        }

        // Keep the furthest-zone ratchet honest: ratchet UP to the best currently-clearable zone, but
        // when a rebirth crashed our stats (challenge chains) and the ratcheted zone is no longer
        // fightable AT ALL, drop back to the best idle-able zone — the old behavior kept sending us
        // to the stale high zone, and CombatManager parked in the Safe Zone forever.
        private static void UpdateFurthestZone()
        {
            int before = _furthestZone;
            var best = ZoneStatHelper.GetBestZone();
            if (best == null)
            {
                if (_furthestZone > 0 && ZoneStatHelper.ZoneFightType(_furthestZone) == 0)
                    _furthestZone = -1;
                return;
            }
            if (best.Zone > _furthestZone)
            {
                // Mid-snipe promotions are legitimate: stats grow continuously (NGUs, cube), so a
                // zone that measured just-short right after the gold swap can come back in reach
                // seconds later. Log it — the silent retarget after a logged drop-back read as
                // "sniped the wrong zone in the wrong gear" (user-reported).
                if (_furthestZone >= 0 && LockManager.HasGoldLock())
                    Log($"Zone {best.Zone} back in reach mid-snipe (stats grew); retargeting from {_furthestZone}.");
                _furthestZone = best.Zone;
            }
            else if (_furthestZone > best.Zone && ZoneStatHelper.ZoneFightType(_furthestZone) == 0)
            {
                // Mid-snipe (gold lock held) the shortfall is the gold loadout's weaker stats, not
                // a rebirth — say so, and note that the new-zone trigger stays disarmed for it.
                Log(LockManager.HasGoldLock()
                    ? $"Gold loadout can't clear zone {_furthestZone}; sniping {best.Zone} instead (re-arms on the next new zone or rebirth)."
                    : $"Gold zone {_furthestZone} is no longer fightable after rebirth; dropping back to {best.Zone}.");
                _furthestZone = best.Zone;
            }
            if (_furthestZone != before)
                AdviseZoneDropChance(_furthestZone);
        }

        // RvL-style drop-chance advice, once per zone change: how much total drop chance the new farm
        // zone wants before its regular drops are capped (from the game's own loot tables), vs what we
        // have now (Character.lootFactor is the exact multiplier the drop rolls use).
        private static int _lastDcAdviceZone = -1;

        private static void AdviseZoneDropChance(int zone)
        {
            if (zone < 0 || zone == _lastDcAdviceZone) return;
            _lastDcAdviceZone = zone;
            try
            {
                if (!ZoneStatHelper.RecommendedDcPercent.TryGetValue(zone, out var recPct)) return;
                double curPct = Character.lootFactor() * 100.0;
                string name = ZoneHelpers.ZoneList.TryGetValue(zone, out var n) ? n : $"Zone {zone}";
                if (curPct < recPct)
                    Log($"{name}: recommend >= {FmtPct(recPct)} total drop chance to cap its regular drops (currently {FmtPct(curPct)}).");
            }
            catch (Exception e) { LogDebug($"DC advice: {e.Message}"); }
        }

        private static string FmtPct(double v)
        {
            string[] suf = { "%", "K%", "M%", "B%" };
            int i = 0;
            while (v >= 1000 && i < suf.Length - 1) { v /= 1000; i++; }
            return v >= 100 ? $"{v:0}{suf[i]}" : $"{v:0.#}{suf[i]}";
        }

        public static void ResetFurthestZone()
        {
            _furthestZone = -1;
            _lastNewZoneTrigger = -1;
        }

        // S3 trigger engine: which event fired last (Gold pipeline's snipe stage shows it).
        public static string LastSnipeTrigger = "";
        public static int FurthestZone => _furthestZone;

        public void SetResnipe()
        {
            // Trigger PRIORITY is the pure decision in Managers.SnipeTrigger.Decide (audit M5); the live
            // reads, the static baseline/last-armed state, and the latch side effects stay here.
            bool advisor = Settings.AdvisorGold;
            bool armNewZone = advisor || Settings.SnipeOnNewZone || Settings.GoldCBlockMode;
            var best = armNewZone ? ZoneStatHelper.GetBestZone() : null;   // computed only when armed, as before
            bool allowTimer = !advisor && Settings.SnipeOnTimer && Settings.ResnipeTime > 0;   // timer is manual-only
            bool timerHit = allowTimer && Math.Abs(Character.rebirthTime.totalseconds - Settings.ResnipeTime) < 1;

            var r = Managers.SnipeTrigger.Decide(armNewZone, best != null, best?.Zone ?? -1,
                _furthestZone, _lastNewZoneTrigger, Settings.GoldSnipeComplete, allowTimer, timerHit);

            if (r.SeedBaseline)
                _furthestZone = best.Zone;

            if (r.Trigger != null && Settings.GoldSnipeComplete)
            {
                if (r.NewZone >= 0)
                    _lastNewZoneTrigger = r.NewZone;
                Settings.GoldSnipeComplete = false;
                LastSnipeTrigger = r.Trigger;
                Log($"Re-snipe: {r.Trigger}");
            }
        }

        public void ShowBoostProgress()
        {
            ih[] boostSlots = InventoryManager.GetBoostSlots(Character.inventory.GetConvertedInventory().ToArray());
            try
            {
                InventoryManager.ShowBoostProgress(boostSlots);
            }
            catch (Exception e)
            {
                LogDebug(e.Message);
                LogDebug(e.StackTrace);
            }
        }

        public void OnApplicationQuit() => Loader.Unload();

        public static void ResetBoostProgress()
        {
            Log($"Resetting Boost Average");
            InventoryManager.Reset();
        }
    }
}
