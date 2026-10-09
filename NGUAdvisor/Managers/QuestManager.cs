using System;
using System.Collections.Generic;
using static NGUAdvisor.Main;

namespace NGUAdvisor.Managers
{
    public static class QuestManager
    {
        private static Character _character => Main.Character;
        private static readonly BeastQuestController _qc = _character.beastQuestController;
        private static bool shouldQuest;
        private static bool questBankOverfill;

        private static BeastQuest Quest => _character.beastQuest;

        public static bool BankOverfill => questBankOverfill;

        // EQUIPMENT item ids droppable per MAJOR-QUEST zone. The capstone hold (:CapstoneHold) is the
        // only consumer, and it holds a finished quest while any of these is still un-maxed.
        //
        // THE EXTRACTION RULE, and the two ways the previous table broke it:
        //
        //   ids = { every makeLoot(id) AND makeLevelledLoot(id, ...) in LootDrop.zone<N>Drop }
        //         filtered to Equipment.isEquipment() -- i.e. type[id] is one of
        //         Head/Chest/Legs/Boots/Weapon/Accessory  [DECOMP] Equipment.cs:570-577, part.cs
        //
        //   1. The old table captured only makeLevelledLoot(...) and MISSED EVERY makeLoot(...) gear
        //      id. Five of the ten reachable rows were materially incomplete as a result: zone 9 held
        //      exactly one id (437) where the game drops eight, and zones 2/5/12/13 each missed a
        //      whole boss set. The hold ended early -- or never started -- in half the zones it can
        //      run in, which is the entire feature. (audit/12 §Q-1)
        //
        //   2. The old table included part.Misc ids -- 66, 339, 367, 368, 369, 370, 371, 163. Misc
        //      items are not equipment, so the advisor never merges or boosts them
        //      (InventoryManager.cs:242), Equipment.level never leaves 0, and markItemAsMaxxed needs
        //      level >= 100 [DECOMP] AllItemListController.cs:144-153. itemMaxxed[id] could therefore
        //      NEVER become true, and because CapstoneHold breaks on the FIRST un-maxed id, the three
        //      cooking ids (367 in zone 15, 369 in zone 20, 370 in zone 22) pinned a 3-hour hold
        //      forever, on exactly the zones whose ordinary gear was already capped. (audit/12 §Q-2)
        //
        // ONLY THESE TEN ZONES CAN EVER BE ASKED FOR. CapstoneHold reads _qc.curQuestZone(), whose
        // switch returns exactly { 1, 2, 5, 9, 12, 13, 15, 20, 21, 22 } or -100
        // [DECOMP] BeastQuestController.cs:997-1013. The previous table carried 25 further rows that
        // no code path could reach; they are removed rather than left to rot un-exercised, since a
        // TryGetValue miss on an unreachable zone is unreachable too.
        public static readonly Dictionary<int, int[]> ZoneItems = new Dictionary<int, int[]>
        {
            // [DECOMP] LootDrop.cs:158  zone1Drop   (quest 278)
            { 1, new[] { 40,41,42,43,44,45,46,77 } },
            // [DECOMP] LootDrop.cs:252  zone2Drop   (quest 281) -- 53 was the missing makeLoot id
            { 2, new[] { 47,48,49,50,51,52,53,135,432 } },
            // [DECOMP] LootDrop.cs:614  zone5Drop   (quest 283) -- 68-74 were missing; 66 was Misc
            { 5, new[] { 53,68,69,70,71,72,73,74,435 } },
            // [DECOMP] LootDrop.cs:1114 zone9Drop   (quest 279) -- 95-101 were missing; row held only 437
            { 9, new[] { 95,96,97,98,99,100,101,437 } },
            // [DECOMP] LootDrop.cs:1492 zone12Drop  (quest 282) -- 122-126 were missing; 66 was Misc
            { 12, new[] { 122,123,124,125,126,127,439 } },
            // [DECOMP] LootDrop.cs:1602 zone13Drop  (quest 287) -- 76,130-134 were missing; 339 was Misc
            { 13, new[] { 76,130,131,132,133,134,440 } },
            // [DECOMP] LootDrop.cs:1780 zone15Drop  (quest 285) -- 367 was the cooking-item deadlock
            { 15, new[] { 76,143,144,145,146,147,148,441 } },
            // [DECOMP] LootDrop.cs:2467 zone20Drop  (quest 280) -- 369 was the cooking-item deadlock
            { 20, new[] { 142,221,222,223,224,225,226,227,444 } },
            // [DECOMP] LootDrop.cs:2624 zone21Drop  (quest 284) -- already correct
            { 21, new[] { 142,213,214,215,216,217,218,219,220,445 } },
            // [DECOMP] LootDrop.cs:2771 zone22Drop  (quest 286) -- 370 was the cooking-item deadlock
            { 22, new[] { 142,231,232,233,234,235,236,446 } },
        };

        // Capstone hold (advisor): a major quest is free forced-farming time in its zone — if the
        // zone's gear isn't all maxed, hold the turn-in and keep fighting so drops keep merging.
        // Guards: never against the bank-overfill predictor; 20-minute budget per quest.
        private static DateTime _capstoneStart = DateTime.MinValue;
        private static DateTime _lastHoldLog = DateTime.MinValue;

        public static string CapstoneItem { get; private set; }

        public static bool CapstoneHold()
        {
            CapstoneItem = null;
            try
            {
                if (Settings == null || !Settings.AdvisorQuests) return false;
                // OPT-IN (user 2026-07-11: a ready major parked at 100% for hours read as a hang —
                // and Gear Hunt is now the deliberate gear-farming tool, so the hold defaults off).
                if (!Settings.QuestHoldForGear) return false;
                // A pooled-major burst exists to CHAIN quests — never hold one open mid-burst;
                // and while the hunt owns farming, quest zones aren't where gear time goes.
                if (Settings.PoolMajorQuests && Settings.QuestBurstActive) return false;
                try { if (GearHunter.Active) return false; } catch { }
                if (!Quest.inQuest || Quest.reducedRewards)
                {
                    _capstoneStart = DateTime.MinValue;
                    return false;
                }
                if (questBankOverfill) return false;

                // A nearly-full inventory defeats the hold: no free slots means the zone gear we're
                // holding for can't even drop, while at-target quest items flood what's left (they
                // keep dropping on every manual kill and the game never counts them past target).
                if (FreeInventorySlots() < 4) return false;

                int zone = _qc.curQuestZone();
                if (!ZoneItems.TryGetValue(zone, out var ids)) return false;

                // Unmaxed AND actually farmable: a loot-filtered item never drops, so holding for
                // it would wait forever (log-audit find: holds expiring without progress).
                var il = _character.inventory.itemList;
                int missing = -1;
                foreach (var id in ids)
                {
                    if (id >= il.itemMaxxed.Count || il.itemMaxxed[id]) continue;
                    bool filteredOut = false;
                    try { filteredOut = id < il.itemFiltered.Count && il.itemFiltered[id]; } catch { }
                    if (filteredOut) continue;
                    missing = id;
                    break;
                }
                if (missing < 0)
                {
                    _capstoneStart = DateTime.MinValue;
                    return false;
                }

                // Budget: the bank-overfill guard above is the real cost control (banked regen never
                // wasted); the clock is only a runaway stop. 20 minutes proved far too short to cap
                // an item (user: 10 majors, nothing capped) — 3 hours of forced farm time is cheap.
                if (_capstoneStart == DateTime.MinValue) _capstoneStart = DateTime.UtcNow;
                if ((DateTime.UtcNow - _capstoneStart).TotalMinutes > 180) return false;

                CapstoneItem = Main.ItemNameNice(missing);
                try
                {
                    var slot = LoadoutManager.FindItemSlot(missing);
                    if (slot != null) CapstoneItem += $" (lv {slot.level}/100)";
                }
                catch { }
                return true;
            }
            catch (Exception e)
            {
                Main.LogDebug($"CapstoneHold: {e.Message}");
                return false;
            }
        }

        private static int FreeInventorySlots()
        {
            try
            {
                var inv = _character.inventory.inventory;
                int free = 0;
                for (int i = 0; i < inv.Count; i++)
                    if (inv[i] == null || inv[i].id == 0) free++;
                return free;
            }
            catch { return int.MaxValue; }   // unknown -> don't trip the guard
        }

        public static void PerformSlowActions()
        {
            EnforceMajorNeverIdle();
            UpdateBankOverfill();
            UpdateShouldQuest();
            CheckQuestTurnin();
        }

        // STRICT RULE (user): a major quest is NEVER run in idle mode. Idling ticks idleProgress,
        // and the first full tick clears allActive — permanently forfeiting this quest's manual
        // completion QP/AP bonus. SetIdleMode coerces new requests; this catches a major that is
        // already idling (e.g. the in-game toggle was left on when the quest started).
        private static void EnforceMajorNeverIdle()
        {
            try
            {
                if (Quest.inQuest && !Quest.reducedRewards && Quest.idleMode)
                {
                    Log("Major quest was in idle mode — forcing manual (strict rule: majors never idle)");
                    Quest.idleMode = false;
                    _qc.updateButtons();
                    _qc.updateButtonText();
                }
            }
            catch (Exception e) { Main.LogDebug($"EnforceMajorNeverIdle: {e.Message}"); }
        }

        private static void UpdateBankOverfill()
        {
            if (!Settings.AutoQuest)
            {
                questBankOverfill = false;
                return;
            }

            var slots = _qc.maxBankedQuests() - Quest.curBankedQuests + 1;
            var time = slots * _qc.timerThreshold() - Quest.dailyQuestTimer.totalseconds;
            var averageDrops = Settings.FiftyItemMinors || _character.adventure.itopod.perkLevel[94] >= 610 ? 50f : 54.5f;
            var remainingDrops = Quest.inQuest ? Quest.targetDrops - Quest.curDrops : averageDrops;
            var eta = _qc.expectedTimePerDrop() * _qc.idleDropFactor() * remainingDrops;
            // Give a bit of extra time for safety
            questBankOverfill = time * 1.1f < eta;
        }

        private static void UpdateShouldQuest()
        {
            if (!Settings.AutoQuest)
            {
                shouldQuest = false;
            }
            // Major quests take precedence over adventure zones
            else if (Quest.inQuest && !Quest.reducedRewards || Settings.QuestsFullBank && questBankOverfill)
            {
                shouldQuest = true;
            }
            else if (Settings.CombatEnabled)
            {
                // Don't quest if combat is enabled, the zone that would route is unlocked, it is not
                // the ITOPOD, and Fallthrough is not allowed.
                //
                // audit/40 §3 item 7. This used to read Settings.SnipeZone and
                // Settings.AdventureTargetITOPOD — layer-1 INTENT fields — and so decided from a zone
                // the character was not in whenever layer 2 overrode. The zone now comes from
                // Main.ResolveIntentZone, the single owner of the R10 chain, and the second copy of
                // its Target ITOPOD row is deleted; QuestStandDown carries the whole argument,
                // including why THIS consumer wants the intent rather than the routed zone.
                int intentZone = Main.ResolveIntentZone();
                var isSniping = QuestStandDown.IsSniping(intentZone,
                    CombatManager.IsZoneUnlocked(intentZone), Settings.AllowZoneFallback);

                if (isSniping)
                {
                    if (LockManager.HasQuestLock())
                        LockManager.TryQuestSwap();

                    SetIdleMode(Quest.reducedRewards && !Settings.ManualMinors);
                }

                shouldQuest = !isSniping;
            }
        }

        // One butter attempt per quest, made right before the actual turn-in (log-audit find: the old
        // at-target-minus-2 window retried a failing tryUseButter every pass — 45 minutes of
        // "Buttering Major Quest" spam while the capstone hold kept the quest at target).
        private static bool _butterAttempted;

        private static void CheckQuestTurnin()
        {
            if (!Quest.inQuest)
            {
                _butterAttempted = false;
                return;
            }

            if (_qc.readyToHandIn())
            {
                if (CapstoneHold())
                {
                    if ((DateTime.UtcNow - _lastHoldLog).TotalMinutes >= 5)
                    {
                        _lastHoldLog = DateTime.UtcNow;
                        Log($"Holding quest turn-in — maxing {CapstoneItem} while the zone is free farm time");
                        ChallengeOverlay.Record("QUEST", $"quest hold: {CapstoneItem}", "maxing zone gear before turn-in");
                    }
                    return;
                }

                if (!Quest.usedButter && !_butterAttempted)
                {
                    _butterAttempted = true;   // tryUseButter can fail (AP) — never retry-spam
                    if (Quest.reducedRewards && Settings.UseButterMinor)
                    {
                        Log("Buttering Minor Quest");
                        _qc.tryUseButter();
                    }
                    else if (!Quest.reducedRewards && Settings.UseButterMajor)
                    {
                        Log("Buttering Major Quest");
                        _qc.tryUseButter();
                    }
                }

                Log("Turning in quest");
                _qc.completeQuest();
                _butterAttempted = false;

                // Check if we need to swap back gear and release lock
                if (LockManager.HasQuestLock())
                {
                    // No more quests, swap back
                    if (_character.beastQuest.curBankedQuests == 0)
                        LockManager.TryQuestSwap();
                    // Else if majors are off and we're not manualing minors, swap back
                    else if (!Settings.AllowMajorQuests && !Settings.ManualMinors)
                        LockManager.TryQuestSwap();
                }
            }
        }

        public static int IsQuesting()
        {
            if (!Settings.AutoQuest)
                return -1;

            if (!Quest.inQuest)
                return -1;

            if (!shouldQuest)
                return -1;

            if (Quest.reducedRewards && !Settings.ManualMinors)
                return -1;

            int questZone = _qc.curQuestZone();
            if (!CombatManager.IsZoneUnlocked(questZone))
                return -1;

            EquipQuestingLoadout();
            return questZone;
        }

        private static void SetIdleMode(bool idle)
        {
            // STRICT RULE (user): a major quest is NEVER run in idle mode (see EnforceMajorNeverIdle).
            // Coerce here so no caller can idle a major, whatever its reasoning.
            if (idle && Quest.inQuest && !Quest.reducedRewards)
                idle = false;

            if (Quest.idleMode != idle)
            {
                Quest.idleMode = idle;
                _qc.updateButtons();
                _qc.updateButtonText();
            }
        }

        public static void ManageQuests()
        {
            if (!Settings.AutoQuest)
            {
                if (LockManager.HasQuestLock())
                    LockManager.TryQuestSwap();
                return;
            }

            var majorQuests = Settings.AllowMajorQuests && Quest.curBankedQuests > 0;
            // Check if Quest Bank will overfill before we can finish the current idle quest
            majorQuests |= Settings.QuestsFullBank && questBankOverfill;

            // POOL MAJOR QUESTS (user rule): bank to cap, then burn the WHOLE bank in one burst,
            // then pool again — and never start a major while Gear Hunt owns routing (the burst
            // waits at cap until the hunt is done; the wasted regen at cap is the accepted cost).
            // Burst state persists in settings so a reload mid-burst keeps burning the bank.
            if (Settings.PoolMajorQuests && Settings.AllowMajorQuests)
            {
                bool burst = Settings.QuestBurstActive;
                int cap = 10;
                try { cap = Math.Max(1, _qc.maxBankedQuests()); } catch { }
                if (!burst && Quest.curBankedQuests >= cap) burst = true;
                else if (burst && Quest.curBankedQuests <= 0) burst = false;   // in-flight major still completes
                if (burst != Settings.QuestBurstActive)
                {
                    Settings.QuestBurstActive = burst;
                    Log(burst
                        ? $"Advisor: quest bank at cap ({cap}) — bursting every banked major"
                        : "Advisor: major burst complete — pooling the quest bank again");
                    ChallengeOverlay.Record("QUEST",
                        burst ? "major quest BURST" : "burst done — pooling majors",
                        burst ? $"bank {cap}/{cap}" : "bank spent");
                }
                bool hunting = false;
                try { hunting = GearHunter.Active; } catch { }
                majorQuests = burst && !hunting && Quest.curBankedQuests > 0;
            }
            else if (Settings.QuestBurstActive)
            {
                // Pooling toggled off mid-burst: clear the persisted flag, or re-enabling pooling
                // later would burst immediately at a part-filled bank instead of pooling to cap.
                Settings.QuestBurstActive = false;
            }
            majorQuests &= shouldQuest;

            // First logic: not in a quest
            if (!Quest.inQuest)
            {
                var startQuest = false;

                // If we're allowing major quests and we have a quest available and we should quest
                if (majorQuests)
                {
                    _character.settings.useMajorQuests = true;
                    SetIdleMode(false);
                    EquipQuestingLoadout();
                    startQuest = true;
                }
                else if (!Settings.ManualMinors || shouldQuest)
                {
                    _character.settings.useMajorQuests = false;
                    SetIdleMode(!Settings.ManualMinors);

                    if (Settings.ManualMinors && shouldQuest)
                        EquipQuestingLoadout();
                    else if (LockManager.HasQuestLock())
                        LockManager.TryQuestSwap();

                    startQuest = true;
                }

                if (startQuest)
                {
                    _qc.startQuest();
                    _qc.refreshMenu();
                }
                // If we're not questing and we still have the lock, restore gear
                else if (LockManager.HasQuestLock())
                {
                    LockManager.TryQuestSwap();
                }

                return;
            }

            // Second logic, we're in a quest
            if (Quest.reducedRewards)
            {
                // While POOLING (not bursting), overfill can't start a major anyway — abandoning
                // the minor for it would just skip-loop at the cap boundary. Sitting at cap is
                // the pooling trade-off; the burst is what spends the bank.
                var abandonQuest = Settings.QuestsFullBank && questBankOverfill
                    && !(Settings.PoolMajorQuests && !Settings.QuestBurstActive);
                if (majorQuests && Settings.AbandonMinors && Quest.targetDrops > 0)
                {
                    float progress = Quest.curDrops / (float)Quest.targetDrops * 100;
                    // If all this is true get rid of this minor quest
                    abandonQuest |= progress <= Settings.MinorAbandonThreshold;
                }
                abandonQuest |= Settings.FiftyItemMinors && Quest.targetDrops - Quest.curDrops > 50;

                if (abandonQuest)
                {
                    _qc.skipQuest();
                    _qc.refreshMenu();
                }
                else
                {
                    SetIdleMode(!Settings.ManualMinors);
                }
            }
            else
            {
                SetIdleMode(false);
            }
        }

        public static void EquipQuestingLoadout()
        {
            if (!Settings.ManageQuestLoadouts)
                return;

            if (!LockManager.HasQuestLock())
            {
                if (!LockManager.TryQuestSwap())
                    Log("Tried to equip quest loadout but unable to acquire lock");
            }
            else
            {
                LockManager.RefreshQuestGear();
            }
        }
    }
}
