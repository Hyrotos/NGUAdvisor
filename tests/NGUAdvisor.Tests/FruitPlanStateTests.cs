using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    public class FruitPlanStateTests
    {
        private static string State(long tier, long target, int chapter = 3, int minChapter = 3, int cap = 10,
            int minCap = 0, bool canUnlock = true, bool currentSet = false)
            => FruitPlanState.Classify(tier, target, chapter, minChapter, cap, minCap, canUnlock, ref currentSet);

        [Fact]
        public void AReachedTierIsDoneWhateverElseHolds()
        {
            Assert.Equal(FruitPlanState.Done, State(10, 10, chapter: 1, minChapter: 5, cap: 10, minCap: 24, canUnlock: false));
        }

        [Fact]
        public void OnlyTheFirstOpenStepIsCurrent()
        {
            bool set = false;
            Assert.Equal(FruitPlanState.Current, FruitPlanState.Classify(3, 10, 3, 3, 10, 0, true, ref set));
            Assert.True(set);
            Assert.Equal(FruitPlanState.Queued, FruitPlanState.Classify(0, 5, 3, 3, 10, 0, true, ref set));
        }

        [Fact]
        public void AnEarlierChapterHoldsTheStepAndDoesNotClaimCurrent()
        {
            bool set = false;
            Assert.Equal(FruitPlanState.Chapter, FruitPlanState.Classify(0, 12, 3, 5, 24, 24, true, ref set));
            Assert.False(set);
        }

        [Fact]
        public void AFruitThatCannotBeUnlockedYetIsLocked()
        {
            Assert.Equal(FruitPlanState.Locked, State(0, 24, canUnlock: false, cap: 24));
            // Once it has a tier it is unlocked by definition, whatever the gate says.
            Assert.Equal(FruitPlanState.Current, State(1, 24, canUnlock: false, cap: 24));
        }

        [Fact]
        public void TheTier24PushWaitsForTheCap()
        {
            Assert.Equal(FruitPlanState.Cap, State(10, 24, cap: 10, minCap: 24));
            Assert.Equal(FruitPlanState.Current, State(10, 24, cap: 24, minCap: 24));
        }

        [Fact]
        public void ATierCostsTheBaseTimesTheNextTierSquared()
        {
            Assert.Equal(1, FruitPlanState.TierCost(1, 0));
            Assert.Equal(9 * 50, FruitPlanState.TierCost(50, 2));
        }

        [Fact]
        public void AStepsTotalIsEveryTierItStillBuys()
        {
            // 3 -> 5 buys tier 4 and tier 5: 16 + 25.
            Assert.Equal(41 * 10, FruitPlanState.TotalCost(10, 3, 5));
            Assert.Equal(FruitPlanState.TierCost(10, 3), FruitPlanState.TotalCost(10, 3, 4));
            Assert.Equal(0, FruitPlanState.TotalCost(10, 5, 5));
        }

        [Fact]
        public void ALaterStepForTheSameFruitStartsWhereTheEarlierOneEnds()
        {
            // Gold is at tier 2 and the plan reads Gold 4, then Gold 10: the second step is 4 -> 10.
            Assert.Equal(2, FruitPlanState.StartTier(2, 0));
            Assert.Equal(4, FruitPlanState.StartTier(2, 4));
            // Already past the earlier target: count from where the fruit really is.
            Assert.Equal(6, FruitPlanState.StartTier(6, 4));
        }

        [Fact]
        public void ATargetAboveTheCapStopsAtTheCap()
        {
            Assert.Equal(FruitPlanState.Current, State(9, 24, cap: 10));
            Assert.Equal(FruitPlanState.Cap, State(10, 24, cap: 10));
        }
    }
}
