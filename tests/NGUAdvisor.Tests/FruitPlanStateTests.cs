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
        public void ATargetAboveTheCapStopsAtTheCap()
        {
            Assert.Equal(FruitPlanState.Current, State(9, 24, cap: 10));
            Assert.Equal(FruitPlanState.Cap, State(10, 24, cap: 10));
        }
    }
}
