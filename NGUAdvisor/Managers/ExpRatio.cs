namespace NGUAdvisor.Managers
{
    // Unity-free EXP pool split — the energy vs magic fractions the balancer targets (audit M5
    // migration). Encodes the guide's difficulty/phase rules as a pure function so they're
    // headless-tested and can't silently drift:
    //
    //   Base            -> 0.5 / 0.5  (an EVEN EXP split == the 3:1 stat-VALUE ratio, since magic units
    //                                  cost 3x energy).
    //   Normal, D4       -> 0.4 / 0.6 (guide ch.4: post-CBlock2 / T6v2 and before T6v4, 2:1 value toward
    //                                  magic).
    //   Evil, pre-T7, Ygg+EXP magic NGUs capped -> 1.0 / 0.0 ENERGY ONLY (guide ch.5: "Pre-T7: buy only
    //                                  Energy AFTER you can consistently cap the Ygg/EXP magic NGUs").
    //   Evil, pre-T7, still building magic       -> 0.5 / 0.5 (keep buying magic until those NGUs cap).
    //   Evil, post-T7    -> 0.5 / 0.5 (guide ch.5: "Post-T7: balance ratios back to 3:1 Energy:Magic").
    //   Sadistic / other -> base (no separate guidance encoded here yet).
    //
    // Inputs are plain bools so the caller does the (game-coupled) difficulty/titan/NGU reads and this
    // stays pure and testable. The gated energy-only case is the point: the balancer previously used
    // base 3:1 on Evil throughout, and an earlier fix went energy-only for ALL of pre-T7 (too early) —
    // it must wait until the Ygg/EXP magic NGUs can be capped.
    public static class ExpRatio
    {
        // WHAT ONE TICK OF THE EXP WALK DOES WITH THE BANK.
        //
        // The walk spends a FRACTION of the bank per tick, spread over the stats furthest behind. That
        // works while a fraction buys whole units. It fails on a small bank in two different ways,
        // and both shipped:
        //
        //   * A flat "skip under 100 EXP" floor made any bank under 1000 unspendable -- 510 EXP x 10%
        //     is 51, so the tick bought nothing, every minute, on an account whose EXP only trickles.
        //   * Flooring the budget at the CHEAPEST unit instead fixed that and broke the walk: a point
        //     of cap costs 1 EXP, so every small bank dripped into cap one EXP a minute, and the 150
        //     a point of power costs was never saved up -- while power was the stat furthest behind.
        //
        // So a small bank is not spread at all. If a fraction of it cannot buy one unit of the stat
        // furthest behind, the tick buys exactly that one unit; and if the whole bank cannot, it
        // WAITS. Waiting is the point: it is the only way a slow bank ever reaches the price of the
        // thing the ratio says it needs most, rather than being spent on whatever is cheap.
        public enum Step { Wait, MostBehindOnly, Waterfill }

        public static Step WalkStep(double bank, double fraction, double unitOfMostBehind, out long budget)
        {
            budget = 0;
            if (double.IsNaN(bank) || bank <= 0 || unitOfMostBehind <= 0 || double.IsInfinity(unitOfMostBehind))
                return Step.Wait;
            double slice = bank * fraction;
            if (slice >= unitOfMostBehind)
            {
                budget = slice > long.MaxValue ? long.MaxValue : (long)slice;
                return Step.Waterfill;
            }
            if (bank >= unitOfMostBehind)
            {
                budget = (long)unitOfMostBehind;
                return Step.MostBehindOnly;
            }
            return Step.Wait;
        }

        public static void Split(bool isEvil, bool t7Killed, bool magicNgusCapped, bool normalMagicSkew,
                                 out double pe, out double pm)
        {
            if (isEvil)
            {
                if (t7Killed) { pe = 0.5; pm = 0.5; }              // post-T7: back to the base 3:1 value ratio
                else if (magicNgusCapped) { pe = 1.0; pm = 0.0; } // pre-T7, Ygg+EXP capped: energy only
                else { pe = 0.5; pm = 0.5; }                       // pre-T7, still capping Ygg+EXP: base 3:1
                return;
            }

            if (normalMagicSkew) { pe = 0.4; pm = 0.6; }
            else { pe = 0.5; pm = 0.5; }
        }
    }
}
