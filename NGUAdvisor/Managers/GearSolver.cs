using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // THE GEAR SEARCH, WITH THE GAME TAKEN OUT OF IT.
    //
    // This is GearOptimizer.Optimize's body, verbatim, with the seven values it used to close over
    // hoisted into an explicit Inputs bag. Nothing about the search changed — the split is the whole
    // point of the file.
    //
    // WHY. Main.Character and Main.InventoryController are static welds: a file that touches either
    // cannot link into tests/NGUAdvisor.Tests, so for as long as the search lived inside GearOptimizer
    // it had ZERO tests over it while ~1979 pure-function tests ran green beside it. Three defects
    // shipped past that green suite in a single session, all on the far side of the weld. The clearest:
    // when a GearFloorSet had no feasible solution, phase 1 scored EVERY candidate at negative infinity,
    // so PickSlot could never improve on its starting point and Optimize returned Score = -Infinity —
    // a garbage number handed to callers that do arithmetic on it (AdvisorApply divides scores to
    // report "+x% from new drops"). A live probe found it. The suite structurally could not.
    //
    // Same shape as the AdventureFloor / AdventureFloorReader split, and for the same stated reason:
    // the arithmetic is separated from the live read precisely so it can be linked, because every bug
    // this subsystem has shipped has been SILENT — no throw, nothing odd-looking, just a worse set
    // equipped.
    //
    // THE UNITY BOUNDARY IS THE `part` ENUM. `part` is an Assembly-CSharp type and cannot appear here,
    // so the candidate pools are keyed by GearLockSlot — the Unity-free enum GearLock already defines,
    // whose six members are exactly the six wearable parts. GearOptimizer.BuildPools does the mapping
    // at insertion time, so pool ENUMERATION ORDER is unchanged (Dictionary with no removals enumerates
    // in insertion order), which matters: the respawn pin iterates the pools and its tie-break is
    // order-sensitive.
    public static class GearSolver
    {
        public class Result
        {
            public int MainWeapon, OffWeapon, Head, Chest, Legs, Boots;
            public readonly List<int> Accessories = new List<int>();
            public double Score;
            // Only meaningful when the solve carried floors; Feasible stays false-by-default and unread
            // otherwise. Infeasible is a REPORTABLE state, which is the entire advantage over a weighted
            // blend: an unreachable requirement is something you get told about.
            public FloorVerdict Floors;
            // The Gear Lock as it was actually applied — which id took which slot, and every id that
            // could not. Null when the solve carried no locks. Same argument as Floors: a lock the
            // solver dropped in silence is a set the user never asked for.
            public GearLockPlan Lock;
            // Each chain step's objective scored over the WHOLE final set, in step order; one entry
            // for a plain objective. Score alone cannot compare two sets under a chain: it is the lead
            // step's, and a later step takes accessories from the lead (GearChain.DecidingStep).
            public double[] StepScores = new double[0];
            public IEnumerable<int> AllIds()
            {
                if (MainWeapon != 0) yield return MainWeapon;
                if (OffWeapon != 0) yield return OffWeapon;
                if (Head != 0) yield return Head;
                if (Chest != 0) yield return Chest;
                if (Legs != 0) yield return Legs;
                if (Boots != 0) yield return Boots;
                foreach (var a in Accessories) yield return a;
            }
        }

        // Everything the search used to read off the game, as plain-old data. Built by
        // GearOptimizer.Optimize from the live inventory; built by hand in tests.
        public struct Inputs
        {
            // Candidate items per wearable slot, deduped by id. Enumeration order is the order
            // GearOptimizer.BuildPools met them (equipped first, then the bag) and is load-bearing for
            // the respawn pin's tie-break — do not sort it.
            public Dictionary<GearLockSlot, List<KeyValuePair<int, GearScorer.Item>>> Pools;
            // The same items by id. This is also the OWNERSHIP set the Gear Lock resolves against:
            // "owned" has to mean "the solver can actually seat it", and two walks that nearly agree is
            // how this subsystem has produced silent wrong answers.
            public Dictionary<int, GearScorer.Item> IdToItem;
            // The two fixed pseudo-items present in every candidate set: the Infinity Cube, and the
            // character's NUDE adventure base. Neither is a slot; both enter the score.
            public GearScorer.Item Cube;
            public GearScorer.Item BaseItem;
            public bool TwoWeapons;            // InventoryController.weapon2Unlocked()
            public int AccessorySlots;         // InventoryController.accessorySpaces()
            // InventoryController.weapon2Factor() x 100 ([DECOMP] InventoryController.cs:687 — 0 while
            // wish 28 is unlearned, else wish 28 + wish 45 progress capped at 1). Read ONCE per solve
            // rather than per score: the live property already caches for 30s, so a solve saw one value
            // anyway, and one value per solve is the only self-consistent way to score a comparison.
            public double OffhandPercent;
            // The live half of the Gear Lock catalog: is this id a real wearable item, and do you have
            // one? Null is fine when no locks are asked for.
            public Func<int, GearLockItem> Lookup;
        }

        // `locks` and `floors` are both OPTIONAL and null is the whole of the old behaviour. With
        // neither, this method computes exactly what it computed before — same search, same score,
        // same result — which is the safety property that lets a constrained mode land on the live
        // equip path at all.
        //
        // The two constraints are SIBLINGS and compose: a lock fixes a SLOT, a floor constrains a
        // TOTAL. A lock is applied first (it seeds the incumbent), then the floor phases run over
        // whatever is left free, so a locked set that cannot reach a floor comes back INFEASIBLE with
        // the shortfall named and the number of held slots said out loud — the same reportable state
        // an unreachable floor already produced, plus the reason it may be unreachable.
        //
        // A GearChain.ChainObjective runs its steps in order; anything else is a one-step chain and
        // takes exactly the path it always took.
        public static Result Solve(Inputs inp, GearObjectives.Objective target, bool forceTopRespawn = false,
                                   GearLockSet locks = null, GearFloorSet floors = null)
        {
            var steps = GearChain.StepsOf(target);
            if (steps.Count == 0) return new Result();
            bool chainMode = target is GearChain.ChainObjective;
            // The step being optimized. ScoreOf reads it, so the search below never has to know
            // whether it is running a chain.
            var obj = steps[0].Objective;
            var idToItem = inp.IdToItem ?? new Dictionary<int, GearScorer.Item>();
            var pools = inp.Pools ?? new Dictionary<GearLockSlot, List<KeyValuePair<int, GearScorer.Item>>>();
            var cube = inp.Cube;
            var baseItem = inp.BaseItem;
            bool twoWeapons = inp.TwoWeapons;
            int accSlots = Math.Max(0, inp.AccessorySlots);
            double offhand = inp.OffhandPercent;

            List<KeyValuePair<int, GearScorer.Item>> Pool(GearLockSlot p) =>
                pools.TryGetValue(p, out var l) ? l : new List<KeyValuePair<int, GearScorer.Item>>();

            var weapons = Pool(GearLockSlot.Weapon);
            var heads = Pool(GearLockSlot.Head);
            var chests = Pool(GearLockSlot.Chest);
            var legs = Pool(GearLockSlot.Legs);
            var boots = Pool(GearLockSlot.Boots);
            var accPool = Pool(GearLockSlot.Accessory);

            var r = new Result();

            // The farm sets' weapon pin (GearPriority.PinTopPowerWeapon): the single highest-Power
            // weapon in the pool, held in the main hand for the whole solve. Resolved ONCE -- it does
            // not depend on the chain's progress, and the respawn pass re-runs the chain per candidate.
            int powerWeaponPin = 0;
            if (steps.Any(p => p.PinTopPowerWeapon))
            {
                double bestPower = 0;
                foreach (var w in weapons)
                    if (w.Value.Stats.TryGetValue(GearObjectives.Stat.Power, out double pw) && pw > bestPower)
                    {
                        bestPower = pw;
                        powerWeaponPin = w.Key;
                    }
            }

            // ── GEAR LOCK ──────────────────────────────────────────────────────────────────────────
            // Resolve the user's named items against THIS inventory and THESE slot counts. Re-resolved
            // on every solve and never cached, which is what makes a changing accessorySpaces() a
            // non-event: an item that did not fit before fits the moment the slot unlocks, and one
            // that stops fitting is reported rather than silently displacing something.
            var cap = GearLockCapacity.Of(twoWeapons, accSlots);
            GearLockPlan lockPlan = null;
            if (locks != null && !locks.IsEmpty)
            {
                lockPlan = GearLockPlan.Resolve(locks.Ids, inp.Lookup, cap);
                r.Lock = lockPlan;
            }

            // Slots held against the ascent — by a Gear Lock, or by the respawn pin. ONE mechanism:
            // before this there were two ad-hoc pins writing the same decision, which is the shape of
            // defect this codebase has paid for repeatedly.
            bool holdWeapon = false, holdOff = false, holdHead = false,
                 holdChest = false, holdLegs = false, holdBoots = false;
            int holdAccessories = 0;

            bool Pinned(GearLockSlot p)
            {
                switch (p)
                {
                    case GearLockSlot.Weapon: return holdWeapon;
                    case GearLockSlot.Head: return holdHead;
                    case GearLockSlot.Chest: return holdChest;
                    case GearLockSlot.Legs: return holdLegs;
                    case GearLockSlot.Boots: return holdBoots;
                    case GearLockSlot.Accessory: return holdAccessories > 0;
                    default: return false;
                }
            }

            // Seat the locked items into a FRESH result. Called again after every `r = new Result()`,
            // which is the bug the old required-accessory path worked around by returning early: the
            // respawn pass rebuilds `r` from scratch, and anything not re-seated here is silently lost.
            void SeedLocks(bool swapWeapons)
            {
                holdWeapon = holdOff = holdHead = holdChest = holdLegs = holdBoots = false;
                holdAccessories = 0;
                if (lockPlan == null) { SeedPowerWeapon(); return; }
                if (lockPlan.Weapons.Count > 0)
                {
                    int a = lockPlan.Weapons[0];
                    int b = lockPlan.Weapons.Count > 1 ? lockPlan.Weapons[1] : 0;
                    if (swapWeapons && b != 0) { int t = a; a = b; b = t; }
                    r.MainWeapon = a; holdWeapon = true;
                    if (b != 0) { r.OffWeapon = b; holdOff = true; }
                }
                if (lockPlan.Head != 0) { r.Head = lockPlan.Head; holdHead = true; }
                if (lockPlan.Chest != 0) { r.Chest = lockPlan.Chest; holdChest = true; }
                if (lockPlan.Legs != 0) { r.Legs = lockPlan.Legs; holdLegs = true; }
                if (lockPlan.Boots != 0) { r.Boots = lockPlan.Boots; holdBoots = true; }
                foreach (var a in lockPlan.Accessories) { r.Accessories.Add(a); holdAccessories++; }
                SeedPowerWeapon();
            }

            // After the locks, so a locked weapon keeps the main hand: an explicit lock outranks a
            // preset's pin. A pin with no free seat is simply not seated.
            void SeedPowerWeapon()
            {
                if (powerWeaponPin == 0 || r.MainWeapon == powerWeaponPin || r.OffWeapon == powerWeaponPin) return;
                if (!holdWeapon) { r.MainWeapon = powerWeaponPin; holdWeapon = true; }
                else if (twoWeapons && !holdOff) { r.OffWeapon = powerWeaponPin; holdOff = true; }
            }

            List<GearScorer.Item> WornList()
            {
                var list = new List<GearScorer.Item>(16);
                void AddId(int id) { if (id != 0 && idToItem.TryGetValue(id, out var it)) list.Add(it); }
                AddId(r.MainWeapon); AddId(r.OffWeapon);
                AddId(r.Head); AddId(r.Chest); AddId(r.Legs); AddId(r.Boots);
                foreach (var a in r.Accessories) AddId(a);
                list.Add(cube); list.Add(baseItem);
                return list;
            }

            // The floor stats for the set as it currently stands. Read separately from the objective's
            // stats on purpose: a floor may name something the objective does not score at all, which
            // is the entire point of having one.
            var floorNames = (floors != null && !floors.IsEmpty)
                ? floors.Floors.Select(f => f.Stat).Distinct().ToArray()
                : new string[0];
            Dictionary<string, double> FloorStats()
            {
                var bag = new Dictionary<string, double>();
                if (floorNames.Length == 0) return bag;
                var vals = GearScorer.GetRawVals(WornList(), floorNames, offhand);
                for (int i = 0; i < floorNames.Length; i++) bag[floorNames[i]] = vals[i];
                return bag;
            }

            // 0 = drive toward feasible, 1 = maximise the objective without leaving feasible.
            // THE SEARCH IS UNTOUCHED. MainAscent/AccessoryOptimize/PickSlot only ever ask ScoreOf(),
            // so a constrained solve is a change to what "better" MEANS, not to how it is searched —
            // which is why this can be added to a live equip path without re-testing the solver.
            int phase = 1;

            double ScoreOf()
            {
                var list = WornList();
                double objective = GearScorer.ScoreRaw(list, obj.Stats, obj.Exponents, offhand);
                if (floorNames.Length == 0) return objective;

                // Phase 2: the floor turned out to be unreachable, so it stops constraining anything and
                // this is the plain objective again. Never entered while a feasible set exists.
                if (phase == 2) return objective;

                var bag = FloorStats();
                if (phase == 0)
                    // Maximising the negated shortfall walks toward feasibility. Normalised per floor,
                    // so a stat in the billions does not drown one measured in percent.
                    return -floors.RelativeShortfall(bag);

                // Phase 1: an infeasible set is not "worse", it is not a candidate. PickSlot starts from
                // the incumbent — which phase 0 left feasible — so negative infinity can never win and
                // the ascent physically cannot wander back out of the feasible region.
                return floors.AllMet(bag) ? objective : double.NegativeInfinity;
            }

            // Re-pick the single best item for one slot, given everything else fixed.
            bool PickSlot(IEnumerable<KeyValuePair<int, GearScorer.Item>> pool, Func<int> get, Action<int> set)
            {
                int start = get(); int best = start; double bs = ScoreOf();
                foreach (var c in pool)
                {
                    set(c.Key); double s = ScoreOf();
                    if (s > bs) { bs = s; best = c.Key; }
                }
                set(best);
                return best != start;
            }

            void MainAscent()
            {
                for (int iter = 0; iter < 8; iter++)
                {
                    bool changed = false;
                    if (!Pinned(GearLockSlot.Weapon))
                        changed |= PickSlot(weapons.Where(w => w.Key != r.OffWeapon), () => r.MainWeapon, v => r.MainWeapon = v);
                    // holdOff is its own flag rather than Pinned(GearLockSlot.Weapon): the two weapon
                    // slots are ONE slot KIND but two independent seats, so a single locked weapon must
                    // leave the other seat free to be optimized.
                    if (twoWeapons && !holdOff)
                        changed |= PickSlot(weapons.Where(w => w.Key != r.MainWeapon), () => r.OffWeapon, v => r.OffWeapon = v);
                    if (!Pinned(GearLockSlot.Head)) changed |= PickSlot(heads, () => r.Head, v => r.Head = v);
                    if (!Pinned(GearLockSlot.Chest)) changed |= PickSlot(chests, () => r.Chest, v => r.Chest = v);
                    if (!Pinned(GearLockSlot.Legs)) changed |= PickSlot(legs, () => r.Legs, v => r.Legs = v);
                    if (!Pinned(GearLockSlot.Boots)) changed |= PickSlot(boots, () => r.Boots, v => r.Boots = v);
                    if (!changed) break;
                }
            }

            // The accessory seats the step being optimized owns: [accFirst, accCap). For a plain
            // objective that is every seat past the held ones, which is what this always searched.
            int accFirst = 0, accCap = 0;

            void AccessoryOptimize()
            {
                if (accCap <= 0 || accPool.Count == 0) return;
                // Held accessories — locked items and/or a pinned respawn item — occupy the FRONT of
                // the list and are never swapped out. The count is what changed when Gear Lock landed:
                // it used to be 0-or-1, and a lock can hold every accessory slot there is. The greedy
                // fill below stops on its own in that case (Count is already accSlots), and the swap
                // loop starts past the end, so a fully-locked accessory set is a no-op rather than an
                // error — which is the degenerate case the feature has to survive.
                int fixedCount = accFirst;
                // Greedy fill. Each accessory id is used at most once BY DESIGN: NGU only lets one copy of a
                // given accessory be equipped at a time, even if you own duplicates. So this uniqueness guard
                // (and the id-dedup in BuildPools) enforces a real game rule — it is NOT an optimizer limitation.
                while (r.Accessories.Count < accCap)
                {
                    int best = 0; double bs = ScoreOf();
                    foreach (var c in accPool)
                    {
                        if (r.Accessories.Contains(c.Key)) continue;   // one copy per accessory id (game rule)
                        r.Accessories.Add(c.Key); double s = ScoreOf(); r.Accessories.RemoveAt(r.Accessories.Count - 1);
                        if (s > bs) { bs = s; best = c.Key; }
                    }
                    if (best == 0) break; // nothing improves
                    r.Accessories.Add(best);
                }
                // local swap
                for (int iter = 0; iter < 50; iter++)
                {
                    bool improved = false;
                    for (int i = fixedCount; i < r.Accessories.Count && i < accCap; i++)
                    {
                        int cur = r.Accessories[i]; int best = cur; double bs = ScoreOf();
                        foreach (var c in accPool)
                        {
                            if (c.Key == cur || r.Accessories.Contains(c.Key)) continue;
                            r.Accessories[i] = c.Key; double s = ScoreOf();
                            if (s > bs) { bs = s; best = c.Key; }
                        }
                        r.Accessories[i] = best;
                        if (best != cur) improved = true;
                    }
                    if (!improved) break;
                }
            }

            // Two phases when floors are present, one when they are not. Phase 0 walks to a feasible
            // set; phase 1 then maximises the objective without ever leaving it. Split like this rather
            // than folded into a single penalty because a penalty is a weight in disguise, and the
            // whole reason for floors is that a weight cannot say "this is required".
            double RunConstrained()
            {
                if (floorNames.Length == 0) return RunOptimize();

                phase = 0;
                RunOptimize();
                bool feasible = floors.AllMet(FloorStats());
                if (!feasible)
                {
                    // Honest about the limit: this solver is coordinate ascent, so "could not reach it"
                    // is not the same as "no such set exists". The message blames the floor rather than
                    // the objective either way, because that is the actionable half.
                    //
                    // The held-slot count rides along because a Gear Lock is a plausible CAUSE of the
                    // infeasibility and the operator cannot see it from the floor alone: lock four
                    // accessories to a doll set and the survival floor that was reachable yesterday is
                    // not reachable today, and "the floor is out of reach" on its own points at the
                    // wrong thing to change.
                    r.Floors = FloorVerdict.Infeasible(floors.Unmet(FloorStats()), FloorStats(),
                                                       lockPlan == null ? 0 : lockPlan.SlotsHeld);

                    // ⚠ FALL BACK TO THE UNCONSTRAINED ANSWER, phase 2, NOT phase 1. Live probe caught
                    // this: with no feasible set anywhere, phase 1 scores EVERY candidate at negative
                    // infinity, so PickSlot can never improve on its starting point and Score comes back
                    // -Infinity. That is a garbage number handed to callers that do arithmetic on it —
                    // AdvisorApply divides scores to report "+x% from new drops".
                    //
                    // An unreachable floor provides no guidance, so the least surprising thing to return
                    // is the set you would have had without it, plus a loud verdict. Returning a
                    // half-satisfied set would satisfy neither the floor nor the objective.
                    phase = 2;
                    return RunOptimize();
                }

                phase = 1;
                double sc = RunOptimize();
                r.Floors = FloorVerdict.Ok(0);
                return sc;
            }

            // Does this objective score anything a FREE main seat could wear? A step that does not
            // (Respawn, Drop Chance) rates every helmet the same as no helmet, so letting it own the
            // main slots means leaving them empty -- and the equipper keeps whatever was worn in a
            // slot it is not handed.
            bool HasMainOpinion(GearObjectives.Objective o)
            {
                bool Scores(List<KeyValuePair<int, GearScorer.Item>> pool)
                {
                    foreach (var c in pool)
                        foreach (var stat in o.Stats)
                            if (c.Value.Stats.TryGetValue(stat, out double v) && v > 0) return true;
                    return false;
                }
                return ((!holdWeapon || (twoWeapons && !holdOff)) && Scores(weapons))
                    || (!holdHead && Scores(heads)) || (!holdChest && Scores(chests))
                    || (!holdLegs && Scores(legs)) || (!holdBoots && Scores(boots));
            }

            // Highest raw Power in a pool: which item to wear when no step cares. Starts below zero
            // so a pool of Power-less items still yields one -- something beats nothing.
            int HighestPower(IEnumerable<KeyValuePair<int, GearScorer.Item>> pool)
            {
                int pick = 0; double bestPower = -1;
                foreach (var c in pool)
                {
                    c.Value.Stats.TryGetValue(GearObjectives.Stat.Power, out double pw);
                    if (pw > bestPower) { bestPower = pw; pick = c.Key; }
                }
                return pick;
            }

            // Chains only. An owner keeps just the main slots its stats fill, and a slot left at 0 is
            // not handed to the equipper at all. Cannot lower any step's score or break a floor: stats
            // are non-negative, and the slot is only empty because nothing strictly improved it.
            void FillEmptyMainSlotsByPower()
            {
                if (!holdWeapon && r.MainWeapon == 0)
                    r.MainWeapon = HighestPower(weapons.Where(w => w.Key != r.OffWeapon));
                if (twoWeapons && !holdOff && r.OffWeapon == 0)
                    r.OffWeapon = HighestPower(weapons.Where(w => w.Key != r.MainWeapon));
                if (!holdHead && r.Head == 0) r.Head = HighestPower(heads);
                if (!holdChest && r.Chest == 0) r.Chest = HighestPower(chests);
                if (!holdLegs && r.Legs == 0) r.Legs = HighestPower(legs);
                if (!holdBoots && r.Boots == 0) r.Boots = HighestPower(boots);
            }

            // One pass over the steps. THE MAIN SLOTS GO TO THE FIRST STEP WITH AN OPINION ABOUT THEM,
            // not to step 0 unconditionally; every step after the owner is accessory-only, which is
            // exactly "freeze the main slots once they are claimed". Each step's accessory budget is
            // taken from the seats still free when it runs, so a step that fills fewer than it asked
            // for hands the rest on.
            //
            // Safe to re-enter on a filled set (the floor phases do): a step then re-searches the
            // seats it owns by position instead of filling them.
            double RunOptimize()
            {
                int frozen = holdAccessories;
                bool mainOwned = false;
                foreach (var step in steps)
                {
                    obj = step.Objective;
                    accFirst = frozen;
                    accCap = frozen + Math.Min(Math.Max(0, step.MaxAccessorySlots), Math.Max(0, accSlots - frozen));
                    if (!mainOwned)
                    {
                        // alternate until stable (slots interact only through the product objective)
                        double prev = double.NegativeInfinity;
                        for (int round = 0; round < 5; round++)
                        {
                            MainAscent();
                            AccessoryOptimize();
                            double cur = ScoreOf();
                            if (cur <= prev * (1 + 1e-12)) break;
                            prev = cur;
                        }
                        // While walking toward a floor every step scores the same shortfall, so the
                        // first one has already searched the main slots for all of them.
                        mainOwned = !chainMode || (floorNames.Length > 0 && phase == 0) || HasMainOpinion(obj);
                    }
                    else AccessoryOptimize();
                    frozen = Math.Min(r.Accessories.Count, accCap);
                }
                obj = steps[0].Objective;
                if (chainMode) FillEmptyMainSlotsByPower();
                // A chain has no single score, so this is the lead step's.
                return ScoreOf();
            }

            bool HasRespawn()
            {
                bool Has(int id) => id != 0 && idToItem.TryGetValue(id, out var it)
                    && it.Stats.TryGetValue(GearObjectives.Stat.Respawn, out var rv) && rv > 0;
                if (Has(r.MainWeapon) || Has(r.OffWeapon) || Has(r.Head) || Has(r.Chest) || Has(r.Legs) || Has(r.Boots)) return true;
                foreach (var a in r.Accessories) if (Has(a)) return true;
                return false;
            }

            // One solve from a clean slate, with the locks seated first. `swapWeapons` is only ever
            // meaningful when TWO weapons are locked.
            double SolveWith(bool swapWeapons)
            {
                r = new Result();
                r.Lock = lockPlan;
                SeedLocks(swapWeapons);
                return RunConstrained();
            }

            // Pass 1: merit, around whatever the lock holds.
            //
            // ⚠ TWO STATEMENTS, DELIBERATELY. `r.Score = SolveWith(false)` would evaluate the receiver
            // `r` BEFORE the call, and SolveWith replaces `r` — so the score would land on the
            // discarded Result and every caller would read 0.
            bool weaponsSwapped = false;
            double meritScore = SolveWith(false);
            r.Score = meritScore;

            // TWO LOCKED WEAPONS ARE NOT INTERCHANGEABLE. The offhand's stats are multiplied by
            // weapon2Factor() ([DECOMP] InventoryController.cs:687), which is well under 1 for most of
            // the game, so which of the pair goes in which hand changes the score. The search cannot
            // discover this — both seats are held — so try the other ordering and keep the better one.
            // Ties keep the order the user wrote, because that is the one they can predict.
            if (lockPlan != null && twoWeapons && lockPlan.Weapons.Count > 1)
            {
                var first = r;
                double firstScore = r.Score;
                double swappedScore = SolveWith(true);
                r.Score = swappedScore;
                if (swappedScore > firstScore * (1 + 1e-12)) weaponsSwapped = true;
                else r = first;
            }

            // A REQUIRED lock outranks merit, because it is not competing on merit. The Ring of
            // Apathy (135) carries curAttack/capAttack/curDefense/capDefense = 0 and specType1/2/3 =
            // None ([DECOMP] ItemNameDesc.cs:2664-2676) — literally every stat zero — so it scores
            // exactly 0 against any objective and a merit pass will NEVER select it. Its whole value is
            // a mechanic the scorer cannot see: without it worn, UUG is invincible (TitanTables
            // .RequiredAccessory carries the citation).
            //
            // AND IT SUPPRESSES THE RESPAWN PIN — exactly as before this was a lock. The old code got
            // that by returning early, with the stated reason "the respawn pin rebuilds `r` and would
            // drop this one"; SeedLocks removes that reason, but the RULE stands on its own and is
            // preserved deliberately rather than quietly relaxed: a required item beats a respawn
            // preference, and changing which set goes on a live titan fight needs its own in-game
            // validation. Note the condition is `Applied > 0`, not merely "a required lock was asked
            // for": when the mechanic item is not owned nothing was pinned, and the old code fell
            // through to the respawn pass in exactly that case too.
            //
            // A USER'S Gear Lock is a preference, not a mechanic, and composes with the pin instead —
            // lock a doll, ask for respawn, and you get both.
            bool requiredHeld = locks != null && locks.Required && lockPlan != null && lockPlan.Applied > 0;

            // "Top single Respawn": only when the merit loadout carries NO respawn at all do we pin one
            // respawn item in — and we pick the candidate whose PINNED LOADOUT scores best overall
            // (tie-break: more respawn), not the one with the highest raw respawn. This prevents a
            // pure-respawn item (Stapler) being force-pinned alongside an item that already covers
            // respawn on merit (Ring of Greed), which double-equipped respawn.
            if (forceTopRespawn && !requiredHeld && !HasRespawn())
            {
                // Is there any seat left for a respawn item of this part, given what the lock holds?
                // Answered from lockPlan alone, BEFORE `r` is thrown away, so a skipped candidate can
                // never leave a half-built result standing in for the merit set.
                bool SeatFree(GearLockSlot p)
                {
                    // The power-weapon pin takes a weapon seat the lock plan knows nothing about.
                    int lockedWeapons = lockPlan == null ? 0 : lockPlan.Weapons.Count;
                    bool pinSeated = powerWeaponPin != 0 && lockedWeapons < cap.Weapons
                                     && (lockPlan == null || !lockPlan.Holds(powerWeaponPin));
                    if (p == GearLockSlot.Weapon) return lockedWeapons + (pinSeated ? 1 : 0) < cap.Weapons;
                    if (lockPlan == null) return p != GearLockSlot.Accessory || accSlots > 0;
                    switch (p)
                    {
                        case GearLockSlot.Weapon: return lockPlan.Weapons.Count < cap.Weapons;
                        case GearLockSlot.Head: return lockPlan.Head == 0;
                        case GearLockSlot.Chest: return lockPlan.Chest == 0;
                        case GearLockSlot.Legs: return lockPlan.Legs == 0;
                        case GearLockSlot.Boots: return lockPlan.Boots == 0;
                        case GearLockSlot.Accessory: return lockPlan.Accessories.Count < accSlots;
                        default: return false;
                    }
                }

                // The merit answer, kept so it can be handed back untouched when no candidate is
                // usable — with a lock in play EVERY candidate can be skipped, and the loop below
                // overwrites `r` on the way past.
                Result merit = r;
                Result best = null;
                double bestScore = double.NegativeInfinity, bestResp = -1;
                foreach (var kv in pools)
                {
                    foreach (var it in kv.Value)
                    {
                        if (!it.Value.Stats.TryGetValue(GearObjectives.Stat.Respawn, out var resp) || resp <= 0) continue;
                        GearLockSlot p = kv.Key;
                        if (p == GearLockSlot.Accessory && accSlots <= 0) continue;
                        // A candidate the lock already holds is not a candidate: it is worn, so
                        // HasRespawn() would have been true, and pinning it again is a duplicate.
                        if (lockPlan != null && lockPlan.Holds(it.Key)) continue;
                        if (it.Key == powerWeaponPin) continue;
                        // Its seat is already held by the lock. Overwriting a locked item is the one
                        // thing a lock forbids, so this candidate simply is not available.
                        if (!SeatFree(p)) continue;

                        r = new Result();
                        r.Lock = lockPlan;
                        SeedLocks(weaponsSwapped);

                        switch (p)
                        {
                            case GearLockSlot.Weapon:
                                // The lock may hold the mainhand; then the offhand is the free seat.
                                if (!holdWeapon) { r.MainWeapon = it.Key; holdWeapon = true; }
                                else { r.OffWeapon = it.Key; holdOff = true; }
                                break;
                            case GearLockSlot.Head: r.Head = it.Key; holdHead = true; break;
                            case GearLockSlot.Chest: r.Chest = it.Key; holdChest = true; break;
                            case GearLockSlot.Legs: r.Legs = it.Key; holdLegs = true; break;
                            case GearLockSlot.Boots: r.Boots = it.Key; holdBoots = true; break;
                            case GearLockSlot.Accessory: r.Accessories.Add(it.Key); holdAccessories++; break;
                        }
                        double s = RunOptimize();
                        // User rule (Stapler 12% beat Ring of Greed 16% via loadout-score tiebreak):
                        // the pinned slot's JOB is respawn — highest respawn wins outright; loadout
                        // score only breaks respawn ties.
                        bool take = best == null || resp > bestResp
                            || (resp >= bestResp && s > bestScore * (1 + 1e-12));
                        if (take) { best = r; bestScore = s; bestResp = resp; }
                    }
                }
                if (best != null) { r = best; r.Score = bestScore; }
                else r = merit;
            }

            var finalSet = WornList();
            r.StepScores = steps.Select(st => GearScorer.ScoreRaw(finalSet, st.Objective.Stats, st.Objective.Exponents, offhand)).ToArray();
            return r;
        }
    }
}
