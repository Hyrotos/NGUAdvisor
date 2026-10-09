using System;

namespace NGUAdvisor.Managers
{
    // WHEN A GEAR PASS RE-EQUIPS AND WHEN IT DECLINES.
    // Unity-free, linked into the test project.
    //
    // ── WHY THIS EXISTS ───────────────────────────────────────────────────────────────────────────
    // Two paths answer the same user request — "give me gear for this objective":
    //
    //   AdvisorApply.ForceGearReoptimize   the "Re-optimize gear now" button
    //   AdvisorApply.ApplyGearRefresh      the periodic pass, once per 120s
    //
    // They are both Unity-bound (live inventory, LoadoutManager.ChangeGear), so neither can be unit
    // tested, and they drifted: the periodic pass bypassed its score bar when the OBJECTIVE changed,
    // the button did not. Observed 2026-08-18 on a user's end-game save — the operator picked "Wishes",
    // pressed the button five times over two minutes, got no gear change and no log output, and the
    // periodic pass then switched to Wishes on its own. Wishes and NGUs share four of Wishes' seven
    // stats (GearObjectives.cs:84,94), so an NGU-optimised set clears a "did the score improve?" bar
    // routinely — which is why the two paths disagreed in practice and not just in principle.
    //
    // ⚠ WHAT THE TWO PATHS ACTUALLY DISAGREE ABOUT — measured, after a first reading got it backwards.
    // ApplyGearRefresh's `objectiveChanged` bypass applies to its 5% ANTI-CHURN BAR ONLY. On an
    // objective change it STILL declines to equip when the worn set out-scores the solved one
    // (AdvisorApply.cs:1601-1606, which commits the trackers and returns without equipping). The button
    // carried the same worn-vs-best guard. So the two agree here, and the only real difference is the
    // 5% bar itself, which is periodic-only and deliberate: it exists to stop drop-driven churn, and
    // the button is a deliberate human action that should not be throttled.
    //
    // That is why this class does NOT make an objective change bypass the worn-vs-best guard. It cannot:
    // the optimiser searches the whole inventory, so `best <= worn` on a fresh objective means THE WORN
    // SET ALREADY IS that objective's best (only a lock can make it otherwise, which is what locksWorn
    // covers). Re-equipping it anyway would be a pure cost — LoadoutManager.ChangeGear ZEROES energy,
    // magic and R3 allocation across every system until the next allocation pass.
    //
    // ⚠ WHAT THIS CLASS DELIBERATELY DOES NOT DECIDE. The two remaining halves of the same fix are
    // SEQUENCING inside those Unity-bound methods and cannot be expressed here:
    //   * the trackers (_lastGearObjective/_lastGearLocks) must commit only AFTER a pass resolves,
    //     never before the guard — ApplyGearRefresh states it as "a no-op pass must NOT consume the
    //     bypass", and the button used to commit first and consume the change it then declined;
    //   * _lastGearCheck belongs to the PERIODIC pass alone. The button setting it meant each press
    //     pushed the automatic pass out another 120s.
    // Read those at their call sites; a green suite here says nothing about them.
    internal static class GearRefreshPolicy
    {
        public enum Verdict
        {
            /// <summary>Equip the solved set.</summary>
            Equip,
            /// <summary>The worn set is already the best available for this objective; leave it on.</summary>
            AlreadyOptimal,
        }

        /// <summary>
        /// Stable string form of a Gear Lock, for comparing one pass's locks against the last one's.
        /// </summary>
        /// <remarks>
        /// Order is PRESERVED, not sorted: the lock list comes from a profile row the user authored, and
        /// two different orderings of the same ids are treated as a change. That is the conservative
        /// direction — a spurious "changed" costs one re-equip, a missed change leaves the wrong gear on
        /// until the objective name itself moves.
        /// </remarks>
        public static string LockKey(int[] locks)
        {
            if (locks == null || locks.Length == 0) return "";
            var parts = new string[locks.Length];
            for (var i = 0; i < locks.Length; i++) parts[i] = locks[i].ToString();
            return string.Join(",", parts);
        }

        /// <summary>
        /// Has the user's request changed since the last resolved pass? A CHANGED GEAR LOCK COUNTS AS A
        /// CHANGED OBJECTIVE — the locks name concrete items chosen for a plan, so re-pinning them is as
        /// much a new request as renaming the objective.
        /// </summary>
        public static bool ObjectiveChanged(string objName, string lastObjective, string lockKey, string lastLockKey)
        {
            return !string.Equals(objName ?? "", lastObjective ?? "", StringComparison.Ordinal)
                || !string.Equals(lockKey ?? "", lastLockKey ?? "", StringComparison.Ordinal);
        }

        /// <summary>
        /// The manual button's decision.
        /// </summary>
        /// <param name="wornScore">Score of the currently equipped set, under THIS objective. 0 when unknown.</param>
        /// <param name="bestScore">Score of the solved set.</param>
        /// <param name="locksWorn">Are this objective's locked items actually on right now?</param>
        /// <remarks>
        /// Each conjunct earns its place:
        ///   wornScore > 0   an unknown worn score is not evidence of optimality;
        ///   best &lt;= worn    the "don't downgrade" test — and on a CHANGED objective it is also the
        ///                   "you are already wearing this objective's best set" test, because the
        ///                   optimiser searches the whole inventory;
        ///   locksWorn       the score comparison is BLIND to a lock, and a lock usually COSTS score —
        ///                   so without this, pressing the button while wearing a better UNLOCKED set
        ///                   answers "already optimal" and never equips the locked one.
        ///
        /// Deliberately independent of whether the objective changed; see the class remarks.
        /// </remarks>
        // The same question for a CHAIN objective, which has no single score to ask it with: a
        // later step takes accessories from the lead, so the chain's own set always scores its lead
        // BELOW a set built for the lead alone, and Decide would call that older set "optimal" and
        // never equip the chain. Set membership is the only honest test: nothing to do exactly when
        // every item of the best set is already on.
        public static Verdict DecideChain(bool bestSetWorn, bool locksWorn)
            => bestSetWorn && locksWorn ? Verdict.AlreadyOptimal : Verdict.Equip;

        public static Verdict Decide(double wornScore, double bestScore, bool locksWorn)
        {
            if (wornScore > 0 && bestScore <= wornScore && locksWorn)
                return Verdict.AlreadyOptimal;
            return Verdict.Equip;
        }
    }
}
