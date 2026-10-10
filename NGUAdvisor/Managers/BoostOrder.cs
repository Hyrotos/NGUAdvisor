using System.Collections.Generic;

namespace NGUAdvisor.Managers
{
    // THE ORDER BOOSTS ARE HANDED OUT IN. Unity-free: the three groups come in already resolved, so
    // the one decision here - which group leads - is tested without the game.
    //
    //   advisor list   worn gear, then the priority list, then locked inventory items
    //   manual list    the priority list, then worn gear, then locked inventory items
    //
    // The advisor's list is built from UNEQUIPPED items only (InventoryAdvisor.AutoBoostPriority), so
    // leading with it fed every boost to gear in the bag while the set actually being worn waited.
    // A manual list is the operator's own ordering and keeps the lead: putting a bag item first there
    // is a deliberate choice.
    public static class BoostOrder
    {
        public static List<T> Arrange<T>(IEnumerable<T> priority, IEnumerable<T> equipped, IEnumerable<T> locked,
            bool advisorList)
        {
            var result = new List<T>();
            if (advisorList)
            {
                result.AddRange(equipped);
                result.AddRange(priority);
            }
            else
            {
                result.AddRange(priority);
                result.AddRange(equipped);
            }
            result.AddRange(locked);
            return result;
        }
    }
}
