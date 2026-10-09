using System;
using SimpleJSON;

namespace NGUAdvisor.Managers
{
    // Hand-extracted titan requirement tables. Kept in this Unity-free class (pure double[][][] data, no game or
    // Unity dependency) so they can be shape/monotonicity-tested (review finding #33) WITHOUT loading the game.
    // OptimizationAdvisor consumes them via its local TitanAk/TitanGuide aliases.
    //
    // The SimpleJSON import at the top is not a game dependency: SimpleJson.cs is Unity-free and is already
    // linked into the test assembly alongside this file, so the chip-row helpers at the bottom that touch the
    // published DOM stay testable under the same rule as the rest of the class.
    public static class TitanTables
    {
        // How many VERSIONS of a titan have actually been DEFEATED, from the human-readable version number.
        //
        // ZoneHelpers.TitanVersion returns `titan{n}Version + 1` — the version you are currently ON, which
        // is never less than 1. Two separate callers needed "how many have I beaten" and only one of them
        // got the conversion right:
        //   LevelPlanner.TickNguTrack   Math.Max(0, TitanVersion(6) - 1)   correct
        //   ExpBalancer.Pools           TitanVersion(6) >= 1               ALWAYS TRUE
        // That second one silently disabled the guide's entire pre-T7 Evil EXP rule — ExpRatio.Split tests
        // t7Killed first, so the energy-only branch below it was unreachable for the whole of Evil ch.5,
        // and the account ran the post-T7 split instead. Nothing failed; the readout just quietly meant
        // something else.
        //
        // It lives here, in a Unity-free class, so the conversion has ONE home and a headless test rather
        // than being re-derived at each call site.
        // ⚠ DEPRECATED IN FAVOUR OF THE BESTIARY, AND THE REASON IS NOT ARITHMETIC.
        //
        // The conversion below is correct. Its PREMISE is not: it assumes "the version you are on" equals
        // "one more than the versions you have defeated". The decomp says that only holds if the player
        // presses the difficulty button by hand after every kill — `adventure.titan{n}Version` is written
        // ONLY by changeTitanDifficulty ([DECOMP] AdventureController.cs:3835-3846) and never
        // auto-advances. Under the advisor the field is pinned to whatever version is being CHASED or
        // PARKED, so it is a selector, not a ledger.
        //
        // Measured on the operator's account 2026-08-11: GREASY NERD V2 killed eight times, and this
        // function returned 0 defeated the whole while — because the selector had been parked on v1. That
        // silently disabled the Evil-NGU tail, the Evil nguLevelTrack switch and the pre-T7 EXP split, all
        // three of which gate on it, and pinned the goal card's kill-ladder stage at "first kill" forever.
        //
        // Kept because indices 0-4 and 12-13 have no per-version bestiary record, so callers still need a
        // fallback. NEW CALLERS SHOULD USE ZoneHelpers.VersionsDefeatedByKills, which reads the game's own
        // per-version kill counts.
        public static int VersionsDefeated(int humanVersion) => Math.Max(0, humanVersion - 1);

        // THE GAME'S OWN PER-VERSION KILL RECORD, which nothing in this codebase read until now.
        //
        // Both death paths write it: the autokill branch calls addKills on the exact version id
        // ([DECOMP] AdventureController.cs:958/975/992/1008/1021 and siblings) and a manual kill routes
        // through enemyDeath -> confirmedKill(spriteID) ([DECOMP] BestiaryController.cs:215-232). So it
        // counts kills however they happened, which is precisely the question the selector could not answer.
        //
        // V1 ids, versions consecutive ascending (V1..V4). Transcribed from the addKills call sites above,
        // NOT derived from a pattern — the gaps are irregular (339 follows 337, 365 follows 347) and a
        // formula would be wrong.
        //   index 5  T6 Beast     312..315
        //   index 6  T7 Nerd      334..337
        //   index 7  T8 Godmother 339..342
        //   index 8  T9 Exile     344..347
        //   index 9  T10 Hungers  365..368
        //   index 10 T11 Lobster  369..372
        //   index 11 T12 Amalg    373..376
        // -1 means "no per-version record exists" — indices 0-4 are unversioned titans with plain
        // titan{n}Kills fields, and 12-13 (Tippi, Traitor) are unversioned entirely.
        public static readonly int[] BestiaryV1Id =
        {
            -1, -1, -1, -1, -1, 312, 334, 339, 344, 365, 369, 373, -1, -1
        };

        // The bestiary enemy id for one titan version, or -1 when there is no record to read.
        // Unity-free so the id arithmetic is testable without the game build.
        public static int BestiaryId(int titanIndex, int version)
        {
            if (titanIndex < 0 || titanIndex >= BestiaryV1Id.Length) return -1;
            if (version < 1 || version > 4) return -1;
            int baseId = BestiaryV1Id[titanIndex];
            return baseId < 0 ? -1 : baseId + (version - 1);
        }

        public static bool HasVersionKillRecord(int titanIndex)
            => titanIndex >= 0 && titanIndex < BestiaryV1Id.Length && BestiaryV1Id[titanIndex] >= 0;

        // ---- chase hysteresis ---------------------------------------------------------------------
        //
        // WHETHER TO KEEP CHASING A FIRST KILL, given how far best-gear stats reach toward the bar.
        //
        // The decision this replaces was a bare threshold — ready at >= 1.0x, park below it — recomputed
        // every 60s. Sitting anywhere near the line, it alternated: observed on the operator's account
        // 2026-08-11 flipping v2 -> v1 -> v2 inside two minutes, and each landing on v1 dropped adventure
        // routing mid-spawn (at v1 the titan auto-kills, so there is no target to hold the zone) and
        // handed the game a free v1 kill instead of the v2 attempt it had just committed to.
        //
        // A deadband fixes the whole class: commit at the full bar, abandon only after falling well
        // below it. The asymmetry is deliberate — starting a doomed fight is cheap and recoverable,
        // abandoning a winnable one mid-window costs the entire spawn cycle (1-4.5h for T7).
        public const double ChaseCommitRatio  = 1.00;
        public const double ChaseAbandonRatio = 0.90;

        public static bool ChaseReady(bool wasChasing, double worstRatio)
            => wasChasing ? worstRatio >= ChaseAbandonRatio : worstRatio >= ChaseCommitRatio;

        // ---- manual-fight readiness ---------------------------------------------------------------
        //
        // One shared gate for queueing a manual fight and displaying its readiness. Regen is only
        // required when the row supplies a positive regen threshold.
        public static bool MeetsStatsRequirement(double attack, double defense, double regen,
            double reqAttack, double reqDefense, double reqRegen)
            => attack >= reqAttack && defense >= reqDefense && (reqRegen <= 0 || regen >= reqRegen);

        // The row a manual fight is judged against: the guide's manual columns, falling back to the AK
        // attack/defense where the guide has none. Null = no row at all (Tippi/Traitor at 12-13, or a
        // version the tables do not describe) — the requirement is unknown and the caller must decide
        // what that means rather than read it as "not ready".
        public static double[] ManualRow(int titanIndex, int version)
            => Row(Guide, titanIndex, version) ?? Row(Ak, titanIndex, version);

        // Can this titan/version be FOUGHT manually below AK? Always the guide's manual columns — never
        // the staged ladder: that one moves to the idle and then the AK row after the first kill, and a
        // below-AK titan by definition fails its AK row, so staging the gate dropped a titan from the
        // queue the moment it had died once (GRB, user-reported 2026-10-09). No regen gate: only the
        // game's autokill checks regen.
        public static bool ManualFightReady(int titanIndex, int version, double attack, double defense)
        {
            double[] row = ManualRow(titanIndex, version);
            return row != null && MeetsStatsRequirement(attack, defense, 0, row[0], row[1], 0);
        }

        private static double[] Row(double[][][] table, int titanIndex, int version)
            => titanIndex >= 0 && titanIndex < table.Length && version >= 1 && version <= table[titanIndex].Length
                ? table[titanIndex][version - 1] : null;

        // Titan abbreviations (0-based index), relocated from the retired WinForms TitansPanel — consumed by
        // AtHourPlanner + the UiBridge titan snapshot node.
        public static readonly string[] Abbrev =
        {
            "GRB", "GCT", "Jake", "UUG", "Walderp", "Beast", "Nerd",
            "Godmother", "Exile", "Hungers", "Lobster", "Amalg", "Tippi", "Traitor"
        };

        // ------------------------------------------------------- gear a fight REQUIRES for a mechanic
        // Items that must be WORN for the fight to be winnable at all, as opposed to items that merely
        // score well. Nothing else in the gear pipeline can express this: every objective is a set of
        // STATS, and the one entry here has none.
        //
        // T4 (index 3) — UUG THE UNMENTIONABLE, zone 14 — and the Ring of Apathy, item 135.
        // [DECOMP] InventoryController.apathyCheck() scans `character.inventory.accs` — the EQUIPPED
        // accessory slots — for id 135 and returns ITS LEVEL, or -1 when it is not equipped. EnemyAI
        // then branches on that number (:715-753, and again :1516-1545):
        //
        //     < 0    invincible = true;  growCount += 400;  growCount *= 2;     <- UNKILLABLE
        //     < 100  growCount += (100 - level), scaled by (2 - level/100)      <- still growing
        //     >= 100 the insult does nothing
        //
        // and damage is `1 + growCount/100`. So fighting UUG without the ring ON is not merely slow,
        // it cannot be won, against a boss whose damage is simultaneously ballooning.
        //
        // ⚠ THIS IS NOT THE AK GATE, and conflating the two is the trap. Autokill needs
        // `itemList.itemMaxxed[135]` — a flag on the ITEM LIST meaning "levelled to max"
        // (AdventureController:3201, and ZoneHelpers.AutokillAvailable case 3 reads exactly that).
        // apathyCheck needs it in an ACCESSORY SLOT RIGHT NOW. A player can satisfy the first and fail
        // the second, which is precisely the state the advisor used to walk into.
        //
        // 0 = no required accessory. Only T4 has one in the whole game as of this writing; the table is
        // per-titan so a second mechanic does not need a code change.
        public const int ApathyRingId = 135;

        // The level at which the ring fully suppresses UUG's growth. Below it the fight is winnable but
        // the boss still grows, so this is a WARNING threshold, not a gate.
        public const int ApathyFullLevel = 100;

        public static readonly int[] RequiredAccessory =
        {
            0, 0, 0, ApathyRingId, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        };

        public static int RequiredAccessoryFor(int titanIndex) =>
            titanIndex >= 0 && titanIndex < RequiredAccessory.Length ? RequiredAccessory[titanIndex] : 0;

        // Autokill attack/defense/HP-REGEN requirements per titan index (0-based) and version (1-4),
        // extracted from the game's autokillTitan{N}V{V}Achieved methods (reference/decomp-full/
        // AdventureController.cs). Regen (third column, 0 = no check) is a REAL gate from T4 up —
        // omitting it let the UI claim AK-ready while the game refused to fire. T4 additionally
        // needs a maxxed item 135 and T5 needs boss5Kills >= 3; T9+ can alternatively be unlocked
        // by kill counts. Those non-stat gates live in ZoneHelpers.AutokillAvailable — this table
        // is the stat path you can push toward.
        public static readonly double[][][] Ak =
        {
            new[] { new[] { 3000.0, 2500.0, 0.0 } },
            new[] { new[] { 9000.0, 7000.0, 0.0 } },
            new[] { new[] { 25000.0, 15000.0, 0.0 } },
            new[] { new[] { 8e5, 4e5, 1.4e4 } },
            new[] { new[] { 1.3e7, 7e6, 1.5e5 } },
            new[] { new[] { 2.5e9, 1.6e9, 2.5e7 }, new[] { 2.5e10, 1.6e10, 2.5e8 }, new[] { 2.5e11, 1.6e11, 2.5e9 }, new[] { 2.5e12, 1.6e12, 2.5e10 } },
            new[] { new[] { 5e14, 2.5e14, 5e12 }, new[] { 1e16, 5e15, 1e14 }, new[] { 2e17, 1e17, 2e15 }, new[] { 5e18, 2.5e18, 5e16 } },
            new[] { new[] { 5e18, 2.5e18, 5e16 }, new[] { 1e20, 5e19, 1e18 }, new[] { 2e21, 1e21, 2e19 }, new[] { 5e22, 2.5e22, 5e20 } },
            new[] { new[] { 1e23, 5e22, 1e21 }, new[] { 2e24, 1e24, 2e22 }, new[] { 4e25, 2e25, 4e23 }, new[] { 7.5e26, 3.7e26, 7.5e24 } },
            new[] { new[] { 4e28, 2e28, 4e26 }, new[] { 3.2e29, 1.6e29, 1.6e27 }, new[] { 2e30, 1e30, 1e28 }, new[] { 1e31, 5e30, 5e28 } },
            new[] { new[] { 1.8e31, 6e30, 1.2e29 }, new[] { 9e31, 3e31, 6e29 }, new[] { 3.6e32, 1.2e32, 2.5e30 }, new[] { 1.1e33, 3.6e32, 7.5e30 } },
            new[] { new[] { 3e33, 1e33, 2e31 }, new[] { 1.2e34, 4e33, 8e31 }, new[] { 3.6e34, 1.2e34, 2.4e32 }, new[] { 7.2e34, 2.4e34, 4.8e32 } },
        };

        // Guide-recommended kill-ladder stats per titan/version { manual atk, manual def, idle atk,
        // idle def } — the community guide's hand-tuned numbers (reference/ngu-guide/titan-list.md).
        // NOT derivable from Ak: the old 45%/80%-of-AK scalars were calibrated on T1 and
        // overstated Beast first-kill by ~60% and idle ~2x (user report). Idle 0/0 = the guide lists
        // no idle numbers (Walderp, Godmother, T10-T12): those are fought manually until AK, so the
        // ladder skips the idle stage. Manual numbers assume max move-cooldown items + Beast Mode on.
        // Walderp's manual is the FINAL form (first form is 800K/400K).
        public static readonly double[][][] Guide =
        {
            new[] { new[] { 1350.0, 1350.0, 2300.0, 2100.0 } },
            new[] { new[] { 5000.0, 4000.0, 6000.0, 5000.0 } },
            new[] { new[] { 1.4e4, 1.2e4, 2.2e4, 1.4e4 } },
            new[] { new[] { 4e5, 3e5, 6e5, 4e5 } },
            new[] { new[] { 4e6, 3e6, 0.0, 0.0 } },
            new[] { new[] { 7e8, 5e8, 1e9, 7e8 }, new[] { 7e9, 5e9, 1e10, 7e9 }, new[] { 7e10, 5e10, 1e11, 7e10 }, new[] { 7e11, 5e11, 1e12, 7e11 } },
            new[] { new[] { 1.4e14, 9e13, 3e14, 2e14 }, new[] { 3.2e15, 1.6e15, 6e15, 4e15 }, new[] { 5.5e16, 3.5e16, 1.2e17, 8e16 }, new[] { 1.3e18, 7.5e17, 2.5e18, 1.5e18 } },
            new[] { new[] { 1.7e18, 7e17, 0.0, 0.0 }, new[] { 3.9e19, 1.5e19, 0.0, 0.0 }, new[] { 6.6e20, 3.5e20, 0.0, 0.0 }, new[] { 1.5e22, 6.4e21, 0.0, 0.0 } },
            new[] { new[] { 2.5e22, 1.3e22, 6e22, 3e22 }, new[] { 3.8e23, 1.6e23, 1.5e24, 7e23 }, new[] { 7.5e24, 3.5e24, 3e25, 1.5e25 }, new[] { 2e26, 1e26, 7e26, 3.5e26 } },
            new[] { new[] { 1.55e28, 3e27, 0.0, 0.0 }, new[] { 1.3e29, 3.6e28, 0.0, 0.0 }, new[] { 7.8e29, 1.6e29, 0.0, 0.0 }, new[] { 4e30, 9e29, 0.0, 0.0 } },
            new[] { new[] { 1.1e31, 4e30, 0.0, 0.0 }, new[] { 6e31, 1.8e31, 0.0, 0.0 }, new[] { 2.5e32, 8.2e31, 0.0, 0.0 }, new[] { 7.5e32, 2.5e32, 0.0, 0.0 } },
            new[] { new[] { 1.47e33, 4.7e32, 0.0, 0.0 }, new[] { 5.6e33, 2.1e33, 0.0, 0.0 }, new[] { 2.1e34, 6e33, 0.0, 0.0 }, new[] { 4.1e34, 1e34, 0.0, 0.0 } },
        };

        // ------------------------------------------------------------------ autokill readiness (UI chips)
        // The state vocabulary + threshold arithmetic behind UiBridge's `titans.ak` chip row. It lives HERE,
        // beside the table it reads, for one reason: UiBridge cannot be linked into the test assembly (it
        // pulls in Unity and Assembly-CSharp), so any readiness logic written there is untestable. These
        // functions touch nothing but their arguments, so the classification is unit-testable even though
        // the live stat reads and the game's own gate are not.
        //
        // WHY THREE STATES. Meeting the stat table is NOT the same as being autokillable — that conflation
        // has already shipped a lie once (see the Ak header). ZoneHelpers.AutokillAvailable is the only
        // authority on whether the game will actually fire, and it carries non-stat gates the table cannot
        // express (T4 needs item 135 maxxed, T5 needs boss5Kills >= 3).
        public const string StateReady = "ready";        // the game's own gate says yes
        public const string StateGated = "gated";        // every stat threshold met, game still refuses -> more attack will NOT help
        public const string StateShort = "short";        // at least one stat threshold unmet
        public const string StateUnknown = "unknown";    // a live read failed; nothing is claimed either way

        // A threshold of 0 is the Ak table's "no gate here" sentinel (T1-T3 have no HP-regen check), and a
        // gate that does not exist is satisfied by definition. It is NOT "0% of nothing" — callers must not
        // publish a progress percentage for it, or the UI draws a bar for a requirement the game never makes.
        public static bool StatMet(double have, double threshold)
        {
            if (threshold <= 0) return true;
            if (double.IsNaN(have)) return false;
            return have >= threshold;
        }

        // Progress toward a threshold as a 0..100 whole percent. Only meaningful when threshold > 0; a
        // sentinel 0 threshold reports 100 (satisfied) so a caller that does publish it can't render a
        // permanently empty bar, but the intended treatment is to omit the value entirely.
        public static double StatPct(double have, double threshold)
        {
            if (threshold <= 0) return 100;
            if (double.IsNaN(have) || have <= 0) return 0;    // -Infinity lands here via `have <= 0`
            double p = have / threshold * 100.0;
            // +Infinity deliberately falls through to the 100 clamp below: StatMet already calls an infinite
            // stat "met", and a bar that reads 0% for a met gate would contradict its own chip.
            if (double.IsNaN(p)) return 0;
            if (p < 0) return 0;
            if (p > 100) return 100;
            return Math.Round(p);
        }

        // The Ak row for a titan+version, or null when either is out of range (a version the table doesn't
        // describe means we do not know the requirement — the caller must degrade, not guess).
        public static double[] AkRow(int titanIndex, int version)
        {
            if (Ak == null || titanIndex < 0 || titanIndex >= Ak.Length) return null;
            var versions = Ak[titanIndex];
            if (versions == null || version < 1 || version > versions.Length) return null;
            var row = versions[version - 1];
            return row != null && row.Length >= 3 ? row : null;
        }

        // How many versions a titan has = the number of requirement rows the table holds for it. Static per
        // titan, so the UI can render "v3 of 4" and tell "already at the last version" apart from "there is
        // harder content above the one being autokilled" WITHOUT a second game read.
        //
        // AGREES WITH ZoneHelpers.IsVersionedTitan BY CONSTRUCTION: rows 0-4 hold one version, rows 5-11 hold
        // four, which is exactly `titanIndex >= 5 && titanIndex <= 11`. Nothing in the compiler enforces that —
        // ZoneHelpers needs Unity and cannot be linked into the test assembly — so TitanTablesTests pins the
        // equivalence against a local copy of the predicate. If the table ever gains a row the predicate does
        // not expect (or vice versa), that test is the thing that says so.
        //
        // Out of range (Tippi/Traitor at 12-13, which have no Ak path at all) reports 1 rather than 0: one form
        // is the honest floor for a titan we have no version data for, and a 0 would hand the UI an empty
        // version range to divide by.
        public static int VersionCount(int titanIndex)
        {
            if (Ak == null || titanIndex < 0 || titanIndex >= Ak.Length) return 1;
            var versions = Ak[titanIndex];
            return versions == null || versions.Length < 1 ? 1 : versions.Length;
        }

        // All three stat gates cleared (regen only where the table sets one).
        public static bool AkStatsMet(double atk, double def, double regen, double[] req)
        {
            if (req == null || req.Length < 3) return false;
            return StatMet(atk, req[0]) && StatMet(def, req[1]) && StatMet(regen, req[2]);
        }

        // `autokillAvailable` is ZoneHelpers.AutokillAvailable — the game's own gate — and it OUTRANKS the
        // stat table in both directions:
        //   * true with stats short: T9-T12 unlock autokill from a kill count alone
        //     (autokillTitan9V1Achieved returns true at bestiary kills >= 24, before any stat check). Calling
        //     that "short" would tell the user to push stats they no longer need.
        //   * false with stats met: the T4/T5 item-and-kill gates. That is `gated`, and it is the whole point
        //     of this vocabulary — it says "stop pushing attack, this is blocked on something else".
        public static string AkState(double atk, double def, double regen, double[] req, bool autokillAvailable)
        {
            if (autokillAvailable) return StateReady;
            if (req == null || req.Length < 3) return StateUnknown;
            return AkStatsMet(atk, def, regen, req) ? StateGated : StateShort;
        }

        // ------------------------------------------------------------------ respawn cooldown (UI chips)
        // AUTOKILLABLE IS NOT KILLABLE NOW, and `state` only answers the first question. Nothing in
        // ZoneHelpers.AutokillAvailable — nor in any autokillTitan{N}V{V}Achieved method it calls — looks at
        // the respawn clock. The game gates the kill itself SEPARATELY, on
        // `boss{N}Spawn.totalseconds >= boss{N}SpawnTime()` (AdventureController.manageFight, and the same
        // comparison drives its own "TITAN AVAILABLE!" label), and EVERY kill — autokill or manual — calls
        // `boss{N}Spawn.reset()`. So "ready" and "on cooldown for another ninety minutes" are routinely true
        // at the same instant. That is the promise the chip row could not keep, and this key is the fix.
        //
        // ZERO MEANS AVAILABLE NOW; ABSENT MEANS UNKNOWN. Same contract as `regen` above, for the same reason
        // and with sharper teeth: `respawnSec: 0` is a positive claim that the titan can be fought this
        // second, so a failed read must never collapse into it.
        //
        // IT IS A READING, NOT A RATE — DO NOT EXTRAPOLATE IT. The clocks advance in AdventureController's
        // Update, and boss5Spawn (Walderp, index 4) is the one that does NOT advance unconditionally: it is
        // gated on `waldoDefeats <= waldoFinds || waldoFinds >= 4`, so his number can sit FROZEN at a positive
        // value for as long as it takes to find Waldo again. Anything that counts this key down locally
        // eventually promises an availability the game was never going to grant.
        public const string KeyRespawn = "respawnSec";
        private const string KeyIndex = "i";

        // No cooldown in the game is longer than boss12-boss14's 27000 s (7.5 h), and every bossNSpawnTime()
        // floors at 3600 s, so a full day is a loose ceiling that no live read can reach. Past it the number
        // is an artifact, not a wait, and "unknown" is the honest report.
        public const double RespawnSecMax = 86400.0;

        // Whole seconds of cooldown remaining, or null when nothing can be claimed.
        //
        // The producer is ZoneHelpers.TimeTillTitanSpawn, whose float? is nullable in signature only — every
        // path through it returns a value. It is typed here as float? anyway so that a future null becomes an
        // omitted key rather than a silent zero.
        //
        // CEILING, NOT ROUND: rounding 0.4 s down to 0 would publish "available now" while the game still
        // refuses, which is the exact class of lie this key exists to stop. Ceiling can only ever over-state
        // the wait by <1 s, and the snapshot republishes a second later regardless.
        //
        // A NEGATIVE IS NOT "OVERDUE", IT IS A BROKEN CONTRACT: the only producer clamps at zero
        // (Mathf.Max(0f, ...)), so a negative means that clamp is gone, and guessing "available" off a
        // reading we no longer understand is precisely the wrong direction to guess in.
        public static int? RespawnSeconds(float? raw)
        {
            if (!raw.HasValue) return null;
            double s = raw.Value;
            if (double.IsNaN(s) || double.IsInfinity(s)) return null;
            if (s < 0 || s > RespawnSecMax) return null;
            return (int)Math.Ceiling(s);
        }

        // Overlay live respawn countdowns onto an ALREADY-BUILT chip row, in place.
        //
        // Split from the row build on purpose. The readiness fields are reflection-heavy and change rarely, so
        // UiBridge caches that array for ~5 s; a countdown that is 5 s stale is simply a countdown that is
        // wrong, so the bridge calls THIS on every tick against the same cached instance. Measured cost of the
        // twelve reads it drives: ~4 us and ~1.6 KB per tick, against the ~215 us and ~314 KB the same tick
        // already spends serializing the snapshot line — about 2% of that one step alone.
        //
        // `read` is ZoneHelpers.TimeTillTitanSpawn, injected rather than called directly so this stays
        // testable (ZoneHelpers needs Unity and cannot be linked into the test assembly). It is allowed to
        // throw: it dereferences Main.Character plus two reflected members, and a game update that renames
        // either produces a null and an NRE. One titan's throw must not cost the other eleven their timer, so
        // each entry is read inside its own guard.
        //
        // SET OR REMOVE — NEVER LEAVE STALE. The array outlives the tick, so a key written while reads worked
        // would otherwise sit there frozen once they stop, and a countdown stuck at 00:42 is worse than no
        // countdown at all. Anything unreadable this tick has its key removed, which keeps absence meaning
        // exactly one thing.
        public static void StampRespawn(JSONArray ak, Func<int, float?> read)
        {
            if (ak == null || read == null) return;
            for (int k = 0; k < ak.Count; k++)
            {
                var o = ak[k] as JSONObject;
                if (o == null) continue;

                // Key off the entry's OWN `i`, not its array position: the row is documented as indexed by
                // that field. Reading a missing key back would hand us a JSONLazyCreator whose AsInt getter
                // WRITES a 0 into the parent (SimpleJson's lazy-creation side effect) and would then stamp
                // titan 0's clock onto it; IsNumber carries no such side effect, so it is tested first.
                var idx = o[KeyIndex];
                int? sec = null;
                if (idx != null && idx.IsNumber)
                {
                    try { sec = RespawnSeconds(read(idx.AsInt)); }
                    catch { sec = null; }
                }

                if (sec.HasValue) o[KeyRespawn] = sec.Value;
                else o.Remove(KeyRespawn);
            }
        }

        // ------------------------------------------------------------------ unlock gate (`unlocked`)
        // READY IS NOT SPAWNABLE, AND `state` CANNOT SEE WHY. `state` answers "will the game autokill this
        // titan"; `respawnSec` answers "is the cooldown up". For four of the twelve there is a THIRD gate
        // that neither one reads: `character.adventure.titan{N}Unlocked`. The game requires it in BOTH the
        // autokill branch (AdventureController.manageFight — e.g. line 899 for titan 6) and the spawn table
        // (spawnEnemy, lines 2396-2424), and NOTHING inside autokillTitan{N}V{V}Achieved looks at it. So a
        // locked Beast reads `ready` with a zero clock while the game will not put him in the zone at all.
        //
        // DELIBERATELY NOT FOLDED INTO `state`. ZoneHelpers.AutokillAvailable is the ADVISOR's own decision
        // input (gold-bank targeting, objective selection, kill-gear routing), not just this chip row's, so
        // narrowing what it returns changes automation and needs in-game validation. This key lets the row
        // tell the truth without touching the decision path: the UI renders LOCKED when `unlocked === false`,
        // whatever `state` says, and offers "unlock it" rather than "push attack".
        //
        // TRUE FOR THE OTHER EIGHT — NOT ABSENT. Titans 1-5 and 10-12 have no such flag, and a gate that does
        // not exist is satisfied by definition: exactly the rule StatMet already applies to the sentinel 0 in
        // the regen column. Publishing the vacuous `true` is what keeps ABSENCE MEANING EXACTLY ONE THING —
        // the read failed. A missing `unlocked` must never be rendered as "locked".
        public const string KeyUnlocked = "unlocked";

        // The four titans the game gates behind titan{N}Unlocked, by 0-based titan index:
        //   5 = Beast (titan6, zone 19)   6 = Nerd (titan7, 23)
        //   7 = Godmother (titan8, 26)    8 = Exile (titan9, 30)
        // No other index has one — grep of the decompile finds titan6/7/8/9Unlocked and nothing else.
        // (Zone 45, THE TRAITOR, carries a comparable live flag `adventure.ratTitanDefeated`, but titan 14
        // sits outside the Ak table entirely — it has no autokill path — so no chip exists to carry it.)
        public static bool HasUnlockFlag(int titanIndex) => titanIndex >= 5 && titanIndex <= 8;

        // `live` is character.adventure.titan{N}Unlocked as read this rebuild, or null when that read failed
        // or was never attempted. Returns null when nothing can be claimed — the caller must then OMIT the
        // key, never publish false.
        public static bool? UnlockState(int titanIndex, bool? live)
        {
            if (titanIndex < 0) return null;
            if (!HasUnlockFlag(titanIndex)) return true;   // no such gate -> satisfied, cf. StatMet(x, 0)
            return live;
        }

        // ------------------------------------------------------------------ Walderp hide-and-seek (`waldo`)
        // WHY TITAN 5'S CLOCK CAN SIT STILL. `respawnSec`'s header already warns that boss5Spawn is the one
        // clock that does not advance unconditionally; this node is the other half of that warning — the part
        // the player can act on.
        //
        // The mechanic (WaldoSaysUnlocker.cs + AdventureController.cs:725, 1597, 2393):
        //   * Zone 16 serves `enemyList[16][waldoDefeats]` — four decoys at 0-3, the real WALDERP (#310,
        //     bigBoss5) at 4. Beating a decoy raises `waldoDefeats` (LootDrop.zone16Drop) and he vanishes
        //     into a random menu as a faint image.
        //   * AdventureController.Update advances boss5Spawn ONLY while
        //     `waldoDefeats <= waldoFinds || waldoFinds >= 4`. While he is hidden the respawn clock is
        //     FROZEN — not slow, stopped — so the countdown the chip shows is not counting down.
        //   * Clicking him raises `waldoFinds`, which unfreezes the clock until the next decoy falls. Four
        //     defeat-then-find cycles later the real Walderp is what zone 16 spawns.
        //   * He relocates every 180 s (updateWaldo's `waldoTimer % 180`) to a fresh +/-200 px offset.
        // The game's own label for this state is "WALDERP IS HIDING IN THE MENUS! FIND HIM!" — it replaces
        // the timer rather than sitting beside it, which is the treatment to copy.
        //
        // `hiding` IS THE FREEZE CONDITION ITSELF, not a paraphrase of it. `waldoDefeats > waldoFinds` is
        // precisely the negation of the game's `waldoDefeats <= waldoFinds || waldoFinds >= 4`: finds only
        // ever rise while hiding and defeats caps at 4, so `defeats > finds` already implies `finds < 4`.
        //
        // DO NOT DERIVE IT FROM THE MENU ID. WaldoSaysUnlocker.currentMenu is -1 for the whole first ~180 s
        // after the game starts (waldoTimer is a scene field, not save data, so it restarts at 0 and the
        // first attachWaldoToMenu waits for the first 180-tick) and again for the frame or two between each
        // fade-out and the next relocation. He is hiding throughout. That is why `menu`/`menuName` are
        // OPTIONAL decorations on a state that `hiding` alone decides.
        public const int WaldoTitanIndex = 4;      // Walderp's index in Abbrev/Ak/the chip row
        public const int WaldoFindsRequired = 4;   // finds needed before zone 16 serves the real Walderp
        public const string KeyWaldo = "waldo";

        // A menu name is a scene GameObject name, i.e. content we do not control. Capped so a pathological
        // one cannot bloat a line that ships at 1 Hz.
        private const int WaldoMenuNameMax = 64;

        public static bool WaldoHiding(int defeats, int finds) => defeats > finds;

        // Build the `waldo` node from raw reads, or null when nothing can be claimed (the caller must then
        // omit the key — ABSENT MEANS UNREADABLE, never "not hiding").
        //
        // `menuId`/`menuName` are read from a DIFFERENT object (Character.waldoUnlocker) to `defeats`/`finds`
        // (Character.adventure), and are allowed to fail on their own: "he is hiding, location unknown" is a
        // genuine state of the game and is far more useful than dropping the whole node. So they are omitted
        // independently, and never published at all when he is not hiding — a stale menu from the last hunt
        // would read as a live instruction.
        public static JSONObject WaldoNode(int? defeats, int? finds, int? menuId, string menuName)
        {
            if (!defeats.HasValue || !finds.HasValue) return null;
            int d = defeats.Value, f = finds.Value;
            if (d < 0 || f < 0) return null;                  // off-contract read; claim nothing

            var o = new JSONObject();
            bool hiding = WaldoHiding(d, f);
            o["hiding"] = hiding;
            o["finds"] = f;
            o["defeats"] = d;
            if (!hiding) return o;

            if (menuId.HasValue && menuId.Value >= 0) o["menu"] = menuId.Value;
            if (menuName != null)
            {
                var t = menuName.Trim();
                if (t.Length > WaldoMenuNameMax) t = t.Substring(0, WaldoMenuNameMax);
                if (t.Length > 0) o["menuName"] = t;
            }
            return o;
        }
    }
}
