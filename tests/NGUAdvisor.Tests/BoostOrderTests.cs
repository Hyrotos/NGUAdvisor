using NGUAdvisor.Managers;
using Xunit;

namespace NGUAdvisor.Tests
{
    public class BoostOrderTests
    {
        private static readonly int[] List = { 81, 84 };       // bag items on the priority list
        private static readonly int[] Worn = { 78, 80 };
        private static readonly int[] Locked = { 90 };

        [Fact]
        public void UnderTheAdvisorListWornGearIsBoostedBeforeTheList()
        {
            Assert.Equal(new[] { 78, 80, 81, 84, 90 }, BoostOrder.Arrange(List, Worn, Locked, advisorList: true));
        }

        [Fact]
        public void AManualListKeepsTheLead()
        {
            Assert.Equal(new[] { 81, 84, 78, 80, 90 }, BoostOrder.Arrange(List, Worn, Locked, advisorList: false));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void LockedInventoryItemsAlwaysComeLast(bool advisorList)
        {
            var order = BoostOrder.Arrange(List, Worn, Locked, advisorList);
            Assert.Equal(90, order[order.Count - 1]);
        }
    }
}
