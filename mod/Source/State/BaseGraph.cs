using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBridge.Engine;
using RimBridge.Server;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimBridge.State
{
    /// <summary>Object-centric view of the base: rooms, doors, contents, free space, problems — what a player sees, not a grid.</summary>
    public static class BaseGraph
    {
        static bool IsPlayerRoom(Room r, Map map)
        {
            if (r.IsDoorway || r.PsychologicallyOutdoors || r.TouchesMapEdge || r.CellCount > 600 || r.CellCount == 0) return false;
            if (r.Role == null || r.Role == RoomRoleDefOf.None) return r.ContainedAndAdjacentThings.Any(t => t.Faction == Faction.OfPlayer && t.def.category == ThingCategory.Building) && r.CellCount <= 200;
            return true;
        }

        public static JObject Room(Room r, Map map, bool verbose)
        {
            var rect = Locate.RoomRect(r);
            var contents = new Dictionary<string, List<Thing>>();
            int freeInterior = 0, unroofed = 0; float glow = 0;
            var cells = r.Cells.ToList();
            foreach (var c in cells)
            {
                if (!c.Roofed(map)) unroofed++;
                glow += map.glowGrid.GroundGlowAt(c);
                bool blocked = false;
                foreach (var t in c.GetThingList(map))
                {
                    if (t.def.category == ThingCategory.Building || t is Blueprint || t is Frame)
                    {
                        if (t.def.passability != Traversability.Standable || t.def.building?.isEdifice == true || t is Blueprint || t is Frame) blocked = true;
                        var def = ((t as Blueprint)?.def.entityDefToBuild ?? (t as Frame)?.def.entityDefToBuild ?? (BuildableDef)t.def).defName + (t is Blueprint || t is Frame ? " (planned)" : "");
                        if (!contents.TryGetValue(def, out var l)) contents[def] = l = new List<Thing>();
                        if (!l.Contains(t)) l.Add(t);
                    }
                }
                if (!blocked && c.Standable(map)) freeInterior++;
            }
            // doors: border door cells and what they lead to
            var doors = new JArray();
            var seenDoors = new HashSet<Building_Door>();
            foreach (var c in r.BorderCells)
            {
                var d = c.GetEdifice(map) as Building_Door;
                if (d == null || !seenDoors.Add(d)) continue;
                var other = new IntVec3[] { c + IntVec3.North, c + IntVec3.South, c + IntVec3.East, c + IntVec3.West }
                    .Select(n => n.GetRoom(map)).FirstOrDefault(rr => rr != null && rr != r && !rr.IsDoorway);
                string leads = other == null ? "?" : other.PsychologicallyOutdoors || other.TouchesMapEdge ? "outside" : (other.Role?.defName ?? "room") + " #" + other.ID;
                doors.Add(new JObject { ["id"] = d.ThingID, ["cell"] = Snapshot.Cell(c), ["leads_to"] = leads, ["open"] = d.Open, ["forbidden"] = d.IsForbidden(Faction.OfPlayer) });
            }
            var problems = new JArray();
            if (unroofed > 0) problems.Add($"{unroofed} cells unroofed");
            if (doors.Count == 0 && cells.Count > 1) problems.Add("no door");
            if (cells.Count > 0 && glow / cells.Count < 0.3f) problems.Add("dark");
            if (r.Temperature < 5) problems.Add($"cold ({Math.Round(r.Temperature)}C)");
            if (r.Temperature > 30) problems.Add($"hot ({Math.Round(r.Temperature)}C)");
            if (freeInterior == 0 && cells.Count > 1) problems.Add("no free floor");
            var o = new JObject
            {
                ["id"] = r.ID,
                ["ref"] = "Room:" + r.ID,
                ["role"] = r.Role?.defName,
                ["rect"] = Render.Value(rect, 1),
                ["size"] = $"{rect.Width}x{rect.Height} ({cells.Count} cells)",
                ["free_floor"] = freeInterior,
                ["doors"] = doors,
                ["contents"] = new JObject(contents.OrderByDescending(kv => kv.Value.Count).Select(kv => new JProperty(kv.Key, verbose ? new JArray(kv.Value.Select(t => (JToken)ThingBrief(t, map))) : (JToken)kv.Value.Count))),
                ["owners"] = string.Join(",", r.Owners.Select(x => x.LabelShort)),
                ["temp"] = Math.Round(r.Temperature),
                ["impressiveness"] = Math.Round(r.GetStat(RoomStatDefOf.Impressiveness)),
                ["problems"] = problems,
            };
            var (an, arect) = AnchorComponent.Nearest(rect.CenterCell);
            if (an != null && arect.Overlaps(rect)) o["anchor"] = an;
            return o;
        }

        public static JObject ThingBrief(Thing t, Map map)
        {
            var o = new JObject { ["id"] = t.ThingID, ["at"] = Snapshot.Cell(t.Position), ["rot"] = t.Rotation.ToStringWord() };
            var def = (t as Blueprint)?.def.entityDefToBuild as ThingDef ?? (t as Frame)?.def.entityDefToBuild as ThingDef ?? t.def;
            if (def.size.x > 1 || def.size.z > 1) o["size"] = $"{def.size.x}x{def.size.z}";
            if (def.hasInteractionCell) o["interaction_cell"] = Snapshot.Cell(ThingUtility.InteractionCellWhenAt(def, t.Position, t.Rotation, map));
            if (t is Building_Bed bed) o["owner"] = string.Join(",", bed.OwnersForReading.Select(x => x.LabelShort));
            return o;
        }

        [Rpc("state.base", "{verbose?: false} the base as objects: rooms (role, size, free floor, doors and where they lead, contents with ids/interaction cells, problems: unroofed/no door/dark/cold), structures outside rooms, anchors, trapped colonists. Use this instead of grids to reason about the base.")]
        public static JToken Base(JObject p)
        {
            GameCtl.GameControl.RequirePlaying();
            var map = Find.CurrentMap;
            bool verbose = P.Bool(p, "verbose", false);
            var home = Snapshot.HomeCenter(map);
            var rooms = new JArray();
            var inRooms = new HashSet<Thing>();
            foreach (var r in map.regionGrid.AllRooms.Where(r => IsPlayerRoom(r, map)).OrderBy(r => r.Cells.First().DistanceTo(home)).Take(40))
            {
                rooms.Add(Room(r, map, verbose));
                foreach (var c in r.Cells) foreach (var t in c.GetThingList(map)) inRooms.Add(t);
                foreach (var c in r.BorderCells) foreach (var t in c.GetThingList(map)) inRooms.Add(t);
            }
            // player structures not inside any room (walls of unfinished rooms, turrets, traps, outdoor tables...)
            var outside = new Dictionary<string, List<Thing>>();
            foreach (var b in map.listerBuildings.allBuildingsColonist.Concat<Thing>(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame)))
            {
                if (b.Faction != Faction.OfPlayer || inRooms.Contains(b) || b.def.defName.Contains("Conduit")) continue;
                var def = ((b as Blueprint)?.def.entityDefToBuild ?? (b as Frame)?.def.entityDefToBuild ?? (BuildableDef)b.def).defName + (b is Blueprint || b is Frame ? " (planned)" : "");
                if (!outside.TryGetValue(def, out var l)) outside[def] = l = new List<Thing>();
                l.Add(b);
            }
            var outsideJ = new JObject(outside.OrderByDescending(kv => kv.Value.Count).Take(30).Select(kv => new JProperty(kv.Key, kv.Value.Count <= 6 || verbose ? new JArray(kv.Value.Select(t => (JToken)ThingBrief(t, map))) : (JToken)$"{kv.Value.Count} (e.g. {string.Join(", ", kv.Value.Take(3).Select(t => t.ThingID))})")));
            // trapped colonists: cannot reach the home centre
            var trapped = new JArray();
            foreach (var pw in map.mapPawns.FreeColonistsSpawned)
            {
                if (pw.Downed || pw.Dead) continue;
                if (!pw.CanReach(home, PathEndMode.OnCell, Danger.Deadly) && !pw.CanReachMapEdge())
                {
                    var room = pw.GetRoom();
                    trapped.Add(new JObject { ["pawn"] = pw.LabelShort, ["id"] = pw.ThingID, ["at"] = Snapshot.Cell(pw.Position), ["room"] = room != null ? "Room:" + room.ID : null, ["note"] = "cannot reach home or the map edge — walled in?" });
                }
            }
            // furniture that should be indoors but isn't (beds, benches, stoves, tables): the "your barracks is open" signal
            var furnitureOut = outside.Where(kv => !kv.Key.StartsWith("Wall") && !kv.Key.StartsWith("Door") && !kv.Key.Contains("Trap") && !kv.Key.Contains("Turret") && !kv.Key.Contains("Sandbag") && !kv.Key.Contains("Fence") && !kv.Key.Contains("Spot") && !kv.Key.Contains("Pin") && !kv.Key.Contains("Solar") && !kv.Key.Contains("Wind") && !kv.Key.Contains("Geyser") && !kv.Key.Contains("(planned)"))
                .Select(kv => $"{kv.Key} x{kv.Value.Count}").ToList();
            return new JObject
            {
                ["home_center"] = Snapshot.Cell(home),
                ["furniture_not_in_any_room"] = furnitureOut.Count > 0 ? string.Join(", ", furnitureOut) : null,
                ["rooms"] = rooms,
                ["structures_outside_rooms"] = outsideJ,
                ["anchors"] = AnchorComponent.List(new JObject()),
                ["trapped_colonists"] = trapped,
                ["blueprints_pending"] = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count(b => b.Faction == Faction.OfPlayer),
                ["frames_in_progress"] = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count(b => b.Faction == Faction.OfPlayer),
            };
        }
    }
}
