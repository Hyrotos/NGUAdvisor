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
    }
}
