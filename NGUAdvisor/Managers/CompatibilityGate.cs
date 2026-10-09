using System;
using System.IO;

namespace NGUAdvisor.Managers
{
    // Fail-closed game-version gate (audit finding P0-3).
    //
    // The advisor reads hundreds of live NGU Idle members by name/index and takes actions off them.
    // Nothing here checked the game build, so the first patch that renames or renumbers a member would
    // make those reads SILENTLY wrong and the advisor would keep automating on bad data. This records
    // the game version the advisor was last run (and implicitly validated) against; when the live
    // version later changes, ACTIONS are held in observe-only until the user re-baselines. Reads, HUD
    // and advice are never gated by this - only automation that changes game state.
    //
    // Re-baseline (acknowledge a new build): delete <advisorDir>\compat.dat and Reload Advisor. The
    // next start records the current build and resumes automation. We deliberately fail OPEN on a
    // fresh install (no baseline yet) and when the version can't be read at all - the gate's job is to
    // catch a build CHANGE during ongoing use, not to guess whether an unknown build is correct.
    public static class CompatibilityGate
    {
        private const string AckFileName = "compat.dat";

        private static bool _evaluated;
        private static bool _actionsAllowed;   // default false => fail-closed until the build is confirmed
        private static int _liveVersion;
        private static int _baselineVersion;
        private static string _ackPath;

        // True only once we've confirmed the live game build matches the acknowledged baseline (or we
        // could not read a version to gate on). Evaluated lazily so a caller during a window where
        // Character isn't ready yet simply sees "not allowed" and retries on the next tick.
        public static bool ActionsAllowed
        {
            get { EnsureEvaluated(); return _actionsAllowed; }
        }

        public static bool Evaluated => _evaluated;
        public static int LiveVersion => _liveVersion;
        public static int BaselineVersion => _baselineVersion;

        // Called from Main.Start once the advisor directory is known. Safe to call again (e.g. on
        // Reload) - it re-reads the baseline file and re-evaluates from scratch.
        public static void Initialize(string advisorDir)
        {
            try { _ackPath = string.IsNullOrEmpty(advisorDir) ? null : Path.Combine(advisorDir, AckFileName); }
            catch { _ackPath = null; }
            _evaluated = false;
            EnsureEvaluated();
        }

        private static void EnsureEvaluated()
        {
            if (_evaluated) return;

            var c = Main.Character;
            if (c == null) return;   // can't decide yet; stay fail-closed and retry on the next access

            int live;
            try { live = c.getVersion(); }
            catch { live = 0; }
            _liveVersion = live;

            // Can't read a sane version to gate on: don't brick a working setup over something we can't
            // measure. Allow, but say so once.
            if (live <= 0)
            {
                _actionsAllowed = true;
                _evaluated = true;
                Main.Log("Compatibility gate: could not read the game build; proceeding without a version check.");
                return;
            }

            _baselineVersion = ReadBaseline();

            // First run (or a deliberate re-baseline): adopt the current build. We have no oracle for
            // "correct", so a fresh install trusts the build it is installed on.
            if (_baselineVersion <= 0)
            {
                WriteBaseline(live);
                _baselineVersion = live;
                _actionsAllowed = true;
                _evaluated = true;
                Main.Log($"Compatibility gate: baselined to game build {VersionText(live)}. Automation enabled.");
                return;
            }

            if (_baselineVersion == live)
            {
                _actionsAllowed = true;
                _evaluated = true;
                return;
            }

            // The build changed under us. Hold actions; reads/HUD stay alive.
            _actionsAllowed = false;
            _evaluated = true;
            Main.Log("==============================================================");
            Main.Log($"COMPATIBILITY HOLD: game build changed ({VersionText(_baselineVersion)} -> {VersionText(live)}).");
            Main.Log("Automation is paused (OBSERVE-ONLY): a game update can move or rename the values the");
            Main.Log("advisor reads, which would make it act on wrong data.");
            Main.Log($"If you have confirmed the advisor still behaves correctly on build {VersionText(live)},");
            Main.Log($"delete '{HostPath.For(_ackPath)}' and Reload Advisor to resume automation.");
            Main.Log("==============================================================");
        }

        private static int ReadBaseline()
        {
            try
            {
                if (_ackPath == null || !File.Exists(_ackPath)) return 0;
                var txt = (File.ReadAllText(_ackPath) ?? "").Trim();
                return int.TryParse(txt, out var v) ? v : 0;
            }
            catch { return 0; }
        }

        private static void WriteBaseline(int version)
        {
            try { if (_ackPath != null) File.WriteAllText(_ackPath, version.ToString()); }
            catch (Exception e) { Main.LogDebug("Compatibility gate: could not write baseline: " + e.Message); }
        }

        // Short status for the HUD/overlay; null when healthy (nothing to show).
        public static string StatusLine()
        {
            if (!_evaluated) return "checking game build...";
            if (_actionsAllowed) return null;
            return $"OBSERVE-ONLY - game build changed to {VersionText(_liveVersion)} (delete compat.dat + reload to resume)";
        }

        // getVersion() is an int like 1234 meaning "1.234"; mirror Character.getVersionAsString()'s format.
        private static string VersionText(int v)
        {
            try { return (v / 1000) + "." + (v % 1000).ToString("000"); }
            catch { return v.ToString(); }
        }
    }
}
