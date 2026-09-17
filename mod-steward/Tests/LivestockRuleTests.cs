// Verse-free tests for the livestock arithmetic (bucket split, tame/slaughter planning, summary).
using RimBridge.Steward;
using Xunit;

namespace RimBridge.Tests
{
    public class LivestockRuleTests
    {
        static LivestockRule.Targets Total(int min, int max) => new LivestockRule.Targets { Min = min, Max = max };
        static int[] B(int am, int af, int jm, int jf) => new[] { am, af, jm, jf };
        static readonly int[] None = { 0, 0, 0, 0 };

        [Fact]
        public void Buckets_MapAdultAndGender_AndParseNames()
        {
            Assert.Equal(LivestockRule.AdultMale, LivestockRule.Bucket(adult: true, female: false));
            Assert.Equal(LivestockRule.AdultFemale, LivestockRule.Bucket(adult: true, female: true));
            Assert.Equal(LivestockRule.JuvenileMale, LivestockRule.Bucket(adult: false, female: false));
            Assert.Equal(LivestockRule.JuvenileFemale, LivestockRule.Bucket(adult: false, female: true));
            Assert.Equal(0, LivestockRule.BucketIndex("adult_male"));
            Assert.Equal(1, LivestockRule.BucketIndex("AdultFemale"));
            Assert.Equal(3, LivestockRule.BucketIndex("juvenile female"));
            Assert.Equal(-1, LivestockRule.BucketIndex("cats"));
            Assert.Equal(-1, LivestockRule.BucketIndex(null));
        }

        [Fact]
        public void Meets_TotalMode_UsesMinAndMax()
        {
            Assert.True(LivestockRule.Meets(Total(2, 5), B(1, 2, 0, 0)));
            Assert.False(LivestockRule.Meets(Total(2, 5), B(1, 0, 0, 0)));
            Assert.False(LivestockRule.Meets(Total(2, 5), B(3, 3, 0, 0)));
            Assert.True(LivestockRule.Meets(Total(2, LivestockRule.NoLimit), B(10, 10, 0, 0)));   // no upper bound
            Assert.True(LivestockRule.Meets(Total(6, 4), B(3, 3, 0, 0)));                       // max < min: max follows min
        }

        [Fact]
        public void Compute_TotalMode_TamesBreedingStockFirst_WithinBudget()
        {
            var plan = LivestockRule.Compute(Total(6, 10), have: B(1, 1, 0, 0), pendingTame: None, wild: B(3, 2, 2, 2), cullable: None, maxNew: 3);
            Assert.Equal(3, plan.TameTotal);                       // need 4, budget 3
            Assert.Equal(2, plan.Tame[LivestockRule.AdultFemale]); // females first
            Assert.Equal(1, plan.Tame[LivestockRule.AdultMale]);
            Assert.Equal(0, plan.SlaughterTotal);
        }

        [Fact]
        public void Compute_TotalMode_PendingTameCountsTowardMin()
        {
            var plan = LivestockRule.Compute(Total(4, 6), have: B(1, 1, 0, 0), pendingTame: B(0, 2, 0, 0), wild: B(5, 5, 0, 0), cullable: None, maxNew: 10);
            Assert.Equal(0, plan.TameTotal);
        }

        [Fact]
        public void Compute_TotalMode_SlaughtersMalesFirst_NeverBelowMax()
        {
            var have = B(3, 3, 2, 2);   // 10 owned, max 6
            var plan = LivestockRule.Compute(Total(2, 6), have, None, None, cullable: B(3, 3, 2, 2), maxNew: 10);
            Assert.Equal(4, plan.SlaughterTotal);
            Assert.Equal(3, plan.Slaughter[LivestockRule.AdultMale]);
            Assert.Equal(1, plan.Slaughter[LivestockRule.JuvenileMale]);
            Assert.Equal(0, plan.Slaughter[LivestockRule.AdultFemale]);
            Assert.Equal(0, plan.TameTotal);
        }

        [Fact]
        public void Compute_TotalMode_RespectsVetoedAnimals_AndFlags()
        {
            var have = B(3, 3, 0, 0);
            var plan = LivestockRule.Compute(Total(0, 4), have, None, None, cullable: B(1, 0, 0, 0), maxNew: 10);
            Assert.Equal(1, plan.SlaughterTotal);   // only one male may be culled (others bonded/pregnant)
            var off = LivestockRule.Compute(Total(0, 4), have, None, None, cullable: B(3, 3, 0, 0), maxNew: 10, tame: true, slaughter: false);
            Assert.Equal(0, off.SlaughterTotal);
            var noTame = LivestockRule.Compute(Total(8, 10), B(1, 0, 0, 0), None, wild: B(5, 5, 0, 0), cullable: None, maxNew: 10, tame: false);
            Assert.Equal(0, noTame.TameTotal);
        }

        [Fact]
        public void Compute_TotalMode_ReleasesSurplusPendingTame()
        {
            // 4 owned + 3 pending tame = 7 > max 5: drop 2 pending (young males first)
            var plan = LivestockRule.Compute(Total(2, 5), have: B(2, 2, 0, 0), pendingTame: B(1, 1, 1, 0), wild: B(5, 5, 5, 5), cullable: None, maxNew: 10);
            Assert.Equal(0, plan.TameTotal);
            Assert.Equal(2, plan.ReleaseTotal);
            Assert.Equal(1, plan.ReleaseTame[LivestockRule.JuvenileMale]);
            Assert.Equal(1, plan.ReleaseTame[LivestockRule.AdultMale]);
            Assert.Equal(0, plan.SlaughterTotal);
        }

        [Fact]
        public void Compute_BucketMode_PlansPerBucket()
        {
            var t = new LivestockRule.Targets
            {
                BucketMin = B(1, 4, 0, 0),
                BucketMax = B(1, 6, 0, LivestockRule.NoLimit),   // no young males at all, young females unbounded
            };
            Assert.True(t.PerBucket);
            var have = B(2, 2, 3, 5);
            var plan = LivestockRule.Compute(t, have, None, wild: B(0, 3, 0, 0), cullable: B(2, 2, 3, 5), maxNew: 10);
            Assert.Equal(2, plan.Tame[LivestockRule.AdultFemale]);   // 2 -> 4
            Assert.Equal(1, plan.Slaughter[LivestockRule.AdultMale]); // 2 -> 1
            Assert.Equal(3, plan.Slaughter[LivestockRule.JuvenileMale]);
            Assert.Equal(0, plan.Slaughter[LivestockRule.JuvenileFemale]);
            Assert.False(LivestockRule.Meets(t, have));
            Assert.True(LivestockRule.Meets(t, B(1, 5, 0, 9)));
        }

        [Fact]
        public void Summary_ReportsBucketsAndBounds()
        {
            Assert.Equal("adult ♂2 ♀4 · young ♂1 ♀0 = 7 (min 4, max 10)", LivestockRule.Summary(Total(4, 10), B(2, 4, 1, 0)));
            Assert.Contains("max ∞", LivestockRule.Summary(Total(4, LivestockRule.NoLimit), B(2, 4, 1, 0)));
            var t = new LivestockRule.Targets { BucketMin = B(1, 2, 0, 0), BucketMax = B(1, 4, 0, LivestockRule.NoLimit) };
            Assert.Contains("adult_female 2..4", LivestockRule.Summary(t, B(1, 2, 0, 0)));
            Assert.Contains("juvenile_female 0..∞", LivestockRule.Summary(t, B(1, 2, 0, 0)));
        }
    }
}
