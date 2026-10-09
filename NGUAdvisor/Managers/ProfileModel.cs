using System;
using System.Collections.Generic;
using System.Linq;
using SimpleJSON;

namespace NGUAdvisor.Managers
{
    // Editable in-memory model of an allocation profile's "Breakpoints".
    //
    // Parses with SimpleJSON (same lib the advisor uses) and serializes back to clean, indented JSON.
    // Zero UI / game dependencies so the load->edit->save round-trip is unit-testable in isolation.
    //
    // SAFETY MODEL: only the systems the editor actually edits are modeled as typed data (currently the
    // three resource priority timelines: Energy, Magic, R3). EVERY other system (Gear, Diggers, Beards,
    // Wandoos, NGUDiff, Consumables, Rebirth, Challenges, and anything unknown) is passed through VERBATIM
    // so it can never be lost or corrupted by a round-trip. Later phases model more systems one at a time,
    // each re-verified by the round-trip test. System ordering (both the top-level system order captured in
    // _systemOrder and key order within nested objects) is preserved by re-emitting in the order SimpleJSON
    // enumerated on load. NOTE: SimpleJSON's JSONObject is backed by a plain Dictionary<string,JSONNode>
    // (SimpleJson.cs), whose enumeration order is not a guaranteed contract. It equals insertion (file) order
    // here ONLY because the load->edit->save path never removes or clears keys, so on Mono/.NET Framework the
    // never-shrunk Dictionary happens to enumerate in insertion order. If SimpleJSON is ever swapped for a
    // hash-randomizing map, back JSONObject with an insertion-ordered structure to keep this round-trip stable.
    //
    // "GUI owns the file": within a MODELED breakpoint, human-comment fields are dropped on save. The drop
    // decision is made purely by KEY NAME via IsCommentKey (see CommentExact denylist + prefix rules below),
    // NOT by value type. A key matches the comment denylist (Comment*, Note*, Thresholds, Priorities1..9 doc
    // lines, etc.) is dropped; EVERY other extra key is preserved verbatim into Extras regardless of its value
    // type - including named alternate priority/gear sets (arrays like "AdvDC"/"PrioritiesDefault") AND
    // string-valued backup loadouts (e.g. "Default (MeepleMolotovEMPC)": "[ 326, ... ]"), which are user data.
    public class ProfileModel
    {
        public class PriorityBreakpoint
        {
            public int TimeSeconds;
            public List<string> Priorities = new List<string>();
            // Challenge tag: when set, the runtime prefers this breakpoint while that challenge is
            // active (BaseBreakpoints challenge-aware selection). "" = normal timeline breakpoint.
            public string Challenge = "";
            // Preserved functional (non-string) extra keys, e.g. named alternate priority sets.
            public readonly List<KeyValuePair<string, JSONNode>> Extras = new List<KeyValuePair<string, JSONNode>>();

            public int Hours => TimeSeconds / 3600;
            public int Minutes => (TimeSeconds % 3600) / 60;
            public int Seconds => TimeSeconds % 60;
        }

        // A breakpoint that carries an ordered list of integer indices (Diggers, Beards).
        public class ListBreakpoint
        {
            public int TimeSeconds;
            public List<int> Items = new List<int>();
            // Gear only: when set, the advisor optimizes gear for this objective.
            //
            // GEAR LOCK. Objective and Items used to be mutually exclusive — the two setters below each
            // cleared the other, and the runtime ignored Items whenever Objective was set. They are not
            // exclusive any more, and the combined meaning is a strict SUPERSET of both old ones:
            //     Items only      -> wear exactly these        (unchanged)
            //     Objective only  -> optimize every slot       (unchanged)
            //     BOTH            -> lock these items in, optimize every REMAINING slot   (new)
            // Which is why this needed no new JSON key: "ID" has always meant "these items are in the
            // set", and an objective now says what to do with the slots it does not name.
            public string Objective = "";
            // Gear only: when optimizing, always pin the single best Respawn item into the loadout.
            public bool ForceRespawn = false;
            // Diggers only: how many diggers should be ACTIVE at this breakpoint. 0 = unset, meaning
            // "as many as the game's unlocked slots allow", which is the behaviour every profile had
            // before this key existed. Distinct from Items.Count on purpose: the list is a PRIORITY
            // ORDER, so you can name your top two and still ask for four active, letting the advisor
            // choose the rest.
            public int Count = 0;
            public string Challenge = "";
            public readonly List<KeyValuePair<string, JSONNode>> Extras = new List<KeyValuePair<string, JSONNode>>();

            public int Hours => TimeSeconds / 3600;
            public int Minutes => (TimeSeconds % 3600) / 60;
            public int Seconds => TimeSeconds % 60;
        }

        // A breakpoint carrying an ordered list of string tokens (Consumables "Items": ["EPOT-B","MPOT-B:5"]).
        public class StringListBreakpoint
        {
            public int TimeSeconds;
            public List<string> Items = new List<string>();
            public string Challenge = "";
            public readonly List<KeyValuePair<string, JSONNode>> Extras = new List<KeyValuePair<string, JSONNode>>();
            public int Hours => TimeSeconds / 3600;
            public int Minutes => (TimeSeconds % 3600) / 60;
            public int Seconds => TimeSeconds % 60;
        }

        // One entry of the Rebirth array: a Type + optional trigger time + optional numeric Target. Any other
        // keys are preserved.
        public class RebirthEntry
        {
            public string Type = "";
            public int TimeSeconds;
            public double? Target;
            public readonly List<KeyValuePair<string, JSONNode>> Extras = new List<KeyValuePair<string, JSONNode>>();
            public int Hours => TimeSeconds / 3600;
            public int Minutes => (TimeSeconds % 3600) / 60;
            public int Seconds => TimeSeconds % 60;
        }

        // A breakpoint carrying a single integer value (Wandoos OS, NGU difficulty).
        public class ValueBreakpoint
        {
            public int TimeSeconds;
            public int Value;
            public string Challenge = "";
            public readonly List<KeyValuePair<string, JSONNode>> Extras = new List<KeyValuePair<string, JSONNode>>();

            public int Hours => TimeSeconds / 3600;
            public int Minutes => (TimeSeconds % 3600) / 60;
            public int Seconds => TimeSeconds % 60;
        }

        // Modeled systems.
        public List<PriorityBreakpoint> Energy = new List<PriorityBreakpoint>();
        public List<PriorityBreakpoint> Magic = new List<PriorityBreakpoint>();
        public List<PriorityBreakpoint> R3 = new List<PriorityBreakpoint>();
        public List<ListBreakpoint> Diggers = new List<ListBreakpoint>();
        public List<ListBreakpoint> Beards = new List<ListBreakpoint>();
        public List<ListBreakpoint> Gear = new List<ListBreakpoint>();   // payload key "ID"
        public List<ValueBreakpoint> Wandoos = new List<ValueBreakpoint>();   // payload key "OS"
        public List<ValueBreakpoint> NGUDiff = new List<ValueBreakpoint>();   // payload key "Diff"
        public List<StringListBreakpoint> Consumables = new List<StringListBreakpoint>();  // payload key "Items"
        public List<RebirthEntry> Rebirth = new List<RebirthEntry>();
        public List<string> Challenges = new List<string>();   // flat top-level array (not time-based)

        private static readonly HashSet<string> ModeledSystems =
            new HashSet<string>(StringComparer.Ordinal) { "Energy", "Magic", "R3", "Diggers", "Beards", "Gear", "Wandoos", "NGUDiff", "Consumables", "Rebirth", "Challenges" };

        // Original "Breakpoints" object and its key order, for verbatim passthrough of unmodeled systems.
        // Captures SimpleJSON's (Dictionary-backed) key enumeration order at load; == insertion order because the round-trip never removes keys.
        private readonly List<string> _systemOrder = new List<string>();
        private readonly Dictionary<string, JSONNode> _passthrough = new Dictionary<string, JSONNode>(StringComparer.Ordinal);

        // ----- companion Timeline-Editor mutations -----
        // Kept on the SHARED model so the round-trip stays lossless (passthrough / alternate sets
        // preserved) and the op is unit-tested; ProfileService wraps it with load -> validate ->
        // write-in-place and the injector's AllocationWatcher reloads.

        /// <summary>Remove one breakpoint from a system's timeline. False on unknown system / out-of-range index.</summary>
        public bool RemoveBreakpoint(string systemKey, int index)
        {
            switch (systemKey)
            {
                case "energy": return RemoveAt(Energy, index);
                case "magic": return RemoveAt(Magic, index);
                case "r3": return RemoveAt(R3, index);
                case "gear": return RemoveAt(Gear, index);
                case "diggers": return RemoveAt(Diggers, index);
                case "beards": return RemoveAt(Beards, index);
                case "wandoos": return RemoveAt(Wandoos, index);
                case "ngudiff": return RemoveAt(NGUDiff, index);
                case "consumables": return RemoveAt(Consumables, index);
                case "rebirth": return RemoveAt(Rebirth, index);
                default: return false;
            }
        }

        private static bool RemoveAt<T>(List<T> list, int index)
        {
            if (list == null || index < 0 || index >= list.Count) return false;
            list.RemoveAt(index);
            return true;
        }

        /// <summary>
        /// Insert a new blank breakpoint at <paramref name="sec"/> and return its index (ascending-time
        /// position; ties append after existing equal-time entries). -1 for an unknown system. The runtime
        /// re-sorts by time on load (BaseBreakpoints), so position is purely for a chronological UI; the blank
        /// defaults are structurally valid (empty priorities/items, Value 0, Rebirth Type "Time").
        /// </summary>
        public int AddBreakpoint(string systemKey, int sec)
        {
            if (sec < 0) sec = 0;
            switch (systemKey)
            {
                case "energy": return InsertSorted(Energy, new PriorityBreakpoint { TimeSeconds = sec }, b => b.TimeSeconds);
                case "magic": return InsertSorted(Magic, new PriorityBreakpoint { TimeSeconds = sec }, b => b.TimeSeconds);
                case "r3": return InsertSorted(R3, new PriorityBreakpoint { TimeSeconds = sec }, b => b.TimeSeconds);
                case "gear": return InsertSorted(Gear, new ListBreakpoint { TimeSeconds = sec }, b => b.TimeSeconds);
                case "diggers": return InsertSorted(Diggers, new ListBreakpoint { TimeSeconds = sec }, b => b.TimeSeconds);
                case "beards": return InsertSorted(Beards, new ListBreakpoint { TimeSeconds = sec }, b => b.TimeSeconds);
                case "wandoos": return InsertSorted(Wandoos, new ValueBreakpoint { TimeSeconds = sec }, b => b.TimeSeconds);
                case "ngudiff": return InsertSorted(NGUDiff, new ValueBreakpoint { TimeSeconds = sec }, b => b.TimeSeconds);
                case "consumables": return InsertSorted(Consumables, new StringListBreakpoint { TimeSeconds = sec }, b => b.TimeSeconds);
                case "rebirth": return InsertSorted(Rebirth, new RebirthEntry { TimeSeconds = sec, Type = "Time" }, b => b.TimeSeconds);
                default: return -1;
            }
        }

        private static int InsertSorted<T>(List<T> list, T item, Func<T, int> time)
        {
            int i = 0;
            while (i < list.Count && time(list[i]) <= time(item)) i++;
            list.Insert(i, item);
            return i;
        }

        /// <summary>Set one breakpoint's time (seconds). False on unknown system / out-of-range index.</summary>
        public bool SetTimeSeconds(string systemKey, int index, int sec)
        {
            if (sec < 0) sec = 0;
            switch (systemKey)
            {
                case "energy": return At(Energy, index, b => b.TimeSeconds = sec);
                case "magic": return At(Magic, index, b => b.TimeSeconds = sec);
                case "r3": return At(R3, index, b => b.TimeSeconds = sec);
                case "gear": return At(Gear, index, b => b.TimeSeconds = sec);
                case "diggers": return At(Diggers, index, b => b.TimeSeconds = sec);
                case "beards": return At(Beards, index, b => b.TimeSeconds = sec);
                case "wandoos": return At(Wandoos, index, b => b.TimeSeconds = sec);
                case "ngudiff": return At(NGUDiff, index, b => b.TimeSeconds = sec);
                case "consumables": return At(Consumables, index, b => b.TimeSeconds = sec);
                case "rebirth": return At(Rebirth, index, b => b.TimeSeconds = sec);
                default: return false;
            }
        }

        /// <summary>Set a priority timeline breakpoint's ordered tokens (energy/magic/r3 only).</summary>
        public bool SetPriorities(string systemKey, int index, List<string> tokens)
        {
            var l = systemKey == "energy" ? Energy : systemKey == "magic" ? Magic : systemKey == "r3" ? R3 : null;
            return At(l, index, b => b.Priorities = tokens ?? new List<string>());
        }

        /// <summary>Set an int-list breakpoint's items (gear/diggers/beards). Gear switches to manual-ID mode.</summary>
        public bool SetItems(string systemKey, int index, List<int> ids)
        {
            var l = systemKey == "gear" ? Gear : systemKey == "diggers" ? Diggers : systemKey == "beards" ? Beards : null;
            return At(l, index, b => { b.Items = ids ?? new List<int>(); if (systemKey == "gear") { b.Objective = ""; b.ForceRespawn = false; DropLegacyChain(b); } });
        }

        /// <summary>How many diggers should be ACTIVE at this breakpoint. 0 clears it (use all unlocked
        /// slots). Separate from the item list, which is a priority ORDER and may be shorter.</summary>
        public bool SetListCount(string systemKey, int index, int count)
        {
            var l = systemKey == "diggers" ? Diggers : systemKey == "beards" ? Beards : null;
            return At(l, index, b => b.Count = count < 0 ? 0 : count);
        }

        /// <summary>Put a gear breakpoint into optimize-objective mode (clears the manual ID list).</summary>
        public bool SetGearObjective(int index, string objective, bool forceRespawn) =>
            At(Gear, index, b => { b.Objective = objective ?? ""; b.ForceRespawn = forceRespawn; b.Items = new List<int>(); DropLegacyChain(b); });

        // The older structured chain keys ride through a round trip as Extras, and at runtime
        // "Priorities" SUPERSEDES Objective. So a setter that states what the row now optimizes for
        // has to remove them, or the row would keep running the chain it was just edited away from.
        private static void DropLegacyChain(ListBreakpoint b) =>
            b.Extras.RemoveAll(kv => kv.Key == "Priorities" || kv.Key == "TopPowerWeapon");

        /// <summary>GEAR LOCK: pin <paramref name="lockedIds"/> AND optimize every remaining slot for
        /// <paramref name="objective"/>. The one setter that writes both halves of a gear breakpoint;
        /// the two above stay as they are because each states one half AND clears the other, which is
        /// still exactly what "just an ID list" and "just an objective" mean.</summary>
        public bool SetGearLock(int index, List<int> lockedIds, string objective, bool forceRespawn) =>
            At(Gear, index, b =>
            {
                b.Items = lockedIds ?? new List<int>();
                b.Objective = objective ?? "";
                b.ForceRespawn = forceRespawn;
                DropLegacyChain(b);
            });

        /// <summary>Set a single-value breakpoint's value (wandoos/ngudiff).</summary>
        public bool SetValue(string systemKey, int index, int value)
        {
            var l = systemKey == "wandoos" ? Wandoos : systemKey == "ngudiff" ? NGUDiff : null;
            return At(l, index, b => b.Value = value);
        }

        /// <summary>Set the consumables breakpoint's string items ("CODE" / "CODE:amount").</summary>
        public bool SetStringItems(int index, List<string> items) =>
            At(Consumables, index, b => b.Items = items ?? new List<string>());

        /// <summary>Set a rebirth entry's Type + optional numeric Target.</summary>
        public bool SetRebirth(int index, string type, double? target) =>
            At(Rebirth, index, b => { b.Type = type ?? ""; b.Target = target; });

        /// <summary>Set a breakpoint's challenge tag ("" = untagged). Rebirth entries carry no challenge.</summary>
        public bool SetChallenge(string systemKey, int index, string challenge)
        {
            var c = challenge ?? "";
            switch (systemKey)
            {
                case "energy": return At(Energy, index, b => b.Challenge = c);
                case "magic": return At(Magic, index, b => b.Challenge = c);
                case "r3": return At(R3, index, b => b.Challenge = c);
                case "gear": return At(Gear, index, b => b.Challenge = c);
                case "diggers": return At(Diggers, index, b => b.Challenge = c);
                case "beards": return At(Beards, index, b => b.Challenge = c);
                case "wandoos": return At(Wandoos, index, b => b.Challenge = c);
                case "ngudiff": return At(NGUDiff, index, b => b.Challenge = c);
                case "consumables": return At(Consumables, index, b => b.Challenge = c);
                default: return false;
            }
        }

        private static bool At<T>(List<T> list, int index, Action<T> apply)
        {
            if (list == null || index < 0 || index >= list.Count) return false;
            apply(list[index]);
            return true;
        }

        // ----- Load -----

        public static ProfileModel Load(string json)
        {
            var root = JSON.Parse(json);
            if (root == null)
                throw new Exception("Profile could not be parsed as JSON.");
            var bps = root["Breakpoints"];
            if (bps == null || !bps.IsObject)
                throw new Exception("Profile has no \"Breakpoints\" object.");

            var m = new ProfileModel();
            foreach (var kv in bps.AsObject)
            {
                m._systemOrder.Add(kv.Key);
                if (kv.Key == "Energy") m.Energy = LoadPriorities(kv.Value);
                else if (kv.Key == "Magic") m.Magic = LoadPriorities(kv.Value);
                else if (kv.Key == "R3") m.R3 = LoadPriorities(kv.Value);
                else if (kv.Key == "Diggers") m.Diggers = LoadList(kv.Value, "List");
                else if (kv.Key == "Beards") m.Beards = LoadList(kv.Value, "List");
                else if (kv.Key == "Gear") m.Gear = LoadList(kv.Value, "ID");
                else if (kv.Key == "Wandoos") m.Wandoos = LoadValue(kv.Value, "OS");
                else if (kv.Key == "NGUDiff") m.NGUDiff = LoadValue(kv.Value, "Diff");
                else if (kv.Key == "Consumables") m.Consumables = LoadStringList(kv.Value, "Items");
                else if (kv.Key == "Rebirth") m.Rebirth = LoadRebirth(kv.Value);
                else if (kv.Key == "Challenges") { foreach (var c in ArrayChildren(kv.Value)) m.Challenges.Add(c.Value); }
                else m._passthrough[kv.Key] = kv.Value; // verbatim
            }
            return m;
        }

        private static IEnumerable<JSONNode> ArrayChildren(JSONNode node) =>
            node != null && node.IsArray ? node.Children : Enumerable.Empty<JSONNode>();

        // Keys that are pure human documentation and are dropped on save. Everything NOT matched here is
        // preserved verbatim - including named alternate priority/gear sets (arrays) AND string-valued
        // backup loadouts like "Default (MeepleMolotovEMPC)": "[ 326, ... ]" which are user data, not comments.
        private static readonly HashSet<string> CommentExact =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Comment", "Note", "Thresholds", "Timing", "DiggerOptions", "BeardOptions", "GO Notes", "GO Note" };

        private static bool IsCommentKey(string key)
        {
            if (CommentExact.Contains(key)) return true;
            if (key.StartsWith("Comment", StringComparison.OrdinalIgnoreCase)) return true;  // Comment2, CommentB
            if (key.StartsWith("Note", StringComparison.OrdinalIgnoreCase)) return true;      // Note1, Note2
            if (key.StartsWith("GO Note", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("PriorityComment", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("PriorityExample", StringComparison.OrdinalIgnoreCase)) return true;
            if (key.StartsWith("PriorityPercent", StringComparison.OrdinalIgnoreCase)) return true;
            // "Priorities" + a digit (Priorities1..9 doc lines) - but NOT PrioritiesDefault / PrioritiesX (data)
            if (key.Length > 10 && key.StartsWith("Priorities", StringComparison.OrdinalIgnoreCase) && char.IsDigit(key[10]))
                return true;
            return false;
        }

        private static List<PriorityBreakpoint> LoadPriorities(JSONNode node)
        {
            var list = new List<PriorityBreakpoint>();
            foreach (var bp in ArrayChildren(node))
            {
                var b = new PriorityBreakpoint { TimeSeconds = ParseTime(bp["Time"]) };
                foreach (var p in ArrayChildren(bp["Priorities"]))
                    b.Priorities.Add(p.Value);

                if (bp.IsObject)
                {
                    foreach (var kv in bp.AsObject)
                    {
                        if (kv.Key == "Time" || kv.Key == "Priorities") continue;
                        if (kv.Key == "Challenge") { b.Challenge = kv.Value.Value ?? ""; continue; }
                        if (IsCommentKey(kv.Key)) continue;
                        b.Extras.Add(new KeyValuePair<string, JSONNode>(kv.Key, kv.Value));
                    }
                }
                list.Add(b);
            }
            return list;
        }

        private static List<ListBreakpoint> LoadList(JSONNode node, string payloadKey)
        {
            var list = new List<ListBreakpoint>();
            foreach (var bp in ArrayChildren(node))
            {
                var b = new ListBreakpoint { TimeSeconds = ParseTime(bp["Time"]) };
                foreach (var it in ArrayChildren(bp[payloadKey]))
                    b.Items.Add(it.AsInt);

                if (bp.IsObject)
                    foreach (var kv in bp.AsObject)
                    {
                        if (kv.Key == "Time" || kv.Key == payloadKey) continue;
                        if (kv.Key == "Objective") { b.Objective = kv.Value.Value; continue; }
                        if (kv.Key == "TopRespawn") { b.ForceRespawn = kv.Value.AsBool; continue; }
                        if (kv.Key == "Count") { b.Count = kv.Value.AsInt; continue; }
                        if (kv.Key == "Challenge") { b.Challenge = kv.Value.Value ?? ""; continue; }
                        if (IsCommentKey(kv.Key)) continue;
                        b.Extras.Add(new KeyValuePair<string, JSONNode>(kv.Key, kv.Value));
                    }
                list.Add(b);
            }
            return list;
        }

        private static List<StringListBreakpoint> LoadStringList(JSONNode node, string payloadKey)
        {
            var list = new List<StringListBreakpoint>();
            foreach (var bp in ArrayChildren(node))
            {
                var b = new StringListBreakpoint { TimeSeconds = ParseTime(bp["Time"]) };
                foreach (var it in ArrayChildren(bp[payloadKey]))
                    b.Items.Add(it.Value);
                if (bp.IsObject)
                    foreach (var kv in bp.AsObject)
                    {
                        if (kv.Key == "Time" || kv.Key == payloadKey) continue;
                        if (kv.Key == "Challenge") { b.Challenge = kv.Value.Value ?? ""; continue; }
                        if (IsCommentKey(kv.Key)) continue;
                        b.Extras.Add(new KeyValuePair<string, JSONNode>(kv.Key, kv.Value));
                    }
                list.Add(b);
            }
            return list;
        }

        private static List<RebirthEntry> LoadRebirth(JSONNode node)
        {
            var list = new List<RebirthEntry>();
            foreach (var bp in ArrayChildren(node))
            {
                var b = new RebirthEntry
                {
                    Type = bp["Type"] != null ? bp["Type"].Value : "",
                    TimeSeconds = ParseTime(bp["Time"])
                };
                if (bp["Target"] != null && bp["Target"].IsNumber) b.Target = bp["Target"].AsDouble;
                if (bp.IsObject)
                    foreach (var kv in bp.AsObject)
                    {
                        if (kv.Key == "Type" || kv.Key == "Time" || kv.Key == "Target") continue;
                        if (IsCommentKey(kv.Key)) continue;
                        b.Extras.Add(new KeyValuePair<string, JSONNode>(kv.Key, kv.Value));
                    }
                list.Add(b);
            }
            return list;
        }

        private static List<ValueBreakpoint> LoadValue(JSONNode node, string payloadKey)
        {
            var list = new List<ValueBreakpoint>();
            foreach (var bp in ArrayChildren(node))
            {
                var b = new ValueBreakpoint { TimeSeconds = ParseTime(bp["Time"]) };
                if (bp[payloadKey] != null && bp[payloadKey].IsNumber) b.Value = bp[payloadKey].AsInt;
                if (bp.IsObject)
                    foreach (var kv in bp.AsObject)
                    {
                        if (kv.Key == "Time" || kv.Key == payloadKey) continue;
                        if (kv.Key == "Challenge") { b.Challenge = kv.Value.Value ?? ""; continue; }
                        if (IsCommentKey(kv.Key)) continue;
                        b.Extras.Add(new KeyValuePair<string, JSONNode>(kv.Key, kv.Value));
                    }
                list.Add(b);
            }
            return list;
        }

        // Mirrors BaseBreakpoints.ParseTime: number = seconds; object = sum of h/m/(other=seconds).
        private static int ParseTime(JSONNode timeNode)
        {
            if (timeNode == null) return 0;
            if (timeNode.IsNumber) return timeNode.AsInt;
            int t = 0;
            if (timeNode.IsObject)
            {
                foreach (var kv in timeNode.AsObject)
                {
                    if (!kv.Value.IsNumber) continue;
                    switch (kv.Key.ToLower())
                    {
                        case "h": t += 3600 * kv.Value.AsInt; break;
                        case "m": t += 60 * kv.Value.AsInt; break;
                        default: t += kv.Value.AsInt; break;
                    }
                }
            }
            return t;
        }

        // ----- Save -----

        public string ToJson()
        {
            var bps = new JSONObject();

            // Emit systems in their original order; regenerate modeled ones, pass others through verbatim.
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in _systemOrder)
            {
                if (emitted.Contains(key)) continue;
                emitted.Add(key);
                if (key == "Energy") bps["Energy"] = PrioritiesToJson(Energy);
                else if (key == "Magic") bps["Magic"] = PrioritiesToJson(Magic);
                else if (key == "R3") bps["R3"] = PrioritiesToJson(R3);
                else if (key == "Diggers") bps["Diggers"] = ListToJson(Diggers, "List");
                else if (key == "Beards") bps["Beards"] = ListToJson(Beards, "List");
                else if (key == "Gear") bps["Gear"] = ListToJson(Gear, "ID");
                else if (key == "Wandoos") bps["Wandoos"] = ValueToJson(Wandoos, "OS");
                else if (key == "NGUDiff") bps["NGUDiff"] = ValueToJson(NGUDiff, "Diff");
                else if (key == "Consumables") bps["Consumables"] = StringListToJson(Consumables, "Items");
                else if (key == "Rebirth") bps["Rebirth"] = RebirthToJson(Rebirth);
                else if (key == "Challenges") bps["Challenges"] = ChallengesToJson(Challenges);
                else if (_passthrough.TryGetValue(key, out var raw)) bps[key] = raw;
            }

            // Modeled systems that were not present originally but now have content (defensive).
            if (!emitted.Contains("Energy") && Energy.Count > 0) bps["Energy"] = PrioritiesToJson(Energy);
            if (!emitted.Contains("Magic") && Magic.Count > 0) bps["Magic"] = PrioritiesToJson(Magic);
            if (!emitted.Contains("R3") && R3.Count > 0) bps["R3"] = PrioritiesToJson(R3);
            if (!emitted.Contains("Diggers") && Diggers.Count > 0) bps["Diggers"] = ListToJson(Diggers, "List");
            if (!emitted.Contains("Beards") && Beards.Count > 0) bps["Beards"] = ListToJson(Beards, "List");
            if (!emitted.Contains("Gear") && Gear.Count > 0) bps["Gear"] = ListToJson(Gear, "ID");
            if (!emitted.Contains("Wandoos") && Wandoos.Count > 0) bps["Wandoos"] = ValueToJson(Wandoos, "OS");
            if (!emitted.Contains("NGUDiff") && NGUDiff.Count > 0) bps["NGUDiff"] = ValueToJson(NGUDiff, "Diff");
            if (!emitted.Contains("Consumables") && Consumables.Count > 0) bps["Consumables"] = StringListToJson(Consumables, "Items");
            if (!emitted.Contains("Rebirth") && Rebirth.Count > 0) bps["Rebirth"] = RebirthToJson(Rebirth);
            if (!emitted.Contains("Challenges") && Challenges.Count > 0) bps["Challenges"] = ChallengesToJson(Challenges);

            var root = new JSONObject();
            root["Breakpoints"] = bps;
            return root.ToString(2);
        }

        private static JSONNode TimeToJson(int seconds)
        {
            if (seconds <= 0)
                return new JSONNumber(0);
            var o = new JSONObject();
            int h = seconds / 3600, m = (seconds % 3600) / 60, s = seconds % 60;
            if (h != 0) o["h"] = h;
            if (m != 0) o["m"] = m;
            if (s != 0) o["s"] = s;
            return o;
        }

        private static JSONArray PrioritiesToJson(List<PriorityBreakpoint> list)
        {
            var arr = new JSONArray();
            foreach (var b in list)
            {
                var o = new JSONObject();
                o["Time"] = TimeToJson(b.TimeSeconds);
                var pr = new JSONArray();
                foreach (var p in b.Priorities) pr.Add(p);
                o["Priorities"] = pr;
                if (!string.IsNullOrEmpty(b.Challenge)) o["Challenge"] = b.Challenge;
                foreach (var kv in b.Extras) o[kv.Key] = kv.Value;
                arr.Add(o);
            }
            return arr;
        }

        private static JSONArray StringListToJson(List<StringListBreakpoint> list, string payloadKey)
        {
            var arr = new JSONArray();
            foreach (var b in list)
            {
                var o = new JSONObject();
                o["Time"] = TimeToJson(b.TimeSeconds);
                var items = new JSONArray();
                foreach (var s in b.Items) items.Add(s);
                o[payloadKey] = items;
                if (!string.IsNullOrEmpty(b.Challenge)) o["Challenge"] = b.Challenge;
                foreach (var kv in b.Extras) o[kv.Key] = kv.Value;
                arr.Add(o);
            }
            return arr;
        }

        private static JSONArray RebirthToJson(List<RebirthEntry> list)
        {
            var arr = new JSONArray();
            foreach (var b in list)
            {
                var o = new JSONObject();
                if (!string.IsNullOrEmpty(b.Type)) o["Type"] = b.Type;
                o["Time"] = TimeToJson(b.TimeSeconds);
                if (b.Target.HasValue) o["Target"] = b.Target.Value;
                foreach (var kv in b.Extras) o[kv.Key] = kv.Value;
                arr.Add(o);
            }
            return arr;
        }

        private static JSONArray ChallengesToJson(List<string> list)
        {
            var arr = new JSONArray();
            foreach (var c in list) arr.Add(c);
            return arr;
        }

        private static JSONArray ValueToJson(List<ValueBreakpoint> list, string payloadKey)
        {
            var arr = new JSONArray();
            foreach (var b in list)
            {
                var o = new JSONObject();
                o["Time"] = TimeToJson(b.TimeSeconds);
                o[payloadKey] = b.Value;
                if (!string.IsNullOrEmpty(b.Challenge)) o["Challenge"] = b.Challenge;
                foreach (var kv in b.Extras) o[kv.Key] = kv.Value;
                arr.Add(o);
            }
            return arr;
        }

        private static JSONArray ListToJson(List<ListBreakpoint> list, string payloadKey)
        {
            var arr = new JSONArray();
            foreach (var b in list)
            {
                var o = new JSONObject();
                o["Time"] = TimeToJson(b.TimeSeconds);
                var items = new JSONArray();
                foreach (var i in b.Items) items.Add(i);
                o[payloadKey] = items;
                if (!string.IsNullOrEmpty(b.Objective)) o["Objective"] = b.Objective;
                if (b.ForceRespawn) o["TopRespawn"] = b.ForceRespawn;
                // Only when set, so profiles that never use it stay byte-identical on a round trip.
                if (b.Count > 0) o["Count"] = b.Count;
                if (!string.IsNullOrEmpty(b.Challenge)) o["Challenge"] = b.Challenge;
                foreach (var kv in b.Extras) o[kv.Key] = kv.Value;
                arr.Add(o);
            }
            return arr;
        }
    }
}
