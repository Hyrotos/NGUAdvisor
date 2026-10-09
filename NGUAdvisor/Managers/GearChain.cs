using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // One step of a gear priority chain: an objective plus how many of the still-free accessory
    // slots it is allowed to claim. Ported from the reference optimizer's (factor, maxslots) pair.
    public class GearPriority
    {
        public GearObjectives.Objective Objective;
        public int MaxAccessorySlots = GearChain.Unlimited;

        // Farm sets want the hardest-hitting weapon regardless of what the lead objective scores:
        // kills per second is what the loot stat multiplies, and an NGU lead picks a weapon for its
        // energy specs. The chain grammar cannot say "this step owns only the weapon" -- the first
        // step with an opinion owns every main slot -- so this rides in as a pin instead, exactly
        // like forceTopRespawn. Read off ANY step; the lead is where it is written.
        public bool PinTopPowerWeapon;
    }

    // The chain layer: ordered objectives, each with an accessory budget.
    //
    // Why this exists: GearSolver scores ONE objective, so it fills every accessory slot with the
    // same stat (all-Power accessories under "Adventure"). The reference optimizer instead runs its
    // priorities in sequence, each claiming at most maxslots of the remaining free accessory slots,
    // which is what produces mixed sets.
    //
    // Presets live HERE and not in GearObjectives.Objectives on purpose: GearOptimizerDiagnostic and
    // InventoryAdvisor iterate that list and optimize every entry. A preset is still selectable by
    // name wherever an objective is -- GearOptimizer.FindObjective falls through to FindPreset, and a
    // ChainObjective IS an Objective (its lead step's), so a caller that only scores keeps working.
    //
    // Unity-free (linked into tests) -- keep it that way.
    public static class GearChain
    {
        public const int Unlimited = int.MaxValue;

        // The reference caps its priority list at 5; native adopts the same cap so a runaway chain
        // cannot multiply the per-priority optimize cost without bound.
        public const int MaxPriorities = 5;

        // A named chain, usable anywhere an objective is. Stats/Exponents are the LEAD step's, so
        // Result.Score and CurrentScore describe the lead -- which is NOT enough to compare two sets
        // under a chain (a later step takes accessories from the lead). Callers that compare use
        // Result.StepScores and DecidingStep instead.
        public sealed class ChainObjective : GearObjectives.Objective
        {
            public readonly IReadOnlyList<GearPriority> Priorities;
            public ChainObjective(string name, IReadOnlyList<GearPriority> priorities)
                : base(name, priorities[0].Objective.Stats, priorities[0].Objective.Exponents)
            { Priorities = priorities; }
        }

        public static GearObjectives.Objective FindObjective(string name)
            => GearObjectives.Objectives.FirstOrDefault(o =>
                string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

        private static GearPriority Step(string objective, int slots, bool pinTopPowerWeapon = false)
            => new GearPriority
            {
                Objective = FindObjective(objective),
                MaxAccessorySlots = slots,
                PinTopPowerWeapon = pinTopPowerWeapon,
            };

        // A step naming an objective that does not exist is dropped rather than guessed at, and a
        // chain left with no steps is not offered at all.
        private static ChainObjective Preset(string name, params GearPriority[] steps)
        {
            var valid = steps.Where(s => s.Objective != null).Take(MaxPriorities).ToList();
            return valid.Count == 0 ? null : new ChainObjective(name, valid);
        }

        // Named chains, selectable exactly like an objective. The first two repeat their lead
        // objective as the final unlimited step: reserve a couple of slots for the secondary stat,
        // then fill whatever is left with the lead again. Expressing a reserve this way needs no new
        // grammar -- the same objective may appear more than once in a chain.
        public static readonly IReadOnlyList<ChainObjective> Presets = new[]
        {
            // Adventure that always keeps a respawn accessory. The TopRespawn pin only fires when the
            // loadout has NO respawn at all, so on merit-respawn gear it never engages; this reserves
            // a slot unconditionally.
            Preset("Adventure + Respawn",
                Step("Adventure", 3), Step("Respawn", 1), Step("Adventure", Unlimited)),
            // Adventure that keeps energy-support accessories instead of stacking pure Power.
            Preset("Adventure + Energy",
                Step("Adventure", 3), Step("Energy NGU", 2), Step("Adventure", Unlimited)),
            // Farm sets: max drop chance on the accessories, real stats everywhere else.
            //
            // These are the ONLY shape that works for a loot stat. No main-slot item in the game
            // carries Drop Chance, so leading with it leaves every helmet, chest and weapon scoring
            // dead equal. The partner objective leads (owning the main slots at budget 0, claiming no
            // accessory), Drop Chance then takes all of them.
            Preset("Drop Chance + Adventure",
                Step("Adventure", 0, pinTopPowerWeapon: true), Step("Drop Chance", Unlimited)),
            Preset("Drop Chance + NGUs",
                Step("NGUs", 0, pinTopPowerWeapon: true), Step("Drop Chance", Unlimited)),
        }.Where(p => p != null).ToList();

        public static ChainObjective FindPreset(string name)
            => Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        // Any objective as a chain, so the solver handles exactly one shape: a preset is its own
        // steps, a plain objective is one step that may take every accessory slot.
        public static IReadOnlyList<GearPriority> StepsOf(GearObjectives.Objective obj)
        {
            if (obj == null) return new GearPriority[0];
            var chain = obj as ChainObjective;
            return chain != null
                ? chain.Priorities
                : new[] { new GearPriority { Objective = obj, MaxAccessorySlots = Unlimited } };
        }

        // Lexicographic over the steps, like the chain itself: the first step whose best/worn ratio
        // leaves [1/bar, bar] decides -- above it the best set improves, below it the worn set is
        // better. -1 = every step inside the bar. A worn score of 0 under a positive best (a base-0 stat
        // like Respawn that nothing worn carries) is an improvement, not a division by zero.
        public static int DecidingStep(IReadOnlyList<double> worn, IReadOnlyList<double> best, double bar,
                                       out bool improves)
        {
            improves = false;
            int steps = Math.Min(worn.Count, best.Count);
            for (int k = 0; k < steps; k++)
            {
                if (worn[k] <= 0)
                {
                    if (best[k] <= 0) continue;
                    improves = true;
                    return k;
                }
                if (best[k] >= worn[k] * bar) { improves = true; return k; }
                if (best[k] * bar <= worn[k]) return k;
            }
            return -1;
        }

        // "Adventure(3) > Respawn(1) > Adventure(all)" -- the DECLARED per-step budget, never a
        // computed one, so the string is free of game reads and stable for a given chain.
        public static string Describe(IReadOnlyList<GearPriority> chain)
        {
            if (chain == null || chain.Count == 0) return "(no chain)";
            var parts = new List<string>(chain.Count);
            foreach (var step in chain)
            {
                if (step == null || step.Objective == null) continue;
                var slots = step.MaxAccessorySlots >= Unlimited ? "all" : step.MaxAccessorySlots.ToString();
                parts.Add($"{step.Objective.Name}({slots}){(step.PinTopPowerWeapon ? "+PowerWeapon" : "")}");
            }
            return parts.Count == 0 ? "(no chain)" : string.Join(" > ", parts.ToArray());
        }
    }
}
