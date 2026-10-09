using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    // Shape/monotonicity guards for the hand-extracted titan requirement tables (review finding #33). These are
    // magic numbers transcribed from the game's autokill methods + the community guide; a typo or a stale row
    // would silently corrupt titan targeting. These invariants were verified against the current data, and are
    // written to respect the intentional 0.0 sentinels (no regen gate below T4; no idle stage for some titans).
    public class TitanTablesTests
    {
        [Fact]
        public void Both_tables_cover_twelve_titans_with_matching_version_counts()
        {
            Assert.Equal(12, TitanTables.Ak.Length);
            Assert.Equal(12, TitanTables.Guide.Length);
            for (int t = 0; t < 12; t++)
            {
                int expected = t < 5 ? 1 : 4;   // T1-T5 single form; T6-T12 have four versions
                Assert.Equal(expected, TitanTables.Ak[t].Length);
                Assert.Equal(expected, TitanTables.Guide[t].Length);
            }
        }

        [Fact]
        public void Ak_rows_have_three_nonnegative_columns()
        {
            foreach (var titan in TitanTables.Ak)
                foreach (var ver in titan)
                {
                    Assert.Equal(3, ver.Length);
                    Assert.All(ver, x => Assert.True(x >= 0));
                }
        }

        [Fact]
        public void Guide_rows_have_four_nonnegative_columns()
        {
            foreach (var titan in TitanTables.Guide)
                foreach (var ver in titan)
                {
                    Assert.Equal(4, ver.Length);
                    Assert.All(ver, x => Assert.True(x >= 0));
                }
        }

        [Fact]
        public void Ak_attack_and_defense_positive_and_regen_gates_from_T4()
        {
            for (int t = 0; t < TitanTables.Ak.Length; t++)
                foreach (var ver in TitanTables.Ak[t])
                {
                    Assert.True(ver[0] > 0, $"T{t + 1} atk must be > 0");
                    Assert.True(ver[1] > 0, $"T{t + 1} def must be > 0");
                    // Regen (HP-regen gate) is a sentinel 0 for T1-T3, a real positive gate from T4 (index 3) up.
                    if (t < 3) Assert.Equal(0.0, ver[2]);
                    else Assert.True(ver[2] > 0, $"T{t + 1} regen must gate (> 0)");
                }
        }

        [Fact]
        public void Guide_manual_positive_and_idle_is_both_or_neither()
        {
            foreach (var titan in TitanTables.Guide)
                foreach (var ver in titan)
                {
                    Assert.True(ver[0] > 0, "manual atk must be > 0");
                    Assert.True(ver[1] > 0, "manual def must be > 0");
                    // Idle atk/def are a sentinel PAIR: either both 0 (no idle stage) or both positive.
                    Assert.Equal(ver[2] == 0.0, ver[3] == 0.0);
                }
        }

        [Fact]
        public void Ak_requirements_strictly_increase_across_versions()
        {
            for (int t = 0; t < TitanTables.Ak.Length; t++)
            {
                var vers = TitanTables.Ak[t];
                for (int v = 1; v < vers.Length; v++)
                    for (int col = 0; col < 3; col++)
                        Assert.True(vers[v][col] > vers[v - 1][col],
                            $"T{t + 1} Ak col{col}: v{v + 1} ({vers[v][col]}) must exceed v{v} ({vers[v - 1][col]})");
            }
        }

        [Fact]
        public void Ak_first_version_attack_increases_across_titans()
        {
            for (int t = 1; t < TitanTables.Ak.Length; t++)
                Assert.True(TitanTables.Ak[t][0][0] > TitanTables.Ak[t - 1][0][0],
                    $"T{t + 1} v1 atk must exceed T{t} v1 atk");
        }

        // --------------------------------------------------------------- version counts (UiBridge `ak[].vmax`)
        // TWO INDEPENDENT SOURCES describe how many versions a titan has: the Ak table's row count, and
        // ZoneHelpers.IsVersionedTitan's `index >= 5 && index <= 11`. UiBridge publishes the first as `vmax`
        // while `v` comes from ZoneHelpers.TitanVersion, which is gated on the second — so a disagreement
        // would ship a chip reading e.g. "v1 of 1" for a titan the game happily advances to v2.
        //
        // ZoneHelpers pulls in Unity and cannot be linked into this assembly, so the predicate is MIRRORED
        // here as a literal copy. That is the point: this test fails if either side moves independently.
        private static bool IsVersionedTitan(int titanIndex) => titanIndex >= 5 && titanIndex <= 11;

        [Fact]
        public void VersionCount_matches_Ak_row_count_for_every_titan()
        {
            for (int t = 0; t < TitanTables.Ak.Length; t++)
                Assert.Equal(TitanTables.Ak[t].Length, TitanTables.VersionCount(t));
        }

        [Fact]
        public void VersionCount_agrees_with_the_IsVersionedTitan_predicate()
        {
            for (int t = 0; t < TitanTables.Ak.Length; t++)
            {
                int vmax = TitanTables.VersionCount(t);
                if (IsVersionedTitan(t))
                    Assert.True(vmax > 1, $"T{t + 1} is a versioned titan but the Ak table gives it {vmax} version(s)");
                else
                    Assert.True(vmax == 1, $"T{t + 1} is unversioned but the Ak table gives it {vmax} versions");
            }
        }

        [Fact]
        public void VersionCount_never_reports_zero_even_off_the_table()
        {
            // Tippi (12) and Traitor (13) have no Ak row at all, and the UI would divide by this.
            Assert.Equal(1, TitanTables.VersionCount(12));
            Assert.Equal(1, TitanTables.VersionCount(13));
            Assert.Equal(1, TitanTables.VersionCount(-1));
            Assert.Equal(1, TitanTables.VersionCount(int.MaxValue));
        }

        [Fact]
        public void Every_version_from_1_to_vmax_has_a_readable_Ak_row()
        {
            // `vmax` is only useful to the UI if every version it advertises actually resolves to a row —
            // otherwise "v2 of 4" could still degrade to state "unknown".
            for (int t = 0; t < TitanTables.Ak.Length; t++)
            {
                int vmax = TitanTables.VersionCount(t);
                for (int v = 1; v <= vmax; v++)
                    Assert.NotNull(TitanTables.AkRow(t, v));
                Assert.Null(TitanTables.AkRow(t, vmax + 1));
            }
        }

        [Fact]
        public void Guide_manual_requirements_increase_across_versions()
        {
            for (int t = 0; t < TitanTables.Guide.Length; t++)
            {
                var vers = TitanTables.Guide[t];
                for (int v = 1; v < vers.Length; v++)
                    for (int col = 0; col < 2; col++)   // manual atk/def only; idle columns may be sentinel 0
                        Assert.True(vers[v][col] > vers[v - 1][col],
                            $"Guide T{t + 1} col{col}: v{v + 1} must exceed v{v}");
            }
        }

        // ZoneHelpers.TitanVersion returns the version you are currently ON (save field + 1), so it is
        // never below 1. "Have I beaten one yet" is therefore version-1, and getting that wrong is not
        // loud: ExpBalancer used `TitanVersion(6) >= 1`, which is a tautology, and it silently disabled
        // the guide's whole pre-T7 Evil EXP rule because ExpRatio.Split tests that flag first.
        //
        // THE CASE THAT MATTERS is the first one: on a fresh Evil account titan7Version is 0, so
        // TitanVersion returns 1, and "versions defeated" must be ZERO.
        [Theory]
        [InlineData(1, 0)]   // on v1, none beaten  <- the bug: this used to read as "post-T7"
        [InlineData(2, 1)]   // v1 beaten
        [InlineData(3, 2)]
        [InlineData(4, 3)]
        public void VersionsDefeated_is_one_less_than_the_version_you_are_on(int humanVersion, int expected)
        {
            Assert.Equal(expected, TitanTables.VersionsDefeated(humanVersion));
        }

        // TitanVersion returns 1 for a non-versioned titan and could in principle return 0 from a failed
        // read; neither may produce a negative count, which would invert every `>= 1` test downstream.
        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        public void VersionsDefeated_never_goes_negative(int humanVersion)
        {
            Assert.Equal(0, TitanTables.VersionsDefeated(humanVersion));
        }

        [Fact]
        public void Titan_stats_gate_rejects_unready_manual_fights_and_honors_regen_gate()
        {
            // Grand Corrupted Tree is T2 (index 1): first manual kill requires 5K attack / 4K defense.
            var gct = TitanTables.Guide[1][0];
            Assert.False(TitanTables.MeetsStatsRequirement(1200, 1220, 0, gct[0], gct[1], 0));
            Assert.True(TitanTables.MeetsStatsRequirement(gct[0], gct[1], 0, gct[0], gct[1], 0));

            // T4's autokill stage also requires the game's HP-regen gate.
            var t4Ak = TitanTables.Ak[3][0];
            Assert.False(TitanTables.MeetsStatsRequirement(t4Ak[0], t4Ak[1], t4Ak[2] - 1,
                t4Ak[0], t4Ak[1], t4Ak[2]));
            Assert.True(TitanTables.MeetsStatsRequirement(t4Ak[0], t4Ak[1], t4Ak[2],
                t4Ak[0], t4Ak[1], t4Ak[2]));
        }

        [Fact]
        public void Manual_fight_gate_uses_manual_stats_not_the_idle_or_ak_rows()
        {
            // GRB after its first kill: 1.5K/1.5K clears manual (1350/1350) but neither idle
            // (2300/2100) nor AK (3000/2500) — it must stay fightable.
            Assert.True(TitanTables.ManualFightReady(0, 1, 1500, 1500));
            Assert.False(TitanTables.ManualFightReady(0, 1, 1349, 1500));
            Assert.False(TitanTables.ManualFightReady(0, 1, 1500, 1349));
            // GCT needs 5K/4K, and an unknown titan/version is never ready.
            Assert.False(TitanTables.ManualFightReady(1, 1, 1500, 1500));
            Assert.False(TitanTables.ManualFightReady(0, 2, 1e30, 1e30));
            Assert.False(TitanTables.ManualFightReady(99, 1, 1e30, 1e30));
        }

        // No row is "unknown", not "unready": the caller keeps Tippi/Traitor queued on exactly this.
        [Fact]
        public void Manual_row_is_null_only_where_the_tables_hold_nothing()
        {
            Assert.Same(TitanTables.Guide[1][0], TitanTables.ManualRow(1, 1));
            Assert.Same(TitanTables.Guide[5][3], TitanTables.ManualRow(5, 4));
            Assert.Null(TitanTables.ManualRow(12, 1));
            Assert.Null(TitanTables.ManualRow(13, 1));
            Assert.Null(TitanTables.ManualRow(0, 2));
        }
    }
}
