using System.Linq;

namespace NGUAdvisor.Managers
{
    // What the profile editor's pickers offer, in the advisor's own words.
    //
    // A priority token is written "NGU-3" or "AUG-10", and the editor used to ask for exactly that:
    // one text field, the grammar in a hint line. Knowing that 3 is "Gold" and 10 is the Advanced
    // Exoskeleton's upgrade was the user's job. PriorityCatalog already says which token types each
    // resource has and how far their index runs; this adds what each index IS, so a picker can say it.
    //
    // Unity-free, and deliberately NOT part of PriorityCatalog: that file is linked into the companion
    // host, which has no reason to carry the NGU name tables.
    public static class EditorCatalog
    {
        private static readonly string[] AdvancedTraining =
            { "Toughness", "Power", "Block", "Wandoos Energy", "Wandoos Magic" };

        private static readonly string[] Augments =
        {
            "Safety Scissors", "Milk Infusion", "Cannon Implant", "Shoulder Mounted Minigun",
            "Energy Buster", "Advanced Exoskeleton", "Laser Sword"
        };

        // The eight blood rituals in the game's order. The game's own table numbers them from 1;
        // the token index is that number minus one, so RIT-0 is the Tack. (Names as the game's
        // ritual list shows them; the last is unlocked by Troll Challenge 6.)
        private static readonly string[] Rituals =
        {
            "Poke Yourself with a Tack", "Fifty Papercuts", "A Big-Ass Hickey", "Eat a bowl of Barbed Wire",
            "Grand Theft Blood Bank", "Self Decapitation", "Hug a Woodchipper", "Turn Yourself Inside Out"
        };

        // The names of a token type's indexes, in index order, or null when the index is a plain
        // number with no name behind it (BR's time limit in seconds).
        //
        // AUG runs over a FLAT 0-13: even is an augment, odd is that augment's upgrade. That pairing
        // is the single most misread thing in the grammar, so the names spell it out.
        // BT runs 0-5 attack, 6-11 defense (BasicTrainingBP).
        public static string[] IndexNames(ResourceKind kind, string code)
        {
            switch ((code ?? "").ToUpperInvariant())
            {
                case "NGU":
                    return kind == ResourceKind.Energy ? NguValueMath.ENames
                         : kind == ResourceKind.Magic ? NguValueMath.MNames : null;
                case "AT":
                    return AdvancedTraining;
                case "AUG":
                    return Enumerable.Range(0, Augments.Length * 2)
                                     .Select(i => Augments[i / 2] + (i % 2 == 1 ? " — upgrade" : "")).ToArray();
                case "BT":
                    return Enumerable.Range(0, 12)
                                     .Select(i => (i < 6 ? "Attack skill " : "Defense skill ") + (i % 6 + 1)).ToArray();
                case "RIT":
                    return kind == ResourceKind.Magic ? Rituals : null;
                case "HACK":
                case "MILEHACK":
                    return SystemCatalog.Hacks.Select(h => h.Value).ToArray();
                default:
                    return null;
            }
        }
    }
}
