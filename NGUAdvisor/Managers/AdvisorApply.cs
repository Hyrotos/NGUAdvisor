using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // Route C3 Phase B: opt-in auto-apply. When a system's toggle is on, the advisor's goal-aware
    // recommendation is applied instead of only being displayed. Runs from Main's 10s loop
    // (main thread), guarded, throttled, and logs every change it makes.
    //
    // Safety rules:
    //  - Master automation (GlobalEnabled) must be on.
    //  - Never acts while LockManager holds a titan/ygg/pit/gold/cooking lock (mode swaps own the sets).
    //  - Diggers/beards: never acts inside a challenge (challenge-tagged profile breakpoints own them);
    //    while enabled, profile digger/beard timelines are substituted with the advisor set.
    //  - Wandoos OS: switching wipes E/M Dump levels, so we only switch when one projected HOUR on the
    //    better OS (from zero levels) still beats 1.5x the bonus you currently have — i.e. the switch
    //    pays for itself within the hour. Throttled to one switch per 10 minutes.
    public static class AdvisorApply
    {
        private static DateTime _lastTick = DateTime.MinValue;
        private static DateTime _lastOsSwitch = DateTime.MinValue;

        // ---- fault containment (stage R2) ----
        //
        // ONE APPLIER'S EXCEPTION USED TO KILL EVERY LATER ONE. All of these ran under a single outer catch,
        // so a throw in ApplyPerks — position five of nineteen — silently skipped EXP buys, titan gold, gold,
        // PIT, quests, blood, boost priority, zones, titans and transforms for that tick. And five of them
        // (Diggers, Beards, Perks, Quirks, Ygg buys) have NO throttle: they run every 30s tick, so a
        // persistent fault in any of them starved the entire tail permanently. The throttled ones were worse
        // to diagnose, not better — their throttle short-circuits on the alternate ticks, so the starvation
        // was INTERMITTENT, and automation that half-works reads like a game bug rather than an exception.
        //
        // The fix is containment, not a framework: catch per step, name the step, keep going. Twelve of the
        // nineteen operations get this. The other seven already catch their own complete bodies and are left
        // strictly alone — wrapping them again would just double-log.
        private sealed class Fault
        {
            public int Consecutive;      // failures in this episode, total
            public int SinceReport;      // failures since we last said anything
            public DateTime FirstAt;
            public DateTime LastAt;      // last EXCEPTION — the quiet window is measured from here
            public DateTime LastReportAt;
            public string Type;
            public string Message;
        }

        private static readonly Dictionary<string, Fault> _faults = new Dictionary<string, Fault>();

        // The step is the rate-limiting boundary, NOT the exception message: messages carry ids, values and
        // object descriptions that change every throw, so keying on them would let a "new" signature flood
        // both logs at 30-second intervals — which is the noise this exists to prevent.
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(10);

        // The quiet-window message states the ACTUAL interval, derived from the constant above rather than
        // typed into the string. A probe build that shortens ReportEvery to two minutes must SAY two minutes;
        // a message that hardcodes "10" would be a lie in exactly the build you are using to check that the
        // messages tell the truth.
        private static string ReportWindowText
        {
            get
            {
                double m = ReportEvery.TotalMinutes;
                return m == 1 ? "1 minute" : $"{m:0} minutes";
            }
        }

        private const string TickStep = "Advisor tick";

        // Runs one applier. The GATE stays outside: a disabled or throttled step never reaches here as a
        // failure, and a step that returns early because it has nothing to do is a SKIP, not a success worth
        // announcing. Silence is the normal case and the only correct one.
        //
        // A NONTHROWING RETURN IS NOT NECESSARILY COMPLETED WORK. This helper sees "the call came back"; it
        // cannot see whether the applier did anything, because every applier's throttle, disabled-feature and
        // nothing-to-do exits are plain `return`s indistinguishable from a full successful run. Everything
        // downstream of here is named for what is actually observable, not for what we would like it to mean.
        private static void RunStep(string name, Action action)
        {
            try { action(); }
            catch (Exception e) { OnStepFailed(name, e); return; }
            ObserveStepReturn(name);
        }

        private static void OnStepFailed(string name, Exception e)
        {
            var now = DateTime.UtcNow;
            Fault f;

            if (!_faults.TryGetValue(name, out f))
            {
                // First failure of a healthy step: say so at once, with the one full stack for this episode.
                f = new Fault
                {
                    Consecutive = 1,
                    SinceReport = 0,
                    FirstAt = now,
                    LastAt = now,
                    LastReportAt = now,
                    Type = e.GetType().Name,
                    Message = e.Message
                };
                _faults[name] = f;
                Main.Log($"Advisor: {name} failed — {f.Type}: {f.Message}");
                Main.LogDebug($"Advisor step {name} failed:\n{e}");
                return;
            }

            f.Consecutive++;
            f.SinceReport++;
            f.LastAt = now;
            f.Type = e.GetType().Name;      // latest signature, for the next periodic report
            f.Message = e.Message;

            if (now - f.LastReportAt < ReportEvery) return;   // suppressed: no session line, no stack

            Main.Log($"Advisor: {name} still failing — {f.SinceReport} failure(s) since the last report; latest {f.Type}: {f.Message}");
            Main.LogDebug($"Advisor step {name} still failing ({f.Consecutive} consecutive since {f.FirstAt:HH:mm:ss}):\n{e}");
            f.LastReportAt = now;
            f.SinceReport = 0;
        }

        // WE DO NOT KNOW THAT IT RECOVERED. We know it stopped throwing. Those are different claims, and this
        // method is careful to make only the second one.
        //
        // Clearing the fault on the first nonthrowing return cannot work, because a THROTTLED SKIP is a
        // nonthrowing return: ApplyExpBuys comes back clean on every tick inside its 60s window without
        // touching the failing path at all. Clear-on-return would therefore read that skip as a recovery,
        // close the episode, and let the NEXT real attempt open a fresh one and report as a first failure —
        // fail, "recovered", fail, "recovered", twice a minute forever, each with a failure count of one. A
        // worse flood than the one this exists to fix, and a lying one.
        //
        // So the episode is measured from the last EXCEPTION, not the first return: it clears once the step
        // has gone a full reporting interval without throwing again. One rule, both problems — a throttled
        // skip cannot forge a clearance (the next failure is at most one throttle away, well inside the
        // window), and a step that has genuinely stopped failing says so once and goes quiet.
        //
        // What the message may claim: no exception for ten minutes. What it must NOT claim: that the applier
        // did any work, that the failing path was exercised at all, or that the defect is fixed. It may have
        // been throttled, disabled mid-episode, or had nothing to do for the whole window. The wording is
        // observational on purpose. The cost — the line can lag a real fix by up to the interval — is the
        // price of never asserting something we cannot see.
        private static void ObserveStepReturn(string name)
        {
            Fault f;
            if (!_faults.TryGetValue(name, out f)) return;          // healthy: silent, as it is ~always
            if (DateTime.UtcNow - f.LastAt < ReportEvery) return;   // episode still open

            _faults.Remove(name);
            Main.Log($"Advisor: {name} fault quiet for {ReportWindowText} after {f.Consecutive} failure(s).");
            Main.LogDebug($"Advisor step {name} fault state cleared after {ReportWindowText} without another exception; "
                        + $"{f.Consecutive} failure(s) observed; latest was {f.Type}: {f.Message}");
        }

        public static void Tick()
        {
            try
            {
                if (Main.Settings == null || !Main.Settings.GlobalEnabled) return;
                if (!CompatibilityGate.ActionsAllowed) return;   // observe-only on an unrecognized game build (P0-3)
                var c = Main.Character;
                if (c == null) return;
                if ((DateTime.UtcNow - _lastTick).TotalSeconds < 30) return;
                _lastTick = DateTime.UtcNow;

                // Challenge overlay first: it sets the gear-objective override the gear refresh
                // below consults (and clears itself outside challenges / when toggled off).
                // Routed through RunStep now (R11): its own outer whole-Tick catch was removed so the
                // bounded reporter owns the fault — nested sub-catches inside it stay.
                RunStep("Challenge overlay", ChallengeOverlay.Tick);
                // Level caps ride the segment the overlay just computed (self-gates on AutoProfile).
                RunStep("Level planner", LevelPlanner.Tick);

                // Watch what the player OWNS, not what they wear: a drop or a merge means the optimizer's
                // answer may have changed, and until now the only thing that ever noticed was the 120s
                // poll inside ApplyGearRefresh. Observing is read-only and costs nothing, so it sits
                // OUTSIDE the lock check — a titan swap holding the lock must not make us miss a drop.
                RunStep("Gear watch", () =>
                {
                    // Nothing downstream could act on a change, so don't even look. Turning the trigger
                    // back on re-primes the baseline on the next tick (Poll's first call never fires),
                    // which is right: a change observed while the feature was off isn't its business,
                    // and the 120s poll still picks it up.
                    if (!Main.Settings.AdvisorGearOnDrop || !Main.Settings.ManageGear || !Main.Settings.AdvisorGearRefresh)
                    {
                        GearWatch.Reset();
                        return;
                    }
                    if (GearWatch.Poll()) GearInventoryChanged();
                });

                // Set/gear appliers must not fight a mode lock's temporary swaps; the purchase and
                // routing appliers below touch nothing a lock owns, so they keep running during locks
                // (audit fix: previously a titan wait stalled perk/EXP/blood automation for no reason).
                //
                // CanSwap() is evaluated ONCE, exactly where it always was — the containment goes around each
                // applier, never around the policy that decides whether it runs, and never re-checks the lock
                // between them.
                if (LockManager.CanSwap())
                {
                    if (Main.Settings.AdvisorDiggers) RunStep("Diggers", () => ApplyDiggers(c));
                    if (Main.Settings.AdvisorBeards) RunStep("Beards", () => ApplyBeards(c));
                    if (Main.Settings.AdvisorWandoosOS) RunStep("Wandoos OS", () => ApplyWandoosOs(c));
                    if (Main.Settings.AdvisorGearRefresh) RunStep("Gear refresh", ApplyGearRefresh);
                }

                if (Main.Settings.AdvisorPerks) RunStep("Perks", ApplyPerks);
                if (Main.Settings.AdvisorQuirks) RunStep("Quirks", ApplyQuirks);
                if (Main.Settings.AdvisorYggBuys) RunStep("Ygg buys", ApplyYggBuys);
                if (Main.Settings.AdvisorExpBuys) RunStep("EXP buys", ApplyExpBuys);
                // ApplyTitanGold's inner try guards only the version lookup — the body, including two
                // persisted writes, was exposed. The whole call is contained now.
                if (Main.Settings.AutoTitanGold || Main.Settings.AdvisorGold) RunStep("Titan gold", ApplyTitanGold);
                if (Main.Settings.AdvisorGold || Main.Settings.SnipeOnGoldStarved) ApplyGold();      // gated: reports via OnStepFailed
                if (Main.Settings.AdvisorPit) ApplyPit();                                            // gated: reports via OnStepFailed
                if (Main.Settings.AdvisorQuests) ApplyQuests();                                      // gated: reports via OnStepFailed
                if (Main.Settings.AdvisorBlood) RunStep("Blood", ApplyBlood);
                if (Main.Settings.AutoBoostPriority) RunStep("Boost priority", ApplyBoostPriority);
                // Gear Hunt routes the zone even in MANUAL ZONE mode — the toggle is the intent.
                if (Main.Settings.AdvisorZones || GearHunter.Active) RunStep("Zones", ApplyZones);
                if (Main.Settings.AdvisorTitans) ApplyTitans();                                      // gated: reports via OnStepFailed
                RunStep("Transforms", TransformManager.Tick);

                ObserveStepReturn(TickStep);
            }
            // FINAL SAFETY BOUNDARY, and now a bounded one. Nothing a RunStep caught can reach here, so this
            // only fires for orchestration itself — a gate getter, CanSwap(), the helper. That is a more
            // serious fault than any single applier (the whole tick died), so it is session-visible, and it
            // goes through the same per-step interval rather than writing a stack to debug.log every 30s.
            catch (Exception e) { OnStepFailed(TickStep, e); }
        }

        // Advisor-driven boost priority (Boosts tab, ADVISOR ACTIVE): recompute the ranked list every
        // 10 minutes (it runs the full objective sweep) and write it into the existing priority-boost
        // pipeline. Manual mode leaves Settings.PriorityBoosts entirely alone.
        private static DateTime _lastBoostPrio = DateTime.MinValue;

        private static void ApplyBoostPriority()
        {
            if (!Main.Settings.ManageInventory) return;
            if ((DateTime.UtcNow - _lastBoostPrio).TotalMinutes < 10) return;
            _lastBoostPrio = DateTime.UtcNow;

            var v = InventoryAdvisor.Compute();
            var ids = InventoryAdvisor.AutoBoostPriority(v);
            var cur = Main.Settings.PriorityBoosts ?? new int[0];
            if (!ids.SequenceEqual(cur))
            {
                Main.Settings.PriorityBoosts = ids;
                Main.Log($"Advisor: boost priority -> {(ids.Length > 0 ? string.Join(", ", ids.Select(x => x.ToString()).ToArray()) : "(equipped only)")}");
            }
        }

        private static void ApplyDiggers(Character c)
        {
            // AUTOMATION ANDed with DECISIONS, same contract as ApplyBeards and the six appliers that
            // already open this way. The caller tests AdvisorDiggers (:201); this is the other half.
            // Without it, AUTOMATION OFF + DECISIONS ADVISOR kept reconciling membership and levelling
            // every tick while the panel said the tool was not operating diggers.
            //
            // ManageDiggers is the whole of it and there is no double-gate: UpgradeDiggers is a
            // SEPARATE, narrower field covering the buy-the-next-tier path, and it already self-gates
            // at DiggerManager.cs:378/:405 on UpdateCheapestDigger/UpgradeCheapestDigger — neither of
            // which this method calls. ReconcileAdvisorDiggers and RecapDiggers have no gate of their
            // own (RecapDiggers reads only Settings.DiggerCap, a tuning value, not a permission).
            if (!Main.Settings.ManageDiggers) return;

            // The digger path reconciles membership in place and only records through EquipDiggers,
            // so a run where the set never changes never re-records at all. Same affirmation as the
            // beard and ITOPOD paths above.
            try { WriteLedger.Reaffirm("diggers.active", ChallengeOverlay.Segment); } catch { }

            var set = OptimizationAdvisor.CurrentDiggerSet();
            // ⚠ NULL AND EMPTY ARE FOLDED HERE, the shape that made the 100LC beard rule unenforceable
            // (see BeardRule). Harmless today because no digger rule needs "equip none" — if one ever
            // does, this needs the same tri-state split rather than a new flag.
            if (set == null || set.Length == 0) return;

            // Converge membership in place (obsolete off, affordable-missing on, correct left alone), then
            // ALWAYS re-level whatever ended up active. Leveling must NOT be gated on the full
            // recommendation activating: a recommended digger that can't afford even level 1 (the Evil
            // Blood digger, base drain ~1e24 >> gross ~5e21) can NEVER activate, so the old "recap only on
            // a complete set" gate never opened and froze the whole set at level 1 the entire run (user-
            // caught on the Evil climb; the diagnostic showed d10 unaffordable, set never completed, so
            // RecapDiggers never ran — except the rare titan-window tick that displaced d10 with d0).
            // ReconcileAdvisorDiggers activates every affordable member at level 1 BEFORE we level, so
            // leveling can't shut out a member that could have run; the only members left off are ones that
            // couldn't afford level 1 regardless. Recap every pass so levels also track gross as it climbs.
            bool complete = DiggerManager.ReconcileAdvisorDiggers(set, out bool membershipChanged);
            // Pass the recommendation order explicitly: reconcile is membership-only and never updates
            // _curDiggers, so the parameterless RecapDiggers would level the greedy budget in a stale/null
            // order and discard the ranking (Adventure-leads / Stats-on-push / DC-on-titan). With `set`,
            // the greedy allocation levels high-priority diggers first — critical on a tight Evil budget.
            if (c.diggers.activeDiggers.Count > 0)
                DiggerManager.RecapDiggers(set);
            // Log only a real, complete equip (membership actually changed AND the whole set is live) —
            // the incomplete-but-leveling passes stay quiet so an unaffordable member can't spam the log.
            if (complete && membershipChanged)
                Main.Log($"Advisor: equipped diggers {string.Join(", ", set.Select(i => i.ToString()).ToArray())}");
        }

        private static void ApplyBeards(Character c)
        {
            // AUTOMATION ANDed with DECISIONS, which is the model's whole contract: AUTOMATION
            // (ManageBeards) is "may the tool operate this system at all", DECISIONS (AdvisorBeards,
            // tested by the caller at :202) is "who decides what it does". Six appliers already open
            // this way — ApplyGearRefresh:1367, ApplyBoostPriority:240, ApplyBlood:338,
            // ApplyTitanGold:409, ApplyTitans:552, ApplyZones:1051.
            //
            // ⚠ THIS ONE DID NOT, so AUTOMATION OFF + DECISIONS ADVISOR still equipped beards every
            // tick — the panel says the tool is not operating beards while the tool operates beards.
            // Same defect the money pit is already recorded as having (ApplyPit has no AutoMoneyPit
            // gate); ApplyDiggers, ApplyWandoosOs and ApplyQuests share it and are NOT touched here.
            if (!Main.Settings.ManageBeards) return;

            // TRI-STATE, and the empty case is the one that had no handling. null = no opinion, leave
            // the equipped set alone. Empty = a positive "wear none" (BeardRule.None, the 100LC rule) —
            // which must CLEAR, because abstaining leaves the beards equipped before the challenge began
            // still on for its whole duration. Folding the two together under `set.Length == 0` is what
            // let seven beards ride through the 100 Level Challenge.
            var set = OptimizationAdvisor.CurrentBeardSet();
            if (set == null) return;
            var active = c.beards.activeBeards;
            // Vacuously true for the empty set once the clear has landed, so the rule states itself
            // once per challenge instead of once per tick.
            if (active.Count == set.Length && set.All(active.Contains))
            {
                // Same set, freshly re-decided under whatever segment is live now. Tell the ledger,
                // or the row it wrote under the PREVIOUS segment stays marked stale for the rest of
                // the run while the advisor keeps choosing it (audit, 2026-08-19).
                try { WriteLedger.Reaffirm("beards.active", ChallengeOverlay.Segment); } catch { }
                return;
            }

            if (BeardManager.EquipBeards(set))
                Main.Log(set.Length == 0
                    ? "Advisor: cleared beards — the 100 Level Challenge is run without them; the game does not enforce it ([DECOMP] only TrollChallengeController.cs:650 sets beards.disabled), so the advisor honours the stated rule"
                    : $"Advisor: equipped beards {string.Join(", ", set.Select(i => i.ToString()).ToArray())}");
        }

        // Guide-ordered spending (SpendPlanner): buy the next perk/quirk/fruit-tier in the guide's
        // chapter order whenever points/seeds cover it. Bounded per tick; every purchase is logged.
        private static void ApplyPerks()
        {
            int n = SpendPlanner.BuyPerks(50);
            if (n > 0)
            {
                var next = SpendPlanner.NextPerk();
                Main.Log($"Advisor: bought {n} perk level(s) toward the guide order{(next.Known ? $"; next: {next.Name}" : "")}");
            }
        }

        private static void ApplyQuirks()
        {
            int n = SpendPlanner.BuyQuirks(50);
            if (n > 0)
            {
                var next = SpendPlanner.NextQuirk();
                Main.Log($"Advisor: bought {n} quirk level(s) toward the guide order{(next.Known ? $"; next: {next.Name}" : "")}");
            }
        }

        private static void ApplyYggBuys()
        {
            var b = SpendPlanner.NextFruit();
            if (b.Known && b.Affordable && SpendPlanner.BuyFruitTier())
                Main.Log($"Advisor: bought {b.Name} tier {b.CurLevel + 1} for {b.Cost} seeds (guide order)");
        }

        // Blood planner auto: cast Iron Pill at the breakpoint-optimal moment (BloodPlanner decides;
        // the threshold path in CastBloodSpells is disabled for the pill while this is on).
        private static DateTime _lastBloodCheck = DateTime.MinValue;
        private static string _lastRouteReason;

        private static void ApplyBlood()
        {
            if (!Main.Settings.CastBloodSpells) return;
            if ((DateTime.UtcNow - _lastBloodCheck).TotalSeconds < 60) return;
            _lastBloodCheck = DateTime.UtcNow;

            var plan = BloodPlanner.Analyze();
            if (plan.Known && plan.CastIronNow)
                BloodMagicManager.ironPill.CastPlanned();

            // Route the investment auto-spells (the game splits blood evenly among enabled toggles;
            // pooling turns them all off so the Iron Pill can actually charge).
            BloodPlanner.FillRouting(ref plan);
            if (plan.RouteKnown)
            {
                var bm = Main.Character.bloodMagic;
                bool r = !plan.PoolForPill && plan.WantRebirth;
                bool l = !plan.PoolForPill && plan.WantLoot;
                bool g = !plan.PoolForPill && plan.WantGold;
                bool changed = bm.rebirthAutoSpell != r || bm.lootAutoSpell != l || bm.goldAutoSpell != g;
                if (changed)
                {
                    bm.rebirthAutoSpell = r;
                    bm.lootAutoSpell = l;
                    bm.goldAutoSpell = g;
                }

                // ⚠ THIS PATH RUNS EVERY 60s AND Main.QuickStuff WRITES THE SAME THREE TOGGLES EVERY
                // 0.5s, from its own thresholds, with nothing arbitrating between them. The fast one
                // therefore wins roughly 120 : 1, which makes this one very nearly decorative whenever
                // they disagree. Recorded unconditionally rather than inside `changed` for exactly that
                // reason: the ledger's job here is to show that a second writer exists, and a writer
                // that is being overwritten a hundred times a minute never sees `changed` go true.
                var onNames = new List<string>();
                if (r) onNames.Add("Rebirth");
                if (l) onNames.Add("Loot");
                if (g) onNames.Add("Gold");
                WriteLedger.Record("blood.spells.advisor",
                    onNames.Count > 0 ? string.Join(" + ", onNames.ToArray()) : "all off",
                    "advisor blood routing for this run phase",
                    ChallengeOverlay.Segment,
                    "Recomputed from scratch once every 60 seconds",
                    "Main.QuickStuff writes the same three toggles every 0.5 seconds",
                    "If the two ever disagree, the 0.5s path is the one you are actually running");
                // Log on a toggle change OR whenever the REASON changes. Logging only on change made
                // the routing invisible whenever the advisor booted into an already-correct toggle
                // state — the common case after a reload — so "working" and "never ran" looked the
                // same in inject.log.
                string reason = plan.PoolForPill ? "pooling for Iron Pill (all auto-spells off)" : plan.RouteReason;
                if (changed || reason != _lastRouteReason)
                {
                    _lastRouteReason = reason;
                    Main.Log($"Advisor: blood routing -> {reason}{(changed ? "" : " (toggles already correct)")}");
                }
            }
        }

        // Data-driven titan gold: target the HIGHEST autokill-able titan for the next gold bank (its
        // drop dwarfs all lower titans, so only it matters), and re-bank when its AK version rises.
        // Replaces hand-picking TitanGoldTargets checkboxes; the existing snapshot/lock machinery does
        // the actual gold-gear swap on the AK cycle.
        private static DateTime _lastTitanGold = DateTime.MinValue;

        // Cached: AutokillAvailable for titans 6+ goes through reflection, and this is consulted by
        // the advisor's Power and Gold rows (2s cadence) as well as the titan-gold applier. AK status
        // changes on the scale of minutes, so 30s staleness is free performance.
        private static int _akTitan = -1;
        private static DateTime _akTitanAt = DateTime.MinValue;

        public static int HighestAkTitan()
        {
            if ((DateTime.UtcNow - _akTitanAt).TotalSeconds < 30) return _akTitan;
            _akTitanAt = DateTime.UtcNow;
            int best = -1;
            for (int i = 0; i < ZoneHelpers.TitanZones.Length; i++)
            {
                // RiddleLocked as well as AutokillAvailable: a locked titan never spawns, so boss{N}Spawn
                // saturates at its spawn time and stays there. That makes IsTitanSpawningSoon permanently
                // true, which pins the gold loadout on indefinitely -- the 10-minute stall valve clears the
                // target and this method re-arms it 60s later, so the run sits in gold gear at ~90% duty
                // cycle and never banks anything. Picking the next titan DOWN is strictly better: it can
                // actually be killed. The swap path below already guarded 6/7/8; this path guarded nothing.
                try { if (ZoneHelpers.AutokillAvailable(i) && !ZoneHelpers.RiddleLocked(i)) best = i; }
                catch { }
            }
            _akTitan = best;
            return best;
        }

        private static void ApplyTitanGold()
        {
            if (!Main.Settings.ManageGoldLoadouts) return;
            if ((DateTime.UtcNow - _lastTitanGold).TotalSeconds < 60) return;
            _lastTitanGold = DateTime.UtcNow;

            int best = HighestAkTitan();
            if (best < 0) return;
            int ver = 1;
            try { ver = ZoneHelpers.TitanVersion(best); } catch { }

            var done = Main.Settings.TitanMoneyDone;
            var banked = Main.Settings.TitanGoldVersionBanked;
            if (done != null && best < done.Length && done[best]
                && banked != null && best < banked.Length && banked[best] > 0 && banked[best] < ver)
            {
                done[best] = false;
                Main.Settings.TitanMoneyDone = done;
                Main.Log($"Advisor: Titan {best + 1} AK version rose to v{ver} — re-banking gold on the next kill");
            }

            var targets = new bool[ZoneHelpers.TitanZones.Length];
            targets[best] = done == null || best >= done.Length || !done[best];
            var cur = Main.Settings.TitanGoldTargets;
            bool differs = cur == null || cur.Length != targets.Length;
            if (!differs)
                for (int i = 0; i < targets.Length; i++)
                    if (cur[i] != targets[i]) { differs = true; break; }
            if (differs)
            {
                Main.Settings.TitanGoldTargets = targets;
                if (targets[best])
                    Main.Log($"Advisor: targeting Titan {best + 1} (v{ver}) for the next gold bank");
            }
        }

        // Advisor gold (E1 pipeline): auto-CBlock while a challenge is active (challenge runs live on
        // zone sniping), and the gold-starvation snipe trigger (throttled — it re-runs the snipe when
        // augments can't be afforded despite TM holding gold).
        private static DateTime _lastGoldCheck = DateTime.MinValue;

        private static void ApplyGold()
        {
            if ((DateTime.UtcNow - _lastGoldCheck).TotalMinutes < 2) return;
            _lastGoldCheck = DateTime.UtcNow;

            try
            {
                var c = Main.Character;
                if (Main.Settings.AdvisorGold)
                {
                    string challenge = null;
                    try { challenge = ChallengeDetector.Current(); } catch { }
                    bool wantCBlock = challenge != null;
                    if (Main.Settings.GoldCBlockMode != wantCBlock && !Main.Settings.MoneyPitRunMode)
                    {
                        Main.Settings.GoldCBlockMode = wantCBlock;
                        Main.Log($"Advisor: gold snipe mode -> {(wantCBlock ? $"challenge ({challenge})" : "normal")}");
                    }
                }

                // Starvation trigger: advisor always; manual mode via its S3 toggle.
                if (!Main.Settings.AdvisorGold && !Main.Settings.SnipeOnGoldStarved) return;
                if (c.machine.realBaseGold > 0 && Main.Settings.GoldSnipeComplete
                    && OptimizationAdvisor.GoldStarvedForAugs(c, 1.0))
                {
                    Main.Settings.GoldSnipeComplete = false;
                    Main.LastSnipeTrigger = "gold starvation";
                    Main.Log("Re-snipe: gold starvation (augments unaffordable)");
                }
            }
            catch (Exception ex) { OnStepFailed("Gold", ex); return; }

            // Reached only after the throttle gate passed and the body ran without throwing — the
            // 2-minute gate above is the eligibility check, so getting here is a real exercise.
            ObserveStepReturn("Gold");
        }

        // Advisor quest strategy: majors whenever banked, bank-overfill guard on, abandon minors
        // under 30%, butter majors only, minors idle. AutoQuest itself stays the user's master
        // switch; the 50-item rule follows the perk that enables it. Applied once, logged once.
        private static DateTime _lastQuestCheck = DateTime.MinValue;

        private static void ApplyQuests()
        {
            if ((DateTime.UtcNow - _lastQuestCheck).TotalSeconds < 60) return;
            _lastQuestCheck = DateTime.UtcNow;

            try
            {
                var s = Main.Settings;
                if (!s.AutoQuest) return;
                var changed = new List<string>();
                if (!s.AllowMajorQuests) { s.AllowMajorQuests = true; changed.Add("majors on"); }
                if (!s.QuestsFullBank) { s.QuestsFullBank = true; changed.Add("bank guard on"); }
                if (s.ManualMinors) { s.ManualMinors = false; changed.Add("minors idle"); }
                if (!s.AbandonMinors) { s.AbandonMinors = true; changed.Add("abandon minors"); }
                if (s.MinorAbandonThreshold != 30) { s.MinorAbandonThreshold = 30; changed.Add("abandon <30%"); }
                if (!s.UseButterMajor) { s.UseButterMajor = true; changed.Add("butter majors"); }
                if (s.UseButterMinor) { s.UseButterMinor = false; changed.Add("no minor butter"); }
                bool fifty = false;
                try { fifty = Main.Character.adventure.itopod.perkLevel[94] >= 610; } catch { }
                if (s.FiftyItemMinors != fifty) { s.FiftyItemMinors = fifty; changed.Add(fifty ? "50-item minors" : "54-item minors"); }
                if (changed.Count > 0)
                    Main.Log($"Advisor: quest strategy -> {string.Join(", ", changed.ToArray())}");
            }
            catch (Exception ex) { OnStepFailed("Quests", ex); return; }

            // Reached only when AutoQuest was on and the strategy pass completed without throwing. The
            // !AutoQuest return inside the try exits the method before here, so a disabled invocation
            // never counts as a successful exercise.
            ObserveStepReturn("Quests");
        }

        // Advisor Money Pit: act on the shared plan (tier-ETA + safety gates in MoneyPitManager).
        private static DateTime _lastPitCheck = DateTime.MinValue;

        private static void ApplyPit()
        {
            if ((DateTime.UtcNow - _lastPitCheck).TotalSeconds < 60) return;
            _lastPitCheck = DateTime.UtcNow;

            try
            {
                var plan = MoneyPitManager.AdvisorPlan();
                if (plan.Throw)
                {
                    Main.Log($"Advisor: money pit -> {plan.Verdict} (predicted: {MoneyPitManager.PredictNext()})");
                    MoneyPitManager.AdvisorThrow();
                }
            }
            catch (Exception ex) { OnStepFailed("Pit", ex); return; }

            // Reached after the 60s gate passed and AdvisorPlan (plus any throw) ran without throwing.
            // The "Pit" key is automatic-only — the manual Throw Now path (R10) reports via Activity and
            // never touches this fault state.
            ObserveStepReturn("Pit");
        }

        // Advisor titan targeting (Titans hero card): target every reachable titan below auto-kill —
        // riddle titans (6/7/8) only once their quest flags unlock. Drops targets the moment AK lands.
        private static DateTime _lastTitanTargets = DateTime.MinValue;

        // The chase decision's memory, so it can be hysteretic rather than re-derived cold every pass.
        // Keyed to "index:version" — a new objective has to clear the full bar on its own merits, so the
        // deadband can never carry a commitment across to a titan it was never made about.
        private static string _chaseKey;
        private static bool _chasing;

        // Would the best projected kill set win a manual fight against this titan/version? A titan the
        // tables hold no row for (Tippi, Traitor) cannot be judged, so it stays queued as it always was.
        private static bool TitanAttemptReady(Character c, int titanIndex, int version)
        {
            if (TitanTables.ManualRow(titanIndex, version) == null) return true;
            try
            {
                OptimizationAdvisor.ProjectedBestGear(out var am, out var dm);
                return TitanTables.ManualFightReady(titanIndex, version,
                    c.totalAdvAttack() * am, c.totalAdvDefense() * dm);
            }
            catch { return false; }
        }

        private static void ApplyTitans()
        {
            if (!Main.Settings.ManageTitans) return;
            if ((DateTime.UtcNow - _lastTitanTargets).TotalSeconds < 60) return;
            _lastTitanTargets = DateTime.UtcNow;

            try
            {
                var c = Main.Character;

                // During any challenge, below-AK titans are unviable (reduced stats, constant resets);
                // AK'd titans die automatically regardless. Clear targets and stand down.
                string challenge = null;
                try { challenge = ChallengeDetector.Current(); } catch { }
                if (challenge != null)
                {
                    var curT = Main.Settings.TitanSwapTargets;
                    if (curT != null && curT.Any(x => x))
                    {
                        Main.Settings.TitanSwapTargets = new bool[14];
                        Main.Log($"Advisor: challenge active ({challenge}) — titan targeting paused (only AK'd titans viable)");
                        ChallengeOverlay.Record("TITAN", "titan targeting paused", "challenge stats can't push AK");
                    }
                    return;
                }

                // Objective + attempt-readiness FIRST — both the target list and the spawn version
                // depend on it. A "first kill" objective is only ATTEMPTED once the projected
                // best-gear stats actually cover the staged manual requirement (user-reported: the
                // advisor chased a freshly-AK'd titan's next version nowhere near a manual attempt —
                // wasted fights, and the spawn was parked off the AK version that pays gold/drops).
                var objv = OptimizationAdvisor.NextObjective();
                int primary = objv.Known ? objv.Index : -1;
                bool attemptReady = true;
                if (objv.Known && objv.Stage == "first kill")
                {
                    // HYSTERETIC, and the state is keyed to the objective it belongs to. A bare threshold
                    // here alternated across the line every 60s pass; each flip to "not ready" parked the
                    // spawn on the AK version, which drops adventure routing mid-window and gives the game
                    // a free lower-version kill in place of the attempt just committed to. See
                    // TitanTables.ChaseReady for why the band is asymmetric.
                    string key = objv.Index + ":" + objv.Version;
                    if (_chaseKey != key) { _chaseKey = key; _chasing = false; }   // new objective, prove it again
                    try
                    {
                        OptimizationAdvisor.ProjectedBestGear(out var am, out var dm);
                        double aR = objv.ReqAttack  > 0 ? c.totalAdvAttack()  * am / objv.ReqAttack  : double.MaxValue;
                        double dR = objv.ReqDefense > 0 ? c.totalAdvDefense() * dm / objv.ReqDefense : double.MaxValue;
                        attemptReady = TitanTables.ChaseReady(_chasing, Math.Min(aR, dR));
                    }
                    catch { attemptReady = false; }
                    if (_chasing != attemptReady)
                        Main.Log($"Advisor: titan chase -> {(attemptReady ? "committed" : "abandoned")} " +
                                 $"T{objv.Index + 1} v{objv.Version} (best-gear projection crossed the " +
                                 $"{(attemptReady ? TitanTables.ChaseCommitRatio : TitanTables.ChaseAbandonRatio):0.00}x band)");
                    _chasing = attemptReady;
                }
                else { _chaseKey = null; _chasing = false; }

                int maxZone = ZoneHelpers.GetMaxReachableZone(true);
                var targets = new bool[14];
                for (int i = 0; i < ZoneHelpers.TitanZones.Length && i < 14; i++)
                {
                    if (ZoneHelpers.TitanZones[i] > maxZone) continue;
                    // Was an inline 6/7/8 chain that silently omitted titan9 (Exile), so a locked Exile stayed
                    // a swap target. Shared helper now, so the two paths can't drift apart again.
                    if (ZoneHelpers.RiddleLocked(i)) continue;
                    bool ak = false;
                    try { ak = ZoneHelpers.AutokillAvailable(i); } catch { }
                    if (ak) continue;
                    // The objective answers for itself: the chase decision above, applied just below.
                    // Every OTHER below-AK titan is queued only if the best projected kill set clears
                    // its MANUAL requirement (not the staged one — see TitanTables.ManualFightReady).
                    // Checking only the primary objective let every later, merely-unlocked titan
                    // through regardless of stats (e.g. Grand Corrupted Tree entered the queue while
                    // T1 was still the only reachable manual fight).
                    targets[i] = i == primary || TitanAttemptReady(c, i, ZoneHelpers.TitanVersion(i));
                }
                // Not ready for the first-kill attempt: don't attend its spawns in kill gear at all.
                // (The version parking below keeps the AK-able version spawning for gold/drops.)
                if (!attemptReady && primary >= 0 && primary < targets.Length)
                    targets[primary] = false;

                var cur = Main.Settings.TitanSwapTargets ?? new bool[14];
                bool differs = cur.Length != targets.Length;
                if (!differs)
                    for (int i = 0; i < targets.Length; i++)
                        if (cur[i] != targets[i]) { differs = true; break; }
                if (differs)
                {
                    Main.Settings.TitanSwapTargets = targets;
                    var names = new List<string>();
                    for (int i = 0; i < targets.Length; i++)
                        if (targets[i])
                            names.Add(ZoneHelpers.ZoneList.TryGetValue(ZoneHelpers.TitanZones[i], out var n) ? n : $"Titan {i + 1}");
                    Main.Log($"Advisor: titan targets -> {(names.Count > 0 ? string.Join(", ", names.ToArray()) : "(none — no fight-ready titan below auto-kill)")}");
                }

                // The advisor owns titan killing: the kill-gear swap master must be on or the
                // snapshot machinery never equips the P/T set (user-reported death loop).
                if (!Main.Settings.SwapTitanLoadouts)
                {
                    Main.Settings.SwapTitanLoadouts = true;
                    Main.Log("Advisor: titan kill-gear swaps enabled (advisor manages titans)");
                }

                // Force the objective titan's SPAWN version to the version being chased — spawn
                // version is user-selected and never auto-advances (user: AK'd v1, 22 kills of v2,
                // spawn still parked on the wrong version blocks AK progress).
                // EXCEPTION (user-reported death loop): while a gold bank is pending on this titan,
                // the gold swap needs the AK-able spawn version (the kill is free in gold gear) —
                // forcing v2 turned that into a real fight fought in drop gear. Bank first, then push.
                if (primary >= 5 && primary <= 11)
                {
                    bool goldPending = false;
                    try
                    {
                        var gt = Main.Settings.TitanGoldTargets;
                        var md = Main.Settings.TitanMoneyDone;
                        goldPending = Main.Settings.ManageGoldLoadouts
                            && gt != null && primary < gt.Length && gt[primary]
                            && (md == null || primary >= md.Length || !md[primary]);
                    }
                    catch { }
                    try
                    {
                        int spawn = ZoneHelpers.TitanVersion(primary);
                        if (goldPending || !attemptReady)
                        {
                            // Park the spawn on the highest AK-able version: while a gold bank is
                            // pending it completes there for free, and while the next version's
                            // first-kill stats are out of reach even in best gear, the AK version
                            // keeps paying gold/drops instead of feeding doomed attempts.
                            // <= objv.Version, NOT <. The old strict bound could never select the objective's
                            // own version even when it auto-kills, so a v4 objective with v4 AK available got
                            // parked on v3 -- the advisor logging "targeting Titan 6 (v4) for the next gold
                            // bank" and "titan spawn version -> v3" on adjacent lines, and undoing a manual
                            // v4 selection every minute. The bound was trying to say "don't park on a version
                            // you can't auto-kill", but AutokillAvailable already tests exactly that, so it
                            // was redundant when the version qualifies and harmful when it does. Parking on
                            // the highest AK-able version is strictly better: the kill is still free and the
                            // gold and drops scale with the version.
                            int akVer = 0;
                            for (int vv = 1; vv <= objv.Version; vv++)
                                try { if (ZoneHelpers.AutokillAvailable(primary, vv)) akVer = vv; } catch { }

                            // AutokillAvailable is a RECORD, not a capability test: [DECOMP]
                            // autokillTitan{N}V{v}Achieved is only true for versions already auto-killed.
                            // Walking down on it alone skips a version that can be BEATEN but never has
                            // been — and that closes a loop with no exit, because achieving v2's AK
                            // requires fighting v2, which parking on v1 prevents. Field symptom: "ready
                            // for T7 v2 but killing T7 v1" with 2.4x headroom on the v2 floor.
                            //
                            // So ask whether the version is killable instead of whether it was killed:
                            // convert its staged requirement to a gear floor and let the solver answer.
                            //
                            // NOT under goldPending. There the kill has to be FREE — the gold swap fights
                            // in drop gear, and turning that into a real fight is the death loop this
                            // branch's exception was added to stop. Killable is not free; only an AK is.
                            int parkVer = akVer;
                            if (!goldPending)
                            {
                                string killWhy;
                                int killable = TitanFloorPlanner.HighestKillable(primary, objv.Version, out killWhy);
                                // 0 means "could not determine" and must leave the AK answer standing —
                                // reading it as "nothing is killable" would park at the bottom of the ladder.
                                if (killable > parkVer) { parkVer = killable; Main.LogDebug($"Titan step-down: {killWhy}"); }
                            }

                            if (parkVer > 0 && spawn != parkVer)
                            {
                                ZoneHelpers.SetTitanVersion(primary, parkVer);
                                if (goldPending)
                                {
                                    Main.Log($"Advisor: titan spawn version -> v{parkVer} (gold bank pending — free AK kill first)");
                                    ChallengeOverlay.Record("TITAN", $"titan version → v{parkVer}", "gold bank pending — banking before the push");
                                }
                                else if (parkVer > akVer)
                                {
                                    Main.Log($"Advisor: titan spawn version -> v{parkVer} (v{objv.Version} first-kill stats out of reach, but v{parkVer} is killable in best gear — fighting it beats farming v{akVer})");
                                    ChallengeOverlay.Record("TITAN", $"titan version → v{parkVer}", $"v{objv.Version} is out of reach; v{parkVer} is winnable, so it is worth fighting");
                                }
                                else
                                {
                                    Main.Log($"Advisor: titan spawn version -> v{parkVer} (v{objv.Version} first-kill stats out of reach — farming the AK version meanwhile)");
                                    ChallengeOverlay.Record("TITAN", $"titan version → v{parkVer}", $"v{objv.Version} first kill needs {objv.ReqAttack:0.#e0} atk — not there yet even in best gear");
                                }
                            }
                        }
                        else if (spawn != objv.Version)
                        {
                            ZoneHelpers.SetTitanVersion(primary, objv.Version);
                            Main.Log($"Advisor: titan spawn version -> v{objv.Version} (chasing its {objv.Stage})");
                            ChallengeOverlay.Record("TITAN", $"titan version → v{objv.Version}", $"objective is v{objv.Version} {objv.Stage}");
                        }
                    }
                    catch (Exception ex) { Main.LogDebug($"Titan version set: {ex.Message}"); }
                }

                if (primary >= 0 && targets.Length > primary && targets[primary])
                {
                    double reqA = objv.ReqAttack, reqD = objv.ReqDefense;
                    double atk = c.totalAdvAttack();
                    double def = c.totalAdvDefense();
                    // Posture from the kill ladder, FIELD-CALIBRATED (user cleared the v2 fight only
                    // on Defensive — Offensive at half the defense requirement was still too greedy):
                    //   Defensive  — the default for every real fight; block/dodge wins marginal ones
                    //   Offensive  — both stats fully cover the stage requirement
                    //   Idle       — auto-kill stage only (the fight is trivially won)
                    int mode;
                    if (objv.Stage == "auto-kill" && atk >= reqA && def >= reqD) mode = 0;
                    else if (reqA > 0 && reqD > 0 && atk >= reqA && def >= reqD) mode = 3;
                    else mode = 2;
                    // Beast cuts defense for damage: only past 1.25x the stage bar on a proven kill.
                    bool beast = reqD > 0 && def / reqD >= 1.25 && objv.Stage != "first kill";
                    if (Main.Settings.TitanCombatMode != mode || Main.Settings.TitanBeastMode != beast)
                    {
                        Main.Settings.TitanCombatMode = mode;
                        Main.Settings.TitanBeastMode = beast;
                        string[] modes = { "Idle", "Snipe", "Defensive", "Offensive" };
                        Main.Log($"Advisor: titan combat -> {modes[mode]}, beast {(beast ? "on" : "off")}");
                    }
                }
            }
            catch (Exception ex) { OnStepFailed("Titans", ex); return; }

            // Reached after the full targeting pass ran without throwing. The challenge-active stand-down
            // returns from inside the try (titan targeting is paused, not exercised), so it does not clear
            // the fault — health is observed only when the real targeting logic completed.
            ObserveStepReturn("Titans");
        }

        // Advisor zone routing (Adventure > ZONES, ADVISOR ACTIVE): point the farm zone at the best
        // boost-farm location. Deliberately NOT active while CBlock/pit-run gold logic owns zones —
        // those modes drive SnipeZone dynamically and must win.
        private static DateTime _lastZoneCheck = DateTime.MinValue;

        // The gear-farm challenge pause's surfacing latch: the challenge code currently holding the
        // farm, or null while it runs. GearFarmPause owns the decision; this is the state it needs.
        // Statics wipe on payload reload, which re-announces once — the correct side to fail on.
        private static string _gearFarmPauseSignature;

        // The three-phase machine's surfacing latch: the phase+zone last announced. The DECISION is
        // stateless by construction (ZonePhase is a pure classifier — a phase must not stay entered
        // after its condition lifts); this is the one piece of state, and it exists only so a
        // transition is announced once instead of every pass. Statics wipe on payload reload, which
        // re-announces once — the correct side to fail on.
        private static string _zonePhaseSignature;

        // The gear farm's own verdict text, latched so its state changes surface once each.
        private static string _gearFarmTextSignature;

        // The rare-farm target, latched on item+zone so a change of target surfaces once.
        private static string _rareFarmSignature;

        // The set-farm target, latched on zone.
        private static string _setFarmSignature;

        // The "rares are available" offer, latched on count+target so it states the position once and
        // again only when the position actually changes.
        private static string _raresOfferSignature;

        // THE HYSTERESIS INSTRUMENT'S STATE — audit/41 §6, RouteChurn. Statics wipe on payload
        // reload, so the first route after a reload records as "first route since load" rather than
        // as a change with a fabricated elapsed time. That is the correct side to fail on: this
        // instrument's whole output is the elapsed field, and a made-up one is worse than a missing
        // one. Not readonly-by-accident — RouteChurn.State is a class precisely so Observe cannot be
        // handed a copy and lose the history.
        private static readonly RouteChurn.State _routeChurn = new RouteChurn.State();

        // ⚠ IT MEASURES, IT NEVER DECIDES. Called from each routing site AFTER that site has already
        // chosen, with a value copy of the decision. Nothing it computes is read back by any routing
        // path, and a RouteChurn.Route has no way to reach Settings.SnipeZone — so "the instrument
        // does not change routing" is a property of the call shape, not a promise. 41 §6 records
        // hysteresis as a RISK nobody has observed; building the margin now would be fitting a
        // constant to no data (audit/35). This builds the thing that would size it.
        //
        // ⚠ AND IT IS NOT STATE-CHANGE THROTTLED. Every other surfacing latch in this file suppresses
        // until a signature moves, because there the change IS the news and the metric between
        // changes is noise. Here the change is the event, so a throttle would delete the signal
        // entirely. What keeps it readable is the run-length counter in the header.
        //
        // DEBUG CHANNEL, the AllocTelemetry precedent (ConstraintLayerBridge.cs:550-554): the route
        // lines this accompanies are already on the operator channel, and this is the measurement
        // depth underneath them. Unconditional — debug.log is always written, so the instrument
        // cannot go quiet in the state it exists for (the 41 §7.4 pattern).
        private static void Churn(RouteChurn.Route r)
        {
            try
            {
                var block = RouteChurn.Format(RouteChurn.Observe(_routeChurn, r, DateTime.UtcNow));
                if (block != null) Main.LogDebug(block);
            }
            catch (Exception e) { try { Main.LogDebug($"RouteChurn: {e.Message}"); } catch { } }
        }

        // THE OFFER, shown when Farm Rare Accessories is OFF and eligible rares exist. The advisor is
        // declining to spend the hours and saying what it declined, which is the whole point of the
        // opt-out: silence here would be indistinguishable from "there is nothing to farm".
        private static void SurfaceRaresAvailable(GearFarmAdvisor.Verdict g)
        {
            if (g == null || g.RareCount <= 0) return;

            // ⚠ FALLS BACK TO NearestRare. Gating this on an ELIGIBLE rare made the offer silent in
            // the one state it exists for: with the farm OFF the DC digger is benched and the Loot
            // Hunter gear is not worn, so drop chance is at its lowest and nothing clears the cadence
            // bar — while switching the farm ON is exactly what seats them. The offer must describe
            // the work, not only the work that already qualifies at the worst drop chance available.
            var best = g.Rare ?? g.NearestRare;
            if (best == null) return;

            var sig = g.RareCount + "@" + best.ItemId + "#" + best.Zone;
            if (!GearFarmPause.ShouldSurface(sig, _raresOfferSignature)) return;
            _raresOfferSignature = sig;

            var what = best.ChainLabel == null ? best.ItemName : $"{best.ItemName} [{best.ChainLabel}]";
            var line = $"{g.RareCount} rare accessor{(g.RareCount == 1 ? "y" : "ies")} uncapped "
                     + $"— nearest {what} in {best.ZoneName} "
                     + $"(a drop every ~{FmtH(best.HoursPerDrop)}, ~{FmtH(best.HoursToFinish)} to finish)"
                     + (g.RareCount > 1 ? $", ~{FmtH(g.RareHoursAll)} for all {g.RareCount}" : "")
                     + $". Turn on Farm Rare Accessories to chase {(g.RareCount == 1 ? "it" : "them")}"
                     // The numbers above are measured with the farm OFF, so they are the pessimistic
                     // end: turning it on seats the DC digger and the DC/Respawn gear.
                     + (g.RareEligible == 0
                        ? " — drop chance improves once it does (DC digger + Loot Hunter gear)."
                        : $" ({g.RareEligible} already within the {3:0}h cadence bar).");
            Main.Log($"Advisor: {line}");
            ChallengeOverlay.Record("GEAR", line, "set gear is capped; rares are opt-in");
        }

        // Route to a zone whose own SET gear is still uncapped and dropping at a workable cadence.
        // Outranks the rare track: a set pays a completion bonus that a set-less stray does not.
        // `runnerUp` is second place in the SAME ranking, for the churn log's "by how much did it
        // win?" — read only by the instrument, never by this method's decision.
        private static bool ApplySetFarm(GearFarmAdvisor.ZonePlan p, GearFarmAdvisor.ZonePlan runnerUp)
        {
            if (p == null || p.Zone < 0) return false;
            if (!Main.Settings.AdvisorZones) return false;

            // Same demand as every other farm: standing in a zone collecting drops.
            FarmVenue.DropFarmActive = true;

            var sig = "set@" + p.Zone;
            if (GearFarmPause.ShouldSurface(sig, _setFarmSignature))
            {
                _setFarmSignature = sig;
                var line = $"set farm -> {p.ZoneName} ({p.MissingItems.Count} set item(s) left, "
                         + $"a drop every ~{FmtH(p.SlowestSetCadence)}, ~{FmtH(p.HoursToCap)} to finish)"
                         + ItopodOverrideNote()
                         + OwnerOverrideNote(p.Zone, p.ZoneName);
                Main.Log($"Advisor: {line}");
                ChallengeOverlay.Record("GEAR", line, "set completion outranks set-less accessories");
            }

            // OUTSIDE the latch above: a route change is the churn event even when the announcement
            // is suppressed, and BEFORE the SnipeZone guard, because a track change that keeps the
            // zone number is still a ChangeGear + a digger re-level.
            Churn(RouteChurn.Of("SET", p.Zone, p.ZoneName, "set completion outranks set-less accessories",
                score: p.HoursToCap, scoreLabel: "cap", cadence: p.SlowestSetCadence,
                bar: GearFarmAdvisor.TargetHours, barOnCadence: true,
                runnerUp: runnerUp?.HoursToCap ?? double.NaN, runnerUpName: runnerUp?.ZoneName));

            if (Main.Settings.SnipeZone != p.Zone)
                Main.Settings.SnipeZone = p.Zone;
            return true;
        }

        // Route to an eligible ultra-rare / chain item. Returns true when it took routing.
        private static bool ApplyRareFarm(GearFarmAdvisor.RareTarget r, GearFarmAdvisor.RareTarget runnerUp)
        {
            if (r == null || r.Zone < 0) return false;
            if (!Main.Settings.AdvisorZones) return false;

            // DECLARE THE DROP-CHANCE DEMAND. The digger pass reads this to run the DC/PP venue law
            // (FarmVenue): standing in a zone waiting on drops is exactly when digger 0 earns and
            // digger 8 has nothing to collect — [DECOMP] AdventureController.cs:2919 gates the whole
            // perk-point block on `zone == 1000`. Raised here rather than inferred from SnipeZone
            // because only this path knows the zone was chosen FOR its drop rate.
            FarmVenue.DropFarmActive = true;

            // Hoisted so the overlay's reason and the churn log's reason are provably one string.
            var why = r.DcWontHelp ? "flat drop rate — more drop chance will not speed this up"
                                   : "drops arrive regularly at the current drop chance";

            var sig = r.ItemId + "@" + r.Zone;
            if (GearFarmPause.ShouldSurface(sig, _rareFarmSignature))
            {
                _rareFarmSignature = sig;
                var what = r.ChainLabel == null ? r.ItemName : $"{r.ItemName} [{r.ChainLabel}]";
                var line = $"rare farm -> {r.ZoneName} for {what} "
                         + $"(a drop every ~{FmtH(r.HoursPerDrop)}, {r.DropsNeeded} merges left "
                         + $"= ~{FmtH(r.HoursToFinish)}){ItopodOverrideNote()}"
                         + OwnerOverrideNote(r.Zone, r.ZoneName);
                Main.Log($"Advisor: {line}");
                ChallengeOverlay.Record("GEAR", line, why);
            }

            // RANKED ON ITS CADENCE, not on time-to-finish: "the point of an eligible rare is that
            // drops arrive regularly enough to be worth standing there" (GearFarmAdvisor). So the
            // score and the cadence are one number here, and the churn log prints it once.
            Churn(RouteChurn.Of("RARE", r.Zone, r.ZoneName, why,
                score: r.HoursPerDrop, scoreLabel: "drop", cadence: r.HoursPerDrop,
                bar: GearFarmAdvisor.TargetHours, barOnCadence: true,
                runnerUp: runnerUp?.HoursPerDrop ?? double.NaN, runnerUpName: runnerUp?.ItemName));

            if (Main.Settings.SnipeZone != r.Zone)
                Main.Settings.SnipeZone = r.Zone;
            return true;
        }

        private static string FmtH(double h)
            => double.IsInfinity(h) ? "never" : h >= 1 ? $"{h:0.#}h" : $"{h * 60:0}m";

        // A drop farm outranks Target ITOPOD (Main.cs, the tempZone resolution) — but an override the
        // operator did not ask for must not be silent. That is the whole finding of audit/40 §3: the
        // advisor wrote a farm zone, announced it, and the toggle discarded it one line later with
        // nothing said, for an entire run.
        private static string ItopodOverrideNote()
        {
            try
            {
                return Main.Settings.AdventureTargetITOPOD
                    ? " — overriding Target ITOPOD, which is still on"
                    : "";
            }
            catch { return ""; }
        }

        // THE SAME FINDING FROM THE OTHER SIDE — audit/40 §3 item 2, still live per §6.4.
        // ItopodOverrideNote above answers "is this line overriding the operator's toggle". This
        // answers the opposite question: "is this line's target going to be adventured at all".
        // ApplyZones stands down for exactly two of the six layer-2 contenders (:988,
        // GoldCBlockMode || MoneyPitRunMode). The other four — §7's corrected membership: R4 the
        // empty Time Machine, R5 the gold snipe, R6 titans, R7 quests — own routing for as long as
        // their condition holds, and the advisor writes, announces and quotes an ETA straight
        // through them.
        //
        // ⚠ IT DOES NOT STAND DOWN, ON PURPOSE, AND IT DOES NOT RE-RANK. A return here would freeze
        // SnipeZone on whatever was last written — the challenge pause's reasoning verbatim (audit/40
        // §1.2, and the fall-through note at the pause below) — and it would be a precedence change,
        // which audit/40 §2 records as deliberate on every row. The write, the ranking and the
        // routing are untouched; only the sentence moves.
        //
        // THE CAUSE IS THE RESOLVER'S OWN, NEVER RE-DERIVED. ZoneRouting.Last is what Main.SnipeZone
        // latched on its most recent pass, and that runs from LateUpdate (~60/s) while this runs on a
        // 30s tick, so it is at most one frame old. ONE state can leave it stale: adventure being
        // uninteractable, where SnipeZone() returns above every gate (Main.cs:1337) and nothing is
        // adventured at all. A note naming an owner from before that would name the wrong cause, and
        // this campaign's own rule is that a line naming the wrong thing is worse than none.
        private static string OwnerOverrideNote(int target, string targetName)
        {
            try
            {
                if (!Main.Character.buttons.adventure.interactable) return "";
                return ZoneRouting.OwnerNote(ZoneRouting.Last, target, targetName);
            }
            catch { return ""; }
        }

        // PHASES IDLE AND ITOPOD. Returns true when the machine took routing.
        //
        // ⚠ IT MUST NOT SWALLOW THE BOOST FARM. Returning false hands routing to the boost/ITOPOD
        // path below, exactly as the challenge pause does (see the fall-through note in ApplyZones)
        // and for the same reason: this branch is reached when the gear farm has nothing to say, and
        // a machine with no ladder to climb must leave the advisor behaving as it would with Farm
        // Gear Zones off. It takes routing only when there is a real phase to be in.
        //
        // The pause itself is upstream of this: a paused farm never reaches here (the call site sits
        // inside `if (gfSig == null)`), so the pause still falls through to the boost path untouched.
        private static bool ApplyZonePhase()
        {
            ZonePhase.PlanReport r;
            try { r = ZonePhase.Explain(ZonePhaseReader.Candidates()); }
            catch (Exception ex) { Main.LogDebug($"ZonePhase: {ex.Message}"); return false; }
            var d = r.Chosen;

            if (d.Phase == ZonePhase.Phase.None)
            {
                // ⚠ A DECLINE MUST SAY SO ONCE. Falling through to the boost farm is legitimate and
                // common, but it emits nothing — so "correctly declining" and "silently broken" read
                // identically, which is the 25 §4 failure one level up. Latched on the COUNTS, so a
                // steady state costs one line and a change in what the machine sees costs one more.
                var declineSig = "None#" + r.Candidates + "/" + r.FarmReady + "/" + r.Idle + "/" + r.Parked + "/" + r.Declined;
                if (ZonePhase.ShouldSurface(declineSig, _zonePhaseSignature))
                {
                    _zonePhaseSignature = declineSig;
                    Main.Log($"Advisor: zone phase -> none, boost farm keeps routing ({r.Summary()})");
                }
                return false;
            }

            // P2e: behind AdvisorZones. Reaching here already implies it is on (:753 returns above),
            // so this is the rule stated where it is enforced rather than only in a comment.
            if (!ZonePhase.WritesZone(d, Main.Settings.AdvisorZones))
                return false;

            var sig = ZonePhase.Signature(d);
            if (ZonePhase.ShouldSurface(sig, _zonePhaseSignature))
            {
                _zonePhaseSignature = sig;
                // IDLE RAISES THE DROP-FARM DEMAND, SO IDLE OVERRIDES Target ITOPOD — and audit/40
                // §6.1 requires an override to say so. The set, rare and FARM lines have carried this
                // note since 271f5f8; IDLE, added by the same campaign, did not, so the one phase
                // that exists to stand in a zone silently beat the operator's own toggle. Asked of
                // ZonePhase, not re-derived, so this and the demand below cannot drift apart.
                //
                // ⚠ BOTH NOTES, AND IN THIS ORDER. They are not alternatives and they are not
                // mutually exclusive — the toggle being on does not stop a titan owning routing, so
                // one line can legitimately carry both. They answer different questions: the toggle
                // note says WHAT THIS LINE BEAT, the owner note says WHETHER THE TARGET IS BEING
                // ADVENTURED AT ALL (see both helpers' headers above).
                //
                // The order is the one already established at the three sibling lines (:839-840,
                // :882-883, :1108-1109) and pinned by ZoneOwnerNoteTests'
                // The_note_composes_with_the_Target_ITOPOD_note: toggle note first, owner note LAST.
                // The owner note has to be terminal because it qualifies everything before it — put
                // it first and "…is not being adventured while it holds — overriding Target ITOPOD"
                // dangles the override clause off the end of a negation and reads as nonsense.
                var line = ZonePhase.Message(d, ZonePhaseReader.ZoneName(d.TargetZone))
                         + (ZonePhase.RaisesDropFarmDemand(d) ? ItopodOverrideNote() : "")
                         + OwnerOverrideNote(d.TargetZone, ZonePhaseReader.ZoneName(d.TargetZone));
                Main.Log($"Advisor: {line}");
                // P3b. The transition line is the only line this phase gets, and ITOPOD can park on
                // it indefinitely — so it also goes to the FEED, where it stays visible without
                // re-emitting. A farm that stops without saying so is indistinguishable from a farm
                // that broke (amendment 25 §4, found at two hours' cost).
                ChallengeOverlay.Record("GEAR", line,
                    d.Parked ? ZonePhase.GapText(d) : d.Reason);
            }

            // IDLE is a drop farm by definition — "idle in the zone until you collect at least one
            // copy of the accessories". ITOPOD is the opposite venue and gives the demand back. The
            // SAME expression the line above announced with: the demand and the sentence describing
            // it are one rule (ZonePhase.RaisesDropFarmDemand), not two that agree today.
            FarmVenue.DropFarmActive = ZonePhase.RaisesDropFarmDemand(d);

            // ⚠ DELIBERATELY UNRANKED. The phase machine picks the HIGHEST qualifying zone number
            // (ZonePhase.Explain), not the best value of a continuous metric — it has no score to
            // report, and reporting the one-hit gap as if it were one would put an attack-power
            // figure in a column every other track fills with hours. Its half of a churn line is the
            // elapsed time and the reason, both of which are the real content anyway.
            Churn(RouteChurn.Of(d.Phase.ToString().ToUpperInvariant(), d.TargetZone,
                ZonePhaseReader.ZoneName(d.TargetZone), d.Parked ? ZonePhase.GapText(d) : d.Reason));

            if (Main.Settings.SnipeZone != d.TargetZone)
                Main.Settings.SnipeZone = d.TargetZone;
            return true;
        }

        private static void ApplyZones()
        {
            if (!Main.Settings.CombatEnabled) return;
            if (Main.Settings.GoldCBlockMode || Main.Settings.MoneyPitRunMode) return;

            // GEAR HUNT: the user-picked stage outranks the automatic farms. Cheap and outside the
            // 10-minute throttle so flipping the toggle acts on the next tick; an unreachable stage
            // leaves routing alone until it unlocks.
            if (GearHunter.Active)
            {
                if (!GearHunter.ZoneReachable()) return;
                int hz = Main.Settings.GearHuntZone;
                string hn = ZoneHelpers.ZoneList.TryGetValue(hz, out var n) ? n : $"Zone {hz}";
                // Unranked: this stage was picked by the user, not by a score. Instrumented anyway —
                // hunt is above the 10-minute throttle, so it is the one track that can time a
                // transition to the minute, and entering/leaving it is a ChangeGear like any other.
                Churn(RouteChurn.Of("HUNT", hz, hn, "gear hunt — the user-picked stage outranks the automatic farms"));
                if (Main.Settings.SnipeZone != hz)
                {
                    Main.Settings.SnipeZone = hz;
                    // The hunt is the TOP row of ResolveIntentZone (Main.cs), so nothing inside the
                    // R10 chain takes it — but the six gates that return ABOVE the chain still do.
                    Main.Log($"Advisor: farm zone -> {hn} (gear hunt)" + OwnerOverrideNote(hz, hn));
                }
                return;
            }
            if (!Main.Settings.AdvisorZones) return;

            if ((DateTime.UtcNow - _lastZoneCheck).TotalMinutes < 10) return;
            _lastZoneCheck = DateTime.UtcNow;

            // Farm Gear Zones outranks the boost farm: every capped item is a PERMANENT item-list
            // bonus, and only zones that finish inside the advisor's time budget qualify.
            if (Main.Settings.AdvisorFarmGear)
            {
                // THE CHALLENGE PAUSE (amendment 25 §5). This gates an EXISTING writer — amendment 26
                // §3 corrected 25 §2's "reaches a text string and nothing else": the SnipeZone write
                // below is live, so without this gate gear farming has been overriding the adventure
                // zone mid-challenge all along. Laser Sword is the sole exception (GearFarmPause).
                //
                // IT FALLS THROUGH, IT DOES NOT RETURN. A return would freeze SnipeZone on whatever
                // the gear farm last set it to — the very placement the pause exists to undo. Falling
                // through hands routing back to the boost/ITOPOD path, i.e. the advisor behaves
                // exactly as it would with Farm Gear Zones switched off. Re-engagement is automatic
                // when the challenge clears; §5 assumes that, and its open item 3 leaves "or wait for
                // the user" undecided, so nothing here decides it either.
                string gfChallenge = null;
                try { gfChallenge = ChallengeDetector.Current(); } catch { }
                var gfSig = GearFarmPause.Signature(gfChallenge);
                if (GearFarmPause.ShouldSurface(gfSig, _gearFarmPauseSignature))
                {
                    _gearFarmPauseSignature = gfSig;
                    var line = GearFarmPause.Message(gfSig);
                    Main.Log($"Advisor: {line}");
                    ChallengeOverlay.Record("GEAR", line, GearFarmPause.Reason(gfSig));
                }
                if (gfSig == null)
                {
                    var g = GearFarmAdvisor.Analyze();
                    if (g.Known && g.Best != null)
                    {
                        // PHASE: FARM. The gear farm ranks farm targets by HoursToCap against a time
                        // budget (GearFarmAdvisor.cs:396, :421); the phase machine does not second-
                        // guess that ranking, it only labels the phase so the transition line is
                        // continuous with IDLE and ITOPOD.
                        //
                        // ⚠ SURFACED ON THE PHASE, NOT ON THE ZONE. This used to log only when
                        // SnipeZone changed, which is silent for the transition that matters most:
                        // IDLE on zone N becomes FARM on zone N the moment one-hit is reached, and
                        // the zone number does not move. That is a phase change with no line — the
                        // 25 §4 shape. The latch below fires on phase+zone, so it says so.
                        var farmSig = "Farm#" + g.Best.Zone;
                        if (ZonePhase.ShouldSurface(farmSig, _zonePhaseSignature))
                        {
                            _zonePhaseSignature = farmSig;
                            var fline = $"zone phase -> FARM {g.Best.ZoneName} "
                                      + $"(gear: {g.Best.MissingItems.Count} uncapped, ~{g.Best.HoursToCap:0.#}h to cap)"
                                      + ItopodOverrideNote()
                                      + OwnerOverrideNote(g.Best.Zone, g.Best.ZoneName);
                            Main.Log($"Advisor: {fline}");
                            ChallengeOverlay.Record("GEAR", fline, "one-hit met, set not capped");
                        }
                        // Same demand as the rare track: FARM stands in a zone collecting drops.
                        FarmVenue.DropFarmActive = true;
                        // FARM's admission bar is on HoursToCap (Viable), NOT on cadence — the one
                        // track of the three where that is true, which is why the bar names the
                        // quantity it tests instead of assuming it.
                        Churn(RouteChurn.Of("FARM", g.Best.Zone, g.Best.ZoneName, "one-hit met, set not capped",
                            score: g.Best.HoursToCap, scoreLabel: "cap", cadence: g.Best.SlowestSetCadence,
                            bar: GearFarmAdvisor.TargetHours, barOnCadence: false,
                            runnerUp: g.BestRunnerUp?.HoursToCap ?? double.NaN,
                            runnerUpName: g.BestRunnerUp?.ZoneName));
                        if (Main.Settings.SnipeZone != g.Best.Zone)
                            Main.Settings.SnipeZone = g.Best.Zone;
                        return;
                    }

                    // ⚠ THE GEAR FARM'S REASON WAS COMPUTED AND THROWN AWAY. Verdict.Text says which
                    // of three very different states this is — "All farmable zone gear is capped",
                    // "No gear zone caps within 3h — closest is X (needs ~N% drop chance)", or "roll
                    // caps hold them past 3h" (GearFarmAdvisor.cs:426-443) — and ApplyZones read only
                    // .Best, so the log could not distinguish "nothing left to do" from "everything
                    // is out of budget". Both produce the same silence, and they want opposite
                    // responses. Latched, so it is one line per change of state, not one per pass.
                    if (g.Known && !string.IsNullOrEmpty(g.Text)
                        && GearFarmPause.ShouldSurface(g.Text, _gearFarmTextSignature))
                    {
                        _gearFarmTextSignature = g.Text;
                        Main.Log($"Advisor: {g.Text}");
                        // [OPERATOR] asked for the per-zone missing items by name. Without it the
                        // only way to learn WHY a zone was rated as it was, was to read the drop
                        // table by hand — which is how "why does it even consider Chocolate World"
                        // went unanswered for a whole run.
                        // EVERY candidate, not just Best/Nearest — on the "roll caps hold them past
                        // 3h" path both of those are null, which is precisely the path where the
                        // question "which item is holding this zone" gets asked. Bounded by the
                        // number of zones with uncapped gear, and latched to the text above.
                        foreach (var p in g.Plans)
                            Main.Log($"Advisor:   {p.ZoneName}: {GearFarmAdvisor.DescribeMissing(p)}");
                    }

                    // SET COMPLETION FIRST [OPERATOR 2026-08-05]: "weigh capping a new set for the
                    // bonus above non set accessories. once the sets are maxxed, go back and farm the
                    // non set accessories." A set pays a completion bonus on top of the per-item
                    // list bonuses; a set-less stray pays only the latter. Above the rare track by
                    // rank, so the two are no longer decided by which time bar each was measured by.
                    // When every set is capped, SetTarget is null and routing falls through to Rare.
                    if (g.Known && g.SetTarget != null && ApplySetFarm(g.SetTarget, g.SetRunnerUp)) return;
                    if (g.Known && g.SetTarget == null) _setFarmSignature = null;

                    // THE ULTRA-RARE / CHAIN TRACK [OPERATOR 2026-08-05], BELOW the gear farm and
                    // ABOVE the boost farm. Reached only when the gear farm has no in-budget zone, so
                    // baseline set gear always wins when there is any; below that, a rare whose drops
                    // arrive regularly beats boost farming, because most of both chains is Looting and
                    // every merge compounds into all later farming.
                    // OPT-OUT [OPERATOR 2026-08-05]. Off = set gear only; the advisor still says what
                    // is on the table so the choice to spend the hours is the user's, not a silent
                    // commitment. Defaults OFF because these are long: measured on this save the
                    // cheapest eligible rare was ~35h and the dearest 642h.
                    if (g.Known && Main.Settings.AdvisorFarmRares)
                    {
                        _raresOfferSignature = null;   // re-offer if it is switched back off
                        if (g.Rare != null && ApplyRareFarm(g.Rare, g.RareRunnerUp)) return;
                    }
                    else if (g.Known)
                    {
                        // OFF, or on with nothing eligible. Either way say what is outstanding —
                        // gated on RareCount (any uncapped rare), NOT on one being eligible.
                        SurfaceRaresAvailable(g);
                        _rareFarmSignature = null;     // re-announce the route if it is switched on
                        if (g.RareCount == 0) _raresOfferSignature = null;
                    }

                    // THE THREE-PHASE RULE'S OTHER TWO PHASES. Reached only when the gear farm has no
                    // target — which is exactly the state in which they apply, because Analyze
                    // discards every zone that is not already one-shottable (GearFarmAdvisor.cs:375)
                    // and those are the zones IDLE and ITOPOD are about. Before this, ApplyZones fell
                    // straight through to the boost farm here and the two phases did not exist.
                    if (ApplyZonePhase()) return;
                }
            }

            // Routing reached the boost/ITOPOD path, so no drop farm owns it — hand the demand back.
            // Cleared HERE rather than at the top of ApplyZones: the 10-minute throttle returns early
            // on most ticks, so clearing up there would drop the demand seconds after raising it.
            FarmVenue.DropFarmActive = false;

            var v = BoostFarmAdvisor.Analyze();
            if (!v.Known) return;
            int target = v.BestZone == -1000 ? 1000 : v.BestZone;
            string name = v.BestName;
            string detail = $"{v.BestRate:0.##} boost-value/kill";
            // Farm Best Boost: boost zones only beat the ITOPOD while something consumes boosts.
            if (Main.Settings.AdvisorFarmBoost && target != 1000 && !BoostFarmAdvisor.BoostDemandExists(out var why))
            {
                target = 1000;
                name = "ITOPOD";
                detail = $"no boost demand — {why}";
            }
            // ⚠ RANKED IN boost-value/kill, WHERE HIGHER WINS AND THE UNIT IS NOT HOURS. The only
            // track of the seven for which both are true; the churn log carries both facts with the
            // number so a rate is never printed as a time or subtracted from one.
            Churn(RouteChurn.Of("BOOST", target, name, detail,
                score: v.BestRate, scoreLabel: "boost-value/kill", scoreInHours: false, higherWins: true));

            if (Main.Settings.SnipeZone != target)
            {
                Main.Settings.SnipeZone = target;
                // Silent when target is 1000 — that is the ITOPOD, the fallback venue itself, and no
                // owner "takes it away" (ZoneRouting.OwnerNote's second silence).
                Main.Log($"Advisor: farm zone -> {name} ({detail})" + OwnerOverrideNote(target, name));
            }
        }

        // EXP balancing (guide ratios): one walk step per minute, waterfilling up to 10% of banked EXP
        // across the lagging stats — raises the lowest levels first, converging on the ratio in gentle
        // chunks, then maintains it with proportional buys.
        private static DateTime _lastExpBuy = DateTime.MinValue;

        private static void ApplyExpBuys()
        {
            if ((DateTime.UtcNow - _lastExpBuy).TotalSeconds < 60) return;
            _lastExpBuy = DateTime.UtcNow;
            var what = ExpBalancer.BuyTick(0.10);
            if (what != null)
                Main.Log($"Advisor: bought {what}");
        }

        // Phase C: gear auto-refresh. When the active gear breakpoint is objective-driven, periodically
        // re-optimize the same objective and re-equip if a new drop/merge made a meaningfully better
        // loadout available (>= 5%). Optimize is heavy, so this is throttled well beyond the 30s tick.
        private static DateTime _lastGearCheck = DateTime.MinValue;
        private static string _lastGearObjective;
        // The Gear Lock the last committed pass ran with, as a canonical id string. Tracked next to
        // the objective and for the same reason: a lock change must bypass the 5% anti-churn bar, and
        // a lock usually LOWERS the solved score, so without this a newly added lock never clears the
        // bar and never goes on. "" = no locks, which is every profile written before the feature.
        private static string _lastGearLocks = "";
        // False on every payload load. A reload can leave a lock's TEMP loadout equipped with the
        // restore set lost (Unload doesn't release locks; statics wipe — user-reported: gear stayed
        // swapped after a reload and never returned to the segment loadout, because the score
        // early-outs below read "scores about as well" as "nothing to do"). The first pass after a
        // load therefore equips the objective's best set UNCONDITIONALLY, re-asserting known-good
        // gear; the anti-churn thresholds apply from then on.
        private static bool _gearAsserted;

        // Called by LockManager when a mode lock restores its saved gear: that gear is whatever was
        // worn at ACQUISITION — stale if the segment/objective moved while the lock was held
        // (user-reported: AT gear restored into the NGU MARATHON). Clearing the objective marker
        // re-arms the changed-objective bypass, and clearing the throttle makes the very next
        // advisor tick re-evaluate instead of waiting out the 120s window.
        public static void GearRestored()
        {
            _lastGearObjective = null;
            _lastGearCheck = DateTime.MinValue;
        }

        // Are every locked item actually ON right now? The two "don't downgrade" guards below compare
        // SCORES, and a score cannot see a lock — pinning an item the optimizer would not have chosen
        // is, by construction, a lower score. So both guards additionally require that the locks are
        // already worn; otherwise they would answer "already optimal" about a set that is missing the
        // very items the user pinned, and the locks would never be equipped at all.
        //
        // True when there is no lock, which is the whole of the old behaviour.
        // Is every item of this set already on? The chain paths' "nothing to do" test
        // (GearRefreshPolicy.DecideChain). Unreadable gear reads as "not worn", so the equip proceeds.
        private static bool SetIsWorn(int[] ids)
        {
            try
            {
                var worn = new HashSet<int>(LoadoutManager.CurrentGearIds());
                return ids.Length > 0 && ids.All(worn.Contains);
            }
            catch { return false; }
        }

        private static bool LocksAreWorn(GearLockPlan plan)
        {
            if (plan == null || plan.Applied == 0) return true;
            try
            {
                var worn = new HashSet<int>(LoadoutManager.CurrentGearIds());
                foreach (var id in plan.Weapons) if (!worn.Contains(id)) return false;
                foreach (var id in plan.Accessories) if (!worn.Contains(id)) return false;
                if (plan.Head != 0 && !worn.Contains(plan.Head)) return false;
                if (plan.Chest != 0 && !worn.Contains(plan.Chest)) return false;
                if (plan.Legs != 0 && !worn.Contains(plan.Legs)) return false;
                if (plan.Boots != 0 && !worn.Contains(plan.Boots)) return false;
                return true;
            }
            // An unreadable inventory must not STRAND the locks: fall back to "not worn", which lets
            // the equip proceed. The opposite default would silently keep them off forever.
            catch { return false; }
        }

        // A drop / merge / trash changed what the player owns, so the set the optimizer would pick may
        // have changed too. Deliberately NOT GearRestored(): that also clears _lastGearObjective, which
        // makes objectiveChanged true in ApplyGearRefresh and BYPASSES the 5% anti-churn bar — a 0.2%
        // improvement would then trigger a full re-equip, and every ChangeGear zeroes energy/magic/R3
        // allocation until the next allocation pass (up to 10s of nothing across eight systems).
        // Re-arm the clock; keep the bar.
        public static void GearInventoryChanged()
        {
            _lastGearCheck = DateTime.MinValue;
        }

        // Companion "Re-optimize gear now" button. Unlike GearRestored() (which only re-arms the throttled
        // auto pass — and that pass still bails on ManageGear-off / locks / the anti-churn bar, so the user
        // sees "nothing happened"), this is an explicit manual action: it resolves the active objective,
        // equips the optimizer's best set right now on the main thread, and returns a human-readable outcome
        // so the companion can show it. Returns WHY nothing changed when a set can't be equipped, instead of
        // silently no-op'ing. Must run on the Unity main thread (DrainCommands guarantees this).
        public static string ForceGearReoptimize()
        {
            try
            {
                if (Main.Settings == null || !Main.Settings.ManageGear)
                    return "Gear automation is OFF — turn it on (Loadouts · Main) to let the advisor equip gear.";
                // The tick path runs inside `if (LockManager.CanSwap())` (see Tick), but this one only
                // ever checked the QUEST lock — so pressing the button during a titan / gold / cooking /
                // yggdrasil / money-pit window equipped the main set straight over that mode's loadout.
                // On a real (non-autokill) titan that strips the Power/Toughness kill set ResolveTitanGear
                // deliberately forces, which is the death loop; and RestoreConfiguration then throws the
                // user's request away anyway, restoring the gear worn at lock acquisition. Same gate as
                // the tick, so the two paths can no longer disagree.
                if (!LockManager.CanSwap())
                    return $"A {LockManager.GetLockTypeName().ToLowerInvariant()} swap owns your gear right now — try again once it finishes.";
                if (LockManager.HasQuestLock())
                    return "A major quest is running — gear is held to the quest set until it finishes.";

                var resolved = GearObjectiveApply.Current();
                if (resolved.Source == GearObjectiveResolver.Src.Noec)
                    return "No-Equipment Challenge is active — there's nothing to equip.";
                // A manual ID row is a gear breakpoint — it just names items instead of an objective.
                // Telling the operator to "add a gear breakpoint" when they authored one is the same
                // class of wrong answer as the old ":percent is ignored" line.
                if (resolved.IsManual) return resolved.Sentence;

                string objName = resolved.Name;
                if (string.IsNullOrEmpty(objName))
                    return "No gear objective is active — pick one under Loadouts › Main, or add a gear breakpoint to your profile.";

                if (objName == GearObjectiveResolver.LootHunter)
                {
                    var huntIds = GearHunter.ResolveLoadout(out var what);
                    if (huntIds.Length == 0) return "The loot-hunter loadout resolved empty.";
                    LoadoutManager.ChangeGear(huntIds);
                    Main.InventoryController.assignCurrentEquipToLoadout(0);
                    // A slot the OPERATOR owns, saved over by four separate advisor gear paths, with nothing
                    // anywhere saying so until the census found it. Its previous contents are retained nowhere,
                    // which is why the ledger row exists and an undo for it cannot.
                    WriteLedger.Record("gear.slot0", "overwritten with the advisor's current gear",
                        "saved automatically after an advisor gear change", ChallengeOverlay.Segment,
                        "Four advisor gear paths do this; all of them land here",
                        "The contents that were in the slot before are not kept anywhere",
                        "Re-save the slot yourself if you were using it");
                    // NOT _lastGearCheck — see the main path below for why the button must not move the
                    // periodic pass's clock.
                    _gearAsserted = true; _lastGearObjective = objName;
                    Main.Log($"Advisor: gear re-optimized on request — loot hunter ({what})");
                    return $"Equipped the loot-hunter set ({what}).";
                }

                var obj = GearOptimizer.FindObjective(objName);
                if (obj == null) return $"Couldn't find the '{objName}' objective.";
                double cur = GearOptimizer.CurrentScore(obj);
                // Name, respawn flag AND Gear Lock come from the SAME resolution, so the set scored
                // here is always the set that would be equipped. For every pre-existing source this is
                // the profile breakpoint's flag exactly as before; only the standing pin supplies its
                // own, and only the profile row supplies locks.
                var best = GearOptimizer.Optimize(obj, resolved.ForceRespawn, GearLockSet.Of(resolved.Locks));
                if (best == null) return "The optimizer returned no set for this objective.";
                GearOptimizer.ReportLock(best);
                var ids = best.AllIds().Where(x => x > 0).Distinct().ToArray();
                if (ids.Length == 0) return "The optimizer returned an empty set.";

                // ── THREE RULES THIS BUTTON USED TO BREAK, ALL OF WHICH ApplyGearRefresh BELOW ALREADY
                //    STATES AS LAW. They are repeated here because the two paths answer the SAME user
                //    request ("give me gear for this objective") and used to answer it differently.
                //
                // 1. THE GUARD ITSELF IS SHARED, NOT RESTATED. Both paths decline on
                //    "worn out-scores best, and the locks are on"; only the periodic pass adds its 5%
                //    anti-churn bar on top (objectiveChanged bypasses THAT bar, and nothing else —
                //    AdvisorApply.cs:1601-1606 still declines to equip on a changed objective when the
                //    worn set wins). The guard now lives in GearRefreshPolicy so the next edit to
                //    either path cannot quietly diverge.
                // 2. THE TRACKERS COMMIT ONLY WHEN THE PASS RESOLVES. Committing before the guard
                //    recorded "this objective is handled" for a pass that equipped nothing — the exact
                //    thing ApplyGearRefresh's comment forbids ("a no-op pass must NOT consume the
                //    bypass"). A declining click therefore consumed the objective change, and the
                //    periodic pass that would have honoured it saw objectiveChanged == false.
                // 3. THE BUTTON DOES NOT OWN THE PERIODIC CLOCK. _lastGearCheck gates ApplyGearRefresh
                //    to one pass per 120s; setting it here meant every click PUSHED THE AUTOMATIC PASS
                //    OUT ANOTHER TWO MINUTES. Clicking "Re-optimize gear now" repeatedly was the
                //    slowest way to get re-optimized gear. The button equips synchronously and needs no
                //    clock of its own.
                string lockKey = GearRefreshPolicy.LockKey(resolved.Locks);
                bool objectiveChanged = GearRefreshPolicy.ObjectiveChanged(objName, _lastGearObjective, lockKey, _lastGearLocks);

                // Don't downgrade: if the worn set already scores at/above the optimizer's best, leave it.
                // On a CHANGED objective this same test means "you are already wearing the best set for
                // it", because the optimiser searched the whole inventory — so declining is right, and
                // re-equipping would be a pure cost: ChangeGear zeroes energy/magic/R3 allocation.
                var chainObj = obj as GearChain.ChainObjective;
                var verdict = chainObj != null
                    ? GearRefreshPolicy.DecideChain(SetIsWorn(ids), LocksAreWorn(best.Lock))
                    : GearRefreshPolicy.Decide(cur, best.Score, LocksAreWorn(best.Lock));
                if (verdict == GearRefreshPolicy.Verdict.AlreadyOptimal)
                {
                    // A VERIFIED already-optimal IS a resolution (ApplyGearRefresh commits on the same
                    // footing), so it commits — but AFTER the decision, never before it.
                    _gearAsserted = true; _lastGearObjective = objName; _lastGearLocks = lockKey;
                    // Rule 3's other half: a declining click used to be invisible. Five of them in a row
                    // left NOTHING in the log, so "I pressed it and nothing happened" could not be told
                    // apart from "the command never arrived".
                    Main.Log($"Advisor: gear already optimal for '{obj.Name}' — nothing re-equipped.");
                    return $"Already optimal for '{obj.Name}' — your equipped set is the best available.";
                }

                // A chain has no single gain to quote: the lead step's score usually FALLS when a later
                // step takes its accessories.
                double gain = chainObj == null && cur > 0 ? (best.Score / cur - 1) * 100 : 0;
                LoadoutManager.ChangeGear(ids);
                Main.InventoryController.assignCurrentEquipToLoadout(0);
                // A slot the OPERATOR owns, saved over by four separate advisor gear paths, with nothing
                // anywhere saying so until the census found it. Its previous contents are retained nowhere,
                // which is why the ledger row exists and an undo for it cannot.
                WriteLedger.Record("gear.slot0", "overwritten with the advisor's current gear",
                    "saved automatically after an advisor gear change", ChallengeOverlay.Segment,
                    "Four advisor gear paths do this; all of them land here",
                    "The contents that were in the slot before are not kept anywhere",
                    "Re-save the slot yourself if you were using it");
                _gearAsserted = true; _lastGearObjective = objName; _lastGearLocks = lockKey;
                Main.Log(chainObj != null
                    ? $"Advisor: gear {(objectiveChanged ? "switched to" : "re-optimized on request for")} '{obj.Name}' [{GearChain.Describe(chainObj.Priorities)}]"
                    : objectiveChanged
                    ? $"Advisor: gear switched to '{obj.Name}' on request (objective change, {gain:+0.#;-0.#;+0.0}%)"
                    : $"Advisor: gear re-optimized on request for '{obj.Name}' (+{gain:0.#}%)");
                return gain > 0.05
                    ? $"Equipped the best set for '{obj.Name}' — +{gain:0.#}% over what was on."
                    : $"Equipped the best set for '{obj.Name}'.";
            }
            catch (Exception e)
            {
                Main.LogDebug($"ForceGearReoptimize: {e}");
                return "Re-optimize hit an error — check the Debug log.";
            }
        }

        private static void ApplyGearRefresh()
        {
            if (!Main.Settings.ManageGear) return;
            // CanSwap() allows the quest lock through, but quest gear is equipped then — don't fight it.
            if (LockManager.HasQuestLock()) return;

            // One shared definition of "which objective, and why" (GearObjectiveResolver): NOEC beats
            // everything, then the challenge rotation, then gear hunt, then the profile timeline, then
            // the user's standing pick. The hunt is checked BEFORE the override because
            // GearObjectiveOverride is the SEGMENT gear whenever AutoProfile is on, so `override ?? hunt`
            // never fell through and the Loot Hunter loadout was never equipped (user-reported).
            var resolved = GearObjectiveApply.Current();
            if (resolved.Source == GearObjectiveResolver.Src.Noec) return;   // no equipment — don't churn
            string objName = resolved.Name;
            if (string.IsNullOrEmpty(objName)) return;

            // A new drop/merge re-arms this clock (GearWatch, which runs earlier in this same tick), so
            // a better set is picked up on this pass instead of waiting out the rest of the 2 minutes.
            // It deliberately does NOT touch _lastGearObjective: that would set objectiveChanged below
            // and bypass the 5% bar, so a 0.2% gain would trigger a full re-equip — and every equip
            // zeroes energy/magic/R3 allocation until the next allocation pass. Re-arm the clock, keep
            // the bar.
            if ((DateTime.UtcNow - _lastGearCheck).TotalSeconds < 120) return;
            _lastGearCheck = DateTime.UtcNow;

            if (objName == GearObjectiveResolver.LootHunter)
            {
                // Hybrid set (pool accessories + best P/T): no single objective score exists, so the
                // anti-churn test is set-membership — re-equip only when the resolved set isn't worn.
                var huntIds = GearHunter.ResolveLoadout(out var what);
                if (huntIds.Length == 0) return;
                bool huntChanged = objName != _lastGearObjective;
                var worn = new HashSet<int>(LoadoutManager.CurrentGearIds());
                if (_gearAsserted && !huntChanged && huntIds.All(worn.Contains))
                {
                    _lastGearObjective = objName;
                    return;
                }
                bool firstHunt = !_gearAsserted;
                _gearAsserted = true;
                _lastGearObjective = objName;
                LoadoutManager.ChangeGear(huntIds);
                Main.InventoryController.assignCurrentEquipToLoadout(0);
                // A slot the OPERATOR owns, saved over by four separate advisor gear paths, with nothing
                // anywhere saying so until the census found it. Its previous contents are retained nowhere,
                // which is why the ledger row exists and an undo for it cannot.
                WriteLedger.Record("gear.slot0", "overwritten with the advisor's current gear",
                    "saved automatically after an advisor gear change", ChallengeOverlay.Segment,
                    "Four advisor gear paths do this; all of them land here",
                    "The contents that were in the slot before are not kept anywhere",
                    "Re-save the slot yourself if you were using it");
                Main.Log($"Advisor: gear hunt loadout equipped — {what}{(firstHunt ? " (startup/reload assert)" : "")}");
                return;
            }

            var obj = GearOptimizer.FindObjective(objName);
            if (obj == null) return;
            // Objective switches (segment/rotation changes) bypass the 5% bar: "wrong gear that's
            // within 5% on the NEW objective" is still wrong gear (user-reported: TM HOUR wearing
            // the push loadout). The threshold only applies to same-objective drop improvements.
            // _lastGearObjective commits ONLY when a pass actually resolves the switch (equip, or
            // verified already-optimal) — a no-op pass must NOT consume the bypass (user-reported:
            // segment flipped during a titan lock; the first post-release pass fizzled and the
            // stale AT gear then sat inside the 5% bar forever).
            //
            // A CHANGED GEAR LOCK COUNTS AS A CHANGED OBJECTIVE, and this is the wiring that would
            // otherwise have shipped broken. The 5% bar compares the solved score against the WORN
            // score, and a lock normally lowers the solved score — so editing your profile to pin two
            // items, with the objective name unchanged, produces `best.Score < cur * 1.05` forever and
            // the locks never go on. Nothing anywhere would have said so. Same rule the objective
            // switch already has, for the same reason: "gear that is within 5% but not what was asked
            // for" is still the wrong gear.
            // Same two helpers the button uses (GearRefreshPolicy) — these were two separately written
            // copies of the same expression, which is how the two paths drifted apart in the first place.
            string lockKey = GearRefreshPolicy.LockKey(resolved.Locks);
            bool objectiveChanged = GearRefreshPolicy.ObjectiveChanged(objName, _lastGearObjective, lockKey, _lastGearLocks);
            double cur = GearOptimizer.CurrentScore(obj);
            // Same resolution supplied the name, the respawn flag AND the Gear Lock, so this score
            // always describes the set that would actually be equipped. Unchanged from before for
            // every pre-existing source (no profile written before Gear Lock carries one).
            var best = GearOptimizer.Optimize(obj, resolved.ForceRespawn, GearLockSet.Of(resolved.Locks));
            if (best == null) return;
            GearOptimizer.ReportLock(best);
            var chainObj = obj as GearChain.ChainObjective;
            string chainGain = null;
            if (chainObj != null)
            {
                // A CHAIN IS JUDGED STEP BY STEP, NOT ON best.Score. The same 5% bar, applied to the
                // first step that leaves it (GearChain.DecidingStep), and "already wearing it" is set
                // membership rather than a score -- see GearRefreshPolicy.DecideChain for why the lead
                // score cannot be trusted with either question.
                if (_gearAsserted)
                {
                    int k = GearOptimizer.ChainDecidingStep(chainObj, best, 1.05, out bool improves,
                                                            out double wornStep, out double bestStep);
                    if (!objectiveChanged && !improves) return;
                    var chainIds = best.AllIds().Where(x => x > 0).Distinct().ToArray();
                    if (GearRefreshPolicy.DecideChain(SetIsWorn(chainIds), LocksAreWorn(best.Lock))
                        == GearRefreshPolicy.Verdict.AlreadyOptimal)
                    {
                        _lastGearObjective = objName;   // verified: the chain's best set IS what is worn
                        _lastGearLocks = lockKey;
                        return;
                    }
                    if (improves)
                        chainGain = (wornStep > 0 ? $"+{(bestStep / wornStep - 1) * 100:0.#}% " : "new ")
                                  + chainObj.Priorities[k].Objective.Name;
                }
            }
            else if (_gearAsserted)
            {
                if (!objectiveChanged && (cur <= 0 || best.Score < cur * 1.05)) return;
                // Same trap as ForceGearReoptimize's "already optimal": a lock costs score, so without
                // the LocksAreWorn clause this branch would declare the UNLOCKED worn set "optimal for
                // the new objective", commit the change, and leave the locks off permanently.
                if (objectiveChanged && cur > 0 && best.Score <= cur && LocksAreWorn(best.Lock))
                {
                    _lastGearObjective = objName;   // verified: equipped gear IS optimal for the new objective
                    _lastGearLocks = lockKey;
                    return;
                }
            }

            var ids = best.AllIds().Where(x => x > 0).Distinct().ToArray();
            if (ids.Length == 0) return;
            bool firstAssert = !_gearAsserted;
            _gearAsserted = true;
            _lastGearObjective = objName;
            _lastGearLocks = lockKey;
            LoadoutManager.ChangeGear(ids);
            Main.InventoryController.assignCurrentEquipToLoadout(0);
            // A slot the OPERATOR owns, saved over by four separate advisor gear paths, with nothing
            // anywhere saying so until the census found it. Its previous contents are retained nowhere,
            // which is why the ledger row exists and an undo for it cannot.
            WriteLedger.Record("gear.slot0", "overwritten with the advisor's current gear",
                "saved automatically after an advisor gear change", ChallengeOverlay.Segment,
                "Four advisor gear paths do this; all of them land here",
                "The contents that were in the slot before are not kept anywhere",
                "Re-save the slot yourself if you were using it");
            Main.Log(firstAssert
                ? $"Advisor: gear asserted for '{obj.Name}' (startup/reload — known-good loadout re-equipped)"
                : objectiveChanged
                    ? $"Advisor: gear switched to '{obj.Name}' loadout (objective change)"
                    : chainObj != null
                        ? $"Advisor: re-optimized gear for '{obj.Name}' ({chainGain ?? "set changed"} from new drops)"
                        : $"Advisor: re-optimized gear for '{obj.Name}' (+{(best.Score / cur - 1) * 100:0.#}% from new drops)");
        }

        private static void ApplyWandoosOs(Character c)
        {
            // AUTOMATION ANDed with DECISIONS. Every return below this line is FEASIBILITY or
            // hysteresis — installed, a 10-minute cooldown, a 1.25x advantage floor — and not one of
            // them is permission, so with AdvisorWandoosOS on there was nothing a user could switch
            // off to stop this writing.
            //
            // ⚠ THE MOST EXPENSIVE OF THE THREE TO GET WRONG. Changing the OS WIPES wandoos levels;
            // CustomAllocation.cs:181-189 records that as a user-reported incident ("every advisor
            // reload re-applied the profile's wandoos breakpoint, and the OS change WIPES wandoos
            // levels - hours of progress gone"). An automation switch that does not actually stop it
            // is the version of this bug that costs a run.
            if (!Main.Settings.ManageWandoos) return;

            if (!c.wandoos98.installed && c.wandoos98.OSlevel <= 0) return;
            if ((DateTime.UtcNow - _lastOsSwitch).TotalMinutes < 10) return;

            // Project over the RUN's remaining length (short runs favor cheap fast OSs) and act on the
            // same >=1.25x threshold at which the advisor row turns red — the row and the auto agree.
            int horizon = WandoosAdvisor.RunHorizonMinutes();
            var v = WandoosAdvisor.Compare(horizon);
            if (!v.Known || v.BestOs == v.CurrentOs || v.Advantage < 1.25) return;

            // Current REAL bonus (with the levels we'd be throwing away) vs the projected horizon
            // on the better OS starting from zero: the switch must pay for itself within the run.
            double actualNow = c.wandoos98Controller.wandoosBonus();
            if (v.Cases[v.BestOs].Bonus < actualNow * 1.5) return;

            string from = v.CurrentName;
            c.wandoos98.changeOS((OSType)v.BestOs);

            // The advisor's claim on the OS. The profile's Wandoos breakpoints write it too, from a
            // different rule, so the pair reads Contested. Named here as the expensive one: changeOS
            // wipes the dump levels, and the row says so rather than leaving the cost to be discovered.
            WriteLedger.Record("wandoos.os.advisor", string.IsNullOrEmpty(v.BestName) ? "OS " + v.BestOs : v.BestName,
                $"projected {v.Advantage:0.0}x better than {from} over the rest of the run",
                ChallengeOverlay.Segment,
                $"Switched from {from} after a 10-minute cooldown and a 1.25x advantage floor",
                "Your profile's Wandoos breakpoints write this field too",
                "⚠ The switch wiped the Wandoos energy and magic dump levels — that is what it cost");
            _lastOsSwitch = DateTime.UtcNow;
            Main.Log($"Advisor: switched Wandoos OS {from} -> {v.BestName} (~{WandoosAdvisor.FmtX(v.Advantage)} better at your cap)");
        }
    }
}
