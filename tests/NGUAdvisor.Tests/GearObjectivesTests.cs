using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // GearObjectives is pure data, and every one of its stat strings is matched BY NAME at runtime
    // (GearScorer looks them up in a Dictionary<string,double> keyed by the same constants). A typo
    // therefore doesn't throw or warn — the stat silently contributes nothing and the objective quietly
    // optimizes for less than it claims. Nothing else in the codebase can catch that.
    public class GearObjectivesTests
    {
        // Every string constant declared on GearObjectives.Stat — the authoritative vocabulary.
        private static readonly HashSet<string> DeclaredStats = new HashSet<string>(
            typeof(GearObjectives.Stat)
                .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()),
            StringComparer.Ordinal);

        [Fact]
        public void TheStatVocabularyIsNotEmpty()
        {
            Assert.True(DeclaredStats.Count > 20, $"only found {DeclaredStats.Count} stat constants");
        }

        [Fact]
        public void EveryObjectiveHasAUsableName()
        {
            foreach (var o in GearObjectives.Objectives)
            {
                Assert.False(string.IsNullOrWhiteSpace(o.Name));
                // Names round-trip through settings.json and the companion's <select> values; leading or
                // trailing space would break FindObjective's exact (ordinal, case-insensitive) match.
                Assert.Equal(o.Name.Trim(), o.Name);
            }
        }

        [Fact]
        public void ObjectiveNamesAreUnique()
        {
            // FindObjective takes the FIRST case-insensitive match, so a duplicate would make one of them
            // permanently unreachable.
            var dupes = GearObjectives.Objectives
                .GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToArray();
            Assert.True(dupes.Length == 0, "duplicate objective names: " + string.Join(", ", dupes));
        }

        [Fact]
        public void EveryObjectiveTargetsAtLeastOneStat()
        {
            foreach (var o in GearObjectives.Objectives)
            {
                Assert.NotNull(o.Stats);
                Assert.NotEmpty(o.Stats);
            }
        }

        [Fact]
        public void EveryObjectiveStatIsADeclaredStatConstant()
        {
            var bad = new List<string>();
            foreach (var o in GearObjectives.Objectives)
                foreach (var s in o.Stats)
                    if (!DeclaredStats.Contains(s)) bad.Add($"{o.Name} -> '{s}'");
            Assert.True(bad.Count == 0, "objective targets an unknown stat (it would score 0 forever): "
                                        + string.Join(", ", bad));
        }

        [Fact]
        public void EverySpecTypeMapsToDeclaredStats()
        {
            var bad = new List<string>();
            foreach (var kv in GearObjectives.SpecTypeToStats)
            {
                Assert.NotNull(kv.Value);
                Assert.NotEmpty(kv.Value);
                foreach (var s in kv.Value)
                    if (!DeclaredStats.Contains(s)) bad.Add($"specType {kv.Key} -> '{s}'");
            }
            Assert.True(bad.Count == 0, "specType maps to an unknown stat: " + string.Join(", ", bad));
        }

        [Fact]
        public void ExponentsAreEitherAbsentOrExactlyOnePerStat()
        {
            // ScoreVals guards with `exponents.Count > i`: a SHORT array silently leaves later stats at
            // weight 1, and a LONG one silently ignores the tail. Either way the objective doesn't do
            // what it says, with no error.
            foreach (var o in GearObjectives.Objectives)
            {
                if (o.Exponents == null) continue;
                Assert.True(o.Exponents.Length == o.Stats.Length,
                    $"'{o.Name}' has {o.Stats.Length} stats but {o.Exponents.Length} exponents");
            }
        }

        [Fact]
        public void ExponentsArePositive()
        {
            foreach (var o in GearObjectives.Objectives)
            {
                if (o.Exponents == null) continue;
                foreach (var e in o.Exponents)
                    Assert.True(e > 0, $"'{o.Name}' has a non-positive exponent {e}");
            }
        }

        [Fact]
        public void AdventureDoesNotTargetRespawn()
        {
            // Documented rule at the objective's definition: Respawn is base-zero, so including it in a
            // PRODUCT explodes the score at low totals (16 -> 36 respawn reads as "doubled") and the
            // optimizer stacks respawn items that are mostly wasted. Respawn coverage is the TopRespawn
            // pin's job, not the objective's.
            var adventure = GearObjectives.Objectives.First(o => o.Name == "Adventure");
            Assert.DoesNotContain(GearObjectives.Stat.Respawn, adventure.Stats);
            Assert.Contains(GearObjectives.Stat.Power, adventure.Stats);
            Assert.Contains(GearObjectives.Stat.Toughness, adventure.Stats);
        }

        // Kill rate is linear in Power and Toughness only has to clear a threshold, so Power carries
        // twice the weight. With equal weights a point of Toughness out-priced a point of Power.
        [Fact]
        public void Adventure_weighs_power_twice_as_heavily_as_toughness()
        {
            var adventure = GearObjectives.Objectives.First(o => o.Name == "Adventure");
            Assert.Equal(new[] { GearObjectives.Stat.Power, GearObjectives.Stat.Toughness }, adventure.Stats);
            Assert.Equal(new[] { 1.0, 0.5 }, adventure.Exponents);

            // Same total stats either way; the set that puts them into Power must win.
            var power = new GearScorer.Item(); power.Stats[GearObjectives.Stat.Power] = 400; power.Stats[GearObjectives.Stat.Toughness] = 100;
            var tough = new GearScorer.Item(); tough.Stats[GearObjectives.Stat.Power] = 100; tough.Stats[GearObjectives.Stat.Toughness] = 400;
            double p = GearScorer.ScoreRaw(new[] { power }, adventure.Stats, adventure.Exponents, 100);
            double t = GearScorer.ScoreRaw(new[] { tough }, adventure.Stats, adventure.Exponents, 100);
            Assert.True(p > t);
            Assert.Equal(2.0, p / t, 9);   // (4 x 1^0.5) / (1 x 4^0.5)
        }

        [Theory]
        // The names the advisor hard-codes. If one is renamed, FindObjective returns null at runtime and
        // the affected path silently stops optimizing — these are the ones that must never drift.
        [InlineData("Adventure")]     // GearOptimizer.ResolveTitanGear real-fight override; GearHunter
        [InlineData("Gold Drops")]    // GearOptimizer.ResolveGoldGear default; NOTM challenge default
        [InlineData("NGUs")]          // ChallengeOverlay growth phase
        [InlineData("Power")]         // OptimizationAdvisor.ProjectedBestGear
        [InlineData("Toughness")]     // OptimizationAdvisor.ProjectedBestGear
        [InlineData("Time Machine")]  // ChallengeOverlay TM HOUR segment
        [InlineData("Advanced Training")]  // ChallengeOverlay AT HOUR segment
        [InlineData("Augments")]      // ChallengeOverlay AUGMENTATION segment
        public void HardCodedObjectiveNamesStillResolve(string name)
        {
            Assert.Contains(GearObjectives.Objectives, o =>
                string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
