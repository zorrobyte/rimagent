// Written for RimBridge (2026): standing orders — deterministic reflexes that run inside the mod on their own
// cadence, each toggleable and explainable by the director, each respecting manual-touch cooldowns.
// Ticking pattern adapted from Autopilot's ReflexComponent (MIT, the user's own code).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Verse;

namespace RimBridge.Steward.Orders
{
    /// <summary>What one pass of an order did: a one-line summary, how many things it is acting on, their ids.</summary>
    public sealed class OrderReport
    {
        public string Summary = "";
        public int ActingOn;
        public List<string> Ids = new List<string>();

        public static OrderReport Idle(string summary) => new OrderReport { Summary = summary };
        public OrderReport Act(string id) { Ids.Add(id); ActingOn = Ids.Count; return this; }
    }

    /// <summary>
    /// One standing order. Subclass, give it a unique Id, and add it to StandingOrders.Register (or the built-in
    /// list) — the registry, the RPCs, the ticker and the persisted state all key on Id. Run must never assume the
    /// previous pass happened (saves load mid-fight); Explain lists the rules in plain words.
    /// </summary>
    public abstract class Order
    {
        public abstract string Id { get; }
        public abstract string Label { get; }
        /// <summary>One paragraph for steward.orders.explain.</summary>
        public abstract string Doc { get; }
        public abstract int IntervalTicks { get; }
        public virtual bool DefaultEnabled => true;
        /// <summary>Touch reasons (rpc names, "rpc:detail" prefixes) that make this order leave a thing alone; null = every touch, empty = none.</summary>
        public virtual IReadOnlyList<string>? TouchScopes => null;
        /// <summary>Prefixes of the OwnedValues keys this order writes ("bed:", "temp:", …) so explain/release can list and clear its manual marks.</summary>
        public virtual IReadOnlyList<string> OwnedPrefixes => Array.Empty<string>();

        /// <summary>Stable tick offset so orders sharing an interval do not all fire on the same tick.</summary>
        public int Offset => OrderSchedule.Offset(Id, IntervalTicks);

        public abstract OrderReport Run(Map map);
        public abstract IEnumerable<string> Explain();

        public bool Enabled => StandingOrders.IsEnabled(this);

        protected bool Touched(Thing? t) => t != null && StandingOrders.IsTouched(t.ThingID, TouchScopes);
        protected bool Touched(string? id) => StandingOrders.IsTouched(id, TouchScopes);
    }

    /// <summary>Registry, enable/disable, manual-touch cooldowns and the run wrapper. State lives in StewardGame.</summary>
    public static class StandingOrders
    {
        public const int BudgetMsWarn = 20;
        public const int DefaultTouchTicks = TouchTable.DefaultCooldownTicks;

        private static readonly List<Order> _orders = new List<Order>
        {
            new Order_Combat(),
            new Order_Rescue(),
            new Order_Fire(),
            new Order_Unforbid(),
            new Order_Corpses(),
            new Order_Beds(),
            new Order_Policies(),
            new Order_Blueprints(),
        };

        public static IReadOnlyList<Order> All => _orders;

        /// <summary>Adds an order (ignored when an order with the same id exists).</summary>
        public static void Register(Order order)
        {
            if (order == null || _orders.Any(o => o.Id == order.Id)) return;
            _orders.Add(order);
        }

        public static Order? Get(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            string k = id!.Trim();
            return _orders.FirstOrDefault(o => string.Equals(o.Id, k, StringComparison.OrdinalIgnoreCase));
        }

        public static T? Get<T>() where T : Order => _orders.OfType<T>().FirstOrDefault();

        public static string IdList => string.Join("|", _orders.Select(o => o.Id));

        // ── enable ──

        public static bool IsEnabled(Order order)
        {
            var g = StewardGame.Current;
            if (g != null && g.orderEnabled.TryGetValue(order.Id, out bool v)) return v;
            return order.DefaultEnabled;
        }

        public static void SetEnabled(Order order, bool enabled)
        {
            var g = StewardGame.Current;
            if (g == null) return;
            if (enabled == order.DefaultEnabled) g.orderEnabled.Remove(order.Id); else g.orderEnabled[order.Id] = enabled;
        }

        // ── manual touch ──

        static int Now => Find.TickManager?.TicksGame ?? 0;

        /// <summary>
        /// Records that the director acted on this thing/pawn by hand: the matching orders leave it alone for
        /// `cooldownTicks` (default 2500 = one in-game hour). reason is the RPC name, optionally "rpc:detail".
        /// </summary>
        public static void Touch(string? thingOrPawnId, string reason, int cooldownTicks = DefaultTouchTicks)
        {
            if (string.IsNullOrEmpty(thingOrPawnId)) return;
            StewardGame.Current?.touches.Touch(thingOrPawnId!, reason, Now, cooldownTicks);
        }

        public static void Touch(Thing? thing, string reason, int cooldownTicks = DefaultTouchTicks) => Touch(thing?.ThingID, reason, cooldownTicks);

        public static bool IsTouched(string? id, IReadOnlyList<string>? scopes = null)
            => StewardGame.Current?.touches.IsTouched(id, Now, scopes) ?? false;

        public static bool IsTouched(Thing? thing, IReadOnlyList<string>? scopes = null) => thing != null && IsTouched(thing.ThingID, scopes);

        public static List<(string id, string reason, int ticksLeft)> HandsOff(IReadOnlyList<string>? scopes = null)
            => StewardGame.Current?.touches.Active(Now, scopes) ?? new List<(string, string, int)>();

        /// <summary>Every live touch reason of one thing/pawn.</summary>
        public static List<string> LiveReasons(string? id) => StewardGame.Current?.touches.LiveReasons(id, Now) ?? new List<string>();

        /// <summary>
        /// Hands a thing/pawn back to the orders: clears its touch cooldowns and the OwnedValues manual marks of the given
        /// orders (all orders when null). Returns what was cleared.
        /// </summary>
        public static (int touches, int marks) Release(string id, IEnumerable<Order>? orders = null)
        {
            var g = StewardGame.Current;
            if (g == null || string.IsNullOrEmpty(id)) return (0, 0);
            int touches = g.touches.Reasons.TryGetValue(id, out var per) ? per.Count : 0;
            g.touches.Clear(id);
            int marks = 0;
            foreach (var o in orders ?? All)
                foreach (var prefix in o.OwnedPrefixes)
                {
                    string key = prefix + id;
                    if (g.owned.ManualUntil.ContainsKey(key)) { g.owned.ClearManual(key); marks++; }
                }
            return (touches, marks);
        }

        // ── running ──

        /// <summary>Runs one order now (scheduled or forced), isolated, timed, and recorded in StewardGame.</summary>
        public static OrderReport RunNow(Order order, Map map)
        {
            var sw = Stopwatch.StartNew();
            OrderReport report;
            try { report = order.Run(map) ?? OrderReport.Idle(""); }
            catch (Exception ex)
            {
                StewardLog.ErrorOnce($"orders: {order.Id} threw: {ex}", unchecked(0x0d0e0000 ^ order.Id.GetHashCode()));
                report = OrderReport.Idle("error: " + ex.Message);
            }
            sw.Stop();
            if (sw.ElapsedMilliseconds > BudgetMsWarn)
                StewardLog.WarningOnce($"orders: {order.Id} took {sw.ElapsedMilliseconds} ms (> {BudgetMsWarn} ms budget)", unchecked(0x0d0e2000 ^ order.Id.GetHashCode()));
            StewardGame.Current?.RecordOrderRun(order.Id, Now, report);
            return report;
        }

        public static int LastRunTick(Order order) => StewardGame.Current?.orderLastRun.TryGetValue(order.Id, out int t) == true ? t : -1;
        public static string? LastSummary(Order order) => StewardGame.Current?.orderSummary.TryGetValue(order.Id, out var s) == true ? s : null;
        public static int ActingOn(Order order) => StewardGame.Current?.orderActingOn.TryGetValue(order.Id, out int n) == true ? n : 0;
    }

    /// <summary>Ticks every enabled order on its own interval, staggered by Order.Offset.</summary>
    public class OrdersComponent : MapComponent
    {
        public OrdersComponent(Map map) : base(map) { }

        public static OrdersComponent? For(Map? map) => map?.GetComponent<OrdersComponent>();

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            try { TickInner(); }
            catch (Exception ex) { StewardLog.ErrorOnce($"orders: tick failed on {map}: {ex}", unchecked(0x0d0e1000 ^ map.uniqueID)); }
        }

        private void TickInner()
        {
            if (Current.ProgramState != ProgramState.Playing || StewardGame.Current == null) return;
            if (!map.mapPawns.AnyColonistSpawned) return;
            int tick = Find.TickManager.TicksGame;
            StewardGame.Current.AdoptLegacyCombat(map);
            if (tick % 120 == 0) Order_Fire.ExpireBoost(StewardGame.Current, tick); // even when the fire order is off
            foreach (var order in StandingOrders.All)
            {
                if (!OrderSchedule.Due(tick, order.IntervalTicks, order.Offset)) continue;
                if (!order.Enabled) continue;
                StandingOrders.RunNow(order, map);
            }
        }
    }

}

namespace RimBridge.Steward
{
    using RimBridge.Steward.Orders;

    /// <summary>The combat order's state on one map (keyed by map.uniqueID in StewardGame.combat).</summary>
    public sealed class CombatState : IExposable
    {
        public bool active;
        public int engagedTick = -1;
        public int lastHostileTick = -1;
        public int lastHoldTick = -1;
        public bool prolongedReported;
        /// <summary>Pawns this order drafted (only those whose draft state it actually changed).</summary>
        public HashSet<string> drafted = new HashSet<string>();
        /// <summary>pawn id → area label the pawn had before the order restricted it ("" = unrestricted).</summary>
        public Dictionary<string, string> prevArea = new Dictionary<string, string>();
        /// <summary>Fighters relieved to eat/sleep, re-drafted once both needs recover.</summary>
        public HashSet<string> relieved = new HashSet<string>();
        // not persisted: re-derived after a load
        public Dictionary<string, IntVec3> assigned = new Dictionary<string, IntVec3>();
        public string lastMode = "";
        public string mode = "idle";

        private List<string>? _draftedScribe;
        private List<string>? _relievedScribe;

        /// <summary>Something is left to undraft/restore after a release (touched pawns whose cooldown had not expired).</summary>
        public bool PendingRelease => !active && (drafted.Count > 0 || prevArea.Count > 0);

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) { _draftedScribe = new List<string>(drafted); _relievedScribe = new List<string>(relieved); }
            Scribe_Values.Look(ref active, "active");
            Scribe_Values.Look(ref engagedTick, "engagedTick", -1);
            Scribe_Values.Look(ref lastHostileTick, "lastHostileTick", -1);
            Scribe_Values.Look(ref lastHoldTick, "lastHoldTick", -1);
            Scribe_Values.Look(ref prolongedReported, "prolongedReported");
            Scribe_Collections.Look(ref _draftedScribe, "drafted", LookMode.Value);
            Scribe_Collections.Look(ref _relievedScribe, "relieved", LookMode.Value);
            Scribe_Collections.Look(ref prevArea, "prevArea", LookMode.Value, LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                drafted = new HashSet<string>(_draftedScribe ?? new List<string>());
                relieved = new HashSet<string>(_relievedScribe ?? new List<string>());
                prevArea ??= new Dictionary<string, string>();
                _draftedScribe = null; _relievedScribe = null;
            }
        }
    }

    public partial class StewardGame
    {
        // enable overrides, per-order run records
        public Dictionary<string, bool> orderEnabled = new Dictionary<string, bool>();
        public Dictionary<string, int> orderLastRun = new Dictionary<string, int>();
        public Dictionary<string, string> orderSummary = new Dictionary<string, string>();
        public Dictionary<string, int> orderActingOn = new Dictionary<string, int>();
        // manual-touch cooldowns
        public TouchTable touches = new TouchTable();
        // rally rect (w <= 0 = none), bound to one map
        public int rallyX, rallyZ, rallyW, rallyH;
        public int rallyMapId = -1;
        // combat order: one state per map (map.uniqueID)
        public Dictionary<int, CombatState> combat = new Dictionary<int, CombatState>();
        /// <summary>Pass-1 saves kept one global combat state; it is adopted by the first ticking home map (or the rally map).</summary>
        private CombatState? _legacyCombat;
        // rescue order
        public string? rescueSpotId;
        // fire order
        public Dictionary<string, int> fireSeen = new Dictionary<string, int>();
        public int fireBoostUntil = -1;
        public string fireBoostPosture = "";
        public bool fireBoostOwnsPosture;
        public Dictionary<string, float> fireBoostPrev = new Dictionary<string, float>();
        // values other orders set on things (beds' owners, heater targets, medical care, food policy) + manual marks
        public OwnedValues owned = new OwnedValues();
        // policies order
        /// <summary>pawn id → food policy id before the steward-raw switch (-1 = none).</summary>
        public Dictionary<string, int> foodPrevPolicy = new Dictionary<string, int>();
        public bool foodSwitchActive;
        public int stewardRawPolicyId = -1;
        // corpses order
        public string? butcherSpotId;
        /// <summary>Unique load ids of the standing bills the corpses order created (a deleted one is not re-created for 2 days).</summary>
        public string? butcherBillId;
        public string? cremateBillId;
        // blueprints order
        public Dictionary<string, int> bpUnreachableSince = new Dictionary<string, int>();
        public int bpStaleReportedDay = -1;

        private Dictionary<string, string>? _ownedSetScribe;
        private Dictionary<string, int>? _ownedManualScribe;
        private Dictionary<string, int>? _touchUntilScribe;
        private Dictionary<string, string>? _touchReasonScribe;
        // pass-1 combat fields (read only; written as the legacy block so an old save still loads)
        private bool _lgCombatActive;
        private int _lgCombatEngagedTick = -1, _lgCombatLastHostileTick = -1, _lgCombatLastHoldTick = -1;
        private List<string>? _lgCombatDrafted;
        private Dictionary<string, string>? _lgCombatPrevArea;

        // ── combat state access ──

        /// <summary>The combat state of this map (created on first use).</summary>
        public CombatState Combat(Map map)
        {
            int key = map?.uniqueID ?? -1;
            if (!combat.TryGetValue(key, out var st)) combat[key] = st = new CombatState();
            return st;
        }

        public CombatState? CombatOrNull(Map? map) => map != null && combat.TryGetValue(map.uniqueID, out var st) ? st : null;

        /// <summary>True while the combat order is engaged on this map and the order is switched on (a stale flag never stalls other orders).</summary>
        public bool CombatEngaged(Map? map)
        {
            var st = CombatOrNull(map);
            if (st == null || !st.active) return false;
            var order = StandingOrders.Get<Order_Combat>();
            return order == null || order.Enabled;
        }

        public IEnumerable<KeyValuePair<int, CombatState>> CombatStates => combat;

        /// <summary>Drops combat states of maps that no longer exist.</summary>
        public void PruneCombat()
        {
            if (Find.Maps == null) return;
            var alive = new HashSet<int>(Find.Maps.Select(m => m.uniqueID));
            foreach (var k in combat.Keys.Where(k => !alive.Contains(k)).ToList()) combat.Remove(k);
        }

        internal void AdoptLegacyCombat(Map map)
        {
            if (_legacyCombat == null || map == null) return;
            bool mine = rallyMapId >= 0 ? rallyMapId == map.uniqueID : map.IsPlayerHome;
            if (!mine) return;
            combat[map.uniqueID] = _legacyCombat;
            _legacyCombat = null;
            StewardLog.Message($"orders: adopted the pass-1 combat state on map {map.uniqueID}");
        }

        public bool HasRally(Map? map) => rallyW > 0 && rallyH > 0 && map != null && rallyMapId == map.uniqueID;

        public CellRect? RallyRect(Map? map) => HasRally(map) ? new CellRect(rallyX, rallyZ, rallyW, rallyH) : (CellRect?)null;

        public void SetRally(Map map, CellRect rect)
        {
            rallyX = rect.minX; rallyZ = rect.minZ; rallyW = rect.Width; rallyH = rect.Height; rallyMapId = map.uniqueID;
        }

        public void ClearRally() { rallyX = rallyZ = rallyW = rallyH = 0; rallyMapId = -1; }

        public void RecordOrderRun(string id, int tick, OrderReport report)
        {
            orderLastRun[id] = tick;
            orderSummary[id] = report.Summary ?? "";
            orderActingOn[id] = report.ActingOn;
        }

        private void ExposeOrdersData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                touches.Prune(Find.TickManager?.TicksGame ?? 0);
                _touchUntilScribe = new Dictionary<string, int>(touches.Until);
                _touchReasonScribe = touches.EncodeReasons();
                owned.Prune(Find.TickManager?.TicksGame ?? 0);
                _ownedSetScribe = new Dictionary<string, string>(owned.Set);
                _ownedManualScribe = new Dictionary<string, int>(owned.ManualUntil);
                PruneCombat();
                _lgCombatActive = false; _lgCombatEngagedTick = _lgCombatLastHostileTick = _lgCombatLastHoldTick = -1;
                _lgCombatDrafted = null; _lgCombatPrevArea = null;
            }
            Scribe_Collections.Look(ref orderEnabled, "orderEnabled", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref orderLastRun, "orderLastRun", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref orderSummary, "orderSummary", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref orderActingOn, "orderActingOn", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _touchUntilScribe, "touchUntil", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _touchReasonScribe, "touchReason", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref rallyX, "rallyX");
            Scribe_Values.Look(ref rallyZ, "rallyZ");
            Scribe_Values.Look(ref rallyW, "rallyW");
            Scribe_Values.Look(ref rallyH, "rallyH");
            Scribe_Values.Look(ref rallyMapId, "rallyMapId", -1);
            Scribe_Collections.Look(ref combat, "combatByMap", LookMode.Value, LookMode.Deep);
            // legacy (pass 1) global combat state
            Scribe_Values.Look(ref _lgCombatActive, "combatActive");
            Scribe_Values.Look(ref _lgCombatEngagedTick, "combatEngagedTick", -1);
            Scribe_Values.Look(ref _lgCombatLastHostileTick, "combatLastHostileTick", -1);
            Scribe_Values.Look(ref _lgCombatLastHoldTick, "combatLastHoldTick", -1);
            Scribe_Collections.Look(ref _lgCombatDrafted, "combatDrafted", LookMode.Value);
            Scribe_Collections.Look(ref _lgCombatPrevArea, "combatPrevArea", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref rescueSpotId, "rescueSpotId");
            Scribe_Collections.Look(ref fireSeen, "fireSeen", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref fireBoostUntil, "fireBoostUntil", -1);
            Scribe_Values.Look(ref fireBoostPosture, "fireBoostPosture", "");
            Scribe_Values.Look(ref fireBoostOwnsPosture, "fireBoostOwnsPosture");
            Scribe_Collections.Look(ref fireBoostPrev, "fireBoostPrev", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _ownedSetScribe, "ownedSet", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref _ownedManualScribe, "ownedManualUntil", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref foodPrevPolicy, "foodPrevPolicy", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref foodSwitchActive, "foodSwitchActive");
            Scribe_Values.Look(ref stewardRawPolicyId, "stewardRawPolicyId", -1);
            Scribe_Values.Look(ref butcherSpotId, "butcherSpotId");
            Scribe_Values.Look(ref butcherBillId, "butcherBillId");
            Scribe_Values.Look(ref cremateBillId, "cremateBillId");
            Scribe_Collections.Look(ref bpUnreachableSince, "bpUnreachableSince", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref bpStaleReportedDay, "bpStaleReportedDay", -1);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                orderEnabled ??= new Dictionary<string, bool>();
                orderLastRun ??= new Dictionary<string, int>();
                orderSummary ??= new Dictionary<string, string>();
                orderActingOn ??= new Dictionary<string, int>();
                combat ??= new Dictionary<int, CombatState>();
                foreach (var k in combat.Keys.Where(k => combat[k] == null).ToList()) combat.Remove(k);
                if (_lgCombatActive || (_lgCombatDrafted != null && _lgCombatDrafted.Count > 0) || (_lgCombatPrevArea != null && _lgCombatPrevArea.Count > 0))
                {
                    _legacyCombat = new CombatState
                    {
                        active = _lgCombatActive,
                        engagedTick = _lgCombatEngagedTick,
                        lastHostileTick = _lgCombatLastHostileTick,
                        lastHoldTick = _lgCombatLastHoldTick,
                        drafted = new HashSet<string>(_lgCombatDrafted ?? new List<string>()),
                        prevArea = _lgCombatPrevArea ?? new Dictionary<string, string>(),
                    };
                }
                _lgCombatDrafted = null; _lgCombatPrevArea = null;
                fireSeen ??= new Dictionary<string, int>();
                fireBoostPrev ??= new Dictionary<string, float>();
                fireBoostPosture ??= "";
                touches = TouchTable.Decode(_touchUntilScribe, _touchReasonScribe);
                _touchUntilScribe = null; _touchReasonScribe = null;
                owned = new OwnedValues
                {
                    Set = _ownedSetScribe ?? new Dictionary<string, string>(),
                    ManualUntil = _ownedManualScribe ?? new Dictionary<string, int>(),
                };
                _ownedSetScribe = null; _ownedManualScribe = null;
                foodPrevPolicy ??= new Dictionary<string, int>();
                bpUnreachableSince ??= new Dictionary<string, int>();
            }
        }
    }
}
