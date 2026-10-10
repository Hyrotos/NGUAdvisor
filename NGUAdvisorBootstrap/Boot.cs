using System;
using System.IO;
using System.Reflection;

namespace NGUAdvisorBootstrap
{
    // Hot-reload bootstrap: injected ONCE per game session (Run NGU Advisor.bat), then loads the real
    // NGUAdvisor.dll from BYTES. Assembly.Load(byte[]) bypasses Mono's by-name image cache, so a new
    // build on disk can be loaded again into the same session — no game restart.
    //
    // Reload() is invoked by the payload's hot-reload request (F5, or the companion's "Hot-reload
    // advisor" button) via reflection -- Main.PerformHotReload looks this type up by name: it tears the old
    // payload down through its own Loader.Unload() (destroys the GameObject, which stops the update
    // loops and closes the forms), then byte-loads whatever is on disk. The old assembly stays in
    // memory inert (a few MB per reload, cleared by the next real restart) — acceptable dev cost.
    public static class Boot
    {
        private static Assembly _payload;
        private static string _dllPath;
        private static string _logPath;

        public static void Init()
        {
            try
            {
                // Guard against double-running the bat in one session (seen in logs: two payloads
                // started 13s apart — duplicate Mains fight over timers and writers).
                if (_payload != null)
                {
                    Log("Init called again but a payload is already running - use Hot-reload advisor (F5) instead.");
                    return;
                }
                var localLow = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData", "LocalLow", "NGUAdvisor");
                _logPath = Path.Combine(localLow, "logs", "bootstrap.log");

                // smi loads this assembly from MEMORY, so Assembly.Location is empty (first attempt
                // resolved "NGUAdvisor.dll" against the game's CWD and failed). The bat writes its own
                // injector directory to a well-known file before injecting; that is the source of truth.
                _dllPath = null;
                var pathFile = Path.Combine(localLow, "injector-path.txt");
                if (File.Exists(pathFile))
                {
                    var dir = (File.ReadAllText(pathFile) ?? "").Trim();
                    if (dir.Length > 0 && File.Exists(Path.Combine(dir, "NGUAdvisor.dll")))
                        _dllPath = Path.Combine(dir, "NGUAdvisor.dll");
                    else
                        Log($"injector-path.txt points at '{dir}' but NGUAdvisor.dll is not there");
                }
                else
                {
                    Log($"no injector-path.txt at {pathFile} - start through the launcher or 'Run NGU Advisor.bat' (they write the path)");
                }

                // Fallback: a path-loaded copy of this assembly knows its own directory.
                if (_dllPath == null)
                {
                    var loc = Assembly.GetExecutingAssembly().Location;
                    if (!string.IsNullOrEmpty(loc))
                    {
                        var cand = Path.Combine(Path.GetDirectoryName(loc), "NGUAdvisor.dll");
                        if (File.Exists(cand)) _dllPath = cand;
                    }
                }

                if (_dllPath == null)
                {
                    Log("init FAILED: could not locate NGUAdvisor.dll (no valid injector-path.txt, no assembly location)");
                    return;
                }

                Log($"bootstrap up; payload path: {_dllPath}");
                LoadPayload();
            }
            catch (Exception e)
            {
                Log($"init FAILED: {e}");
            }
        }

        public static void Reload()
        {
            try
            {
                Log("reload requested");
                try
                {
                    _payload?.GetType("NGUAdvisor.Loader")?.GetMethod("Unload")?.Invoke(null, null);
                    Log("old payload unloaded");
                }
                catch (Exception e)
                {
                    Log($"unload threw (continuing with load): {e.Message}");
                }
                LoadPayload();
            }
            catch (Exception e)
            {
                Log($"reload FAILED: {e}");
            }
        }

        private static void LoadPayload()
        {
            var bytes = File.ReadAllBytes(_dllPath);
            _payload = Assembly.Load(bytes);
            _payload.GetType("NGUAdvisor.Loader").GetMethod("Init").Invoke(null, null);
            Log($"payload loaded and started: {_payload.GetName().Name} v{_payload.GetName().Version} ({bytes.Length} bytes)");
        }

        private static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath));
                File.AppendAllText(_logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n");
            }
            catch { }
        }
    }
}
