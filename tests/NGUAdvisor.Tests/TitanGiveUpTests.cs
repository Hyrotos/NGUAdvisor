using System;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    public class TitanGiveUpTests : IDisposable
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 14, 17, 0, 0, DateTimeKind.Utc);

        public TitanGiveUpTests() => TitanGiveUp.Clear();
        public void Dispose() => TitanGiveUp.Clear();

        [Fact]
        public void ATitanNobodyGaveUpOnIsNotBlocked()
        {
            Assert.False(TitanGiveUp.Blocks(2, 1, 4000, 14000, T0));
        }

        [Fact]
        public void AGiveUpHoldsWhileTheFightIsTheSame()
        {
            TitanGiveUp.Record(2, 1, 4000, 14000, T0);

            // The next target pass, a minute later, with the same stats: still out.
            Assert.True(TitanGiveUp.Blocks(2, 1, 4000, 14000, T0.AddMinutes(1)));
            // A little stronger is still the fight that was lost.
            Assert.True(TitanGiveUp.Blocks(2, 1, 4800, 15000, T0.AddHours(1)));
        }

        [Fact]
        public void EnoughGrowthInEitherStatEndsIt()
        {
            TitanGiveUp.Record(2, 1, 4000, 14000, T0);
            Assert.False(TitanGiveUp.Blocks(2, 1, 5000, 14000, T0.AddMinutes(30)));

            TitanGiveUp.Record(2, 1, 4000, 14000, T0);
            Assert.False(TitanGiveUp.Blocks(2, 1, 4000, 17500, T0.AddMinutes(30)));
        }

        [Fact]
        public void ItExpires()
        {
            TitanGiveUp.Record(2, 1, 4000, 14000, T0);
            Assert.False(TitanGiveUp.Blocks(2, 1, 4000, 14000, T0 + TitanGiveUp.RetryAfter));
        }

        [Fact]
        public void AnotherVersionIsAnotherFight()
        {
            TitanGiveUp.Record(2, 2, 4000, 14000, T0);
            Assert.False(TitanGiveUp.Blocks(2, 1, 4000, 14000, T0.AddMinutes(1)));
        }

        [Fact]
        public void OnceEndedItStaysEndedUntilTheNextGiveUp()
        {
            TitanGiveUp.Record(2, 1, 4000, 14000, T0);
            Assert.False(TitanGiveUp.Blocks(2, 1, 6000, 14000, T0.AddMinutes(5)));
            // Stats fall back (a gear swap): the old give-up is gone, so nothing blocks.
            Assert.False(TitanGiveUp.Blocks(2, 1, 4000, 14000, T0.AddMinutes(6)));
        }

        [Fact]
        public void GiveUpsAreKeptPerTitan()
        {
            TitanGiveUp.Record(1, 1, 4000, 14000, T0);
            Assert.True(TitanGiveUp.Blocks(1, 1, 4000, 14000, T0.AddMinutes(1)));
            Assert.False(TitanGiveUp.Blocks(2, 1, 4000, 14000, T0.AddMinutes(1)));
        }
    }
}
