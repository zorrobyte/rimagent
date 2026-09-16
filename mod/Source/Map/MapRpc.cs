using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBridge.Engine;
using RimBridge.Server;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimBridge.MapView
{
    public static class MapRpc
    {
        static Verse.Map Map() { GameCtl.GameControl.RequirePlaying(); return Find.CurrentMap; }

        [Rpc("map.view", "{x?, z?, w?: 60, h?: 40, layer?: all|terrain|buildings|zones|pawns|items|roof|fog|home, center?: bool} ASCII view of a region (x,z = min corner unless center=true; default centred on home). Max 150x150.")]
        public static JToken View(JObject p)
        {
            var map = Map();
            var home = State.Snapshot.HomeCenter(map);
            int w = Math.Min(P.Int(p, "w", 60), 150), h = Math.Min(P.Int(p, "h", 40), 150);
            bool center = P.Bool(p, "center", p["x"] == null);
            int x = P.Int(p, "x", home.x), z = P.Int(p, "z", home.z);
            var box = center ? CellRect.CenteredOn(new IntVec3(x, 0, z), w, h) : new CellRect(x, z, w, h);
            string layer = P.Str(p, "layer", "all");
            if (!AsciiView.Layers.Contains(layer)) throw new RpcError("layer must be one of " + string.Join("|", AsciiView.Layers));
            box = box.ClipInsideMap(map);
            return new JObject { ["legend"] = AsciiView.Legend, ["box"] = Render.Value(box, 1), ["grid"] = AsciiView.RenderLayer(map, box, layer) };
        }

        /// <summary>Shared renderer for the building camera; marks are drawn as 'X'.</summary>
        public static JObject DetailRender(Verse.Map map, CellRect box, bool roofLayer, HashSet<IntVec3>? marks)
        {
            var p = new JObject { ["x"] = box.CenterCell.x, ["z"] = box.CenterCell.z, ["w"] = box.Width, ["h"] = box.Height, ["roof"] = roofLayer };
            if (marks != null) p["mark"] = new JArray(marks.Select(m => new JArray(m.x, m.z)));
            return (JObject)Detail(p);
        }

        [Rpc("map.detail", "{x?, z?, around?: thingId|pawn (centre on it), w?: 24, h?: 24 (max 60 — enough for a whole base), roof?: false} the BUILDING CAMERA: zoomed ASCII where every column is numbered, each building type gets its own letter (UPPER = built, lower = blueprint/frame), '*' marks interaction spots that must stay clear, '+' doors, '_' stockpile, ',' growing zone, 'i' items, '@' colonists, '!' hostiles, '^' rock, '~' water, '.' open ground. Returns legend + list of things in view with id/rot/size. Use before and after placing anything.")]
        public static JToken Detail(JObject p)
        {
            var map = Map();
            IntVec3 center;
            if (p["around"] != null)
            {
                var t = Lookup.ThingOrNull(P.Str(p, "around")) ?? Lookup.Pawn(P.Str(p, "around"));
                center = t.PositionHeld;
            }
            else
            {
                var home = State.Snapshot.HomeCenter(map);
                center = new IntVec3(P.Int(p, "x", home.x), 0, P.Int(p, "z", home.z));
            }
            int w = Math.Min(P.Int(p, "w", 24), 60), h = Math.Min(P.Int(p, "h", 24), 60);
            bool roofLayer = P.Bool(p, "roof", false);
            var box = CellRect.CenteredOn(center, w, h).ClipInsideMap(map);

            var letters = new Dictionary<string, char>();
            var counts = new Dictionary<string, int>();
            string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";
            char LetterFor(string def)
            {
                if (!letters.TryGetValue(def, out var c)) { c = letters.Count < alphabet.Length ? alphabet[letters.Count] : '?'; letters[def] = c; }
                return c;
            }
            var interaction = new HashSet<IntVec3>();
            var things = new JArray();
            var seen = new HashSet<Thing>();
            foreach (var c in box)
            {
                foreach (var t in c.GetThingList(map))
                {
                    if (seen.Contains(t)) continue;
                    if (t.def.category != ThingCategory.Building && !(t is Blueprint) && !(t is Frame)) continue;
                    if (t.def.building?.isNaturalRock == true || t.def.defName.Contains("Conduit")) continue;
                    seen.Add(t);
                    var def = (t as Blueprint)?.def.entityDefToBuild ?? (t as Frame)?.def.entityDefToBuild ?? (BuildableDef)t.def;
                    counts[def.defName] = counts.TryGetValue(def.defName, out var cnt) ? cnt + 1 : 1;
                    LetterFor(def.defName);
                    if (def is ThingDef td && td.hasInteractionCell)
                        interaction.Add(ThingUtility.InteractionCellWhenAt(td, t.Position, t.Rotation, map));
                    var o = new JObject { ["id"] = t.ThingID, ["def"] = def.defName, ["pos"] = new JArray(t.Position.x, t.Position.z), ["rot"] = t.Rotation.ToStringWord(), ["size"] = new JArray(t.def.size.x, t.def.size.z), ["state"] = t is Blueprint ? "blueprint" : t is Frame ? "frame" : "built" };
                    if (def is ThingDef td2 && td2.hasInteractionCell) o["interaction_cell"] = State.Snapshot.Cell(ThingUtility.InteractionCellWhenAt(td2, t.Position, t.Rotation, map));
                    things.Add(o);
                }
            }
            var marks = new HashSet<IntVec3>();
            if (p["mark"] is JArray mk) foreach (var m in mk) marks.Add(Lookup.Cell(m));
            var colonists = new HashSet<IntVec3>(map.mapPawns.FreeColonistsSpawned.Select(x => x.Position));
            var hostiles = new HashSet<IntVec3>(map.mapPawns.AllPawnsSpawned.Where(x => x.HostileTo(Faction.OfPlayer)).Select(x => x.Position));
            var sb = new System.Text.StringBuilder();
            // rulers: tens digits then units digits, one column per cell
            sb.Append("      "); for (int x = box.minX; x <= box.maxX; x++) sb.Append(x >= 100 ? (x / 100).ToString() : " "); sb.Append('\n');
            sb.Append("      "); for (int x = box.minX; x <= box.maxX; x++) sb.Append((x / 10 % 10).ToString()); sb.Append('\n');
            sb.Append("      "); for (int x = box.minX; x <= box.maxX; x++) sb.Append((x % 10).ToString()); sb.Append('\n');
            for (int z = box.maxZ; z >= box.minZ; z--)
            {
                sb.Append(z.ToString().PadLeft(4)).Append("  ");
                for (int x = box.minX; x <= box.maxX; x++)
                {
                    var c = new IntVec3(x, 0, z);
                    char g;
                    if (marks.Contains(c)) g = 'X';
                    else if (c.Fogged(map)) g = '?';
                    else if (hostiles.Contains(c)) g = '!';
                    else if (colonists.Contains(c)) g = '@';
                    else
                    {
                        Thing? b = null;
                        foreach (var t in c.GetThingList(map))
                        {
                            if (t.def.building?.isNaturalRock == true) { b = t; break; }
                            if ((t.def.category == ThingCategory.Building || t is Blueprint || t is Frame) && !t.def.defName.Contains("Conduit")) { b = t; break; }
                        }
                        if (b != null && b.def.building?.isNaturalRock == true) g = b.def.building.isResourceRock ? 'o' : '^';
                        else if (b != null)
                        {
                            var def = (b as Blueprint)?.def.entityDefToBuild ?? (b as Frame)?.def.entityDefToBuild ?? (BuildableDef)b.def;
                            if (b is Building_Door || (def is ThingDef dd && dd.thingClass == typeof(Building_Door)) || def.defName.Contains("Door")) g = '+';
                            else { g = LetterFor(def.defName); if (b is Blueprint || b is Frame) g = char.ToLowerInvariant(g); }
                        }
                        else if (interaction.Contains(c)) g = '*';
                        else if (roofLayer && c.Roofed(map)) g = c.GetRoof(map).isThickRoof ? 'R' : 'r';
                        else if (c.GetThingList(map).Any(t => t.def.category == ThingCategory.Item)) g = 'i';
                        else if (c.GetZone(map) is Zone_Stockpile) g = '_';
                        else if (c.GetZone(map) is Zone_Growing) g = ',';
                        else if (c.GetTerrain(map).IsWater) g = '~';
                        else if (c.GetTerrain(map).passability == Traversability.Impassable) g = '^';
                        else if (c.GetPlant(map) is Plant pl && pl.def.plant.IsTree) g = 'T';
                        else g = '.';
                    }
                    sb.Append(g);
                }
                sb.Append('\n');
            }
            var legend = new JObject();
            foreach (var kv in letters) legend[kv.Value.ToString()] = kv.Key + " x" + (counts.TryGetValue(kv.Key, out var n) ? n : 0) + " (lowercase = blueprint/frame)";
            legend["*"] = "interaction spot — keep clear"; legend["+"] = "door"; legend["_"] = "stockpile"; legend[","] = "growing zone"; legend["i"] = "item"; legend["@"] = "colonist"; legend["!"] = "hostile"; legend["^"] = "rock"; legend["o"] = "ore"; legend["~"] = "water"; legend["T"] = "tree"; legend["."] = "open";
            if (roofLayer) { legend["r"] = "roofed (constructed)"; legend["R"] = "thick rock roof"; }
            if (marks.Count > 0) legend["X"] = "marked cell";
            var anchorsInView = new JObject();
            foreach (var kv in AnchorComponent.All()) if (kv.Value.Overlaps(box)) anchorsInView[kv.Key] = Render.Value(kv.Value, 1);
            return new JObject { ["box"] = Render.Value(box, 1), ["centre"] = State.Snapshot.Cell(center), ["grid"] = sb.ToString(), ["legend"] = legend, ["anchors_in_view"] = anchorsInView, ["things"] = things, ["tip"] = "x is read down the three header rows (hundreds/tens/units); z is the row label. Cells: [x, z]. Locations also accept 'ThingId +E2', '@Pawn', anchors ('bedroom2:NW') and 'Room:<id>'." };
        }

        [Rpc("map.overview", "{blocks?: 50} coarse whole-map picture, one char per block (majority feature)")]
        public static JToken Overview(JObject p)
        {
            var map = Map();
            int blocks = Math.Min(P.Int(p, "blocks", 50), 125);
            int bs = Math.Max(2, map.Size.x / blocks);
            var sb = new System.Text.StringBuilder();
            var hostiles = map.mapPawns.AllPawnsSpawned.Where(x => x.HostileTo(Faction.OfPlayer)).Select(x => x.Position).ToList();
            for (int bz = (map.Size.z - 1) / bs; bz >= 0; bz--)
            {
                sb.Append((bz * bs).ToString().PadLeft(4)).Append(' ');
                for (int bx = 0; bx <= (map.Size.x - 1) / bs; bx++)
                {
                    int rock = 0, water = 0, trees = 0, fertile = 0, homeC = 0, build = 0, ore = 0, fog = 0, bp = 0, n = 0;
                    bool hostile = false;
                    for (int x = bx * bs; x < Math.Min((bx + 1) * bs, map.Size.x); x++)
                    for (int z = bz * bs; z < Math.Min((bz + 1) * bs, map.Size.z); z++)
                    {
                        var c = new IntVec3(x, 0, z); n++;
                        if (c.Fogged(map)) { fog++; continue; }
                        var ed = c.GetEdifice(map);
                        if (ed != null)
                        {
                            if (ed.def.building?.isResourceRock == true) ore++;
                            else if (ed.def.building?.isNaturalRock == true) rock++;
                            else if (ed.Faction == Faction.OfPlayer) build++;
                        }
                        else if (c.GetThingList(map).Any(t => t is Blueprint || t is Frame)) bp++;
                        var terr = c.GetTerrain(map);
                        if (terr.IsWater) water++;
                        else if (terr.fertility >= 1f) fertile++;
                        var pl = c.GetPlant(map);
                        if (pl != null && pl.def.plant.IsTree) trees++;
                        if (map.areaManager.Home[c]) homeC++;
                    }
                    if (hostiles.Any(hp => hp.x / bs == bx && hp.z / bs == bz)) hostile = true;
                    char g = '.';
                    if (fog > n * 0.6) g = '?';
                    else if (hostile) g = '!';
                    else if (bp > 0) g = 'p';
                    else if (build > 0) g = 'B';
                    else if (homeC > n * 0.3) g = 'H';
                    else if (ore > 0) g = 'o';
                    else if (rock > n * 0.5) g = '^';
                    else if (water > n * 0.4) g = '~';
                    else if (trees > n * 0.25) g = 'T';
                    else if (fertile > n * 0.4) g = 'f';
                    sb.Append(g);
                }
                sb.Append('\n');
            }
            return new JObject { ["block_size"] = bs, ["legend"] = "? fog | ! hostile | p blueprints | B our buildings | H home area | o ore | ^ rock | ~ water | T trees | f fertile | . open", ["grid"] = sb.ToString(), ["home_center"] = State.Snapshot.Cell(State.Snapshot.HomeCenter(map)) };
        }

        [Rpc("map.find", "{def?: defName, category?: ThingCategoryDef, group?: ThingRequestGroup, kind?: resource_rock|tree|harvestable|corpse|chunk|animal|item|building|blueprint, near?: [x,z], radius?: n, reachable_from?: pawn, forbidden?: bool, faction?: player|hostile|none|any, limit?: 50} find things; sorted by distance from 'near' (default home)")]
        public static JToken FindThings(JObject p)
        {
            var map = Map();
            var near = p["near"] != null ? Lookup.Cell(p["near"]) : State.Snapshot.HomeCenter(map);
            float radius = P.Float(p, "radius", 9999f);
            int limit = Math.Min(P.Int(p, "limit", 50), 500);
            string? def = P.OptStr(p, "def"), cat = P.OptStr(p, "category"), group = P.OptStr(p, "group"), kind = P.OptStr(p, "kind"), faction = P.OptStr(p, "faction");
            bool? forbidden = p["forbidden"] != null ? P.Bool(p, "forbidden", false) : (bool?)null;
            Pawn? reach = p["reachable_from"] != null ? Lookup.Pawn(P.Str(p, "reachable_from")) : null;

            IEnumerable<Thing> src;
            if (def != null) src = map.listerThings.ThingsOfDef(Lookup.Def<ThingDef>(def));
            else if (group != null)
            {
                if (!Enum.TryParse<ThingRequestGroup>(group, true, out var g)) throw new RpcError("unknown group; see engine.get Type:Verse.ThingRequestGroup");
                src = map.listerThings.ThingsInGroup(g);
            }
            else if (kind == "resource_rock") src = map.listerThings.AllThings.Where(t => t.def.building?.isResourceRock == true);
            else if (kind == "tree") src = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).Where(t => t.def.plant.IsTree);
            else if (kind == "harvestable") src = map.listerThings.ThingsInGroup(ThingRequestGroup.HarvestablePlant).Where(t => t is Plant pl && pl.HarvestableNow);
            else if (kind == "corpse") src = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
            else if (kind == "chunk") src = map.listerThings.ThingsInGroup(ThingRequestGroup.Chunk);
            else if (kind == "animal") src = map.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps.Animal);
            else if (kind == "item") src = map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item);
            else if (kind == "building") src = map.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Building && t.def.building?.isNaturalRock != true);
            else if (kind == "blueprint") src = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame));
            else if (cat != null) src = map.listerThings.AllThings;
            else throw new RpcError("give one of def, category, group, kind");

            ThingCategoryDef? cdef = cat != null ? Lookup.Def<ThingCategoryDef>(cat) : null;
            var results = new List<(Thing t, float d)>();
            foreach (var t in src)
            {
                if (!t.Spawned) continue;
                if (cdef != null && !(t.def.thingCategories?.Any(c => c == cdef || c.Parents.Contains(cdef)) ?? false)) continue;
                if (forbidden.HasValue && t.IsForbidden(Faction.OfPlayer) != forbidden.Value) continue;
                if (faction == "player" && t.Faction != Faction.OfPlayer) continue;
                if (faction == "hostile" && !t.HostileTo(Faction.OfPlayer)) continue;
                if (faction == "none" && t.Faction != null) continue;
                float d = t.Position.DistanceTo(near);
                if (d > radius) continue;
                if (t.Position.Fogged(map)) continue;
                results.Add((t, d));
            }
            var arr = new JArray();
            foreach (var (t, d) in results.OrderBy(r => r.d))
            {
                if (reach != null && !reach.CanReach(t, PathEndMode.Touch, Danger.Some)) continue;
                var o = t is Pawn pw ? Render.PawnHandle(pw) : Render.ThingHandle(t);
                o["dist"] = (int)d;
                if (t.IsForbidden(Faction.OfPlayer)) o["forbidden"] = true;
                if (t is Plant pl2) { o["growth"] = Math.Round(pl2.Growth, 2); o["harvestable"] = pl2.HarvestableNow; }
                if (t.def.building?.isResourceRock == true) o["yields"] = t.def.building.mineableThing?.defName + " x" + t.def.building.mineableYield;
                if (t is Corpse cp) { o["rotting"] = cp.GetRotStage().ToString(); o["of"] = cp.InnerPawn?.kindDef?.defName; }
                arr.Add(o);
                if (arr.Count >= limit) break;
            }
            return new JObject { ["count"] = results.Count, ["near"] = State.Snapshot.Cell(near), ["things"] = arr };
        }

        [Rpc("map.cell", "{cell: [x,z]} everything at a cell: terrain, fertility, roof, zone, room, things, fogged, walkable")]
        public static JToken Cell(JObject p)
        {
            var map = Map();
            var c = Lookup.Cell(p["cell"]);
            if (!c.InBounds(map)) throw new RpcError("cell out of bounds");
            var o = new JObject { ["cell"] = State.Snapshot.Cell(c), ["fogged"] = c.Fogged(map) };
            if (c.Fogged(map)) return o;
            var terr = c.GetTerrain(map);
            o["terrain"] = terr.defName; o["fertility"] = terr.fertility; o["walkable"] = c.Walkable(map); o["standable"] = c.Standable(map);
            o["roof"] = c.GetRoof(map)?.defName; o["zone"] = c.GetZone(map)?.label; o["home"] = map.areaManager.Home[c];
            var room = c.GetRoom(map); if (room != null) o["room"] = Render.Value(room, 0);
            o["temperature"] = Math.Round(GenTemperature.GetTemperatureForCell(c, map));
            o["light"] = Math.Round(map.glowGrid.GroundGlowAt(c), 2);
            o["things"] = new JArray(c.GetThingList(map).Select(t => t is Pawn pw ? (JToken)Render.PawnHandle(pw) : Render.ThingHandle(t)));
            o["designations"] = new JArray(map.designationManager.AllDesignationsAt(c).Select(d => d.def.defName));
            return o;
        }

        [Rpc("map.reachable", "{pawn, target: [x,z]|thingId, danger?: Some|Deadly|None} can the pawn reach the target?")]
        public static JToken Reachable(JObject p)
        {
            Map();
            var pawn = Lookup.Pawn(P.Str(p, "pawn"));
            var target = (LocalTargetInfo)Coerce.To(p["target"], typeof(LocalTargetInfo))!;
            var danger = (Danger)Enum.Parse(typeof(Danger), P.Str(p, "danger", "Some"), true);
            return new JObject { ["reachable"] = pawn.CanReach(target, PathEndMode.Touch, danger), ["reachable_deadly"] = pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly) };
        }

        [Rpc("map.path", "{from: [x,z]|pawn, to: [x,z]|thingId, pawn?} path cost/length between two points (optionally for a specific pawn)")]
        public static JToken Path(JObject p)
        {
            var map = Map();
            Pawn? pawn = p["pawn"] != null ? Lookup.Pawn(P.Str(p, "pawn")) : null;
            IntVec3 from;
            if (p["from"] is JArray) from = Lookup.Cell(p["from"]);
            else { var pw = Lookup.Pawn(P.Str(p, "from")); from = pw.Position; pawn ??= pw; }
            var to = (LocalTargetInfo)Coerce.To(p["to"], typeof(LocalTargetInfo))!;
            var tp = pawn != null ? TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassDoors) : TraverseParms.For(TraverseMode.PassDoors, Danger.Deadly);
            using var path = map.pathFinder.FindPathNow(from, to, tp, null, PathEndMode.Touch);
            if (path == null || !path.Found) return new JObject { ["found"] = false };
            return new JObject { ["found"] = true, ["cost"] = Math.Round(path.TotalCost), ["cells"] = path.NodesLeftCount, ["straight"] = (int)from.DistanceTo(to.Cell) };
        }

        [Rpc("map.open_rects", "{w, h, near?: [x,z], radius?: 40, limit?: 8, allow_trees?: true} find open rectangles of buildable, unroofed-or-roofed standable ground with no buildings/blueprints; returns min corners sorted by distance")]
        public static JToken OpenRects(JObject p)
        {
            var map = Map();
            int w = P.Int(p, "w"), h = P.Int(p, "h");
            var near = p["near"] != null ? Lookup.Cell(p["near"]) : State.Snapshot.HomeCenter(map);
            int radius = P.Int(p, "radius", 40), limit = P.Int(p, "limit", 8);
            bool allowTrees = P.Bool(p, "allow_trees", true);
            var found = new List<(IntVec3 c, float d)>();
            var taken = new List<CellRect>();
            for (int x = near.x - radius; x <= near.x + radius; x += 2)
            for (int z = near.z - radius; z <= near.z + radius; z += 2)
            {
                var rect = new CellRect(x, z, w, h);
                if (!rect.InBounds(map)) continue;
                if (taken.Any(t => t.Overlaps(rect))) continue;
                bool ok = true;
                foreach (var c in rect)
                {
                    if (c.Fogged(map) || !c.Standable(map) || c.GetEdifice(map) != null || c.GetTerrain(map).IsWater || c.GetTerrain(map).passability == Traversability.Impassable || !c.GetTerrain(map).affordances.Contains(TerrainAffordanceDefOf.Light)) { ok = false; break; }
                    if (c.GetZone(map) != null) { ok = false; break; }
                    var things = c.GetThingList(map);
                    if (things.Any(t => t is Blueprint || t is Frame || t.def.category == ThingCategory.Building || (!allowTrees && t is Plant pl && pl.def.plant.IsTree))) { ok = false; break; }
                }
                if (!ok) continue;
                found.Add((new IntVec3(x, 0, z), rect.CenterCell.DistanceTo(near)));
                taken.Add(rect);
            }
            return new JArray(found.OrderBy(f => f.d).Take(limit).Select(f => new JObject { ["at"] = State.Snapshot.Cell(f.c), ["dist"] = (int)f.d, ["w"] = w, ["h"] = h }));
        }

        [Rpc("map.terrain_stats", "counts of fertile soil, water, rock, ore (by type) etc. within radius of home")]
        public static JToken TerrainStats(JObject p)
        {
            var map = Map();
            var near = p["near"] != null ? Lookup.Cell(p["near"]) : State.Snapshot.HomeCenter(map);
            int radius = P.Int(p, "radius", 50);
            var ore = new Dictionary<string, int>();
            int fertile = 0, rich = 0, water = 0, rock = 0, trees = 0, fog = 0, n = 0, geysers = 0;
            foreach (var c in GenRadial.RadialCellsAround(near, radius, true))
            {
                if (!c.InBounds(map)) continue; n++;
                if (c.Fogged(map)) { fog++; continue; }
                var ed = c.GetEdifice(map);
                if (ed?.def.building?.isResourceRock == true) { var k = ed.def.building.mineableThing?.defName ?? ed.def.defName; ore[k] = ore.TryGetValue(k, out var v) ? v + 1 : 1; }
                else if (ed?.def.building?.isNaturalRock == true) rock++;
                var t = c.GetTerrain(map);
                if (t.IsWater) water++; else if (t.fertility >= 1.4f) rich++; else if (t.fertility >= 1f) fertile++;
                if (c.GetPlant(map)?.def.plant.IsTree == true) trees++;
                if (c.GetThingList(map).Any(th => th.def.defName == "SteamGeyser")) geysers++;
            }
            return new JObject { ["cells"] = n, ["fogged"] = fog, ["fertile"] = fertile, ["rich_soil"] = rich, ["water"] = water, ["rock"] = rock, ["trees"] = trees, ["geysers"] = geysers, ["ore_cells"] = JObject.FromObject(ore) };
        }
    }
}
