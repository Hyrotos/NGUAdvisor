using System.Collections.Generic;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // GearScorer is the one piece of the optimizer that is pure, and it was validated by hand against
    // gmiclotte's tool with nothing to hold that validation in place. These tests pin the behaviours a
    // well-meaning "optimization" would break.
    public class GearScorerTests
    {
        private static GearScorer.Item Item(bool isWeapon, params (string stat, double val)[] stats)
        {
            var it = new GearScorer.Item { IsWeapon = isWeapon };
            foreach (var (s, v) in stats) it.Stats[s] = v;
            return it;
        }

        private static double Score(IReadOnlyList<GearScorer.Item> equip, string stat, double offhand = 100)
            => GearScorer.ScoreRaw(equip, new[] { stat }, null, offhand);

        [Theory]
        [InlineData(GearObjectives.Stat.Respawn)]
        [InlineData(GearObjectives.Stat.Power)]
        [InlineData(GearObjectives.Stat.Toughness)]
        public void BaseZeroStats_StartFromNothing(string stat)
        {
            // Nothing equipped => 0, not 1. These three stats ARE the whole multiplier.
            Assert.Equal(0.0, Score(new List<GearScorer.Item>(), stat));
            Assert.Equal(0.5, Score(new[] { Item(false, (stat, 50.0)) }, stat));
        }

        [Theory]
        [InlineData(GearObjectives.Stat.DropChance)]
        [InlineData(GearObjectives.Stat.GoldDrops)]
        [InlineData(GearObjectives.Stat.EnergyPower)]
        public void PercentStats_StartFromOneHundred(string stat)
        {
            // Everything else is a bonus ON TOP of the game's base 100%.
            Assert.Equal(1.0, Score(new List<GearScorer.Item>(), stat));
            Assert.Equal(1.5, Score(new[] { Item(false, (stat, 50.0)) }, stat));
        }

        [Fact]
        public void Offhand_IsDiscounted_ButMainhandIsNot()
        {
            var main = Item(true, (GearObjectives.Stat.DropChance, 40.0));
            var off = Item(true, (GearObjectives.Stat.DropChance, 40.0));
            // 100 + 40 (mainhand, full) + 40*0.5 (offhand, halved) = 160
            Assert.Equal(1.60, Score(new[] { main, off }, GearObjectives.Stat.DropChance, 50), 10);
        }

        [Fact]
        public void FirstWeapon_FlipsMainhand_EvenWhenItLacksTheStat()
        {
            // THE SUBTLE ONE. The mainhand flag flips on the FIRST WEAPON regardless of whether that
            // weapon carries the stat being scored. A "skip items without this stat" optimization would
            // leave the second weapon still looking like the mainhand and stop discounting it — the
            // offhand would silently become twice as valuable as the game says it is.
            var mainNoStat = Item(true);                                              // carries nothing
            var offWithStat = Item(true, (GearObjectives.Stat.DropChance, 40.0));     // offhand
            Assert.Equal(1.20, Score(new[] { mainNoStat, offWithStat }, GearObjectives.Stat.DropChance, 50), 10);
        }

        [Fact]
        public void NonWeapons_AreNeverDiscounted()
        {
            var a = Item(false, (GearObjectives.Stat.DropChance, 40.0));
            var b = Item(false, (GearObjectives.Stat.DropChance, 40.0));
            Assert.Equal(1.80, Score(new[] { a, b }, GearObjectives.Stat.DropChance, 50), 10);
        }

        [Fact]
        public void NaN_IsSkipped_NotPropagated()
        {
            var ok = Item(false, (GearObjectives.Stat.DropChance, 50.0));
            var bad = Item(false, (GearObjectives.Stat.DropChance, double.NaN));
            var s = Score(new[] { ok, bad }, GearObjectives.Stat.DropChance);
            Assert.False(double.IsNaN(s));
            Assert.Equal(1.5, s, 10);
        }

        [Fact]
        public void NullItems_AreSkipped()
        {
            var list = new List<GearScorer.Item> { null, Item(false, (GearObjectives.Stat.DropChance, 50.0)), null };
            Assert.Equal(1.5, Score(list, GearObjectives.Stat.DropChance), 10);
        }

        [Fact]
        public void Score_IsTheProductOfPerStatMultipliers()
        {
            var it = Item(false, (GearObjectives.Stat.DropChance, 100.0), (GearObjectives.Stat.GoldDrops, 50.0));
            var s = GearScorer.ScoreRaw(new[] { it },
                new[] { GearObjectives.Stat.DropChance, GearObjectives.Stat.GoldDrops }, null, 100);
            Assert.Equal(2.0 * 1.5, s, 10);
        }

        [Fact]
        public void Exponents_WeightEachStat()
        {
            var it = Item(false, (GearObjectives.Stat.DropChance, 300.0));   // multiplier 4
            var half = GearScorer.ScoreRaw(new[] { it }, new[] { GearObjectives.Stat.DropChance }, new[] { 0.5 }, 100);
            Assert.Equal(2.0, half, 10);                                     // 4^0.5
        }

        [Fact]
        public void MissingExponents_DefaultToWeightOne()
        {
            // ScoreVals guards with `exponents.Count > i`, so a SHORT array silently leaves the tail at
            // weight 1. GearObjectivesTests asserts no shipped objective relies on that.
            var it = Item(false, (GearObjectives.Stat.DropChance, 100.0), (GearObjectives.Stat.GoldDrops, 100.0));
            var stats = new[] { GearObjectives.Stat.DropChance, GearObjectives.Stat.GoldDrops };
            var shortExp = GearScorer.ScoreRaw(new[] { it }, stats, new[] { 1.0 }, 100);
            var noExp = GearScorer.ScoreRaw(new[] { it }, stats, null, 100);
            Assert.Equal(noExp, shortExp, 10);
        }

        [Fact]
        public void MoreOfATargetedStat_NeverScoresWorse()
        {
            var less = Item(false, (GearObjectives.Stat.DropChance, 10.0));
            var more = Item(false, (GearObjectives.Stat.DropChance, 20.0));
            Assert.True(Score(new[] { more }, GearObjectives.Stat.DropChance)
                      > Score(new[] { less }, GearObjectives.Stat.DropChance));
        }

        [Fact]
        public void UntargetedStats_DoNotAffectTheScore()
        {
            var plain = Item(false, (GearObjectives.Stat.DropChance, 10.0));
            var loaded = Item(false, (GearObjectives.Stat.DropChance, 10.0), (GearObjectives.Stat.Cooking, 999.0));
            Assert.Equal(Score(new[] { plain }, GearObjectives.Stat.DropChance),
                         Score(new[] { loaded }, GearObjectives.Stat.DropChance), 10);
        }

        [Theory]
        [InlineData(GearObjectives.Stat.Respawn, 0.0)]
        [InlineData(GearObjectives.Stat.Power, 0.0)]
        [InlineData(GearObjectives.Stat.Toughness, 0.0)]
        [InlineData(GearObjectives.Stat.DropChance, 100.0)]
        [InlineData(GearObjectives.Stat.GoldDrops, 100.0)]
        public void BaseValue_IsZeroOnlyForTheThreeAdditiveStats(string stat, double expected) =>
            Assert.Equal(expected, GearScorer.BaseValue(stat));

        // The game floors the respawn factor at 0.2 (decomp AdventureController.respawnTime), so a
        // gear Respawn total past 80% buys NOTHING. Scoring it linearly told the optimizer that the
        // 81st point was worth as much as the first.
        [Fact]
        public void RespawnTotal_IsCappedAtTheGamesFloor()
        {
            var atCap = new[] { Item(false, (GearObjectives.Stat.Respawn, 80.0)) };
            var overCap = new[] { Item(false, (GearObjectives.Stat.Respawn, 80.0)), Item(false, (GearObjectives.Stat.Respawn, 40.0)) };
            Assert.Equal(Score(atCap, GearObjectives.Stat.Respawn), Score(overCap, GearObjectives.Stat.Respawn));
        }

        [Fact]
        public void RespawnBelowTheCap_StillScoresLinearly()
        {
            double half = Score(new[] { Item(false, (GearObjectives.Stat.Respawn, 40.0)) }, GearObjectives.Stat.Respawn);
            double full = Score(new[] { Item(false, (GearObjectives.Stat.Respawn, 80.0)) }, GearObjectives.Stat.Respawn);
            Assert.Equal(2.0, full / half, 9);
        }

        // The cap is a property of the STAT (a game rule), not of an objective, so it must not leak
        // into stats the game does not floor.
        [Fact]
        public void OtherStats_AreNotCapped()
        {
            double small = Score(new[] { Item(false, (GearObjectives.Stat.DropChance, 500.0)) }, GearObjectives.Stat.DropChance);
            double big = Score(new[] { Item(false, (GearObjectives.Stat.DropChance, 1500.0)) }, GearObjectives.Stat.DropChance);
            Assert.True(big > small);
        }
    }
}
