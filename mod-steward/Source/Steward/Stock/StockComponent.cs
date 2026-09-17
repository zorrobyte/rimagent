// Derived from Colony Manager Redux Core/Manager.cs, Core/JobTracker.cs and Things/Building_AIManager.cs (MIT, see THIRD_PARTY_NOTICES.md)
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimBridge.Steward;
using Verse;

namespace RimBridge.Steward.Stock
{
    /// <summary>
    /// Headless job runner: every SchedulerIntervalTicks, run the most overdue job's Gather → Execute.
    /// No manager desk or Managing work type is required.
    /// </summary>
    public class StockComponent : MapComponent
    {
        private List<StockJob> _jobs = new List<StockJob>();
        private int _nextId = 1;
        private bool _defaultsApplied;
        private int _lastScaleCheckTick = -1;
        private int _lastColonistCount = -1;
        private bool _productionDefaultApplied;
        private int _lastProductionCheckTick = -1;
        public bool OutdoorJobsPaused;

        public StockComponent(Map map) : base(map) { }

        public static StockComponent? For(Map? map) => map?.GetComponent<StockComponent>();

        public IReadOnlyList<StockJob> Jobs => _jobs;
        private StockSettings Settings => StewardMod.Settings.stock;

        public T Add<T>(T job) where T : StockJob
        {
            job.Map = map;
            job.MapId = map.uniqueID;
            job.Id = _nextId++;
            if (job.Trigger != null) job.Trigger.CountAllOnMap = true; // loose items count until a stockpile exists
            _jobs.Add(job);
            return job;
        }

        public void Remove(StockJob job, bool cleanup = true)
        {
            if (cleanup) { try { job.CleanUp(); } catch (Exception ex) { StewardLog.Warning($"stock: cleanup of {job} threw: {ex.Message}"); } }
            _jobs.Remove(job);
        }

        public IEnumerable<T> JobsOfType<T>() where T : StockJob => _jobs.OfType<T>();

        public StockJob? FindById(int id) => _jobs.FirstOrDefault(j => j.Id == id);

        public StockJob? FindByKind(string kind) => _jobs.FirstOrDefault(j => string.Equals(j.Kind, kind, StringComparison.OrdinalIgnoreCase));

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            if (!_defaultsApplied && _jobs.Count == 0 && map.IsPlayerHome)
            {
                try { DefaultStockPlan.Apply(this); }
                catch (Exception ex) { StewardLog.Error($"stock: default plan failed on {map}: {ex}"); }
                _defaultsApplied = true;
            }
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();
            try { TickInner(); }
            catch (Exception ex) { StewardLog.ErrorOnce($"stock: tick failed on {map}: {ex}", 0x57ea0000 ^ map.uniqueID); }
        }

        private void TickInner()
        {
            var s = Settings;
            if (!StewardSwitch.StockEnabled) return;
            int tick = Find.TickManager.TicksGame;
            if (tick % Math.Max(30, s.SchedulerIntervalTicks) != 0) return;
            if (map.mapPawns.FreeColonistsSpawnedCount == 0) return;

            if (s.ScaleTargetsWithColonists && tick - _lastScaleCheckTick > 60000)
            {
                _lastScaleCheckTick = tick;
                int n = map.mapPawns.FreeColonistsSpawnedCount;
                if (n != _lastColonistCount) { _lastColonistCount = n; DefaultStockPlan.Rescale(this, n); }
            }

            if (map.IsPlayerHome && !_productionDefaultApplied && tick - _lastProductionCheckTick >= 2500)
            {
                _lastProductionCheckTick = tick;
                try { if (DefaultStockPlan.EnsureProduction(this, map.mapPawns.FreeColonistsSpawnedCount)) _productionDefaultApplied = true; }
                catch (Exception ex) { StewardLog.ErrorOnce($"stock: default production job failed: {ex}", 0x57ea1000 ^ map.uniqueID); }
            }

            StockJob? job = null;
            foreach (var j in _jobs)
            {
                if (!j.ShouldRunNow(tick)) continue;
                if (OutdoorJobsPaused && j.IsOutdoorWork) continue;
                if (!IsKindEnabled(j)) continue;
                if (job == null || j.LastRunTick < job.LastRunTick) job = j;
            }
            if (job == null) return;
            RunJob(job);
        }

        public bool IsKindEnabled(StockJob job)
        {
            var s = Settings;
            return job.Kind switch
            {
                "Forestry" => s.EnableForestry,
                "Foraging" => s.EnableForaging,
                "Hunting" => s.EnableHunting,
                "Mining" => s.EnableMining,
                "Production" => s.EnableProduction,
                "Livestock" => s.EnableLivestock,
                _ => true,
            };
        }

        public bool RunJob(StockJob job)
        {
            var sw = Stopwatch.StartNew();
            bool workDone = false;
            try
            {
                var data = job.Gather();
                if (data != null) workDone = job.Execute(data);
                job.ConsecutiveFailures = 0;
            }
            catch (Exception ex)
            {
                job.ConsecutiveFailures++;
                if (job.ConsecutiveFailures >= 3)
                {
                    job.Suspended = true;
                    StewardLog.Warning($"stock: {job} suspended after repeated errors: {ex}");
                }
                else StewardLog.ErrorOnce($"stock: {job} threw: {ex}", job.GetUniqueLoadID().GetHashCode());
            }
            finally
            {
                job.Touch();
                sw.Stop();
                if (sw.ElapsedMilliseconds > Settings.BudgetMsWarn)
                    StewardLog.WarningOnce($"stock: {job} took {sw.ElapsedMilliseconds} ms", job.GetUniqueLoadID().GetHashCode() ^ 0x5a5a);
                StewardLedger.AfterRun(job, workDone);
            }
            return workDone;
        }

        public void Notify_AreaRemoved(Area area)
        {
            foreach (var j in _jobs) j.Notify_AreaRemoved(area);
        }

        public string DebugSummary()
        {
            var sb = new System.Text.StringBuilder($"=== Stock ({map}) jobs={_jobs.Count} paused={OutdoorJobsPaused} ===\n");
            foreach (var j in _jobs)
            {
                sb.AppendLine($"{j} designations={j.Designations.Count} managed={j.Managed} suspended={j.Suspended} autoScaled={j.AutoScaled} lastRun={j.LastRunTick} noTargetRuns={j.RunsWithoutTargets}");
                if (!j.LastRunSummary.NullOrEmpty()) sb.AppendLine($"    last: {j.LastRunSummary}");
            }
            return sb.ToString();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref _jobs, "jobs", LookMode.Deep);
            Scribe_Values.Look(ref _nextId, "nextId", 1);
            Scribe_Values.Look(ref _defaultsApplied, "defaultsApplied");
            Scribe_Values.Look(ref _productionDefaultApplied, "productionDefaultApplied");
            Scribe_Values.Look(ref _lastColonistCount, "lastColonistCount", -1);
            Scribe_Values.Look(ref OutdoorJobsPaused, "outdoorJobsPaused");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                _jobs ??= new List<StockJob>();
                _jobs.RemoveAll(j => j == null);
                foreach (var j in _jobs)
                {
                    if (j.Map == null) j.Map = map;
                    if (j.MapId < 0) j.MapId = map.uniqueID; // saves from before mapId was scribed
                }
            }
        }
    }
}
