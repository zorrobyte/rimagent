// Derived from Colony Manager Redux ManagerJobs/ManagerJob_Foraging.cs (MIT, see THIRD_PARTY_NOTICES.md); synchronous rewrite.
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimBridge.Steward.Stock
{
    /// <summary>
    /// Keeps wild plant products (berries, herbal medicine, other non-wood harvests) at target by designating
    /// wild, allowed plants for harvest, best yield-per-distance first. Plants in growing zones or pots are
    /// left alone so the job never competes with the colony's own crops.
    /// Structure mirrors StockJob_Forestry: Gather() → WorkData → Execute().
    /// </summary>
    public class StockJob_Foraging : StockJob
    {
        public enum WorkKind { None, CleanUp, ReduceDesignations, AddDesignations }

        public sealed class WorkData
        {
            public WorkKind Kind;
            public List<Designation> AreaDesignationsToRemove = new List<Designation>();
            public List<(Designation designation, int yield, int countAfter)> DesignationsToRemove = new List<(Designation, int, int)>();
            public List<(Plant plant, int yield, int countAfter)> DesignationsToAdd = new List<(Plant, int, int)>();
        }

        public HashSet<ThingDef> AllowedPlants = new HashSet<ThingDef>();
        public Area? ForagingArea;
        public bool InvertForagingArea;
        /// <summary>When true only fully mature plants are designated; otherwise anything yielding more than 1 now counts.</summary>
        public bool ForceFullyMature;
        private string? _foragingAreaScribe;
        private bool _completed;

        public StockJob_Foraging() { }

        public StockJob_Foraging(Map map) : base(map)
        {
            Label = "Foraging (berries, herbal medicine)";
            Trigger = new Trigger_Threshold(this, ThresholdMath.AccumulationOnlyOps, 3000);
            ConfigureParentFilter();
            foreach (var td in AllPlants) SetAllowed(td, true);
        }

        public override string Kind => "Foraging";
        public override WorkTypeDef? WorkType => WorkTypeDefOf.Growing;
        public override DesignationDef? DesignationDef => DesignationDefOf.HarvestPlant;
        public override IEnumerable<string> Targets => AllowedPlants.Select(p => p.label);

        public IEnumerable<ThingDef> AllPlants => PlantUtility.GetForagingPlants(Map);

        /// <summary>Allow/disallow a plant; keeps the threshold filter in sync with the products of the allowed set.</summary>
        public void SetAllowed(ThingDef plant, bool allow)
        {
            if (allow) AllowedPlants.Add(plant); else AllowedPlants.Remove(plant);
            var product = plant.plant?.harvestedThingDef;
            if (product == null) return;
            // Several plants may share one product (e.g. berries): keep the product allowed while any of them is.
            bool stayAllowed = AllowedPlants.Any(p => p.plant?.harvestedThingDef == product);
            Trigger.ThresholdFilter.SetAllow(product, stayAllowed);
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
            if (!WantsWork)
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
            AddRelevantGameDesignations();

            var data = new WorkData();
            data.AreaDesignationsToRemove.AddRange(PlanAreaCleanupDesignations());

            int count = Trigger.GetCurrentCount() + CurrentDesignatedYield();
            var directive = DirectiveFor(count);
            if (directive != Directive.Increase || _designations.Count > StewardMod.Settings.stock.MaxDesignationsPerJob)
            {
                data.Kind = WorkKind.ReduceDesignations;
                PlanReduceDesignations(ref count, data);
                return data;
            }

            data.Kind = WorkKind.AddDesignations;
            PlanAddDesignations(ref count, data);
            return data;
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

            var plants = GetThingsSorted(Map.listerThings.AllThings.OfType<Plant>(), IsValidUndesignatedTarget, (p, dist) => p.YieldNow() / dist);
            if (plants.Count == 0)
            {
                RunsWithoutTargets++;
                Note($"no valid wild plants (count {count} / target {EffectiveTarget})");
                return;
            }
            RunsWithoutTargets = 0;

            var yields = plants.Select(p => p.YieldNow()).ToList();
            int max = StewardMod.Settings.stock.MaxDesignationsPerJob;
            int designateCount = PlantMath.ComputeNumberToDesignate(count, yields, _designations.Count, CountMeetsTarget, n => n < max);
            for (int i = 0; i < designateCount; i++)
            {
                count += yields[i];
                data.DesignationsToAdd.Add((plants[i], yields[i], count));
            }
        }

        private static bool ShouldRemoveForAreaCleanup(bool hasThing, bool inAllowedArea) => !hasThing || !inAllowedArea;

        private List<Designation> PlanAreaCleanupDesignations()
        {
            var toRemove = new List<Designation>();
            foreach (var des in _designations)
            {
                bool hasThing = des.target.HasThing;
                bool inArea = hasThing && ProductCounter.IsInAllowedArea(ForagingArea, des.target.Thing.Position, InvertForagingArea);
                if (ShouldRemoveForAreaCleanup(hasThing, inArea)) toRemove.Add(des);
            }
            return toRemove;
        }

        /// <summary>Count player-made harvest designations on allowed wild plants toward the target (never released by the job).</summary>
        private void AddRelevantGameDesignations()
        {
            RebuildAdopted((DesignationDefOf.HarvestPlant, des => des.target.HasThing && des.target.Thing is Plant plant && IsValidDesignatedTarget(plant)));
        }

        private bool IsValidUndesignatedTarget(Plant target) =>
            target.def.plant != null
            && target.Map == Map
            && AllowedPlants.Contains(target.def)
            && target.Spawned
            && Map.designationManager.DesignationOn(target) == null
            // harvest only mature plants, or non-mature ones that already yield something right now.
            && ((!ForceFullyMature && target.YieldNow() > 1) || target.LifeStage == PlantLifeStage.Mature)
            && !target.sown
            && !PlantUtility.InGrowingZoneOrPot(Map, target.Position)
            && ProductCounter.IsInAllowedArea(ForagingArea, target.Position, InvertForagingArea)
            && IsReachable(target, PathEndMode.Touch);

        private bool IsValidDesignatedTarget(Plant target) =>
            target.def.plant != null
            && target.Map == Map
            && AllowedPlants.Contains(target.def)
            && target.Spawned
            && !PlantUtility.InGrowingZoneOrPot(Map, target.Position)
            && ProductCounter.IsInAllowedArea(ForagingArea, target.Position, InvertForagingArea);

        // ── Execute ───────────────────────────────────────────────────────────

        public override bool Execute(object dataObj)
        {
            var data = (WorkData)dataObj;
            bool workDone = false;
            switch (data.Kind)
            {
                case WorkKind.None: return false;
                case WorkKind.CleanUp: CleanUp(); return false;
            }

            ExecuteAreaCleanupDesignations(data);

            if (data.Kind == WorkKind.ReduceDesignations)
            {
                int removed = 0;
                foreach (var (des, _, _) in data.DesignationsToRemove)
                {
                    des.Delete(); _designations.Remove(des); removed++;
                }
                if (removed > 0) { Note($"released {removed} designations (count {Trigger.GetCurrentCount()} / {EffectiveTarget})"); workDone = true; }
                else Note("targets already satisfied");
            }
            else
            {
                int added = 0;
                foreach (var (plant, _, _) in data.DesignationsToAdd)
                {
                    // Re-validate: the plant may have been destroyed, harvested, or designated by the player since Gather().
                    if (plant.DestroyedOrNull() || !plant.Spawned || Map.designationManager.DesignationOn(plant) != null) continue;
                    AddDesignation(new Designation(plant, DesignationDefOf.HarvestPlant));
                    added++;
                }
                if (added > 0) { Note($"designated {added} plants (count {Trigger.GetCurrentCount()} + pending → target {EffectiveTarget})"); workDone = true; }
            }
            return workDone;
        }

        private void ExecuteAreaCleanupDesignations(WorkData data)
        {
            int missing = 0, outsideArea = 0;
            foreach (var des in data.AreaDesignationsToRemove)
            {
                bool hasThing = des.target.HasThing;
                bool inArea = hasThing && ProductCounter.IsInAllowedArea(ForagingArea, des.target.Thing.Position, InvertForagingArea);
                if (!ShouldRemoveForAreaCleanup(hasThing, inArea)) continue;
                if (!hasThing) missing++; else outsideArea++;
                des.Delete();
                _designations.Remove(des);
            }
            if (missing + outsideArea > 0)
                Note($"cleaned {missing + outsideArea} designations ({missing} missing target, {outsideArea} outside foraging area)");
        }

        public override void CleanUp()
        {
            CleanDeadDesignations();
            base.CleanUp();
        }

        public override void Notify_AreaRemoved(Area area)
        {
            if (ForagingArea == area) ForagingArea = null;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref ForceFullyMature, "forceFullyMature");
            Scribe_Values.Look(ref InvertForagingArea, "invertForagingArea");
            Scribe_Values.Look(ref _completed, "completed");
            ScribeAreaByLabel(ref ForagingArea, ref _foragingAreaScribe, "foragingArea", Map);
            Scribe_Collections.Look(ref AllowedPlants, "allowedPlants", LookMode.Def);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                AllowedPlants ??= new HashSet<ThingDef>();
                AllowedPlants.RemoveWhere(p => p == null);
                ConfigureParentFilter();
            }
        }
    }

    public static partial class StockJobFactories
    {
        static partial void AddForagingImpl(StockComponent comp, int colonists)
        {
            var job = comp.Add(new StockJob_Foraging(comp.map));
            job.Trigger.TargetCount = DefaultStockPlan.ForagingTarget(colonists);
        }
    }
}
