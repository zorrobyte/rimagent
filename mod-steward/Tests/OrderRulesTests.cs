// Verse-free tests for the rules behind the unforbid / corpses / beds / policies / blueprints standing orders.
using System.Collections.Generic;
using System.Linq;
using RimBridge.Steward.Orders;
using Xunit;

namespace RimBridge.Tests
{
    public class OrderRulesTests
    {
        // ── OwnedValues ──

        [Fact]
        public void OwnedValues_DetectsOutsideChanges_AndMarksManualForCooldown()
        {
            var o = new OwnedValues();
            Assert.False(o.Observe("temp:1", "21.0", 100)); // no record yet: nothing to compare
            o.Record("temp:1", "21.0");
            Assert.False(o.Observe("temp:1", "21.0", 200));
            Assert.False(o.IsManual("temp:1", 200));
            Assert.True(o.Observe("temp:1", "18.0", 300, 1000));
            Assert.True(o.IsManual("temp:1", 300));
            Assert.True(o.IsManual("temp:1", 1299));
            Assert.False(o.IsManual("temp:1", 1300));
            Assert.Equal("18.0", o.Set["temp:1"]); // record follows the outside change
            Assert.False(o.Observe("temp:1", "18.0", 1400));
        }

        [Fact]
        public void OwnedValues_ForeverNeverExpires_AndPruneDropsDeadKeys()
        {
            var o = new OwnedValues();
            o.MarkManual("med:a", 10, OwnedValues.Forever);
            Assert.True(o.IsManual("med:a", int.MaxValue - 1));
            o.MarkManual("bed:b", 10, 100);
            o.Record("bed:b", "x"); o.Record("bed:c", "y");
            int n = o.Prune(500, new[] { "med:a", "bed:b" });
            Assert.Equal(2, n); // bed:b's mark expired, bed:c is gone
            Assert.False(o.ManualUntil.ContainsKey("bed:b"));
            Assert.False(o.Set.ContainsKey("bed:c"));
            Assert.True(o.Set.ContainsKey("bed:b"));
            // a later, shorter mark never shortens a live one
            o.MarkManual("bed:b", 600, 1000);
            o.MarkManual("bed:b", 700, 10);
            Assert.Equal(1600, o.ManualUntil["bed:b"]);
        }

        // ── temperature ──

        [Theory]
        [InlineData(SeasonKind.Winter, true, 21f)]
        [InlineData(SeasonKind.Winter, false, 21f)]
        [InlineData(SeasonKind.PermanentWinter, false, 21f)]
        [InlineData(SeasonKind.Summer, true, 24f)]
        [InlineData(SeasonKind.Summer, false, 24f)]
        [InlineData(SeasonKind.PermanentSummer, true, 24f)]
        [InlineData(SeasonKind.Spring, true, 21f)]
        [InlineData(SeasonKind.Spring, false, 24f)]
        [InlineData(SeasonKind.Fall, true, 21f)]
        [InlineData(SeasonKind.Undefined, false, 24f)]
        public void TempTargets_BySeasonAndDevice(SeasonKind season, bool heater, float expected)
        {
            Assert.Equal(expected, TempTargets.Target(season, heater));
        }

        [Fact]
        public void TempTargets_ClampsToDeviceRange()
        {
            Assert.Equal(20f, TempTargets.Clamp(24f, -50f, 20f));
            Assert.Equal(22f, TempTargets.Clamp(21f, 22f, 50f));
        }

        /// <summary>The temperature pass as Order_Policies.RunTemperature runs it, over OwnedValues + TempRules; returns the target it would set (null = left alone).</summary>
        static float? TempPass(OwnedValues o, string key, float current, float want, int tick, bool cooler)
        {
            string cur = current.ToString("0.0");
            if (o.IsManual(key, tick)) return null;
            if (TempRules.WhyNot(o.HasRecord(key), current, 21f, cooler) != null) { o.Record(key, cur); o.MarkManual(key, tick, OwnedValues.Forever); return null; }
            if (o.Observe(key, cur, tick, OwnedValues.Forever)) return null;
            float set = System.Math.Abs(current - want) > 0.05f ? want : current;
            o.Record(key, set.ToString("0.0"));
            return set;
        }

        [Fact]
        public void TempRules_AdoptOnlyDefaults_NeverFreezers_OutsideChangeIsFinal()
        {
            Assert.Null(TempRules.WhyNot(hasRecord: false, current: 21f, defaultTarget: 21f, cooler: true));
            Assert.Null(TempRules.WhyNot(false, 21.04f, 21f, false));
            Assert.Equal("not at the default target", TempRules.WhyNot(false, 18f, 21f, false));
            Assert.Equal("freezer", TempRules.WhyNot(false, -10f, 21f, cooler: true));
            Assert.Equal("freezer", TempRules.WhyNot(true, -10f, 21f, cooler: true));      // even a managed cooler someone turned into a freezer
            Assert.Null(TempRules.WhyNot(true, -10f, 21f, cooler: false));                 // a heater at -10 is just an outside change (Observe handles it)

            var o = new OwnedValues();
            // fresh 21°C cooler in summer → set to 24 and managed
            Assert.Equal(24f, TempPass(o, "temp:fresh", 21f, 24f, 100, cooler: true));
            Assert.Equal(24f, TempPass(o, "temp:fresh", 24f, 24f, 1300, cooler: true));
            // a -10°C cooler with no record → untouched and manual forever
            Assert.Null(TempPass(o, "temp:freezer", -10f, 24f, 100, cooler: true));
            Assert.True(o.IsManual("temp:freezer", int.MaxValue - 1));
            Assert.Null(TempPass(o, "temp:freezer", -10f, 24f, 999999, cooler: true));
            // a managed cooler the director changed to 10°C → untouched now and still untouched after 2+ days
            Assert.Null(TempPass(o, "temp:fresh", 10f, 24f, 2500, cooler: true));
            Assert.Null(TempPass(o, "temp:fresh", 10f, 24f, 2500 + 2 * OwnedValues.TwoDays, cooler: true));
            Assert.True(o.IsManual("temp:fresh", 2500 + 2 * OwnedValues.TwoDays));
            // a heater first seen at 18°C is not adopted either
            Assert.Null(TempPass(o, "temp:heater", 18f, 21f, 100, cooler: false));
            Assert.Null(TempPass(o, "temp:heater", 18f, 21f, 999999, cooler: false));
        }

        // ── food switch ──

        [Fact]
        public void FoodSwitch_HasHysteresis()
        {
            Assert.False(FoodSwitch.Decide(meals: 10, colonists: 5, active: false)); // 10 == 5*2: not below
            Assert.True(FoodSwitch.Decide(9, 5, false));
            Assert.True(FoodSwitch.Decide(19, 5, true));   // stays on below 5*4
            Assert.False(FoodSwitch.Decide(20, 5, true));  // off at 5*4
            Assert.False(FoodSwitch.Decide(0, 0, true));   // no colonists: never on
        }

        // ── medical defaults ──

        [Fact]
        public void MedicalDefaults_FollowTheSpec()
        {
            Assert.Equal("NormalOrWorse", MedicalDefaults.For(CareKind.Colonist));
            Assert.Equal("HerbalOrWorse", MedicalDefaults.For(CareKind.Prisoner));
            Assert.Equal("HerbalOrWorse", MedicalDefaults.For(CareKind.Animal));
            Assert.Equal("HerbalOrWorse", MedicalDefaults.For(CareKind.Slave));
        }

        // ── corpse routing ──

        static CorpseFacts Human(bool grave = false, bool stock = false, bool crem = false, bool rotten = false)
            => new CorpseFacts { Humanlike = true, FreeGrave = grave, StockpileAccepts = stock, Crematorium = crem, Rotten = rotten };

        [Fact]
        public void CorpseRouting_HumanFresh_GraveThenStockpileThenCremation()
        {
            Assert.Equal(CorpseAction.Bury, CorpseRouting.Decide(Human(grave: true, stock: true, crem: true)));
            Assert.Equal(CorpseAction.StripAndHaul, CorpseRouting.Decide(Human(stock: true, crem: true)));
            Assert.Equal(CorpseAction.Cremate, CorpseRouting.Decide(Human(crem: true)));
            Assert.Equal(CorpseAction.None, CorpseRouting.Decide(Human()));
        }

        [Fact]
        public void CorpseRouting_Rotten_DumpElseCremateHumansElseNothing()
        {
            Assert.Equal(CorpseAction.Dump, CorpseRouting.Decide(Human(rotten: true, stock: true, grave: true)));
            Assert.Equal(CorpseAction.Cremate, CorpseRouting.Decide(Human(rotten: true, crem: true)));
            Assert.Equal(CorpseAction.None, CorpseRouting.Decide(Human(rotten: true)));
            Assert.Equal(CorpseAction.Dump, CorpseRouting.Decide(new CorpseFacts { Animal = true, Rotten = true, StockpileAccepts = true }));
            Assert.Equal(CorpseAction.None, CorpseRouting.Decide(new CorpseFacts { Animal = true, Rotten = true, Crematorium = true }));
        }

        [Fact]
        public void CorpseRouting_Guards_AndAnimals()
        {
            Assert.Equal(CorpseAction.Butcher, CorpseRouting.Decide(new CorpseFacts { Animal = true }));
            Assert.Equal(CorpseAction.None, CorpseRouting.Decide(new CorpseFacts { Animal = true, CombatActive = true }));
            var hostile = Human(grave: true); hostile.HostileCamp = true;
            Assert.Equal(CorpseAction.None, CorpseRouting.Decide(hostile));
            var touched = Human(grave: true); touched.Touched = true;
            Assert.Equal(CorpseAction.None, CorpseRouting.Decide(touched));
            Assert.Equal(CorpseAction.None, CorpseRouting.Decide(new CorpseFacts())); // mechanoid/unknown
            Assert.Equal(CorpseAction.None, CorpseRouting.Decide(null!));
        }

        // ── bed pairing ──

        [Fact]
        public void BedPairing_PrefersPartnersBedWithRoom()
        {
            var beds = new List<BedCandidate>
            {
                new BedCandidate("single-near", 1, 0, 1f),
                new BedCandidate("partner-double", 2, 1, 50f, partnerOwns: true),
            };
            Assert.Equal("partner-double", BedPairing.Choose(beds, partnerNeedsBed: false));
            // partner's bed is full (someone else is in it): fall through to the nearest empty bed
            beds[1] = new BedCandidate("partner-double", 2, 2, 50f, partnerOwns: true);
            Assert.Equal("single-near", BedPairing.Choose(beds, false));
        }

        [Fact]
        public void BedPairing_CoupleWithoutBeds_TakesEmptyDouble_ElseNearestEmpty()
        {
            var beds = new List<BedCandidate>
            {
                new BedCandidate("single-near", 1, 0, 1f),
                new BedCandidate("double-far", 2, 0, 90f),
                new BedCandidate("double-occupied", 2, 1, 2f), // a stranger sleeps here: never
            };
            Assert.Equal("double-far", BedPairing.Choose(beds, partnerNeedsBed: true));
            Assert.Equal("single-near", BedPairing.Choose(beds, partnerNeedsBed: false));
        }

        [Fact]
        public void BedPairing_SkipsManualMedicalAndUnusable()
        {
            var beds = new List<BedCandidate>
            {
                new BedCandidate("manual", 1, 0, 1f, manual: true),
                new BedCandidate("medical", 1, 0, 2f, medical: true),
                new BedCandidate("crib", 1, 0, 3f, usable: false),
                new BedCandidate("ok", 1, 0, 40f),
            };
            Assert.Equal("ok", BedPairing.Choose(beds, false));
            Assert.Null(BedPairing.Choose(beds.Take(3).ToList(), false));
            Assert.Null(BedPairing.Choose(new List<BedCandidate>(), true));
        }

        // ── blueprints ──

        [Fact]
        public void BlueprintRules_StaleAndUnreachableTimers()
        {
            Assert.False(BlueprintRules.IsStale(-1, 999999));
            Assert.False(BlueprintRules.IsStale(0, 3 * 60000 - 1));
            Assert.True(BlueprintRules.IsStale(0, 3 * 60000));
            Assert.False(BlueprintRules.CancelUnreachable(-1, 100000));
            Assert.False(BlueprintRules.CancelUnreachable(1000, 1000 + 4999));
            Assert.True(BlueprintRules.CancelUnreachable(1000, 1000 + 5000));
        }

        [Fact]
        public void BlueprintRules_MissingMaterials()
        {
            var have = new Dictionary<string, int> { ["Steel"] = 30, ["WoodLog"] = 500 };
            var missing = BlueprintRules.Missing(new[] { ("Steel", 100), ("WoodLog", 20), ("ComponentIndustrial", 2), ("Nothing", 0) }, d => have.TryGetValue(d, out int n) ? n : 0);
            Assert.Equal(2, missing.Count);
            Assert.Contains(("Steel", 70), missing);
            Assert.Contains(("ComponentIndustrial", 2), missing);
            Assert.Empty(BlueprintRules.Missing(null!, d => 0));
        }

        // ── unforbid ──

        [Fact]
        public void UnforbidRules_ScopeAndExclusions()
        {
            Assert.Null(UnforbidRules.WhyNot(new ForbiddenFacts { InHomeArea = true, DistToBase = 99f }));
            Assert.Null(UnforbidRules.WhyNot(new ForbiddenFacts { DistToBase = 20f }));
            Assert.Equal("outside", UnforbidRules.WhyNot(new ForbiddenFacts { DistToBase = 21f }));
            Assert.Null(UnforbidRules.WhyNot(new ForbiddenFacts { DistToBase = 40f, RecentItem = true }));
            Assert.Equal("outside", UnforbidRules.WhyNot(new ForbiddenFacts { DistToBase = 41f, RecentItem = true }));
            Assert.Equal("colonist corpse", UnforbidRules.WhyNot(new ForbiddenFacts { InHomeArea = true, ColonistCorpse = true }));
            Assert.Equal("hands-off", UnforbidRules.WhyNot(new ForbiddenFacts { InHomeArea = true, Touched = true }));
            Assert.Equal("hostile structure", UnforbidRules.WhyNot(new ForbiddenFacts { InHomeArea = true, HostileStructure = true }));
            Assert.Equal("caravan", UnforbidRules.WhyNot(new ForbiddenFacts { InHomeArea = true, CaravanItem = true }));
            Assert.Equal("unreachable", UnforbidRules.WhyNot(new ForbiddenFacts { InHomeArea = true, Reachable = false }));
        }
    }
}
