using System;
using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // THE GEAR SEARCH. Until GearSolver was split out of GearOptimizer this file could not exist:
    // the search was the body of a method that reads Main.Character and Main.InventoryController, so
    // it could not link into this assembly, and ~1979 pure-function tests ran green beside a 300-line
    // coordinate ascent that nothing could see. Three defects shipped past that green suite in one
    // session. The clearest is the first test below.
    //
    // Every bug this subsystem has produced has been SILENT — no throw, nothing odd-looking, just a
    // worse set equipped — so the assertions here are on NUMBERS and IDS, never on "it returned
    // something". Where a number is pinned it is derived by hand in the comment, and the two
    // brute-force tests check the ascent against an exhaustive enumeration of the same pool.
    public class GearSolverTests
    {
        // ── THE FIXTURE ───────────────────────────────────────────────────────────────────────────
        // A hand-built inventory. Ids are grouped by hundreds so a test reads at a glance, matching
        // GearLockTests: 1xx weapons, 2xx head, 3xx chest, 4xx legs, 5xx boots, 6xx accessories.
        private sealed class Bag
        {
            public readonly Dictionary<GearLockSlot, List<KeyValuePair<int, GearScorer.Item>>> Pools
                = new Dictionary<GearLockSlot, List<KeyValuePair<int, GearScorer.Item>>>();
            public readonly Dictionary<int, GearScorer.Item> IdToItem = new Dictionary<int, GearScorer.Item>();
            private readonly Dictionary<int, GearLockSlot> _slotOf = new Dictionary<int, GearLockSlot>();
            // Known to the game's item table but NOT in the bag — the "you don't own one" refusal.
            public readonly HashSet<int> Unowned = new HashSet<int>();

            public bool TwoWeapons = true;
            public int AccessorySlots = 2;
            public double Offhand = 50;          // weapon2Factor() x 100
            public GearScorer.Item Cube = new GearScorer.Item();
            public GearScorer.Item Base = new GearScorer.Item();

            // Add(id, slot, "Power", 200, "Respawn", 12)
            public Bag Add(int id, GearLockSlot slot, params object[] stats)
            {
                var it = new GearScorer.Item { IsWeapon = slot == GearLockSlot.Weapon };
                for (int i = 0; i + 1 < stats.Length; i += 2)
                    it.Stats[(string)stats[i]] = Convert.ToDouble(stats[i + 1]);
                IdToItem[id] = it;
                _slotOf[id] = slot;
                List<KeyValuePair<int, GearScorer.Item>> list;
                if (!Pools.TryGetValue(slot, out list))
                {
                    list = new List<KeyValuePair<int, GearScorer.Item>>();
                    Pools[slot] = list;
                }
                list.Add(new KeyValuePair<int, GearScorer.Item>(id, it));
                return this;
            }

            public GearLockItem Look(int id)
            {
                GearLockSlot slot;
                if (_slotOf.TryGetValue(id, out slot))
                    return Unowned.Contains(id)
                        ? GearLockItem.NotOwned(slot, "Item " + id)
                        : GearLockItem.Have(slot, "Item " + id);
                return GearLockItem.Missing();
            }

            public List<int> Ids(GearLockSlot slot)
            {
                List<KeyValuePair<int, GearScorer.Item>> list;
                return Pools.TryGetValue(slot, out list) ? list.Select(kv => kv.Key).ToList() : new List<int>();
            }

            public GearSolver.Inputs Inputs => new GearSolver.Inputs
            {
                Pools = Pools,
                IdToItem = IdToItem,
                Cube = Cube,
                BaseItem = Base,
                TwoWeapons = TwoWeapons,
                AccessorySlots = AccessorySlots,
                OffhandPercent = Offhand,
                Lookup = Look
            };

            // Score a set exactly the way the solver's WornList() does — main weapon first, then the
            // offhand, then armour, then accessories, then the two fixed pseudo-items. The ORDER is
            // load-bearing: GearScorer discounts the SECOND weapon it meets, so a set with no mainhand
            // and an offhand would score the offhand at full value.
            public double ScoreSet(GearObjectives.Objective obj, int main, int off, int head, int chest,
                                   int legs, int boots, IEnumerable<int> accs)
            {
                var list = new List<GearScorer.Item>();
                Action<int> add = id => { GearScorer.Item it; if (id != 0 && IdToItem.TryGetValue(id, out it)) list.Add(it); };
                add(main); add(off); add(head); add(chest); add(legs); add(boots);
                if (accs != null) foreach (var a in accs) add(a);
                list.Add(Cube); list.Add(Base);
                return GearScorer.ScoreRaw(list, obj.Stats, obj.Exponents, Offhand);
            }

            public double ScoreOf(GearObjectives.Objective obj, GearSolver.Result r)
                => ScoreSet(obj, r.MainWeapon, r.OffWeapon, r.Head, r.Chest, r.Legs, r.Boots, r.Accessories);

            public double RawStat(string stat, GearSolver.Result r)
            {
                var list = new List<GearScorer.Item>();
                Action<int> add = id => { GearScorer.Item it; if (id != 0 && IdToItem.TryGetValue(id, out it)) list.Add(it); };
                add(r.MainWeapon); add(r.OffWeapon); add(r.Head); add(r.Chest); add(r.Legs); add(r.Boots);
                foreach (var a in r.Accessories) add(a);
                list.Add(Cube); list.Add(Base);
                return GearScorer.GetRawVals(list, new[] { stat }, Offhand)[0];
            }
        }

        private static readonly GearObjectives.Objective Power =
            GearObjectives.Objectives.First(o => o.Name == "Power");

        private const string P = GearObjectives.Stat.Power;
        private const string T = GearObjectives.Stat.Toughness;
        private const string R = GearObjectives.Stat.Respawn;

        // The standard pool. Power is a BASE-ZERO stat, so the score is simply (total Power)/100 and
        // every expectation below can be arithmetic rather than a golden blob.
        //
        //   weapons 101=100  102=200  103=50      head 201=10  202=40
        //   chest   301=30   legs 401=20  boots 501=25
        //   acc     601=15   602=5    603=60
        //
        // Two weapon slots, offhand factor 50%, two accessory slots, empty cube and nude base.
        private static Bag Standard()
        {
            return new Bag()
                .Add(101, GearLockSlot.Weapon, P, 100).Add(102, GearLockSlot.Weapon, P, 200)
                .Add(103, GearLockSlot.Weapon, P, 50)
                .Add(201, GearLockSlot.Head, P, 10).Add(202, GearLockSlot.Head, P, 40)
                .Add(301, GearLockSlot.Chest, P, 30)
                .Add(401, GearLockSlot.Legs, P, 20)
                .Add(501, GearLockSlot.Boots, P, 25)
                .Add(601, GearLockSlot.Accessory, P, 15).Add(602, GearLockSlot.Accessory, P, 5)
                .Add(603, GearLockSlot.Accessory, P, 60);
        }

        private static GearFloorSet Floor(string stat, double value)
        {
            var s = new GearFloorSet();
            s.Floors.Add(new GearFloor { Stat = stat, Value = value });
            return s;
        }

        private static string Set(GearSolver.Result r)
            => r.MainWeapon + "/" + r.OffWeapon + " " + r.Head + " " + r.Chest + " " + r.Legs + " "
             + r.Boots + " [" + string.Join(",", r.Accessories) + "]";

        // ══ THE REGRESSION THIS FILE EXISTS FOR ═══════════════════════════════════════════════════

        // ⚠ THE DEFECT: with no feasible set anywhere, phase 1 scores EVERY candidate at negative
        // infinity, so PickSlot can never improve on its starting point and Optimize returns
        // Score = -Infinity. That number is then handed to callers that do ARITHMETIC on it —
        // AdvisorApply divides scores to report "+x% from new drops" — so the damage is downstream of
        // the solver and nothing throws. A live probe found it; this suite structurally could not,
        // because the search did not link. It does now.
        [Fact]
        public void An_unreachable_floor_returns_a_FINITE_score_not_negative_infinity()
        {
            var bag = Standard();
            // Nothing in the bag carries Toughness at all, so this floor cannot be met by any set.
            var r = GearSolver.Solve(bag.Inputs, Power, false, null, Floor(T, 1e9));

            Assert.False(double.IsNegativeInfinity(r.Score));
            Assert.False(double.IsInfinity(r.Score));
            Assert.False(double.IsNaN(r.Score));
            Assert.True(r.Score > 0);
        }

        [Fact]
        public void An_unreachable_floor_is_REPORTED_as_infeasible_with_the_shortfall_named()
        {
            var bag = Standard();
            var r = GearSolver.Solve(bag.Inputs, Power, false, null, Floor(T, 1e9));

            Assert.False(r.Floors.Feasible);
            Assert.Contains("Toughness", r.Floors.Message);
            Assert.Contains("the floor is out of reach, not the objective", r.Floors.Message);
            // No lock was asked for, so the lock clause must NOT appear — it would send the operator
            // to change a setting they never set.
            Assert.DoesNotContain("Gear Lock", r.Floors.Message);
        }

        // The stated contract of the phase-2 fallback: "an unreachable floor provides no guidance, so
        // the least surprising thing to return is the set you would have had without it". Asserted as
        // an EQUALITY against the unconstrained solve rather than a pinned blob, so it keeps meaning
        // something if the pool ever changes.
        [Fact]
        public void An_unreachable_floor_hands_back_exactly_the_unconstrained_set()
        {
            var bag = Standard();
            var free = GearSolver.Solve(bag.Inputs, Power);
            var blocked = GearSolver.Solve(bag.Inputs, Power, false, null, Floor(T, 1e9));

            Assert.Equal(Set(free), Set(blocked));
            Assert.Equal(free.Score, blocked.Score);
            Assert.True(free.Floors.Feasible == false && free.Floors.Message == null); // unread when no floors
        }

        // ══ THE UNCONSTRAINED SEARCH ══════════════════════════════════════════════════════════════

        // Hand-derived: mainhand 102 (200) + offhand 101 (100 x 50%) = 250, head 202 (40),
        // chest 301 (30), legs 401 (20), boots 501 (25), accessories 603 (60) + 601 (15) = 440 Power.
        // Power is base-zero so the score is 440/100 = 4.4 exactly.
        [Fact]
        public void The_unconstrained_optimum_is_the_hand_computed_best_set()
        {
            var bag = Standard();
            var r = GearSolver.Solve(bag.Inputs, Power);

            Assert.Equal(102, r.MainWeapon);
            Assert.Equal(101, r.OffWeapon);
            Assert.Equal(202, r.Head);
            Assert.Equal(301, r.Chest);
            Assert.Equal(401, r.Legs);
            Assert.Equal(501, r.Boots);
            Assert.Equal(new List<int> { 603, 601 }, r.Accessories);
            Assert.Equal(4.4, r.Score, 12);
        }

        // The bigger weapon belongs in the MAINHAND, because the offhand's stats are multiplied by
        // weapon2Factor() ([DECOMP] InventoryController.cs:687). 102/101 scores 250; 101/102 scores 200.
        [Fact]
        public void The_larger_weapon_takes_the_mainhand_because_the_offhand_is_discounted()
        {
            var bag = Standard();
            var r = GearSolver.Solve(bag.Inputs, Power);
            Assert.True(bag.ScoreSet(Power, 102, 101, 202, 301, 401, 501, new[] { 603, 601 })
                      > bag.ScoreSet(Power, 101, 102, 202, 301, 401, 501, new[] { 603, 601 }));
            Assert.Equal(102, r.MainWeapon);
        }

        [Fact]
        public void With_the_offhand_locked_by_the_game_no_second_weapon_is_worn()
        {
            var bag = Standard();
            bag.TwoWeapons = false;               // wish 28 unlearned
            var r = GearSolver.Solve(bag.Inputs, Power);
            Assert.Equal(102, r.MainWeapon);
            Assert.Equal(0, r.OffWeapon);
        }

        // weapon2Factor() is 0 until wish 28 has levels ([DECOMP] InventoryController.cs:687-690). A
        // second weapon then contributes exactly nothing, and PickSlot improves only on a STRICT
        // increase — so the seat is left empty rather than filled with a decoration.
        [Fact]
        public void A_zero_offhand_factor_leaves_the_offhand_empty()
        {
            var bag = Standard();
            bag.Offhand = 0;
            var r = GearSolver.Solve(bag.Inputs, Power);
            Assert.Equal(102, r.MainWeapon);
            Assert.Equal(0, r.OffWeapon);
        }

        [Fact]
        public void No_accessory_slots_means_no_accessories()
        {
            var bag = Standard();
            bag.AccessorySlots = 0;
            var r = GearSolver.Solve(bag.Inputs, Power);
            Assert.Empty(r.Accessories);
            Assert.Equal(102, r.MainWeapon);      // the rest of the ascent is unaffected
        }

        [Fact]
        public void An_empty_inventory_returns_an_empty_set_with_a_finite_score()
        {
            var bag = new Bag();
            var r = GearSolver.Solve(bag.Inputs, Power);
            Assert.Equal("0/0 0 0 0 0 []", Set(r));
            Assert.Equal(0.0, r.Score);
            Assert.False(double.IsInfinity(r.Score));
        }

        // Coordinate ascent is a HEURISTIC, and the only honest way to know it reaches the optimum on
        // a pool this size is to enumerate the pool. Both objectives are checked: a single base-zero
        // stat (separable, so the ascent must be exact) and the two-stat Adventure product (NOT
        // separable — the score is a product of sums, so this is the case that could genuinely fall
        // short, and it is worth knowing that it does not here).
        [Theory]
        [InlineData("Power")]
        [InlineData("Adventure")]
        public void The_ascent_reaches_the_brute_force_optimum(string objective)
        {
            var bag = Standard();
            // Give the pool a second stat so "Adventure" (Power x Toughness) is a real trade-off.
            bag.Add(104, GearLockSlot.Weapon, T, 300).Add(203, GearLockSlot.Head, T, 90)
               .Add(604, GearLockSlot.Accessory, T, 120).Add(605, GearLockSlot.Accessory, P, 20, T, 20);
            bag.Base.Stats[P] = 100; bag.Base.Stats[T] = 100;   // a nude base, as the live adapter supplies

            var obj = GearObjectives.Objectives.First(o => o.Name == objective);
            var r = GearSolver.Solve(bag.Inputs, obj);
            double best = BruteForce(bag, obj);

            Assert.Equal(best, r.Score, 9);
            Assert.Equal(bag.ScoreOf(obj, r), r.Score, 9);
        }

        private static double BruteForce(Bag bag, GearObjectives.Objective obj)
        {
            var w = bag.Ids(GearLockSlot.Weapon); w.Insert(0, 0);
            var h = bag.Ids(GearLockSlot.Head); h.Insert(0, 0);
            var c = bag.Ids(GearLockSlot.Chest); c.Insert(0, 0);
            var l = bag.Ids(GearLockSlot.Legs); l.Insert(0, 0);
            var b = bag.Ids(GearLockSlot.Boots); b.Insert(0, 0);
            var accIds = bag.Ids(GearLockSlot.Accessory);

            // Accessory ORDER does not change the score (only weapons are position-sensitive), so
            // subsets are enough.
            var accSets = new List<List<int>>();
            int n = accIds.Count;
            for (int mask = 0; mask < (1 << n); mask++)
            {
                var pick = new List<int>();
                for (int i = 0; i < n; i++) if ((mask & (1 << i)) != 0) pick.Add(accIds[i]);
                if (pick.Count <= bag.AccessorySlots) accSets.Add(pick);
            }

            double best = double.NegativeInfinity;
            foreach (var main in w)
                foreach (var off in bag.TwoWeapons ? w : new List<int> { 0 })
                {
                    if (off != 0 && off == main) continue;
                    foreach (var hh in h)
                        foreach (var cc in c)
                            foreach (var ll in l)
                                foreach (var bb in b)
                                    foreach (var acc in accSets)
                                    {
                                        double s = bag.ScoreSet(obj, main, off, hh, cc, ll, bb, acc);
                                        if (s > best) best = s;
                                    }
                }
            return best;
        }

        // ══ FEASIBLE FLOORS ═══════════════════════════════════════════════════════════════════════

        // The only Toughness in the pool is head 201, and the unconstrained ascent prefers head 202
        // (Power 40 vs 10). The floor must therefore MOVE the head slot and keep it moved.
        [Fact]
        public void A_reachable_floor_produces_a_set_that_actually_meets_it()
        {
            var bag = Standard();
            bag.Add(201, GearLockSlot.Head, P, 10, T, 120);   // re-add 201 with Toughness
            var r = GearSolver.Solve(bag.Inputs, Power, false, null, Floor(T, 100));

            Assert.True(r.Floors.Feasible);
            Assert.True(bag.RawStat(T, r) >= 100, "the returned set does not actually clear the floor");
            Assert.Equal(201, r.Head);
            // ...and everything NOT pinned by the floor is still maximised:
            // 200 + 100x50% + 10 + 30 + 20 + 25 + 60 + 15 = 410 Power.
            Assert.Equal(4.10, r.Score, 12);
            Assert.Equal(bag.ScoreOf(Power, r), r.Score, 12);
        }

        [Fact]
        public void Every_floor_in_a_conjunction_must_hold_not_just_one()
        {
            var bag = Standard();
            bag.Add(201, GearLockSlot.Head, P, 10, T, 120);
            var floors = Floor(T, 100);
            floors.Floors.Add(new GearFloor { Stat = P, Value = 380 });
            var r = GearSolver.Solve(bag.Inputs, Power, false, null, floors);

            Assert.True(r.Floors.Feasible);
            Assert.True(bag.RawStat(T, r) >= 100);
            Assert.True(bag.RawStat(P, r) >= 380);
        }

        // A floor the unconstrained optimum already clears must cost NOTHING — same set, same score,
        // and a verdict that says so. This is the case that would silently degrade every constrained
        // solve if phase 0 ever left the incumbent somewhere phase 1 could not climb out of.
        [Fact]
        public void A_floor_the_best_set_already_clears_costs_nothing()
        {
            var bag = Standard();
            var free = GearSolver.Solve(bag.Inputs, Power);
            var bound = GearSolver.Solve(bag.Inputs, Power, false, null, Floor(P, 100));   // 440 >> 100

            Assert.Equal(Set(free), Set(bound));
            Assert.Equal(free.Score, bound.Score, 12);
            Assert.True(bound.Floors.Feasible);
            Assert.Equal("the best set already clears every floor", bound.Floors.Message);
        }

        // ⚠ CHARACTERISATION, NOT AN ENDORSEMENT. FloorVerdict.Ok takes a `repairs` count and words
        // itself differently when it is non-zero ("N slot(s) traded away from the objective to clear
        // the floors") — but the solver only ever calls Ok(0), so that wording is UNREACHABLE and a
        // feasible solve always claims the best set already cleared everything. Here it plainly did
        // not: the floor moved the head slot off 202 and cost 30 Power. Pinned so that wiring the real
        // count up has to be a deliberate, visible edit rather than a silent behaviour change.
        [Fact]
        public void A_feasible_solve_always_reports_zero_repairs_even_when_the_floor_moved_a_slot()
        {
            var bag = Standard();
            bag.Add(201, GearLockSlot.Head, P, 10, T, 120);
            var free = GearSolver.Solve(bag.Inputs, Power);
            var bound = GearSolver.Solve(bag.Inputs, Power, false, null, Floor(T, 100));

            Assert.Equal(202, free.Head);
            Assert.Equal(201, bound.Head);          // the floor DID trade a slot away
            Assert.True(bound.Score < free.Score);  // and it cost score
            Assert.Equal(0, bound.Floors.Repairs);  // ...and the verdict still says nothing was traded
            Assert.Equal("the best set already clears every floor", bound.Floors.Message);
        }

        // ══ GEAR LOCK ═════════════════════════════════════════════════════════════════════════════

        [Fact]
        public void A_locked_item_is_seated_and_every_other_slot_is_optimised_around_it()
        {
            var bag = Standard();
            var r = GearSolver.Solve(bag.Inputs, Power, false, GearLockSet.Of(new[] { 201 }));

            Assert.Equal(201, r.Head);                       // the lock, not the better 202
            Assert.Equal(102, r.MainWeapon);                 // everything else is still the optimum
            Assert.Equal(101, r.OffWeapon);
            Assert.Equal(301, r.Chest);
            Assert.Equal(401, r.Legs);
            Assert.Equal(501, r.Boots);
            Assert.Equal(new List<int> { 603, 601 }, r.Accessories);
            // 250 + 10 + 30 + 20 + 25 + 75 = 410
            Assert.Equal(4.10, r.Score, 12);
            Assert.Equal(1, r.Lock.Applied);
            Assert.Null(r.Lock.Message);                     // nothing refused, so nothing said
        }

        [Fact]
        public void A_locked_accessory_holds_a_slot_and_the_remaining_slots_are_filled_on_merit()
        {
            var bag = Standard();
            var r = GearSolver.Solve(bag.Inputs, Power, false, GearLockSet.Of(new[] { 602 }));

            // Held accessories occupy the FRONT of the list and are never swapped out.
            Assert.Equal(new List<int> { 602, 603 }, r.Accessories);
            // 250 + 40 + 30 + 20 + 25 + 5 + 60 = 430
            Assert.Equal(4.30, r.Score, 12);
        }

        [Fact]
        public void Locking_every_slot_returns_exactly_the_locked_set()
        {
            var bag = Standard();
            bag.AccessorySlots = 2;
            var locks = GearLockSet.Of(new[] { 103, 101, 201, 301, 401, 501, 601, 602 });
            var r = GearSolver.Solve(bag.Inputs, Power, false, locks);

            Assert.True(r.Lock.FillsEverySlot(GearLockCapacity.Of(true, 2)));
            Assert.Equal(8, r.Lock.Applied);
            Assert.Equal(201, r.Head);
            Assert.Equal(301, r.Chest);
            Assert.Equal(401, r.Legs);
            Assert.Equal(501, r.Boots);
            Assert.Equal(new List<int> { 601, 602 }, r.Accessories);
            Assert.Contains(r.MainWeapon, new[] { 101, 103 });
            Assert.Contains(r.OffWeapon, new[] { 101, 103 });
            Assert.NotEqual(r.MainWeapon, r.OffWeapon);
            Assert.Equal(bag.ScoreOf(Power, r), r.Score, 12);
        }

        // OVER-LOCKING is the case a user is most likely to hit: three locked accessories against two
        // unlocked slots. The overflow is the LAST one written, so the order they typed is the
        // priority order — and the refusal is REPORTED, because a lock the solver dropped in silence
        // is a set they never asked for.
        [Fact]
        public void Over_locking_seats_what_fits_in_the_order_written_and_names_what_does_not()
        {
            var bag = Standard();
            bag.AccessorySlots = 2;
            var r = GearSolver.Solve(bag.Inputs, Power, false, GearLockSet.Of(new[] { 601, 602, 603 }));

            Assert.Equal(new List<int> { 601, 602 }, r.Accessories);   // 603, the best one, does NOT fit
            Assert.Equal(2, r.Lock.Applied);
            Assert.Single(r.Lock.Issues);
            Assert.Equal(GearLockIssue.NoRoom, r.Lock.Issues[0].Kind);
            Assert.Equal(603, r.Lock.Issues[0].Id);
            Assert.Contains("2 of 3 locked items are being worn", r.Lock.Message);
            // 250 + 40 + 30 + 20 + 25 + 15 + 5 = 385
            Assert.Equal(3.85, r.Score, 12);
        }

        [Fact]
        public void A_lock_on_an_item_you_do_not_own_seats_nothing_and_says_why()
        {
            var bag = Standard();
            bag.Unowned.Add(201);
            var r = GearSolver.Solve(bag.Inputs, Power, false, GearLockSet.Of(new[] { 201 }));

            Assert.Equal(0, r.Lock.Applied);
            Assert.Equal(GearLockIssue.NotOwned, r.Lock.Issues[0].Kind);
            Assert.Contains("isn't in your inventory", r.Lock.Message);
            Assert.Equal(202, r.Head);                        // the ascent had a free head slot
            Assert.Equal(4.4, r.Score, 12);                   // ...so this is just the unconstrained answer
        }

        [Fact]
        public void A_lock_on_an_id_the_item_table_does_not_define_seats_nothing()
        {
            var bag = Standard();
            var r = GearSolver.Solve(bag.Inputs, Power, false, GearLockSet.Of(new[] { 9999 }));

            Assert.Equal(0, r.Lock.Applied);
            Assert.Equal(GearLockIssue.Unknown, r.Lock.Issues[0].Kind);
            Assert.Equal(4.4, r.Score, 12);
        }

        // ── the two-weapon ordering pass ──────────────────────────────────────────────────────────
        // TWO LOCKED WEAPONS ARE NOT INTERCHANGEABLE: the offhand is multiplied by weapon2Factor()
        // ([DECOMP] InventoryController.cs:687). The search cannot discover the better ordering — both
        // seats are HELD — so the solver runs the whole solve twice and keeps the better one.
        [Fact]
        public void Two_locked_weapons_are_reordered_so_the_stronger_one_takes_the_mainhand()
        {
            var bag = Standard();
            // Written weaker-first. 103 main + 101 off = 50 + 50 = 100; 101 main + 103 off = 100 + 25 = 125.
            var r = GearSolver.Solve(bag.Inputs, Power, false, GearLockSet.Of(new[] { 103, 101 }));

            Assert.Equal(101, r.MainWeapon);
            Assert.Equal(103, r.OffWeapon);
            // 125 + 40 + 30 + 20 + 25 + 75 = 315
            Assert.Equal(3.15, r.Score, 12);
        }

        [Fact]
        public void Two_locked_weapons_already_in_the_better_order_are_left_alone()
        {
            var bag = Standard();
            var r = GearSolver.Solve(bag.Inputs, Power, false, GearLockSet.Of(new[] { 101, 103 }));
            Assert.Equal(101, r.MainWeapon);
            Assert.Equal(103, r.OffWeapon);
            Assert.Equal(3.15, r.Score, 12);
        }

        // "Ties keep the order the user wrote, because that is the one they can predict." At a 100%
        // offhand factor the two orderings score identically, and the swap must NOT be taken.
        [Fact]
        public void A_tied_weapon_ordering_keeps_the_order_the_user_wrote()
        {
            var bag = Standard();
            bag.Offhand = 100;
            var r = GearSolver.Solve(bag.Inputs, Power, false, GearLockSet.Of(new[] { 103, 101 }));
            Assert.Equal(103, r.MainWeapon);
            Assert.Equal(101, r.OffWeapon);
        }

        // A SINGLE locked weapon must leave the OTHER hand free — the two weapon seats are one slot
        // KIND but two independent seats, which is why holdOff is its own flag.
        [Fact]
        public void One_locked_weapon_still_leaves_the_other_hand_to_the_optimiser()
        {
            var bag = Standard();
            var r = GearSolver.Solve(bag.Inputs, Power, false, GearLockSet.Of(new[] { 103 }));

            Assert.Equal(103, r.MainWeapon);
            Assert.Equal(102, r.OffWeapon);            // the best remaining, chosen freely
            // 50 + 200x50% + 40 + 30 + 20 + 25 + 75 = 340
            Assert.Equal(3.40, r.Score, 12);
        }

        // ── locks vs floors ───────────────────────────────────────────────────────────────────────

        // The two constraints are SIBLINGS: a lock fixes a SLOT, a floor constrains a TOTAL. When a
        // lock is what puts the floor out of reach, the verdict says how many slots were held — the
        // operator cannot see that from the floor alone, and "the floor is out of reach" on its own
        // would send them to change the wrong number.
        [Fact]
        public void A_lock_that_makes_a_floor_unreachable_is_reported_with_the_held_slot_count()
        {
            var bag = Standard();
            bag.Add(201, GearLockSlot.Head, P, 10, T, 120);   // the ONLY Toughness in the pool
            var locked = GearSolver.Solve(bag.Inputs, Power, false,
                                          GearLockSet.Of(new[] { 202 }), Floor(T, 100));

            Assert.False(locked.Floors.Feasible);
            Assert.Contains("Gear Lock is holding 1 slot,", locked.Floors.Message);
            Assert.False(double.IsInfinity(locked.Score));    // still finite: the phase-2 fallback
            Assert.Equal(202, locked.Head);
            // ...and without the lock the very same floor is reachable.
            var free = GearSolver.Solve(bag.Inputs, Power, false, null, Floor(T, 100));
            Assert.True(free.Floors.Feasible);
        }

        [Fact]
        public void The_held_slot_count_is_pluralised_and_counts_every_held_slot()
        {
            var bag = Standard();
            bag.Add(201, GearLockSlot.Head, P, 10, T, 120);
            var r = GearSolver.Solve(bag.Inputs, Power, false,
                                     GearLockSet.Of(new[] { 202, 301, 601 }), Floor(T, 100));
            Assert.False(r.Floors.Feasible);
            Assert.Contains("Gear Lock is holding 3 slots,", r.Floors.Message);
        }

        // ══ THE RESPAWN PIN ═══════════════════════════════════════════════════════════════════════
        // A pool where the merit answer carries NO respawn, so forceTopRespawn actually fires. ONE
        // accessory seat, so the two respawn candidates genuinely compete for it:
        //   604 = Respawn 20, no Power at all   (the better respawn, the worse loadout)
        //   605 = Respawn 10, Power 30          (the worse respawn, the better loadout)
        // Neither beats 603 (Power 60) on merit, which is what leaves the merit set respawn-free.
        private static Bag RespawnBag()
        {
            var bag = Standard();
            bag.AccessorySlots = 1;
            bag.Add(604, GearLockSlot.Accessory, R, 20)
               .Add(605, GearLockSlot.Accessory, R, 10, P, 30);
            return bag;
        }

        [Fact]
        public void Without_the_pin_a_respawn_free_set_stays_respawn_free()
        {
            var bag = RespawnBag();
            var r = GearSolver.Solve(bag.Inputs, Power);
            Assert.Equal(new List<int> { 603 }, r.Accessories);   // pure merit: 60 Power beats both
            Assert.DoesNotContain(604, r.Accessories);
            Assert.DoesNotContain(605, r.Accessories);
        }

        // "Highest respawn wins OUTRIGHT; loadout score only breaks respawn ties." (User rule: a
        // Stapler at 12% beat a Ring of Greed at 16% when score was allowed to decide.) Pinning 605
        // yields the better LOADOUT — it carries 30 Power and 604 carries none — and it must still
        // lose, because the pinned slot's JOB is respawn.
        [Fact]
        public void The_pin_takes_the_highest_respawn_item_even_when_it_scores_worse()
        {
            var bag = RespawnBag();
            var r = GearSolver.Solve(bag.Inputs, Power, true);

            Assert.Equal(new List<int> { 604 }, r.Accessories);
            // ...and pinning the loser really would have scored better, so the rule is doing work.
            Assert.True(bag.ScoreSet(Power, 102, 101, 202, 301, 401, 501, new[] { 605 })
                      > bag.ScoreSet(Power, 102, 101, 202, 301, 401, 501, new[] { 604 }));
            Assert.True(r.Score > 0 && !double.IsInfinity(r.Score));
            Assert.Equal(bag.ScoreOf(Power, r), r.Score, 12);
        }

        [Fact]
        public void The_pin_does_not_fire_when_the_merit_set_already_carries_respawn()
        {
            var bag = Standard();
            bag.Add(603, GearLockSlot.Accessory, P, 60, R, 5);   // the merit pick now carries respawn
            var pinned = GearSolver.Solve(bag.Inputs, Power, true);
            var merit = GearSolver.Solve(bag.Inputs, Power);
            Assert.Equal(Set(merit), Set(pinned));
            Assert.Equal(merit.Score, pinned.Score, 12);
        }

        [Fact]
        public void The_pin_hands_back_the_merit_set_untouched_when_no_respawn_item_exists()
        {
            var bag = Standard();                 // nothing in the pool carries Respawn at all
            var pinned = GearSolver.Solve(bag.Inputs, Power, true);
            var merit = GearSolver.Solve(bag.Inputs, Power);
            Assert.Equal(Set(merit), Set(pinned));
            Assert.Equal(merit.Score, pinned.Score, 12);
        }

        // A REQUIRED lock — the titan mechanic item, today only the Ring of Apathy — outranks the
        // respawn preference. The rule predates Gear Lock (the old code got it by returning early) and
        // is preserved deliberately: changing which set goes on a live titan fight needs its own
        // in-game validation.
        [Fact]
        public void A_required_lock_that_was_applied_SUPPRESSES_the_respawn_pin()
        {
            var bag = RespawnBag();
            var r = GearSolver.Solve(bag.Inputs, Power, true, GearLockSet.RequiredItem(601));

            Assert.Equal(1, r.Lock.Applied);
            Assert.Equal(new List<int> { 601 }, r.Accessories);
            Assert.DoesNotContain(604, r.Accessories);
        }

        // ...but the condition is `Applied > 0`, NOT merely "a required lock was asked for". When the
        // mechanic item is not owned nothing was pinned, and the old code fell through to the respawn
        // pass in exactly that case too.
        [Fact]
        public void A_required_lock_that_could_NOT_be_applied_leaves_the_respawn_pin_running()
        {
            var bag = RespawnBag();
            bag.Unowned.Add(601);
            var r = GearSolver.Solve(bag.Inputs, Power, true, GearLockSet.RequiredItem(601));

            Assert.Equal(0, r.Lock.Applied);
            Assert.Equal(new List<int> { 604 }, r.Accessories);     // the pin fired
        }

        // A USER'S Gear Lock is a preference, not a mechanic, and COMPOSES with the pin instead —
        // lock a doll, ask for respawn, and you get both. This is the one behavioural difference
        // between GearLockSet.Of and GearLockSet.RequiredItem.
        [Fact]
        public void A_users_lock_composes_with_the_respawn_pin_rather_than_suppressing_it()
        {
            var bag = RespawnBag();
            bag.AccessorySlots = 2;
            var r = GearSolver.Solve(bag.Inputs, Power, true, GearLockSet.Of(new[] { 601 }));

            Assert.Contains(601, r.Accessories);                    // the lock
            Assert.Contains(604, r.Accessories);                    // and the pin
        }

        // The lock already holds the only free seat, so there is nothing to pin into. The merit answer
        // must come back UNTOUCHED — the pin loop overwrites `r` on its way past every candidate, and
        // a half-built result standing in for the merit set is exactly the silent wrong set this
        // subsystem keeps producing.
        [Fact]
        public void With_no_seat_left_for_a_respawn_item_the_merit_set_is_returned_untouched()
        {
            var bag = RespawnBag();
            var locks = GearLockSet.Of(new[] { 601 });
            var pinned = GearSolver.Solve(bag.Inputs, Power, true, locks);
            var merit = GearSolver.Solve(bag.Inputs, Power, false, locks);

            Assert.Equal(Set(merit), Set(pinned));
            Assert.Equal(merit.Score, pinned.Score, 12);
            Assert.False(double.IsInfinity(pinned.Score));
        }

        // ⚠ CHARACTERISATION OF A LIVE HAZARD, NOT AN ENDORSEMENT. The phase-2 fallback closed the
        // -Infinity hole on the MAIN path, but the respawn pin re-runs RunOptimize() at whatever phase
        // the constrained solve finished in — so when the floor was FEASIBLE (phase 1) and pinning the
        // respawn item makes it unreachable, every candidate scores -Infinity again and that is what
        // lands in Score. Reachable only with forceTopRespawn AND floors together.
        //
        // ⚠ DO NOT RE-DERIVE "no live caller does this" AND TRUST IT. That was written here when it was
        // true, and it was already false: ResolveTitanGear had begun passing TitanObjectiveRespawn into
        // a constrained solve forty minutes earlier. Reachability is a property of the whole tree at one
        // moment, so a claim about it goes stale silently. The caller now guards instead (it retries
        // without the pin, and requires a FINITE score), and this stays pinned rather than fixed because
        // changing the pin changes every set it has ever chosen and needs its own in-game validation.
        [Fact]
        public void KNOWN_HAZARD_the_respawn_pin_can_still_return_negative_infinity_under_a_floor()
        {
            var bag = Standard();
            bag.AccessorySlots = 1;
            bag.Add(606, GearLockSlot.Accessory, T, 120);          // the ONLY Toughness, and an accessory
            bag.Add(604, GearLockSlot.Accessory, R, 20);           // the respawn candidate

            // Without the pin the floor is comfortably feasible.
            var free = GearSolver.Solve(bag.Inputs, Power, false, null, Floor(T, 100));
            Assert.True(free.Floors.Feasible);
            Assert.Equal(new List<int> { 606 }, free.Accessories);

            // With it, the pin takes the single accessory seat and the floor becomes unreachable.
            var pinned = GearSolver.Solve(bag.Inputs, Power, true, null, Floor(T, 100));
            Assert.Contains(604, pinned.Accessories);
            Assert.True(double.IsNegativeInfinity(pinned.Score),
                        "if this now passes a finite score, the hazard was fixed — update the report, do not delete the test");
        }

        // ⚠ CHARACTERISATION OF A SECOND LIVE HAZARD IN THE SAME PLACE. The respawn pin rebuilds `r`
        // from scratch per candidate (`r = new Result()`), re-seats r.Lock and the locked slots — and
        // does NOT re-seat r.Floors. So a solve that carried floors comes back with the DEFAULT
        // verdict: Feasible false, Message null. Infeasible-with-a-reason silently becomes
        // infeasible-with-nothing-said, which is the exact failure mode floors exist to prevent.
        // Reachable only with forceTopRespawn AND floors together, which no live caller does today.
        // Pinned rather than fixed: it is a behaviour change on the equip path.
        [Fact]
        public void KNOWN_HAZARD_the_respawn_pin_discards_the_floor_verdict()
        {
            var bag = RespawnBag();

            var noPin = GearSolver.Solve(bag.Inputs, Power, false, null, Floor(T, 1e9));
            Assert.False(noPin.Floors.Feasible);
            Assert.NotNull(noPin.Floors.Message);                 // the verdict survives without the pin

            var pinned = GearSolver.Solve(bag.Inputs, Power, true, null, Floor(T, 1e9));
            Assert.Contains(604, pinned.Accessories);             // the pin did fire
            Assert.False(pinned.Floors.Feasible);
            Assert.Null(pinned.Floors.Message);                   // ...and took the reason with it
        }

        // ══ INVARIANTS ACROSS THE WHOLE CONFIGURATION MATRIX ══════════════════════════════════════

        // ⚠ THE TWO-STATEMENT HAZARD. `r` is REASSIGNED during the solve (`r = new Result()` inside
        // SolveWith, and again in the respawn pass), so `r.Score = SolveWith(false)` would evaluate
        // the receiver BEFORE the call and land the score on the discarded object — every caller
        // would read 0. Nothing throws; the only visible symptom is a Score that does not describe
        // the returned set. So: for every combination of lock / floor / pin, the reported Score must
        // equal a fresh scoring of the ids that came back.
        public static IEnumerable<object[]> Matrix()
        {
            foreach (var lockKind in new[] { "none", "head", "twoweapons", "over", "required" })
                foreach (var floorKind in new[] { "none", "met", "tight", "unreachable" })
                    foreach (var pin in new[] { false, true })
                        yield return new object[] { lockKind, floorKind, pin };
        }

        [Theory]
        [MemberData(nameof(Matrix))]
        public void The_reported_score_always_describes_the_returned_set(string lockKind, string floorKind, bool pin)
        {
            var bag = RespawnBag();
            bag.Add(201, GearLockSlot.Head, P, 10, T, 120);

            GearLockSet locks =
                  lockKind == "head" ? GearLockSet.Of(new[] { 201 })
                : lockKind == "twoweapons" ? GearLockSet.Of(new[] { 103, 101 })
                : lockKind == "over" ? GearLockSet.Of(new[] { 601, 602, 603, 604, 605 })
                : lockKind == "required" ? GearLockSet.RequiredItem(601)
                : null;

            GearFloorSet floors =
                  floorKind == "met" ? Floor(P, 50)
                : floorKind == "tight" ? Floor(T, 100)
                : floorKind == "unreachable" ? Floor(T, 1e9)
                : null;

            var r = GearSolver.Solve(bag.Inputs, Power, pin, locks, floors);

            Assert.False(double.IsNaN(r.Score));
            Assert.Equal(bag.ScoreOf(Power, r), r.Score, 12);
        }

        // The same matrix, minus the one combination pinned as a known hazard above: the Score handed
        // to callers that divide by it must be a real number.
        [Theory]
        [MemberData(nameof(Matrix))]
        public void The_reported_score_is_finite_outside_the_known_pin_plus_floor_hazard(string lockKind, string floorKind, bool pin)
        {
            var bag = RespawnBag();
            bag.Add(201, GearLockSlot.Head, P, 10, T, 120);
            bag.AccessorySlots = 2;

            GearLockSet locks =
                  lockKind == "head" ? GearLockSet.Of(new[] { 201 })
                : lockKind == "twoweapons" ? GearLockSet.Of(new[] { 103, 101 })
                : lockKind == "over" ? GearLockSet.Of(new[] { 601, 602, 603, 604, 605 })
                : lockKind == "required" ? GearLockSet.RequiredItem(601)
                : null;

            GearFloorSet floors =
                  floorKind == "met" ? Floor(P, 50)
                : floorKind == "tight" ? Floor(T, 100)
                : floorKind == "unreachable" ? Floor(T, 1e9)
                : null;

            var r = GearSolver.Solve(bag.Inputs, Power, pin, locks, floors);
            Assert.False(double.IsInfinity(r.Score), "Score = " + r.Score + " for " + Set(r));
        }

        // No id may appear twice: NGU equips one copy of a given accessory at a time, and the two
        // weapon seats cannot hold the same weapon either.
        [Theory]
        [MemberData(nameof(Matrix))]
        public void No_item_is_ever_worn_twice(string lockKind, string floorKind, bool pin)
        {
            var bag = RespawnBag();
            bag.Add(201, GearLockSlot.Head, P, 10, T, 120);

            GearLockSet locks =
                  lockKind == "head" ? GearLockSet.Of(new[] { 201 })
                : lockKind == "twoweapons" ? GearLockSet.Of(new[] { 103, 101 })
                : lockKind == "over" ? GearLockSet.Of(new[] { 601, 602, 603, 604, 605 })
                : lockKind == "required" ? GearLockSet.RequiredItem(601)
                : null;

            GearFloorSet floors =
                  floorKind == "met" ? Floor(P, 50)
                : floorKind == "tight" ? Floor(T, 100)
                : floorKind == "unreachable" ? Floor(T, 1e9)
                : null;

            var r = GearSolver.Solve(bag.Inputs, Power, pin, locks, floors);
            var ids = r.AllIds().Where(x => x != 0).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.True(r.Accessories.Count <= bag.AccessorySlots);
        }

        // ══ THE SEAM ITSELF ═══════════════════════════════════════════════════════════════════════

        // The candidate pools are a Dictionary, the respawn pin ITERATES it, and its tie-break reads
        // whichever equal-respawn candidate it meets first — so pool enumeration order is part of the
        // answer. GearOptimizer.BuildPools inserts equipped-then-bag; this pins the assumption that
        // enumeration follows insertion, which is what makes keying by GearLockSlot instead of the
        // game's `part` enum a non-event.
        [Fact]
        public void Pool_enumeration_follows_insertion_order()
        {
            var d = new Dictionary<GearLockSlot, int>();
            var order = new[] { GearLockSlot.Accessory, GearLockSlot.Weapon, GearLockSlot.Boots,
                                GearLockSlot.Head, GearLockSlot.Chest, GearLockSlot.Legs };
            foreach (var s in order) d[s] = 0;
            Assert.Equal(order, d.Keys.ToArray());
        }

        // A default Inputs is what a caller gets if it forgets to fill the bag. It must not throw —
        // the live path wraps Optimize in a try/catch that falls back to a STATIC loadout, so an
        // exception here is a silent downgrade rather than a visible failure.
        [Fact]
        public void A_default_inputs_bag_solves_to_nothing_rather_than_throwing()
        {
            var r = GearSolver.Solve(default(GearSolver.Inputs), Power);
            Assert.Equal("0/0 0 0 0 0 []", Set(r));
            Assert.Equal(0.0, r.Score);
            Assert.Null(r.Lock);
        }

        [Fact]
        public void A_negative_accessory_count_is_treated_as_none_rather_than_crashing()
        {
            var bag = Standard();
            bag.AccessorySlots = -3;
            var r = GearSolver.Solve(bag.Inputs, Power, true, GearLockSet.Of(new[] { 601 }));
            Assert.Empty(r.Accessories);
            Assert.Equal(0, r.Lock.Applied);
        }

        // Null locks / null floors is the whole of the pre-constraint behaviour, and an EMPTY set of
        // either must be indistinguishable from null — otherwise "the user cleared the box" quietly
        // becomes a different solve.
        [Fact]
        public void An_empty_floor_set_and_an_empty_lock_set_are_the_same_as_none()
        {
            var bag = Standard();
            var plain = GearSolver.Solve(bag.Inputs, Power);
            var empty = GearSolver.Solve(bag.Inputs, Power, false, new GearLockSet(), new GearFloorSet());

            Assert.Equal(Set(plain), Set(empty));
            Assert.Equal(plain.Score, empty.Score, 12);
            Assert.Null(empty.Lock);
            Assert.Null(empty.Floors.Message);
        }

        // ══ CHAINS ════════════════════════════════════════════════════════════════════════════════
        // A named chain runs its steps in order, each with an accessory budget. The pool: Power on
        // every main slot and three accessories, Respawn on two accessories, Drop Chance on three.
        //
        //   weapons 101=100  102=200   head 201=40   chest 301=30   legs 401=20   boots 501=25
        //   acc  601 P60  602 P50  603 P40  604 P30 | 611 R10  612 R6 | 621 DC30  622 DC20  623 DC10
        private const string DC = GearObjectives.Stat.DropChance;

        private static Bag ChainBag(int accessorySlots)
        {
            var bag = new Bag()
                .Add(101, GearLockSlot.Weapon, P, 100, T, 100).Add(102, GearLockSlot.Weapon, P, 200, T, 200)
                .Add(201, GearLockSlot.Head, P, 40, T, 40).Add(301, GearLockSlot.Chest, P, 30, T, 30)
                .Add(401, GearLockSlot.Legs, P, 20, T, 20).Add(501, GearLockSlot.Boots, P, 25, T, 25)
                .Add(601, GearLockSlot.Accessory, P, 60, T, 60).Add(602, GearLockSlot.Accessory, P, 50, T, 50)
                .Add(603, GearLockSlot.Accessory, P, 40, T, 40).Add(604, GearLockSlot.Accessory, P, 30, T, 30)
                .Add(611, GearLockSlot.Accessory, R, 10).Add(612, GearLockSlot.Accessory, R, 6)
                .Add(621, GearLockSlot.Accessory, DC, 30).Add(622, GearLockSlot.Accessory, DC, 20)
                .Add(623, GearLockSlot.Accessory, DC, 10);
            bag.AccessorySlots = accessorySlots;
            return bag;
        }

        private static GearChain.ChainObjective Chain(string name) => GearChain.FindPreset(name);
        private static GearObjectives.Objective Obj(string name) => GearChain.FindObjective(name);

        // The reason chains exist: one objective fills every accessory slot with the same stat.
        [Fact]
        public void A_plain_objective_fills_every_accessory_with_its_own_stat_and_a_chain_does_not()
        {
            var bag = ChainBag(5);
            var plain = GearSolver.Solve(bag.Inputs, Obj("Adventure"));
            Assert.Equal(new[] { 601, 602, 603, 604 }, plain.Accessories.OrderBy(x => x));

            // Adventure(3) > Respawn(1) > Adventure(all): three power, the best respawn, then the
            // fourth power accessory in the seat that is left.
            var chained = GearSolver.Solve(bag.Inputs, Chain("Adventure + Respawn"));
            Assert.Equal(new[] { 601, 602, 603, 611, 604 }, chained.Accessories);
            Assert.Equal(Set(plain).Split('[')[0], Set(chained).Split('[')[0]);   // same main slots
        }

        [Fact]
        public void A_step_never_takes_more_accessory_seats_than_are_left()
        {
            // Two seats: Adventure(3) takes both, Respawn(1) and the tail get nothing.
            var two = GearSolver.Solve(ChainBag(2).Inputs, Chain("Adventure + Respawn"));
            Assert.Equal(new[] { 601, 602 }, two.Accessories);

            var none = GearSolver.Solve(ChainBag(0).Inputs, Chain("Adventure + Respawn"));
            Assert.Empty(none.Accessories);
            Assert.Equal(102, none.MainWeapon);
        }

        // Budget 0 on the lead is how "main slots only" is spelled: it owns the armour and weapons
        // and leaves every accessory to the loot stat.
        [Fact]
        public void A_farm_chain_gives_the_main_slots_to_the_lead_and_the_accessories_to_drop_chance()
        {
            var bag = ChainBag(3);
            var r = GearSolver.Solve(bag.Inputs, Chain("Drop Chance + Adventure"));
            Assert.Equal("102/101 201 301 401 501 [621,622,623]", Set(r));

            // A plain Drop Chance objective scores no main-slot item, so it leaves them all empty.
            var plain = GearSolver.Solve(bag.Inputs, Obj("Drop Chance"));
            Assert.Equal("0/0 0 0 0 0 [621,622,623]", Set(plain));
        }

        // Respawn rates every helmet the same as no helmet. Leading with it must not freeze the main
        // slots empty: the first step WITH an opinion takes them.
        [Fact]
        public void The_main_slots_go_to_the_first_step_that_scores_them()
        {
            var chain = new GearChain.ChainObjective("t", new[]
            {
                new GearPriority { Objective = Obj("Respawn"), MaxAccessorySlots = 1 },
                new GearPriority { Objective = Obj("Adventure") },
            });
            var r = GearSolver.Solve(ChainBag(3).Inputs, chain);
            Assert.Equal("102/101 201 301 401 501 [611,601,602]", Set(r));
        }

        // A main slot no step scores is still handed to the equipper, by raw Power, because a slot
        // the solver leaves at 0 keeps whatever happened to be worn in it.
        [Fact]
        public void A_chain_fills_a_main_slot_no_step_scores_by_raw_power()
        {
            var bag = ChainBag(2).Add(502, GearLockSlot.Boots, R, 1);   // boots: 501 has Power, 502 does not
            var chain = new GearChain.ChainObjective("t", new[]
            {
                new GearPriority { Objective = Obj("Respawn"), MaxAccessorySlots = 2 },
                new GearPriority { Objective = Obj("Drop Chance") },
            });
            var r = GearSolver.Solve(bag.Inputs, chain);
            Assert.Equal(502, r.Boots);                       // Respawn owns the main slots it scores...
            Assert.Equal("102/101 201 301 401", Set(r).Split(' ')[0] + " " + r.Head + " " + r.Chest + " " + r.Legs);
            Assert.Equal(new[] { 611, 612 }, r.Accessories);  // ...and both accessory seats
        }

        [Fact]
        public void The_power_weapon_pin_holds_the_main_hand_against_the_lead_objective()
        {
            // 103 is the best NGU weapon and carries no Power; the farm chain must not wield it.
            var bag = ChainBag(3).Add(103, GearLockSlot.Weapon, GearObjectives.Stat.NGUSpeed, 500);
            var r = GearSolver.Solve(bag.Inputs, Chain("Drop Chance + NGUs"));
            Assert.Equal(102, r.MainWeapon);
            Assert.Equal(103, r.OffWeapon);

            // A locked weapon outranks the preset's pin and keeps the main hand.
            var locked = GearSolver.Solve(bag.Inputs, Chain("Drop Chance + NGUs"), false, GearLockSet.Of(new[] { 101 }));
            Assert.Equal(101, locked.MainWeapon);
            Assert.Equal(102, locked.OffWeapon);
        }

        // Locked accessories are held in front of every step's budget, not charged to one.
        [Fact]
        public void A_locked_accessory_is_kept_and_the_steps_budget_the_seats_that_remain()
        {
            var r = GearSolver.Solve(ChainBag(5).Inputs, Chain("Adventure + Respawn"), false, GearLockSet.Of(new[] { 623 }));
            Assert.Equal(new[] { 623, 601, 602, 603, 611 }, r.Accessories);
        }

        [Fact]
        public void A_chain_reports_one_score_per_step_and_its_lead_as_the_score()
        {
            // Four seats: the respawn accessory costs the lead its fourth power accessory.
            var bag = ChainBag(4);
            var chain = Chain("Adventure + Respawn");
            var r = GearSolver.Solve(bag.Inputs, chain);
            Assert.Equal(new[] { 601, 602, 603, 611 }, r.Accessories);
            Assert.Equal(3, r.StepScores.Length);
            Assert.Equal(bag.ScoreOf(Obj("Adventure"), r), r.Score, 12);
            Assert.Equal(r.Score, r.StepScores[0], 12);
            Assert.Equal(bag.ScoreOf(Obj("Respawn"), r), r.StepScores[1], 12);
            Assert.Equal(0.10, r.StepScores[1], 12);

            // The lead scores LOWER than under the plain objective -- which is exactly why a chain
            // cannot be compared on Score.
            var plain = GearSolver.Solve(bag.Inputs, Obj("Adventure"));
            Assert.True(r.Score < plain.Score);
            Assert.Equal(plain.Score, Assert.Single(plain.StepScores), 12);
        }

        // The respawn pin re-runs the whole chain around each candidate.
        [Fact]
        public void The_respawn_pin_composes_with_a_chain()
        {
            var r = GearSolver.Solve(ChainBag(3).Inputs, Chain("Drop Chance + Adventure"), true);
            Assert.Equal(new[] { 611, 621, 622 }, r.Accessories);
            Assert.Equal("102/101 201 301 401 501", Set(r).Split(' ')[0] + " " + r.Head + " " + r.Chest + " " + r.Legs + " " + r.Boots);
        }

        // A floor the chain's own set misses is met first, then each step re-searches its seats.
        [Fact]
        public void A_chain_under_a_floor_stays_feasible()
        {
            var bag = ChainBag(3);
            // Main slots give 200 + 100*0.5 + 40 + 30 + 20 + 25 = 365 Power; the floor needs one
            // power accessory on top, which the farm chain would not pick by itself.
            var floors = Floor(P, 400);
            var r = GearSolver.Solve(bag.Inputs, Chain("Drop Chance + Adventure"), false, null, floors);
            Assert.True(r.Floors.Feasible, r.Floors.Message);
            Assert.True(bag.RawStat(P, r) >= 400);
            Assert.Contains(621, r.Accessories);
            Assert.False(double.IsInfinity(r.Score));
        }

        // A chain of the user's own is resolved from its spelling and solved like a named one.
        [Fact]
        public void A_spelled_chain_solves_like_the_named_chain_it_spells()
        {
            var bag = ChainBag(5);
            var named = GearSolver.Solve(bag.Inputs, Chain("Adventure + Respawn"));
            var spelled = GearSolver.Solve(bag.Inputs, GearChain.Find("Adventure(3) > Respawn(1) > Adventure(all)"));
            Assert.Equal(Set(named), Set(spelled));

            var own = GearSolver.Solve(bag.Inputs, GearChain.Find("Respawn(2) > Drop Chance(1) > Adventure(all)"));
            Assert.Equal("102/101 201 301 401 501 [611,612,621,601,602]", Set(own));
        }
    }
}
