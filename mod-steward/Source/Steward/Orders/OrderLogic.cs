// Written for RimBridge (2026). Verse-free rules behind the standing orders (rally spreading, draft eligibility,
// manual-touch cooldowns, the stagger schedule, fire clustering); shared with the unit tests in mod/Tests.
using System;
using System.Collections.Generic;

namespace RimBridge.Steward.Orders
{
    /// <summary>A standable cell inside the rally rect with the game's surrounding-cover score (0 = open ground).</summary>
    public struct RallyCandidate
    {
        public int X, Z;
        public float Cover;
        public RallyCandidate(int x, int z, float cover) { X = x; Z = z; Cover = cover; }
        public override string ToString() => $"({X},{Z} cover {Cover:0.00})";
    }

    /// <summary>Picks distinct rally cells: cover first, then spread out, all biased toward the rect centre.</summary>
    public static class RallyLogic
    {
        public const float CoverWeight = 1.5f;
        public const float SpreadCap = 4f;
        public const float CenterWeight = 0.05f;

        static float Dist(int ax, int az, int bx, int bz)
        {
            float dx = ax - bx, dz = az - bz;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// Greedy: the first pick is the best-covered cell near the centre; every later pick maximises
        /// min(distance to the cells already picked, SpreadCap) + cover × CoverWeight − distance-to-centre × CenterWeight.
        /// Returns at most n cells, never the same cell twice, in pick order (stable for equal scores).
        /// </summary>
        public static List<RallyCandidate> Spread(IReadOnlyList<RallyCandidate> candidates, int n, int centerX, int centerZ)
        {
            var picks = new List<RallyCandidate>();
            if (candidates == null || candidates.Count == 0 || n <= 0) return picks;
            var used = new bool[candidates.Count];
            while (picks.Count < n && picks.Count < candidates.Count)
            {
                int best = -1; float bestScore = float.MinValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (used[i]) continue;
                    var c = candidates[i];
                    float score = c.Cover * CoverWeight - Dist(c.X, c.Z, centerX, centerZ) * CenterWeight;
                    if (picks.Count > 0)
                    {
                        float min = float.MaxValue;
                        foreach (var p in picks) min = Math.Min(min, Dist(c.X, c.Z, p.X, p.Z));
                        score += Math.Min(min, SpreadCap);
                    }
                    if (score > bestScore) { bestScore = score; best = i; }
                }
                if (best < 0) break;
                used[best] = true;
                picks.Add(candidates[best]);
            }
            return picks;
        }
    }

    /// <summary>Plain facts about a colonist, filled by the combat order from the live pawn.</summary>
    public sealed class DraftFlags
    {
        public bool Spawned = true;
        public bool Dead;
        public bool Downed;
        public bool Prisoner;
        public bool Slave;
        public bool Juvenile;
        public bool IncapableOfViolence;
        public bool InMentalState;
        public bool HasDrafter = true;
        public bool HasWeapon = true;
        public float HealthPct = 1f;
        public bool Unmanaged;
        public bool Touched;
        /// <summary>Food / rest need levels (0..1); 1 when the pawn has no such need.</summary>
        public float Food = 1f;
        public float Rest = 1f;
        /// <summary>The order relieved this pawn to eat/sleep and it has not recovered yet (hysteresis).</summary>
        public bool Resting;
        /// <summary>A hostile stands within CombatTimers.NearHostileRadius of the pawn or the rally centre: no relief now.</summary>
        public bool HostileNear;
    }

    /// <summary>Draft rules of the combat order (spec: skip downed, prisoners, children, pacifists, &lt;30% health, mental breaks, unmanaged, touched, unarmed; plus a needs guard).</summary>
    public static class CombatEligibility
    {
        public const float MinHealthPct = 0.3f;
        /// <summary>Below this food or rest level a drafted fighter is relieved (when no hostile is near).</summary>
        public const float ReliefPct = 0.15f;
        /// <summary>A relieved fighter is drafted again only once both needs are above this level.</summary>
        public const float RecoverPct = 0.5f;
        public const string NeedsReason = "needs (hungry/exhausted)";

        /// <summary>Hysteresis: true when the pawn should be (or stay) relieved to eat/sleep.</summary>
        public static bool NeedsBreak(float food, float rest, bool resting, bool hostileNear)
        {
            if (resting) return food < RecoverPct || rest < RecoverPct;
            if (hostileNear) return false;
            return food < ReliefPct || rest < ReliefPct;
        }

        /// <summary>null when the pawn may be drafted, else the reason it is skipped.</summary>
        public static string? WhyNot(DraftFlags f)
        {
            if (f == null) return "no pawn";
            if (!f.Spawned || f.Dead) return "not on the map";
            if (f.Downed) return "downed";
            if (f.Prisoner) return "prisoner";
            if (f.Slave) return "slave";
            if (f.Juvenile) return "child";
            if (f.IncapableOfViolence) return "incapable of violence";
            if (f.InMentalState) return "mental state";
            if (!f.HasDrafter) return "cannot be drafted";
            if (f.Touched) return "hands-off (manual touch)";
            if (f.Unmanaged) return "unmanaged (steward.pawn managed=false)";
            if (f.HealthPct < MinHealthPct) return $"health {(int)(f.HealthPct * 100)}% < {(int)(MinHealthPct * 100)}%";
            if (!f.HasWeapon) return "unarmed";
            if (NeedsBreak(f.Food, f.Rest, f.Resting, f.HostileNear)) return NeedsReason;
            return null;
        }

        public static bool CanDraft(DraftFlags f) => WhyNot(f) == null;
    }

    /// <summary>Touch scopes of the combat order and how a touch reason affects release.</summary>
    public static class CombatScopes
    {
        /// <summary>Touches that keep a pawn out of the draft (and out of the Home restriction) for the cooldown.</summary>
        public static readonly string[] Touch = { "ui.draft", "ui.goto", "ui.attack", "ui.order.drafted", "ui.job", "ui.press:draft", "ui.set_policies:area" };
        /// <summary>Manual military control: on release such a pawn stays drafted until the touch expires.</summary>
        public static readonly string[] Control = { "ui.draft", "ui.goto", "ui.attack", "ui.order.drafted", "ui.press:draft" };
        /// <summary>The director chose the pawn's area: the order's Home restriction is never restored over it.</summary>
        public static readonly string[] Area = { "ui.set_policies:area" };

        static bool AnyIn(IEnumerable<string>? reasons, string[] scopes)
        {
            if (reasons == null) return false;
            foreach (var r in reasons) foreach (var s in scopes) if (TouchTable.ReasonInScope(r, s)) return true;
            return false;
        }

        /// <summary>True when a pawn the order drafted must stay drafted at release (live control touch).</summary>
        public static bool KeepDrafted(IEnumerable<string>? liveReasons) => AnyIn(liveReasons, Control);

        /// <summary>
        /// What to do with a Home restriction the order applied: Restore (put the previous area back), Drop (the
        /// director changed the area meanwhile or chose one explicitly: forget the record), or Keep (retry later).
        /// </summary>
        public static AreaRelease AreaAction(IEnumerable<string>? liveReasons, bool stillHome)
        {
            if (!stillHome) return AreaRelease.Drop;
            if (AnyIn(liveReasons, Area)) return AreaRelease.Drop;
            return AreaRelease.Restore;
        }
    }

    public enum AreaRelease { Restore, Drop, Keep }

    /// <summary>Facts about one hostile, filled by the combat order from the live thing.</summary>
    public sealed class HostileFacts
    {
        public bool IsPawn;
        public bool HasLord;
        public bool Siege;          // LordJob_Siege
        public bool Assaulting;     // current duty is an assault-type duty (AssaultColony, Breaching, Sapper, Kidnap, Steal, HuntEnemiesIndividual, AssaultThing)
        public bool Manhunter;
        public bool InHome;
        public float DistToRally;
        public bool ColonistOutside;    // some colonist stands outside the Home area (or there is no Home area)
    }

    /// <summary>
    /// Engage or watch? Hostiles inside the Home area or near the rally engage; assaulting raiders engage; sieges,
    /// staging/sleeping raids, mech-cluster guards, far turrets and manhunters with everybody indoors are watched.
    /// </summary>
    public static class ThreatRules
    {
        public const float EngageRadius = 40f;

        public static bool Engage(HostileFacts f)
        {
            if (f == null) return false;
            if (f.InHome || f.DistToRally <= EngageRadius) return true;
            if (f.Siege) return false;
            if (f.Manhunter) return f.ColonistOutside;
            if (f.IsPawn && f.HasLord) return f.Assaulting;
            return false;
        }

        public static string Label(HostileFacts f)
        {
            if (f.Siege) return "siege";
            if (f.Manhunter) return "manhunter";
            if (f.IsPawn && f.HasLord) return f.Assaulting ? "assaulting" : "staging";
            return f.IsPawn ? "hostile" : "structure";
        }
    }

    /// <summary>Which touch reasons mean the director already handled a patient (rescue order).</summary>
    public static class RescueRules
    {
        public static bool PatientHandled(string? reason)
        {
            if (string.IsNullOrEmpty(reason)) return false;
            if (reason == "ui.job:Rescue" || reason == "ui.job:TendPatient") return true;
            string? label = null;
            if (reason!.StartsWith("ui.order.drafted:", StringComparison.Ordinal)) label = reason.Substring("ui.order.drafted:".Length);
            else if (reason.StartsWith("ui.order:", StringComparison.Ordinal)) label = reason.Substring("ui.order:".Length);
            if (label == null) return false;
            return label.StartsWith("Rescue", StringComparison.OrdinalIgnoreCase) || label.StartsWith("Tend", StringComparison.OrdinalIgnoreCase);
        }

        public static bool PatientHandled(IEnumerable<string>? liveReasons)
        {
            if (liveReasons == null) return false;
            foreach (var r in liveReasons) if (PatientHandled(r)) return true;
            return false;
        }
    }

    /// <summary>
    /// Manual-touch cooldowns: thing/pawn id → every live (reason, until tick). A reason is "rpc" or "rpc:detail"; a
    /// scope matches a reason when equal or when the reason starts with "scope:". Several reasons live side by side so
    /// a later touch of another kind never erases an earlier scoped one.
    /// </summary>
    public sealed class TouchTable
    {
        public const int DefaultCooldownTicks = 2500;

        /// <summary>id → the latest until tick over all its reasons (fast path for unscoped checks).</summary>
        public Dictionary<string, int> Until = new Dictionary<string, int>();
        /// <summary>id → reason → until tick.</summary>
        public Dictionary<string, Dictionary<string, int>> Reasons = new Dictionary<string, Dictionary<string, int>>();

        public int Count => Until.Count;

        public void Touch(string id, string reason, int now, int cooldownTicks = DefaultCooldownTicks)
        {
            if (string.IsNullOrEmpty(id)) return;
            reason ??= "";
            int until = now + Math.Max(1, cooldownTicks);
            if (Until.TryGetValue(id, out int old) && old > until) Until[id] = old; else Until[id] = until;
            if (!Reasons.TryGetValue(id, out var per)) Reasons[id] = per = new Dictionary<string, int>();
            if (per.TryGetValue(reason, out int prev) && prev > until) until = prev;
            per[reason] = until;
        }

        public void Clear(string id) { Until.Remove(id); Reasons.Remove(id); }

        public static bool ReasonInScope(string? reason, string scope)
        {
            if (string.IsNullOrEmpty(reason) || string.IsNullOrEmpty(scope)) return false;
            return reason == scope || reason!.StartsWith(scope + ":", StringComparison.Ordinal);
        }

        static bool InScopes(string reason, IReadOnlyList<string> scopes)
        {
            foreach (var s in scopes) if (ReasonInScope(reason, s)) return true;
            return false;
        }

        /// <summary>True while the cooldown lasts; with scopes, only touches whose reason is in one of them count.</summary>
        public bool IsTouched(string? id, int now, IReadOnlyList<string>? scopes = null)
        {
            if (id == null || !Until.TryGetValue(id, out int until)) return false;
            if (until <= now) return false;
            if (scopes == null || scopes.Count == 0) return true;
            if (!Reasons.TryGetValue(id, out var per)) return false;
            foreach (var kv in per) if (kv.Value > now && InScopes(kv.Key, scopes)) return true;
            return false;
        }

        /// <summary>Every live (reason, ticks left) of one id, longest-lived first.</summary>
        public List<(string reason, int ticksLeft)> ReasonsFor(string? id, int now)
        {
            var list = new List<(string, int)>();
            if (id == null || !Reasons.TryGetValue(id, out var per)) return list;
            foreach (var kv in per) if (kv.Value > now) list.Add((kv.Key, kv.Value - now));
            list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return list;
        }

        /// <summary>Live reasons of one id (strings only).</summary>
        public List<string> LiveReasons(string? id, int now)
        {
            var list = new List<string>();
            foreach (var (reason, _) in ReasonsFor(id, now)) list.Add(reason);
            return list;
        }

        /// <summary>Drops expired reasons and ids; returns how many ids were removed.</summary>
        public int Prune(int now)
        {
            var dead = new List<string>();
            foreach (var kv in Reasons)
            {
                var expired = new List<string>();
                foreach (var r in kv.Value) if (r.Value <= now) expired.Add(r.Key);
                foreach (var r in expired) kv.Value.Remove(r);
                if (kv.Value.Count == 0) dead.Add(kv.Key);
            }
            foreach (var kv in Until) if (kv.Value <= now && !dead.Contains(kv.Key)) dead.Add(kv.Key);
            foreach (var k in dead) Clear(k);
            return dead.Count;
        }

        /// <summary>(id, reason, ticks left) for every live (id, reason) pair, optionally only reasons in the scopes; longest-lived first.</summary>
        public List<(string id, string reason, int ticksLeft)> Active(int now, IReadOnlyList<string>? scopes = null)
        {
            var list = new List<(string, string, int)>();
            foreach (var kv in Reasons)
                foreach (var r in kv.Value)
                {
                    if (r.Value <= now) continue;
                    if (scopes != null && scopes.Count > 0 && !InScopes(r.Key, scopes)) continue;
                    list.Add((kv.Key, r.Key, r.Value - now));
                }
            list.Sort((a, b) => b.Item3.CompareTo(a.Item3));
            return list;
        }

        // ── persistence helpers (one string per id: "reason@until|reason@until"; a legacy plain reason maps to Until[id]) ──

        public Dictionary<string, string> EncodeReasons()
        {
            var d = new Dictionary<string, string>();
            foreach (var kv in Reasons)
            {
                var parts = new List<string>();
                foreach (var r in kv.Value) parts.Add(r.Key.Replace("|", "/").Replace("@", "_") + "@" + r.Value);
                if (parts.Count > 0) d[kv.Key] = string.Join("|", parts);
            }
            return d;
        }

        public static TouchTable Decode(Dictionary<string, int>? until, Dictionary<string, string>? reasons)
        {
            var t = new TouchTable();
            if (until != null) foreach (var kv in until) t.Until[kv.Key] = kv.Value;
            if (reasons != null)
                foreach (var kv in reasons)
                {
                    var per = new Dictionary<string, int>();
                    foreach (var part in (kv.Value ?? "").Split('|'))
                    {
                        if (part.Length == 0) continue;
                        int at = part.LastIndexOf('@');
                        if (at >= 0 && int.TryParse(part.Substring(at + 1), out int u)) per[part.Substring(0, at)] = u;
                        else if (t.Until.TryGetValue(kv.Key, out int legacy)) per[part] = legacy;   // pass-1 save: one plain reason per id
                    }
                    if (per.Count > 0)
                    {
                        t.Reasons[kv.Key] = per;
                        int max = 0; foreach (var v in per.Values) if (v > max) max = v;
                        if (!t.Until.TryGetValue(kv.Key, out int cur) || cur < max) t.Until[kv.Key] = max;
                    }
                }
            return t;
        }
    }

    /// <summary>When each order fires: a stable per-id offset spreads orders that share an interval across ticks.</summary>
    public static class OrderSchedule
    {
        /// <summary>FNV-1a of the id, folded into 0..interval-1 (deterministic across runs and platforms).</summary>
        public static int Offset(string id, int intervalTicks)
        {
            int interval = Math.Max(1, intervalTicks);
            uint h = 2166136261;
            foreach (char c in id ?? "") { h ^= c; h *= 16777619; }
            return (int)(h % (uint)interval);
        }

        public static bool Due(int tick, int intervalTicks, int offset)
        {
            int interval = Math.Max(1, intervalTicks);
            return tick % interval == ((offset % interval) + interval) % interval;
        }

        /// <summary>True once per interval window; robust to a first run (lastRunTick &lt; 0).</summary>
        public static bool IntervalElapsed(int tick, int lastRunTick, int intervalTicks)
            => lastRunTick < 0 || tick - lastRunTick >= Math.Max(1, intervalTicks);
    }

    /// <summary>Combat order timers.</summary>
    public static class CombatTimers
    {
        public const int HoldTicks = 250;
        public const int ReleaseAfterHostileFreeTicks = 600;
        public const float OverrunRadius = 5f;
        /// <summary>A hostile within this distance of a fighter or the rally centre blocks needs relief for that fighter.</summary>
        public const float NearHostileRadius = 30f;
        /// <summary>Engagements longer than this (half a day) are reported once to the ledger (combat_prolonged).</summary>
        public const int ProlongedTicks = 30000;

        public static bool IsProlonged(int engagedTick, int tick) => engagedTick >= 0 && tick - engagedTick >= ProlongedTicks;

        public static bool ShouldRelease(int lastHostileTick, int tick) => lastHostileTick >= 0 && tick - lastHostileTick >= ReleaseAfterHostileFreeTicks;
        public static bool ShouldHold(int lastHoldTick, int tick) => lastHoldTick < 0 || tick - lastHoldTick >= HoldTicks;
    }

    /// <summary>Fire ledger dedupe: fires are bucketed into coarse cells, one ledger event per bucket per cooldown.</summary>
    public static class FireClusters
    {
        public const int BucketSize = 8;
        public const int CooldownTicks = 2500;

        public static string Key(int x, int z, int bucket = BucketSize)
        {
            int b = Math.Max(1, bucket);
            return $"{FloorDiv(x, b)},{FloorDiv(z, b)}";
        }

        static int FloorDiv(int a, int b) => (a >= 0 ? a : a - b + 1) / b;

        /// <summary>Returns the buckets that should be reported now and records them; expired records are dropped.</summary>
        public static List<string> Report(Dictionary<string, int> seen, IEnumerable<string> keys, int now, int cooldown = CooldownTicks)
        {
            var fresh = new List<string>();
            var expired = new List<string>();
            foreach (var kv in seen) if (now - kv.Value >= cooldown) expired.Add(kv.Key);
            foreach (var k in expired) seen.Remove(k);
            foreach (var k in keys)
            {
                if (seen.ContainsKey(k)) continue;
                seen[k] = now;
                fresh.Add(k);
            }
            return fresh;
        }
    }
}
