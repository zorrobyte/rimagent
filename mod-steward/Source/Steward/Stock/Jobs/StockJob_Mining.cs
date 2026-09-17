// Derived from Colony Manager Redux ManagerJobs/ManagerJob_Mining.cs and Helpers/Utilities/Utilities_Mining.cs (MIT, see THIRD_PARTY_NOTICES.md); synchronous rewrite.
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
    /// Keeps a mined resource (default steel) at target by, in order: hauling loose chunks that yield it,
    /// deconstructing ownerless buildings that refund it (off by default), and designating minerals whose
    /// expected yield is highest per distance. Safety checks: roof support, room division, thick roofs,
    /// fog, unopened ancient dangers. Owns three designation lists (mine, haul, deconstruct).
    /// </summary>
    public class StockJob_Mining : StockJob
    {
        public enum WorkKind { None, CleanUp, ReduceDesignations, AddDesignations }

        public sealed class WorkData
        {
            public WorkKind Kind;
            public List<(Designation designation, int yield, int countAfter)> MineDesignationsToRemove = new List<(Designation, int, int)>();
            public List<(Designation designation, int yield, int countAfter)> DeconstructDesignationsToRemove = new List<(Designation, int, int)>();
            public List<(Designation designation, int yield, int countAfter)> HaulDesignationsToRemove = new List<(Designation, int, int)>();
            public List<(Thing target, DesignationDef def, int amount, int countAfter)> DesignationsToAdd = new List<(Thing, DesignationDef, int, int)>();
        }

        private const int SafetyCacheTicks = 2000;
        private const int AncientDangerCacheTicks = 2500;

        public bool AllowMining = true;
        public bool HaulMapChunks = true;
        /// <summary>Off by default: automated dismantling of ruins is a safety decision for the player.</summary>
        public bool DeconstructBuildings;
        public bool CheckRoofSupport = true;
        /// <summary>When on (default), the roof check simulates removal; when off, a pillar grid is left standing.</summary>
        public bool CheckRoofSupportAdvanced = true;
        public bool CheckRoomDivision = true;
        public bool MineThickRoofs = StewardMod.Settings?.stock?.MineThickRoofs ?? false;
        /// <summary>Count player-made mine designations toward the target (they are never released by the job).</summary>
        public bool TakeOwnershipOfMiningJobs;
        public Area? MiningArea;
        public bool InvertMiningArea;

        private List<Designation> _haulDesignations = new List<Designation>();
        private List<Designation> _deconstructDesignations = new List<Designation>();
        private string? _miningAreaScribe;
        private bool _completed;

        private readonly Dictionary<(IntVec3 position, IntVec3 support), Cached<bool>> _roofSupportCache =
            new Dictionary<(IntVec3, IntVec3), Cached<bool>>();
        private readonly Dictionary<IntVec3, Cached<bool>> _roomDividerCache = new Dictionary<IntVec3, Cached<bool>>();
        private readonly Cached<List<CellRect>> _ancientDangerRects = new Cached<List<CellRect>>(new List<CellRect>(), AncientDangerCacheTicks);
        private readonly Dictionary<ThingDef, List<ThingDef>> _materialsInMineral = new Dictionary<ThingDef, List<ThingDef>>();
        private readonly Dictionary<ThingDef, bool> _allowedMineral = new Dictionary<ThingDef, bool>();
        private readonly Dictionary<ThingDef, bool> _allowedBuilding = new Dictionary<ThingDef, bool>();

        public StockJob_Mining() { }

        public StockJob_Mining(Map map) : base(map)
        {
            Label = "Mining (steel)";
            Trigger = new Trigger_Threshold(this, ThresholdMath.AccumulationOnlyOps, 3000);
            ConfigureParentFilter();
            Trigger.ThresholdFilter.SetAllow(ThingDefOf.Steel, true);
            Trigger.SettingsChanged = Notify_ThresholdFilterChanged;
        }

        public override string Kind => "Mining";
        public override WorkTypeDef? WorkType => WorkTypeDefOf.Mining;
        public override DesignationDef? DesignationDef => DesignationDefOf.Mine;
        protected override bool AllowsCaveInterior => true;
        public IReadOnlyList<Designation> HaulDesignations => _haulDesignations;
        public IReadOnlyList<Designation> DeconstructDesignations => _deconstructDesignations;
        protected override IEnumerable<Designation> OwnedDesignations => _designations.Concat(_haulDesignations).Concat(_deconstructDesignations);

        /// <summary>Owned designations only: the cap and the reduce pass never count adopted (external) ones.</summary>
        private int TotalDesignations => _designations.Count + _haulDesignations.Count + _deconstructDesignations.Count;
        private static bool CanAdd(int n) => n < StewardMod.Settings.stock.MaxDesignationsPerJob;
        private static bool ShouldRemoveMore(int n) => n > StewardMod.Settings.stock.MaxDesignationsPerJob;

        // ── Filters ───────────────────────────────────────────────────────────

        private void ConfigureParentFilter()
        {
            Trigger.SetParentFilter(f =>
            {
                foreach (var mineral in MiningUtility.GetMinerals())
                    foreach (var material in MiningUtility.GetMaterialsInMineral(mineral))
                        f.SetAllow(material, true);
                foreach (var material in MiningUtility.GetDeconstructibleBuildings().SelectMany(MiningUtility.GetMaterialsInBuilding).Distinct())
                    f.SetAllow(material, true);
                f.SetAllow(ThingCategoryDefOf.Chunks, false);
            });
        }

        private void Notify_ThresholdFilterChanged()
        {
            _allowedMineral.Clear();
            _allowedBuilding.Clear();
        }

        public bool Counted(ThingDef? def) => def != null && Trigger.ThresholdFilter.Allows(def);

        private List<ThingDef> GetMaterialsInMineral(ThingDef mineral)
        {
            if (!_materialsInMineral.TryGetValue(mineral, out var list))
            {
                list = MiningUtility.GetMaterialsInMineral(mineral);
                _materialsInMineral[mineral] = list;
            }
            return list;
        }

        /// <summary>A mineral is allowed when any of its products is counted by the threshold filter.</summary>
        public bool AllowedMineral(ThingDef? def)
        {
            if (def == null) return false;
            if (_allowedMineral.TryGetValue(def, out bool allowed)) return allowed;
            allowed = GetMaterialsInMineral(def).Any(Counted);
            _allowedMineral[def] = allowed;
            return allowed;
        }

        public bool AllowedBuilding(ThingDef? def)
        {
            if (def == null) return false;
            if (_allowedBuilding.TryGetValue(def, out bool allowed)) return allowed;
            allowed = def.building != null && def.building.IsDeconstructible && def.resourcesFractionWhenDeconstructed > 0
                      && MiningUtility.GetMaterialsInBuilding(def).Any(Counted);
            _allowedBuilding[def] = allowed;
            return allowed;
        }

        // ── Counting ──────────────────────────────────────────────────────────

        public int GetCountInChunk(Thing chunk) => MiningUtility.GetCountInChunk(chunk.def, Counted);
        public int GetCountInMineral(Mineable rock) => MiningUtility.GetCountInMineral(rock.def, Counted);
        public int GetCountInBuilding(Building? building) => MiningUtility.GetCountInBuilding(building, Counted);

        /// <summary>Counted products locked inside chunks already sitting in storage.</summary>
        private int GetCountInStoredChunks()
        {
            int count = 0;
            foreach (var t in Map.listerThings.ThingsInGroup(ThingRequestGroup.Chunk))
            {
                if (!MiningUtility.IsChunk(t.def) || !t.IsInAnyStorage() || t.IsForbidden(Faction.OfPlayer)) continue;
                count += GetCountInChunk(t);
            }
            return count;
        }

        /// <summary>Expected yield of every designation we own plus the adopted external ones.</summary>
        private int GetCountInDesignations()
        {
            int count = 0;
            foreach (var des in _designations) count += MineYield(des);
            foreach (var des in _deconstructDesignations) count += DeconstructYield(des);
            foreach (var des in _haulDesignations) count += HaulYield(des);
            foreach (var des in _adopted)
            {
                if (des.def == DesignationDefOf.Mine) count += MineYield(des);
                else if (des.def == DesignationDefOf.Deconstruct) count += DeconstructYield(des);
                else if (des.def == DesignationDefOf.Haul) count += HaulYield(des);
            }
            return count;
        }

        private int MineYield(Designation des)
        {
            if (!des.target.IsValid) return 0;
            var mineable = des.target.Cell.GetFirstMineable(Map);
            return mineable != null && AllowedMineral(mineable.def) ? GetCountInMineral(mineable) : 0;
        }

        private int DeconstructYield(Designation des) => des.target.HasThing ? GetCountInBuilding(des.target.Thing as Building) : 0;

        private int HaulYield(Designation des) => des.target.HasThing ? GetCountInChunk(des.target.Thing) : 0;

        // ── Safety checks ─────────────────────────────────────────────────────

        private bool WouldCollapseIfSupportDestroyed(IntVec3 position, IntVec3 support)
        {
            var key = (position, support);
            if (_roofSupportCache.TryGetValue(key, out var cached) && cached.TryGetValue(out bool c)) return c;
            bool result = MiningUtility.WouldCollapseIfSupportDestroyed(position, support, Map);
            if (cached != null) cached.Update(result);
            else _roofSupportCache[key] = new Cached<bool>(result, SafetyCacheTicks).Also(x => x.Update(result));
            return result;
        }

        /// <summary>Advanced check: would any roofed cell within support range lose its last roof holder?</summary>
        public bool IsARoofSupport_Advanced(Building building)
        {
            if (!CheckRoofSupport || !CheckRoofSupportAdvanced) return false;
            int n = RoofCollapseUtility.RoofSupportRadialCellsCount;
            for (int i = n - 1; i >= 0; i--)
                if (WouldCollapseIfSupportDestroyed(GenRadial.RadialPattern[i] + building.Position, building.Position))
                    return true;
            return false;
        }

        public bool IsARoofSupport_Basic(Building building) =>
            CheckRoofSupport && !CheckRoofSupportAdvanced && MiningUtility.IsARoofSupport_Basic(building.Position);

        public bool IsARoomDivider(Thing target)
        {
            if (!CheckRoomDivision) return false;
            var position = target.Position;
            if (_roomDividerCache.TryGetValue(position, out var cached) && cached.TryGetValue(out bool c)) return c;
            bool result = MiningUtility.IsARoomDivider(position, Map);
            if (cached != null) cached.Update(result);
            else _roomDividerCache[position] = new Cached<bool>(result, SafetyCacheTicks).Also(x => x.Update(result));
            return result;
        }

        public bool IsAllowedToMineRoofAt(Thing target) =>
            MineThickRoofs || !(Map.roofGrid.RoofAt(target.Position)?.isThickRoof ?? false);

        public bool IsInAllowedArea(Thing target) => ProductCounter.IsInAllowedArea(MiningArea, target.Position, InvertMiningArea);

        private List<CellRect> AncientDangerRects()
        {
            if (_ancientDangerRects.TryGetValue(out var rects)) return rects;
            return _ancientDangerRects.Update(MiningUtility.GetAncientDangerRects(Map));
        }

        private bool InAncientDanger(Thing target) => MiningUtility.InAnyRect(AncientDangerRects(), target.Position);

        // ── Target validity ───────────────────────────────────────────────────

        public bool IsRelevantDeconstructionTarget(Building target) =>
            target.def.building != null
            && target.def.building.IsDeconstructible
            && target.def.resourcesFractionWhenDeconstructed > 0
            && target.def.CostListAdjusted(target.Stuff, false).Any(tc => Counted(tc.thingDef));

        public bool IsValidDeconstructionTarget(Building? target, bool includeDesignated = false)
        {
            if (target == null || !target.Spawned || target.Map != Map) return false;
            // Never our own structures; only ownerless (claimable) ruins the player could deconstruct.
            if (target.Faction == Faction.OfPlayer || !target.DeconstructibleBy(Faction.OfPlayer)) return false;
            var designation = Map.designationManager.DesignationOn(target);
            bool designationOk = includeDesignated
                ? designation == null || designation.def == DesignationDefOf.Deconstruct
                : designation == null;
            return designationOk
                && !target.IsForbidden(Faction.OfPlayer)
                && !target.Position.Fogged(Map)
                && AllowedBuilding(target.def)
                && IsRelevantDeconstructionTarget(target)
                && IsInAllowedArea(target)
                && !InAncientDanger(target)
                && IsReachable(target, PathEndMode.Touch)
                && !IsARoofSupport_Basic(target)
                && !IsARoomDivider(target);
        }

        public bool IsValidMiningTarget(Mineable? target, bool includeDesignated = false)
        {
            if (target == null || !target.Spawned || target.Map != Map) return false;
            if (!target.def.mineable || !AllowedMineral(target.def)) return false;
            // Expect lots of fogged rock: check fog before anything expensive.
            if (target.Position.Fogged(Map)) return false;
            var designation = Map.designationManager.DesignationOn(target)
                              ?? Map.designationManager.DesignationAt(target.Position, DesignationDefOf.Mine);
            bool designationOk = includeDesignated
                ? designation == null || designation.def == DesignationDefOf.Mine
                : designation == null && !Map.designationManager.HasMapDesignationAt(target.Position);
            return designationOk
                && IsInAllowedArea(target)
                && GetCountInMineral(target) > 0
                && !InAncientDanger(target)
                && !IsARoomDivider(target)
                // basic grid only; the advanced simulation runs at designation time on the sorted candidates
                && !IsARoofSupport_Basic(target)
                && IsAllowedToMineRoofAt(target)
                && IsReachable(target, PathEndMode.Touch);
        }

        private bool IsValidChunkTarget(Thing t) =>
            MiningUtility.IsChunk(t.def)
            && t.Spawned
            && !t.IsInAnyStorage()
            && !t.Position.Fogged(Map)
            && !Map.reservationManager.IsReserved(t)
            && Map.designationManager.DesignationOn(t) == null
            && IsInAllowedArea(t)
            && !InAncientDanger(t)
            && GetCountInChunk(t) > 0
            && IsReachable(t, PathEndMode.ClosestTouch);

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
            CleanDeadList(_haulDesignations, DesignationDefOf.Haul);
            CleanDeadList(_deconstructDesignations, DesignationDefOf.Deconstruct);
            AddRelevantGameDesignations();

            int count = Trigger.GetCurrentCount() + GetCountInStoredChunks() + GetCountInDesignations();
            var data = new WorkData();

            var directive = DirectiveFor(count);
            if (directive != Directive.Increase || ShouldRemoveMore(TotalDesignations))
            {
                data.Kind = WorkKind.ReduceDesignations;
                PlanReduceDesignations(ref count, data);
                return data;
            }

            data.Kind = WorkKind.AddDesignations;
            if (!HaulMapChunks && !DeconstructBuildings && !AllowMining) { Note("all tasks disabled"); return data; }
            if (!CanAdd(TotalDesignations)) { Note("designation cap reached"); return data; }

            if (HaulMapChunks) PlanHaulChunks(ref count, data);
            if (DeconstructBuildings && CanAdd(TotalDesignations + data.DesignationsToAdd.Count)) PlanDeconstructBuildings(ref count, data);
            if (AllowMining && CanAdd(TotalDesignations + data.DesignationsToAdd.Count)) PlanMineResources(ref count, data);

            if (data.DesignationsToAdd.Count == 0)
            {
                RunsWithoutTargets++;
                Note($"no valid targets (count {count} / target {EffectiveTarget})");
            }
            else RunsWithoutTargets = 0;
            return data;
        }

        private void CleanDeadList(List<Designation> list, DesignationDef def)
        {
            if (list.Count == 0) return;
            var live = new HashSet<Designation>(Map.designationManager.SpawnedDesignationsOfDef(def));
            int before = list.Count;
            list.RemoveAll(d => !live.Contains(d));
            if (before != list.Count) Note($"dropped {before - list.Count} dead {def.defName} designations");
        }

        /// <summary>
        /// Count existing game designations (player/director-made) that work toward our target. They are never
        /// released by this job. Mine adoption needs TakeOwnershipOfMiningJobs; haul/deconstruct adoption follows the
        /// HaulMapChunks/DeconstructBuildings toggles, so a job that may not deconstruct ignores deconstruct orders.
        /// </summary>
        private void AddRelevantGameDesignations()
        {
            RebuildAdopted(
                (TakeOwnershipOfMiningJobs ? DesignationDefOf.Mine : null,
                    des => des.target.IsValid && IsValidMiningTarget(des.target.Cell.GetFirstMineable(Map), true)),
                (DeconstructBuildings ? DesignationDefOf.Deconstruct : null,
                    des => des.target.HasThing && IsValidDeconstructionTarget(des.target.Thing as Building, true)),
                (HaulMapChunks ? DesignationDefOf.Haul : null,
                    des => des.target.HasThing && MiningUtility.IsChunk(des.target.Thing.def) && GetCountInChunk(des.target.Thing) > 0));
        }

        private void PlanReduceDesignations(ref int count, WorkData data)
        {
            int planned = 0;
            int total = TotalDesignations;

            // Mine designations first: lowest yield-per-distance released first.
            var mineables = GetThingsSorted(
                _designations.Where(d => d.target.IsValid).Select(d => d.target.Cell.GetFirstMineable(Map)).Where(m => m != null)!,
                _ => true,
                (m, dist) => -GetCountInMineral(m) / dist);
            foreach (var mineable in mineables)
            {
                var pos = mineable.Position;
                var des = _designations.FirstOrDefault(d => d.target.Cell == pos);
                if (des == null) continue;
                int yield = GetCountInMineral(mineable);
                count -= yield;
                if (CountMeetsTarget(count) || ShouldRemoveMore(total - planned))
                {
                    data.MineDesignationsToRemove.Add((des, yield, count));
                    planned++;
                }
                else { count += yield; break; }
            }

            if (CountMeetsTarget(count) || ShouldRemoveMore(total - planned))
            {
                var buildings = GetThingsSorted(
                    _deconstructDesignations.Where(d => d.target.HasThing && d.target.Thing is Building).Select(d => (Building)d.target.Thing),
                    _ => true,
                    (b, dist) => -GetCountInBuilding(b) / dist);
                foreach (var building in buildings)
                {
                    var des = _deconstructDesignations.FirstOrDefault(d => d.target.Thing == building);
                    if (des == null) continue;
                    int yield = GetCountInBuilding(building);
                    count -= yield;
                    if (CountMeetsTarget(count) || ShouldRemoveMore(total - planned))
                    {
                        data.DeconstructDesignationsToRemove.Add((des, yield, count));
                        planned++;
                    }
                    else { count += yield; break; }
                }
            }

            if (CountMeetsTarget(count) || ShouldRemoveMore(total - planned))
            {
                var chunks = GetThingsSorted(
                    _haulDesignations.Where(d => d.target.HasThing).Select(d => d.target.Thing),
                    _ => true,
                    (c, dist) => -GetCountInChunk(c) / dist);
                foreach (var chunk in chunks)
                {
                    var des = _haulDesignations.FirstOrDefault(d => d.target.Thing == chunk);
                    if (des == null) continue;
                    int yield = GetCountInChunk(chunk);
                    count -= yield;
                    if (CountMeetsTarget(count) || ShouldRemoveMore(total - planned))
                    {
                        data.HaulDesignationsToRemove.Add((des, yield, count));
                        planned++;
                    }
                    else { count += yield; break; }
                }
            }

            if (planned == 0) Note($"target already satisfied (count {count} / target {EffectiveTarget})");
        }

        private void PlanHaulChunks(ref int count, WorkData data)
        {
            var chunks = GetThingsSorted(
                Map.listerThings.ThingsInGroup(ThingRequestGroup.Chunk),
                IsValidChunkTarget,
                (c, dist) => GetCountInChunk(c) / dist);
            foreach (var chunk in chunks)
            {
                if (CountMeetsTarget(count) || !CanAdd(TotalDesignations + data.DesignationsToAdd.Count)) break;
                int amount = GetCountInChunk(chunk);
                count += amount;
                data.DesignationsToAdd.Add((chunk, DesignationDefOf.Haul, amount, count));
            }
        }

        private void PlanDeconstructBuildings(ref int count, WorkData data)
        {
            var buildings = GetThingsSorted(
                Map.listerThings.AllThings.OfType<Building>(),
                b => IsValidDeconstructionTarget(b),
                (b, dist) => GetCountInBuilding(b) / dist);
            foreach (var building in buildings)
            {
                if (CountMeetsTarget(count) || !CanAdd(TotalDesignations + data.DesignationsToAdd.Count)) break;
                if (IsARoofSupport_Advanced(building)) continue;
                int amount = GetCountInBuilding(building);
                count += amount;
                data.DesignationsToAdd.Add((building, DesignationDefOf.Deconstruct, amount, count));
            }
        }

        private void PlanMineResources(ref int count, WorkData data)
        {
            var mineables = GetThingsSorted(
                Map.listerThings.AllThings.OfType<Mineable>(),
                m => IsValidMiningTarget(m),
                (m, dist) => GetCountInMineral(m) / dist);
            foreach (var mineable in mineables)
            {
                if (CountMeetsTarget(count) || !CanAdd(TotalDesignations + data.DesignationsToAdd.Count)) break;
                if (IsARoofSupport_Advanced(mineable)) continue;
                int amount = GetCountInMineral(mineable);
                count += amount;
                data.DesignationsToAdd.Add((mineable, DesignationDefOf.Mine, amount, count));
            }
        }

        // ── Execute ───────────────────────────────────────────────────────────

        public override bool Execute(object dataObj)
        {
            var data = (WorkData)dataObj;
            switch (data.Kind)
            {
                case WorkKind.None: return false;
                case WorkKind.CleanUp: CleanUp(); return false;
                case WorkKind.ReduceDesignations: return ExecuteReduceDesignations(data);
                default: return ExecuteAddDesignations(data);
            }
        }

        private bool ExecuteReduceDesignations(WorkData data)
        {
            int removed = 0;
            foreach (var (des, _, _) in data.MineDesignationsToRemove) { des.Delete(); _designations.Remove(des); removed++; }
            foreach (var (des, _, _) in data.DeconstructDesignationsToRemove) { des.Delete(); _deconstructDesignations.Remove(des); removed++; }
            foreach (var (des, _, _) in data.HaulDesignationsToRemove) { des.Delete(); _haulDesignations.Remove(des); removed++; }
            if (removed > 0) Note($"released {removed} designations (count {Trigger.GetCurrentCount()} / {EffectiveTarget})");
            return removed > 0;
        }

        private bool ExecuteAddDesignations(WorkData data)
        {
            int mine = 0, haul = 0, deconstruct = 0;
            var dm = Map.designationManager;
            foreach (var (target, def, _, _) in data.DesignationsToAdd)
            {
                // Re-validate: the target may have been mined, hauled or destroyed since planning.
                if (target.DestroyedOrNull() || !target.Spawned) continue;

                if (def == DesignationDefOf.Mine)
                {
                    if (dm.HasMapDesignationAt(target.Position)) continue;
                    AddDesignation(new Designation(target.Position, DesignationDefOf.Mine));
                    mine++;
                }
                else if (def == DesignationDefOf.Haul)
                {
                    if (dm.HasMapDesignationOn(target)) continue;
                    if (target.IsForbidden(Faction.OfPlayer)) target.SetForbidden(false, false);
                    var des = new Designation(target, DesignationDefOf.Haul);
                    dm.AddDesignation(des);
                    _haulDesignations.Add(des);
                    haul++;
                }
                else if (def == DesignationDefOf.Deconstruct)
                {
                    if (dm.HasMapDesignationOn(target)) continue;
                    if (!(target is Building b) || !b.DeconstructibleBy(Faction.OfPlayer)) continue;
                    var des = new Designation(target, DesignationDefOf.Deconstruct);
                    dm.AddDesignation(des);
                    _deconstructDesignations.Add(des);
                    deconstruct++;
                }
            }
            int added = mine + haul + deconstruct;
            if (added > 0)
                Note($"designated {mine} rocks, {haul} chunks, {deconstruct} buildings (count {Trigger.GetCurrentCount()} + pending → target {EffectiveTarget})");
            return added > 0;
        }

        public override void CleanUp()
        {
            CleanDeadDesignations();
            CleanDeadList(_haulDesignations, DesignationDefOf.Haul);
            CleanDeadList(_deconstructDesignations, DesignationDefOf.Deconstruct);
            base.CleanUp();
            CleanUpDesignations(_haulDesignations);
            CleanUpDesignations(_deconstructDesignations);
        }

        public override void Notify_AreaRemoved(Area area)
        {
            if (MiningArea == area) MiningArea = null;
        }

        // ── Persistence ──────────────────────────────────────────────────────

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref AllowMining, "allowMining", true);
            Scribe_Values.Look(ref HaulMapChunks, "haulMapChunks", true);
            Scribe_Values.Look(ref DeconstructBuildings, "deconstructBuildings", false);
            Scribe_Values.Look(ref CheckRoofSupport, "checkRoofSupport", true);
            Scribe_Values.Look(ref CheckRoofSupportAdvanced, "checkRoofSupportAdvanced", true);
            Scribe_Values.Look(ref CheckRoomDivision, "checkRoomDivision", true);
            Scribe_Values.Look(ref MineThickRoofs, "mineThickRoofs", false);
            Scribe_Values.Look(ref TakeOwnershipOfMiningJobs, "takeOwnershipOfMiningJobs", false);
            Scribe_Values.Look(ref InvertMiningArea, "invertMiningArea");
            Scribe_Values.Look(ref _completed, "completed");
            ScribeAreaByLabel(ref MiningArea, ref _miningAreaScribe, "miningArea", Map);
            ScribeExtraDesignations(ref _haulDesignations, "haulDesignations");
            ScribeExtraDesignations(ref _deconstructDesignations, "deconstructDesignations");

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                // Mine designations are cell-targeted, so the base resolver (thing-based) leaves the deep-loaded
                // copies in place; swap them for the live ones so CleanDeadDesignations recognises them.
                if (Map != null)
                    for (int i = 0; i < _designations.Count; i++)
                    {
                        var d = _designations[i];
                        if (d.target.HasThing || !d.target.IsValid) continue;
                        _designations[i] = Map.designationManager.DesignationAt(d.target.Cell, DesignationDefOf.Mine) ?? d;
                    }
                ConfigureParentFilter();
                Trigger.SettingsChanged = Notify_ThresholdFilterChanged;
            }
        }

        private void ScribeExtraDesignations(ref List<Designation> designations, string key)
        {
            if (Scribe.mode == LoadSaveMode.Saving && Map != null)
                designations.RemoveAll(d => !Map.designationManager.AllDesignations.Contains(d));
            Scribe_Collections.Look(ref designations, key, LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                designations ??= new List<Designation>();
                if (Map != null)
                    for (int i = 0; i < designations.Count; i++)
                    {
                        var thing = designations[i].target.Thing;
                        if (thing == null) continue;
                        designations[i] = Map.designationManager.DesignationOn(thing, designations[i].def) ?? designations[i];
                    }
            }
        }
    }

    public static partial class StockJobFactories
    {
        static partial void AddMiningImpl(StockComponent comp, int colonists)
        {
            var job = comp.Add(new StockJob_Mining(comp.map));
            job.Trigger.TargetCount = DefaultStockPlan.SteelTarget(colonists);
            job.Label = "Mining (steel)";
            job.HaulMapChunks = true;
            job.DeconstructBuildings = true;
            job.CheckRoofSupport = true;
            job.CheckRoomDivision = true;
        }
    }
}
