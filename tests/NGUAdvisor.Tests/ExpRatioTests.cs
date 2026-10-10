using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Headless guard for the difficulty/phase EXP split (audit M5). Locks the guide's ratio matrix —
    // in particular the Evil ch.5 rule the balancer was missing: energy-only pre-T7 but ONLY once the
    // Ygg/EXP magic NGUs are capped, base 3:1 while still building them, and base 3:1 post-T7 — versus
    // the Normal-only magic skew. Guards against silently drifting back to base 3:1 on Evil, and against
    // going energy-only too early (the first fix's mistake).
    public class ExpRatioTests
    {
        private static (double e, double m) Split(bool evil, bool t7, bool capped, bool normalSkew)
        {
            ExpRatio.Split(evil, t7, capped, normalSkew, out double pe, out double pm);
            return (pe, pm);
        }

        [Fact]
        public void Base_split_is_even()
            => Assert.Equal((0.5, 0.5), Split(evil: false, t7: false, capped: false, normalSkew: false));

        [Fact]
        public void Normal_D4_skews_toward_magic()
            => Assert.Equal((0.4, 0.6), Split(evil: false, t7: false, capped: false, normalSkew: true));

        [Fact]
        public void Evil_pre_T7_before_magic_ngus_cap_stays_base()
            => Assert.Equal((0.5, 0.5), Split(evil: true, t7: false, capped: false, normalSkew: false));

        [Fact]
        public void Evil_pre_T7_once_magic_ngus_capped_is_energy_only()
            => Assert.Equal((1.0, 0.0), Split(evil: true, t7: false, capped: true, normalSkew: false));

        [Fact]
        public void Evil_post_T7_returns_to_base_3to1_regardless_of_cap()
        {
            Assert.Equal((0.5, 0.5), Split(evil: true, t7: true, capped: true, normalSkew: false));
            Assert.Equal((0.5, 0.5), Split(evil: true, t7: true, capped: false, normalSkew: false));
        }

        [Fact]
        public void Evil_ignores_the_normal_magic_skew()
        {
            Assert.Equal((1.0, 0.0), Split(evil: true, t7: false, capped: true, normalSkew: true));
            Assert.Equal((0.5, 0.5), Split(evil: true, t7: false, capped: false, normalSkew: true));
        }

        // ── the walk step ─────────────────────────────────────────────────────────────────────────

        // A tenth of 510 EXP is 51: it buys no power (150). The old flat floor skipped the tick; a
        // cheapest-unit floor dripped it into cap. The stat furthest behind gets its one unit instead.
        [Fact]
        public void A_small_bank_buys_one_unit_of_the_stat_furthest_behind()
        {
            long budget;
            Assert.Equal(ExpRatio.Step.MostBehindOnly, ExpRatio.WalkStep(510, 0.10, 150, out budget));
            Assert.Equal(150, budget);
            Assert.Equal(ExpRatio.Step.MostBehindOnly, ExpRatio.WalkStep(280, 0.10, 150, out budget));
            Assert.Equal(150, budget);
            Assert.Equal(ExpRatio.Step.MostBehindOnly, ExpRatio.WalkStep(150, 0.10, 150, out budget));
        }

        // Saving up is the point: a bank that cannot buy the unit yet is held, not spent on something
        // cheaper that is less far behind.
        [Fact]
        public void A_bank_that_cannot_afford_that_unit_waits()
        {
            long budget;
            Assert.Equal(ExpRatio.Step.Wait, ExpRatio.WalkStep(149, 0.10, 150, out budget));
            Assert.Equal(0, budget);
            Assert.Equal(ExpRatio.Step.Wait, ExpRatio.WalkStep(280, 0.10, 450, out budget));   // magic power
            Assert.Equal(ExpRatio.Step.Wait, ExpRatio.WalkStep(0, 0.10, 1, out budget));
        }

        [Fact]
        public void A_bank_whose_fraction_buys_whole_units_is_spread_as_before()
        {
            long budget;
            Assert.Equal(ExpRatio.Step.Waterfill, ExpRatio.WalkStep(1500, 0.10, 150, out budget));
            Assert.Equal(150, budget);
            Assert.Equal(ExpRatio.Step.Waterfill, ExpRatio.WalkStep(50000, 0.10, 450, out budget));
            Assert.Equal(5000, budget);
            // When cap -- one EXP a point -- is what is furthest behind, even a tiny bank is spread.
            Assert.Equal(ExpRatio.Step.Waterfill, ExpRatio.WalkStep(280, 0.10, 1, out budget));
            Assert.Equal(28, budget);
        }

        [Theory]
        [InlineData(double.NaN, 150)]
        [InlineData(-5, 150)]
        [InlineData(500, 0)]
        [InlineData(500, double.MaxValue)]
        [InlineData(500, double.PositiveInfinity)]
        public void An_unreadable_bank_or_unit_waits(double bank, double unit)
        {
            long budget;
            Assert.Equal(ExpRatio.Step.Wait, ExpRatio.WalkStep(bank, 0.10, unit, out budget));
            Assert.Equal(0, budget);
        }
    }
}
