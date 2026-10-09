using System.Collections.Generic;
using System.Linq;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    public class GearChainTests
    {
        private static GearPriority Step(string objective, int slots)
            => new GearPriority { Objective = GearChain.FindObjective(objective), MaxAccessorySlots = slots };

        [Fact]
        public void FindPreset_IsCaseInsensitiveAndReturnsNullForUnknown()
        {
            Assert.NotNull(GearChain.FindPreset("adventure + respawn"));
            Assert.Null(GearChain.FindPreset("no such chain"));
        }

        [Fact]
        public void EveryPresetResolvesItsObjectivesAndRespectsTheLengthCap()
        {
            Assert.Equal(4, GearChain.Presets.Count);
            foreach (var preset in GearChain.Presets)
            {
                Assert.NotEmpty(preset.Priorities);
                Assert.True(preset.Priorities.Count <= GearChain.MaxPriorities);
                foreach (var priority in preset.Priorities)
                    Assert.NotNull(priority.Objective);
            }
        }

        // A preset is picked by name from the same list as the objectives, and a plain objective
        // wins the lookup -- a collision would make the preset unreachable.
        [Fact]
        public void PresetNamesDoNotCollideWithObjectiveNames()
        {
            foreach (var preset in GearChain.Presets)
                Assert.Null(GearChain.FindObjective(preset.Name));
        }

        // A chain is handed to callers that only know Objective, and they score with its Stats.
        [Fact]
        public void APresetScoresAsItsLeadStep()
        {
            foreach (var preset in GearChain.Presets)
            {
                Assert.Same(preset.Priorities[0].Objective.Stats, preset.Stats);
                Assert.Same(preset.Priorities[0].Objective.Exponents, preset.Exponents);
            }
        }

        [Fact]
        public void StepsOf_APlainObjectiveIsOneUnlimitedStep()
        {
            var respawn = GearChain.FindObjective("Respawn");
            var step = Assert.Single(GearChain.StepsOf(respawn));
            Assert.Same(respawn, step.Objective);
            Assert.Equal(GearChain.Unlimited, step.MaxAccessorySlots);
            Assert.False(step.PinTopPowerWeapon);

            var preset = GearChain.FindPreset("Adventure + Respawn");
            Assert.Same(preset.Priorities, GearChain.StepsOf(preset));
            Assert.Empty(GearChain.StepsOf(null));
        }

        [Fact]
        public void Describe_RendersBudgetsAndDistinguishesUnlimited()
        {
            Assert.Equal("Adventure(3) > Respawn(1) > Adventure(all)",
                         GearChain.Describe(GearChain.FindPreset("Adventure + Respawn").Priorities));
            Assert.Equal("Adventure(all)", GearChain.Describe(new[] { Step("Adventure", GearChain.Unlimited) }));
            Assert.Equal("Adventure(2)", GearChain.Describe(new[] { Step("Adventure", 2) }));
        }

        [Fact]
        public void Describe_HandlesNullEmptyAndUnusableSteps()
        {
            Assert.Equal("(no chain)", GearChain.Describe(null));
            Assert.Equal("(no chain)", GearChain.Describe(new GearPriority[0]));
            Assert.Equal("(no chain)", GearChain.Describe(new[] { new GearPriority { MaxAccessorySlots = 2 } }));
            Assert.Equal("Adventure(1)", GearChain.Describe(new[] { null, new GearPriority(), Step("Adventure", 1) }));
        }

        [Fact]
        public void Describe_GivesEveryPresetItsOwnKey()
        {
            var keys = new HashSet<string>();
            foreach (var preset in GearChain.Presets)
                Assert.True(keys.Add(GearChain.Describe(preset.Priorities)), $"duplicate key for '{preset.Name}'");
        }

        [Fact]
        public void FarmPresetsPinTheTopPowerWeaponOnTheirLead()
        {
            foreach (var name in new[] { "Drop Chance + Adventure", "Drop Chance + NGUs" })
            {
                var preset = GearChain.FindPreset(name);
                Assert.NotNull(preset);
                Assert.True(preset.Priorities[0].PinTopPowerWeapon, $"{name} lead must pin the power weapon");
                Assert.Equal(0, preset.Priorities[0].MaxAccessorySlots);
                Assert.Equal("Drop Chance", preset.Priorities[1].Objective.Name);
                Assert.Equal(GearChain.Unlimited, preset.Priorities[1].MaxAccessorySlots);
            }
            Assert.Equal("Adventure(0)+PowerWeapon > Drop Chance(all)",
                         GearChain.Describe(GearChain.FindPreset("Drop Chance + Adventure").Priorities));
        }

        // Under Respawn(3) > NGUs(all) the capped Respawn lead read x1 forever, so a +57 % NGU set
        // was never equipped.
        [Fact]
        public void DecidingStep_ACappedLeadPassesTheVerdictToTheNextStep()
        {
            int step = GearChain.DecidingStep(new[] { 0.8, 1.0e6 }, new[] { 0.8, 1.57e6 }, 1.02, out bool improves);
            Assert.Equal(1, step);
            Assert.True(improves);
        }

        [Fact]
        public void DecidingStep_TheFirstStepOutsideTheBarDecidesEitherWay()
        {
            Assert.Equal(0, GearChain.DecidingStep(new[] { 1.0, 1.0 }, new[] { 1.05, 0.5 }, 1.02, out bool leadImproves));
            Assert.True(leadImproves);

            Assert.Equal(1, GearChain.DecidingStep(new[] { 1.0, 1.0 }, new[] { 1.01, 0.9 }, 1.02, out bool tailImproves));
            Assert.False(tailImproves);

            Assert.Equal(-1, GearChain.DecidingStep(new[] { 1.0, 1.0 }, new[] { 1.019, 0.99 }, 1.02, out _));
        }

        [Fact]
        public void DecidingStep_AStatNothingWornCarriesIsAnImprovementNotAZeroDivision()
        {
            Assert.Equal(1, GearChain.DecidingStep(new[] { 0.0, 0.0 }, new[] { 0.0, 0.3 }, 1.02, out bool improves));
            Assert.True(improves);
            Assert.Equal(-1, GearChain.DecidingStep(new[] { 0.0 }, new[] { 0.0 }, 1.02, out _));
        }

        // The chain's own set scores its LEAD lower than a lead-only set, so the single-score test
        // would call the old set optimal and never equip the chain.
        [Fact]
        public void DecideChain_IsSetMembershipNotAScore()
        {
            Assert.Equal(GearRefreshPolicy.Verdict.Equip, GearRefreshPolicy.DecideChain(false, true));
            Assert.Equal(GearRefreshPolicy.Verdict.Equip, GearRefreshPolicy.DecideChain(true, false));
            Assert.Equal(GearRefreshPolicy.Verdict.AlreadyOptimal, GearRefreshPolicy.DecideChain(true, true));
        }

        // ── SPELLED CHAINS ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Find_ResolvesAPlainObjectiveANamedChainAndASpelledOne()
        {
            Assert.Same(GearChain.FindObjective("Adventure"), GearChain.Find("adventure"));
            Assert.Same(GearChain.FindPreset("Adventure + Respawn"), GearChain.Find("Adventure + Respawn"));
            Assert.IsType<GearChain.ChainObjective>(GearChain.Find("Respawn(1) > NGUs(all)"));
            Assert.Null(GearChain.Find("Advent"));
            Assert.Null(GearChain.Find(""));
            Assert.Null(GearChain.Find(null));
        }

        // Describe and Parse are the two halves of one spelling: whatever one prints, the other reads.
        [Fact]
        public void Parse_ReadsBackExactlyWhatDescribePrints()
        {
            foreach (var preset in GearChain.Presets)
            {
                string spelled = GearChain.Describe(preset.Priorities);
                var parsed = GearChain.Parse(spelled);
                Assert.NotNull(parsed);
                Assert.Equal(spelled, parsed.Name);
                Assert.Equal(spelled, GearChain.Describe(parsed.Priorities));
                Assert.Equal(preset.Priorities.Select(p => p.PinTopPowerWeapon), parsed.Priorities.Select(p => p.PinTopPowerWeapon));
                Assert.Equal(preset.Priorities.Select(p => p.MaxAccessorySlots), parsed.Priorities.Select(p => p.MaxAccessorySlots));
            }
        }

        [Fact]
        public void Parse_IsForgivingAboutSpacingAndCaseAndNamesTheChainCanonically()
        {
            var chain = GearChain.Parse("  adventure( 3 )>respawn(1) >  ADVENTURE(All)+powerweapon ");
            Assert.NotNull(chain);
            Assert.Equal("Adventure(3) > Respawn(1) > Adventure(all)+PowerWeapon", chain.Name);
            Assert.Equal(GearChain.Unlimited, chain.Priorities[2].MaxAccessorySlots);
            Assert.True(chain.Priorities[2].PinTopPowerWeapon);
            // An objective whose own name has a space in it.
            Assert.Equal("Energy NGU(2)", GearChain.Parse("Energy NGU(2)").Name);
        }

        // Strict: a chain with a step silently missing is a different chain.
        [Theory]
        [InlineData("Adventure")]                        // not a chain at all
        [InlineData("Adventure(3) > Nonsense(1)")]       // unknown objective
        [InlineData("Adventure(3) > (1)")]               // no objective
        [InlineData("Adventure(-1)")]                    // not a budget
        [InlineData("Adventure(x)")]
        [InlineData("Adventure(3")]
        [InlineData("Adventure(3) >")]
        [InlineData("Power(1) > Power(1) > Power(1) > Power(1) > Power(1) > Power(1)")]   // six steps
        public void Parse_RefusesAnythingItCannotReadInFull(string spelled)
            => Assert.Null(GearChain.Parse(spelled));

        private static KeyValuePair<string, int> S(string objective, int slots)
            => new KeyValuePair<string, int>(objective, slots);

        // The profile's structured "Priorities" spelling, as the same name.
        [Fact]
        public void Spell_TurnsStructuredStepsIntoTheChainsName()
        {
            Assert.Equal("Adventure(3) > Respawn(1) > Adventure(all)",
                         GearChain.Spell(new[] { S("adventure", 3), S("Respawn", 1), S("Adventure", 0) }, false));
            // The row-level pin lands on the lead step, like the farm presets write it.
            Assert.Equal("Adventure(0)+PowerWeapon > Drop Chance(all)",
                         GearChain.Spell(new[] { S("Adventure", -1), S("Drop Chance", 0) }, true));
        }

        // A negative Slots claims nothing. Mapping it to "all" would let a typo'd -1 swallow every
        // accessory slot and starve the rest of the chain.
        [Fact]
        public void Spell_ANegativeBudgetClaimsNothingAndAnUnknownStepIsDroppedAndReported()
        {
            var unknown = new List<string>();
            Assert.Equal("Respawn(0) > NGUs(all)",
                         GearChain.Spell(new[] { S("Respawn", -1), S("Nonsense", 2), S("NGUs", 0) }, false, unknown));
            Assert.Equal(new[] { "Nonsense" }, unknown);
            Assert.Null(GearChain.Spell(new[] { S("Nonsense", 1) }, false));
            Assert.Null(GearChain.Spell(null, false));
        }

        [Fact]
        public void Spell_UsesOnlyTheFirstFiveSteps()
        {
            var six = Enumerable.Range(1, 6).Select(i => S("Power", i));
            Assert.Equal("Power(1) > Power(2) > Power(3) > Power(4) > Power(5)", GearChain.Spell(six, false));
        }

        [Fact]
        public void WithPowerWeapon_PinsTheLeadOfAnyObjectiveOnce()
        {
            Assert.Equal("NGUs(all)+PowerWeapon", GearChain.WithPowerWeapon("NGUs"));
            Assert.Equal("Adventure(3)+PowerWeapon > Respawn(1) > Adventure(all)", GearChain.WithPowerWeapon("Adventure + Respawn"));
            Assert.Equal("Drop Chance + Adventure", GearChain.WithPowerWeapon("Drop Chance + Adventure"));   // already pinned
            Assert.Null(GearChain.WithPowerWeapon("Nonsense"));
        }

        // ── PROFILE ADVICE ────────────────────────────────────────────────────────────────────────

        private static string Profile(string gearRows)
            => "{ \"Breakpoints\": { \"Gear\": [" + gearRows + "] } }";

        [Fact]
        public void ProfileWarnings_SaysNothingAboutRowsThatResolve()
        {
            Assert.Empty(GearChain.ProfileWarnings(Profile(
                "{ \"Time\": 0, \"ID\": [1, 2] }," +
                "{ \"Time\": 60, \"ID\": [], \"Objective\": \"NGUs\" }," +
                "{ \"Time\": 120, \"ID\": [], \"Objective\": \"Drop Chance + Adventure\" }," +
                "{ \"Time\": 180, \"ID\": [], \"Objective\": \"Respawn(1) > NGUs(all)\" }," +
                "{ \"Time\": 240, \"ID\": [], \"Priorities\": [ { \"Objective\": \"Respawn\", \"Slots\": 1 }, { \"Objective\": \"NGUs\" } ] }")));
            Assert.Empty(GearChain.ProfileWarnings(""));
            Assert.Empty(GearChain.ProfileWarnings("not json"));
        }

        [Fact]
        public void ProfileWarnings_NamesARowThatWillChooseNoGear()
        {
            var w = GearChain.ProfileWarnings(Profile(
                "{ \"Time\": 3600, \"ID\": [], \"Objective\": \"Advent\" }," +
                "{ \"Time\": 5400, \"ID\": [], \"Objective\": \"Adventure(3) > Nope(1)\" }"));
            Assert.Equal(2, w.Count);
            Assert.Contains("\"Advent\" at 1:00 is not recognized", w[0]);
            Assert.Contains("\"Adventure(3) > Nope(1)\" at 1:30 could not be read", w[1]);
        }

        [Fact]
        public void ProfileWarnings_CoversTheStructuredChain()
        {
            string steps = string.Join(",", Enumerable.Range(0, 6).Select(_ => "{ \"Objective\": \"Power\", \"Slots\": 1 }"));
            var tooLong = GearChain.ProfileWarnings(Profile("{ \"Time\": 0, \"ID\": [], \"Priorities\": [" + steps + "] }"));
            Assert.Contains("has 6 steps; only the first 5 are used", Assert.Single(tooLong));

            var w = GearChain.ProfileWarnings(Profile(
                "{ \"Time\": 0, \"ID\": [], \"Priorities\": [ { \"Slots\": 1 }, { \"Objective\": \"Nope\" }, { \"Objective\": \"NGUs\", \"Slots\": -2 } ] }"));
            Assert.Equal(3, w.Count);
            Assert.Contains("has no Objective", w[0]);
            Assert.Contains("\"Nope\" at 0:00 is not recognized", w[1]);
            Assert.Contains("negative Slots", w[2]);
        }

        // ── THE EDITOR'S SIDE ─────────────────────────────────────────────────────────────────────

        // "Priorities" supersedes Objective at runtime and survives a round trip untouched, so a
        // setter that states what the row now optimizes for has to take it out.
        [Fact]
        public void SettingAGearRowsObjectiveRemovesItsStructuredChain()
        {
            string row = "{ \"Time\": 0, \"ID\": [], \"TopPowerWeapon\": true, \"Custom\": \"keep\", " +
                         "\"Priorities\": [ { \"Objective\": \"Respawn\", \"Slots\": 1 } ] }";

            var untouched = ProfileModel.Load(ProfileModel.Load(Profile(row)).ToJson());
            Assert.Contains(untouched.Gear[0].Extras, kv => kv.Key == "Priorities");
            Assert.Contains(untouched.Gear[0].Extras, kv => kv.Key == "TopPowerWeapon");

            foreach (var set in new System.Action<ProfileModel>[]
            {
                m => m.SetGearObjective(0, "NGUs", false),
                m => m.SetGearLock(0, new List<int> { 5 }, "NGUs", false),
                m => m.SetItems("gear", 0, new List<int> { 5 }),
            })
            {
                var m = ProfileModel.Load(Profile(row));
                set(m);
                var reloaded = ProfileModel.Load(m.ToJson());
                Assert.DoesNotContain(reloaded.Gear[0].Extras, kv => kv.Key == "Priorities" || kv.Key == "TopPowerWeapon");
                Assert.Contains(reloaded.Gear[0].Extras, kv => kv.Key == "Custom");
            }
        }

        [Fact]
        public void TheEditorSavesAReadableChainAndRefusesAnUnreadableOne()
        {
            var m = ProfileModel.Load(Profile("{ \"Time\": 0, \"ID\": [] }"));
            var ok = BreakpointEditor.Apply(m, "gear", 0, 0, "Lock: 326; Optimize+Respawn: Adventure(3) > Respawn(1) > Adventure(all)", "", null);
            Assert.True(ok.Ok, ok.Error);
            var saved = ProfileModel.Load(m.ToJson()).Gear[0];
            Assert.Equal("Adventure(3) > Respawn(1) > Adventure(all)", saved.Objective);
            Assert.True(saved.ForceRespawn);
            Assert.Equal(new[] { 326 }, saved.Items);

            var bad = BreakpointEditor.Apply(m, "gear", 0, 0, "Optimize: Adventure(3) > Nope(1)", "", null);
            Assert.False(bad.Ok);
            Assert.Contains("Can't read the chain", bad.Error);
            Assert.Equal("Adventure(3) > Respawn(1) > Adventure(all)", m.Gear[0].Objective);   // nothing written
        }
    }
}
