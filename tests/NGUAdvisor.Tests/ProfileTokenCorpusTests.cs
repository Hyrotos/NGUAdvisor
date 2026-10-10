using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using SimpleJSON;
using Xunit;

namespace NGUAdvisor.Tests
{
    // EVERY `Priorities` TOKEN IN THE REPO, PARSED THE WAY ParseBreakpointArray PARSES IT.
    //
    // The gap this closes is audit 36's headline. The parser has THREE silent exits and only one of them is a
    // parse error anybody would call one:
    //
    //   1. `continue` on a malformed percent or index (14f290e) — the token is destroyed;
    //   2. `yield return null` on an unrecognised stem — every consumer filters `x != null` before prioCount
    //      is computed, so the lane is equally gone and NOTHING logs it;
    //   3. `ALLNGU` on an R3 array — `top = 0`, so the loop body never runs and not even a null is emitted.
    //
    // Exit 2 had twelve live instances: `WISH-0..3` in three Sadistic sample profiles (x3 mirrors = 36 shipped
    // tokens). `WISH` is not one of the parser's 14 stems and not a base code in PriorityCatalog either — the
    // editor would not offer it and the runtime discards it. Wishes are funded by the two Wishes steps in
    // CustomAllocation.DoAllocations (gated on Settings.ManageWishes), never by a profile token, so the stem
    // was never going to exist and the tokens were removed rather than a lane class invented for them.
    //
    // These are string-grammar tests, not allocation tests: they assert which LANES a breakpoint seats, which
    // is precisely the thing that used to disappear without a trace.
    public class ProfileTokenCorpusTests
    {
        private static string RepoRoot([CallerFilePath] string here = null)
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !Directory.Exists(Path.Combine(dir, "NGUAdvisor", "Presets")))
                dir = Path.GetDirectoryName(dir);
            return dir;
        }

        private static string PresetRoot() => Path.Combine(RepoRoot(), "NGUAdvisor", "Presets");
        private static string SampleRoot() => Path.Combine(RepoRoot(), "NGUAdvisor", "SampleProfiles");

        private static IEnumerable<string> AllProfiles() =>
            Directory.GetFiles(PresetRoot(), "*.json")
                .Concat(Directory.GetFiles(SampleRoot(), "*.json", SearchOption.AllDirectories))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        private static readonly string[] Sections = { "Energy", "Magic", "R3" };

        private static BreakpointParseMirror.Pool PoolOf(string section) =>
            section == "Energy" ? BreakpointParseMirror.Pool.Energy :
            section == "Magic" ? BreakpointParseMirror.Pool.Magic :
                                  BreakpointParseMirror.Pool.R3;

        private struct Site
        {
            public string File;
            public string Section;
            public int Breakpoint;
            public int TokenIndex;
            public string Token;
            public override string ToString() =>
                File + " " + Section + "[" + Breakpoint + "] tok[" + TokenIndex + "]=\"" + Token + "\"";
        }

        // Walks exactly the keys the runtime reads: Breakpoints.<section>[].Priorities. Every OTHER
        // array-of-strings key inside a breakpoint (PrioritiesDefault, PrioritiesForNOTM, Before30, After30,
        // AdvDC, …) is inert — BaseBreakpoints reads Time / Priorities / Challenge and nothing else — so this
        // deliberately does not look at them. 492 of them exist; they are documentation, not a program.
        private static IEnumerable<Site> Sites(string file)
        {
            var root = JSON.Parse(File.ReadAllText(file))["Breakpoints"];
            if (root == null) yield break;
            foreach (var section in Sections)
            {
                var arr = root[section];
                if (arr == null || !arr.IsArray) continue;
                for (var b = 0; b < arr.Count; b++)
                {
                    var prios = arr[b]["Priorities"];
                    if (prios == null || !prios.IsArray) continue;
                    for (var t = 0; t < prios.Count; t++)
                        yield return new Site
                        {
                            File = Path.GetFileName(file),
                            Section = section,
                            Breakpoint = b,
                            TokenIndex = t,
                            Token = prios[t].Value
                        };
                }
            }
        }

        private static List<string> Lanes(string file, string section, int breakpoint, bool syncTraining)
        {
            var lanes = new List<BreakpointParseMirror.Lane>();
            foreach (var s in Sites(file))
            {
                if (s.Section != section || s.Breakpoint != breakpoint) continue;
                BreakpointParseMirror.Expand(s.Token, PoolOf(section), syncTraining, lanes);
            }
            return lanes.Select(l => l.ToString()).ToList();
        }

        // ------------------------------------------------------------------ the mirror is pinned to the parser

        // BreakpointParseMirror is a copy, and a copy that drifts is worse than no test. This reads the
        // parser's own source and asserts the stem literals in its StartsWith/Contains chain are exactly the
        // ones the mirror implements — so a fifteenth stem (or a WishBP, if one is ever built) cannot be added
        // to the runtime while the corpus tests keep checking the old grammar.
        [Fact]
        public void Mirror_tag_table_matches_the_parser_source()
        {
            var src = Path.Combine(RepoRoot(), "NGUAdvisor", "AllocationProfiles", "Breakpoints",
                                   "ResourceBreakpoints", "ResourceBreakpoint.cs");
            Assert.True(File.Exists(src), "ResourceBreakpoint.cs moved — the mirror has nothing to pin against");

            var text = File.ReadAllText(src);
            // Uppercase-alpha literals only, so `Contains(":")` / `Contains("-")` are excluded by the pattern.
            var found = new HashSet<string>(
                Regex.Matches(text, @"(?:temp|prio)\.(?:StartsWith|Contains)\(""([A-Z]+)""\)")
                     .Cast<Match>().Select(m => m.Groups[1].Value));
            found.Remove("CAP");   // the IsCap test, not a stem

            Assert.Equal(
                BreakpointParseMirror.StemLiterals.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                found.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        }

        // The mirror's own three silent exits, asserted directly — otherwise the corpus sweep below is a test
        // that can only ever pass. WISH is the token this branch removed from three sample profiles; it is
        // pinned here so the corpus test is demonstrably capable of failing.
        [Theory]
        [InlineData("WISH-0", BreakpointParseMirror.Outcome.UnknownTag)]
        [InlineData("WISH-3:6", BreakpointParseMirror.Outcome.UnknownTag)]
        [InlineData("NGU-4:abc", BreakpointParseMirror.Outcome.Skipped)]      // malformed percent
        [InlineData("NGU-x", BreakpointParseMirror.Outcome.Skipped)]          // malformed index
        [InlineData("NGU--1", BreakpointParseMirror.Outcome.Skipped)]         // negative index is unreachable
        [InlineData("CAPALLNGU", BreakpointParseMirror.Outcome.Emitted)]
        public void Mirror_reproduces_the_parsers_silent_exits(string token, BreakpointParseMirror.Outcome expected)
        {
            var lanes = new List<BreakpointParseMirror.Lane>();
            Assert.Equal(expected, BreakpointParseMirror.Expand(token, BreakpointParseMirror.Pool.Magic, false, lanes));
        }

        [Fact]
        public void Mirror_reproduces_the_group_expansions()
        {
            var lanes = new List<BreakpointParseMirror.Lane>();
            Assert.Equal(BreakpointParseMirror.Outcome.Emitted,
                BreakpointParseMirror.Expand("ALLNGU", BreakpointParseMirror.Pool.Energy, false, lanes));
            Assert.Equal(9, lanes.Count);

            lanes.Clear();
            BreakpointParseMirror.Expand("ALLNGU", BreakpointParseMirror.Pool.Magic, false, lanes);
            Assert.Equal(7, lanes.Count);

            // ALLNGU on R3: top = 0, so the loop body never runs and NOT EVEN A NULL is emitted. The single
            // most invisible outcome the grammar has, and the reason it gets its own corpus test below.
            lanes.Clear();
            Assert.Equal(BreakpointParseMirror.Outcome.EmptyExpansion,
                BreakpointParseMirror.Expand("ALLNGU", BreakpointParseMirror.Pool.R3, false, lanes));
            Assert.Empty(lanes);

            lanes.Clear();
            BreakpointParseMirror.Expand("CAPALLBT", BreakpointParseMirror.Pool.Energy, true, lanes);
            Assert.Equal(6, lanes.Count);
            lanes.Clear();
            BreakpointParseMirror.Expand("CAPALLBT", BreakpointParseMirror.Pool.Energy, false, lanes);
            Assert.Equal(12, lanes.Count);

            lanes.Clear();
            // Lane SET and COUNT only. The lanes' downstream behaviour is not the mirror's business and is
            // not asserted here — see the "WHAT THIS MIRROR DELIBERATELY DOES NOT MODEL" note in
            // BreakpointParseMirror for why a GroupSpread assertion used to sit on this line and why it went.
            BreakpointParseMirror.Expand("CAPALLAT", BreakpointParseMirror.Pool.Energy, false, lanes);
            Assert.Equal(5, lanes.Count);
            Assert.All(lanes, l => Assert.Equal("AdvancedTrainingBP", l.Family));

            lanes.Clear();
            BreakpointParseMirror.Expand("ALLHACK", BreakpointParseMirror.Pool.R3, false, lanes);
            Assert.Equal(15, lanes.Count);   // 0..14; index 15 ("THE END") is excluded outright
        }

        // ------------------------------------------------------------------ the corpus

        // The regression guard for all three silent exits, over every profile in the repo — the auto-installed
        // Presets AND the SampleProfiles the audit found the defect in. A sample profile is not reachable by
        // the runtime today (Directory.GetFiles with no AllDirectories), but promotion into Presets\ is a file
        // copy: the WISH tokens would have become live the moment one of those three files was promoted.
        [Theory]
        [InlineData(true)]    // syncTraining ON  -> ALLBT expands to 6
        [InlineData(false)]   // syncTraining OFF -> ALLBT expands to 12
        public void No_shipped_token_is_silently_discarded(bool syncTraining)
        {
            var bad = new List<string>();
            var tokens = 0;
            var files = 0;

            foreach (var file in AllProfiles())
            {
                files++;
                foreach (var s in Sites(file))
                {
                    tokens++;
                    var lanes = new List<BreakpointParseMirror.Lane>();
                    var outcome = BreakpointParseMirror.Expand(s.Token, PoolOf(s.Section), syncTraining, lanes);
                    if (outcome != BreakpointParseMirror.Outcome.Emitted)
                        bad.Add(outcome + ": " + s);
                }
            }

            Assert.True(files > 60, "the corpus walk found only " + files + " profiles — the roots are wrong");
            Assert.True(tokens > 1000, "the corpus walk found only " + tokens + " tokens — the key walk is wrong");
            Assert.True(bad.Count == 0,
                "tokens that parse to nothing and log nothing:\n  " + string.Join("\n  ", bad));
        }

        // Exit 3 has zero instances and is the most invisible outcome the grammar can produce — no lane, no
        // null, no divisor change, nothing to filter. Pinned separately so it stays at zero.
        [Fact]
        public void No_profile_puts_ALLNGU_on_an_R3_array()
        {
            foreach (var file in AllProfiles())
                foreach (var s in Sites(file))
                {
                    if (s.Section != "R3") continue;
                    Assert.False(s.Token.ToUpperInvariant().Contains("ALLNGU"),
                        s + " — ALLNGU on an R3 array expands to zero lanes and emits nothing at all");
                }
        }

        // ------------------------------------------------------------------ the three repaired files

        // The exact lane set each repaired breakpoint now seats. Written out in full rather than as "four
        // fewer than before": the whole failure was that nobody could see what a Priorities array actually
        // produces, so the fix is only verified if the expectation is the lane list itself.
        // ⚠ THE PERCENT SUFFIXES ARE GONE FROM THESE EXPECTATIONS BECAUSE THE CORPUS CHANGED, NOT
        // BECAUSE THE TEST WAS FAILING. `:percent` now means MANUAL MODE, so the shipped sample
        // profiles had it stripped from their Energy/Magic timelines (where it was inert under the
        // constraint allocator) and kept on R3 (where it was always honoured).
        //
        // This theory CAUGHT that migration, which is what it is for. Every edit below is a suffix
        // removal and nothing else - same lanes, same order, same count, same cap flags:
        //     WandoosBP#0(cap:10) -> (cap)     TimeMachineBP#0(cap:10) -> (cap)
        //     WandoosBP#0(cap:20) -> (cap)     NGUBP#1(cap:50)         -> (cap)
        //     BR#0(:50) / BR#0(:10)            -> BR#0()
        // If a future edit here removes or adds a LANE, that is a different thing and needs its own
        // justification - the whole point of writing the list out in full is that the diff shows it.
        [Theory]
        [InlineData("Sadistic", "24hrRB-StartSad.json", 1,
            "WandoosBP#0(cap)", "TimeMachineBP#0(cap)", "BR#0()",
            "NGUBP#0(cap)", "NGUBP#1(cap)", "NGUBP#2(cap)", "NGUBP#3(cap)",
            "NGUBP#4(cap)", "NGUBP#5(cap)", "NGUBP#6(cap)")]
        [InlineData("Sadistic", "MuffinRB-StartSad.json", 1,
            "WandoosBP#0(cap)", "TimeMachineBP#0(cap)", "BR#0()",
            "NGUBP#0(cap)", "NGUBP#1(cap)", "NGUBP#2(cap)", "NGUBP#3(cap)",
            "NGUBP#4(cap)", "NGUBP#5(cap)", "NGUBP#6(cap)")]
        [InlineData("Sadistic", "MidSADHackDay.json", 3,
            "WandoosBP#0(cap)", "BR#0()", "NGUBP#1(cap)",
            "NGUBP#0()", "NGUBP#1()", "NGUBP#2()", "NGUBP#3()",
            "NGUBP#4()", "NGUBP#5()", "NGUBP#6()")]
        public void Repaired_magic_breakpoint_seats_exactly_these_lanes(
            string difficulty, string name, int breakpoint, params string[] expected)
        {
            var file = Path.Combine(SampleRoot(), difficulty, name);
            Assert.True(File.Exists(file), file + " is missing");
            Assert.Equal(expected, Lanes(file, "Magic", breakpoint, syncTraining: false).ToArray());
        }

        // …and no WISH token survives anywhere in the corpus, not just in the three breakpoints above. Checked
        // against the TOKENS rather than the file text, because the three files now carry a Note that names
        // the removed tokens and a text scan cannot tell an explanation from a program.
        [Fact]
        public void No_profile_anywhere_still_carries_a_WISH_token()
        {
            foreach (var file in AllProfiles())
                foreach (var s in Sites(file))
                    Assert.False(s.Token.ToUpperInvariant().StartsWith("WISH", StringComparison.Ordinal),
                        s + " — there is no WISH stem and no WishBP lane; the token would parse to null and vanish");
        }

        // ------------------------------------------------------------------ RIT-7

        // RIT-7 is KEPT, and this records why so the next sweep does not re-open it. The token parses cleanly
        // (it is not a grammar defect) and it seats RitualBP#7 — the EIGHTH ritual, which exists only once
        // AllBloodMagicController.ritualsUnlocked() returns 8, i.e. once the NORMAL Troll challenge has 6+
        // completions. CBlock2-Normal runs TC-5/6/7 at campaign order 4; both profiles below are Evil, at
        // order >= 5. So the precondition is met by the campaign's own sequencing, and when it is NOT met the
        // lane fails Unlocked(), is filtered by IsValid() BEFORE prioCount is computed, and costs nothing.
        [Theory]
        [InlineData("24hr-EarlyEvil.json", 1)]
        [InlineData("CBlock3.1-E100LC.json", 0)]
        public void RIT_7_parses_to_the_eighth_ritual_lane(string name, int breakpoint)
        {
            var file = Path.Combine(PresetRoot(), name);
            Assert.Contains("RitualBP#7()", Lanes(file, "Magic", breakpoint, syncTraining: false));
        }

        // The unlock is a COUNT and rituals are 0-based, which is the whole reason RIT-7 needs 8 rituals.
        // RitualMath is linked here, so the arithmetic behind the paragraph above is asserted, not narrated.
        [Fact]
        public void The_eighth_ritual_needs_eight_unlocked_not_seven()
        {
            Assert.False(Managers.RitualMath.RitualIndexUnlocked(7, 7));   // no Troll 6 -> RIT-7 is locked
            Assert.True(Managers.RitualMath.RitualIndexUnlocked(7, 8));    // Troll >= 6 -> RIT-7 is real
            Assert.True(Managers.RitualMath.RitualIndexUnlocked(6, 7));    // RIT-6 is the top ritual before it
        }

        // ------------------------------------------------------------------ the five Evil daily drivers

        // THE FIVE FILES `ec2c89f` PUT IN Presets\, PINNED TO THE LANES THEY ACTUALLY SEAT.
        //
        // audit 50 §4.2 carried these as "four unrepaired Evil presets", on the reasoning that a sibling from
        // the same commit had needed repair and these four had not been touched since. §4.2's own limits
        // section says plainly that it did not measure them. Measured 2026-08-07: all five are strict JSON,
        // carry no duplicate key, no duplicate array element, and no token that parses to nothing. They were
        // not repaired because ba94cfd — the pass that fixed 32 malformed profiles — did not list them as
        // changed, i.e. they were already valid. "Untouched" meant "needed nothing".
        //
        // What 24hr-EarlyEvil's repair actually WAS: ba94cfd turned `"Priorities": [ "RIT-7" }` into
        // `[ "RIT-7" ]` — one bracket, in the Magic section. Not a deduplication of anything.
        //
        // NOT claimed clean in every sense: LRB-Evil Energy[4] and Magic[4] are two of the 39 corpus-wide
        // LANE-level two-seat breakpoints audit 36 §3b tabulates (`CAPNGU-8` … `ALLNGU` seats NGUBP#8 twice).
        // 36 calls that class deliberate rather than a typo, and it is overwhelmingly a NORMAL-profile idiom
        // — CBlock1/2/3/4/5, Normal-LRB and SampleProfiles\Normal\24hr.json all carry it. Left exactly as
        // found: whether the second seat should cost the other lanes a share is a profile-design question.
        [Theory]
        [InlineData("24hr-EarlyEvil.json", "Energy", 3,
            // CAPALLBT leads every energy breakpoint: Basic Training costs next to nothing.
            "BasicTrainingBP#0(cap)", "BasicTrainingBP#1(cap)", "BasicTrainingBP#2(cap)", "BasicTrainingBP#3(cap)",
            "BasicTrainingBP#4(cap)", "BasicTrainingBP#5(cap)", "BasicTrainingBP#6(cap)", "BasicTrainingBP#7(cap)",
            "BasicTrainingBP#8(cap)", "BasicTrainingBP#9(cap)", "BasicTrainingBP#10(cap)", "BasicTrainingBP#11(cap)",
            "NGUBP#0()", "NGUBP#1()", "NGUBP#2()", "NGUBP#3()", "NGUBP#4()",
            "NGUBP#5()", "NGUBP#6()", "NGUBP#7()", "NGUBP#8()")]
        [InlineData("24hr-EarlyEvil.json", "Magic", 3,
            "NGUBP#0()", "NGUBP#1()", "NGUBP#2()", "NGUBP#3()", "NGUBP#4()", "NGUBP#5()", "NGUBP#6()")]
        public void Evil_daily_driver_breakpoint_seats_exactly_these_lanes(
            string name, string section, int breakpoint, params string[] expected)
        {
            var file = Path.Combine(PresetRoot(), name);
            Assert.True(File.Exists(file), file + " is missing");
            Assert.Equal(expected, Lanes(file, section, breakpoint, syncTraining: false).ToArray());
        }

        // NINE, NOT EIGHTEEN — the arithmetic audit 01 finding #11 turned on, pinned on the SHIPPED file.
        //
        // #11 (and audit 36 §3a, which measured it) are correct, and they are about the operator's INSTALLED
        // 24hr-EarlyEvil.json, not this one: that file carried `[ALLNGU, CAPWAN, ALLAT, ALLNGU]` at Energy[3]
        // and `[ALLNGU, BR:20, WAN, ALLNGU]` at Magic[3], both duplicate seats non-CAP, so prioCount counted
        // nine NGU lanes as eighteen. 36 §3a is explicit that 01's line numbers resolve against the live file
        // "not against Presets/".
        //
        // PresetInstaller and PresetInstallPlan both compress this to "the installed 24hr-EarlyEvil.json still
        // lists ALLNGU twice ... a month after the repo fixed it", which invites the reading that Presets\ was
        // once broken and got repaired. It was not. Energy h22 and Magic h22 have held one bare ALLNGU each
        // since 679e1d8, and the only repair this file has ever received is ba94cfd's `[ "RIT-7" }` bracket.
        // This test holds the nine so the shipped side of that sentence stays checkable.
        [Fact]
        public void Every_ALLNGU_breakpoint_in_the_Evil_presets_seats_nine_energy_or_seven_magic_NGU_lanes()
        {
            var drivers = new[] { "24hr-EarlyEvil.json", "24hr-Evil.json", "24hr-MidEvil.json",
                                  "24hr-EndEvil.json", "LRB-Evil.json" };
            var checkedAny = 0;

            foreach (var name in drivers)
            {
                var file = Path.Combine(PresetRoot(), name);
                Assert.True(File.Exists(file), name + " is missing from Presets\\");

                // The ALLNGU TOKEN's own expansion, not the whole breakpoint's. A breakpoint may legitimately
                // seat an NGU lane from a second token as well — LRB-Evil Energy[4] is `CAPNGU-8, ALLNGU,
                // CAPALLAT`, which caps NGU 8 and then spreads the remaining idle across all nine, the
                // "CAP allocations make dividers between groups" idiom the presets document in their own
                // PriorityComment. That is a profile-design question and is NOT adjudicated here; see the
                // lane-collision note in the branch report. What #11 alleged is narrower and is what this
                // measures: that ONE `ALLNGU` does not expand to eighteen lanes.
                foreach (var s in Sites(file))
                {
                    // Bare ALLNGU only — CAPALLNGU is a different token and seats capped lanes.
                    if (!string.Equals(s.Token, "ALLNGU", StringComparison.OrdinalIgnoreCase)) continue;

                    var lanes = new List<BreakpointParseMirror.Lane>();
                    var outcome = BreakpointParseMirror.Expand(
                        s.Token, PoolOf(s.Section), syncTraining: false, lanes);

                    Assert.Equal(BreakpointParseMirror.Outcome.Emitted, outcome);
                    var seats = lanes.Select(l => l.ToString()).ToList();
                    Assert.Equal(seats.Count, seats.Distinct().Count());   // one ALLNGU never repeats a lane

                    var expected = s.Section == "Energy" ? 9 : 7;
                    Assert.True(seats.Count == expected,
                        name + " " + s.Section + "[" + s.Breakpoint + "] — one ALLNGU expands to " +
                        seats.Count + " lanes, expected " + expected + ": [" + string.Join(", ", seats) + "]");
                    checkedAny++;
                }
            }

            Assert.True(checkedAny >= 8,
                "only " + checkedAny + " ALLNGU breakpoints found across the five Evil presets — the walk is wrong");
        }

        // CBlock2-Normal is what supplies the sixth Troll. If it ever stops doing so, the two RIT-7 profiles
        // above become dead weight and this is the line that says so.
        [Fact]
        public void CBlock2_Normal_still_takes_the_normal_Troll_count_past_six()
        {
            var text = File.ReadAllText(Path.Combine(PresetRoot(), "CBlock2-Normal.json"));
            var model = Managers.ProfileModel.Load(text);
            Assert.Contains("TC-6", model.Challenges);
            var cblock2 = Managers.CampaignTables.BlockOfProfile("CBlock2-Normal");
            var cblock31 = Managers.CampaignTables.BlockOfProfile("CBlock3.1-E100LC");
            Assert.NotNull(cblock2);
            Assert.NotNull(cblock31);
            Assert.True(cblock2.Order < cblock31.Order,
                "CBlock2-Normal must run before CBlock3.1-E100LC or RIT-7 is unreachable when the block starts");
        }
    }
}
