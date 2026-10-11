namespace NGUAdvisor.Managers
{
    // What one step of the Yggdrasil fruit plan is doing right now. Unity-free: SpendPlanner reads
    // the game, this only names the state, so the order of the checks is tested.
    //
    // The order matters. A step is DONE the moment its tier is reached, whatever else is true. Past
    // that, the first gate that holds it is the one reported - the chapter, then the fruit not being
    // unlockable yet, then the tier cap - because that is the thing the player has to change. Only a
    // step no gate holds can be CURRENT, and only the first such step is.
    public static class FruitPlanState
    {
        public const string Done = "done", Chapter = "chapter", Locked = "locked", Cap = "cap",
                            Current = "current", Queued = "queued";

        // The game's price for the tier AFTER `tier`: base x (tier + 1)^2 (FruitController.upgrade()).
        public static long TierCost(long baseCost, long tier) => baseCost * (tier + 1) * (tier + 1);

        // Every tier a step still has to buy, from the tier it starts at up to its target.
        public static long TotalCost(long baseCost, long from, long target)
        {
            long sum = 0;
            for (long t = from; t < target; t++) sum += TierCost(baseCost, t);
            return sum;
        }

        // Where a step starts counting. The plan names one fruit several times (Gold 2, Gold 4, Gold
        // 10), so a later step begins where the one before it ends, not at the live tier - otherwise
        // its total would charge again for tiers an earlier row already lists.
        public static long StartTier(long liveTier, long earlierTarget) => liveTier > earlierTarget ? liveTier : earlierTarget;

        public static string Classify(long tier, long target, int chapter, int minChapter,
            int cap, int minCap, bool canUnlock, ref bool currentSet)
        {
            if (tier >= target) return Done;
            if (chapter < minChapter) return Chapter;
            if (tier == 0 && !canUnlock) return Locked;
            // The cap gates the step (the tier-24 push needs the cap raised) and it also bounds the
            // target: a tier-10 goal under a cap of 10 is reachable, a tier-24 one is not yet.
            if (cap < minCap || tier >= cap) return Cap;
            if (currentSet) return Queued;
            currentSet = true;
            return Current;
        }
    }
}
