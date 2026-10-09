using System;
using System.Collections.Generic;
using System.Linq;

namespace NGUAdvisor.Managers
{
    // Gear-farm advisor (Farm Gear Zones): find zones whose droppable EQUIPMENT is not yet
    // level-100 (each drop merges +1 toward the permanent item-max bonus) and rank them by
    // time-to-cap at the CURRENT drop chance; when no zone caps inside the time budget, report
    // the drop chance that would.
    //
    // The roll table is extracted VERBATIM from the game's LootDrop.zoneNDrop functions
    // (scratchpad extract-geardrops.js against the decomp): each roll is
    //   P(per kill) = min(Base + Chance * dcFactor, Cap), then 1-of-Span outcomes
    // where dcFactor = lootFactor() for Normal zones and lootFactorRooted() = lootFactor^(1/3)
    // for Evil+ zones, and the roll fires only for its enemy-type branch (Normal/Boss/any).
    // Consumable IDs ride along in some pools (junk cases count toward Span — that's why Span
    // is stored, not items.Length) and are filtered out at runtime via itemInfo.type[id] <= 5,
    // the same equipment test SavedSettings uses. Items with no roll here (guaranteed early
    // drops, quest/titan specials like the Buster of the Exile, dead rolls like item 66 in
    // zones 5/7 whose in-game chance multiplies a zeroed variable) are deliberately absent:
    // they have no farmable rate.
    //
    // Rate model mirrors BoostFarmAdvisor: kill cadence ~equal across one-shottable zones
    // (respawn ~4.5s -> ~800 kills/h), enemy-type mix ~77% normal / ~10% boss. Only zones the
    // character one-shots (attack >= OPower) and has boss-unlocked compete.
    public static class GearFarmAdvisor
    {
        private class Roll
        {
            public double Chance;          // per-kill chance scale on the DC factor
            public double Base;            // flat component (rare: pendant rolls)
            public double Cap = 1.0;       // the game's Mathf.Min ceiling on the roll
            public int Span = 1;           // switch outcomes the roll splits into
            public bool Boss;              // fires on boss kills only
            public bool Normal;            // fires on normal-enemy kills only
            public int[] Items;
        }

        private const double KillsPerHour = 800.0;
        private const double NormalShare = 0.77;
        private const double BossShare = 0.10;
        // A zone is "worth farming now" if its slowest uncapped item finishes inside this budget
        // (same hours-scale ruling as the quest capstone hold: forced farm time is cheap).
        // PUBLIC so the routing instrument can print the bar a route was admitted BY rather than
        // carrying its own copy of the number (RouteChurn / AdvisorApply). Read-only to every caller;
        // the admission rules that use it all live in this file.
        public const double TargetHours = 3.0;

        private static readonly Dictionary<int, Roll[]> Table = new Dictionary<int, Roll[]>
        {
            { 0, new[] {
                new Roll { Chance = 0.25, Span = 1, Normal = true, Items = new[] { 75 } },
                new Roll { Chance = 0.15, Span = 3, Normal = true, Items = new[] { 1, 14, 27 } } } },
            { 1, new[] {
                new Roll { Chance = 0.15, Span = 3, Normal = true, Items = new[] { 1, 14, 27 } },
                new Roll { Chance = 0.65, Span = 7, Boss = true, Items = new[] { 40, 41, 42, 43, 44, 45, 46 } },
                new Roll { Chance = 0.1, Span = 1, Boss = true, Items = new[] { 77 } } } },
            { 2, new[] {
                new Roll { Chance = 0.008, Span = 1, Items = new[] { 135 } },
                new Roll { Chance = 0.12, Span = 3, Normal = true, Items = new[] { 1, 14, 27 } },
                new Roll { Chance = 0.08, Span = 3, Normal = true, Items = new[] { 2, 15, 28 } },
                new Roll { Chance = 0.5, Span = 7, Boss = true, Items = new[] { 47, 48, 49, 50, 51, 52, 53 } },
                new Roll { Chance = 0.013, Span = 1, Items = new[] { 432 } } } },
            { 3, new[] {
                new Roll { Chance = 0.13, Span = 3, Normal = true, Items = new[] { 1, 14, 27 } },
                new Roll { Chance = 0.12, Span = 3, Normal = true, Items = new[] { 2, 15, 28 } },
                new Roll { Chance = 0.75, Span = 9, Boss = true, Items = new[] { 54, 55, 56, 57, 58, 59, 60, 61, 53 } },
                new Roll { Chance = 0.0125, Span = 1, Items = new[] { 433 } } } },
            { 4, new[] {
                new Roll { Chance = 0.08, Span = 3, Normal = true, Items = new[] { 3, 16, 29 } },
                new Roll { Chance = 0.08, Span = 3, Normal = true, Items = new[] { 2, 15, 28 } },
                new Roll { Chance = 0.003, Span = 1, Boss = true, Items = new[] { 66 } },
                new Roll { Chance = 0.01, Span = 1, Boss = true, Items = new[] { 67 } },
                new Roll { Chance = 0.01, Span = 1, Boss = true, Items = new[] { 172 } },
                new Roll { Chance = 0.4, Span = 1, Boss = true, Items = new[] { 53 } },
                new Roll { Chance = 0.01, Span = 1, Items = new[] { 434 } } } },
            { 5, new[] {
                new Roll { Chance = 0.015, Span = 3, Normal = true, Items = new[] { 3, 16, 29 } },
                new Roll { Chance = 0.06, Span = 3, Normal = true, Items = new[] { 2, 15, 28 } },
                new Roll { Chance = 0.4, Span = 8, Boss = true, Items = new[] { 68, 69, 70, 71, 72, 73, 74, 53 } },
                new Roll { Chance = 0.007, Span = 1, Items = new[] { 435 } } } },
            { 7, new[] {
                new Roll { Chance = 0.03, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 3, 16, 29 } },
                new Roll { Chance = 0.03, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 4, 17, 30 } },
                new Roll { Chance = 0.3, Span = 7, Boss = true, Items = new[] { 85, 86, 87, 88, 89, 90, 91 } },
                new Roll { Chance = 0.005, Span = 1, Items = new[] { 436 } } } },
            { 9, new[] {
                new Roll { Chance = 0.07, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 4, 17, 30 } },
                new Roll { Chance = 0.07, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 5, 18, 31 } },
                new Roll { Chance = 0.32, Span = 7, Boss = true, Items = new[] { 95, 96, 97, 98, 99, 100, 101 } },
                new Roll { Chance = 0.005, Span = 1, Items = new[] { 437 } } } },
            { 10, new[] {
                new Roll { Chance = 0.06, Cap = 0.2, Span = 3, Normal = true, Items = new[] { 4, 17, 30 } },
                new Roll { Chance = 0.06, Cap = 0.2, Span = 3, Normal = true, Items = new[] { 5, 18, 31 } },
                new Roll { Chance = 0.3, Span = 7, Boss = true, Items = new[] { 103, 104, 105, 106, 107, 108, 109 } },
                new Roll { Chance = 0.0015, Span = 1, Boss = true, Items = new[] { 110 } },
                new Roll { Chance = 0.002, Span = 1, Boss = true, Items = new[] { 66 } },
                new Roll { Chance = 0.0045, Span = 1, Items = new[] { 438 } } } },
            { 12, new[] {
                new Roll { Chance = 0.03, Cap = 0.25, Span = 3, Normal = true, Items = new[] { 5, 18, 31 } },
                new Roll { Chance = 0.03, Cap = 0.25, Span = 3, Normal = true, Items = new[] { 6, 19, 32 } },
                new Roll { Chance = 0.2, Span = 5, Boss = true, Items = new[] { 122, 123, 124, 125, 126 } },
                new Roll { Chance = 0.0015, Span = 1, Boss = true, Items = new[] { 127 } },
                new Roll { Chance = 0.0025, Span = 1, Boss = true, Items = new[] { 66 } },
                new Roll { Chance = 0.004, Span = 1, Items = new[] { 439 } } } },
            { 13, new[] {
                new Roll { Chance = 0.011, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 6, 19, 32 } },
                new Roll { Chance = 0.011, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 7, 20, 33 } },
                new Roll { Chance = 0.08, Span = 5, Boss = true, Items = new[] { 130, 131, 132, 133, 134 } },
                new Roll { Chance = 0.01, Span = 1, Boss = true, Items = new[] { 76 } },
                new Roll { Chance = 0.002, Span = 1, Items = new[] { 440 } } } },
            { 15, new[] {
                new Roll { Chance = 0.0035, Cap = 0.25, Span = 3, Normal = true, Items = new[] { 6, 19, 32 } },
                new Roll { Chance = 0.0035, Cap = 0.25, Span = 3, Normal = true, Items = new[] { 7, 20, 33 } },
                new Roll { Chance = 0.01, Span = 5, Boss = true, Items = new[] { 143, 144, 145, 146, 147 } },
                new Roll { Chance = 0.0002, Span = 1, Boss = true, Items = new[] { 148 } },
                new Roll { Chance = 0.006, Span = 1, Boss = true, Items = new[] { 76 } },
                new Roll { Chance = 0.0002, Span = 1, Items = new[] { 441 } } } },
            { 17, new[] {
                new Roll { Chance = 0.001, Cap = 0.2, Span = 3, Normal = true, Items = new[] { 7, 20, 33 } },
                new Roll { Chance = 0.001, Cap = 0.2, Span = 3, Normal = true, Items = new[] { 8, 21, 34 } },
                new Roll { Chance = 0.00006, Cap = 0.05, Span = 5, Normal = true, Items = new[] { 164, 165, 166, 167, 168 } },
                new Roll { Chance = 0.00018, Cap = 0.15, Span = 5, Boss = true, Items = new[] { 164, 165, 166, 167, 168 } },
                new Roll { Chance = 0.0005, Cap = 0.1, Span = 1, Boss = true, Items = new[] { 67 } },
                new Roll { Chance = 0.00001, Cap = 0.01, Span = 1, Boss = true, Items = new[] { 128 } },
                new Roll { Chance = 0.0001, Cap = 0.01, Span = 1, Boss = true, Items = new[] { 94 } },
                new Roll { Chance = 0.00005, Cap = 0.01, Span = 1, Boss = true, Items = new[] { 163 } },
                new Roll { Chance = 0.000012, Cap = 0.03, Span = 1, Items = new[] { 442 } } } },
            { 18, new[] {
                new Roll { Chance = 0.00012, Cap = 0.2, Span = 3, Normal = true, Items = new[] { 8, 21, 34 } },
                new Roll { Chance = 0.00012, Cap = 0.2, Span = 3, Normal = true, Items = new[] { 9, 22, 35 } },
                new Roll { Chance = 0.00003, Cap = 0.04, Span = 5, Normal = true, Items = new[] { 173, 174, 175, 176, 177 } },
                new Roll { Chance = 0.00009, Cap = 0.1, Span = 5, Boss = true, Items = new[] { 173, 174, 175, 176, 177 } },
                new Roll { Chance = 0.00007, Cap = 0.01, Span = 1, Boss = true, Items = new[] { 94 } },
                new Roll { Chance = 0.00003, Cap = 0.01, Span = 1, Boss = true, Items = new[] { 163 } },
                new Roll { Chance = 0.000007, Cap = 0.01, Span = 1, Boss = true, Items = new[] { 128 } },
                new Roll { Chance = 0.000001, Cap = 0.005, Span = 1, Boss = true, Items = new[] { 178 } },
                new Roll { Chance = 0.000006, Cap = 0.02, Span = 1, Items = new[] { 443 } } } },
            { 20, new[] {
                new Roll { Chance = 0.00055, Cap = 0.1, Span = 3, Normal = true, Items = new[] { 8, 21, 34 } },
                new Roll { Chance = 0.00055, Cap = 0.1, Span = 3, Normal = true, Items = new[] { 9, 22, 35 } },
                new Roll { Chance = 0.00018, Cap = 0.08, Span = 5, Normal = true, Items = new[] { 221, 222, 223, 224, 225 } },
                new Roll { Chance = 0.00055, Cap = 0.12, Span = 5, Boss = true, Items = new[] { 221, 222, 223, 224, 225 } },
                new Roll { Chance = 0.00018, Cap = 0.12, Span = 2, Boss = true, Items = new[] { 226, 227 } },
                new Roll { Chance = 1E-9, Base = 0.001, Cap = 0.01, Span = 1, Boss = true, Items = new[] { 142 } },
                new Roll { Chance = 0.00008, Cap = 0.016, Span = 1, Items = new[] { 444 } } } },
            { 21, new[] {
                new Roll { Chance = 0.00012, Cap = 0.1, Span = 3, Normal = true, Items = new[] { 8, 21, 34 } },
                new Roll { Chance = 0.00012, Cap = 0.1, Span = 3, Normal = true, Items = new[] { 9, 22, 35 } },
                new Roll { Chance = 0.00007, Cap = 0.08, Span = 7, Normal = true, Items = new[] { 213, 214, 215, 216, 217, 218, 219 } },
                new Roll { Chance = 0.00021, Cap = 0.12, Span = 7, Boss = true, Items = new[] { 213, 214, 215, 216, 217, 218, 219 } },
                new Roll { Chance = 0.000018, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 220 } },
                new Roll { Chance = 1E-10, Base = 0.0015, Cap = 0.015, Span = 1, Boss = true, Items = new[] { 142 } },
                new Roll { Chance = 0.00002, Cap = 0.011, Span = 1, Items = new[] { 445 } } } },
            { 22, new[] {
                new Roll { Chance = 0.0001, Cap = 0.08, Span = 3, Normal = true, Items = new[] { 9, 22, 35 } },
                new Roll { Chance = 0.0001, Cap = 0.06, Span = 3, Normal = true, Items = new[] { 10, 23, 36 } },
                new Roll { Chance = 0.00003, Cap = 0.08, Span = 6, Normal = true, Items = new[] { 231, 232, 233, 234, 235, 236 } },
                new Roll { Chance = 0.0001, Cap = 0.12, Span = 6, Boss = true, Items = new[] { 231, 232, 233, 234, 235, 236 } },
                new Roll { Chance = 2E-11, Base = 0.0015, Cap = 0.02, Span = 1, Boss = true, Items = new[] { 142 } },
                new Roll { Chance = 0.000012, Cap = 0.013, Span = 1, Items = new[] { 446 } } } },
            { 24, new[] {
                new Roll { Chance = 0.00005, Cap = 0.07, Span = 3, Normal = true, Items = new[] { 10, 23, 36 } },
                new Roll { Chance = 0.00005, Cap = 0.07, Span = 3, Normal = true, Items = new[] { 11, 24, 37 } },
                new Roll { Chance = 0.000015, Cap = 0.04, Span = 7, Normal = true, Items = new[] { 251, 252, 253, 254, 255, 256, 257 } },
                new Roll { Chance = 0.00005, Cap = 0.12, Span = 7, Boss = true, Items = new[] { 251, 252, 253, 254, 255, 256, 257 } },
                new Roll { Chance = 0.00005, Cap = 0.03, Span = 1, Boss = true, Items = new[] { 142 } },
                new Roll { Chance = 0.000012, Cap = 0.03, Span = 1, Boss = true, Items = new[] { 128 } },
                new Roll { Chance = 0.000006, Cap = 0.017, Span = 1, Items = new[] { 447 } } } },
            { 25, new[] {
                new Roll { Chance = 0.00003, Cap = 0.08, Span = 3, Normal = true, Items = new[] { 10, 23, 36 } },
                new Roll { Chance = 0.00003, Cap = 0.08, Span = 3, Normal = true, Items = new[] { 11, 24, 37 } },
                new Roll { Chance = 0.000011, Cap = 0.04, Span = 7, Normal = true, Items = new[] { 258, 259, 260, 261, 262, 263, 264 } },
                new Roll { Chance = 0.000035, Cap = 0.12, Span = 7, Boss = true, Items = new[] { 258, 259, 260, 261, 262, 263, 264 } },
                new Roll { Chance = 0.000035, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 142 } },
                new Roll { Chance = 0.00001, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 128 } },
                new Roll { Chance = 0.000014, Cap = 0.017, Span = 1, Items = new[] { 448 } } } },
            { 27, new[] {
                new Roll { Chance = 0.000022, Cap = 0.09, Span = 3, Normal = true, Items = new[] { 10, 23, 36 } },
                new Roll { Chance = 0.000022, Cap = 0.09, Span = 3, Normal = true, Items = new[] { 11, 24, 37 } },
                new Roll { Chance = 0.000009, Cap = 0.04, Span = 7, Normal = true, Items = new[] { 301, 302, 303, 304, 305, 306, 307 } },
                new Roll { Chance = 0.000025, Cap = 0.12, Span = 7, Boss = true, Items = new[] { 301, 302, 303, 304, 305, 306, 307 } },
                new Roll { Chance = 0.000025, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 142 } },
                new Roll { Chance = 0.000006, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 128 } },
                new Roll { Chance = 0.000004, Cap = 0.017, Span = 1, Items = new[] { 449 } } } },
            { 28, new[] {
                new Roll { Chance = 0.000018, Cap = 0.1, Span = 3, Normal = true, Items = new[] { 11, 24, 37 } },
                new Roll { Chance = 0.000018, Cap = 0.1, Span = 3, Normal = true, Items = new[] { 12, 25, 38 } },
                new Roll { Chance = 0.000007, Cap = 0.04, Span = 7, Normal = true, Items = new[] { 308, 309, 310, 311, 312, 313, 314 } },
                new Roll { Chance = 0.000021, Cap = 0.12, Span = 7, Boss = true, Items = new[] { 308, 309, 310, 311, 312, 313, 314 } },
                new Roll { Chance = 0.000021, Cap = 0.08, Span = 1, Boss = true, Items = new[] { 142 } },
                new Roll { Chance = 0.000007, Cap = 0.08, Span = 1, Boss = true, Items = new[] { 128 } },
                new Roll { Chance = 0.0000025, Cap = 0.017, Span = 1, Items = new[] { 450 } } } },
            { 29, new[] {
                new Roll { Chance = 0.000015, Cap = 0.1, Span = 3, Normal = true, Items = new[] { 11, 24, 37 } },
                new Roll { Chance = 0.000015, Cap = 0.1, Span = 3, Normal = true, Items = new[] { 12, 25, 38 } },
                new Roll { Chance = 0.0000055, Cap = 0.04, Span = 7, Normal = true, Items = new[] { 315, 316, 317, 318, 319, 320, 321 } },
                new Roll { Chance = 0.000018, Cap = 0.12, Span = 7, Boss = true, Items = new[] { 315, 316, 317, 318, 319, 320, 321 } },
                new Roll { Chance = 0.000018, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 142 } },
                new Roll { Chance = 0.0000055, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 128 } },
                new Roll { Chance = 0.000002, Cap = 0.017, Span = 1, Items = new[] { 451 } } } },
            { 31, new[] {
                new Roll { Chance = 6E-7, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 11, 24, 37 } },
                new Roll { Chance = 6E-7, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 12, 25, 38 } },
                new Roll { Chance = 2E-7, Cap = 0.05, Span = 7, Normal = true, Items = new[] { 345, 346, 347, 348, 349, 350, 351 } },
                new Roll { Chance = 6E-7, Cap = 0.15, Span = 7, Boss = true, Items = new[] { 345, 346, 347, 348, 349, 350, 351 } },
                new Roll { Chance = 0.0000012, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 170 } },
                new Roll { Chance = 4E-7, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 169 } },
                new Roll { Chance = 8E-8, Cap = 0.017, Span = 1, Items = new[] { 452 } } } },
            { 32, new[] {
                new Roll { Chance = 4E-7, Cap = 0.1, Span = 3, Normal = true, Items = new[] { 12, 25, 38 } },
                new Roll { Chance = 4E-7, Cap = 0.1, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 1.5E-7, Cap = 0.05, Span = 7, Normal = true, Items = new[] { 352, 353, 354, 355, 356, 357, 358 } },
                new Roll { Chance = 4.5E-7, Cap = 0.15, Span = 7, Boss = true, Items = new[] { 352, 353, 354, 355, 356, 357, 358 } },
                new Roll { Chance = 4.5E-7, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 229 } },
                new Roll { Chance = 1.5E-7, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 230 } } } },
            { 33, new[] {
                new Roll { Chance = 2.5E-7, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 12, 25, 38 } },
                new Roll { Chance = 2.5E-7, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 1E-7, Cap = 0.04, Span = 7, Normal = true, Items = new[] { 359, 360, 361, 362, 363, 364, 365 } },
                new Roll { Chance = 2E-8, Cap = 0.12, Span = 1, Normal = true, Items = new[] { 366 } },
                new Roll { Chance = 3E-7, Cap = 0.15, Span = 7, Boss = true, Items = new[] { 359, 360, 361, 362, 363, 364, 365 } },
                new Roll { Chance = 0.000001, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 229 } },
                new Roll { Chance = 6E-8, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 366 } },
                new Roll { Chance = 3E-7, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 230 } } } },
            { 35, new[] {
                new Roll { Chance = 1E-7, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 12, 25, 38 } },
                new Roll { Chance = 1E-7, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 4E-8, Cap = 0.04, Span = 8, Normal = true, Items = new[] { 392, 393, 394, 395, 396, 397, 398, 399 } },
                new Roll { Chance = 1.2E-7, Cap = 0.15, Span = 8, Boss = true, Items = new[] { 392, 393, 394, 395, 396, 397, 398, 399 } },
                new Roll { Chance = 4E-7, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 229 } },
                new Roll { Chance = 1.2E-7, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 230 } } } },
            { 36, new[] {
                new Roll { Chance = 6E-8, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 6E-8, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 2.5E-8, Cap = 0.04, Span = 8, Normal = true, Items = new[] { 400, 401, 402, 403, 404, 405, 406, 407 } },
                new Roll { Chance = 8E-8, Cap = 0.15, Span = 8, Boss = true, Items = new[] { 400, 401, 402, 403, 404, 405, 406, 407 } },
                new Roll { Chance = 2.5E-7, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 229 } },
                new Roll { Chance = 8E-8, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 230 } } } },
            { 37, new[] {
                new Roll { Chance = 4E-8, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 4E-8, Cap = 0.15, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 1.6E-8, Cap = 0.04, Span = 8, Normal = true, Items = new[] { 408, 409, 410, 411, 412, 413, 414, 415 } },
                new Roll { Chance = 5E-8, Cap = 0.15, Span = 8, Boss = true, Items = new[] { 408, 409, 410, 411, 412, 413, 414, 415 } },
                new Roll { Chance = 1.6E-7, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 229 } },
                new Roll { Chance = 6E-8, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 230 } } } },
            { 39, new[] {
                new Roll { Chance = 2.5E-8, Cap = 0.16, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 2.5E-8, Cap = 0.16, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 1E-8, Cap = 0.04, Span = 8, Normal = true, Items = new[] { 453, 454, 455, 456, 457, 458, 459, 460 } },
                new Roll { Chance = 3E-8, Cap = 0.15, Span = 8, Boss = true, Items = new[] { 453, 454, 455, 456, 457, 458, 459, 460 } },
                new Roll { Chance = 1E-7, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 295 } },
                new Roll { Chance = 4E-8, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 296 } } } },
            { 40, new[] {
                new Roll { Chance = 2E-8, Cap = 0.17, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 2E-8, Cap = 0.17, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 8E-9, Cap = 0.05, Span = 8, Normal = true, Items = new[] { 496, 497, 498, 499, 500, 501, 502, 503 } },
                new Roll { Chance = 2.4E-8, Cap = 0.15, Span = 8, Boss = true, Items = new[] { 496, 497, 498, 499, 500, 501, 502, 503 } },
                new Roll { Chance = 8E-8, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 295 } },
                new Roll { Chance = 3E-8, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 296 } } } },
            { 41, new[] {
                new Roll { Chance = 1.6E-8, Cap = 0.17, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 1.6E-8, Cap = 0.17, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 6E-9, Cap = 0.05, Span = 8, Normal = true, Items = new[] { 461, 462, 463, 464, 465, 466, 467, 468 } },
                new Roll { Chance = 1.8E-8, Cap = 0.15, Span = 8, Boss = true, Items = new[] { 461, 462, 463, 464, 465, 466, 467, 468 } },
                new Roll { Chance = 6E-8, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 295 } },
                new Roll { Chance = 2.4E-8, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 296 } } } },
            { 43, new[] {
                new Roll { Chance = 1E-8, Cap = 0.17, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 1E-8, Cap = 0.17, Span = 3, Normal = true, Items = new[] { 13, 26, 39 } },
                new Roll { Chance = 4E-9, Cap = 0.05, Span = 8, Normal = true, Items = new[] { 507, 508, 509, 510, 511, 512, 513, 514 } },
                new Roll { Chance = 1.2E-8, Cap = 0.15, Span = 8, Boss = true, Items = new[] { 507, 508, 509, 510, 511, 512, 513, 514 } },
                new Roll { Chance = 4E-8, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 295 } },
                new Roll { Chance = 1.8E-8, Cap = 0.12, Span = 1, Boss = true, Items = new[] { 296 } } } },

        };

        // The drop table's zone keys and per-zone ids, for consumers that need the DROP LIST without
        // the rate model. ZonePhaseReader is the only one: the three-phase machine has to reason about
        // zones this class deliberately discards (:375 drops everything not yet one-shottable), so it
        // cannot go through Analyze. Read-only projections; the table itself stays private.
        public static IEnumerable<int> FarmZones => Table.Keys;

        public static IEnumerable<int> DroppableIds(int zone)
            => Table.TryGetValue(zone, out var rolls)
                ? rolls.SelectMany(r => r.Items).Distinct()
                : Enumerable.Empty<int>();

        public class ZonePlan
        {
            public int Zone;
            public string ZoneName;
            // The zone's own baseline gear — what "farming this zone" actually finishes. THIS and
            // only this drives HoursToCap and the ranking.
            public List<int> MissingItems = new List<int>();
            // Cross-zone chain links and ultra-rares that also drop here. Reported, never ranked:
            // HoursToCap takes the WORST item (:341), so leaving these in let one shared pendant set
            // the rating for a zone whose own set was already finished (ItemChains, "why this exists").
            public List<int> RareItems = new List<int>();
            // Per-item hours to cap, for both buckets. HoursToCap is the MAX over MissingItems
            // (:341), so without the per-item numbers "why is this zone 54h" is unanswerable.
            public Dictionary<int, double> ItemHours = new Dictionary<int, double>();
            public double HoursToCap;      // slowest missing SET item at current DC
            // The slowest SET item's time to its NEXT drop. The cadence bar the rare track already
            // uses, applied to set gear too — see Verdict.SetTarget for why the two must agree.
            public double SlowestSetCadence = double.PositiveInfinity;
            public double ReqLootFactor;   // lootFactor that brings HoursToCap inside TargetHours (0 = already there, -1 = no DC can)
            public bool Viable;            // HoursToCap <= TargetHours
        }

        // One ultra-rare / chain item, considered on its own terms rather than as zone filler.
        //
        // ELIGIBILITY IS CADENCE, NOT COMPLETION [OPERATOR 2026-08-05]: "farmed accordingly when the
        // user's DC meets the requirement to make drops regular". So the gate is the time to the NEXT
        // drop, not the time to reach level 100 — for most of these chains capping is hundreds of
        // hours at any reachable DC, and a completion gate would simply never open. HoursToFinish is
        // still reported so the choice is visible.
        public class RareTarget
        {
            public int ItemId;
            public string ItemName;
            public string ChainLabel;      // "Pendant 4/7", or null when it is a plain ultra-rare
            public int Zone;
            public string ZoneName;
            public double HoursPerDrop;    // 1 / rate — the cadence the eligibility gate reads
            public double HoursToFinish;   // DropsNeeded / rate
            public int DropsNeeded;
            public bool Eligible;          // HoursPerDrop <= TargetHours
            // ⚠ True when the DC-scaled term is negligible against the flat Base term, i.e. more drop
            // chance does NOT help. Item 142 in zones 20-22 is the case that prompted this: Base is
            // 0.001-0.0015 with Chance 1e-9 to 2e-11, so saturating the roll would need a DC around
            // 1e22 %. Telling the operator to "raise DC" there would be advice that cannot work.
            public bool DcWontHelp;
        }

        public class Verdict
        {
            public bool Known;
            public ZonePlan Best;              // best viable zone (min hours), null if none
            public ZonePlan Nearest;           // best non-viable fallback for the "need X% DC" line
            // ⚠ EVERY candidate, ranked. The "roll caps hold them past 3h" branch used to compute its
            // `fastest` plan as a LOCAL and expose nothing, so a caller wanting to say WHICH items
            // held a zone back had nothing to read — the per-zone breakdown silently printed nothing
            // on exactly the path that needed it most.
            public List<ZonePlan> Plans = new List<ZonePlan>();
            // ⚠ SET COMPLETION OUTRANKS SET-LESS ACCESSORIES [OPERATOR 2026-08-05]: "the advisor
            // should weigh capping a new set for the bonus above non set accessories. once the sets
            // are maxxed, it should go back and farm the non set accessories."
            //
            // This also closes the two-bars problem that made the inversion possible. `Best` asks
            // "can this zone be CAPPED inside 3h", which no real set passes late (PPP: 137h), while
            // the rare track asks "does a drop arrive inside 3h", which set-less strays pass easily
            // (Energy Bar Bar: 1.3h). Classification silently decided which question got asked, so a
            // set-less pair beat the only unfinished SET — which also pays a set-completion bonus.
            // SetTarget applies the RARE track's cadence bar to SET gear, so the two are comparable,
            // and then set gear wins on rank rather than on which bar it happened to be measured by.
            // PPP's slowest set item drops every ~2.1h, inside the same 3h bar.
            //
            // "Once the sets are maxxed" needs no extra code: a zone with no uncapped set items has
            // an empty MissingItems, never becomes a SetTarget, and routing falls to Rare.
            public ZonePlan SetTarget;         // best zone with uncapped SET gear at a workable cadence
            public RareTarget Rare;            // best ELIGIBLE rare target, null if none qualifies
            public RareTarget NearestRare;     // best ineligible rare, for the "why not" line

            // ⚠ SECOND PLACE IN THE SAME RANKING — the routing instrument's "by how much did it win?"
            // (audit/41 §6, RouteChurn). Taken off the very same ordered sequence as the winner
            // rather than re-derived, because a re-derived runner-up is a second copy of a ranking
            // rule and the two would drift silently — which is how a set-less pair came to outrank an
            // unfinished SET in the first place (§3, the two-bars problem).
            //
            // NOTHING ROUTES ON THESE. They are read only by the churn log; a hysteresis margin, if
            // one is ever justified by what that log measures, would be a separate and deliberate
            // change. Null when the ranking had fewer than two entries.
            public ZonePlan BestRunnerUp;      // 2nd viable zone by HoursToCap
            public ZonePlan SetRunnerUp;       // 2nd SetTarget candidate by HoursToCap
            public RareTarget RareRunnerUp;    // 2nd eligible rare by HoursPerDrop

            // For the advisory shown when the rare farm is switched OFF, so the operator can decide
            // whether the hours are worth it ([OPERATOR]: "n rare accessories available for farm,
            // approx time to farm n").
            // ⚠ ALL uncapped rares, not just the ones inside the cadence bar. The offer counted only
            // ELIGIBLE ones at first and went SILENT in the exact state it exists for: with the farm
            // off, the DC digger is benched and the Loot Hunter gear is not worn, so drop chance is
            // at its LOWEST and nothing clears the bar — yet switching the farm on is precisely what
            // seats the digger and the gear. Reporting only what already qualifies made the advisor
            // quiet about work whose cost the user was trying to weigh.
            public int RareCount;              // uncapped rares with a reachable rate
            public int RareEligible;           // ...of those, how many are inside the cadence bar now
            // ⚠ NOT a sum of every rare's hours. Rares that drop in the SAME zone advance TOGETHER —
            // 226 and 227 share one roll with Span 2 — so summing them would double-count the time.
            // This is the sum over distinct ZONES of the slowest eligible rare in each, i.e. what
            // farming them one zone after another actually costs.
            public double RareHoursAll;
            public string Text;
        }

        // Is this item still worth a drop? Equipment only (a titan's table also carries boosts),
        // not already maxxed, and not loot-filtered -- a filtered item never drops, so it can never
        // be the reason to spend a gear swap.
        private static bool StillWanted(int id, ItemList il)
        {
            if (!IsEquipment(id)) return false;
            if (id >= il.itemMaxxed.Count || il.itemMaxxed[id]) return false;
            bool filtered = false;
            try { filtered = id < il.itemFiltered.Count && il.itemFiltered[id]; } catch { }
            return !filtered;
        }

        // The drop-chance factor at which every titan roll that still carries a wanted item hits its
        // cap, for the version that will spawn and with the game's own gates applied. In the titan's
        // own domain (TitanDropTables.Rooted). 0 = drop chance buys nothing from this titan.
        public static double WantedTitanNeedFactor(int titanIndex)
        {
            try
            {
                var c = Main.Character;
                var il = c.inventory.itemList;
                int version = ZoneHelpers.TitanVersion(titanIndex);
                return TitanDropTables.NeedFactor(titanIndex, version,
                    r => TitanGateOpen(r.Gate, c) && r.Items.Any(id => StillWanted(id, il)));
            }
            catch (Exception e) { Main.LogDebug($"GearFarmAdvisor.WantedTitanNeedFactor({titanIndex}): {e.Message}"); }
            return 0;
        }

        private static bool TitanGateOpen(TitanDropTables.TitanGate gate, Character c)
        {
            switch (gate)
            {
                case TitanDropTables.TitanGate.UugRing: return c.inventory.itemList.uugRingComplete;
                case TitanDropTables.TitanGate.Waldo: return c.inventory.itemList.waldoComplete;
                case TitanDropTables.TitanGate.AntiWaldo: return c.inventory.itemList.antiWaldoComplete;
                case TitanDropTables.TitanGate.Titan9Special: return c.adventure.titan9SpecialReward;
                default: return true;
            }
        }

        private static bool IsEquipment(int id)
        {
            try { return id >= 0 && id <= Consts.MAX_GEAR_ID && (int)Main.Character.itemInfo.type[id] <= 5; }
            catch { return false; }
        }

        // Drops still needed to cap: 100 - highest owned level (a fresh drop is level 1; each
        // merge is +1). Unowned items need the full 100.
        private static int DropsNeeded(int id)
        {
            try
            {
                var slot = LoadoutManager.FindItemSlot(id);
                if (slot == null) return 100;
                return Math.Max(1, 100 - slot.level);
            }
            catch { return 100; }
        }

        // Per-hour drop rate of one roll at the given (already zone-adjusted) DC factor.
        private static double RollRate(Roll r, double dcFactor)
        {
            double p = Math.Min(r.Base + r.Chance * dcFactor, r.Cap) / r.Span;
            double share = r.Boss ? BossShare : r.Normal ? NormalShare : 1.0;
            return KillsPerHour * share * p;
        }

        // Zones whose LootDrop function uses lootFactorRooted() (Evil+): the DC factor is the
        // cube root of lootFactor. Extracted alongside the roll constants.
        private static readonly HashSet<int> RootedZones = new HashSet<int>
            { 20, 21, 22, 24, 25, 27, 28, 29, 31, 32, 33, 35, 36, 37, 39, 40, 41, 43 };

        private static double DcFactor(int zone, double lootFactor)
            => RootedZones.Contains(zone) ? Math.Pow(lootFactor, 1.0 / 3.0) : lootFactor;

        // One item's drops/hour in a zone at the given lootFactor. Zero when no roll produces it.
        private static double ItemRate(int zone, Roll[] rolls, int id, double lootFactor)
        {
            double dc = DcFactor(zone, lootFactor);
            double perHour = 0;
            foreach (var r in rolls)
                if (Array.IndexOf(r.Items, id) >= 0)
                    perHour += RollRate(r, dc);
            return perHour;
        }

        // Whether raising drop chance would materially change this item's rate here. The roll is
        // min(Base + Chance*dc, Cap): when Chance*dc is negligible against Base, the item drops at a
        // FLAT rate and no DC helps. Measured at 1000x the current DC — if the rate barely moves,
        // the term is inert. See RareTarget.DcWontHelp.
        private static bool DcIsInert(int zone, Roll[] rolls, int id, double lootFactor)
        {
            double now = ItemRate(zone, rolls, id, lootFactor);
            if (now <= 0) return false;
            double far = ItemRate(zone, rolls, id, lootFactor * 1000.0);
            return far / now < 1.05;   // <5% gain for 1000x the drop chance
        }

        // Hours until every missing item in the zone is capped, at the given lootFactor.
        private static double HoursToCap(int zone, Roll[] rolls, List<int> missing, double lootFactor)
        {
            double dc = DcFactor(zone, lootFactor);
            double worst = 0;
            foreach (var id in missing)
            {
                double perHour = 0;
                foreach (var r in rolls)
                    if (Array.IndexOf(r.Items, id) >= 0)
                        perHour += RollRate(r, dc);
                if (perHour <= 0) return double.PositiveInfinity;
                worst = Math.Max(worst, DropsNeeded(id) / perHour);
            }
            return worst;
        }

        public static Verdict Analyze()
        {
            var v = new Verdict();
            try
            {
                var c = Main.Character;
                if (c == null) return v;

                double lootFactor = c.lootFactor();
                double attack = ZoneStatHelper.EffectiveAdvAttack();
                var il = c.inventory.itemList;

                var plans = new List<ZonePlan>();
                var rareCandidates = new List<RareTarget>();
                foreach (var kv in Table)
                {
                    int zone = kv.Key;
                    try
                    {
                        if (ZoneHelpers.ZoneIsTitan(zone)) continue;
                        // Same unlock gate as the boost advisor, single-sourced + headless-tested (audit M5).
                        if (!BossScale.IsZoneUnlocked(c.effectiveBossID(), zone, ZoneHelpers.ZoneUnlocks)) continue;
                        // Only one-shottable zones farm at full cadence (same gate as the boost advisor).
                        // FAIL CLOSED: an unknown zone is NOT one-shottable. See ZoneGate.
                        var ztable = ZoneStatHelper.UserOverrides;
                        ZoneStats st = null;
                        bool rowFound = ztable != null && ztable.TryGetValue(zone, out st);
                        var gate = ZoneGate.Evaluate(ztable != null, rowFound, rowFound ? st.OPower : 0, attack);
                        if (!gate.Known && ZoneGate.ShouldAnnounce("GearFarm", zone))
                            Main.LogDebug($"GearFarmAdvisor: zone {zone} treated as NOT one-shottable — {gate.Reason}");
                        if (!gate.OneShottable) continue;

                        // Every un-maxxed, un-filtered equipment id the zone drops...
                        var uncapped = new List<int>();
                        foreach (var id in kv.Value.SelectMany(r => r.Items).Distinct())
                        {
                            if (!IsEquipment(id)) continue;
                            if (id >= il.itemMaxxed.Count || il.itemMaxxed[id]) continue;
                            bool filtered = false;
                            try { filtered = id < il.itemFiltered.Count && il.itemFiltered[id]; } catch { }
                            if (filtered) continue;   // a loot-filtered item never drops
                            uncapped.Add(id);
                        }
                        if (uncapped.Count == 0) continue;

                        // ...split into the zone's OWN SET GEAR and everything else. Only set gear
                        // rates the zone.
                        //
                        // THE DISCRIMINATOR IS THE GAME'S OWN maxxedXxx() MEMBERSHIP (ItemSets), not
                        // a rate heuristic and not the chain list. Both of those were tried and both
                        // failed on live data: item 142 was already maxxed so the chain split
                        // excluded nothing, and 220 runs only ~5.9x slower than the Evilverse set
                        // rolls, well inside the 20x bar. What actually distinguishes 220/226/227 is
                        // that the game counts them toward no set completion at all.
                        //
                        // ⚠ THE RARITY YARDSTICK IS OVER ALL DROPPABLE EQUIPMENT, not just the
                        // uncapped ids. Measuring it over `uncapped` made rarity depend on what the
                        // player had already maxxed — a zone with one uncapped item scored itself
                        // against itself, ratio 1, and could never be rare. That was a real defect in
                        // the first cut of this split.
                        double bestRate = 0;
                        foreach (var id in kv.Value.SelectMany(r => r.Items).Distinct())
                        {
                            if (!IsEquipment(id)) continue;
                            var rr = ItemRate(zone, kv.Value, id, lootFactor);
                            if (rr > bestRate) bestRate = rr;
                        }

                        var missing = new List<int>();
                        var rares = new List<int>();
                        foreach (var id in uncapped)
                        {
                            if (ItemSets.IsSetMember(id)
                                && !ItemChains.IsRareInZone(ItemRate(zone, kv.Value, id, lootFactor), bestRate))
                                missing.Add(id);
                            else
                                rares.Add(id);
                        }

                        // Collect the rares for the separate track before deciding whether this zone
                        // still has baseline work — a zone whose ONLY uncapped items are rares drops
                        // out of the ranking below, and its rares must not vanish with it.
                        foreach (var id in rares)
                        {
                            var rate = ItemRate(zone, kv.Value, id, lootFactor);
                            if (rate <= 0) continue;
                            int need = DropsNeeded(id);
                            var rt = new RareTarget
                            {
                                ItemId = id,
                                ItemName = ItemName(id),
                                ChainLabel = ItemChains.Label(id),
                                Zone = zone,
                                ZoneName = ZoneHelpers.ZoneList.TryGetValue(zone, out var rn) ? rn : $"Zone {zone}",
                                HoursPerDrop = 1.0 / rate,
                                HoursToFinish = need / rate,
                                DropsNeeded = need,
                                DcWontHelp = DcIsInert(zone, kv.Value, id, lootFactor)
                            };
                            rt.Eligible = rt.HoursPerDrop <= TargetHours;
                            rareCandidates.Add(rt);
                        }

                        if (missing.Count == 0) continue;   // nothing but rares here — not a farm zone

                        var plan = new ZonePlan
                        {
                            Zone = zone,
                            ZoneName = ZoneHelpers.ZoneList.TryGetValue(zone, out var n) ? n : $"Zone {zone}",
                            MissingItems = missing,
                            RareItems = rares,
                            HoursToCap = HoursToCap(zone, kv.Value, missing, lootFactor)
                        };
                        double slowestSet = 0;
                        foreach (var id in uncapped)
                        {
                            var ir = ItemRate(zone, kv.Value, id, lootFactor);
                            plan.ItemHours[id] = ir > 0 ? DropsNeeded(id) / ir : double.PositiveInfinity;
                            if (missing.Contains(id))
                            {
                                double cad = ir > 0 ? 1.0 / ir : double.PositiveInfinity;
                                if (cad > slowestSet) slowestSet = cad;
                            }
                        }
                        plan.SlowestSetCadence = missing.Count > 0 ? slowestSet : double.PositiveInfinity;
                        plan.Viable = plan.HoursToCap <= TargetHours;

                        // Required lootFactor for the budget: rates are monotonic in DC, so binary
                        // search; if even a huge DC can't cap in budget (roll caps), report -1.
                        if (plan.Viable) plan.ReqLootFactor = 0;
                        else if (double.IsInfinity(HoursToCap(zone, kv.Value, missing, lootFactor * 1e9))
                            || HoursToCap(zone, kv.Value, missing, lootFactor * 1e9) > TargetHours)
                            plan.ReqLootFactor = -1;
                        else
                        {
                            double lo = lootFactor, hi = lootFactor * 1e9;
                            for (int i = 0; i < 60; i++)
                            {
                                double mid = Math.Sqrt(lo * hi);   // geometric: the range spans decades
                                if (HoursToCap(zone, kv.Value, missing, mid) <= TargetHours) hi = mid;
                                else lo = mid;
                            }
                            plan.ReqLootFactor = hi;
                        }
                        plans.Add(plan);
                    }
                    catch { }
                }

                v.Known = true;
                v.Plans = plans.OrderBy(p => p.HoursToCap).ToList();
                // Materialised so the runner-up comes off THE SAME ordered sequence as the winner.
                var viable = plans.Where(p => p.Viable).OrderBy(p => p.HoursToCap).ToList();
                v.Best = viable.FirstOrDefault();
                v.BestRunnerUp = viable.Skip(1).FirstOrDefault();
                v.Nearest = plans.Where(p => !p.Viable && p.ReqLootFactor > 0)
                    .OrderBy(p => p.ReqLootFactor).FirstOrDefault();

                // Set gear that is not cappable inside the budget but IS dropping at a workable
                // cadence. Ranked by time-to-finish so the nearest set completes first; the cadence
                // test is only the admission bar. Excludes anything Best already covers.
                var setRanked = plans
                    .Where(p => p.MissingItems.Count > 0 && !p.Viable && p.SlowestSetCadence <= TargetHours)
                    .OrderBy(p => p.HoursToCap)
                    .ToList();
                v.SetTarget = setRanked.FirstOrDefault();
                v.SetRunnerUp = setRanked.Skip(1).FirstOrDefault();

                // The rare track. Fastest CADENCE wins, not fastest completion: the point of an
                // eligible rare is that drops arrive regularly enough to be worth standing there.
                // A chain item is preferred on a tie because most of both chains is drop chance,
                // which compounds into every later farm (ItemChains, "what they do").
                var rareRanked = rareCandidates.Where(r => r.Eligible)
                    .OrderBy(r => r.HoursPerDrop)
                    .ThenByDescending(r => r.ChainLabel != null)
                    .ToList();
                v.Rare = rareRanked.FirstOrDefault();
                v.RareRunnerUp = rareRanked.Skip(1).FirstOrDefault();
                v.NearestRare = rareCandidates.Where(r => !r.Eligible)
                    .OrderBy(r => r.HoursPerDrop).FirstOrDefault();

                v.RareCount = rareCandidates.Count;
                v.RareEligible = rareCandidates.Count(r => r.Eligible);
                // Same-zone rares advance together, so this is NOT a sum — see RareRollup.
                v.RareHoursAll = RareRollup.SequentialHours(
                    rareCandidates.Select(r => r.Zone).ToList(),
                    rareCandidates.Select(r => r.HoursToFinish).ToList());

                if (v.Best != null)
                {
                    v.Text = $"Gear farm: {v.Best.ZoneName} — {v.Best.MissingItems.Count} item(s) uncapped, ~{FmtHours(v.Best.HoursToCap)} to cap";
                }
                else if (v.Nearest != null)
                {
                    v.Text = $"No gear zone caps within {TargetHours:0}h — closest is {v.Nearest.ZoneName} (needs ~{v.Nearest.ReqLootFactor * 100:#,0}% drop chance)";
                }
                else if (plans.Count > 0)
                {
                    // Uncapped gear exists but the game's per-roll chance CAPS keep every zone past
                    // the budget no matter the DC — honest answer: show the floor; partial levels
                    // accumulated by the boost-farm routing shrink it over time.
                    var fastest = plans.OrderBy(p => p.HoursToCap).First();
                    v.Text = $"Gear uncapped in {plans.Count} zone(s), but roll caps hold them past {TargetHours:0}h — fastest is {fastest.ZoneName} (~{FmtHours(fastest.HoursToCap)})";
                }
                else
                {
                    // ⚠ "SET gear", not "gear". `plans` holds only zones with uncapped SET items —
                    // a zone whose sole remaining items are cross-zone chain links or set-less
                    // strays is skipped (`if (missing.Count == 0) continue;`). Saying "all farmable
                    // zone gear is capped" while rares are outstanding was false, and it was the
                    // message the operator saw when the offer also stayed silent.
                    v.Text = v.RareCount > 0
                        ? $"All zone SET gear is capped — {v.RareCount} rare accessor"
                          + (v.RareCount == 1 ? "y" : "ies") + " still uncapped"
                        : "All farmable zone gear is capped";
                }
                return v;
            }
            catch (Exception e) { Main.LogDebug($"GearFarmAdvisor: {e.Message}"); return v; }
        }

        private static string FmtHours(double h)
            => double.IsInfinity(h) ? "never" : h >= 1 ? $"{h:0.#}h" : $"{h * 60:0}m";

        // [DECOMP] ItemNameDesc.cs:92 constructItemInfo() populates itemName[] in code, so the live
        // read is authoritative and needs no local table. Falls back to the bare id.
        internal static string ItemName(int id)
        {
            try
            {
                var n = Main.Character.itemInfo.itemName[id];
                return string.IsNullOrEmpty(n) ? $"#{id}" : n;
            }
            catch { return $"#{id}"; }
        }

        // The per-zone missing list, named — [OPERATOR] asked for this directly. Two buckets, because
        // conflating them is the bug: the SET items are what farming this zone finishes, the RARE
        // ones are cross-zone chain links that merely also drop here and used to set the zone's whole
        // rating. Levels are shown as merges remaining, since that is the unit that actually moves.
        public static string DescribeMissing(ZonePlan p)
        {
            if (p == null) return "";
            var setPart = p.MissingItems.Count == 0
                ? "nothing ranked"
                : string.Join(", ", p.MissingItems
                    .OrderByDescending(id => Hours(p, id))
                    .Select(id => $"{ItemName(id)} x{DropsNeeded(id)} ~{FmtHours(Hours(p, id))}").ToArray());
            if (p.RareItems.Count == 0) return setPart;
            var rarePart = string.Join(", ", p.RareItems
                .OrderByDescending(id => Hours(p, id))
                .Select(id =>
                {
                    var lbl = ItemChains.Label(id) ?? (ItemSets.IsSetMember(id) ? "rare" : "no set");
                    return $"{ItemName(id)} [{lbl}] x{DropsNeeded(id)} ~{FmtHours(Hours(p, id))}";
                }).ToArray());
            return $"{setPart} | NOT RANKED: {rarePart}";
        }

        private static double Hours(ZonePlan p, int id)
            => p.ItemHours.TryGetValue(id, out var h) ? h : double.PositiveInfinity;
    }
}
