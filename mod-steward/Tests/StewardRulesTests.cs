// Verse-free tests for the steward's pure rules (research queue ordering, stock stall rule).
using System.Collections.Generic;
using RimBridge.Steward;
using Xunit;

namespace RimBridge.Tests
{
    public class StewardRulesTests
    {
        [Fact]
        public void Merge_ReplacesOrAppends_AndDeduplicates()
        {
            var existing = new List<string> { "Electricity", "Batteries" };
            Assert.Equal(new[] { "Batteries", "Stonecutting" }, ResearchQueueLogic.Merge(existing, new[] { "Batteries", " Stonecutting ", "", "Batteries" }, append: false));
            Assert.Equal(new[] { "Electricity", "Batteries", "Stonecutting" }, ResearchQueueLogic.Merge(existing, new[] { "Stonecutting", "Electricity" }, append: true));
            Assert.Empty(ResearchQueueLogic.Merge(null, null, append: true));
        }

        [Fact]
        public void PickNext_SkipsUnavailable_DropsFinished_KeepsOrder()
        {
            var queue = new List<string> { "Done", "NeedsPrereq", "Ready", "AlsoReady" };
            var finished = new HashSet<string> { "Done" };
            var startable = new HashSet<string> { "Ready", "AlsoReady" };
            string? next = ResearchQueueLogic.PickNext(queue, finished.Contains, startable.Contains);
            Assert.Equal("Ready", next);
            Assert.Equal(new[] { "NeedsPrereq", "AlsoReady" }, queue);   // finished dropped, unavailable kept in place
            // once the prerequisite is met the earlier entry wins again
            startable.Add("NeedsPrereq");
            Assert.Equal("NeedsPrereq", ResearchQueueLogic.PickNext(queue, finished.Contains, startable.Contains));
            Assert.Equal(new[] { "AlsoReady" }, queue);
        }

        [Fact]
        public void PickNext_NothingAvailable_ReturnsNull_AndLeavesQueue()
        {
            var queue = new List<string> { "A", "B" };
            Assert.Null(ResearchQueueLogic.PickNext(queue, _ => false, _ => false));
            Assert.Equal(new[] { "A", "B" }, queue);
            Assert.Null(ResearchQueueLogic.PickNext(new List<string>(), _ => false, _ => true));
        }

        [Theory]
        [InlineData(3, 0, 2500, true)]     // three failed runs
        [InlineData(2, 0, 2500, false)]
        [InlineData(0, 24, 2500, true)]    // a day of empty runs at the default interval
        [InlineData(0, 23, 2500, false)]
        [InlineData(0, 12, 5000, true)]    // hunting runs every 5000 ticks
        [InlineData(0, 0, 2500, false)]
        public void StallRule(int failures, int emptyRuns, int interval, bool stalled)
        {
            Assert.Equal(stalled, StockStallRule.IsStalled(failures, emptyRuns, interval));
        }

        [Fact]
        public void StallReport_OncePerDay()
        {
            Assert.True(StockStallRule.ShouldReport(5, null));
            Assert.False(StockStallRule.ShouldReport(5, 5));
            Assert.True(StockStallRule.ShouldReport(6, 5));
        }
    }
}
