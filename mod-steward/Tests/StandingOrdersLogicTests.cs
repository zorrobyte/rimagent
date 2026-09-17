// Verse-free tests for the standing-order rules (rally spreading, draft eligibility, touch cooldowns, stagger schedule, fire buckets).
using System.Collections.Generic;
using System.Linq;
using RimBridge.Steward.Orders;
using Xunit;

namespace RimBridge.Tests
{
    public class StandingOrdersLogicTests
    {
        static List<RallyCandidate> Grid(int w, int h, System.Func<int, int, float>? cover = null)
        {
            var list = new List<RallyCandidate>();
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                    list.Add(new RallyCandidate(x, z, cover?.Invoke(x, z) ?? 0f));
            return list;
        }

        [Fact]
        public void Spread_ReturnsDistinctCells_NeverMoreThanAskedOrAvailable()
        {
            var cells = Grid(5, 5);
            var picks = RallyLogic.Spread(cells, 6, 2, 2);
            Assert.Equal(6, picks.Count);
            Assert.Equal(6, picks.Select(p => (p.X, p.Z)).Distinct().Count());
            Assert.Equal(25, RallyLogic.Spread(cells, 100, 2, 2).Count);
            Assert.Empty(RallyLogic.Spread(cells, 0, 2, 2));
            Assert.Empty(RallyLogic.Spread(new List<RallyCandidate>(), 3, 0, 0));
        }

        [Fact]
        public void Spread_PrefersCoverThenSpacing()
        {
            // one covered cell in the corner, everything else open: it is still picked first
            var cells = Grid(7, 7, (x, z) => x == 6 && z == 6 ? 1.0f : 0f);
            var picks = RallyLogic.Spread(cells, 3, 3, 3);
            Assert.Equal((6, 6), (picks[0].X, picks[0].Z));
            // later picks keep their distance from each other
            for (int i = 0; i < picks.Count; i++)
                for (int j = i + 1; j < picks.Count; j++)
                {
                    int dx = System.Math.Abs(picks[i].X - picks[j].X), dz = System.Math.Abs(picks[i].Z - picks[j].Z);
                    Assert.True(dx + dz >= 2, $"picks {i} and {j} are adjacent");
                }
        }

        [Fact]
        public void Spread_OpenGround_StartsNearCenter()
        {
            var picks = RallyLogic.Spread(Grid(9, 9), 1, 4, 4);
            Assert.Equal((4, 4), (picks[0].X, picks[0].Z));
        }

        [Fact]
        public void CombatEligibility_FollowsTheSpec()
        {
            Assert.Null(CombatEligibility.WhyNot(new DraftFlags()));
            Assert.Equal("downed", CombatEligibility.WhyNot(new DraftFlags { Downed = true }));
            Assert.Equal("prisoner", CombatEligibility.WhyNot(new DraftFlags { Prisoner = true }));
            Assert.Equal("child", CombatEligibility.WhyNot(new DraftFlags { Juvenile = true }));
            Assert.Equal("incapable of violence", CombatEligibility.WhyNot(new DraftFlags { IncapableOfViolence = true }));
            Assert.Equal("mental state", CombatEligibility.WhyNot(new DraftFlags { InMentalState = true }));
            Assert.Contains("health 29%", CombatEligibility.WhyNot(new DraftFlags { HealthPct = 0.29f }));
            Assert.Null(CombatEligibility.WhyNot(new DraftFlags { HealthPct = 0.3f }));
            Assert.Equal("unarmed", CombatEligibility.WhyNot(new DraftFlags { HasWeapon = false }));
            Assert.Contains("unmanaged", CombatEligibility.WhyNot(new DraftFlags { Unmanaged = true }));
            Assert.Contains("hands-off", CombatEligibility.WhyNot(new DraftFlags { Touched = true }));
            Assert.Equal("not on the map", CombatEligibility.WhyNot(new DraftFlags { Dead = true }));
            Assert.False(CombatEligibility.CanDraft(new DraftFlags { HasDrafter = false }));
        }

        [Fact]
        public void TouchTable_ExpiresAfterCooldown_AndKeepsTheLongerOne()
        {
            var t = new TouchTable();
            t.Touch("Human1", "ui.draft", now: 1000);
            Assert.True(t.IsTouched("Human1", 1000));
            Assert.True(t.IsTouched("Human1", 3499));
            Assert.False(t.IsTouched("Human1", 3500));
            Assert.False(t.IsTouched("Nobody", 1000));
            Assert.False(t.IsTouched(null, 1000));
            // a shorter re-touch never shortens an existing cooldown, and both reasons stay live side by side
            t.Touch("Human1", "ui.goto", now: 1100, cooldownTicks: 100);
            Assert.True(t.IsTouched("Human1", 3499));
            Assert.Equal(new[] { "ui.draft", "ui.goto" }, t.LiveReasons("Human1", 1150));
            Assert.Equal(new[] { "ui.draft" }, t.LiveReasons("Human1", 1200));       // the short goto touch expired on its own
            Assert.Equal(1, t.Prune(9999));
            Assert.Equal(0, t.Count);
        }

        [Fact]
        public void TouchTable_KeepsEveryScopedReason_LaterTouchesNeverEraseEarlierOnes()
        {
            var t = new TouchTable();
            int now = 100;
            t.Touch("Jen", "ui.set_policies:area", now);
            t.Touch("Jen", "ui.set_policies:hostility", now);
            Assert.True(t.IsTouched("Jen", now + 10, new[] { "ui.set_policies:area" }));
            Assert.True(t.IsTouched("Jen", now + 10, new[] { "ui.set_policies:hostility" }));
            Assert.False(t.IsTouched("Jen", now + 10, new[] { "ui.set_policies:food" }));
            // different cooldowns per reason: the short one stops matching its scope while the id stays touched unscoped
            t.Touch("Bob", "ui.set_policies:food", now, cooldownTicks: 50);
            t.Touch("Bob", "ui.draft", now, cooldownTicks: 500);
            Assert.True(t.IsTouched("Bob", now + 10, new[] { "ui.set_policies:food" }));
            Assert.False(t.IsTouched("Bob", now + 60, new[] { "ui.set_policies:food" }));
            Assert.True(t.IsTouched("Bob", now + 60));
            Assert.True(t.IsTouched("Bob", now + 60, new[] { "ui.draft" }));
            // Active lists only the matching reason, one row per live (id, reason)
            var active = t.Active(now + 60, new[] { "ui.draft" });
            Assert.Single(active);
            Assert.Equal(("Bob", "ui.draft", 440), active[0]);
            Assert.Equal(4, t.Active(now + 10).Count);   // Jen×2 + Bob×2 live rows
            // encode/decode round-trips every reason and its own until; a pass-1 plain reason maps to the id's until
            var back = TouchTable.Decode(new Dictionary<string, int>(t.Until), t.EncodeReasons());
            Assert.Equal(t.Active(now + 10), back.Active(now + 10));
            var legacy = TouchTable.Decode(new Dictionary<string, int> { ["Old"] = 900 }, new Dictionary<string, string> { ["Old"] = "ui.attack" });
            Assert.True(legacy.IsTouched("Old", 800, new[] { "ui.attack" }));
            Assert.False(legacy.IsTouched("Old", 900));
        }

        [Fact]
        public void CombatScopes_OrderAndPressTouches()
        {
            var t = new TouchTable();
            t.Touch("A", "ui.order.drafted:Attack Human2331", 0);
            t.Touch("B", "ui.press:Hold fire", 0);
            t.Touch("C", "ui.order:Prioritize hauling steel", 0);
            t.Touch("D", "ui.press:draft", 0);
            t.Touch("E", "ui.job:AttackStatic", 0);
            Assert.True(t.IsTouched("A", 10, CombatScopes.Touch));
            Assert.False(t.IsTouched("B", 10, CombatScopes.Touch));   // gizmo toggles like Hold fire do not steal the pawn
            Assert.False(t.IsTouched("C", 10, CombatScopes.Touch));   // an undrafted right-click order is not military control
            Assert.True(t.IsTouched("D", 10, CombatScopes.Touch));
            Assert.True(t.IsTouched("E", 10, CombatScopes.Touch));
            // release policy: only control touches keep a pawn drafted; a ui.job touch lets the order undraft it
            Assert.True(CombatScopes.KeepDrafted(new[] { "ui.job:TendPatient", "ui.attack" }));
            Assert.False(CombatScopes.KeepDrafted(new[] { "ui.job:TendPatient" }));
            Assert.False(CombatScopes.KeepDrafted(null));
            // area policy: restore unless the director moved the pawn elsewhere or chose an area explicitly
            Assert.Equal(AreaRelease.Restore, CombatScopes.AreaAction(new[] { "ui.job:Rescue" }, stillHome: true));
            Assert.Equal(AreaRelease.Restore, CombatScopes.AreaAction(null, stillHome: true));
            Assert.Equal(AreaRelease.Drop, CombatScopes.AreaAction(null, stillHome: false));
            Assert.Equal(AreaRelease.Drop, CombatScopes.AreaAction(new[] { "ui.set_policies:area" }, stillHome: true));
        }

        [Fact]
        public void CombatEligibility_NeedsGuardIsHysteretic()
        {
            Assert.Null(CombatEligibility.WhyNot(new DraftFlags { Food = 0.2f, Rest = 0.2f }));
            Assert.Equal(CombatEligibility.NeedsReason, CombatEligibility.WhyNot(new DraftFlags { Food = 0.1f }));
            Assert.Equal(CombatEligibility.NeedsReason, CombatEligibility.WhyNot(new DraftFlags { Rest = 0.14f }));
            Assert.Null(CombatEligibility.WhyNot(new DraftFlags { Food = 0.1f, HostileNear = true }));           // no relief with a hostile close
            Assert.Equal(CombatEligibility.NeedsReason, CombatEligibility.WhyNot(new DraftFlags { Food = 0.4f, Resting = true }));   // stays relieved until recovered
            Assert.Equal(CombatEligibility.NeedsReason, CombatEligibility.WhyNot(new DraftFlags { Food = 0.4f, Resting = true, HostileNear = true }));
            Assert.Null(CombatEligibility.WhyNot(new DraftFlags { Food = 0.5f, Rest = 0.5f, Resting = true }));
            Assert.True(CombatTimers.IsProlonged(0, 30000));
            Assert.False(CombatTimers.IsProlonged(-1, 99999));
        }

        [Fact]
        public void ThreatRules_WatchVersusEngage()
        {
            Assert.True(ThreatRules.Engage(new HostileFacts { IsPawn = true, HasLord = true, Siege = true, InHome = true, DistToRally = 100 }));
            Assert.True(ThreatRules.Engage(new HostileFacts { IsPawn = true, HasLord = true, Siege = true, DistToRally = 40 }));
            Assert.False(ThreatRules.Engage(new HostileFacts { IsPawn = true, HasLord = true, Siege = true, DistToRally = 60 }));
            Assert.True(ThreatRules.Engage(new HostileFacts { IsPawn = true, HasLord = true, Assaulting = true, DistToRally = 200 }));
            Assert.False(ThreatRules.Engage(new HostileFacts { IsPawn = true, HasLord = true, DistToRally = 200 }));      // staging / sleeping
            Assert.False(ThreatRules.Engage(new HostileFacts { IsPawn = true, Manhunter = true, DistToRally = 80 }));
            Assert.True(ThreatRules.Engage(new HostileFacts { IsPawn = true, Manhunter = true, DistToRally = 80, ColonistOutside = true }));
            Assert.False(ThreatRules.Engage(new HostileFacts { IsPawn = false, DistToRally = 90 }));                        // far turret
            Assert.True(ThreatRules.Engage(new HostileFacts { IsPawn = false, DistToRally = 30 }));
            Assert.False(ThreatRules.Engage(new HostileFacts { IsPawn = true, DistToRally = 90 }));                         // lone hostile far away
            Assert.False(ThreatRules.Engage(null!));
            Assert.Equal("siege", ThreatRules.Label(new HostileFacts { IsPawn = true, HasLord = true, Siege = true }));
            Assert.Equal("staging", ThreatRules.Label(new HostileFacts { IsPawn = true, HasLord = true }));
            Assert.Equal("manhunter", ThreatRules.Label(new HostileFacts { IsPawn = true, Manhunter = true }));
            Assert.Equal("structure", ThreatRules.Label(new HostileFacts()));
        }

        [Fact]
        public void RescueRules_OnlyRescueOrTendTouchesMeanThePatientIsHandled()
        {
            Assert.True(RescueRules.PatientHandled("ui.job:Rescue"));
            Assert.True(RescueRules.PatientHandled("ui.job:TendPatient"));
            Assert.True(RescueRules.PatientHandled("ui.order:Rescue Manu"));
            Assert.True(RescueRules.PatientHandled("ui.order:tend Manu"));
            Assert.True(RescueRules.PatientHandled("ui.order.drafted:Rescue Manu"));
            Assert.False(RescueRules.PatientHandled("ui.attack"));
            Assert.False(RescueRules.PatientHandled("ui.draft"));
            Assert.False(RescueRules.PatientHandled("ui.order:Arrest Manu"));
            Assert.False(RescueRules.PatientHandled("ui.job:Ingest"));
            Assert.False(RescueRules.PatientHandled((string?)null));
            Assert.True(RescueRules.PatientHandled(new[] { "ui.attack", "ui.job:Rescue" }));
            Assert.False(RescueRules.PatientHandled(new[] { "ui.attack", "ui.goto" }));
        }

        [Fact]
        public void TouchTable_ScopesMatchRpcOrPrefix()
        {
            var t = new TouchTable();
            t.Touch("Human1", "ui.set_policies:food", 0);
            t.Touch("Human2", "ui.set_policies:area", 0);
            t.Touch("Bed1", "ui.press:Medical", 0);
            var combat = new[] { "ui.draft", "ui.goto", "ui.set_policies:area" };
            Assert.False(t.IsTouched("Human1", 10, combat));
            Assert.True(t.IsTouched("Human2", 10, combat));
            Assert.True(t.IsTouched("Bed1", 10, new[] { "ui.press" }));
            Assert.True(t.IsTouched("Human1", 10));                       // no scopes = any touch
            Assert.True(t.IsTouched("Human1", 10, new string[0]));       // empty scopes = any touch
            var active = t.Active(10, combat);
            Assert.Single(active);
            Assert.Equal("Human2", active[0].id);
            Assert.Equal(2490, active[0].ticksLeft);
            Assert.Equal(3, t.Active(10).Count);
        }

        [Fact]
        public void Schedule_OffsetsAreStable_InRange_AndStaggered()
        {
            int a = OrderSchedule.Offset("combat", 60), b = OrderSchedule.Offset("rescue", 60), c = OrderSchedule.Offset("fire", 60);
            Assert.Equal(a, OrderSchedule.Offset("combat", 60));
            Assert.InRange(a, 0, 59); Assert.InRange(b, 0, 59); Assert.InRange(c, 0, 59);
            Assert.True(a != b || b != c, "three ids should not all share one offset");
            Assert.Equal(0, OrderSchedule.Offset("anything", 1));
        }

        [Fact]
        public void Schedule_DueExactlyOncePerInterval()
        {
            int interval = 300, offset = OrderSchedule.Offset("rescue", interval);
            int hits = 0, firstHit = -1;
            for (int tick = 0; tick < interval * 3; tick++)
                if (OrderSchedule.Due(tick, interval, offset)) { hits++; if (firstHit < 0) firstHit = tick; }
            Assert.Equal(3, hits);
            Assert.Equal(offset, firstHit);
            Assert.True(OrderSchedule.IntervalElapsed(10, -1, 300));
            Assert.False(OrderSchedule.IntervalElapsed(299, 0, 300));
            Assert.True(OrderSchedule.IntervalElapsed(300, 0, 300));
        }

        [Fact]
        public void CombatTimers_HoldAndRelease()
        {
            Assert.True(CombatTimers.ShouldHold(-1, 5));
            Assert.False(CombatTimers.ShouldHold(100, 349));
            Assert.True(CombatTimers.ShouldHold(100, 350));
            Assert.False(CombatTimers.ShouldRelease(-1, 10000));
            Assert.False(CombatTimers.ShouldRelease(1000, 1599));
            Assert.True(CombatTimers.ShouldRelease(1000, 1600));
        }

        [Fact]
        public void FireClusters_BucketAndDedupe()
        {
            Assert.Equal(FireClusters.Key(3, 5), FireClusters.Key(7, 0));
            Assert.NotEqual(FireClusters.Key(7, 7), FireClusters.Key(8, 8));
            Assert.Equal("-1,-1", FireClusters.Key(-1, -8));
            var seen = new Dictionary<string, int>();
            Assert.Equal(new[] { "0,0", "1,1" }, FireClusters.Report(seen, new[] { "0,0", "1,1", "0,0" }, 100));
            Assert.Empty(FireClusters.Report(seen, new[] { "0,0" }, 2599));
            Assert.Equal(new[] { "0,0" }, FireClusters.Report(seen, new[] { "0,0" }, 2600));   // cooldown elapsed → reported again
            Assert.False(seen.ContainsKey("1,1"));                                              // expired records are dropped
        }
    }
}
