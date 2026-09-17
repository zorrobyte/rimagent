// Written for RimBridge (2026): steward events on the EventLedger (kind "steward").
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RimBridge.Ledger;
using RimBridge.Steward.Stock;
using RimWorld;
using Verse;

namespace RimBridge.Steward
{
    /// <summary>
    /// Ledger events of kind "steward": stock_stalled (once per job per day), stock_reached (target met after being
    /// below), posture_expired, research_advanced. text = "{event} {detail}", data.event = the event name.
    /// </summary>
    public static class StewardLedger
    {
        public const string Kind = "steward";

        private sealed class JobState { public bool Seen; public bool Below; public int? LastStalledDay; }
        private static readonly Dictionary<string, JobState> _jobs = new Dictionary<string, JobState>();
        private static bool _hooked;

        /// <summary>Idempotent: subscribes to StewardTuning.PostureExpired.</summary>
        public static void EnsureHooked()
        {
            if (_hooked) return;
            _hooked = true;
            StewardTuning.PostureExpired += OnPostureExpired;
        }

        /// <summary>Forget per-job state (new game / load).</summary>
        public static void Reset() => _jobs.Clear();

        public static bool IsStalled(StockJob job) =>
            job != null && StockStallRule.IsStalled(job.ConsecutiveFailures, job.RunsWithoutTargets, job.UpdateIntervalTicks);

        /// <summary>Called by StockComponent after every job run (scheduled or steward.stock.run).</summary>
        public static void AfterRun(StockJob job, bool workDone)
        {
            try
            {
                if (job?.Trigger == null) return;
                string key = job.GetUniqueLoadID();
                if (!_jobs.TryGetValue(key, out var st)) _jobs[key] = st = new JobState();
                bool below = job.WantsWork;
                int current = job.CurrentCount;
                int target = job.EffectiveTarget;
                if (st.Seen && st.Below && !below)
                    Add("stock_reached", $"{job.Label} {current}/{target}", JobData(job, current, target));
                st.Seen = true;
                st.Below = below;
                if ((below || job.ConsecutiveFailures >= 3) && IsStalled(job))
                {
                    int day = GenDate.DaysPassed;
                    if (StockStallRule.ShouldReport(day, st.LastStalledDay))
                    {
                        st.LastStalledDay = day;
                        var data = JobData(job, current, target);
                        data["failures"] = job.ConsecutiveFailures;
                        data["runs_without_targets"] = job.RunsWithoutTargets;
                        data["summary"] = job.LastRunSummary;
                        Add("stock_stalled", $"{job.Label} {current}/{target}: {job.LastRunSummary ?? "no progress"}", data);
                    }
                }
            }
            catch (Exception ex) { StewardLog.Warning($"ledger after-run hook threw: {ex.Message}"); }
        }

        public static void ResearchAdvanced(ResearchProjectDef def, int remaining, string? why)
        {
            var data = new JObject { ["def"] = def.defName, ["label"] = def.label, ["remaining"] = remaining };
            if (why != null) data["after"] = why;
            Add("research_advanced", $"{def.label} ({remaining} queued)", data);
        }

        private static void OnPostureExpired(string label)
        {
            Add("posture_expired", label, new JObject { ["label"] = label });
        }

        /// <summary>Kind "orders": combat_engaged {hostiles, drafted}, combat_released, rescue {pawn}, fire {cells}, … (wake-worthy, not critical).</summary>
        public const string OrdersKind = "orders";

        public static void Orders(string ev, string detail, JObject data, IntVec3? cell = null)
        {
            try
            {
                data ??= new JObject();
                data["event"] = ev;
                EventLedger.Add(OrdersKind, $"{ev} {detail}", data, cell);
            }
            catch (Exception ex) { StewardLog.Warning($"ledger add orders/{ev} threw: {ex.Message}"); }
        }

        private static JObject JobData(StockJob job, int current, int target) => new JObject
        {
            ["id"] = job.Id,
            ["kind"] = StewardRpc.KindOut(job.Kind),
            ["label"] = job.Label,
            ["current"] = current,
            ["target"] = target,
        };

        private static void Add(string ev, string detail, JObject data)
        {
            try
            {
                data["event"] = ev;
                EventLedger.Add(Kind, $"{ev} {detail}", data);
            }
            catch (Exception ex) { StewardLog.Warning($"ledger add {ev} threw: {ex.Message}"); }
        }
    }
}
