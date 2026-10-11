using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // HOW MUCH STRONGER THE BEST GEAR WOULD MAKE THE CHARACTER, AS ONE SET.
    //
    // The projection used to take the attack gain from the best Power set and the defence gain from
    // the best Toughness set. Those are two different sets: nobody can wear both, so a requirement
    // that needs attack AND defence looked reachable when no single set reached it, and the advisor
    // committed to titan fights the character could not win.
    //
    // So every candidate is judged whole - its Power score and its Toughness score together - and
    // the multipliers returned belong to ONE of them: the set whose weaker side gains the most. What
    // is worn now is always a candidate, so the answer is never worse than staying as you are.
    //
    // Unity-free: the scores come in as plain numbers, so the choice is tested without the game.
    public static class GearProjection
    {
        public struct Candidate
        {
            public double Power, Toughness;
            public Candidate(double power, double toughness) { Power = power; Toughness = toughness; }
        }

        public static void Pick(double currentPower, double currentToughness, IEnumerable<Candidate> sets,
            out double atkMult, out double defMult)
        {
            atkMult = 1; defMult = 1;
            if (currentPower <= 0 || currentToughness <= 0 || sets == null) return;
            double best = 1;
            foreach (var s in sets)
            {
                double a = s.Power / currentPower, d = s.Toughness / currentToughness;
                double worst = Math.Min(a, d);
                if (worst <= best) continue;
                best = worst; atkMult = a; defMult = d;
            }
        }
    }
}
