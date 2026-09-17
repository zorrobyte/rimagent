// Written for RimBridge (2026): the fire standing order (report + a one-hour "firefight" posture bump).
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;

namespace RimBridge.Steward.Orders
{
    /// <summary>
    /// Fire inside the home area with no colonist on a firefighting job → a "firefight" posture (Firefighter +1,
    /// BasicWorker +0.5) for one hour through StewardTuning (merged into an active posture and restored afterwards),
    /// plus one ledger event per coarse fire cluster per hour.
    /// </summary>
    public sealed class Order_Fire : Order
    {
        public override string Id => "fire";
        public override string Label => "Fire: boost firefighting, report clusters";
        public override string Doc =>
            "Every 120 ticks: fires in the home area are counted. If no colonist is on a BeatFire job the scorer is tilted toward " +
            "firefighting for one hour (posture \"firefight\": Firefighter +1, BasicWorker +0.5; merged into the director's posture " +
            "if one is active and reverted when the hour ends). Each fire cluster (8x8 cell bucket) is reported to the ledger once " +
            "per 2500 ticks as fire {cells}. Nothing is drafted and no job is issued.";
        public override int IntervalTicks => 120;
        public override IReadOnlyList<string>? TouchScopes => Array.Empty<string>();

        public const int BoostTicks = 2500;
        public const string PostureLabel = "firefight";
        public static readonly Dictionary<string, float> BoostDeltas = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
        {
            ["Firefighter"] = 1f,
            ["BasicWorker"] = 0.5f,
        };

        public override IEnumerable<string> Explain()
        {
            yield return "trigger: any spawned fire on a cell of the Home area";
            yield return "nobody fighting: no free colonist whose current job is BeatFire";
            yield return $"action: posture '{PostureLabel}' for 1 hour (Firefighter +1.0, BasicWorker +0.5); an active posture keeps its label and gets the deltas merged, restored when the boost ends";
            yield return $"ledger: fire {{cells, at}} once per {FireClusters.BucketSize}x{FireClusters.BucketSize} bucket per {FireClusters.CooldownTicks} ticks";
            var g = StewardGame.Current;
            if (g != null && g.fireBoostUntil >= 0) yield return $"boost active until tick {g.fireBoostUntil} (posture '{g.fireBoostPosture}', owned: {g.fireBoostOwnsPosture})";
        }

        public override OrderReport Run(Map map)
        {
            var g = StewardGame.Current;
            if (g == null) return OrderReport.Idle("no game state");
            int tick = Find.TickManager.TicksGame;
            ExpireBoost(g, tick);

            var home = map.areaManager.Home;
            var fires = new List<Thing>();
            if (home != null && home.TrueCount > 0)
                foreach (var f in map.listerThings.ThingsOfDef(ThingDefOf.Fire))
                    if (f.Spawned && home[f.Position]) fires.Add(f);
            if (fires.Count == 0) return OrderReport.Idle("no fire in the home area");

            var report = new OrderReport();
            foreach (var f in fires) report.Act(f.ThingID);
            int fighting = map.mapPawns.FreeColonistsSpawned.Count(p => p.CurJobDef == JobDefOf.BeatFire);

            // ledger once per cluster
            var keys = fires.Select(f => FireClusters.Key(f.Position.x, f.Position.z)).Distinct().ToList();
            var fresh = FireClusters.Report(g.fireSeen, keys, tick);
            foreach (var k in fresh)
            {
                var inBucket = fires.Where(f => FireClusters.Key(f.Position.x, f.Position.z) == k).ToList();
                var at = inBucket[0].Position;
                StewardLedger.Orders("fire", $"{inBucket.Count} burning cell(s) in the home area at {at.x},{at.z}" + (fighting == 0 ? ", nobody fighting it" : $", {fighting} fighting"),
                    new JObject { ["cells"] = inBucket.Count, ["at"] = new JArray(at.x, at.z), ["fighting"] = fighting }, at);
            }

            string action = "";
            if (fighting == 0)
            {
                if (g.fireBoostUntil < 0 || !BoostStillInstalled(g))
                {
                    ApplyBoost(g, tick);
                    action = $"; {PostureLabel} posture applied for 1h";
                }
                else action = "; " + PostureLabel + " posture active";
            }
            report.Summary = $"{fires.Count} fire cell(s) in the home area, {fighting} colonist(s) fighting{action}";
            return report;
        }

        static bool BoostStillInstalled(StewardGame g)
        {
            if (!StewardTuning.PostureActive()) return false;
            if (!string.Equals(StewardTuning.PostureLabel, g.fireBoostPosture, StringComparison.OrdinalIgnoreCase)) return false;
            return StewardTuning.PostureWorkDeltas.TryGetValue("Firefighter", out float d) && d >= BoostDeltas["Firefighter"] - 0.001f;
        }

        static void ApplyBoost(StewardGame g, int tick)
        {
            g.fireBoostPrev.Clear();
            if (!StewardTuning.PostureActive())
            {
                StewardTuning.SetPosture(PostureLabel, BoostTicks / (float)StewardTuning.TicksPerHour, BoostDeltas, null, null);
                g.fireBoostOwnsPosture = true;
            }
            else
            {
                foreach (var kv in BoostDeltas)
                {
                    if (StewardTuning.PostureWorkDeltas.TryGetValue(kv.Key, out float prev)) g.fireBoostPrev[kv.Key] = prev;
                    StewardTuning.PostureWorkDeltas[kv.Key] = Math.Max(prev, kv.Value);
                }
                g.fireBoostOwnsPosture = false;
            }
            g.fireBoostPosture = StewardTuning.PostureLabel;
            g.fireBoostUntil = tick + BoostTicks;
        }

        /// <summary>Ends the boost: a posture the order created expires by itself; merged deltas are put back.</summary>
        public static void ExpireBoost(StewardGame g, int tick)
        {
            if (g.fireBoostUntil < 0 || tick < g.fireBoostUntil) return;
            if (!g.fireBoostOwnsPosture && StewardTuning.PostureActive()
                && string.Equals(StewardTuning.PostureLabel, g.fireBoostPosture, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var kv in BoostDeltas)
                {
                    if (g.fireBoostPrev.TryGetValue(kv.Key, out float prev)) StewardTuning.PostureWorkDeltas[kv.Key] = prev;
                    else StewardTuning.PostureWorkDeltas.Remove(kv.Key);
                }
            }
            g.fireBoostUntil = -1;
            g.fireBoostPosture = "";
            g.fireBoostOwnsPosture = false;
            g.fireBoostPrev.Clear();
        }
    }
}
