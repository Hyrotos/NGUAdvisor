using System;
using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    public class EditorCatalogTests
    {
        // A picker built from these names offers exactly indexes 0..IndexMax. One name short and the
        // last index cannot be picked; one too many and the editor writes a token the advisor ignores.
        [Fact]
        public void Every_named_index_list_covers_exactly_the_tokens_index_range()
        {
            foreach (ResourceKind kind in Enum.GetValues(typeof(ResourceKind)))
                foreach (var bt in PriorityCatalog.For(kind))
                {
                    var names = EditorCatalog.IndexNames(kind, bt.Code);
                    if (!bt.HasIndex) { Assert.Null(names); continue; }
                    if (names == null) continue;   // a plain number: RIT, BR
                    Assert.True(names.Length == bt.IndexMax + 1,
                        $"{kind} {bt.Code}: {names.Length} names for indexes 0..{bt.IndexMax}");
                    Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
                }
        }

        [Fact]
        public void The_same_code_is_named_per_resource()
        {
            Assert.Equal("Gold", EditorCatalog.IndexNames(ResourceKind.Energy, "NGU")[3]);
            Assert.Equal("Number", EditorCatalog.IndexNames(ResourceKind.Magic, "NGU")[3]);
            Assert.Null(EditorCatalog.IndexNames(ResourceKind.R3, "NGU"));
        }

        // Even is the augment, odd its upgrade: AUG-12 / AUG-13 are the Laser Sword pair.
        [Fact]
        public void Augment_indexes_spell_out_the_augment_upgrade_pairing()
        {
            var aug = EditorCatalog.IndexNames(ResourceKind.Energy, "aug");
            Assert.Equal("Safety Scissors", aug[0]);
            Assert.Equal("Safety Scissors — upgrade", aug[1]);
            Assert.Equal("Laser Sword", aug[12]);
            Assert.Equal("Laser Sword — upgrade", aug[13]);
        }

        [Fact]
        public void A_plain_number_index_and_an_unknown_code_have_no_names()
        {
            Assert.Null(EditorCatalog.IndexNames(ResourceKind.Magic, "RIT"));
            Assert.Null(EditorCatalog.IndexNames(ResourceKind.Magic, "BR"));
            Assert.Null(EditorCatalog.IndexNames(ResourceKind.Energy, "NOPE"));
            Assert.Null(EditorCatalog.IndexNames(ResourceKind.Energy, null));
        }
    }
}
