using System;
using System.Collections.Generic;
using System.Linq;
using static NGUAdvisor.Main;

namespace NGUAdvisor.Managers
{
    public static class LoadoutManager
    {
        private static Character _character => Main.Character;
        private static readonly InventoryController _ic = Main.InventoryController;

        // The last swap, so the companion can explain a result that LOOKS wrong but isn't. Three
        // distinct outcomes, and conflating them is what makes gear swaps confusing:
        //   Equipped — went on as asked.
        //   Kept     — a slot this objective scores nothing for, still holding what it held. BY DESIGN
        //              (ChangeGear only swaps in); this is the Power/Toughness that survives a
        //              Gold Drops swap. Not a failure.
        //   Missed   — asked for and did NOT go on. The only one that is actually a problem.
        public sealed class SwapOutcome
        {
            public string Mode;
            public int[] Requested = new int[0];
            public int[] Equipped = new int[0];
            public int[] Missed = new int[0];
            public int[] Kept = new int[0];
            public DateTime At;
        }
        public static SwapOutcome LastSwap { get; private set; }

        // Set by ChangeGear the moment it zeroes allocation; CLEARED by CustomAllocation the moment the
        // next allocation pass finishes, which is what turns it into a measured duration. Null means
        // "no swap is currently waiting to be re-seated", which is the steady state.
        public static DateTime? AllocationClearedAt { get; set; }

        private static int[] _savedLoadout;
        private static int[] _tempLoadout;
        private static int[] _savedDaycare;

        private static Inventory Inventory => _character.inventory;

        private static List<Equipment> Daycare => Inventory.daycare;

        public static void RestoreGear()
        {
            Log($"Restoring original loadout");
            // The advisor withdrawing its own write. Distinct from a stale row: the field is back to
            // something the operator owns, which is the outcome an undo would have produced anyway.
            // ChangeGear runs immediately after this and records the restore as its own new row, so the
            // ledger shows the withdrawal and the replacement rather than one row mutating in place.
            WriteLedger.MarkReverted("gear.equipped");
            // Cause.Restore: this UNDOES a swap and must never be gated — see GearChangeGate.Cause.
            ChangeGear(_savedLoadout, GearChangeGate.Cause.Restore);
        }

        // Whether the No Equipment refusal has already been narrated, so the line fires once per
        // transition rather than once per attempted swap. Session state by design; the DECISION is
        // GearChangeGate.TransitionLine and is pure (same split as AugmentBP's _noAugsSurfaced latch).
        private static bool _noecSurfaced;

        // The gated entry point. Every caller that does not name a cause is the advisor acting on its
        // own initiative, which is the case F3 is about — so the default is the gated one.
        public static void ChangeGear(int[] gearIds, bool shockwave = false) =>
            ChangeGear(gearIds, GearChangeGate.Cause.Advisor, shockwave);

        public static void ChangeGear(int[] gearIds, GearChangeGate.Cause cause, bool shockwave = false)
        {
            if (gearIds?.Length > 0 == false)
                return;

            // F3. THE ONE PREDICATE, at the ONE entry point — deliberately not at the fifteen external
            // call sites, which is how a rule like this rots (38 §E7 inverted). During No Equipment,
            // gear contributes 0f to every spec ([DECOMP] InventoryController.cs:647) while every swap
            // still pays removeAllEnergyAndMagic() below, so an advisor-initiated swap is pure cost.
            //
            // Read LIVE and never latched: the gate lifts the instant the challenge ends, with no
            // cached state, exactly like the Pass 1 predicates (constraint-layer-spec §4.5).
            bool inNoec;
            try { inNoec = ChallengeDetector.Current() == "NOEC"; }
            catch { inNoec = false; }   // fail OPEN: an unreadable challenge state must not strand gear

            try
            {
                var line = GearChangeGate.TransitionLine(inNoec, _noecSurfaced);
                if (line != null) Log(line);
                _noecSurfaced = inNoec;

                // The ignored keypress gets its own line, EVERY time, and is deliberately not folded
                // into the latch above: the user pressed F8 and the gear did not move, which without a
                // line is indistinguishable from a broken hotkey. [OPERATOR] ruling.
                var keyed = GearChangeGate.IgnoredHotkeyLine(inNoec, cause);
                if (keyed != null) Log(keyed);
            }
            catch { }

            if (GearChangeGate.Blocks(inNoec, cause))
                return;

            if (GetCurrentGear().Where(x => x > 0).Distinct().OrderBy(x => x).SequenceEqual(gearIds.Where(x => x > 0).Distinct().OrderBy(x => x)))
                return;

            Log($"Received New Gear for {LockManager.GetLockTypeName()}: {string.Join(", ", gearIds)}");
            var headSwapped = false;
            var chestSwapped = false;
            var legsSwapped = false;
            var bootsSwapped = false;
            var weaponSlot = -5;
            var accSlot = 10000;

            // UNASSIGN EVERYTHING FIRST — and it has to be removeALL, not removeMOST.
            //
            // The game SILENTLY REVERTS an accessory swap when committed resources exceed the new cap.
            // InventoryController.swapAcc does the swap, recomputes totalCapEnergy/Magic/Res3, and then:
            //     if (curEnergy - idleEnergy > newCap) { showOverrideTooltip("You need to free up ...
            //         Idle Energy before swapping these 2 accessories"); swapAccs(num, num2); }
            // i.e. it swaps BACK and reports it through a tooltip an injected advisor never sees. The
            // swap returns void, so nothing downstream can tell it failed. Symptom (user-reported
            // 2026-07-31): a titan swap equips the weapons and armour but only some of the accessories,
            // with no error anywhere in the log.
            //
            // removeMostEnergy is not enough because it deliberately omits Basic Training —
            // allOffenseController/allDefenseController are in removeAllEnergy but NOT in
            // removeMostEnergy (decomp Character.cs) — so training energy stays COMMITTED across the
            // swap, keeps curEnergy - idleEnergy above zero, and trips the revert on any accessory that
            // lowers the energy cap. The same applies to magic and R3.
            //
            // This is exactly what the game's own loadout swap does (InventoryController.equipLoadout),
            // behind its `unassignWhenSwapping` setting. The advisor takes that branch unconditionally:
            // a swap that half-applies is worse than a re-allocation, and the next allocation pass is at
            // most one tick away.
            // One call covers all three resources: it ends with allOffense/allDefense (Basic Training),
            // hacksController.removeAllR3() and wishesController.removeAllRes3(), so a separate
            // removeAllRes3() here would be a pure no-op.
            _character.removeAllEnergyAndMagic();
            // Mirror equipLoadout's instaTrain top-up. With instant training on, the game re-seeds 6/6
            // right after unassigning so training keeps ticking; skipping it would stall instaTrain
            // until the next allocation pass for no reason.
            try
            {
                if (_character.arbitrary.instaTrain && _character.idleEnergy >= 12)
                {
                    _character.idleEnergy -= 12L;
                    _character.training.attackEnergy[0] += 6L;
                    _character.training.defenseEnergy[0] += 6L;
                }
            }
            catch { }

            try
            {
                foreach (var itemId in gearIds)
                {
                    var equip = FindItemSlot(itemId, shockwave);

                    if (equip == null)
                    {
                        try
                        {
                            Log($"Missing item {_ic.itemInfo.itemName[itemId]} with ID {itemId}");
                        }
                        catch (Exception)
                        {
                            // pass
                        }

                        continue;
                    }

                    if (equip.slot >= 100000)
                    {
                        if (!equip.equipment.isEquipment())
                            continue;

                        var newSlot = InventoryManager.MoveFromDaycareToInventory(Inventory, equip.slot);
                        if (newSlot < 0)
                        {
                            try
                            {
                                Log("Failed to move an item from daycare: missing empty slots in the inventory.");
                            }
                            catch (Exception)
                            {
                                // pass
                            }

                            continue;
                        }
                        equip.slot = newSlot;
                    }

                    var type = equip.equipment.type;

                    Inventory.item2 = equip.slot;
                    switch (type)
                    {
                        case part.Head when !headSwapped:
                            Inventory.item1 = -1;
                            _ic.swapHead();
                            headSwapped = true;
                            break;
                        case part.Chest when !chestSwapped:
                            Inventory.item1 = -2;
                            _ic.swapChest();
                            chestSwapped = true;
                            break;
                        case part.Legs when !legsSwapped:
                            Inventory.item1 = -3;
                            _ic.swapLegs();
                            legsSwapped = true;
                            break;
                        case part.Boots when !bootsSwapped:
                            Inventory.item1 = -4;
                            _ic.swapBoots();
                            bootsSwapped = true;
                            break;
                        case part.Weapon when weaponSlot == -5:
                            Inventory.item1 = -5;
                            _ic.swapWeapon();
                            weaponSlot--;
                            break;
                        case part.Weapon when weaponSlot == -6 && _ic.weapon2Unlocked():
                            Inventory.item1 = -6;
                            _ic.swapWeapon2();
                            break;
                        case part.Accessory:
                            if (_ic.accessoryID(accSlot) < _ic.accessorySpaces() && accSlot != equip.slot)
                            {
                                Inventory.item1 = accSlot;
                                _ic.swapAcc();
                            }
                            accSlot++;
                            break;
                        default:
                            continue;
                    }
                }
            }
            catch (Exception e)
            {
                Log(e.Message);
                Log(e.StackTrace);
            }

            _ic.updateBonuses();
            _ic.updateInventory();

            UpdateResources();

            // VERIFY. swapAcc/swapHead/... all return void, and swapAcc can silently revert itself (see
            // the unassign note above), so "Finished equipping gear" was previously an unconditional
            // claim that the swap had worked. A partial swap could not appear in the log even in
            // principle — which is why a user-reported one had nothing to show for it.
            //
            // Compared as SETS: ChangeGear's own early-out uses the same distinct-sorted comparison, the
            // caller's list can legitimately contain ids that do not fit (more accessories than unlocked
            // slots), and slot ORDER is not something this method promises.
            try
            {
                var want = gearIds.Where(x => x > 0).Distinct().ToArray();
                var wornNow = GetCurrentGear().Where(x => x > 0).Distinct().ToArray();
                var got = new HashSet<int>(wornNow);
                var asked = new HashSet<int>(want);
                var missed = want.Where(x => !got.Contains(x)).ToArray();
                // KEPT is not a failure and must never be reported as one. ChangeGear only ever swaps
                // IN — no path clears a slot the loadout does not name — so anything worn that the
                // loadout did not ask for is a slot this objective had no opinion about, still holding
                // what it held before. That is the hybrid that keeps Power/Toughness on during a
                // Gold Drops swap and is why high-zone gold snipes survive. It LOOKS like a half-done
                // swap, which is exactly why it is worth naming rather than leaving the user to guess.
                var kept = wornNow.Where(x => !asked.Contains(x)).ToArray();

                LastSwap = new SwapOutcome
                {
                    Mode = LockManager.GetLockTypeName(),
                    Requested = want,
                    Equipped = wornNow,
                    Missed = missed,
                    Kept = kept,
                    At = DateTime.UtcNow
                };

                // WHAT THE SWAP COST, ARMED HERE AND REPORTED WHEN THE COST IS KNOWN.
                //
                // Every swap begins by calling Character.removeAllEnergyAndMagic() (line ~124 above),
                // which zeroes committed energy/magic/R3 across EIGHT controllers — Wandoos, Augments,
                // Time Machine, Basic Training, NGUs, Wishes, Blood Magic and Hacks. Eight is a constant
                // of the game's method, not something to count here.
                //
                // Nothing re-allocates on the way out, so this stamp is also the REQUEST for it:
                // Main.QuickStuff (0.5 s) runs an allocation pass while it is armed. Before that the
                // re-seat was ambient and in practice waited for the 10 s AutomationRoutine.
                //
                // CustomAllocation reports the MEASURED gap when that pass completes and clears the
                // stamp, so the number the operator reads is the number that happened. Twelve triggers
                // reach this method and all of them pass through here.
                AllocationClearedAt = DateTime.UtcNow;

                // One choke point, every equip in the product. Breakpoints, all six lock subsystems,
                // the optimizer, the F8 hotkey — they all land here, which is why gear is the cheapest
                // thing in the registry to instrument and the hardest to miss.
                WriteLedger.Record("gear.equipped",
                    LastSwap.Mode + " · " + want.Length + " item" + (want.Length == 1 ? "" : "s"),
                    missed.Length == 0
                        ? "objective change — " + kept.Length + " slot(s) kept what they had"
                        : missed.Length + " requested item(s) could not be equipped",
                    ChallengeOverlay.Segment,
                    "Requested " + want.Length + ", worn " + wornNow.Length + ", kept " + kept.Length +
                        (missed.Length > 0 ? ", missed " + missed.Length : ""),
                    "Every swap first zeroes committed energy, magic and R3 across eight controllers",
                    "Re-seated by the next fast-loop pass, about half a second later");

                if (missed.Length == 0)
                    Log(kept.Length == 0
                        ? "Finished equipping gear"
                        : $"Finished equipping gear — {want.Length} swapped in, {kept.Length} slot(s) kept " +
                          "what they had (this objective scores nothing for them)");
                else
                    Log($"Finished equipping gear — {missed.Length} of {want.Length} did NOT go on: " +
                        $"{string.Join(", ", missed.Select(x => x.ToString()).ToArray())}. " +
                        "Usual causes: more accessories than unlocked slots, or the game refused the swap.");
            }
            catch { Log("Finished equipping gear"); }
        }

        public static void FillDaycare()
        {
            if (Settings.Shockwave.Length > 0)
            {
                var missingDaycare = Settings.Shockwave.Except(Daycare.Select(x => x.id));
                if (!missingDaycare.Any())
                    return;

                Log($"Putting gear into daycare: {string.Join(", ", missingDaycare)}");

                var availableSlots = new Queue<int>();
                for (int i = 0; i < Daycare.Count; i++)
                {
                    var slotInfo = Daycare[i];
                    if (slotInfo.id == 0)
                        availableSlots.Enqueue(i + 100000);
                }

                if (Settings.MoneyPitDaycare)
                {
                    for (int i = 0; i < Daycare.Count; i++)
                    {
                        var slotInfo = Daycare[i];
                        if (slotInfo.id == 0)
                            continue;
                        if (Array.IndexOf(Settings.Shockwave, slotInfo.id) >= 0)
                            continue;
                        if (_ic.daycares[i].daycareSlider.value < Settings.DaycareThreshold / 100f)
                            availableSlots.Enqueue(i + 100000);
                    }
                }

                foreach (var itemId in missingDaycare)
                {
                    if (availableSlots.Count <= 0)
                        break;

                    var equip = FindItemSlot(itemId, true);
                    if (equip == null)
                    {
                        try
                        {
                            Log($"Missing item {_ic.itemInfo.itemName[itemId]} with ID {itemId}");
                        }
                        catch (Exception)
                        {
                            // pass
                        }

                        continue;
                    }

                    if (equip.slot < 0 || _ic.accessoryID(equip.slot) >= 0)
                    {
                        var emptySlot = Inventory.inventory.FindIndex(x => x.id == 0);
                        if (emptySlot < 0)
                            continue;

                        _character.removeMostEnergy();
                        _character.removeMostMagic();
                        _character.removeAllRes3();

                        Inventory.item1 = equip.slot;
                        Inventory.item2 = emptySlot;
                        switch (equip.equipment.type)
                        {
                            case part.Head:
                                _ic.swapHead();
                                break;
                            case part.Chest:
                                _ic.swapChest();
                                break;
                            case part.Legs:
                                _ic.swapLegs();
                                break;
                            case part.Boots:
                                _ic.swapBoots();
                                break;
                            case part.Weapon:
                                _ic.swapWeapon();
                                break;
                            case part.Accessory:
                                _ic.swapAcc();
                                break;
                            default:
                                continue;
                        }
                    }
                    else
                    {
                        Inventory.item2 = equip.slot;
                    }
                    Inventory.item1 = availableSlots.Dequeue();
                    _ic.swapDaycare();
                }

                _ic.updateBonuses();
                _ic.updateInventory();

                UpdateResources();

                Log("Finished putting gear into daycare");
            }
        }

        public static ih FindItemSlot(int id, bool shockwave = false)
        {
            if (id <= 0)
                return null;

            var items = Inventory.GetConvertedEquips().Concat(Inventory.GetConvertedInventory()).Where(x => x.id == id);
            var isMacGuffin = InventoryManager.macguffinList.Keys.Contains(id);

            if (shockwave)
            {
                // MacGuffins don't hardcap at level 100
                if (!isMacGuffin)
                    items = items.Where(x => x.level < 100);

                // We want to upgrade highest level items
                if (items.Any())
                    return items.AllMaxBy(x => x.level).First();
            }
            else if (items.Any())
            {
                // We want to put lowest level MacGuffin into daycare
                if (isMacGuffin)
                    return items.AllMinBy(x => x.level).First();

                return items.MaxItem();
            }

            if (shockwave && Settings.MoneyPitDaycare)
            {
                var index = Daycare.FindIndex(x => x.id == id);
                if (index >= 0)
                {
                    var completion = Main.InventoryController.daycares[index].daycareSlider.value;
                    if (isMacGuffin || completion <= Settings.DaycareThreshold / 100f)
                    {
                        var helper = Daycare.First(x => x.id == id).GetInventoryHelper(index + 100000);
                        return helper;
                    }
                }
            }

            return null;
        }

        public static void SaveDaycare() => _savedDaycare = Daycare.Select(x => x.id).ToArray();

        public static void RestoreDaycare()
        {
            for (int i = 0; i < _savedDaycare?.Length; i++)
            {
                var item = _savedDaycare[i];

                if (Daycare[i].id == item)
                    continue;

                if (item == 0)
                {
                    InventoryManager.MoveFromDaycareToInventory(Inventory, i + 100000);
                }
                else
                {
                    if (Daycare.Find(x => x.id == item) != null)
                        continue;

                    var equip = FindItemSlot(item, true);
                    if (equip == null)
                    {
                        InventoryManager.MoveFromDaycareToInventory(Inventory, i + 100000);
                        continue;
                    }

                    if (equip.level == 100 && equip.equipment.type != part.MacGuffin)
                    {
                        InventoryManager.MoveFromDaycareToInventory(Inventory, i + 100000);
                        continue;
                    }

                    if (equip.slot < 0 || _ic.accessoryID(equip.slot) >= 0)
                    {
                        var emptySlot = Inventory.inventory.FindIndex(x => x.id == 0);
                        if (emptySlot < 0)
                            continue;

                        _character.removeMostEnergy();
                        _character.removeMostMagic();
                        _character.removeAllRes3();

                        Inventory.item1 = equip.slot;
                        Inventory.item2 = emptySlot;
                        switch (equip.equipment.type)
                        {
                            case part.Head:
                                _ic.swapHead();
                                break;
                            case part.Chest:
                                _ic.swapChest();
                                break;
                            case part.Legs:
                                _ic.swapLegs();
                                break;
                            case part.Boots:
                                _ic.swapBoots();
                                break;
                            case part.Weapon:
                                _ic.swapWeapon();
                                break;
                            case part.Accessory:
                                _ic.swapAcc();
                                break;
                            default:
                                continue;
                        }
                    }
                    else
                    {
                        Inventory.item2 = equip.slot;
                    }
                    Inventory.item1 = i + 100000;
                    _ic.swapDaycare();
                }
            }

            _ic.updateBonuses();
            _ic.updateInventory();

            UpdateResources();
        }

        private static void UpdateResources()
        {
            UpdateEnergy();
            UpdateMagic();
            UpdateRes3();
        }

        private static void UpdateEnergy()
        {
            if (_character.curEnergy >= _character.totalCapEnergy())
            {
                long num = _character.totalCapEnergy() - _character.curEnergy;
                _character.curEnergy += num;
                _character.idleEnergy += num;
            }
        }

        private static void UpdateMagic()
        {
            if (_character.magic.curMagic >= _character.totalCapMagic())
            {
                long num = _character.totalCapMagic() - _character.magic.curMagic;
                _character.magic.curMagic += num;
                _character.magic.idleMagic += num;
            }
        }

        private static void UpdateRes3()
        {
            if (_character.res3.curRes3 >= _character.totalCapRes3())
            {
                long num = _character.totalCapRes3() - _character.res3.curRes3;
                _character.res3.curRes3 += num;
                _character.res3.idleRes3 += num;
            }
        }

        // Public wrapper for the Loadouts tab's "Use Current Gear" (fills manual IDs from equipped).
        public static int[] CurrentGearIds()
        {
            try { return GetCurrentGear().Where(x => x > 0).ToArray(); }
            catch { return new int[0]; }
        }

        private static List<int> GetCurrentGear()
        {
            var loadout = new List<int>
            {
                Inventory.head.id,
                Inventory.boots.id,
                Inventory.chest.id,
                Inventory.legs.id,
                Inventory.weapon.id
            };


            if (_ic.weapon2Unlocked())
                loadout.Add(Inventory.weapon2.id);

            for (var id = 10000; _ic.accessoryID(id) < Inventory.accs.Count; ++id)
            {
                var index = Main.InventoryController.accessoryID(id);
                loadout.Add(Inventory.accs[index].id);
            }

            return loadout;
        }

        public static void SaveCurrentLoadout()
        {
            var loadout = GetCurrentGear();
            _savedLoadout = loadout.ToArray();
            if (_savedLoadout?.Length > 0)
                Log($"Saved Current Loadout {string.Join(", ", _savedLoadout)}");
        }

        public static void SaveTempLoadout()
        {
            var loadout = GetCurrentGear();
            _tempLoadout = loadout.ToArray();
            if (_tempLoadout?.Length > 0)
                Log($"Saved Temp Loadout {string.Join(", ", _tempLoadout)}");
        }

        // Cause.Restore: the other half of the Quick Loadout hotkey — it puts back what the user had
        // before the temp swap. Gating it would strand that loadout for the rest of the challenge.
        public static void RestoreTempLoadout() => ChangeGear(_tempLoadout, GearChangeGate.Cause.Restore);
    }
}
