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

        // ── CHAINS BY NAME ─────────────────────────────────────────────────────────────────────────
        // A chain of your own is SPELLED, in the same form Describe prints:
        //
        //     Adventure(3) > Respawn(1) > Adventure(all)        Adventure(0)+PowerWeapon > Drop Chance(all)
        //
        // and that spelling is its name. Everything that carries a gear objective carries a string --
        // the profile row, the resolver, the advisor's "did the objective change" test, the readouts --
        // so a chain that IS a string needs no second channel through any of them, and two rows mean
        // the same chain exactly when they are spelled the same.

        // Any gear-objective name: a plain objective, a named chain, or a spelled one. Null when it is
        // none of them -- refuse, don't guess: the caller equips whatever comes back.
        public static GearObjectives.Objective Find(string name)
            => FindObjective(name) ?? (GearObjectives.Objective)FindPreset(name) ?? Parse(name);

        private static readonly Dictionary<string, ChainObjective> _parsed = new Dictionary<string, ChainObjective>();

        // A spelled chain, or null if any step is malformed, names no objective, or there are more
        // than MaxPriorities of them. Strict on purpose: a chain with a step silently missing is a
        // different chain, and nothing downstream could tell.
        public static ChainObjective Parse(string spelled)
        {
            if (string.IsNullOrEmpty(spelled) || spelled.IndexOf('(') < 0) return null;
            lock (_parsed)
            {
                if (_parsed.TryGetValue(spelled, out var hit)) return hit;
                var chain = ParseCore(spelled);
                if (_parsed.Count >= 64) _parsed.Clear();   // names come from hand-edited files
                _parsed[spelled] = chain;
                return chain;
            }
        }

        private const string PinSuffix = "+PowerWeapon";

        private static ChainObjective ParseCore(string spelled)
        {
            var parts = spelled.Split('>');
            if (parts.Length > MaxPriorities) return null;
            var steps = new List<GearPriority>(parts.Length);
            foreach (var raw in parts)
            {
                string part = raw.Trim();
                bool pin = part.EndsWith(PinSuffix, StringComparison.OrdinalIgnoreCase);
                if (pin) part = part.Substring(0, part.Length - PinSuffix.Length).TrimEnd();
                int open = part.LastIndexOf('(');
                if (open <= 0 || !part.EndsWith(")")) return null;
                var objective = FindObjective(part.Substring(0, open).Trim());
                if (objective == null) return null;
                string budget = part.Substring(open + 1, part.Length - open - 2).Trim();
                int slots;
                if (string.Equals(budget, "all", StringComparison.OrdinalIgnoreCase)) slots = Unlimited;
                else if (!int.TryParse(budget, System.Globalization.NumberStyles.None,
                                       System.Globalization.CultureInfo.InvariantCulture, out slots)) return null;
                steps.Add(new GearPriority { Objective = objective, MaxAccessorySlots = slots, PinTopPowerWeapon = pin });
            }
            // Named by its canonical spelling, so "adventure( 3 )>respawn(1)" reads back tidy.
            return new ChainObjective(Describe(steps), steps);
        }

        // The profile's older, structured spelling -- "Priorities": [{ "Objective", "Slots" }] plus a
        // row-level "TopPowerWeapon" -- as the name of the same chain. Slots 0 or absent means "every
        // seat left"; a NEGATIVE one claims nothing, so a typo'd -1 cannot swallow the accessory bar.
        // A step naming no known objective is dropped and reported; null when nothing usable is left.
        public static string Spell(IEnumerable<KeyValuePair<string, int>> steps, bool pinTopPowerWeapon,
                                   List<string> unknown = null)
        {
            var chain = new List<GearPriority>();
            foreach (var step in (steps ?? new KeyValuePair<string, int>[0]).Take(MaxPriorities))
            {
                var objective = FindObjective(step.Key);
                if (objective == null) { if (unknown != null) unknown.Add(step.Key ?? ""); continue; }
                chain.Add(new GearPriority
                {
                    Objective = objective,
                    MaxAccessorySlots = step.Value == 0 ? Unlimited : Math.Max(0, step.Value),
                    PinTopPowerWeapon = pinTopPowerWeapon && chain.Count == 0,
                });
            }
            return chain.Count == 0 ? null : Describe(chain);
        }

        // The same objective with the top-Power weapon pinned (a row's "TopPowerWeapon"), as a name.
        // Null when the name is not a gear objective at all.
        public static string WithPowerWeapon(string name)
        {
            var steps = StepsOf(Find(name));
            if (steps.Count == 0) return null;
            if (steps.Any(p => p.PinTopPowerWeapon)) return name;
            return Describe(steps.Select((p, i) => i != 0 ? p : new GearPriority
            {
                Objective = p.Objective,
                MaxAccessorySlots = p.MaxAccessorySlots,
                PinTopPowerWeapon = true,
            }).ToList());
        }

        // ADVICE about a profile's gear rows, never a failure. A row whose objective resolves to
        // nothing is not mis-applied, it is SKIPPED -- and a silent skip reads as "the row ran" while
        // the gear it asked for never goes on. Callers surface these beside the load and never block.
        public static List<string> ProfileWarnings(string json)
        {
            var warnings = new List<string>();
            ProfileModel model = null;
            try { if (!string.IsNullOrEmpty(json)) model = ProfileModel.Load(json); } catch { }
            if (model == null) return warnings;

            foreach (var bp in model.Gear)
            {
                string at = $"{bp.Hours}:{bp.Minutes:00}" + (bp.Seconds > 0 ? $":{bp.Seconds:00}" : "");
                SimpleJSON.JSONNode legacy = null;
                foreach (var kv in bp.Extras) if (kv.Key == "Priorities") legacy = kv.Value;

                if (legacy != null && legacy.IsArray && legacy.Count > 0)
                {
                    if (legacy.Count > MaxPriorities)
                        warnings.Add($"A gear priority chain at {at} has {legacy.Count} steps; only the first {MaxPriorities} are used.");
                    foreach (var step in legacy.AsArray.Children.Take(MaxPriorities))
                    {
                        string name = step["Objective"]?.Value ?? "";
                        if (name == "")
                            warnings.Add($"A gear priority step at {at} has no Objective and will be skipped.");
                        else if (FindObjective(name) == null)
                            warnings.Add($"Gear priority objective \"{name}\" at {at} is not recognized; that step will be skipped.");
                        if ((step["Slots"]?.AsInt ?? 0) < 0)
                            warnings.Add($"Gear priority \"{name}\" at {at} has negative Slots; it will claim no accessory slots.");
                    }
                }
                else if (!string.IsNullOrEmpty(bp.Objective) && Find(bp.Objective) == null)
                    warnings.Add(bp.Objective.IndexOf('(') >= 0
                        ? $"Gear chain \"{bp.Objective}\" at {at} could not be read (every step is Objective(slots) or Objective(all), at most {MaxPriorities}, joined by \">\"); that row will choose no gear."
                        : $"Gear objective \"{bp.Objective}\" at {at} is not recognized; that row will choose no gear.");
            }
            return warnings;
        }

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
