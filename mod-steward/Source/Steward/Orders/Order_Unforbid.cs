// Written for RimBridge (2026): the unforbid standing order. Loop shape adapted from Autopilot's AutoUnforbidReflex
// (MIT, the user's own code); exclusions per STANDING_ORDERS_SPEC order 3.
using System;
using System.Collections.Generic;
using System.Linq;
using RimBridge.Steward.Stock;
using RimWorld;
using Verse;
using Verse.AI.Group;

namespace RimBridge.Steward.Orders
{
    /// <summary>
    /// Forbidden haulables in the home area or within 20 cells of the base centre (and recently landed items — drop-pod
    /// contents — within 40 cells) are unforbidden, except colonist corpses, things the director forbade by hand in the
    /// last hour, things inside hostile-owned structures, caravan cargo and unreachable cells. Skipped during combat.
    /// </summary>
    public sealed class Order_Unforbid : Order
    {
        public override string Id => "unforbid";
        public override string Label => "Unforbid: loot and drops near the base";
        public override string Doc =>
            "Every 600 ticks: forbidden haulable things in the Home area or within 20 cells of the base centre are unforbidden so haulers " +
            "pick them up; items (not chunks, plants or corpses) that spawned within the last 3 days — drop-pod contents — are unforbidden " +
            "within 40 cells. Left alone: corpses of colonists (the corpses order buries them), anything the director forbade/unforbade " +
            "by hand in the last hour (ui.designate), things inside rooms that contain a hostile faction's buildings, caravan cargo " +
            "being loaded, cells no colonist can reach. Nothing runs while the combat order is engaged.";
        public override int IntervalTicks => 600;
        static readonly string[] Scopes = { "ui.designate" };
        public override IReadOnlyList<string>? TouchScopes => Scopes;

        public override IEnumerable<string> Explain()
        {
            yield return $"scope: Home area or within {UnforbidRules.BaseRadius:0} cells of the base centre; recently spawned items (< 3 days) within {UnforbidRules.PodRadius:0} cells (drop pods)";
            yield return "skip: colonist corpses; forbidden/unforbidden by hand in the last hour (ui.designate); inside a room with hostile-faction buildings; caravan cargo; unreachable; fogged";
            yield return "skip: everything while the combat order is engaged";
            yield return "action: SetForbidden(false) — no hauling job is issued, the haul workgivers do the rest";
        }

        public override OrderReport Run(Map map)
        {
            var g = StewardGame.Current;
            if (g == null) return OrderReport.Idle("no game state");
            if (g.CombatEngaged(map)) return OrderReport.Idle("combat engaged; loot stays forbidden");
            int tick = Find.TickManager.TicksGame;
            var home = map.areaManager.Home;
            bool hasHome = home != null && home.TrueCount > 0;
            var center = ProductCounter.GetBaseCenter(map);
            var caravanCargo = CaravanCargo(map);
            var roomHostile = new Dictionary<int, bool>();
            var report = new OrderReport();
            int pods = 0;
            var skipped = new Dictionary<string, int>();

            var haulables = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
            for (int i = 0; i < haulables.Count; i++)
            {
                var t = haulables[i];
                if (!t.Spawned || t is Pawn || !t.IsForbidden(Faction.OfPlayer)) continue;
                if (t.Position.Fogged(map)) continue;
                float dist = t.Position.DistanceTo(center);
                if (dist > UnforbidRules.PodRadius && !(hasHome && home![t.Position])) continue;
                var f = new ForbiddenFacts
                {
                    InHomeArea = hasHome && home![t.Position],
                    DistToBase = dist,
                    ColonistCorpse = t is Corpse c && c.InnerPawn != null && (c.InnerPawn.Faction == Faction.OfPlayer || c.InnerPawn.IsColonist),
                    Touched = Touched(t),
                    CaravanItem = caravanCargo.Contains(t),
                    RecentItem = t.def.category == ThingCategory.Item && !(t is Corpse) && t.spawnedTick >= 0 && tick - t.spawnedTick <= UnforbidRules.RecentItemTicks,
                };
                // cheaper checks first; only evaluate rooms/reachability for things still in the running
                if (UnforbidRules.WhyNot(f) is { } early && early != "hostile structure" && early != "unreachable")
                {
                    if (early != "outside") Count(skipped, early);
                    continue;
                }
                f.HostileStructure = InHostileStructure(map, t.Position, roomHostile);
                f.Reachable = map.reachability.CanReachColony(t.Position);
                var why = UnforbidRules.WhyNot(f);
                if (why != null) { Count(skipped, why); continue; }
                t.SetForbidden(false, false);
                report.Act(t.ThingID);
                if (!f.InHomeArea && dist > UnforbidRules.BaseRadius) pods++;
            }

            if (report.ActingOn == 0 && skipped.Count == 0) { report.Summary = "nothing forbidden near the base"; return report; }
            var parts = new List<string>();
            if (report.ActingOn > 0) parts.Add($"unforbade {report.ActingOn}" + (pods > 0 ? $" ({pods} drop-pod items)" : ""));
            foreach (var kv in skipped.OrderByDescending(k => k.Value)) parts.Add($"{kv.Value} left ({kv.Key})");
            report.Summary = string.Join(", ", parts);
            return report;
        }

        static void Count(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out int n) ? n + 1 : 1;

        /// <summary>Things listed in a forming caravan's transferables.</summary>
        internal static HashSet<Thing> CaravanCargo(Map map)
        {
            var set = new HashSet<Thing>();
            var lords = map.lordManager?.lords;
            if (lords == null) return set;
            foreach (var lord in lords)
            {
                if (!(lord.LordJob is LordJob_FormAndSendCaravan cj) || cj.transferables == null) continue;
                foreach (var tr in cj.transferables)
                    if (tr?.things != null) foreach (var th in tr.things) if (th != null) set.Add(th);
            }
            return set;
        }

        /// <summary>True when the cell lies in an enclosed room that contains a building of a faction hostile to the colony.</summary>
        internal static bool InHostileStructure(Map map, IntVec3 cell, Dictionary<int, bool> cache)
        {
            var room = cell.GetRoom(map);
            if (room == null || room.PsychologicallyOutdoors || room.TouchesMapEdge) return false;
            if (cache.TryGetValue(room.ID, out bool v)) return v;
            bool hostile = false;
            var things = room.ContainedAndAdjacentThings;
            for (int i = 0; i < things.Count; i++)
            {
                if (!(things[i] is Building b) || b.Faction == null || b.Faction == Faction.OfPlayer) continue;
                if (b.Faction.HostileTo(Faction.OfPlayer)) { hostile = true; break; }
            }
            cache[room.ID] = hostile;
            return hostile;
        }
    }
}
