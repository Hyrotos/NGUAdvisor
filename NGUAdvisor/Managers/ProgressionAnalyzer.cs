using System;
using System.Linq;
using NGUAdvisor.AllocationProfiles.RebirthStuff;

namespace NGUAdvisor.Managers
{
    // Route C3 Phase 3.1: reads LIVE progression state (titan kills, boss, difficulty, challenge-block state)
    // to produce an accurate stage, a milestone-based "next goal", and a context-aware profile recommendation
    // — replacing the crude highestBoss+difficulty heuristic. Consumed by StatusPanel/DashboardPanel/overlay.
    // Cached/throttled (heavier optimality math is added in 3.2), guarded, main-thread only.
    //
    // CANONICAL CHAPTER ENGINE — this Chapter (derived from actual TITAN KILLS) is the authoritative "what
    // chapter am I in" for stage/HUD/perk/profile advice, and supersedes the coarse boss-threshold
    // StageDetector.Chapter for those uses. StageDetector is retained ONLY for its two boss-anchored consumers
    // (ChallengeOverlay segment gating, LevelPlanner NGU-track). The two Chapter values intentionally DIVERGE
    // when boss progress leads titan kills (see StageDetector's class note for the full contrast) — do NOT treat
    // them as the same number or substitute one for the other.
    public static class ProgressionAnalyzer
    {
        public struct Progression
        {
            public bool Known;
            public int Chapter;              // 1..8. Canonical titan-kill chapter (see class note) — NOT StageDetector.Chapter (boss-threshold).
            public string Label;             // "Ch.4 T6"
            public string Difficulty;        // Normal / Evil / Sadistic
            public string Activity;          // what we're doing now
            public string NextGoal;          // milestone we're working toward
            public string RecommendedProfile;
            public string RecommendReason;
            public string OptimalFocus;      // GO-style "best gain" advice (filled in 3.2)
        }

        private static readonly Progression Unknown = new Progression
        {
            Known = false, Chapter = 0, Label = "Stage -", Difficulty = "", Activity = "-",
            NextGoal = "-", RecommendedProfile = "", RecommendReason = "", OptimalFocus = ""
        };

        private static Progression _cache = Unknown;
        private static DateTime _cacheTime = DateTime.MinValue;
        private const double CacheMs = 750;

        public static Progression Detect()
        {
            if ((DateTime.UtcNow - _cacheTime).TotalMilliseconds < CacheMs && _cache.Known)
                return _cache;
            try
            {
                _cache = Compute();
                _cacheTime = DateTime.UtcNow;
                return _cache;
            }
            catch (Exception e)
            {
                Main.LogDebug($"ProgressionAnalyzer failed: {e.Message}");
                return _cache.Known ? _cache : Unknown;
            }
        }

        private static Progression Compute()
        {
            var c = Main.Character;
            if (c == null || c.settings == null) return Unknown;

            var diff = c.settings.rebirthDifficulty;
            string diffName = diff == difficulty.sadistic ? "Sadistic" : diff == difficulty.evil ? "Evil" : "Normal";
            int boss = ZoneHelpers.CurrentHighestBoss(c);

            bool t6 = TitanBeaten(5), t7 = TitanBeaten(6), t8 = TitanBeaten(7);

            int chapter; string name;
            if (diff == difficulty.sadistic) { chapter = 8; name = "Sadistic"; }
            else if (diff == difficulty.evil)
            {
                if (t8) { chapter = 7; name = "T9"; }
                else if (t7) { chapter = 6; name = "T8-JRPG"; }
                else { chapter = 5; name = "Evil-IDP"; }
            }
            else
            {
                if (t6) { chapter = 4; name = "T6"; }
                else if (boss >= 100) { chapter = 3; name = "T4-BAE"; }
                else if (boss >= 58) { chapter = 2; name = "T1-Mega"; }
                else { chapter = 1; name = "Start-HSB"; }
            }

            var challenge = ChallengeDetector.Current();
            bool inBlock = challenge != null || SafeAnyChallengesValid();
            string mode = LockManager.GetLockTypeName();
            string activity = challenge != null ? "Challenge " + challenge
                : mode != "Default" ? mode
                : inBlock ? "Challenge block" : "Farming / idle";

            string nextGoal = inBlock ? "Complete challenge block" : MilestoneGoal(chapter, boss);
            string focus = GetOptimalFocus(chapter);

            string rec, reason;
            if (inBlock)
            {
                rec = Main.Settings?.AllocationFile ?? "";
                reason = "In a challenge block — stay on this profile.";
            }
            else
            {
                rec = RecommendProfile(diff, chapter, out reason);
            }

            return new Progression
            {
                Known = true,
                Chapter = chapter,
                Label = $"Ch.{chapter} {name}",
                Difficulty = diffName,
                Activity = activity,
                NextGoal = nextGoal,
                RecommendedProfile = rec,
                RecommendReason = reason,
                OptimalFocus = focus
            };
        }

        // GO-optimality (3.2): compare the optimizer's best loadout to the currently-equipped one for a
        // stage-appropriate, base-100 objective (never zero-scores). Heavier (runs Optimize) so throttled
        // separately (~10s) and cached. Names the gear-improvement headroom; augment/NGU are auto-optimized
        // by the allocation engine already (BestAug / NGU targets), so they aren't re-recommended here.
        private static string _focus = "";
        private static DateTime _focusTime = DateTime.MinValue;
        private const double FocusMs = 10000;

        // The set the optimizer would equip for whatever objective is in force, and which objective that
        // was. Captured from the Optimize() call THIS METHOD ALREADY MAKES — the companion can show the
        // user what "optimal" actually means without a single extra optimizer run. Empty when nothing is
        // in force, or while the Loot Hunter hybrid owns the gear (it has no single objective to score).
        //
        // This is the only place the answer exists at all: a profile gear breakpoint computes its ids
        // live in PerformSwap, equips them and discards them — it writes neither the profile nor any
        // loadout list — so before this there was no way to see the picks short of the F10 dump.
        public static int[] BestGearIds { get; private set; } = new int[0];
        public static string BestGearFor { get; private set; } = "";

        private static string GetOptimalFocus(int chapter)
        {
            if ((DateTime.UtcNow - _focusTime).TotalMilliseconds < FocusMs) return _focus;
            _focusTime = DateTime.UtcNow;
            try
            {
                // Score against the ACTIVE gear objective — the one the equip logic actually optimises
                // for — so this gap agrees with what's equipped and with the Re-optimize-now button.
                // Scoring a fixed objective (NGUs) while gear is equipped for a different one (e.g. Power
                // on a titan push) reported a phantom "+N%" gap (user-reported: says +11% while the
                // loadout already agrees). Fall back to the chapter default when no objective is active.
                // Match the equip logic EXACTLY — same objective AND same forceTopRespawn flag — so the
                // rec can never advertise a gap the advisor won't act on. forceTopRespawn deliberately
                // trades objective score for a respawn item; scoring the pure objective (forceRespawn=
                // false, the old default here) reported that trade as a phantom "+N%" that the equip /
                // Re-optimize-now path correctly calls "already optimal" (the persistent +11% NGUs). And
                // when no gear objective is active, the advisor isn't managing gear — advertise no gap.
                // Through the SHARED resolver, not GearBreakpoints directly: the profile timeline is only
                // one of the sources (challenge rotation, gear hunt, auto-profile segment and the user's
                // standing pick are the others). Reading the timeline alone would advertise "no gap" for
                // every user whose objective comes from anywhere else — including anyone who set a pin.
                //
                // ⚠ AND THE GEAR LOCK, for exactly the same reason the respawn flag is here. A lock
                // pins items the optimiser would not have chosen, so the LOCKED best set scores at or
                // below the unlocked one — permanently. Solving without it would compare an unlocked
                // ideal against the locked set the advisor actually equips and report the difference
                // as a gap: a standing "Re-optimize gear: +N%" recommendation that the equip path
                // correctly refuses to act on, forever. Same phantom-gap shape as the respawn flag,
                // and it took a user report to find that one.
                string objName = null; bool forceRespawn = false; int[] locks = null;
                try
                {
                    var resolved = GearObjectiveApply.Current();
                    objName = resolved.Name;
                    forceRespawn = resolved.ForceRespawn;
                    locks = resolved.Locks;
                }
                catch { }
                if (string.IsNullOrEmpty(objName)) { _focus = ""; BestGearIds = new int[0]; BestGearFor = ""; return _focus; }
                var obj = GearOptimizer.FindObjective(objName);
                if (obj == null) { _focus = ""; BestGearIds = new int[0]; BestGearFor = ""; return _focus; }
                double cur = GearOptimizer.CurrentScore(obj);
                var best = GearOptimizer.Optimize(obj, forceRespawn, GearLockSet.Of(locks));
                double opt = best.Score;
                // Free: this Optimize already ran, and its picks were being thrown away.
                BestGearIds = best.AllIds().Where(x => x > 0).Distinct().ToArray();
                BestGearFor = obj.Name;
                var chainObj = obj as GearChain.ChainObjective;
                if (chainObj != null)
                {
                    // Same 8% threshold, on the chain step that decides (the lead score alone would
                    // read a chain's own set as a downgrade).
                    int k = GearOptimizer.ChainDecidingStep(chainObj, best, 1.08, out bool improves,
                                                            out double wornStep, out double bestStep);
                    _focus = improves
                        ? "Re-optimize gear: " + (wornStep > 0 ? $"+{(bestStep / wornStep - 1) * 100:0}% " : "")
                          + $"{chainObj.Priorities[k].Objective.Name} ({objName})"
                        : $"Gear near-optimal ({objName})";
                }
                else if (cur > 0 && opt > cur)
                {
                    double pct = (opt / cur - 1.0) * 100.0;
                    _focus = pct >= 8 ? $"Re-optimize gear: +{pct:0}% {objName}" : $"Gear near-optimal ({objName})";
                }
                else _focus = $"Gear near-optimal ({objName})";
            }
            catch (Exception e) { Main.LogDebug($"OptimalFocus failed: {e.Message}"); _focus = ""; }
            return _focus;
        }

        // Versioned titans (T6..T12, index 5..11): beaten >= v1 when TitanVersion (which is version+1) >= 2.
        // T5 via boss5Kills. Low titans (T1..T4) are inferred from highestBoss in the chapter logic.
        private static bool TitanBeaten(int idx)
        {
            try
            {
                if (idx >= 5 && idx <= 11) return ZoneHelpers.TitanVersion(idx) >= 2;
                if (idx == 4) return Main.Character.adventure.boss5Kills >= 1;
                return false;
            }
            catch { return false; }
        }

        // Versions of a versioned titan beaten (TitanVersion is version+1).
        private static bool TitanVersionBeaten(int idx, int version)
        {
            try { return ZoneHelpers.TitanVersion(idx) - 1 >= version; }
            catch { return false; }
        }

        private static string MilestoneGoal(int chapter, int boss)
        {
            switch (chapter)
            {
                // Compact hints — sized to fit the status strip's NEXT GOAL cell (the full guide detail
                // lives in the chapter's Goal line + NGU-KNOWLEDGE.md). Standard NGU shorthand: T# = Titan,
                // B# = Boss.
                case 1: return "Kill T1 (GRB)";
                case 2: return "B100 → kill T4";
                case 3: return "Beards → kill T6";
                case 4:
                    if (!TitanVersionBeaten(5, 4)) return "Kill T6 v4";
                    if (boss < 300) return "Reach B300";
                    return "Atk boost → Evil";
                case 5:
                    if (!TitanBeaten(6)) return "B125 → kill T7";
                    if (boss < 166) return "B166 → T8 puzzle";
                    return "Kill T8";
                case 6:
                    if (!TitanBeaten(7)) return "Kill T8";
                    return "R3 → farm T-sets";
                case 7:
                    if (!TitanBeaten(8)) return "Kill T9";
                    return "24 AK → Rad set";
                case 8: return "Sadistic titans";
                default: return "-";
            }
        }

        // Stage/state -> best installed preset for the not-in-a-block case. NEVER text-matches the
        // milestone label (user-reported: every Normal milestone names a titan, so the old
        // goal-contains-"Titan" rule recommended the no-rebirth LRB push essentially always). The
        // Normal steady state is the guide's 24h cadence — every run pushes the number, harvests
        // fruits at the 24h tier (seeds), banks ~24h beard growth, and spends the bulk of the day
        // in the NGU marathon. Normal-LRB (RebirthTime -1) is a deliberate one-shot push, only
        // recommended when the next titan kill is actually in reach (see TitanPushInReach).
        private static string RecommendProfile(difficulty diff, int chapter, out string reason)
        {
            if (diff == difficulty.sadistic)
            {
                reason = "No Sadistic-specific preset authored yet — NGU-focused default.";
                return "Goal-NGU";
            }
            if (diff == difficulty.evil)
                return RecommendEvilProfile(out reason);

            if (TitanPushInReach(out var target))
            {
                reason = $"{target} in reach — one long push, no auto-rebirth; rebirth manually after the kill.";
                return "Normal-LRB";
            }
            if (chapter <= 2)
            {
                reason = "Early game: push adventure zones and boss EXP.";
                return "Goal-Adventure";
            }
            reason = "Daily cadence: number push + fruit/seed harvest + beard banking + NGU marathon.";
            return "Normal-24hr";
        }

        // Evil daily drivers, keyed on the guide's ch.5 re-unlock ladder (EV 58 -> PPPL 100 -> T7 125 ->
        // Meta 158 -> IDP 166).
        //
        // Every non-Normal difficulty used to return "Goal-NGU" — one constant for the entire back half
        // of the game — with a code comment deferring the real answer until "the user reaches those
        // stages, where they're testable". They have. Worse, the constant was not merely unhelpful: on
        // Evil, Goal-NGU carries NGUDiff Diff:0, which pins the NORMAL NGU level track for the whole run,
        // and Rebirth Time 24h1m. Recommending it to someone running a hack day or a challenge block
        // (RebirthTime -1) would flip them off the Evil track AND rebirth a profile deliberately written
        // never to rebirth. Those Evil profiles existed all along in SampleProfiles/Evil — they were just
        // never promoted into Presets/, so the installer could not deliver what the advisor named.
        //
        // TitanPushInReach runs here too. It reads NextObjective + ProjectedBestGear, both of which are
        // already difficulty-correct, so it was only ever Normal-only by accident of the early return.
        private static string RecommendEvilProfile(out string reason)
        {
            int boss = 0;
            try { boss = ZoneHelpers.CurrentHighestBoss(Main.Character); } catch { }

            if (TitanPushInReach(out var target))
            {
                reason = $"{target} in reach — one long push, no auto-rebirth; rebirth manually after the kill.";
                return "LRB-Evil";
            }
            if (boss < 125)
            {
                reason = "Evil re-climb: short runs to re-unlock TM/AT/Wandoos and push the boss wall.";
                return "24hr-EarlyEvil";
            }
            if (boss < 166)
            {
                reason = "T7 window: the guide's 24h Evil shape — TM, augments, NGU+AT, then the Evil-NGU tail.";
                return "24hr-Evil";
            }
            if (boss < 250)
            {
                reason = "Post-IDP: 24h Evil cadence with the Evil NGU track carrying the run.";
                return "24hr-MidEvil";
            }
            reason = "Late Evil: 24h cadence into the T9 push.";
            return "24hr-EndEvil";
        }

        // Kill-readiness gate for the LRB recommendation. In reach = we CAN'T clear the next
        // titan's staged requirement right now, but the optimizer's best Power/Toughness gear
        // projects to >= 70% of it — close enough that one long run of stat building (BT/AT/TM
        // compounding on top of the gear swing) plausibly crosses the line. If current stats
        // already clear it, the 24h cadence takes the kill in stride (titan sniping runs either
        // way); if projected stats are far off, 24h compounding beats a stalled long run. The
        // factor is an approximation — tune against reality like the kill ladder was. Throttled
        // (~10s) like GetOptimalFocus: NextObjective + ProjectedBestGear lean on optimizer runs.
        private const double LrbReachFactor = 0.70;
        private static bool _pushInReach;
        private static string _pushTarget = "";
        private static DateTime _pushAt = DateTime.MinValue;

        private static bool TitanPushInReach(out string target)
        {
            if ((DateTime.UtcNow - _pushAt).TotalMilliseconds < FocusMs)
            {
                target = _pushTarget;
                return _pushInReach;
            }
            _pushAt = DateTime.UtcNow;
            _pushInReach = false;
            _pushTarget = "";
            try
            {
                var o = OptimizationAdvisor.NextObjective();
                if (o.Known && o.ReqAttack > 0)
                {
                    double atk = Main.Character.totalAdvAttack();
                    double def = Main.Character.totalAdvDefense();
                    bool killableNow = atk >= o.ReqAttack && def >= o.ReqDefense;
                    if (atk > 0 && !killableNow)
                    {
                        OptimizationAdvisor.ProjectedBestGear(out var atkMult, out var defMult);
                        if (atk * atkMult >= o.ReqAttack * LrbReachFactor &&
                            def * defMult >= o.ReqDefense * LrbReachFactor)
                        {
                            _pushInReach = true;
                            _pushTarget = $"T{o.Index + 1} {o.Stage}";
                        }
                    }
                }
            }
            catch (Exception e) { Main.LogDebug($"TitanPushInReach failed: {e.Message}"); }
            target = _pushTarget;
            return _pushInReach;
        }

        private static bool SafeAnyChallengesValid()
        {
            try { return BaseRebirth.AnyChallengesValid(); }
            catch { return false; }
        }
    }
}
