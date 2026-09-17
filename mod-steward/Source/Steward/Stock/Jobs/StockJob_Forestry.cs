// Derived from Colony Manager Redux ManagerJobs/ManagerJob_Forestry.cs (MIT, see THIRD_PARTY_NOTICES.md); synchronous rewrite.
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimBridge.Steward.Stock
{
    /// <summary>
    /// Keeps wood (or any tree product) at target by designating trees for harvest, best yield-per-distance first.
    /// Also supports "clear area" mode: cut every allowed plant inside the given areas (no threshold).
    /// This file is the template every other designation job follows: Gather() → WorkData → Execute().
    /// </summary>
    public class StockJob_Forestry : StockJob
    {
        public enum JobType { Logging, ClearArea }

        public enum WorkKind { None, CleanUp, ReduceDesignations, AddDesignations, ClearArea }

        public sealed class WorkData
        {
            public WorkKind Kind;
            public List<Designation> AreaDesignationsToRemove = new List<Designation>();
            public List<(Designation designation, int yield, int countAfter)> DesignationsToRemove = new List<(Designation, int, int)>();
            public List<(Plant plant, int yield, int countAfter)> DesignationsToAdd = new List<(Plant, int, int)>();
            public List<(Plant plant, Area area)> ClearAreaDesignationsToAdd = new List<(Plant, Area)>();
        }

        public JobType Type = JobType.Logging;
        public bool AllowSaplings;
        public Area? LoggingArea;
        public bool InvertLoggingArea;
        public List<Area> ClearAreas = new List<Area>();
        public HashSet<ThingDef> AllowedTrees = new HashSet<ThingDef>();
        private string? _loggingAreaScribe;
        private List<string>? _clearAreasScribe;
        private bool _completed;

        public StockJob_Forestry() { }

        public StockJob_Forestry(Map map, JobType type = JobType.Logging) : base(map)
        {
            Type = type;
            Label = type == JobType.Logging ? "Forestry (wood)" : "Forestry (clear area)";
            Trigger = new Trigger_Threshold(this, ThresholdMath.AccumulationOnlyOps, 3000);
            ConfigureParentFilter();
            if (type == JobType.Logging)
            {
                foreach (var td in AllPlants) SetAllowed(td, true);
                Trigger.ThresholdFilter.SetAllow(ThingDefOf.WoodLog, true);
            }
        }

        public override string Kind => Type == JobType.Logging ? "Forestry" : "ForestryClear";
        public override WorkTypeDef? WorkType => WorkTypeDefOf.PlantCutting;
        public override DesignationDef? DesignationDef => Type == JobType.Logging ? DesignationDefOf.HarvestPlant : DesignationDefOf.CutPlant;

        public IEnumerable<ThingDef> AllPlants => PlantUtility.GetForestryPlants(Map, Type == JobType.ClearArea);

        public void SetAllowed(ThingDef plant, bool allow)
        {
            if (allow) AllowedTrees.Add(plant); else AllowedTrees.Remove(plant);
            if (Type == JobType.Logging && plant.plant?.harvestedThingDef != null)
                Trigger.ThresholdFilter.SetAllow(plant.plant.harvestedThingDef, AllowedTrees.Any(t => t.plant?.harvestedThingDef == plant.plant.harvestedThingDef));
        }

        private void ConfigureParentFilter()
        {
            Trigger.SetParentFilter(f =>
            {
                foreach (var td in AllPlants)
                    if (td.plant?.harvestedThingDef != null) f.SetAllow(td.plant.harvestedThingDef, true);
            });
        }

        // ── Counting ──────────────────────────────────────────────────────────

        /// <summary>Pending yield of our own designations plus adopted external ones.</summary>
        private int CurrentDesignatedYield()
        {
            int count = 0;
            foreach (var des in CountedDesignations)
                if (des.target.HasThing && des.target.Thing is Plant plant && plant.Spawned)
                    count += plant.YieldNow();
            return count;
        }

        // ── Gather ────────────────────────────────────────────────────────────

        public override object? Gather()
        {
            if (Type == JobType.Logging && !WantsWork)
            {
                if (!_completed)
                {
                    _completed = true;
                    Note("target met — releasing designations");
                    return new WorkData { Kind = WorkKind.CleanUp };
                }
                return null;
            }
            _completed = false;

            CleanDeadDesignations();
            var data = new WorkData();
            if (Type == JobType.Logging) PlanLogging(data);
            else if (ClearAreas.Count > 0) { data.Kind = WorkKind.ClearArea; PlanClearAreas(data); }
            return data;
        }

        private void PlanLogging(WorkData data)
        {
            data.AreaDesignationsToRemove.AddRange(PlanAreaCleanupDesignations());
            AddRelevantGameDesignations();

            int count = Trigger.GetCurrentCount() + CurrentDesignatedYield();
            var directive = DirectiveFor(count);
            if (directive != Directive.Increase || _designations.Count > StewardMod.Settings.stock.MaxDesignationsPerJob)
            {
                data.Kind = WorkKind.ReduceDesignations;
                PlanReduceDesignations(ref count, data);
                return;
            }

            data.Kind = WorkKind.AddDesignations;
            PlanAddDesignations(ref count, data);
        }

        private void PlanReduceDesignations(ref int count, WorkData data)
        {
            var sorted = GetThingsSorted(
                _designations.Where(d => d.target.HasThing && d.target.Thing is Plant).Select(d => (Plant)d.target.Thing),
                _ => true,
                (p, dist) => -p.YieldNow() / dist);
            var sortedDesignations = sorted.Select(p => _designations.First(d => d.target.Thing == p)).ToList();
            var yields = sorted.Select(p => p.YieldNow()).ToList();
            int max = StewardMod.Settings.stock.MaxDesignationsPerJob;
            int removeCount = PlantMath.ComputeReduceCount(count, yields, _designations.Count, CountMeetsTarget, n => n > max);
            for (int i = 0; i < removeCount; i++)
            {
                count -= yields[i];
                data.DesignationsToRemove.Add((sortedDesignations[i], yields[i], count));
            }
        }

        private void PlanAddDesignations(ref int count, WorkData data)
        {
            if (!CanAddMoreDesignations()) { Note("designation cap reached"); return; }

            var trees = GetThingsSorted(Map.listerThings.AllThings.OfType<Plant>(), IsValidUndesignatedTarget, (p, dist) => p.YieldNow() / dist);
            if (trees.Count == 0)
            {
                RunsWithoutTargets++;
                Note($"no valid trees (count {count} / target {EffectiveTarget})");
                return;
            }
            RunsWithoutTargets = 0;

            var yields = trees.Select(t => t.YieldNow()).ToList();
            int max = StewardMod.Settings.stock.MaxDesignationsPerJob;
            int designateCount = PlantMath.ComputeNumberToDesignate(count, yields, _designations.Count, CountMeetsTarget, n => n < max);
            for (int i = 0; i < designateCount; i++)
            {
                count += yields[i];
                data.DesignationsToAdd.Add((trees[i], yields[i], count));
            }
        }

        private void PlanClearAreas(WorkData data)
        {
            var dm = Map.designationManager;
            foreach (var area in ClearAreas)
            {
                foreach (var cell in area.ActiveCells)
                {
                    var plant = cell.GetPlant(Map);
                    if (plant == null || dm.AllDesignationsOn(plant).Any()) continue;
                    if (!AllowedTrees.Contains(plant.def)) continue;
                    if (PlantUtility.InGrowingZoneOrPot(Map, cell)) continue;
                    data.ClearAreaDesignationsToAdd.Add((plant, area));
                }
            }
        }

        private List<Designation> PlanAreaCleanupDesignations()
        {
            var toRemove = new List<Designation>();
            if (LoggingArea == null) return toRemove;
            foreach (var des in _designations)
            {
                bool inArea = des.target.HasThing && ProductCounter.IsInAllowedArea(LoggingArea, des.target.Thing.Position, InvertLoggingArea);
                if (!des.target.HasThing || !inArea) toRemove.Add(des);
            }
            return toRemove;
        }

        /// <summary>Count player-made harvest designations on allowed trees toward the target (never released by the job).</summary>
        private void AddRelevantGameDesignations()
        {
            RebuildAdopted((DesignationDefOf.HarvestPlant, des => des.target.HasThing && des.target.Thing is Plant plant && IsValidDesignatedTarget(plant)));
        }

        private bool IsValidUndesignatedTarget(Plant target) =>
            target.def.plant != null
            && target.Map == Map
            && AllowedTrees.Contains(target.def)
            && target.Spawned
            && Map.designationManager.DesignationOn(target) == null
            && ((AllowSaplings && target.YieldNow() > 1) || target.LifeStage == PlantLifeStage.Mature)
            && !PlantUtility.InGrowingZoneOrPot(Map, target.Position)
            && ProductCounter.IsInAllowedArea(LoggingArea, target.Position, InvertLoggingArea)
            && IsReachable(target, PathEndMode.Touch);

        private bool IsValidDesignatedTarget(Plant target) =>
            target.def.plant != null && target.Map == Map && AllowedTrees.Contains(target.def) && target.Spawned
            && ProductCounter.IsInAllowedArea(LoggingArea, target.Position, InvertLoggingArea);

        // ── Execute ───────────────────────────────────────────────────────────

        public override bool Execute(object dataObj)
        {
            var data = (WorkData)dataObj;
            bool workDone = false;
            switch (data.Kind)
            {
                case WorkKind.None: return false;
                case WorkKind.CleanUp: CleanUp(); return false;
                case WorkKind.ClearArea:
                    foreach (var (plant, area) in data.ClearAreaDesignationsToAdd)
                    {
                        if (plant.DestroyedOrNull() || Map.designationManager.AllDesignationsOn(plant).Any()) continue;
                        AddDesignation(new Designation(plant, DesignationDefOf.CutPlant));
                        workDone = true;
                    }
                    if (workDone) Note($"clear-area: designated {data.ClearAreaDesignationsToAdd.Count} plants");
                    return workDone;
            }

            foreach (var des in data.AreaDesignationsToRemove) { des.Delete(); _designations.Remove(des); }

            if (data.Kind == WorkKind.ReduceDesignations)
            {
                int removed = 0;
                foreach (var (des, _, _) in data.DesignationsToRemove)
                {
                    des.Delete(); _designations.Remove(des); removed++;
                }
                if (removed > 0) { Note($"released {removed} designations (count {Trigger.GetCurrentCount()} / {EffectiveTarget})"); workDone = true; }
            }
            else
            {
                int added = 0;
                foreach (var (tree, _, _) in data.DesignationsToAdd)
                {
                    if (tree.DestroyedOrNull()) continue;
                    AddDesignation(new Designation(tree, DesignationDefOf.HarvestPlant));
                    added++;
                }
                if (added > 0) { Note($"designated {added} trees (count {Trigger.GetCurrentCount()} + pending → target {EffectiveTarget})"); workDone = true; }
            }
            return workDone;
        }

        public override void CleanUp()
        {
            CleanDeadDesignations();
            base.CleanUp();
        }

        public override void Notify_AreaRemoved(Area area)
        {
            if (LoggingArea == area) LoggingArea = null;
            ClearAreas.Remove(area);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref Type, "type", JobType.Logging);
            Scribe_Values.Look(ref AllowSaplings, "allowSaplings");
            Scribe_Values.Look(ref InvertLoggingArea, "invertLoggingArea");
            Scribe_Values.Look(ref _completed, "completed");
            ScribeAreaByLabel(ref LoggingArea, ref _loggingAreaScribe, "loggingArea", Map);
            Scribe_Collections.Look(ref AllowedTrees, "allowedTrees", LookMode.Def);
            if (Scribe.mode == LoadSaveMode.Saving) _clearAreasScribe = ClearAreas.Select(a => a.Label).ToList();
            Scribe_Collections.Look(ref _clearAreasScribe, "clearAreas", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                AllowedTrees ??= new HashSet<ThingDef>();
                ClearAreas = new List<Area>();
                if (_clearAreasScribe != null && Map != null)
                    foreach (var label in _clearAreasScribe)
                    {
                        var a = Map.areaManager.GetLabeled(label);
                        if (a != null) ClearAreas.Add(a);
                    }
                ConfigureParentFilter();
            }
        }
    }
}
