using System;
using System.Collections.Generic;
using System.Globalization;

namespace NGUAdvisor.Managers
{
    // Parses the companion Timeline-Editor's canonical payload text into a breakpoint's typed content, with
    // SEMANTIC validation the structural ProfileValidator can't do (ProfileValidator only checks JSON syntax):
    // priority tokens against PriorityCatalog, digger/beard/value indices against SystemCatalog ranges,
    // consumable codes and rebirth types against their catalogs. On success it mutates the shared ProfileModel
    // via its typed setters; ProfileService wraps this with load -> validate(JSON) -> write-in-place. Zero UI /
    // game deps so the parse+validate+apply round-trip is unit-testable headlessly.
    //
    // Canonical payload per system (what BuildTimelinesJson emits and this parses, so they round-trip):
    //   energy/magic/r3 : "NGU-3, WAN, CAPAUG-10:50"        (comma tokens, priority order)
    //   gear            : "326, 100"  OR  "Optimize: <obj>" / "Optimize+Respawn: <obj>"
    //                     OR "Lock: 326, 100; Optimize: <obj>"  (Gear Lock — pin, then optimize)
    //   diggers/beards  : "2, 4, 5"                          (comma slot indices)
    //   wandoos/ngudiff : "2"                                (single index)
    //   consumables     : "LC, MUFFIN:5"                     (comma CODE[:amount])
    //   rebirth         : payload = Type code; target passed separately
    public static class BreakpointEditor
    {
        public struct Result
        {
            public bool Ok;
            public string Error;
            public static Result Success => new Result { Ok = true };
            public static Result Fail(string e) => new Result { Ok = false, Error = e };
        }

        /// <summary>
        /// Apply a full breakpoint edit (time + payload + challenge/target) at systemKey[index]. Validates the
        /// payload semantically and returns a friendly error (no mutation persisted by the caller) on failure.
        /// </summary>
        public static Result Apply(ProfileModel m, string systemKey, int index, int sec,
                                   string payload, string challenge, string target)
        {
            if (m == null) return Result.Fail("No profile loaded.");
            if (sec < 0) sec = 0;
            payload = payload ?? "";
            switch (systemKey)
            {
                case "energy":
                case "magic":
                case "r3": return ApplyPriority(m, systemKey, index, sec, payload, challenge);
                case "gear": return ApplyGear(m, index, sec, payload, challenge);
                case "diggers": return ApplyIntList(m, systemKey, index, sec, payload, challenge, 0, 11, "digger slot");
                case "beards": return ApplyIntList(m, systemKey, index, sec, payload, challenge, 0, 6, "beard slot");
                case "wandoos": return ApplyValue(m, systemKey, index, sec, payload, challenge, 0, 2, "Wandoos OS");
                case "ngudiff": return ApplyValue(m, systemKey, index, sec, payload, challenge, 0, 2, "difficulty");
                case "consumables": return ApplyConsumables(m, index, sec, payload, challenge);
                case "rebirth": return ApplyRebirth(m, index, sec, payload, target);
                default: return Result.Fail("Unknown system '" + systemKey + "'.");
            }
        }

        private static Result NoBp(string systemKey, int index) =>
            Result.Fail("No " + systemKey + " breakpoint at index " + index + ".");

        private static ResourceKind KindOf(string systemKey) =>
            systemKey == "magic" ? ResourceKind.Magic : systemKey == "r3" ? ResourceKind.R3 : ResourceKind.Energy;

        private static Result ApplyPriority(ProfileModel m, string sk, int index, int sec, string payload, string challenge)
        {
            if (!m.SetTimeSeconds(sk, index, sec)) return NoBp(sk, index);
            var kind = KindOf(sk);
            var tokens = new List<string>();
            foreach (var raw in SplitCsv(payload))
            {
                var t = PriorityCatalog.Parse(raw);
                if (!t.Recognized)
                    return Result.Fail("'" + raw + "' is not a known priority token.");
                var bt = PriorityCatalog.Find(kind, t.Base);
                if (bt == null)
                    return Result.Fail("'" + t.Base + "' is not valid for " + kind + ".");
                if (t.Index.HasValue)
                {
                    if (!bt.HasIndex)
                        return Result.Fail("'" + t.Base + "' does not take a -number.");
                    if (t.Index.Value < 0 || t.Index.Value > bt.IndexMax)
                        return Result.Fail("'" + t.Base + "-" + t.Index.Value + "' is out of range (0-" + bt.IndexMax + ").");
                }
                tokens.Add(PriorityCatalog.Build(t.Cap, t.Base, t.Index, t.Percent));
            }
            m.SetPriorities(sk, index, tokens);
            return SetChallenge(m, sk, index, challenge);
        }

        // GEAR LOCK grammar. A gear payload is one or more ';'-separated clauses:
        //
        //     "326, 100"                                 the whole loadout, exactly as before
        //     "Optimize: Time Machine"                   every slot optimized, exactly as before
        //     "Optimize+Respawn: Time Machine"           …plus the top-respawn pin, exactly as before
        //     "Lock: 326, 100; Optimize: Time Machine"   lock those two, optimize every remaining slot
        //
        // ';' is the clause separator rather than '+' because '+' is already inside "Optimize+Respawn"
        // and a separator that can appear inside a clause is a parser waiting to be wrong. A bare ID
        // list is also accepted as a lock clause, because that is literally how the request was
        // phrased ("specify certain items via ID, and then do Optimize: X") and refusing the obvious
        // spelling teaches the grammar rather than the feature.
        private static Result ApplyGear(ProfileModel m, int index, int sec, string payload, string challenge)
        {
            if (!m.SetTimeSeconds("gear", index, sec)) return NoBp("gear", index);
            var p = (payload ?? "").Trim();

            string objective = null;
            bool respawn = false, sawObjective = false;
            var ids = new List<int>();

            foreach (var clause in p.Split(';'))
            {
                var c = clause.Trim();
                if (c.Length == 0) continue;

                if (c.StartsWith("Optimize", StringComparison.OrdinalIgnoreCase))
                {
                    if (sawObjective)
                        return Result.Fail("Only one \"Optimize:\" is allowed per gear breakpoint.");
                    sawObjective = true;
                    int colon = c.IndexOf(':');
                    string head = colon >= 0 ? c.Substring(0, colon) : c;
                    respawn = head.IndexOf("Respawn", StringComparison.OrdinalIgnoreCase) >= 0;
                    objective = colon >= 0 ? c.Substring(colon + 1).Trim() : "";
                    if (objective.Length == 0)
                        return Result.Fail("Gear objective is empty (use \"Optimize: <objective>\").");
                    // A spelled chain that cannot be read would save as a row that chooses no gear.
                    // Only chains are checked: a plain name the list does not know has always been
                    // accepted here, and is reported in the log when the profile loads.
                    if (objective.IndexOf('(') >= 0 && GearChain.Parse(objective) == null)
                        return Result.Fail("Can't read the chain \"" + objective + "\". Write each step as Objective(slots) or " +
                                           "Objective(all), joined by \">\" — at most " + GearChain.MaxPriorities +
                                           " steps, e.g. Adventure(3) > Respawn(1) > Adventure(all).");
                    continue;
                }

                // "Lock: 326, 100" or a bare "326, 100".
                var body = c;
                if (c.StartsWith("Lock", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = c.IndexOf(':');
                    if (colon < 0) return Result.Fail("Write locked items as \"Lock: 326, 100\".");
                    body = c.Substring(colon + 1).Trim();
                    if (body.Length == 0)
                        return Result.Fail("Gear Lock is empty (use \"Lock: <item IDs>\", or drop the clause).");
                }
                foreach (var raw in SplitCsv(body))
                {
                    int id;
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) || id < 0)
                        return Result.Fail("'" + raw + "' is not a valid item ID.");
                    if (ids.Contains(id))
                        return Result.Fail("Item ID " + id + " is listed twice — the game equips one copy of an item at a time.");
                    ids.Add(id);
                }
            }

            // Three shapes, and each writes the setter that MEANS it. The two old setters each clear
            // the half they do not state, which is still the right behaviour for "just a list" and
            // "just an objective" — Gear Lock is the third, and it is the only one that writes both.
            if (sawObjective && ids.Count > 0) m.SetGearLock(index, ids, objective, respawn);
            else if (sawObjective) m.SetGearObjective(index, objective, respawn);
            else m.SetItems("gear", index, ids);

            return SetChallenge(m, "gear", index, challenge);
        }

        private static Result ApplyIntList(ProfileModel m, string sk, int index, int sec, string payload,
                                           string challenge, int min, int max, string what)
        {
            if (!m.SetTimeSeconds(sk, index, sec)) return NoBp(sk, index);

            // Diggers may carry a trailing "xN" — how many diggers should be ACTIVE here, which is not
            // the same as how many are listed. The list is a priority ORDER, so "3, 8 x4" means "these
            // two first, four active in total, advisor picks the other two". Absent = as many as the
            // unlocked slots allow, i.e. exactly what every profile did before this key existed.
            var body = (payload ?? "").Trim();
            int count = 0;
            int xAt = body.LastIndexOf('x');
            if (xAt > 0 && sk == "diggers")
            {
                var tail = body.Substring(xAt + 1).Trim();
                if (int.TryParse(tail, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                {
                    if (n < 1 || n > 12)
                        return Result.Fail("Digger count '" + n + "' is out of range (1-12).");
                    count = n;
                    body = body.Substring(0, xAt).TrimEnd().TrimEnd(',').Trim();
                }
            }

            var ids = new List<int>();
            foreach (var raw in SplitCsv(body))
            {
                if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                    return Result.Fail("'" + raw + "' is not a number.");
                if (id < min || id > max)
                    return Result.Fail(what + " '" + id + "' is out of range (" + min + "-" + max + ").");
                ids.Add(id);
            }
            if (count > 0 && ids.Count > count)
                return Result.Fail("You listed " + ids.Count + " diggers but asked for only " + count + " active.");
            m.SetItems(sk, index, ids);
            if (sk == "diggers") m.SetListCount(sk, index, count);
            return SetChallenge(m, sk, index, challenge);
        }

        private static Result ApplyValue(ProfileModel m, string sk, int index, int sec, string payload,
                                         string challenge, int min, int max, string what)
        {
            if (!m.SetTimeSeconds(sk, index, sec)) return NoBp(sk, index);
            var p = (payload ?? "").Trim();
            if (p.Length == 0)
                return Result.Fail("Enter a " + what + " value (" + min + "-" + max + ").");
            if (!int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < min || v > max)
                return Result.Fail("'" + p + "' is not a valid " + what + " (" + min + "-" + max + ").");
            m.SetValue(sk, index, v);
            return SetChallenge(m, sk, index, challenge);
        }

        private static Result ApplyConsumables(ProfileModel m, int index, int sec, string payload, string challenge)
        {
            if (!m.SetTimeSeconds("consumables", index, sec)) return NoBp("consumables", index);
            var items = new List<string>();
            foreach (var raw in SplitCsv(payload))
            {
                string code = raw, amt = null;
                int c = raw.IndexOf(':');
                if (c >= 0) { code = raw.Substring(0, c).Trim(); amt = raw.Substring(c + 1).Trim(); }
                string canon = null;
                foreach (var kv in SystemCatalog.Consumables)
                    if (string.Equals(kv.Key, code, StringComparison.OrdinalIgnoreCase)) { canon = kv.Key; break; }
                if (canon == null)
                    return Result.Fail("'" + code + "' is not a known consumable code.");
                if (amt != null)
                {
                    if (!int.TryParse(amt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 1)
                        return Result.Fail("'" + raw + "' has an invalid amount (must be a whole number >= 1).");
                    items.Add(canon + ":" + n);
                }
                else items.Add(canon);
            }
            m.SetStringItems(index, items);
            return SetChallenge(m, "consumables", index, challenge);
        }

        private static Result ApplyRebirth(ProfileModel m, int index, int sec, string payload, string target)
        {
            if (!m.SetTimeSeconds("rebirth", index, sec)) return NoBp("rebirth", index);
            var type = (payload ?? "").Trim();
            if (type.Length == 0)
                return Result.Fail("Choose a rebirth type.");
            string canon = null;
            foreach (var kv in SystemCatalog.RebirthTypes)
                if (string.Equals(kv.Key, type, StringComparison.OrdinalIgnoreCase)) { canon = kv.Key; break; }
            if (canon == null)
                return Result.Fail("'" + type + "' is not a known rebirth type.");
            double? tgt = null;
            if (SystemCatalog.TypeTakesTarget(canon))
            {
                var ts = (target ?? "").Trim();
                if (ts.Length == 0)
                    return Result.Fail("A '" + canon + "' rebirth needs a target value.");
                if (!double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out var tv))
                    return Result.Fail("'" + ts + "' is not a number.");
                tgt = tv;
            }
            m.SetRebirth(index, canon, tgt);
            return Result.Success;
        }

        private static Result SetChallenge(ProfileModel m, string sk, int index, string challenge)
        {
            var c = (challenge ?? "").Trim();
            if (c.Length == 0) { m.SetChallenge(sk, index, ""); return Result.Success; }
            c = c.ToUpperInvariant();
            bool known = false;
            foreach (var info in SystemCatalog.Challenges)
                if (string.Equals(info.Code, c, StringComparison.OrdinalIgnoreCase)) { known = true; break; }
            if (!known)
                return Result.Fail("'" + c + "' is not a known challenge code.");
            m.SetChallenge(sk, index, c);
            return Result.Success;
        }

        // ----- Challenge rotation -----
        //
        // PROFILE FORMAT (authoritative — BaseRebirth.ParseChallenges + RCTarget.ChallengeValid):
        //     "Challenges": [ "CODE-<ordinal>", ... ]
        // <ordinal> is a 1-BASED COMPLETION ORDINAL, never a count. An entry is eligible only when
        //     ordinal <= maxCompletions && ordinal == currentCompletions() + 1
        // so "BASIC-3" fires exactly once — on the rebirth that would earn the 3rd Basic completion. A
        // five-run Basic rotation is ["BASIC-1","BASIC-2","BASIC-3","BASIC-4","BASIC-5"]; ["BASIC-5"] does
        // NOTHING until four Basic completions have been earned by hand. ORDER matters too: BaseRebirth
        // engages the FIRST eligible entry, so a code that appears earlier wins the next rebirth.
        //
        // "RUN THIS TO THE CAP" IS ALREADY EXPRESSIBLE — enumerate the ordinals. An ordinal that has already
        // been earned is INERT (ChallengeValid() is false for it and FirstOrDefault skips it), so
        // ["24HR-8","24HR-9","24HR-10"] and ["24HR-1".."24HR-10"] both mean "finish the 24Hs" and both work
        // from any starting completion count. That is why ExpandTarget writes the full 1..N range.
        //
        // ORDINAL 0 IS NOT A SENTINEL, AND MUST NOT BECOME ONE. "CODE-0" parses (int.TryParse succeeds) and
        // is permanently inert (0 == cur + 1 is false for every cur >= 0). Redefining it as "run to the cap"
        // was evaluated and REJECTED — see ChallengeRotationTests. It cannot mean "one more", because
        // eligibility is recomputed from live completions every rebirth and nothing records that an entry
        // already fired, so it is unavoidably greedy: it would starve every entry behind it in the
        // FirstOrDefault scan, hold AnyChallengesValid() true (which short-circuits the rebirth timer) for
        // the entire run, be unable to express "exactly N" or "two more", and have no representation in the
        // companion's target box or ordinal track. It buys no expressive power over enumerating ordinals.
        //
        // EDITOR FORMAT (this file + the companion's Challenges page) keeps the friendly "x N" row, and
        // translates:
        //   READ  (GroupChallenges): a row = a GROUP = a maximal run of ADJACENT entries with the same code
        //         whose ordinals ascend by exactly 1. Target = the run's HIGHEST ordinal, so 1..5 and a
        //         hand-trimmed 3..5 both read as "x 5" ("get to five total"). Grouping is POSITIONAL, so the
        //         rows re-expand to the original array verbatim — including interleaving and a code that
        //         appears twice in different places (CBlock4 ships "24HR-2,3,4 ... <9 other codes> ... 24HR-5";
        //         merging those into one row would promote 24HR-5 ahead of everything between it).
        //   WRITE (CanonChallenges): "CODE-N"    -> TARGET N, expands to CODE-1 .. CODE-N.
        //                            "CODE*N"    -> the same, written explicitly ('x' / '\u00D7' also accepted).
        //                            "CODE-A..B" -> those ordinals VERBATIM. This is the raw/advanced form
        //                                           (A==B for a lone ordinal), and it is what each row's
        //                                           `Entry` is, so a row the user never touched round-trips
        //                                           unchanged and a NON-CONTIGUOUS set such as
        //                                           ["BASIC-2","BASIC-4"] survives intact.
        // Targets and ordinals clamp to the challenge's cap (live maxCompletions via LiveCap when a caller
        // wires it, else SystemCatalog's table) and dedupe on CODE+ORDINAL — never on CODE alone, which is
        // what silently reduced a shipped five-challenge rotation to a single entry.

        /// <summary>
        /// THE eligibility rule, shared by the runtime (BaseRebirth.RCTarget.ChallengeValid) and by every
        /// headless test of a rotation. Pure — no game state — so a whole chain is simulatable without the
        /// game loaded, which is the only way the cross-profile ordinal chains get tested at all.
        /// <para>
        /// STATELESS: recomputed from live completions on every rebirth. An entry fires on exactly one
        /// rebirth — the one that would earn that completion — and is inert before and after it.
        /// </para>
        /// </summary>
        public static bool ChallengeEligible(int ordinal, int currentCompletions, int maxCompletions) =>
            ordinal <= maxCompletions && ordinal == currentCompletions + 1;

        public struct ChallengeItem
        {
            public string Code;
            public int Index;      // 1-based completion ORDINAL (what the runtime matches on)
        }

        /// <summary>One friendly editor row: a contiguous run of completion ordinals for a single code.</summary>
        public sealed class ChallengeGroup
        {
            public string Code;
            public string Label;
            public int From;                                   // lowest ordinal in the run
            public int Target;                                 // highest ordinal in the run == the "x N" the UI shows
            public int Cap;
            public List<int> Ordinals = new List<int>();       // exact ordinals, in file order

            public bool Single => From == Target;
            public bool Partial => From > 1;                   // run does not start at the first completion

            /// <summary>
            /// SHAPE of an old-editor "count" write: a lone ordinal above 1. ADVISORY ONLY — the same shape is
            /// authored deliberately in chained C-block profiles (CBlock1 ships a bare "24HR-3" after two earlier
            /// blocks did 24HR-1/2). Confirm against live completions before offering <see cref="Repair"/>:
            /// it can never fire when currentCompletions() + 1 &lt; From.
            /// </summary>
            public bool Suspect => Single && Target > 1;

            /// <summary>Canonical write-path token for this row — echo it back unchanged and nothing moves.</summary>
            public string Entry => Code + "-" + From + ".." + Target;

            /// <summary>The token that turns a Suspect row back into a full 1..Target rotation.</summary>
            public string Repair => Code + "-1.." + Target;
        }

        /// <summary>
        /// Optional live cap source — Character.allChallenges.&lt;x&gt;.maxCompletions keyed by challenge code,
        /// returning 0 when unknown. Null in the companion and in tests (neither has the game loaded), where
        /// SystemCatalog's table is the fallback. The two agree on all 11 codes today, but the game is
        /// authoritative, so an in-process caller may point this at it.
        /// </summary>
        public static Func<string, int> LiveCap;

        private static SystemCatalog.ChallengeInfo InfoOf(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            foreach (var info in SystemCatalog.Challenges)
                if (string.Equals(info.Code, code, StringComparison.OrdinalIgnoreCase)) return info;
            return null;
        }

        /// <summary>Human label for a challenge code (the code itself if unknown).</summary>
        public static string ChallengeLabel(string code)
        {
            var info = InfoOf(code);
            return info != null ? info.Label : code;
        }

        /// <summary>Max completions for a challenge code: live value when wired, else the catalog; 0 if unknown.</summary>
        public static int ChallengeCap(string code)
        {
            var info = InfoOf(code);
            if (info == null) return 0;
            var f = LiveCap;
            if (f != null)
            {
                try { int live = f(info.Code); if (live > 0) return live; }
                catch { }
            }
            return info.Cap;
        }

        // 0 and negatives clamp UP to 1: they are typos, and ordinal 0 in particular is an entry the runtime
        // can never fire, so leaving it verbatim would write a silently dead rotation entry back to disk.
        private static int ClampOrdinal(string code, int ordinal) =>
            Math.Max(1, Math.Min(ChallengeCap(code), ordinal));

        /// <summary>
        /// Parse ONE profile entry "CODE-&lt;ordinal&gt;" — the runtime's format. Validates the code against
        /// SystemCatalog and clamps the ordinal to [1, cap]. This is NOT the editor's target form; see
        /// <see cref="CanonChallenges"/> for that.
        /// </summary>
        public static bool TryParseChallenge(string raw, out ChallengeItem item)
        {
            item = new ChallengeItem();
            if (string.IsNullOrEmpty(raw)) return false;
            int dash = raw.IndexOf('-');
            if (dash <= 0 || dash >= raw.Length - 1) return false;
            var info = InfoOf(raw.Substring(0, dash).Trim());
            if (info == null) return false;
            if (!int.TryParse(raw.Substring(dash + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
                return false;
            item.Code = info.Code;
            item.Index = ClampOrdinal(info.Code, ordinal);
            return true;
        }

        /// <summary>
        /// Editor target "x N" -> the FULL ordinal range CODE-1 .. CODE-N (clamped to the cap). Deliberately not
        /// trimmed to currentCompletions()+1 .. N: profiles are portable and shared, the shipped presets use the
        /// full range, and an already-earned ordinal is inert (ChallengeValid() is false for it and
        /// FirstOrDefault simply skips it) — whereas a trimmed profile silently stops working on another save.
        /// That inertness is also what makes the full range the SAFE way to say "finish this challenge":
        /// it reaches the cap from any starting completion count, with no sentinel and no schema change.
        /// </summary>
        public static List<string> ExpandTarget(string code, int target)
        {
            var list = new List<string>();
            var info = InfoOf(code);
            if (info == null) return list;
            int n = ClampOrdinal(info.Code, target);
            for (int i = 1; i <= n; i++) list.Add(info.Code + "-" + i);
            return list;
        }

        // Explicit target separators, so a UI can send "BASIC*5" instead of relying on the bare form.
        // No challenge code contains 'x'/'X', so those are unambiguous too.
        // '\u00D7' is the multiplication sign the UI renders in "Basic x 5"; escaped, not literal, because these
        // sources carry no BOM and a char literal must not depend on the compiler guessing the encoding.
        private static readonly char[] TargetSeparators = { '*', '\u00D7', 'x', 'X' };

        /// <summary>
        /// Canonicalize the EDITOR's rotation into the profile's ordinal format. Accepts, per entry:
        /// "CODE-N" / "CODE*N" (target -> CODE-1..CODE-N) and "CODE-A..B" (those ordinals verbatim).
        /// Unknown codes and unparseable entries are dropped; ordinals clamp to the cap; duplicates are removed
        /// on CODE+ORDINAL (first wins) so distinct ordinals of the same code all survive; order is preserved.
        /// NOTE: this is the write path only. Do NOT hand it a raw profile array — a bare "CODE-N" there is an
        /// ordinal, not a target (use <see cref="GroupChallenges"/>, then feed the rows' Entry tokens back).
        /// </summary>
        public static List<string> CanonChallenges(IEnumerable<string> raw)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var outList = new List<string>();
            if (raw == null) return outList;
            foreach (var r in raw)
                foreach (var ordinal in ExpandEntry(r))
                    if (seen.Add(ordinal))
                        outList.Add(ordinal);
            return outList;
        }

        // One editor token -> zero or more canonical "CODE-<ordinal>" strings.
        private static IEnumerable<string> ExpandEntry(string raw)
        {
            var s = (raw ?? "").Trim();
            if (s.Length == 0) yield break;

            // "CODE-A..B" — explicit ordinal range (the raw/advanced form GroupChallenges emits).
            int range = s.IndexOf("..", StringComparison.Ordinal);
            if (range > 0)
            {
                int d = s.IndexOf('-');
                var info = d > 0 && d < range ? InfoOf(s.Substring(0, d).Trim()) : null;
                if (info != null &&
                    int.TryParse(s.Substring(d + 1, range - d - 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) &&
                    int.TryParse(s.Substring(range + 2).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                {
                    a = ClampOrdinal(info.Code, a);
                    b = ClampOrdinal(info.Code, b);
                    if (b < a) { var t = a; a = b; b = t; }
                    for (int i = a; i <= b; i++) yield return info.Code + "-" + i;
                }
                yield break;
            }

            // "CODE*N" / "CODE x N" — the target form, written explicitly.
            int sep = s.IndexOfAny(TargetSeparators);
            if (sep > 0)
            {
                var info = InfoOf(s.Substring(0, sep).Trim());
                if (info != null && int.TryParse(s.Substring(sep + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
                    foreach (var e in ExpandTarget(info.Code, t)) yield return e;
                yield break;
            }

            // "CODE-N" — the friendly target form the "x N" control sends.
            if (TryParseChallenge(s, out var item))
                foreach (var e in ExpandTarget(item.Code, item.Index)) yield return e;
        }

        /// <summary>
        /// Collapse a profile's raw "Challenges" array into friendly editor rows. Adjacent entries sharing a code
        /// with +1 ordinals form one row; anything else starts a new row, so the rows re-expand to the original
        /// array verbatim. Entries the runtime itself ignores (unknown code, no ordinal) are skipped without
        /// breaking a run, because BaseRebirth drops them too.
        /// </summary>
        public static List<ChallengeGroup> GroupChallenges(IEnumerable<string> entries)
        {
            var groups = new List<ChallengeGroup>();
            if (entries == null) return groups;
            ChallengeGroup cur = null;
            foreach (var raw in entries)
            {
                if (!TryParseChallenge(raw, out var it)) continue;
                if (cur != null && cur.Code == it.Code && it.Index == cur.Target + 1)
                {
                    cur.Target = it.Index;
                    cur.Ordinals.Add(it.Index);
                    continue;
                }
                cur = new ChallengeGroup
                {
                    Code = it.Code,
                    Label = ChallengeLabel(it.Code),
                    From = it.Index,
                    Target = it.Index,
                    Cap = ChallengeCap(it.Code),
                };
                cur.Ordinals.Add(it.Index);
                groups.Add(cur);
            }
            return groups;
        }

        private static IEnumerable<string> SplitCsv(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) yield break;
            foreach (var part in s.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) yield return t;
            }
        }
    }
}
