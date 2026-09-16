using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RimBridge.Server;
using RimWorld;
using Verse;

namespace RimBridge.Engine
{
    /// <summary>
    /// Location grammar accepted wherever a cell or rect is expected:
    ///   [x, z]                       absolute cell            [x, z, w, h]           absolute rect
    ///   "Campfire39256"              a thing's position       "@Gamble"              a pawn's position
    ///   "bedroom2"                   a named anchor (cell = centre, rect = the anchor rect)
    ///   "bedroom2:NW"                interior corner/edge of an anchor or Room:<id> (NW NE SW SE N S E W C)
    ///   "bedroom2:inset:1"           anchor rect shrunk by n   "bedroom2:extend:E:4"  rect of n cells beyond the anchor's east edge
    ///   "Room:12"                    a room (bounding rect / centre)
    ///   "<ref> +E2 +N1" or "<ref>+E2N1"   offsets in cells (N S E W)
    /// </summary>
    public static class Locate
    {
        static readonly Regex Offset = new Regex(@"\+?\s*([NSEW])\s*(\d+)", RegexOptions.IgnoreCase);

        public static IntVec3 Cell(JToken? t, Map map, string what = "cell")
        {
            if (t == null || t.Type == JTokenType.Null) throw new RpcError($"missing {what}");
            if (t is JArray a && a.Count >= 2 && a.All(x => x.Type == JTokenType.Integer)) return new IntVec3((int)a[0]!, 0, (int)a[a.Count - 1]!);
            if (t is JObject o && o["x"] != null && o["z"] != null) return new IntVec3((int)o["x"]!, 0, (int)o["z"]!);
            string s = t.ToString().Trim();
            var parts = s.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int px) && int.TryParse(parts[1].Trim(), out int pz)) return new IntVec3(px, 0, pz);
            var (rect, isRect, rest) = ResolveRef(s, map, what);
            var c = isRect ? rect.CenterCell : rect.minX == rect.maxX && rect.minZ == rect.maxZ ? new IntVec3(rect.minX, 0, rect.minZ) : rect.CenterCell;
            return ApplyOffsets(c, rest);
        }

        public static CellRect Rect(JToken? t, Map map, string what = "rect")
        {
            if (t == null || t.Type == JTokenType.Null) throw new RpcError($"missing {what}");
            if (t is JArray a && a.Count == 4) return new CellRect((int)a[0]!, (int)a[1]!, (int)a[2]!, (int)a[3]!);
            if (t is JArray a2 && a2.Count == 2 && a2[0] is JArray) return CellRect.FromLimits(Cell(a2[0], map), Cell(a2[1], map));
            if (t is JObject o && o["min"] != null) return CellRect.FromLimits(Cell(o["min"], map), Cell(o["max"], map));
            string s = t.ToString().Trim();
            var (rect, _, rest) = ResolveRef(s, map, what);
            if (!string.IsNullOrWhiteSpace(rest))
            {
                var d = ApplyOffsets(IntVec3.Zero, rest);
                rect = rect.MovedBy(new IntVec2(d.x, d.z));
            }
            return rect;
        }

        /// <summary>Resolve "name[:modifier...]" into a rect (single-cell rect for points). Returns the leftover offset text.</summary>
        static (CellRect rect, bool isRect, string rest) ResolveRef(string s, Map map, string what)
        {
            // split off trailing offsets like "+E2 +N1"
            string body = s; string rest = "";
            int plus = s.IndexOf('+');
            if (plus > 0) { body = s.Substring(0, plus).Trim(); rest = s.Substring(plus); }
            var segs = body.Split(':');
            string head = segs[0].Trim();
            CellRect rect; bool isRect;
            if (head.StartsWith("@"))
            {
                var pawn = Lookup.Pawn(head.Substring(1));
                rect = CellRect.SingleCell(pawn.PositionHeld); isRect = false;
            }
            else if (head.Equals("Room", StringComparison.OrdinalIgnoreCase) && segs.Length > 1)
            {
                var room = Lookup.RoomOrNull(segs[1].Trim()) ?? throw new RpcError($"no room {segs[1]}");
                rect = RoomRect(room); isRect = true;
                segs = new[] { head + ":" + segs[1] }.Concat(segs.Skip(2)).ToArray();
            }
            else if (head.Equals("home", StringComparison.OrdinalIgnoreCase))
            {
                rect = CellRect.SingleCell(State.Snapshot.HomeCenter(map)); isRect = false;
            }
            else if (AnchorComponent.TryGet(head, out var anchor))
            {
                rect = anchor; isRect = anchor.Width > 1 || anchor.Height > 1;
            }
            else
            {
                var thing = Lookup.ThingOrNull(head) ?? Lookup.PawnOrNull(head);
                if (thing == null) throw new RpcError($"cannot resolve {what} '{head}': not a cell, thing id, @pawn, anchor ({string.Join(", ", AnchorComponent.Names().Take(12))}) or Room:<id>");
                rect = thing.OccupiedRect(); isRect = thing.def.size.x > 1 || thing.def.size.z > 1;
            }
            // modifiers
            for (int i = 1; i < segs.Length; i++)
            {
                string m = segs[i].Trim();
                switch (m.ToUpperInvariant())
                {
                    case "NW": rect = CellRect.SingleCell(new IntVec3(rect.minX, 0, rect.maxZ)); isRect = false; break;
                    case "NE": rect = CellRect.SingleCell(new IntVec3(rect.maxX, 0, rect.maxZ)); isRect = false; break;
                    case "SW": rect = CellRect.SingleCell(new IntVec3(rect.minX, 0, rect.minZ)); isRect = false; break;
                    case "SE": rect = CellRect.SingleCell(new IntVec3(rect.maxX, 0, rect.minZ)); isRect = false; break;
                    case "N": rect = CellRect.SingleCell(new IntVec3(rect.CenterCell.x, 0, rect.maxZ)); isRect = false; break;
                    case "S": rect = CellRect.SingleCell(new IntVec3(rect.CenterCell.x, 0, rect.minZ)); isRect = false; break;
                    case "E": rect = CellRect.SingleCell(new IntVec3(rect.maxX, 0, rect.CenterCell.z)); isRect = false; break;
                    case "W": rect = CellRect.SingleCell(new IntVec3(rect.minX, 0, rect.CenterCell.z)); isRect = false; break;
                    case "C": case "CENTER": case "CENTRE": rect = CellRect.SingleCell(rect.CenterCell); isRect = false; break;
                    case "INSET":
                    {
                        int n = i + 1 < segs.Length && int.TryParse(segs[i + 1], out var k) ? k : 1; i++;
                        rect = rect.ContractedBy(n); break;
                    }
                    case "EXPAND":
                    {
                        int n = i + 1 < segs.Length && int.TryParse(segs[i + 1], out var k) ? k : 1; i++;
                        rect = rect.ExpandedBy(n); break;
                    }
                    case "EXTEND":
                    {
                        // rect:extend:E:4 -> the 4-cell-wide strip beyond the east edge, same height (for extending rooms)
                        string dir = i + 1 < segs.Length ? segs[i + 1].Trim().ToUpperInvariant() : "E";
                        int n = i + 2 < segs.Length && int.TryParse(segs[i + 2], out var k) ? k : 1; i += 2;
                        rect = dir switch
                        {
                            "E" => new CellRect(rect.maxX + 1, rect.minZ, n, rect.Height),
                            "W" => new CellRect(rect.minX - n, rect.minZ, n, rect.Height),
                            "N" => new CellRect(rect.minX, rect.maxZ + 1, rect.Width, n),
                            "S" => new CellRect(rect.minX, rect.minZ - n, rect.Width, n),
                            _ => throw new RpcError("extend direction must be N/S/E/W"),
                        };
                        break;
                    }
                    default: throw new RpcError($"unknown location modifier '{m}' (use NW NE SW SE N S E W C inset:n expand:n extend:DIR:n)");
                }
            }
            return (rect, isRect, rest);
        }

        static IntVec3 ApplyOffsets(IntVec3 c, string rest)
        {
            foreach (Match m in Offset.Matches(rest ?? ""))
            {
                int n = int.Parse(m.Groups[2].Value);
                switch (char.ToUpperInvariant(m.Groups[1].Value[0]))
                {
                    case 'N': c += new IntVec3(0, 0, n); break;
                    case 'S': c += new IntVec3(0, 0, -n); break;
                    case 'E': c += new IntVec3(n, 0, 0); break;
                    case 'W': c += new IntVec3(-n, 0, 0); break;
                }
            }
            return c;
        }

        public static CellRect RoomRect(Room room)
        {
            int minX = int.MaxValue, minZ = int.MaxValue, maxX = int.MinValue, maxZ = int.MinValue;
            foreach (var c in room.Cells) { if (c.x < minX) minX = c.x; if (c.x > maxX) maxX = c.x; if (c.z < minZ) minZ = c.z; if (c.z > maxZ) maxZ = c.z; }
            if (minX == int.MaxValue) return CellRect.Empty;
            return CellRect.FromLimits(minX, minZ, maxX, maxZ);
        }

        public static string Describe(IntVec3 c, Map map)
        {
            // nearest anchor for human-friendly descriptions
            var (name, rect) = AnchorComponent.Nearest(c);
            if (name == null) return $"[{c.x},{c.z}]";
            var d = c - rect.CenterCell;
            string dir = (Math.Abs(d.x) > Math.Abs(d.z) ? (d.x > 0 ? "E" : "W") : (d.z > 0 ? "N" : "S"));
            int dist = Math.Max(Math.Abs(d.x), Math.Abs(d.z));
            return rect.Contains(c) ? $"[{c.x},{c.z}] in {name}" : $"[{c.x},{c.z}] {dist}{dir} of {name}";
        }
    }

    /// <summary>Named anchors, saved with the game. The model names places; every tool then accepts the names.</summary>
    public class AnchorComponent : GameComponent
    {
        public static AnchorComponent? Instance;
        private Dictionary<string, CellRect> anchors = new Dictionary<string, CellRect>(StringComparer.OrdinalIgnoreCase);
        private List<string>? keys; private List<CellRect>? vals;

        public AnchorComponent(Verse.Game game) { Instance = this; }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref anchors, "anchors", LookMode.Value, LookMode.Value, ref keys, ref vals);
            anchors ??= new Dictionary<string, CellRect>(StringComparer.OrdinalIgnoreCase);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) anchors = new Dictionary<string, CellRect>(anchors, StringComparer.OrdinalIgnoreCase);
        }

        public static bool TryGet(string name, out CellRect rect)
        {
            rect = CellRect.Empty;
            return Instance != null && Instance.anchors.TryGetValue(name, out rect);
        }

        public static IEnumerable<string> Names() => Instance?.anchors.Keys ?? Enumerable.Empty<string>();
        public static IReadOnlyDictionary<string, CellRect> All() => Instance?.anchors ?? new Dictionary<string, CellRect>();

        public static (string? name, CellRect rect) Nearest(IntVec3 c)
        {
            if (Instance == null || Instance.anchors.Count == 0) return (null, CellRect.Empty);
            var best = Instance.anchors.OrderBy(kv => kv.Value.Contains(c) ? -1 : kv.Value.ClosestCellTo(c).DistanceTo(c)).First();
            return (best.Key, best.Value);
        }

        [Rpc("anchor.set", "{name, cell?: location, rect?: location-rect, thing?: id} name a place; every tool then accepts the name ('bedroom2', 'bedroom2:NW', 'bedroom2:extend:E:4', 'bedroom2 +N2'). Saved with the game.")]
        public static JToken Set(JObject p)
        {
            GameCtl.GameControl.RequirePlaying();
            if (Instance == null) throw new RpcError("no game");
            var map = Find.CurrentMap;
            string name = P.Str(p, "name").Trim();
            if (name.Length == 0 || name.Contains(":") || name.Contains("+") || name.StartsWith("@")) throw new RpcError("anchor names cannot contain ':' or '+' or start with '@'");
            CellRect rect;
            if (p["rect"] != null) rect = Locate.Rect(p["rect"], map);
            else if (p["thing"] != null) { var t = Lookup.ThingOrNull(P.Str(p, "thing")) ?? Lookup.Pawn(P.Str(p, "thing")); rect = t.OccupiedRect(); }
            else rect = CellRect.SingleCell(Locate.Cell(p["cell"], map));
            Instance.anchors[name] = rect;
            return new JObject { ["anchor"] = name, ["rect"] = Render.Value(rect, 1) };
        }

        [Rpc("anchor.list", "named anchors with rects, sizes and distance/direction from home")]
        public static JToken List(JObject p)
        {
            GameCtl.GameControl.RequirePlaying();
            var map = Find.CurrentMap; var home = State.Snapshot.HomeCenter(map);
            var arr = new JArray();
            foreach (var kv in All().OrderBy(k => k.Key))
            {
                var d = kv.Value.CenterCell - home;
                arr.Add(new JObject { ["name"] = kv.Key, ["rect"] = Render.Value(kv.Value, 1), ["size"] = $"{kv.Value.Width}x{kv.Value.Height}", ["from_home"] = $"{(int)kv.Value.CenterCell.DistanceTo(home)} cells {(Math.Abs(d.x) > Math.Abs(d.z) ? (d.x > 0 ? "E" : "W") : (d.z > 0 ? "N" : "S"))}" });
            }
            return arr;
        }

        [Rpc("anchor.delete", "{name}")]
        public static JToken Delete(JObject p)
        {
            if (Instance == null) throw new RpcError("no game");
            return Instance.anchors.Remove(P.Str(p, "name")) ? "deleted" : "no such anchor";
        }
    }
}
