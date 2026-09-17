// Derived from Colony Manager Redux ManagerJobs/ManagerJob_Hunting.cs and Helpers/Utilities/Utilities_Hunting.cs (MIT, see THIRD_PARTY_NOTICES.md); synchronous rewrite.
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimBridge.Steward.Stock
{
    /// <summary>
    /// Keeps meat (or leather) at target by designating wild animals for hunting, best expected
    /// yield-per-distance first. Counts stock + fresh corpses + pending designations, releases
    /// designations once satisfied, adopts player-made Hunt designations on allowed kinds and
    /// (optionally) unforbids fresh corpses of allowed animals. Steward addition: dangerous
    /// animals (predators, high manhunter chance, very large) are skipped unless the
    /// <c>HuntPredators</c> setting is on.
    /// </summary>
    public class StockJob_Hunting : StockJob
    {
        public enum HuntingTargetResource { Meat, Leather }

        public enum WorkKind { None, CleanUp, ReduceDesignations, AddDesignations }

        public sealed class WorkData
        {
            public WorkKind Kind;
            public List<Designation> AreaDesignationsToRemove = new List<Designation>();
            public List<(Designation designation, int yield, int countAfter)> DesignationsToRemove = new List<(Designation, int, int)>();
            public List<(Corpse corpse, int yield, int countAfter)> CorpsesToUnforbid = new List<(Corpse, int, int)>();
            public List<(Pawn target, int yield, int countAfter)> DesignationsToAdd = new List<(Pawn, int, int)>();
        }

        /// <summary>Predators / manhunter-prone / huge animals need the HuntPredators setting.</summary>
        public const float DangerousManhunterChance = 0.2f;
        public const float DangerousBodySize = 2.5f;

        public HuntingTargetResource TargetResource = HuntingTargetResource.Meat;
        public HashSet<PawnKindDef> AllowedAnimals = new HashSet<PawnKindDef>();
        public Area? HuntingGrounds;
        public bool InvertHuntingGrounds;
        public bool UnforbidCorpses = true;
        private string? _huntingGroundsScribe;
        private List<PawnKindDef>? _allAnimals;
        private bool _completed;

        public StockJob_Hunting() { }

        public StockJob_Hunting(Map map, HuntingTargetResource resource = HuntingTargetResource.Meat) : base(map)
        {
            TargetResource = resource;
            Label = resource == HuntingTargetResource.Meat ? "Hunting (meat)" : "Hunting (leather)";
            Trigger = new Trigger_Threshold(this, ThresholdMath.AccumulationOnlyOps, 3000);
            ConfigureParentFilter();
            ConfigureDefaultThresholdFilter();
            foreach (var kind in AllAnimals)
                if (HuntingUtility.IsAllowedByDangerRule(kind)) SetAllowed(kind, true);
        }

        public override string Kind => TargetResource == HuntingTargetResource.Meat ? "Hunting" : "HuntingLeather";
        public override WorkTypeDef? WorkType => WorkTypeDefOf.Hunting;
        public override DesignationDef? DesignationDef => DesignationDefOf.Hunt;
        public override IEnumerable<string> Targets => AllowedAnimals.Select(pk => pk.LabelCap.Resolve());

        /// <summary>Every animal kind known on this map: biome wild animals, spawned animals and corpses.</summary>
        public List<PawnKindDef> AllAnimals
        {
            get
            {
                if (_allAnimals == null) _allAnimals = HuntingUtility.GetMapPawnKindDefs(Map).ToList();
                return _allAnimals;
            }
        }

        public void RefreshAllAnimals()
        {
            _allAnimals = null;
            ConfigureParentFilter();
        }

        public void SetAllowed(PawnKindDef animal, bool allow)
        {
            if (allow) AllowedAnimals.Add(animal); else AllowedAnimals.Remove(animal);
            var resource = ResourceDef(animal);
            if (resource == null) return;
            // Keep the resource counted while any allowed animal still yields it.
            bool stayAllowed = AllowedAnimals.Any(a => ResourceDef(a) == resource);
            Trigger.ThresholdFilter.SetAllow(resource, stayAllowed);
        }

        private ThingDef? ResourceDef(PawnKindDef kind) =>
            HuntingUtility.SelectResourceDef(TargetResource, kind.RaceProps?.meatDef, kind.RaceProps?.leatherDef);

        private void ConfigureParentFilter()
        {
            Trigger.SetParentFilter(f =>
            {
                if (TargetResource == HuntingTargetResource.Meat)
                {
                    f.SetAllow(ThingCategoryDefOf.MeatRaw, true);
                }
                else
                {
                    foreach (var kind in AllAnimals)
                    {
                        var leather = kind.RaceProps?.leatherDef;
                        if (leather != null) f.SetAllow(leather, true);
                    }
                    var leathers = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("Leathers");
                    if (leathers != null) f.SetAllow(leathers, true);
                }
            });
        }

        /// <summary>
        /// Default threshold: all raw meat except humanlike (and twisted) meat, so meat from trade or
        /// off-map animals counts toward the target; for leather, the leather of the map's animals
        /// (extended per SetAllowed).
        /// </summary>
        private void ConfigureDefaultThresholdFilter()
        {
            if (TargetResource != HuntingTargetResource.Meat) return;
            var filter = Trigger.ThresholdFilter;
            filter.SetAllow(ThingCategoryDefOf.MeatRaw, true);
            foreach (var def in HuntingUtility.HumanLikeMeatDefs) filter.SetAllow(def, false);
            var twisted = DefDatabase<ThingDef>.GetNamedSilentFail("Meat_Twisted");
            if (twisted != null) filter.SetAllow(twisted, false);
        }

        // ── Counting ──────────────────────────────────────────────────────────

        /// <summary>Corpses of allowed animals (never humanlike) inside the hunting grounds.</summary>
        public IEnumerable<Corpse> Corpses
        {
            get
            {
                var things = Map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse);
                for (int i = 0; i < things.Count; i++)
                {
                    if (!(things[i] is Corpse corpse) || corpse.InnerPawn == null) continue;
                    if (!ProductCounter.IsInAllowedArea(HuntingGrounds, corpse.Position, InvertHuntingGrounds)) continue;
                    if (!HuntingUtility.ShouldUnforbidCorpse(AllowedAnimals.Contains(corpse.InnerPawn.kindDef), corpse.InnerPawn.RaceProps.Humanlike)) continue;
                    yield return corpse;
                }
            }
        }

        /// <summary>Expected yield in fresh, unforbidden, colonist-reachable, unburied corpses.</summary>
        private int GetResourceInCorpses()
        {
            int count = 0;
            foreach (var corpse in Corpses)
            {
                if (!IsCountedResource(corpse)) continue;
                if (corpse.IsForbidden(Faction.OfPlayer)) continue;
                if (!Map.reachability.CanReachColony(corpse.Position)) continue;

                // Sarcophagus inherits grave, so no separate check needed.
                var slotGroup = Map.haulDestinationManager.SlotGroupAt(corpse.Position);
                if (slotGroup?.parent is Building_Storage storage && storage.def == ThingDefOf.Grave) continue;

                if (corpse.IsNotFresh()) continue;
                count += HuntingUtility.EstimatedYield(corpse, TargetResource);
            }
            return count;
        }

        /// <summary>Expected yield of our own hunt designations plus adopted external ones.</summary>
        private int GetResourceInDesignations()
        {
            int count = 0;
            foreach (var des in CountedDesignations)
                if (des.target.HasThing && des.target.Thing is Pawn target && !target.Dead)
                    count += HuntingUtility.EstimatedYield(target, TargetResource);
            return count;
        }

        /// <summary>Stock + corpses + designations: what the count will be once pending work lands.</summary>
        public int ExpectedAdditionalCount => GetResourceInCorpses() + GetResourceInDesignations();

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
            var data = new WorkData();
            data.AreaDesignationsToRemove.AddRange(PlanAreaCleanupDesignations());
            AddRelevantGameDesignations();

            int count = Trigger.GetCurrentCount() + GetResourceInCorpses() + GetResourceInDesignations();
            var directive = DirectiveFor(count);
            if (directive != Directive.Increase || _designations.Count > StewardMod.Settings.stock.MaxDesignationsPerJob)
            {
                data.Kind = WorkKind.ReduceDesignations;
                PlanReduceDesignations(ref count, data);
                return data;
            }

            data.Kind = WorkKind.AddDesignations;

            if (UnforbidCorpses)
            {
                PlanUnforbidCorpses(ref count, data);
                if (data.CorpsesToUnforbid.Count > 0 && CountMeetsTarget(count)) return data;
            }

            PlanAddDesignations(ref count, data);
            return data;
        }

        private List<Designation> PlanAreaCleanupDesignations()
        {
            var toRemove = new List<Designation>();
            foreach (var des in _designations)
            {
                bool inArea = des.target.HasThing && ProductCounter.IsInAllowedArea(HuntingGrounds, des.target.Thing.Position, InvertHuntingGrounds);
                if (HuntingUtility.ShouldRemoveForAreaCleanup(des.target.HasThing, inArea)) toRemove.Add(des);
            }
            return toRemove;
        }

        private void PlanReduceDesignations(ref int count, WorkData data)
        {
            var sorted = GetThingsSorted(
                _designations.Where(d => d.target.HasThing && d.target.Thing is Pawn).Select(d => (Pawn)d.target.Thing),
                _ => true,
                (p, dist) => -HuntingUtility.EstimatedYield(p, TargetResource) / dist);
            var sortedDesignations = sorted.Select(p => _designations.First(d => d.target.Thing == p)).ToList();
            var yields = sorted.Select(p => HuntingUtility.EstimatedYield(p, TargetResource)).ToList();
            int max = StewardMod.Settings.stock.MaxDesignationsPerJob;
            int removeCount = PlantMath.ComputeReduceCount(count, yields, _designations.Count, CountMeetsTarget, n => n > max);
            for (int i = 0; i < removeCount; i++)
            {
                count -= yields[i];
                data.DesignationsToRemove.Add((sortedDesignations[i], yields[i], count));
            }
        }

        // originally copypasta from autohuntbeacon by Carry
        // https://ludeon.com/forums/index.php?topic=8930.0
        private void PlanUnforbidCorpses(ref int count, WorkData data)
        {
            foreach (var corpse in Corpses)
            {
                if (CountMeetsTarget(count)) return;
                // Corpses in storage are assumed to be intentionally forbidden.
                if (corpse.IsInAnyStorage() || !corpse.IsForbidden(Faction.OfPlayer)) continue;
                if (corpse.IsNotFresh()) continue;
                if (!Map.reachability.CanReachColony(corpse.Position)) continue;
                int yield = HuntingUtility.EstimatedYield(corpse, TargetResource);
                count += yield;
                data.CorpsesToUnforbid.Add((corpse, yield, count));
            }
        }

        private void PlanAddDesignations(ref int count, WorkData data)
        {
            if (!CanAddMoreDesignations()) { Note("designation cap reached"); return; }

            // value = yield / distance
            var animals = GetThingsSorted(
                Map.mapPawns.AllPawnsSpawned.ToList(),
                IsValidUndesignatedHuntingTarget,
                (p, dist) => HuntingUtility.EstimatedYield(p, TargetResource) / dist);
            if (animals.Count == 0)
            {
                RunsWithoutTargets++;
                Note($"no valid animals (count {count} / target {EffectiveTarget})");
                return;
            }
            RunsWithoutTargets = 0;

            var yields = animals.Select(a => HuntingUtility.EstimatedYield(a, TargetResource)).ToList();
            int max = StewardMod.Settings.stock.MaxDesignationsPerJob;
            int designateCount = PlantMath.ComputeNumberToDesignate(count, yields, _designations.Count, CountMeetsTarget, n => n < max);
            for (int i = 0; i < designateCount; i++)
            {
                count += yields[i];
                data.DesignationsToAdd.Add((animals[i], yields[i], count));
            }
        }

        /// <summary>Count player-made hunt designations on allowed kinds toward the target (never released by the job).</summary>
        private void AddRelevantGameDesignations()
        {
            RebuildAdopted((DesignationDefOf.Hunt, des => des.target.HasThing && des.target.Thing is Pawn pawn && IsValidDesignatedHuntingTarget(pawn)));
        }

        private bool IsValidUndesignatedHuntingTarget(Pawn target) =>
            target.RaceProps != null
            && target.RaceProps.Animal
            && target.Map == Map
            && !target.Dead
            && AllowedAnimals.Contains(target.kindDef)
            && target.Spawned
            && Map.designationManager.DesignationOn(target) == null
            && IsWild(target)
            && IsSafeToHunt(target)
            && ProductCounter.IsInAllowedArea(HuntingGrounds, target.Position, InvertHuntingGrounds)
            && IsReachable(target, PathEndMode.Touch);

        private bool IsValidDesignatedHuntingTarget(Pawn target) =>
            target.RaceProps != null
            && target.RaceProps.Animal
            && target.Map == Map
            && !target.Dead
            && AllowedAnimals.Contains(target.kindDef)
            && target.Spawned
            && IsWild(target)
            && ProductCounter.IsInAllowedArea(HuntingGrounds, target.Position, InvertHuntingGrounds);

        /// <summary>Wild only: never player-owned, tamed or bonded animals.</summary>
        private static bool IsWild(Pawn target)
        {
            if (target.Faction != null && target.Faction.IsPlayer) return false;
            if (target.playerSettings?.Master != null) return false;
            if (target.relations != null && target.relations.DirectRelations.Any(r => r.def == PawnRelationDefOf.Bond)) return false;
            return true;
        }

        /// <summary>Steward safety: skip predators, manhunter-prone, huge or already-aggressive animals unless allowed.</summary>
        private static bool IsSafeToHunt(Pawn target)
        {
            if (target.InAggroMentalState) return false;
            if (!HuntSafety.MayHunt(target.Map, target.kindDef)) return false;
            return true;
        }

        private bool IsCountedResource(PawnKindDef kind)
        {
            var resource = ResourceDef(kind);
            return resource != null && Trigger.ThresholdFilter.Allows(resource);
        }

        private bool IsCountedResource(Corpse corpse) => corpse.InnerPawn != null && IsCountedResource(corpse.InnerPawn.kindDef);

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
                    var animal = des.target.Thing as Pawn;
                    des.Delete();
                    _designations.Remove(des);
                    if (!animal.DestroyedOrNull()) removed++;
                }
                if (removed > 0) { Note($"released {removed} hunt designations (count {Trigger.GetCurrentCount()} / {EffectiveTarget})"); workDone = true; }
                else Note("targets already satisfied");
                return workDone;
            }

            int unforbidden = 0;
            foreach (var (corpse, _, _) in data.CorpsesToUnforbid)
            {
                if (corpse.DestroyedOrNull() || corpse.IsInAnyStorage() || !corpse.IsForbidden(Faction.OfPlayer)) continue;
                corpse.SetForbidden(false, false);
                unforbidden++;
            }
            if (unforbidden > 0) { Note($"unforbade {unforbidden} corpses"); workDone = true; }

            int added = 0;
            foreach (var (animal, _, _) in data.DesignationsToAdd)
            {
                if (animal.DestroyedOrNull() || !animal.Spawned || animal.Dead) continue;
                if (Map.designationManager.DesignationOn(animal) != null) continue;
                AddDesignation(new Designation(animal, DesignationDefOf.Hunt));
                added++;
            }
            if (added > 0) { Note($"designated {added} animals (count {Trigger.GetCurrentCount()} + pending → target {EffectiveTarget})"); workDone = true; }
            return workDone;
        }

        private void ExecuteAreaCleanupDesignations(WorkData data)
        {
            int missing = 0, outside = 0;
            foreach (var des in data.AreaDesignationsToRemove)
            {
                bool hasThing = des.target.HasThing;
                bool inArea = hasThing && ProductCounter.IsInAllowedArea(HuntingGrounds, des.target.Thing.Position, InvertHuntingGrounds);
                if (!HuntingUtility.ShouldRemoveForAreaCleanup(hasThing, inArea)) continue;
                if (!hasThing) missing++; else outside++;
                des.Delete();
                _designations.Remove(des);
            }
            if (missing + outside > 0) Note($"cleaned {missing + outside} designations ({missing} gone, {outside} outside hunting grounds)");
        }

        public override void CleanUp()
        {
            CleanDeadDesignations();
            base.CleanUp();
        }

        public override void Notify_AreaRemoved(Area area)
        {
            if (HuntingGrounds == area) HuntingGrounds = null;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref TargetResource, "targetResource", HuntingTargetResource.Meat);
            Scribe_Collections.Look(ref AllowedAnimals, "allowedAnimals", LookMode.Def);
            Scribe_Values.Look(ref InvertHuntingGrounds, "invertHuntingGrounds");
            Scribe_Values.Look(ref UnforbidCorpses, "unforbidCorpses", true);
            Scribe_Values.Look(ref _completed, "completed");
            ScribeAreaByLabel(ref HuntingGrounds, ref _huntingGroundsScribe, "huntingGrounds", Map);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                AllowedAnimals ??= new HashSet<PawnKindDef>();
                AllowedAnimals.RemoveWhere(a => a == null);
                ConfigureParentFilter();
            }
        }
    }

    /// <summary>Hunting helpers: yield estimates, map animal kinds, corpse/area rules.</summary>
    public static class HuntingUtility
    {
        private static List<ThingDef>? _humanLikeMeatDefs;

        public static int EstimatedMeatCount(PawnKindDef kind) => (int)kind.race.GetStatValueAbstract(StatDefOf.MeatAmount);
        public static int EstimatedMeatCount(Pawn p) => (int)p.GetStatValue(StatDefOf.MeatAmount);
        public static int EstimatedMeatCount(Corpse c) => c.InnerPawn != null ? EstimatedMeatCount(c.InnerPawn) : 0;

        public static int EstimatedLeatherCount(PawnKindDef kind) => (int)kind.race.GetStatValueAbstract(StatDefOf.LeatherAmount);
        public static int EstimatedLeatherCount(Pawn p) => (int)p.GetStatValue(StatDefOf.LeatherAmount);
        public static int EstimatedLeatherCount(Corpse c) => c.InnerPawn != null ? EstimatedLeatherCount(c.InnerPawn) : 0;

        public static int EstimatedYield(PawnKindDef kind, StockJob_Hunting.HuntingTargetResource resource) =>
            resource == StockJob_Hunting.HuntingTargetResource.Meat ? EstimatedMeatCount(kind) : EstimatedLeatherCount(kind);

        public static int EstimatedYield(Pawn p, StockJob_Hunting.HuntingTargetResource resource) =>
            resource == StockJob_Hunting.HuntingTargetResource.Meat ? EstimatedMeatCount(p) : EstimatedLeatherCount(p);

        public static int EstimatedYield(Corpse c, StockJob_Hunting.HuntingTargetResource resource) =>
            c.InnerPawn != null ? EstimatedYield(c.InnerPawn, resource) : 0;

        /// <summary>Meat or leather def for the given resource; null when the race yields none.</summary>
        public static T? SelectResourceDef<T>(StockJob_Hunting.HuntingTargetResource resource, T? meatDef, T? leatherDef) where T : class =>
            resource == StockJob_Hunting.HuntingTargetResource.Meat ? meatDef : leatherDef;

        /// <summary>Unforbid corpses of allowed animals only, and never humanlike corpses.</summary>
        public static bool ShouldUnforbidCorpse(bool isAllowedAnimal, bool isHumanlike) => isAllowedAnimal && !isHumanlike;

        public static bool ShouldRemoveForAreaCleanup(bool hasThing, bool inAllowedArea) => !hasThing || !inAllowedArea;

        /// <summary>False for predators, manhunter-prone and very large kinds unless the HuntPredators setting is on.</summary>
        public static bool IsAllowedByDangerRule(PawnKindDef kind)
            => HuntSafety.MayHunt(Find.CurrentMap ?? Find.AnyPlayerHomeMap, kind);

        /// <summary>Wild biome animals, spawned animals and animal corpses on the map, deduplicated and ordered by label.</summary>
        public static IEnumerable<PawnKindDef> GetMapPawnKindDefs(Map? map, bool animalsOnly = true)
        {
            if (map == null)
                return DefDatabase<PawnKindDef>.AllDefsListForReading.Where(pkd => !animalsOnly || (pkd.RaceProps?.Animal ?? false));

            var wild = GetWildAnimalsSafely(() => map.Biome.AllWildAnimals, map);
            var visible = map.mapPawns.AllPawnsSpawned
                .Where(p => (!animalsOnly || (p.RaceProps?.Animal ?? false)) && !(map.fogGrid?.IsFogged(p.Position) ?? true))
                .Select(p => p.kindDef);
            var corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse)
                .OfType<Corpse>()
                .Where(c => c.InnerPawn != null && (!animalsOnly || (c.InnerPawn.RaceProps?.Animal ?? false)))
                .Select(c => c.InnerPawn.kindDef);
            return CombineAndOrderPawnKindSources(wild, visible, corpses);
        }

        /// <summary>Map.Biome throws for maps whose tile is not yet in the world grid; treat as "no wild animals".</summary>
        public static IEnumerable<PawnKindDef> GetWildAnimalsSafely(Func<IEnumerable<PawnKindDef>> getWildAnimals, object? mapForLogging = null)
        {
            try
            {
                return getWildAnimals().ToList();
            }
            catch (Exception ex)
            {
                StewardLog.Warning($"stock: could not determine biome for map {mapForLogging} to get wild animals; skipping. {ex.GetType().Name}: {ex.Message}");
                return Enumerable.Empty<PawnKindDef>();
            }
        }

        public static IEnumerable<PawnKindDef> CombineAndOrderPawnKindSources(IEnumerable<PawnKindDef> wild, IEnumerable<PawnKindDef> visible, IEnumerable<PawnKindDef> corpses) =>
            wild.Concat(visible).Concat(corpses).Where(k => k != null).Distinct().OrderBy(pk => pk.label);

        /// <summary>Meat defs of humanlike flesh races (so they can be excluded from the meat count by default).</summary>
        public static List<ThingDef> HumanLikeMeatDefs
        {
            get
            {
                if (_humanLikeMeatDefs == null)
                {
                    _humanLikeMeatDefs = DefDatabase<ThingDef>.AllDefsListForReading
                        .Where(def => def.category == ThingCategory.Pawn
                            && def.race != null
                            && def.race.hasMeat
                            && def.race.Humanlike
                            && def.race.IsFlesh
                            && CheckAndReportIfInvalidMeatDef(def))
                        .Select(def => def.race.meatDef)
                        .Distinct()
                        .ToList();
                }
                return _humanLikeMeatDefs;
            }
        }

        private static bool CheckAndReportIfInvalidMeatDef(ThingDef def)
        {
            if (def.race.meatDef != null) return true;
            StewardLog.WarningOnce(
                $"stock: the race of {def} (from {def.modContentPack?.Name}) claims to have humanlike meat but its meatDef is null; it is probably missing hasMeat=false.",
                def.shortHash ^ 0x48554e54);
            return false;
        }
    }

    public static partial class StockJobFactories
    {
        static partial void AddHuntingImpl(StockComponent comp, int colonists)
        {
            var meat = comp.Add(new StockJob_Hunting(comp.map, StockJob_Hunting.HuntingTargetResource.Meat));
            meat.Label = "Hunting (meat)";
            meat.Trigger.TargetCount = DefaultStockPlan.MeatTarget(colonists);
            meat.UpdateIntervalTicks = 5000;

            var leather = comp.Add(new StockJob_Hunting(comp.map, StockJob_Hunting.HuntingTargetResource.Leather));
            leather.Label = "Hunting (leather)";
            leather.Trigger.TargetCount = DefaultStockPlan.LeatherTarget(colonists);
            leather.UpdateIntervalTicks = 5000;
        }
    }
}
