using System;
using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // A TITAN THE KILL GEAR COULD NOT KILL STAYS GIVEN UP FOR A WHILE.
    //
    // ZoneHelpers drops a titan from the swap targets when it is still standing ten minutes after it
    // spawned. The advisor rebuilt the target list a minute later and put it straight back, so the
    // titan lock never let go: the character stood in front of a titan it could not kill, in kill or
    // gold-drop gear, for hours, re-arming every ten minutes. (Seen live: T2 and T3 alternating
    // "still available after 10 minutes" / "titan targets -> ..." all afternoon.)
    //
    // So a give-up is remembered per titan and version, with the projected kill stats it was given
    // up at. It holds until either stat has grown by RetryGain, or RetryAfter has passed, or the
    // version changed - any of which means the next attempt is a different fight from the lost one.
    //
    // Unity-free: the caller supplies the stats and the clock, so the rule is tested.
    public static class TitanGiveUp
    {
        public const double RetryGain = 1.25;
        public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(3);

        private struct Entry { public int Version; public double Attack, Defense; public DateTime At; }
        private static readonly Dictionary<int, Entry> _given = new Dictionary<int, Entry>();

        public static void Record(int titan, int version, double attack, double defense, DateTime now)
            => _given[titan] = new Entry { Version = version, Attack = attack, Defense = defense, At = now };

        public static bool Blocks(int titan, int version, double attack, double defense, DateTime now)
        {
            if (!_given.TryGetValue(titan, out var e)) return false;
            bool stillTheSameFight = e.Version == version
                && now - e.At < RetryAfter
                && attack < e.Attack * RetryGain
                && defense < e.Defense * RetryGain;
            if (!stillTheSameFight) _given.Remove(titan);
            return stillTheSameFight;
        }

        public static void Clear() => _given.Clear();
    }
}
