// Ported from Autopilot Tests/StockkeeperMathTests.cs (2026, MIT); Verse-free.
using System.Collections.Generic;
using RimBridge.Steward.Stock;
using Xunit;

namespace RimBridge.Tests
{
    public class StockMathTests
    {
        [Theory]
        [InlineData(ThresholdOp.LowerThan, 100, 500, false, Directive.Increase)]
        [InlineData(ThresholdOp.LowerThan, 500, 500, true, Directive.Hold)]
        [InlineData(ThresholdOp.Equals, 600, 500, false, Directive.Decrease)]
        [InlineData(ThresholdOp.HigherThan, 400, 500, true, Directive.Hold)]
        [InlineData(ThresholdOp.NotEquals, 500, 500, false, Directive.Increase)]
        public void EvaluateAndDirective(ThresholdOp op, int count, int target, bool met, Directive dir)
        {
            Assert.Equal(met, ThresholdMath.Evaluate(op, count, target));
            Assert.Equal(dir, ThresholdMath.EvaluateDirective(op, count, target));
        }

        [Fact]
        public void MigrateUnsupportedOp_FallsBackOnlyWhenUnsupported()
        {
            Assert.Equal(ThresholdOp.LowerThan, ThresholdMath.MigrateUnsupportedOp(ThresholdOp.LowerThan, ThresholdMath.AccumulationOnlyOps, ThresholdOp.LowerThan));
            Assert.Equal(ThresholdOp.LowerThan, ThresholdMath.MigrateUnsupportedOp(ThresholdOp.HigherThan, ThresholdMath.AccumulationOnlyOps, ThresholdOp.LowerThan));
        }

        [Theory]
        [InlineData(-5, 3000, 0)]
        [InlineData(500, 3000, 500)]
        [InlineData(9999, 3000, 3000)]
        public void ClampTarget(int value, int max, int expected)
        {
            Assert.Equal(expected, ThresholdMath.ClampTarget(value, max));
        }

        [Fact]
        public void DesignateUntilTargetOrCap()
        {
            var yields = new List<int> { 50, 40, 30, 20 };
            int n = PlantMath.ComputeNumberToDesignate(100, yields, 0, c => c >= 180, d => d < 40);
            Assert.Equal(2, n); // 100+50+40 = 190 >= 180
            int capped = PlantMath.ComputeNumberToDesignate(0, yields, 39, c => c >= 1000, d => d < 40);
            Assert.Equal(1, capped);
        }

        [Fact]
        public void ReduceStaysAboveTarget()
        {
            var yields = new List<int> { 10, 20, 30 };
            int r = PlantMath.ComputeReduceCount(200, yields, 3, c => c >= 150, d => d > 100);
            Assert.Equal(2, r); // 200-10-20 = 170 ok; 170-30 = 140 < 150 → stop
        }

        [Theory]
        [InlineData(500, 1f, 500)]
        [InlineData(500, 1.5f, 750)]
        [InlineData(500, 0.5f, 250)]
        [InlineData(3, 0.5f, 1)]
        [InlineData(500, 0f, 0)]
        [InlineData(500, -1f, 0)]
        [InlineData(0, 2f, 0)]
        public void ScaleTarget(int baseline, float multiplier, int expected)
        {
            Assert.Equal(expected, ThresholdMath.ScaleTarget(baseline, multiplier));
        }

        [Fact]
        public void PostureMultiplierChangesWhetherTargetIsMet()
        {
            // 300 wood against a 500 target: below normally, met under a x0 posture (defend), still below under x1.5 (build)
            Assert.False(ThresholdMath.Evaluate(ThresholdOp.LowerThan, 300, ThresholdMath.ScaleTarget(500, 1f)));
            Assert.Equal(Directive.Increase, ThresholdMath.EvaluateDirective(ThresholdOp.LowerThan, 300, ThresholdMath.ScaleTarget(500, 1f)));
            Assert.True(ThresholdMath.Evaluate(ThresholdOp.LowerThan, 300, ThresholdMath.ScaleTarget(500, 0f)));
            Assert.Equal(Directive.Hold, ThresholdMath.EvaluateDirective(ThresholdOp.LowerThan, 300, ThresholdMath.ScaleTarget(500, 0f)));
            // 600 wood meets 500 but not the x1.5 target of 750
            Assert.True(ThresholdMath.Evaluate(ThresholdOp.LowerThan, 600, 500));
            Assert.False(ThresholdMath.Evaluate(ThresholdOp.LowerThan, 600, ThresholdMath.ScaleTarget(500, 1.5f)));
        }
    }
}
