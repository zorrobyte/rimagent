// Written for RimBridge (2026): the blueprints standing order (spec order 7). Replaces the build_stall watcher's
// reason strings: cancels what can never be built, reports what is starved of materials.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBridge.Steward.Stock;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimBridge.Steward.Orders
{
    /// <summary>
    /// Every 2500 ticks: player blueprints/frames that no colonist can reach (for two passes in a row) or that stand on
    /// terrain the building cannot be built on are cancelled (Destroy(Cancel) refunds a frame's materials); blueprints
    /// older than 3 days whose materials are missing from the counted stock are reported, not touched.
    /// </summary>
    public sealed class Order_Blueprints : Order
    {
        public override string Id => "blueprints";
        public override string Label => "Blueprints: cancel the impossible, report the starved";
        public override string Doc =>
            "Every 2500 ticks. Player blueprints and frames are checked: one that no non-downed colonist can reach (PathEndMode.Touch, PassDoors so forbidden doors do not count, Danger.Deadly) " +
            "on two consecutive passes, or whose terrain no longer supports the building (GenConstruct.CanBuildOnTerrain — deep water, etc.), is " +
            "cancelled with Destroy(DestroyMode.Cancel). Blueprints/frames older than 3 days whose remaining cost exceeds the colony's counted " +
            "resources are listed in the summary (and once per day in the ledger) with the missing materials; nothing is done to them. Fogged " +
            "blueprints are ignored. No touch cooldown applies (cancelling is reversible by placing again).";
        public override int IntervalTicks => 2500;
        public override IReadOnlyList<string>? TouchScopes => Array.Empty<string>();

        public override IEnumerable<string> Explain()
        {
            yield return $"cancel: unreachable from every non-downed free colonist (doors passable, forbidden ones too) for {BlueprintRules.UnreachableGraceTicks} ticks (two passes)";
            yield return "cancel: the building's terrain affordance is not met at its cell(s) (GenConstruct.CanBuildOnTerrain)";
            yield return $"report only: older than {BlueprintRules.StaleAfterTicks / 60000} days and the remaining material cost exceeds map.resourceCounter (missing materials listed)";
            yield return "ledger: blueprints_cancelled {n, unreachable, terrain, ids}; blueprints_stale {n, missing} at most once per day";
            yield return "skip: fogged cells; the whole pass when no colonist is up";
        }

        public override OrderReport Run(Map map)
        {
            var g = StewardGame.Current;
            if (g == null) return OrderReport.Idle("no game state");
            int tick = Find.TickManager.TicksGame;
            var report = new OrderReport();
            var things = new List<Thing>();
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)) if (t.Spawned && t.Faction == Faction.OfPlayer && t is Blueprint) things.Add(t);
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)) if (t.Spawned && t.Faction == Faction.OfPlayer && t is Frame) things.Add(t);
            if (things.Count == 0) { g.bpUnreachableSince.Clear(); report.Summary = "no blueprints"; return report; }

            var center = ProductCounter.GetBaseCenter(map);
            var probes = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead && !p.Downed && p.Spawned).OrderBy(p => p.Position.DistanceToSquared(center)).ToList();
            if (probes.Count == 0) { report.Summary = $"{things.Count} blueprint(s); nobody up to judge reachability"; return report; }

            int unreachableNow = 0, cancelledUnreach = 0, cancelledTerrain = 0;
            var cancelledIds = new List<string>();
            var alive = new HashSet<string>();
            var stale = new List<(Thing t, List<(string def, int missing)> missing)>();
            foreach (var t in things)
            {
                if (t.Position.Fogged(map)) continue;
                alive.Add(t.ThingID);
                var ent = t.def.entityDefToBuild;
                var stuff = (t as Blueprint_Build)?.stuffToUse ?? (t as Frame)?.Stuff;

                // terrain
                if (ent != null && !GenConstruct.CanBuildOnTerrain(ent, t.Position, map, t.Rotation, t, stuff))
                {
                    Cancel(t); cancelledTerrain++; cancelledIds.Add(t.ThingID); report.Act(t.ThingID);
                    continue;
                }
                // reachability
                bool reachable = false;
                var tp = TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly);
                foreach (var p in probes) if (map.reachability.CanReach(p.Position, t, PathEndMode.Touch, tp)) { reachable = true; break; }
                if (!reachable)
                {
                    unreachableNow++;
                    if (!g.bpUnreachableSince.TryGetValue(t.ThingID, out int since)) { g.bpUnreachableSince[t.ThingID] = since = tick; }
                    if (BlueprintRules.CancelUnreachable(since, tick))
                    {
                        Cancel(t); cancelledUnreach++; cancelledIds.Add(t.ThingID); report.Act(t.ThingID);
                        g.bpUnreachableSince.Remove(t.ThingID);
                        continue;
                    }
                }
                else g.bpUnreachableSince.Remove(t.ThingID);
                // stale + starved (report only)
                if (BlueprintRules.IsStale(t.spawnedTick, tick))
                {
                    var missing = BlueprintRules.Missing(RemainingCost(t, ent, stuff), def => map.resourceCounter.GetCount(DefDatabase<ThingDef>.GetNamedSilentFail(def)));
                    if (missing.Count > 0) stale.Add((t, missing));
                }
            }
            foreach (var k in g.bpUnreachableSince.Keys.Where(k => !alive.Contains(k)).ToList()) g.bpUnreachableSince.Remove(k);

            int cancelled = cancelledUnreach + cancelledTerrain;
            if (cancelled > 0)
                StewardLedger.Orders("blueprints_cancelled", $"{cancelled} blueprint(s)/frame(s) cancelled ({cancelledUnreach} unreachable, {cancelledTerrain} bad terrain)",
                    new JObject { ["n"] = cancelled, ["unreachable"] = cancelledUnreach, ["terrain"] = cancelledTerrain, ["ids"] = new JArray(cancelledIds.Take(10)) });

            var missingTotals = new Dictionary<string, int>();
            foreach (var (_, m) in stale) foreach (var (def, n) in m) missingTotals[def] = missingTotals.TryGetValue(def, out int v) ? Math.Max(v, n) : n;
            string missingText = string.Join(", ", missingTotals.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} x{kv.Value}"));
            if (stale.Count > 0)
            {
                int day = GenDate.DaysPassed;
                if (g.bpStaleReportedDay != day)
                {
                    g.bpStaleReportedDay = day;
                    var miss = new JObject(); foreach (var kv in missingTotals) miss[kv.Key] = kv.Value;
                    StewardLedger.Orders("blueprints_stale", $"{stale.Count} blueprint(s) older than 3 days starved of materials: {missingText}",
                        new JObject { ["n"] = stale.Count, ["missing"] = miss, ["ids"] = new JArray(stale.Take(10).Select(s => s.t.ThingID)) }, stale[0].t.Position);
                }
            }

            var parts = new List<string> { $"{things.Count} blueprint(s)/frame(s)" };
            if (cancelled > 0) parts.Add($"cancelled {cancelled} ({cancelledUnreach} unreachable, {cancelledTerrain} bad terrain)");
            int pending = unreachableNow - cancelledUnreach;
            if (pending > 0) parts.Add($"{pending} unreachable (cancel next pass if still)");
            if (stale.Count > 0) parts.Add($"{stale.Count} older than 3 days missing materials: {missingText}");
            report.Summary = string.Join(", ", parts);
            return report;
        }

        static void Cancel(Thing t)
        {
            try { t.Destroy(DestroyMode.Cancel); }
            catch (Exception ex) { StewardLog.Warning($"orders: blueprints could not cancel {t.ThingID}: {ex.Message}"); }
        }

        /// <summary>Remaining material cost (a frame's delivered resources subtracted); empty for install blueprints.</summary>
        static IEnumerable<(string def, int need)> RemainingCost(Thing t, BuildableDef? ent, ThingDef? stuff)
        {
            var list = new List<(string, int)>();
            try
            {
                if (t is Frame fr)
                {
                    foreach (var c in fr.TotalMaterialCost())
                        if (c.thingDef != null) list.Add((c.thingDef.defName, c.count - fr.resourceContainer.TotalStackCountOfDef(c.thingDef)));
                }
                else if (t is Blueprint_Build && ent != null)
                {
                    foreach (var c in ent.CostListAdjusted(stuff, false))
                        if (c.thingDef != null) list.Add((c.thingDef.defName, c.count));
                }
            }
            catch (Exception ex) { StewardLog.Warning($"orders: blueprints cost lookup failed for {t.ThingID}: {ex.Message}"); }
            return list;
        }
    }
}
