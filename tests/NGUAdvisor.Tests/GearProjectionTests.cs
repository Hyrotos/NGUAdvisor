using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    public class GearProjectionTests
    {
        private static GearProjection.Candidate Set(double power, double toughness)
            => new GearProjection.Candidate(power, toughness);

        [Fact]
        public void TheTwoBestSetsAreNeverAddedTogether()
        {
            // Worn: 100 / 100. The Power set doubles attack and halves defence, the Toughness set the
            // reverse. The old projection answered 2.0 / 2.0 - a set that does not exist.
            GearProjection.Pick(100, 100, new[] { Set(200, 50), Set(50, 200) }, out var atk, out var def);

            Assert.Equal(1.0, atk);
            Assert.Equal(1.0, def);
        }

        [Fact]
        public void TheMultipliersBelongToOneSet()
        {
            GearProjection.Pick(100, 100, new[] { Set(200, 50), Set(50, 200), Set(140, 130) }, out var atk, out var def);

            Assert.Equal(1.4, atk, 6);
            Assert.Equal(1.3, def, 6);
        }

        [Fact]
        public void TheSetWhoseWeakerSideGainsMostWins()
        {
            GearProjection.Pick(100, 100, new[] { Set(300, 110), Set(150, 140) }, out var atk, out var def);

            Assert.Equal(1.5, atk, 6);
            Assert.Equal(1.4, def, 6);
        }

        [Fact]
        public void NothingBetterThanTheWornSetProjectsNoGain()
        {
            GearProjection.Pick(100, 100, new[] { Set(90, 95), Set(100, 100) }, out var atk, out var def);

            Assert.Equal(1.0, atk);
            Assert.Equal(1.0, def);
        }

        [Fact]
        public void AnUnreadableWornSetProjectsNoGain()
        {
            GearProjection.Pick(0, 100, new[] { Set(500, 500) }, out var atk, out var def);

            Assert.Equal(1.0, atk);
            Assert.Equal(1.0, def);
        }
    }
}
