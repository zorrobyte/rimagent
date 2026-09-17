// Written for RimBridge (2026): the steward.orders* RPC surface.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBridge.Engine;
using RimBridge.Server;
using RimWorld;
using Verse;

namespace RimBridge.Steward.Orders
{
    /// <summary>steward.orders / .set / .rally / .explain / .run (main thread), plus the rows steward.status and state.summary embed.</summary>
    public static class OrdersRpc
    {
        const float TicksPerHour = 2500f;

        static Map Map() { GameCtl.GameControl.RequirePlaying(); return Find.CurrentMap; }
        static StewardGame Game() => StewardGame.Current ?? throw new RpcError("no steward game state (not in a game?)");

        static Order Resolve(JObject p)
        {
            string id = P.Str(p, "id");
            return StandingOrders.Get(id) ?? throw new RpcError($"unknown order '{id}'. Known: {StandingOrders.IdList}");
        }

        public static JObject Row(Order o)
        {
            int tick = Find.TickManager?.TicksGame ?? 0;
            int last = StandingOrders.LastRunTick(o);
            return new JObject
            {
                ["id"] = o.Id,
                ["label"] = o.Label,
                ["enabled"] = o.Enabled,
                ["interval_ticks"] = o.IntervalTicks,
                ["last_run_hours_ago"] = last >= 0 ? (JToken)Math.Round((tick - last) / TicksPerHour, 2) : JValue.CreateNull(),
                ["summary"] = StandingOrders.LastSummary(o) is { } s ? (JToken)s : JValue.CreateNull(),
                ["acting_on"] = StandingOrders.ActingOn(o),
            };
        }

        /// <summary>[{id, enabled, summary, acting_on}] for steward.status.</summary>
        public static JArray StatusRows()
        {
            var arr = new JArray();
            foreach (var o in StandingOrders.All)
                arr.Add(new JObject
                {
                    ["id"] = o.Id,
                    ["enabled"] = o.Enabled,
                    ["summary"] = StandingOrders.LastSummary(o) is { } s ? (JToken)s : JValue.CreateNull(),
                    ["acting_on"] = StandingOrders.ActingOn(o),
                });
            return arr;
        }

        /// <summary>[x,z,w,h] or null.</summary>
        public static JToken RallyJson(Map? map)
        {
            var g = StewardGame.Current;
            if (g == null || map == null || !g.HasRally(map)) return JValue.CreateNull();
            return new JArray(g.rallyX, g.rallyZ, g.rallyW, g.rallyH);
        }

        public static JArray ActiveIds() => new JArray(StandingOrders.All.Where(o => o.Enabled).Select(o => o.Id));

        [Rpc("steward.orders", "standing orders (deterministic reflexes in the mod): [{id: combat|rescue|fire|unforbid|corpses|beds|policies|blueprints, label, enabled, interval_ticks, last_run_hours_ago, summary, acting_on: int}]")]
        public static JToken List(JObject p)
        {
            Map();
            return new JArray(StandingOrders.All.Select(Row));
        }

        [Rpc("steward.orders.set", "{id: order id|all, enabled: bool} switch a standing order on/off (persisted per game) -> row, or [rows] for all; switching combat off while it is engaged releases first (row.released: {undrafted, restored, left})")]
        public static JToken Set(JObject p)
        {
            Map();
            Game();
            if (p["enabled"] == null || p["enabled"]!.Type == JTokenType.Null) throw new RpcError("missing param 'enabled'");
            bool enabled = P.Bool(p, "enabled", true);
            string id = P.Str(p, "id");
            if (string.Equals(id, "all", StringComparison.OrdinalIgnoreCase))
            {
                JObject? released = null;
                // combat first so its release still runs the (still enabled) rescue pass
                foreach (var o in StandingOrders.All.OrderBy(o => o is Order_Combat ? 0 : 1))
                {
                    if (!enabled && o is Order_Combat c && c.Enabled) released = ReleaseAll(c);
                    StandingOrders.SetEnabled(o, enabled);
                }
                var rows = new JArray(StandingOrders.All.Select(Row));
                if (released != null) foreach (var r in rows.OfType<JObject>()) if ((string?)r["id"] == "combat") r["released"] = released;
                return rows;
            }
            var order = Resolve(p);
            JObject? rel = null;
            if (!enabled && order is Order_Combat combat && combat.Enabled) rel = ReleaseAll(combat);
            StandingOrders.SetEnabled(order, enabled);
            var row = Row(order);
            if (rel != null) row["released"] = rel;
            return row;
        }

        /// <summary>Releases every map the combat order is engaged on; null when it was engaged nowhere.</summary>
        static JObject? ReleaseAll(Order_Combat combat)
        {
            var g = StewardGame.Current;
            if (g == null || Find.Maps == null) return null;
            int undrafted = 0, restored = 0, maps = 0;
            var left = new JArray();
            foreach (var map in Find.Maps.ToList())
            {
                var r = combat.ReleaseNow(map);
                if (r == null) continue;
                maps++;
                undrafted += r.Value.Undrafted;
                restored += r.Value.Restored;
                foreach (var s in r.Value.Left) left.Add(s);
            }
            if (maps == 0) return null;
            return new JObject { ["undrafted"] = undrafted, ["restored"] = restored, ["left"] = left, ["maps"] = maps };
        }

        [Rpc("steward.orders.release", "{thing?|pawn?: id or name, id?: order id} hand a thing/pawn back to the standing orders now: clears its manual-touch cooldowns and the manual marks (bed/medical/food/temperature/bill) of the given order (all orders when omitted); a pawn the combat order left drafted is undrafted when no fight is on -> {thing, touches_cleared, marks_cleared, undrafted}")]
        public static JToken ReleaseRpc(JObject p)
        {
            var map = Map();
            var g = Game();
            string? raw = P.OptStr(p, "thing") ?? P.OptStr(p, "pawn");
            if (string.IsNullOrEmpty(raw)) throw new RpcError("missing param 'thing' (or 'pawn')");
            var thing = Lookup.ThingOrNull(raw!) ?? Lookup.PawnOrNull(raw!) ?? throw new RpcError($"no thing or pawn '{raw}'");
            IEnumerable<Order>? orders = null;
            if (p["id"] != null && p["id"]!.Type != JTokenType.Null) orders = new[] { Resolve(p) };
            var (touches, marks) = StandingOrders.Release(thing.ThingID, orders);
            bool undrafted = false;
            if (thing is Pawn pawn && pawn.Drafted && pawn.drafter != null)
            {
                var st = g.CombatOrNull(pawn.Map);
                if (st != null && !st.active && st.drafted.Remove(pawn.ThingID)) { pawn.drafter.Drafted = false; undrafted = true; st.assigned.Remove(pawn.ThingID); }
            }
            return new JObject { ["thing"] = thing.ThingID, ["touches_cleared"] = touches, ["marks_cleared"] = marks, ["undrafted"] = undrafted };
        }

        [Rpc("steward.orders.rally", "{rect?: [x,z,w,h], clear?: true} rally rect for the combat order: set it (must contain standable cells; inside walls, with cover, near the hospital), clear it, or read it with {} -> {rect: [x,z,w,h], center: [x,z], cells: standable count} | null")]
        public static JToken Rally(JObject p)
        {
            var map = Map();
            var g = Game();
            if (P.Bool(p, "clear", false))
            {
                g.ClearRally();
                StandingOrders.Get<Order_Combat>()?.ForgetAssignments();
                return JValue.CreateNull();
            }
            if (p["rect"] != null && p["rect"]!.Type != JTokenType.Null)
            {
                var rect = Locate.Rect(p["rect"], map).ClipInsideMap(map);
                if (rect.Width <= 0 || rect.Height <= 0) throw new RpcError("rect is empty or outside the map");
                if (rect.Area > 2500) throw new RpcError("rect too large (max 2500 cells)");
                int standable = rect.Cells.Count(c => c.Standable(map));
                if (standable == 0) throw new RpcError("rect has no standable cell");
                g.SetRally(map, rect);
                StandingOrders.Get<Order_Combat>()?.ForgetAssignments();
            }
            return Current(map, g);
        }

        static JToken Current(Map map, StewardGame g)
        {
            var r = g.RallyRect(map);
            if (!r.HasValue) return JValue.CreateNull();
            var rect = r.Value;
            return new JObject
            {
                ["rect"] = new JArray(rect.minX, rect.minZ, rect.Width, rect.Height),
                ["center"] = new JArray(rect.minX + rect.Width / 2, rect.minZ + rect.Height / 2),
                ["cells"] = rect.Cells.Count(c => c.InBounds(map) && c.Standable(map)),
            };
        }

        [Rpc("steward.orders.explain", "{id} what a standing order does and what it leaves alone -> {id, label, enabled, doc, rules: [string], summary, hands_off: [{thing, reason, until_hours: float|'forever', key?}] (manual-touch cooldowns this order honours plus its own manual marks: bed:/med:/food:/temp:/bill: keys; steward.orders.release clears them)}")]
        public static JToken Explain(JObject p)
        {
            Map();
            var order = Resolve(p);
            var g = StewardGame.Current;
            int now = Find.TickManager?.TicksGame ?? 0;
            var hands = new JArray();
            // an empty scope list means the order honours no touch at all (fire, blueprints): list nothing
            if (order.TouchScopes == null || order.TouchScopes.Count > 0)
                foreach (var (id, reason, ticksLeft) in StandingOrders.HandsOff(order.TouchScopes))
                    hands.Add(new JObject { ["thing"] = id, ["reason"] = reason, ["until_hours"] = Math.Round(ticksLeft / TicksPerHour, 2) });
            if (g != null)
                foreach (var kv in g.owned.ManualUntil)
                {
                    if (kv.Value <= now) continue;
                    var prefix = order.OwnedPrefixes.FirstOrDefault(pre => kv.Key.StartsWith(pre, StringComparison.Ordinal));
                    if (prefix == null) continue;
                    hands.Add(new JObject
                    {
                        ["thing"] = kv.Key.Substring(prefix.Length),
                        ["reason"] = "changed by hand (" + prefix.TrimEnd(':') + ")",
                        ["until_hours"] = kv.Value == OwnedValues.Forever ? (JToken)"forever" : Math.Round((kv.Value - now) / TicksPerHour, 2),
                        ["key"] = kv.Key,
                    });
                }
            return new JObject
            {
                ["id"] = order.Id,
                ["label"] = order.Label,
                ["enabled"] = order.Enabled,
                ["doc"] = order.Doc,
                ["rules"] = new JArray(order.Explain()),
                ["summary"] = StandingOrders.LastSummary(order) is { } s ? (JToken)s : JValue.CreateNull(),
                ["hands_off"] = hands,
            };
        }

        [Rpc("steward.orders.run", "{id} run a standing order now (ignores its interval and enabled flag) -> {ran: bool (it acted on something), summary, acting_on, ids: [thing ids]}")]
        public static JToken Run(JObject p)
        {
            var map = Map();
            var order = Resolve(p);
            var report = StandingOrders.RunNow(order, map);
            return new JObject
            {
                ["ran"] = report.ActingOn > 0,
                ["summary"] = report.Summary,
                ["acting_on"] = report.ActingOn,
                ["ids"] = new JArray(report.Ids.Take(40)),
            };
        }
    }
}
