using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // Phase 2 of the native gear optimizer (route C3): the LIVE HALF of the search.
    //
    // NGU gear has NO set bonuses, so the objective is near-separable per slot; a coordinate-ascent over the
    // main slots plus greedy-fill + local-swap over accessories (the same heuristic the gear-optimizer uses
    // for accessories) reaches the optimum without the full Pareto machinery. The cube + nude base are fixed
    // and always included. Scoring uses GearScorer (validated against the website).
    //
    // ⚠ THE SEARCH ITSELF NOW LIVES IN GearSolver, and this file is the thin shim that reads the game and
    // calls it. That split is the entire reason GearSolver exists: everything below reaches Main.Character
    // or Main.InventoryController, and a file that does cannot link into tests/NGUAdvisor.Tests. Keep it
    // that way. Anything you are tempted to add to the search belongs on the other side of this boundary,
    // where it can have a test under it — this subsystem's whole defect history is silent wrong sets found
    // by live probes rather than by the suite.
    public static class GearOptimizer
    {
        // The REAL offhand contribution — the game's own InventoryController.weapon2Factor():
        // 0 while the second weapon slot is locked, else wish 28 + wish 45 progress capped at 1.
        // (Closes the last PLAN §4 gap: the hardcoded 100 over-valued the offhand.) Cached briefly —
        // scoring sweeps read this thousands of times per optimize pass.
        private static double _offhand = 100.0;
        private static DateTime _offhandAt = DateTime.MinValue;
        public static double OffhandPercent
        {
            get
            {
                if ((DateTime.UtcNow - _offhandAt).TotalSeconds > 30)
                {
                    _offhandAt = DateTime.UtcNow;
                    try { _offhand = Main.Character.inventoryController.weapon2Factor() * 100.0; }
                    catch { _offhand = 100.0; }
                }
                return _offhand;
            }
        }
        private static double Offhand => OffhandPercent;

        // Optimize for an objective and return the item IDs (for writing into a loadout / profile).
        // forceTopRespawn pins the single best Respawn item so the loadout always keeps some respawn;
        // `locks` pins named items (Gear Lock) and the remaining slots optimize around them.
        public static int[] OptimizeIds(GearObjectives.Objective obj, bool forceTopRespawn = false,
                                        GearLockSet locks = null)
            => Optimize(obj, forceTopRespawn, locks).AllIds().Where(x => x > 0).Distinct().ToArray();

        // Optimize for an objective by name (as stored in profiles/settings); null if unknown.
        // A plain objective first, then a named chain (GearChain.Presets), then a spelled one -- a
        // chain is an Objective, so every caller that resolves a name here can be handed one.
        public static GearObjectives.Objective FindObjective(string name) => GearChain.Find(name);

        // Worn against best under a CHAIN, step by step (GearChain.DecidingStep): the step that
        // decides at `bar`, or -1 when every step is inside it. Result.Score cannot answer this -- it
        // is the lead step's, and a later step takes accessories from the lead, so a set built for
        // the lead alone always out-scores the chain's own set on it.
        public static int ChainDecidingStep(GearChain.ChainObjective chain, GearSolver.Result best, double bar,
                                            out bool improves, out double worn, out double bestStep)
        {
            var wornScores = chain.Priorities.Select(p => CurrentScore(p.Objective)).ToArray();
            int k = GearChain.DecidingStep(wornScores, best.StepScores, bar, out improves);
            worn = k >= 0 ? wornScores[k] : 0;
            bestStep = k >= 0 ? best.StepScores[k] : 0;
            return k;
        }

        // Resolve the gear a mode should equip: if objectiveName is set (and valid), optimize live for it
        // (route C3 3.2) so the mode's gear stays optimal; otherwise fall back to the static loadout IDs.
        // MUST be called on the main thread (reads live inventory). Never throws; falls back on any error.
        public static int[] ResolveModeGear(string objectiveName, bool forceRespawn, int[] fallback,
                                            GearLockSet locks = null)
        {
            if (!string.IsNullOrEmpty(objectiveName))
            {
                var obj = FindObjective(objectiveName);
                if (obj == null)
                    Main.LogDebug($"Mode objective '{objectiveName}' not recognized; using static loadout.");
                else
                {
                    try
                    {
                        var best = Optimize(obj, forceRespawn, locks);
                        var ids = best.AllIds().Where(x => x > 0).Distinct().ToArray();
                        if (ids.Length > 0)
                        {
                            ReportLock(best);
                            string held = best.Lock != null && best.Lock.Applied > 0
                                ? $" (+{best.Lock.Applied} locked)" : "";
                            Main.Log($"Mode gear optimized for '{obj.Name}'{(forceRespawn ? " (+top respawn)" : "")}{held}: {ids.Length} items.");
                            return ids;
                        }
                    }
                    catch (Exception e) { Main.LogDebug($"Mode optimize '{objectiveName}' failed: {e.Message}; using static loadout."); }
                }
            }
            return fallback;
        }

        // Say what the Gear Lock could not honour. Silence when every locked item landed, and silence
        // on a repeat of the same complaint: this sits on paths that re-solve every couple of minutes,
        // and a line per solve is the same as no line at all. The latch clears itself the moment the
        // complaint changes, so unlocking the accessory slot that was over-locked reads as a new line.
        // "This result is safe to equip against a survival floor." Both terms are load-bearing.
        // Feasible alone is not enough: the respawn pin can rebuild the result without re-seating the
        // verdict, and can return -Infinity when a pin makes a feasible floor unreachable (both pinned
        // by GearSolverTests as KNOWN_HAZARD). A non-finite score means the search never actually
        // improved on its starting point, so whatever it is holding was not chosen — it was left there.
        private static bool Feasible(GearSolver.Result r)
            => r != null && r.Floors.Feasible && !double.IsNaN(r.Score) && !double.IsInfinity(r.Score);

        private static string _lastLockReport;
        public static void ReportLock(GearSolver.Result r)
        {
            var msg = r == null || r.Lock == null ? null : r.Lock.Message;
            if (msg == null) { _lastLockReport = null; return; }
            if (msg == _lastLockReport) return;
            _lastLockReport = msg;
            Main.Log(msg);
        }

        // Titan KILL gear. The user's TitanObjective (e.g. "Drop Chance") is a LOOT preference,
        // correct only while every targeted spawn auto-kills — on a REAL fight (spawning titan not
        // AK-able at its spawn version) it is the death loop (user-reported twice: empty loadout,
        // then drop gear on a live T6v2). Real fight -> force "Adventure" (Power + Toughness);
        // AK-trivial spawn -> honor the loot objective; nothing configured -> "Adventure".
        public static int[] ResolveTitanGear()
        {
            string obj = Main.Settings.TitanObjective;
            var fallback = Main.Settings.TitanLoadout;

            bool realFight = false;
            int requiredAcc = 0;
            int realIndex = -1;
            // The most any spawning target still needs from drop chance; not Known = it buys nothing.
            var lootNeed = new BoostFarmAdvisor.GearLootNeed();
            try
            {
                var targets = Main.Settings.TitanSwapTargets;
                for (int i = 0; i < ZoneHelpers.TitanZones.Length; i++)
                {
                    if (targets == null || i >= targets.Length || !targets[i]) continue;
                    if (!ZoneHelpers.TitanSpawningSoon(i)) continue;
                    var need = BoostFarmAdvisor.TitanGearLootFor(i);
                    if (need.Known && (!lootNeed.Known || need.Target > lootNeed.Target)) lootNeed = need;
                    if (!ZoneHelpers.AutokillAvailable(i))
                    {
                        realFight = true;
                        realIndex = i;
                        // The mechanic item for THIS titan, if it has one. Only on a real fight: once
                        // the spawn autokills there is no fight to lose and no reason to spend a slot.
                        requiredAcc = TitanTables.RequiredAccessoryFor(i);
                        break;
                    }
                }
            }
            catch { }

            if (realFight)
            {
                // Survival is a THRESHOLD, not a quantity: Power above what the kill needs buys nothing
                // and could have been Drop Chance. So rather than discarding the loot objective, keep it
                // and CONSTRAIN it — maximise loot subject to clearing the survival floor.
                //
                // ⚠ Falls back to plain "Adventure" (the old behaviour) unless the loot set is PROVEN
                // feasible against the floor. This is the path behind two reported death loops; loot is
                // only ever spent from surplus the solver has demonstrated, never assumed.
                // ⚠ THE PROVEN SET IS THE SET THAT GOES ON. Returning here rather than falling through
                // to ResolveModeGear is not a shortcut — ResolveModeGear re-solves WITHOUT the floors,
                // so proving feasibility and then equipping through it would equip the UNCONSTRAINED
                // loot optimum: a set that cleared nothing, chosen on the strength of a trial it never
                // used. That is the death loop with extra steps.
                string lootObj = obj;
                if (!string.IsNullOrEmpty(lootObj) && !string.Equals(lootObj, "Adventure", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string detail;
                        var floors = TitanFloorPlanner.SurvivalFloor(realIndex, ZoneHelpers.TitanVersion(realIndex), out detail);
                        var lootTarget = FindObjective(lootObj);
                        if (!floors.IsEmpty && lootTarget != null)
                        {
                            // ⚠ THE RESPAWN PIN AND A FLOOR DO NOT COMPOSE. GearSolverTests pins two
                            // hazards that need forceTopRespawn AND floors together: the pin re-runs the
                            // search at whatever phase the constrained solve ended in, so a pin that
                            // makes a feasible floor unreachable scores every candidate at -Infinity and
                            // takes the first one; and it rebuilds `r` without re-seating r.Floors, so a
                            // real verdict becomes a default one. Those tests were written against
                            // f1d65e9, where NO caller passed both — this call is the first, and it was
                            // already deployed before the hazard was found.
                            //
                            // Handled here rather than in the search because changing the pin changes
                            // every set it has ever chosen, and that needs its own in-game validation.
                            // Respawn is a PREFERENCE and survival is a REQUIREMENT, so the preference
                            // yields: try with the pin, and if that cannot be shown feasible, try again
                            // without it and say so.
                            bool wantRespawn = Main.Settings.TitanObjectiveRespawn;
                            var locks = GearLockSet.RequiredItem(requiredAcc);
                            var trial = Optimize(lootTarget, wantRespawn, locks, floors);
                            bool droppedRespawn = false;
                            if (wantRespawn && !Feasible(trial))
                            {
                                var noPin = Optimize(lootTarget, false, locks, floors);
                                if (Feasible(noPin)) { trial = noPin; droppedRespawn = true; }
                            }
                            var ids = trial == null ? null : trial.AllIds().Where(x => x > 0).Distinct().ToArray();
                            if (Feasible(trial) && ids.Length > 0)
                            {
                                ReportLock(trial);
                                // What the constraint actually costs, stated as loot kept rather than as
                                // a raw score: "how much of the drop gear survives the kill requirement"
                                // is the question being asked, and a bare score answers nothing.
                                string kept = "";
                                try
                                {
                                    var free = Optimize(lootTarget, Main.Settings.TitanObjectiveRespawn,
                                                        GearLockSet.RequiredItem(requiredAcc));
                                    if (free != null && free.Score > 0 && !double.IsInfinity(trial.Score))
                                        kept = $" — keeps {trial.Score / free.Score * 100:0.#}% of the " +
                                               $"'{lootObj}' you would get with no fight to survive";
                                }
                                catch { }
                                Main.Log($"Titan fight is live — '{lootObj}' fits inside the kill floor " +
                                         $"({detail}){kept}. The surplus above survival is spent on loot " +
                                         "instead of on stats the fight cannot use." +
                                         (droppedRespawn ? " Top-respawn was dropped: it could not be held " +
                                          "alongside the kill floor, and surviving outranks respawning." : ""));
                                return ids;
                            }
                            Main.Log($"Titan fight is live — '{lootObj}' cannot clear the kill floor ({detail}); " +
                                     "using the kill set instead.");
                        }
                    }
                    catch (Exception e) { Main.LogDebug($"Titan loot-under-floor: {e.Message}"); }
                }

                Main.Log("Titan fight is live (not AK) — kill set overrides the loot objective");
                obj = "Adventure";
                if (requiredAcc != 0)
                {
                    int lvl = ZoneHelpers.EquippedAccessoryLevel(requiredAcc);
                    if (lvl < 0)
                        Main.Log($"This fight REQUIRES item {requiredAcc} worn as an accessory — reserving a slot for it.");
                    else if (requiredAcc == TitanTables.ApathyRingId && lvl < TitanTables.ApathyFullLevel)
                        Main.Log($"Ring of Apathy is level {lvl}; below {TitanTables.ApathyFullLevel} UUG still " +
                                 "grows stronger every insult. Level it to stop the growth entirely.");
                }
            }
            else
            {
                var lootSet = AutokillLootSet(obj, fallback, lootNeed);
                if (lootSet != null) return lootSet;
                if (string.IsNullOrEmpty(obj) && (fallback == null || fallback.Length == 0))
                    obj = "Adventure";
            }
            // The mechanic item is a GEAR LOCK — the general form of the old `requireAccessoryId`
            // parameter — marked Required so it keeps beating the respawn pin exactly as it did before.
            return ResolveModeGear(obj, Main.Settings.TitanObjectiveRespawn, fallback,
                                   GearLockSet.RequiredItem(requiredAcc));
        }

        // GEAR FOR A TITAN THAT AUTO-KILLS: the loot stat belongs in the ACCESSORIES, not in the set.
        //
        // The autokill thresholds are live attack/defense reads, so a set that spends Power and
        // Toughness on loot can turn an auto-kill into a real fight in loot gear. Adventure therefore
        // owns weapons and armour (with the top-Power weapon held), and only accessories go to loot.
        //
        // Drop Chance is SIZED, not maximised. A titan roll is min(chance x dropChance, cap), so past
        // the point where every wanted roll caps, more of it buys nothing: the budget is walked down
        // while the set still reaches that point, and the slots handed back go to Adventure.
        //
        // Applies when the titan objective is a plain loot stat, or is unset with no item list --
        // then Drop Chance is used if (and only if) it still pays, which is the reason the advisor
        // attends an auto-killed titan at all. A chain, "Adventure" or an item list is left alone:
        // the user wrote those. Null = not handled here.
        private static int[] AutokillLootSet(string titanObjective, int[] fallback, BoostFarmAdvisor.GearLootNeed lootNeed)
        {
            try
            {
                string loot = titanObjective;
                if (string.IsNullOrEmpty(loot))
                {
                    if ((fallback != null && fallback.Length > 0) || !lootNeed.Pays) return null;
                    loot = GearObjectives.Stat.DropChance;
                }
                var lootObj = GearChain.FindObjective(loot);
                if (lootObj == null || string.Equals(lootObj.Name, "Adventure", StringComparison.OrdinalIgnoreCase)) return null;

                bool respawn = Main.Settings.TitanObjectiveRespawn;
                GearSolver.Result Run(string budget) => Optimize(
                    FindObjective($"Adventure(0)+PowerWeapon > {lootObj.Name}({budget}) > Adventure(all)"), respawn);

                var best = Run("all");
                if (best == null) return null;
                string sized = "every accessory";
                bool isDropChance = string.Equals(lootObj.Name, GearObjectives.Stat.DropChance, StringComparison.OrdinalIgnoreCase);
                // StepScores[1] is the loot step's objective over the whole set -- for Drop Chance,
                // the game's own gear factor, the same quantity lootNeed.Target is stated in.
                if (isDropChance && lootNeed.Known && best.StepScores.Length > 1 && best.StepScores[1] >= lootNeed.Target)
                {
                    int full = best.Accessories.Count;
                    for (int slots = full - 1; slots >= 0; slots--)
                    {
                        var run = Run(slots.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        if (run == null || run.StepScores.Length < 2 || run.StepScores[1] < lootNeed.Target) break;
                        best = run;
                        sized = $"{slots} of {full} accessories, which already caps every wanted drop";
                    }
                }

                var ids = best.AllIds().Where(x => x > 0).Distinct().ToArray();
                if (ids.Length == 0) return null;
                Main.Log($"Titan auto-kills — '{lootObj.Name}' on {sized}; weapons and armour stay on Adventure"
                       + $"{(respawn ? " (+top respawn)" : "")}: {ids.Length} items.");
                return ids;
            }
            catch (Exception e) { Main.LogDebug($"Titan auto-kill loot set: {e.Message}"); return null; }
        }

        // Gold gear resolution with a data-driven default: when the user configured NEITHER a gold
        // objective NOR a static gold loadout, optimize live for "Gold Drops" instead of doing nothing —
        // the optimizer knows the inventory better than a hand-picked list.
        public static int[] ResolveGoldGear()
        {
            string obj = Main.Settings.GoldObjective;
            var fallback = Main.Settings.GoldDropLoadout;
            if (string.IsNullOrEmpty(obj) && (fallback == null || fallback.Length == 0))
                obj = "Gold Drops";
            return ResolveModeGear(obj, Main.Settings.GoldObjectiveRespawn, fallback);
        }

        // Quest gear. Everything the Quest loadout card can say is honoured exactly as before -- an
        // item list, a plain objective, a chain -- with one addition: the "Quest Drop Rate" entry,
        // which solves for the most quest items per second in the zone the current quest rolled
        // (QuestGearSet has the rule and the reasoning).
        public static int[] ResolveQuestGear()
        {
            var s = Main.Settings;
            if (!string.Equals(s.QuestObjective, QuestGearSet.ObjectiveName, StringComparison.OrdinalIgnoreCase))
                return ResolveModeGear(s.QuestObjective, s.QuestObjectiveRespawn, s.QuestLoadout);

            int zone = -1;
            try
            {
                var c = Main.Character;
                if (c.beastQuest.inQuest) zone = c.beastQuestController.curQuestZone();
            }
            catch { }

            try
            {
                var inputs = LiveInputs();
                QuestGearSet.Fight fight;
                if (zone >= 0 && TryReadQuestFight(zone, s.QuestCombatMode, out fight))
                {
                    var choice = QuestGearSet.Solve(inputs, fight, s.QuestObjectiveRespawn);
                    var ids = choice == null ? null : choice.Set.AllIds().Where(x => x > 0).Distinct().ToArray();
                    if (ids != null && ids.Length > 0)
                    {
                        // questDropChance() already includes the worn gear's factor; dividing it out
                        // leaves the per-kill chance at a gear factor of 1.
                        double perHour = 0;
                        try
                        {
                            double wornQd = CurrentScore(FindObjective(GearObjectives.Stat.QuestDrops));
                            if (wornQd > 0) perHour = Main.Character.beastQuestController.questDropChance() / wornQd * 3600.0;
                        }
                        catch { }
                        Main.Log($"Quest gear for zone {zone}: {choice.Shape} — quest drops x{choice.QuestDrops:0.##}, "
                               + $"{choice.KillsPerSecond * 3600.0:0} kills/h"
                               + (perHour > 0 ? $", ~{choice.Rate * perHour:0} items/h (plain Adventure set ~{choice.AdventureRate * perHour:0}/h)" : "")
                               + (choice.OneShotsEverySpawn ? "." : " — the zone is not a one-shot in full Adventure gear, so the kill set is kept."));
                        return ids;
                    }
                }

                // No quest running yet, or the zone could not be read: the kill-safe shape without the
                // sizing -- weapons and armour on Adventure, every accessory on Quest Drops.
                var safe = GearSolver.Solve(inputs, FindObjective("Adventure(0)+PowerWeapon > Quest Drops(all)"),
                                            s.QuestObjectiveRespawn);
                var safeIds = safe == null ? new int[0] : safe.AllIds().Where(x => x > 0).Distinct().ToArray();
                if (safeIds.Length > 0)
                {
                    Main.Log($"Quest gear: no readable quest zone — kill-safe Quest Drops set ({safeIds.Length} items).");
                    return safeIds;
                }
            }
            catch (Exception e) { Main.LogDebug($"Quest drop-rate set failed: {e.Message}"); }
            return s.QuestLoadout ?? new int[0];
        }

        // The quest zone's spawn table and the character's swing, read live. AdventureController
        // builds enemyList in code and spawnEnemy() picks uniformly from enemyList[zone], so the table
        // is exact and never goes stale on a rebalance.
        private static bool TryReadQuestFight(int zone, int combatMode, out QuestGearSet.Fight fight)
        {
            fight = new QuestGearSet.Fight();
            try
            {
                var c = Main.Character;
                var table = c.adventureController.enemyList;
                if (table == null || zone >= table.Count || table[zone] == null || table[zone].Count == 0) return false;

                int[] blacklist = Main.Settings.BlacklistedBosses;
                bool bossOnly = Main.Settings.SnipeBossOnly && !ZoneHelpers.ZoneIsTitan(zone);
                var enemies = new List<QuestGearSet.Enemy>();
                foreach (var e in table[zone])
                {
                    if (e == null) continue;
                    enemies.Add(new QuestGearSet.Enemy
                    {
                        MaxHP = e.maxHP,
                        Defense = e.defense,
                        Skipped = (blacklist != null && Array.IndexOf(blacklist, e.spriteID) >= 0)
                               || (bossOnly && e.enemyType != enemyType.boss),
                    });
                }
                if (enemies.Count == 0) return false;

                var power = FindObjective("Power");
                var respawn = FindObjective(GearObjectives.Stat.Respawn);
                double wornPower = CurrentScore(power), wornRespawn = CurrentScore(respawn);
                if (wornPower <= 0 || wornRespawn >= 1.0) return false;

                // Idle swings on Adventure.attackSpeed and applies only idleAttackPower(); a manual mode
                // shares the global cooldown and applies regAttackPower() -- and, in every mode but
                // regular-attack-only (4), the offensive buffs the combat loop keeps up.
                bool idle = combatMode == 0 || !CombatHelpers.RegularAttackUnlocked();
                double multiplier;
                if (idle) multiplier = c.idleAttackPower();
                else
                {
                    multiplier = c.regAttackPower();
                    if (combatMode <= 3)
                        multiplier *= Math.Max(1.0, c.adventureController.playerController.offenseBuffFactor);
                }
                double swing = idle ? c.adventure.attackSpeed : CombatHelpers.BaseGlobalCooldown();

                fight.Enemies = enemies.ToArray();
                fight.Idle = idle;
                fight.SwingSeconds = swing > 0 ? swing : 1.0;
                fight.AttackMultiplier = multiplier > 0 ? multiplier : 1.0;
                fight.AttackPerPower = c.totalAdvAttack() / wornPower;
                fight.RespawnWithoutGear = CombatHelpers.BaseRespawnTime() / (1.0 - wornRespawn);
                return true;
            }
            catch (Exception e) { Main.LogDebug($"Quest fight read (zone {zone}): {e.Message}"); return false; }
        }

        // Optimize and equip live. MUST be called on the main thread (equipping touches the game/UI).
        public static void OptimizeAndEquip(GearObjectives.Objective obj, bool forceTopRespawn = false)
        {
            if (obj == null) return;
            var ids = OptimizeIds(obj, forceTopRespawn);
            if (ids.Length > 0)
                LoadoutManager.ChangeGear(ids);
        }

        // Score the CURRENTLY-equipped loadout for an objective (same scoring the optimizer uses), so callers
        // can compare "how good is my gear now" vs Optimize().Score. Read-only; main thread. 0 on failure.
        public static double CurrentScore(GearObjectives.Objective obj)
        {
            try
            {
                var inv = Main.Character.inventory;
                var ic = Main.InventoryController;
                var list = new List<GearScorer.Item>(16);
                void Add(Equipment e) { if (e != null && e.id != 0) list.Add(GameGearAdapter.BuildItem(e, e.type == part.Weapon)); }
                Add(inv.weapon);
                if (ic.weapon2Unlocked()) Add(inv.weapon2);
                Add(inv.head); Add(inv.chest); Add(inv.legs); Add(inv.boots);
                if (inv.accs != null) foreach (var a in inv.accs) Add(a);
                list.Add(GameGearAdapter.BuildCubeItem());
                list.Add(GameGearAdapter.BuildBaseItem());
                return GearScorer.ScoreRaw(list, obj.Stats, obj.Exponents, Offhand);
            }
            catch (Exception e) { Main.LogDebug($"CurrentScore failed: {e.Message}"); return 0; }
        }

        // Optimize for an objective. `locks` and `floors` are both OPTIONAL and null is the whole of
        // the old behaviour.
        //
        // ⚠ THIS IS THE LIVE SHIM, NOT THE SEARCH. Every read below is a game read; the search is
        // GearSolver.Solve and takes all of it as plain-old data. Split for one reason: Main.Character
        // and Main.InventoryController are static welds, so as long as the search sat in this file it
        // could not link into the test assembly and had no tests at all. If you need to change what
        // the solver DOES, change GearSolver; if you need to change what it is TOLD, change here.
        //
        // MUST be called on the main thread — BuildPools, GameGearAdapter and OffhandPercent all read
        // live game state.
        public static GearSolver.Result Optimize(GearObjectives.Objective obj, bool forceTopRespawn = false,
                                                 GearLockSet locks = null, GearFloorSet floors = null)
        {
            return GearSolver.Solve(LiveInputs(), obj, forceTopRespawn, locks, floors);
        }

        // Everything the search reads off the game, as the plain-old data GearSolver takes. MUST be
        // called on the main thread.
        private static GearSolver.Inputs LiveInputs()
        {
            var idToItem = new Dictionary<int, GearScorer.Item>();
            var pools = BuildPools(idToItem);
            var ic = Main.InventoryController;

            return new GearSolver.Inputs
            {
                Pools = pools,
                IdToItem = idToItem,
                Cube = GameGearAdapter.BuildCubeItem(),
                BaseItem = GameGearAdapter.BuildBaseItem(),
                TwoWeapons = ic.weapon2Unlocked(),
                AccessorySlots = Math.Max(0, ic.accessorySpaces()),
                // Read ONCE per solve. The property caches for 30s, so a solve already saw a single
                // value; making that explicit is also the only self-consistent way to score a
                // comparison — two candidates scored at different offhand factors are not comparable.
                OffhandPercent = Offhand,
                // The ownership half of the Gear Lock. Bound to THIS solve's idToItem deliberately:
                // "owned" has to mean "the solver can actually seat it", and two inventory walks that
                // nearly agree is how this subsystem has produced silent wrong answers.
                Lookup = id => LookUp(id, idToItem)
            };
        }

        // The live half of the Gear Lock catalog: is this id a real wearable item, and do you have one?
        //
        // ⚠ VERIFIED AGAINST THE DECOMP RATHER THAN THE NAME. `itemInfo.type` is a `part[600]`
        // ([DECOMP] ItemNameDesc.cs:16) whose entries are assigned in code by constructItemInfo()
        // ([DECOMP] ItemNameDesc.cs:92); every index 0..514 is assigned explicitly and there are no
        // gaps, which is why Consts.MAX_GEAR_ID = 514 is a sound upper bound. That matters: the array
        // is 600 long and `default(part)` is part.Head, so an UNBOUNDED read of an undefined id would
        // come back "a valid head", and a typo'd lock would silently hold the head slot with nothing
        // in it. The bound is load-bearing, not decoration.
        //
        // Ownership comes from `idToItem`, which BuildPools filled from inventory + daycare-free bag +
        // currently-equipped — i.e. exactly the candidate set the solver can choose from. Deliberately
        // NOT from a separate inventory walk: "owned" has to mean "the solver can actually seat it",
        // and two walks that nearly agree is how this subsystem has produced silent wrong answers.
        private static GearLockItem LookUp(int id, Dictionary<int, GearScorer.Item> idToItem)
        {
            part pt;
            try
            {
                if (id <= 0 || id > Consts.MAX_GEAR_ID) return GearLockItem.Missing();
                pt = Main.Character.itemInfo.type[id];
            }
            catch { return GearLockItem.Missing(); }

            GearLockSlot slot;
            if (!TryWearableSlot(pt, out slot)) return GearLockItem.Missing();

            string name;
            try { name = Main.ItemNameNice(id); } catch { name = ""; }
            return idToItem.ContainsKey(id)
                ? GearLockItem.Have(slot, name)
                : GearLockItem.NotOwned(slot, name);
        }

        // THE UNITY BOUNDARY, in one function. `part` is an Assembly-CSharp enum, so it cannot appear
        // in GearSolver (which has to compile in the test assembly); GearLockSlot is the Unity-free
        // enum with exactly the six WEARABLE members. Translating here means the solver never sees a
        // game type, and both callers below get the same answer to "can this be worn at all".
        //
        // Boosts, Misc, MacGuffins and None are real ids that cannot be WORN, and false is the honest
        // answer for them: a lock on a boost id has no slot to take, and a boost in the bag is not a
        // candidate for any slot.
        private static bool TryWearableSlot(part pt, out GearLockSlot slot)
        {
            switch (pt)
            {
                case part.Weapon: slot = GearLockSlot.Weapon; return true;
                case part.Head: slot = GearLockSlot.Head; return true;
                case part.Chest: slot = GearLockSlot.Chest; return true;
                case part.Legs: slot = GearLockSlot.Legs; return true;
                case part.Boots: slot = GearLockSlot.Boots; return true;
                case part.Accessory: slot = GearLockSlot.Accessory; return true;
                default: slot = GearLockSlot.Weapon; return false;   // never read; `false` is the answer
            }
        }

        // Build candidate pools by slot from inventory + currently-equipped, deduped by item id.
        //
        // ⚠ INSERTION ORDER IS LOAD-BEARING. The respawn pin iterates these pools and its tie-break
        // ("highest respawn wins outright; loadout score only breaks respawn ties") reads whichever
        // equal-respawn candidate it meets first. A Dictionary with no removals enumerates in insertion
        // order, and the insertion order here is equipped-then-bag. Do not sort, and do not pre-create
        // the six buckets.
        private static Dictionary<GearLockSlot, List<KeyValuePair<int, GearScorer.Item>>> BuildPools(Dictionary<int, GearScorer.Item> idToItem)
        {
            var inv = Main.Character.inventory;
            var ic = Main.InventoryController;
            var pools = new Dictionary<GearLockSlot, List<KeyValuePair<int, GearScorer.Item>>>();

            void Consider(Equipment e)
            {
                if (e == null || e.id == 0 || idToItem.ContainsKey(e.id)) return;
                var pt = e.type;
                GearLockSlot slot;
                if (!TryWearableSlot(pt, out slot)) return;
                var item = GameGearAdapter.BuildItem(e, pt == part.Weapon);
                idToItem[e.id] = item;
                if (!pools.TryGetValue(slot, out var list))
                {
                    list = new List<KeyValuePair<int, GearScorer.Item>>();
                    pools[slot] = list;
                }
                list.Add(new KeyValuePair<int, GearScorer.Item>(e.id, item));
            }

            Consider(inv.weapon);
            if (ic.weapon2Unlocked()) Consider(inv.weapon2);
            Consider(inv.head); Consider(inv.chest); Consider(inv.legs); Consider(inv.boots);
            if (inv.accs != null) foreach (var a in inv.accs) Consider(a);
            if (inv.inventory != null) foreach (var e in inv.inventory) Consider(e);
            return pools;
        }
    }
}
