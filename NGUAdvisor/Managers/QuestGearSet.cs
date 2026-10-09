using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // THE QUEST GEAR THAT COLLECTS THE MOST QUEST ITEMS PER SECOND, FOR ONE ZONE.
    //
    // Game truth: every manual kill in the quest zone rolls questDropChance() = 0.05 x (1 + gear
    // QuestDrop) x sigil x ITOPOD, and gear Respawn enters respawnTime() as max(0.2, 1 - R). So
    //
    //     items/s  =  (1 + QuestDrop)  x  kills/s
    //
    // and the two halves pull against a third thing: the Power that makes each kill ONE swing. A set
    // that optimizes Quest Drops or Respawn blindly spends that Power, the zone stops being a
    // one-shot, and the rate falls instead of rising.
    //
    // So accessories are traded away from Adventure ONLY WHILE EVERY SPAWN STAYS A GUARANTEED
    // ONE-SHOT. Nothing in the zone ever gets a turn, which is why this needs no Toughness model.
    // If the full Adventure set does not one-shot the zone to begin with, it is kept as it is:
    // a zone that takes several swings is a kill problem, not a loot one.
    //
    // The search and its arithmetic live here, Unity-free, so they are tested; GearOptimizer
    // .ResolveQuestGear only reads the game into the two input bags.
    public static class QuestGearSet
    {
        // The name the Quest loadout's objective picker stores. It is not a gear objective and is
        // deliberately not resolvable as one: it only means something with a quest zone to solve for.
        public const string ObjectiveName = "Quest Drop Rate";

        // Every attack move rolls Random.Range(0.8, 1.2) on (attack - defense/2); the low end is what
        // a GUARANTEED kill has to clear.
        public const double MinRoll = 0.8;

        public struct Enemy
        {
            public double MaxHP, Defense;
            // Blacklisted, or not a boss while boss-only sniping is on: the advisor retreats to the
            // Safe Zone rather than fighting it, which costs a round trip and yields nothing.
            public bool Skipped;
        }

        // The fight, as plain data: the zone's whole spawn table (the game picks uniformly from it)
        // and how the character swings in the quest's combat mode.
        public struct Fight
        {
            public Enemy[] Enemies;
            public bool Idle;                  // idle attacks pay one swing of latency after every spawn
            public double SwingSeconds;
            public double AttackMultiplier;    // the mode's multiplier on adventure attack
            // Live adventure attack per point of the WORN set's Power score. Attack is linear in the
            // Power stat, so a candidate's attack is this times its own Power score.
            public double AttackPerPower;
            // Respawn time with the worn gear's Respawn divided back out: what gear does not touch.
            public double RespawnWithoutGear;
        }

        public class Choice
        {
            public GearSolver.Result Set;
            public string Shape;               // the chain that produced it, as GearChain.Describe prints it
            public bool OneShotsEverySpawn;    // false = the Adventure set was kept as a kill set
            public double QuestDrops;          // the gear's quest-drop factor
            public double KillsPerSecond;
            public double Rate;                // QuestDrops x KillsPerSecond
            public double AdventureRate;       // the same for the plain Adventure set, for the report
        }

        private struct Cadence { public bool OneShot; public double KillsPerSecond; }

        public static Choice Solve(GearSolver.Inputs inp, Fight fight, bool forceTopRespawn)
        {
            var adventure = GearChain.FindObjective("Adventure");
            var questDrops = GearChain.FindObjective(GearObjectives.Stat.QuestDrops);
            var respawn = GearChain.FindObjective(GearObjectives.Stat.Respawn);
            var power = GearChain.FindObjective("Power");
            if (adventure == null || questDrops == null || respawn == null || power == null) return null;
            if (fight.Enemies == null || fight.Enemies.Length == 0) return null;
            if (fight.AttackPerPower <= 0 || fight.SwingSeconds <= 0 || fight.AttackMultiplier <= 0) return null;

            double Score(GearSolver.Result r, GearObjectives.Objective o) => ScoreOf(inp, r, o);
            double Rate(GearSolver.Result r, Cadence c) => Score(r, questDrops) * c.KillsPerSecond;
            Cadence Project(GearSolver.Result r) => CadenceFor(fight,
                fight.AttackPerPower * Score(r, power),
                // Respawn is base-zero and capped at the game's floor, so its score IS the gear's R.
                fight.RespawnWithoutGear * (1.0 - Score(r, respawn)));

            var full = GearSolver.Solve(inp, adventure, forceTopRespawn);
            var fullCadence = Project(full);
            var best = new Choice
            {
                Set = full,
                Shape = "Adventure(all)",
                OneShotsEverySpawn = fullCadence.OneShot,
                KillsPerSecond = fullCadence.KillsPerSecond,
                Rate = Rate(full, fullCadence),
            };
            best.AdventureRate = best.Rate;
            best.QuestDrops = Score(full, questDrops);
            if (!fullCadence.OneShot) return best;

            int slots = full.Accessories.Count;
            for (int keep = slots - 1; keep >= 0; keep--)
            {
                bool anyOneShot = false;
                double lastRespawn = -1;
                for (int resp = 0; resp <= slots - keep; resp++)
                {
                    var chain = new GearChain.ChainObjective("quest", new[]
                    {
                        new GearPriority { Objective = adventure, MaxAccessorySlots = keep },
                        new GearPriority { Objective = respawn, MaxAccessorySlots = resp },
                        new GearPriority { Objective = questDrops, MaxAccessorySlots = GearChain.Unlimited },
                        new GearPriority { Objective = adventure, MaxAccessorySlots = GearChain.Unlimited },
                    });
                    var run = GearSolver.Solve(inp, chain, forceTopRespawn);
                    double runRespawn = Score(run, respawn);
                    if (resp > 0 && runRespawn <= lastRespawn) break;   // capped, or no respawn item left
                    lastRespawn = runRespawn;

                    var c = Project(run);
                    if (!c.OneShot) continue;
                    anyOneShot = true;
                    double rate = Rate(run, c);
                    if (rate <= best.Rate * (1.0 + 1e-9)) continue;     // ties keep the stronger set
                    best.Set = run;
                    best.Rate = rate;
                    best.KillsPerSecond = c.KillsPerSecond;
                    best.QuestDrops = Score(run, questDrops);
                    best.Shape = GearChain.Describe(chain.Priorities.Take(3).ToList());
                }
                if (!anyOneShot) break;   // fewer Adventure accessories only lose more attack
            }
            return best;
        }

        // Kill cadence when every fought spawn dies to the opening swing. A manual mode fires that
        // swing on the spawn frame, so its cycle is the respawn (or one cooldown, whichever is
        // longer); idle pays a full swing of latency first.
        private static Cadence CadenceFor(Fight f, double attack, double respawn)
        {
            if (respawn < 0) respawn = 0;
            double total = 0;
            int fought = 0;
            foreach (var e in f.Enemies)
            {
                if (e.Skipped) { total += respawn + 2.0 * f.SwingSeconds; continue; }
                double guaranteed = Math.Max(0.0, attack - e.Defense / 2.0) * f.AttackMultiplier * MinRoll;
                if (guaranteed <= 0 || e.MaxHP > guaranteed) return new Cadence();
                total += f.Idle ? respawn + f.SwingSeconds : Math.Max(respawn, f.SwingSeconds);
                fought++;
            }
            if (fought == 0 || total <= 0) return new Cadence();
            return new Cadence { OneShot = true, KillsPerSecond = fought / total };
        }

        // A solved set under any objective, scored the way the solver scores it: main weapon, then
        // offhand (GearScorer discounts the SECOND weapon it meets), armour, accessories, and the
        // two fixed pseudo-items.
        public static double ScoreOf(GearSolver.Inputs inp, GearSolver.Result r, GearObjectives.Objective obj)
        {
            if (r == null || obj == null) return 0;
            var items = inp.IdToItem ?? new Dictionary<int, GearScorer.Item>();
            var list = new List<GearScorer.Item>(16);
            void Add(int id) { if (id != 0 && items.TryGetValue(id, out var it)) list.Add(it); }
            Add(r.MainWeapon); Add(r.OffWeapon);
            Add(r.Head); Add(r.Chest); Add(r.Legs); Add(r.Boots);
            foreach (var a in r.Accessories) Add(a);
            list.Add(inp.Cube); list.Add(inp.BaseItem);
            return GearScorer.ScoreRaw(list, obj.Stats, obj.Exponents, inp.OffhandPercent);
        }
    }
}
