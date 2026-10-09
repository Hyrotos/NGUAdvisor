using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static NGUAdvisor.Main;

namespace NGUAdvisor.Managers
{
    public static class ZoneHelpers
    {
        public static Dictionary<int, string> ZoneList = new Dictionary<int, string>
        {
            {-1, "Safe Zone: Awakening Site"},
            {0, "Tutorial Zone"},
            {1, "Sewers"},
            {2, "Forest"},
            {3, "Cave of Many Things"},
            {4, "The Sky"},
            {5, "High Security Base"},
            {6, "Gordon Ramsay Bolton"},
            {7, "Clock Dimension"},
            {8, "Grand Corrupted Tree"},
            {9, "The 2D Universe"},
            {10, "Ancient Battlefield"},
            {11, "Jake From Accounting"},
            {12, "A Very Strange Place"},
            {13, "Mega Lands"},
            {14, "UUG THE UNMENTIONABLE"},
            {15, "The Beardverse"},
            {16, "WALDERP"},
            {17, "Badly Drawn World"},
            {18, "Boring-Ass Earth"},
            {19, "THE BEAST"},
            {20, "Chocolate World"},
            {21, "The Evilverse"},
            {22, "Pretty Pink Princess Land"},
            {23, "GREASY NERD"},
            {24, "Meta Land"},
            {25, "Interdimensional Party"},
            {26, "THE GODMOTHER"},
            {27, "Typo Zonw"},
            {28, "The Fad-Lands"},
            {29, "JRPGVille"},
            {30, "THE EXILE"},
            {31, "The Rad-lands"},
            {32, "Back To School"},
            {33, "The West World"},
            {34, "IT HUNGERS"},
            {35, "The Breadverse"},
            {36, "That 70's Zone"},
            {37, "The Halloweenies"},
            {38, "ROCK LOBSTER"},
            {39, "Construction Zone"},
            {40, "DUCK DUCK ZONE"},
            {41, "The Nether Regions"},
            {42, "AMALGAMATE"},
            {43, "7 Aethereal Seas"},
            {44, "TIPPI THE TUTORIAL MOUSE"},
            {45, "THE TRAITOR"}
        };

        // Boss (effectiveBossID) required to unlock each adventure zone, indexed by zone number, sourced 1:1
        // from AdventureController.constructDropdown. Previously omitted zone 38 (ROCK LOBSTER, also 826, the
        // second of two consecutive 826 zones with The Halloweenies) which shifted every later Sadistic
        // threshold one slot too high and left the array one short, so TitanZones[13]==45 (THE TRAITOR) indexed
        // off the end and titan 14 was never reachable/snapshotted. The zone-38 826 entry is restored below.
        // LIMITATION: zone 45 (THE TRAITOR) additionally requires the live flag character.adventure.ratTitanDefeated
        // in-game; this static table only encodes the 902 boss threshold, so GetMaxReachableZone may count zone 45
        // as reachable slightly before the rat titan is actually beaten (Sadistic-endgame only).
        public static int[] ZoneUnlocks = new int[] {
            0, 7, 17, 37, 48, 58, 58, 66, 66, 74, 82, 82, 90, 100, 100, 108, 116, 116, 124, 132, 137, // Normal
            359, 401, 426, 459, 467, 467, 475, 483, 491, 491, 501,                                    // Evil
            727, 752, 777, 810, 818, 826, 826, 834, 842, 850, 850, 871, 897, 902};                    // Sadistic (idx38 ROCK LOBSTER=826)

        private static Character _character => Main.Character;

        public static readonly int[] TitanZones = { 6, 8, 11, 14, 16, 19, 23, 26, 30, 34, 38, 42, 44, 45 };
        public const int TitanCount = 14;

        private static Dictionary<int, TitanSnapshot> _titanDetails = new Dictionary<int, TitanSnapshot>();

        private static TitanSnapshotSummary _titanSnapshotSummary = new TitanSnapshotSummary();

        public static bool ZoneIsTitan(int zone) => Array.IndexOf(TitanZones, zone) >= 0;

        // Highest boss reached AT THE CURRENT REBIRTH DIFFICULTY. The game keeps these separate: `highestBoss`
        // is Normal's all-time max and does NOT reset on Evil/Sadistic entry, so reading it on Evil returns the
        // Normal number (user-caught 2026-07-17: Evil boss 24 was read as Normal 301, so every boss-gated
        // stage/climb decision behaved as late-game). Evil is "Hard" internally, Sadistic its own counter.
        public static int CurrentHighestBoss(Character c)
        {
            try
            {
                if (c.settings.rebirthDifficulty >= difficulty.sadistic) return c.highestSadisticBoss;
                if (c.settings.rebirthDifficulty >= difficulty.evil) return c.highestHardBoss;
                return c.highestBoss;
            }
            catch (Exception ex)
            {
                Main.LogDebug($"CurrentHighestBoss read failed (difficulty={c?.settings?.rebirthDifficulty}): {ex.Message}");
                return 0;
            }
        }

        public static bool IsVersionedTitan(int titanIndex) => titanIndex >= 5 && titanIndex <= 11;

        public static bool ZoneIsWalderp(int zone) => zone == 16;

        public static bool ZoneIsBeast(int zone) => zone == 19;

        public static bool ZoneIsNerd(int zone) => zone == 23;

        public static bool ZoneIsGodmother(int zone) => zone == 26;

        public static bool ZoneIsExile(int zone) => zone == 30;

        public static bool ZoneIsItHungers(int zone) => zone == 34;

        public static bool TitanSpawningSoon(int titanIndex) => _character.buttons.adventure.IsInteractable() && IsTitanSpawningSoon(titanIndex);

        // The game's OWN enemy entry for a titan at its current version (user-reported mislabel:
        // "Walderp v1" — WALDERP #310 is just "WALDERP", no versions exist; the Beast's versions
        // are separate enemies, "THE BEAST V1" #312 / "THE BEAST V2" #313...). Versioned titans'
        // entries are found by their V-suffix; unversioned use the zone's last slot (the slot the
        // game's own autokill path uses, e.g. enemyList[16][4] for Walderp). Callers should append
        // a version tag only if this name carries one.
        public static string TitanEnemyName(int titanIndex)
        {
            try
            {
                if (titanIndex < 0 || titanIndex >= TitanZones.Length) return null;
                var list = _character.adventureController.enemyList[TitanZones[titanIndex]];
                if (list == null || list.Count == 0) return null;
                if (IsVersionedTitan(titanIndex))
                {
                    string suffix = $"V{TitanVersion(titanIndex)}";
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        var n = list[i]?.name;
                        if (n != null && n.TrimEnd().EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                            return n;
                    }
                }
                return list[list.Count - 1]?.name;
            }
            catch { return null; }
        }

        public static int TitanVersion(int titanIndex)
        {
            if (!IsVersionedTitan(titanIndex))
                return 1;

            return _character.adventure.GetFieldValue<Adventure, int>($"titan{titanIndex + 1}Version") + 1;
        }

        // HOW MANY TIMES THIS EXACT TITAN VERSION HAS DIED, from the game's own bestiary.
        //
        // Returns -1 for "no record", which callers must NOT read as zero — indices 0-4 and 12-13 have no
        // per-version entry at all, and a failed read is not evidence of never having killed it. The
        // distinction is the whole point: the field this replaces returned a confident 0 for a titan the
        // operator had killed eight times.
        //
        // ⚠ kills >= 1 CAN BE AN UNLOCK ARTIFACT. When a higher version auto-kills, the game calls
        // forceUnlock on every version below it ([DECOMP] AdventureController.cs:910-912, :928-929), which
        // seeds a lower version's count. Harmless for the question actually being asked — if v3 autokills,
        // v1 is beaten by definition — but do not treat the number as an exact kill tally.
        public static int VersionKills(int titanIndex, int version)
        {
            try
            {
                int id = TitanTables.BestiaryId(titanIndex, version);
                if (id < 0) return -1;
                var enemies = _character.bestiary.enemies;
                if (enemies == null || id >= enemies.Count) return -1;
                return enemies[id].kills;
            }
            catch { return -1; }
        }

        // The HIGHEST version of this titan that has ever died, which is what every "versions defeated"
        // caller actually wants. Highest rather than a count: forceUnlock backfills the versions below a
        // kill, so they agree in practice, and highest degrades sanely if they ever do not.
        // Falls back to the old selector proxy only where no bestiary record exists.
        public static int VersionsDefeatedByKills(int titanIndex)
        {
            if (!TitanTables.HasVersionKillRecord(titanIndex))
                return TitanTables.VersionsDefeated(TitanVersion(titanIndex));

            int highest = 0, read = 0;
            for (int v = 1; v <= 4; v++)
            {
                int k = VersionKills(titanIndex, v);
                if (k < 0) continue;          // unreadable slot — not a zero
                read++;
                if (k > 0) highest = v;
            }
            // Nothing readable at all: the bestiary is not answering, so do not claim it said none.
            return read == 0 ? TitanTables.VersionsDefeated(TitanVersion(titanIndex)) : highest;
        }

        public static void SetTitanVersion(int titanIndex, int version)
        {
            if (TitanVersion(titanIndex) == version) return;

            if (!IsVersionedTitan(titanIndex) && version != 1)
                throw new IndexOutOfRangeException();

            _character.adventure.SetFieldValue($"titan{titanIndex + 1}Version", version - 1);

            // ⚠ HAND-INSTRUMENTED, AND IT HAD TO BE. The target member name is built from a loop
            // variable — titan{n}Version — so nothing keyed on member name could ever have found this
            // site. It is one of the five the census called uninterceptable, and the only one of those
            // five that survives in the ledger, because there is exactly one call site to hook.
            //
            // Worth showing for its own sake: this field is the game's difficulty SELECTOR, three
            // advisor writers target it with no arbitration between them, and it is not the record of
            // what you have killed — that lives in the bestiary.
            WriteLedger.Record("titan.version", "T" + (titanIndex + 1) + " v" + version,
                "the advisor moved which version of this titan will spawn",
                ChallengeOverlay.Segment,
                "This is the difficulty selector, not a record of what has been beaten",
                "Three advisor paths write it: the chase, the gold-bank park and the stall valve",
                "It stays where it was last put — nothing resets it between runs");
        }

        public static bool AutokillAvailable(int titanIndex, int version)
        {
            if (titanIndex >= 12)
                return false;

            if (titanIndex >= 5)
                return _character.adventureController.CallMethod<AdventureController, bool>($"autokillTitan{titanIndex + 1}V{version}Achieved");

            if (version != 1)
                return false;

            switch (titanIndex)
            {
                case 0:
                    return _character.totalAdvAttack() >= 3000.0 && _character.totalAdvDefense() >= 2500.0;
                case 1:
                    return _character.totalAdvAttack() >= 9000.0 && _character.totalAdvDefense() >= 7000.0;
                case 2:
                    return _character.totalAdvAttack() >= 25000.0 && _character.totalAdvDefense() >= 15000.0;
                case 3:
                    var itemCheck = _character.inventory.itemList.itemMaxxed[135];
                    return _character.totalAdvAttack() >= 8e5 && _character.totalAdvDefense() >= 4e5 && _character.totalAdvHPRegen() >= 14000.0 && itemCheck;
                case 4:
                    var killCheck = _character.adventure.boss5Kills >= 3;
                    return _character.totalAdvAttack() >= 1.3e7 && _character.totalAdvDefense() >= 7e6 && _character.totalAdvHPRegen() >= 1.5e5 && killCheck;
            }

            return false;
        }

        public static bool AutokillAvailable(int titanIndex) => AutokillAvailable(titanIndex, TitanVersion(titanIndex));

        // Does a swap into loot gear pay for this titan? An auto-killed titan dies in whatever is
        // worn, so the only reason to spend a gear swap on one is its drop table: some roll still
        // carries a wanted item AND the worn gear does not already cap it.
        public static bool TitanDropChancePays(int titanIndex) => BoostFarmAdvisor.TitanGearLootFor(titanIndex).Pays;

        // Titans 6-9 (indices 5-8) sit behind a clue riddle the player solves by hand. Until it's solved
        // AdventureController.spawnEnemy serves an ordinary mob for the zone instead of the titan, so the titan
        // cannot be fought at all -- autokill stats are beside the point. Deliberately SEPARATE from
        // AutokillAvailable: that answers "are your stats good enough", which stays true and is what the UI's
        // stat chips report. This answers "can it appear", and callers that pick a titan to WAIT ON need both.
        //
        // Fails open (false) on an unreadable field so a reflection/save hiccup can't newly strand a titan the
        // player can actually fight -- same posture as the inline try/catch this replaced in AdvisorApply.
        public static bool RiddleLocked(int titanIndex)
        {
            try
            {
                var adv = _character?.adventure;
                if (adv == null) return false;
                switch (titanIndex)
                {
                    case 5: return !adv.titan6Unlocked;
                    case 6: return !adv.titan7Unlocked;
                    case 7: return !adv.titan8Unlocked;
                    case 8: return !adv.titan9Unlocked;
                    default: return false;
                }
            }
            catch { return false; }
        }

        public static int? GetHighestSpawningTitanZone()
        {
            var spawningTitans = _titanSnapshotSummary.TitansSpawningSoon;
            if (!spawningTitans.Any())
                return null;
            return TitanZones[spawningTitans.AllMaxBy(x => x.TitanIndex).First().TitanIndex];
        }

        public static bool AnyTitansSpawningSoon() => _titanSnapshotSummary.AnySpawningSoon;

        public static bool ShouldRunGoldLoadout() => _titanSnapshotSummary.RunGoldLoadout;

        public static bool ShouldRunTitanLoadout() => _titanSnapshotSummary.RunTitanLoadout;

        public static void RefreshTitanSnapshots()
        {
            if (!Main.Character.buttons.adventure.IsInteractable())
            {
                _titanSnapshotSummary = new TitanSnapshotSummary();
                _titanDetails.Clear();
                return;
            }

            int maxZone = GetMaxReachableZone(true);
            for (int titanIndex = 0; titanIndex < TitanZones.Length; titanIndex++)
            {
                if (TitanZones[titanIndex] > maxZone)
                    continue;

                var currentSnapshot = GetTitanSnapshot(titanIndex);
                if (!_titanDetails.ContainsKey(titanIndex))
                {
                    _titanDetails[titanIndex] = currentSnapshot;
                    continue;
                }

                var oldSnapshot = _titanDetails[titanIndex];
                oldSnapshot.ShouldUseGoldLoadout = currentSnapshot.ShouldUseGoldLoadout;
                oldSnapshot.ShouldUseTitanLoadout = currentSnapshot.ShouldUseTitanLoadout;

                // The titan is active, if it has been active for over 10 minutes, stats are probably not high enough to kill
                // Disable the titan to prevent sitting with suboptimal gear forever
                if (oldSnapshot.SpawnSoonTimestamp.HasValue && currentSnapshot.SpawnSoonTimestamp.HasValue && (currentSnapshot.ShouldUseGoldLoadout || currentSnapshot.ShouldUseTitanLoadout))
                {
                    // Waiting for kill...
                    if ((currentSnapshot.SpawnSoonTimestamp.Value - oldSnapshot.SpawnSoonTimestamp.Value).TotalMinutes < 10.0)
                        continue;

                    Log($"Titan {titanIndex} still available after 10 minutes");

                    var version = TitanVersion(titanIndex);
                    var reducedVersion = false;
                    while (version-- > 1)
                    {
                        if (AutokillAvailable(titanIndex, version))
                        {
                            SetTitanVersion(titanIndex, version);

                            reducedVersion = true;

                            Log($"Reducing Titan {titanIndex} version to {version}");

                            break;
                        }
                    }

                    if (!reducedVersion)
                    {
                        if (currentSnapshot.ShouldUseGoldLoadout)
                        {
                            Log($"Disabling Titan {titanIndex} as a valid gold swap target");

                            Settings.TitanGoldTargets[titanIndex] = false;

                            currentSnapshot.ShouldUseGoldLoadout = false;
                        }
                        else
                        {
                            Log($"Disabling Titan {titanIndex} as a valid swap target");

                            Settings.TitanSwapTargets[titanIndex] = false;

                            currentSnapshot.ShouldUseTitanLoadout = false;
                        }
                        Settings.SaveSettings();
                    }

                    _titanDetails[titanIndex] = currentSnapshot;
                }
                // If the timestamp is now null, the titan has been killed, flag the kill as done if the titan was set to use a gold loadout
                else if (oldSnapshot.SpawnSoonTimestamp.HasValue && !currentSnapshot.SpawnSoonTimestamp.HasValue && currentSnapshot.ShouldUseGoldLoadout)
                {
                    // Marking titan gold swap as complete (and remember at which AK version we banked,
                    // so the auto titan-gold system can re-bank when a higher version becomes killable)
                    Settings.TitanMoneyDone[titanIndex] = true;
                    try
                    {
                        var bv = Settings.TitanGoldVersionBanked;
                        if (bv != null && titanIndex < bv.Length)
                        {
                            bv[titanIndex] = TitanVersion(titanIndex);
                            Settings.TitanGoldVersionBanked = bv;
                        }
                    }
                    catch { }
                    Settings.SaveSettings();

                    _titanDetails[titanIndex] = currentSnapshot;
                }
                else
                {
                    _titanDetails[titanIndex] = currentSnapshot;
                }
            }

            // Materialize once; this collection is read repeatedly (AnySpawningSoon/RunTitanLoadout/RunGoldLoadout) and would otherwise re-run the query each access
            _titanSnapshotSummary.TitansSpawningSoon = _titanDetails.Select(x => x.Value).Where(x => x.SpawnSoonTimestamp.HasValue && (x.ShouldUseGoldLoadout || x.ShouldUseTitanLoadout)).ToList();
        }

        // The LEVEL of an accessory that is equipped RIGHT NOW, or -1 if it is not worn. Mirrors
        // [DECOMP] InventoryController.apathyCheck() exactly, which is the read the game itself makes:
        // it walks `character.inventory.accs` — the equipped slots — and returns `accs[i].level`.
        //
        // ⚠ NOT `itemList.itemMaxxed[id]`. That is a flag meaning "you have levelled this item to max"
        // and is what the AUTOKILL gate reads (AutokillAvailable case 3). Owning a maxxed ring and
        // wearing it are different facts, and only the second one keeps UUG killable.
        public static int EquippedAccessoryLevel(int itemId)
        {
            try
            {
                var accs = _character.inventory.accs;
                if (accs == null) return -1;
                for (int i = 0; i < accs.Count; i++)
                    if (accs[i] != null && accs[i].id == itemId) return accs[i].level;
            }
            catch { }
            return -1;
        }

        // "Would walking into this titan's zone be a fight we cannot win?"
        //
        // TRUE only when the titan has a required accessory, is NOT autokillable (an AK spawn never
        // reaches the fight), and that accessory is not worn. The gear swap runs BEFORE routing
        // (LockManager acquires the titan lock and calls ChangeGear, then Main routes), so by the time
        // this is asked the optimizer has already pinned the item if it could. Reaching here therefore
        // means it genuinely could not — not owned, or no accessory slot — and the fight is unwinnable
        // rather than merely unequipped.
        public static bool TitanFightBlocked(int titanIndex, out string reason)
        {
            reason = null;
            try
            {
                int required = TitanTables.RequiredAccessoryFor(titanIndex);
                if (required == 0) return false;
                if (AutokillAvailable(titanIndex)) return false;
                if (EquippedAccessoryLevel(required) >= 0) return false;

                reason = $"item {required} is not equipped as an accessory";
                if (required == TitanTables.ApathyRingId)
                    reason = "the Ring of Apathy is not equipped — without it UUG is INVINCIBLE " +
                             "([DECOMP] EnemyAI sets invincible=true when apathyCheck() returns -1)";
                return true;
            }
            catch { return false; }
        }

        public static float? TimeTillTitanSpawn(int bossId)
        {
            var spawnTime = Main.Character.adventureController.CallMethod<AdventureController, float>($"boss{bossId + 1}SpawnTime");
            var spawn = Main.Character.adventure.GetFieldValue<Adventure, PlayerTime>($"boss{bossId + 1}Spawn");

            return Mathf.Max(0f, spawnTime - (float)spawn.totalseconds);
        }

        private static TitanSnapshot GetTitanSnapshot(int titanIndex)
        {
            DateTime? spawnSoonTimestamp = IsTitanSpawningSoon(titanIndex) ? (DateTime?)DateTime.Now : null;
            bool shouldUseTitanLoadout = Settings.ManageTitans && Settings.SwapTitanLoadouts && Settings.TitanSwapTargets[titanIndex];
            bool shouldUseGoldLoadout = Settings.ManageGoldLoadouts && Settings.TitanGoldTargets[titanIndex] && !Settings.TitanMoneyDone[titanIndex];

            var titanSnapshot = new TitanSnapshot(titanIndex, spawnSoonTimestamp, shouldUseTitanLoadout, shouldUseGoldLoadout);

            return titanSnapshot;
        }

        private static bool IsTitanSpawningSoon(int bossId)
        {
            var time = TimeTillTitanSpawn(bossId);

            // The way this works is that spawn.totalseconds will count up until it reaches spawnTime and then stays there
            // This triggers the boss to be available, and after killed spawn.totalseconds will reset back down to 0
            return time < 20f;
        }

        public static int GetMaxReachableZone(bool includingTitans)
        {
            int effectiveBoss = _character.effectiveBossID();
            // Linear scan, unlocked = requirement met (<=). The old Array.BinarySearch returned an
            // ARBITRARY index on this table's duplicate boss requirements (58,58 / 66,66 / ...), which
            // is the long-reported upstream bug: riddle titans (and their zones) didn't count as
            // reachable until one boss KILL past their unlock.
            int maxZone = 0;
            for (int i = 0; i < ZoneUnlocks.Length; i++)
                if (ZoneUnlocks[i] <= effectiveBoss)
                    maxZone = i;

                for (int i = maxZone; i >= 0; i--)
                {
                    if (!ZoneIsTitan(i))
                        return i;
                    if (includingTitans)
                        return i;
                }
            return -1;
        }
    }

    public class TitanSnapshotSummary
    {
        public IEnumerable<TitanSnapshot> TitansSpawningSoon { get; set; } = new List<TitanSnapshot>();

        public bool AnySpawningSoon => TitansSpawningSoon.Any();

        public bool RunTitanLoadout => TitansSpawningSoon.Any(x => x.ShouldUseTitanLoadout);

        public bool RunGoldLoadout => TitansSpawningSoon.Any(x => x.ShouldUseGoldLoadout);
    }

    public class TitanSnapshot
    {
        public int TitanIndex { get; set; }

        public DateTime? SpawnSoonTimestamp { get; set; }

        public bool ShouldUseTitanLoadout { get; set; }

        public bool ShouldUseGoldLoadout { get; set; }

        public TitanSnapshot(int titanIndex, DateTime? spawnSoonTimestamp, bool shouldUseTitanLoadout, bool shouldUseGoldLoadout)
        {
            TitanIndex = titanIndex;
            SpawnSoonTimestamp = spawnSoonTimestamp;
            ShouldUseTitanLoadout = shouldUseTitanLoadout;
            ShouldUseGoldLoadout = shouldUseGoldLoadout;
        }
    }
}
