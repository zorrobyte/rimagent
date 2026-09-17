// Derived from Colony Manager Redux ManagerJobs/ManagerJob.cs (MIT, see THIRD_PARTY_NOTICES.md); synchronous, no UI.
// Modified for RimBridge (2026): namespace RimBridge.Steward.*, identifiers renamed, logging/settings routed through RimBridge (BridgeLog, StewardMod.Settings).
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimBridge.Steward.Stock
{
    /// <summary>
    /// A threshold-driven job: when its trigger wants more, plan designations (Gather), then apply them
    /// (Execute); when satisfied, trim designations it created. Runs headless from StockComponent.
    /// </summary>
    public abstract class StockJob : IExposable, ILoadReferenceable
    {
        public const int DefaultUpdateIntervalTicks = 2500;

        public Map Map = null!;
        public int Id;
        public string Label = "";
        public Trigger_Threshold Trigger = null!;
        public int UpdateIntervalTicks = DefaultUpdateIntervalTicks;
        public int LastRunTick = -1;
        public bool Suspended;
        public bool Managed = true;
        public bool CheckReachable = true;
        public bool UsePathBasedDistance;
        public int ConsecutiveFailures;
        /// <summary>True while the target is still the auto-scaled default (planner/LLM overrides clear it).</summary>
        public bool AutoScaled = true;
        /// <summary>How many consecutive runs found nothing to designate although the trigger wanted more.</summary>
        public int RunsWithoutTargets;
        public string? LastRunSummary;

        /// <summary>Designations this job created (persisted). Only these are ever planned for removal.</summary>
        protected List<Designation> _designations = new List<Designation>();
        /// <summary>
        /// External designations (player/director-made) that work toward this target: their expected yield counts
        /// as pending, but the job never deletes them. Rebuilt from the live designation manager every run; not persisted.
        /// </summary>
        protected readonly List<Designation> _adopted = new List<Designation>();
        private int _lastAdoptedCount;
        /// <summary>uniqueID of the owning map, scribed so GetUniqueLoadID does not depend on the (later-resolved) Map reference.</summary>
        private int _mapId = -1;
        private readonly List<string> _notes = new List<string>();
        private readonly Dictionary<(IntVec3, PathEndMode, Danger), Cached<bool>> _reachability =
            new Dictionary<(IntVec3, PathEndMode, Danger), Cached<bool>>();
        private const int ReachabilityCacheTicks = 2000;

        protected StockJob() { }
        protected StockJob(Map map) { Map = map; _mapId = map?.uniqueID ?? -1; }

        /// <summary>Owning map id used for the load ID (set by StockComponent.Add; backfilled after load for old saves).</summary>
        public int MapId { get => _mapId; internal set => _mapId = value; }

        public abstract string Kind { get; }
        public abstract WorkTypeDef? WorkType { get; }
        public abstract DesignationDef? DesignationDef { get; }
        public virtual IEnumerable<string> Targets => Trigger.ThresholdFilter.AllowedThingDefs.Select(d => d.label);
        public IReadOnlyList<Designation> Designations => _designations;
        /// <summary>External designations currently counted toward the target (never removed by this job).</summary>
        public IReadOnlyList<Designation> AdoptedDesignations => _adopted;
        /// <summary>Every designation this job created (subclasses append their extra lists).</summary>
        protected virtual IEnumerable<Designation> OwnedDesignations => _designations;
        /// <summary>Owned + adopted: everything whose expected yield counts as pending stock.</summary>
        protected IEnumerable<Designation> CountedDesignations => _designations.Concat(_adopted);
        public IReadOnlyList<string> Notes => _notes;
        public virtual bool IsOutdoorWork => true;

        public bool ShouldRunNow(int tick) =>
            Managed && !Suspended && (LastRunTick < 0 || tick - LastRunTick >= UpdateIntervalTicks);

        public void Touch() => LastRunTick = Find.TickManager.TicksGame;

        /// <summary>Read-only planning. Returns a job-specific work data object, or null when nothing to do.</summary>
        public abstract object? Gather();

        /// <summary>Applies the plan. Returns true when any designation/bill was added or removed.</summary>
        public abstract bool Execute(object data);

        /// <summary>Removes every designation this job created; adopted (external) ones are left in place.</summary>
        public virtual void CleanUp()
        {
            CleanUpDesignations(_designations);
            _adopted.Clear();
        }

        public virtual void Notify_AreaRemoved(Area area) { }

        /// <summary>
        /// Effective target after posture multipliers. Threshold jobs plan against this, so a x0 posture makes the
        /// target 0: met at once, the job releases its own designations and idles until the posture ends.
        /// </summary>
        public int EffectiveTarget => StewardTuning.EffectiveTarget(Kind, Trigger.TargetCount);

        /// <summary>What the status row reports as "current" (the trigger's count unless a job counts something else, e.g. animals).</summary>
        public virtual int CurrentCount => Trigger.GetCurrentCount();

        /// <summary>Whether a count satisfies this job: the trigger's op against the effective target unless overridden.</summary>
        public virtual bool CountMeetsTarget(int count) => ThresholdMath.Evaluate(Trigger.Op, count, EffectiveTarget);

        /// <summary>Increase / Decrease / Hold for a count against the effective target.</summary>
        public Directive DirectiveFor(int count) => ThresholdMath.EvaluateDirective(Trigger.Op, count, EffectiveTarget);

        /// <summary>True while the job wants work done (current count does not meet the effective target).</summary>
        public virtual bool WantsWork => !CountMeetsTarget(Trigger.GetCurrentCount());

        // ── Notes (replaces CMR ManagerLog) ─────────────────────────────────

        protected void Note(string text)
        {
            _notes.Add($"[{Find.TickManager.TicksGame}] {text}");
            if (_notes.Count > 20) _notes.RemoveAt(0);
            LastRunSummary = text;
        }

        // ── Designation bookkeeping ──────────────────────────────────────────

        protected static void CleanUpDesignations(List<Designation> designations)
        {
            foreach (var d in designations) d.Delete();
            designations.Clear();
        }

        /// <summary>Drops designations the game no longer has (target destroyed, player cancelled).</summary>
        protected void CleanDeadDesignations()
        {
            if (_designations.Count == 0) return;
            var live = DesignationDef != null
                ? Map.designationManager.SpawnedDesignationsOfDef(DesignationDef)
                : Map.designationManager.AllDesignations;
            var liveSet = new HashSet<Designation>(live);
            int before = _designations.Count;
            _designations.RemoveAll(d => !liveSet.Contains(d));
            if (before != _designations.Count) Note($"dropped {before - _designations.Count} dead designations");
        }

        protected void AddDesignation(Designation d)
        {
            Map.designationManager.AddDesignation(d);
            _designations.Add(d);
        }

        protected bool HasDesignationOn(Thing t) => DesignationDef != null && Map.designationManager.DesignationOn(t, DesignationDef) != null;

        /// <summary>
        /// Rebuilds the adopted list: live designations of each def that pass its filter and were not created by this
        /// job. A null def is skipped (adoption toggled off). Notes when the number changes. Adopted designations are
        /// counted toward the target but never planned for removal or deleted by CleanUp.
        /// </summary>
        protected void RebuildAdopted(params (DesignationDef? def, Func<Designation, bool> accept)[] sources)
        {
            _adopted.Clear();
            var owned = new HashSet<Designation>(OwnedDesignations);
            var seen = new HashSet<Designation>();
            foreach (var (def, accept) in sources)
            {
                if (def == null) continue;
                foreach (var des in Map.designationManager.SpawnedDesignationsOfDef(def))
                {
                    if (des == null || owned.Contains(des) || !seen.Add(des)) continue;
                    if (!accept(des)) continue;
                    _adopted.Add(des);
                }
            }
            if (_adopted.Count != _lastAdoptedCount)
            {
                if (_adopted.Count > 0) Note($"counting {_adopted.Count} external designations (not ours; left in place)");
                _lastAdoptedCount = _adopted.Count;
            }
        }

        // ── Geometry ─────────────────────────────────────────────────────────

        /// <summary>Straight-line distance ×2 (CMR's path-based option is omitted; the 1.6 pathfinder API changed).</summary>
        public virtual float Distance(Thing target, IntVec3 source)
            => Mathf.Sqrt(source.DistanceToSquared(target.Position)) * 2;

        public IntVec3 BaseCenter => ProductCounter.GetBaseCenter(Map);

        /// <summary>Filters, then sorts descending by sorter(thing, distance).</summary>
        protected List<T> GetThingsSorted<T>(IEnumerable<T> unsorted, Func<T, bool> predicate, Func<T, float, float> sorter, IntVec3? source = null)
            where T : Thing
        {
            var pos = source ?? BaseCenter;
            var scored = new List<(T t, float s)>();
            foreach (var t in unsorted)
            {
                if (t == null || !predicate(t)) continue;
                scored.Add((t, sorter(t, Distance(t, pos))));
            }
            scored.Sort((a, b) => b.s.CompareTo(a.s));
            var result = new List<T>(scored.Count);
            foreach (var (t, _) in scored) result.Add(t);
            return result;
        }

        /// <summary>Distance and danger limits shared by every stock job (keeps pawns out of caves and away from hives/hostiles).</summary>
        public virtual bool IsSafeTarget(Thing target)
        {
            var s = StewardMod.Settings.stock;
            var pos = target.Position;
            float d = pos.DistanceTo(BaseCenter);
            if (s.MaxWorkRadius > 0 && d > s.MaxWorkRadius) return false;
            if (AllowsCaveInterior == false && d > 25f)
            {
                var roof = pos.GetRoof(Map);
                if (roof != null && roof.isNatural && roof.isThickRoof) return false; // deep cave / mountain interior
            }
            RefreshDangerSpots();
            float avoid = s.DangerAvoidRadius;
            for (int i = 0; i < _dangerSpots.Count; i++)
                if (_dangerSpots[i].DistanceTo(pos) < avoid) return false;
            return true;
        }

        /// <summary>Mining legitimately works under thick roof; other jobs treat it as a cave.</summary>
        protected virtual bool AllowsCaveInterior => false;

        private readonly List<IntVec3> _dangerSpots = new List<IntVec3>();
        private int _dangerSpotsTick = -1;

        private void RefreshDangerSpots()
        {
            int tick = Find.TickManager.TicksGame;
            if (_dangerSpotsTick == tick) return;
            _dangerSpotsTick = tick;
            _dangerSpots.Clear();
            foreach (var p in Map.mapPawns.AllPawnsSpawned)
            {
                if (p.Dead || p.Downed) continue;
                if (p.HostileTo(Faction.OfPlayer) || (p.RaceProps.Animal && p.MentalStateDef != null && p.MentalStateDef.IsAggro)) _dangerSpots.Add(p.Position);
                else if (p.RaceProps.Animal && p.RaceProps.predator && p.RaceProps.baseBodySize >= 1f && p.Faction == null) _dangerSpots.Add(p.Position);
            }
            foreach (var t in Map.listerThings.AllThings)
            {
                if (t.def.defName == "Hive" || t.def.defName.StartsWith("Hive") || t.def.defName == "AncientCryptosleepCasket") _dangerSpots.Add(t.Position);
            }
        }

        public virtual bool IsReachable(Thing target, PathEndMode pathEndMode = PathEndMode.Touch, Danger danger = Danger.Some)
        {
            if (target.Position.Fogged(Map)) return false;
            if (!IsSafeTarget(target)) return false;
            if (!CheckReachable) return true;
            var key = (target.Position, pathEndMode, danger);
            if (_reachability.TryGetValue(key, out var cached) && cached.TryGetValue(out bool c)) return c;
            bool result = Map.mapPawns.FreeColonistsSpawned.Any(p => p.CanReach(target, pathEndMode, danger));
            if (cached != null) cached.Update(result);
            else _reachability[key] = new Cached<bool>(result, ReachabilityCacheTicks).Also(x => x.Update(result));
            return result;
        }

        protected bool CanAddMoreDesignations() => _designations.Count < StewardMod.Settings.stock.MaxDesignationsPerJob;

        // ── Persistence ──────────────────────────────────────────────────────

        public virtual void ExposeData()
        {
            Scribe_References.Look(ref Map, "map");
            Scribe_Values.Look(ref _mapId, "mapId", -1);
            Scribe_Values.Look(ref Id, "id");
            Scribe_Values.Look(ref Label, "label", "");
            Scribe_Deep.Look(ref Trigger, "trigger", this);
            Scribe_Values.Look(ref UpdateIntervalTicks, "updateInterval", DefaultUpdateIntervalTicks);
            Scribe_Values.Look(ref LastRunTick, "lastRunTick", -1);
            Scribe_Values.Look(ref Suspended, "suspended");
            Scribe_Values.Look(ref Managed, "managed", true);
            Scribe_Values.Look(ref CheckReachable, "checkReachable", true);
            Scribe_Values.Look(ref UsePathBasedDistance, "usePathBasedDistance");
            Scribe_Values.Look(ref AutoScaled, "autoScaled", true);
            ScribeDesignations(ref _designations);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Trigger ??= new Trigger_Threshold(this);
                Trigger.Attach(this);
                _designations ??= new List<Designation>();
            }
        }

        protected void ScribeDesignations(ref List<Designation> designations)
        {
            if (Scribe.mode == LoadSaveMode.Saving && Map != null)
                designations.RemoveAll(d => !Map.designationManager.AllDesignations.Contains(d));
            Scribe_Collections.Look(ref designations, "designations", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                designations ??= new List<Designation>();
                if (Map != null)
                    for (int i = 0; i < designations.Count; i++)
                    {
                        var thing = designations[i].target.Thing;
                        if (thing == null) continue;
                        designations[i] = Map.designationManager.DesignationOn(thing) ?? designations[i];
                    }
            }
        }

        protected static void ScribeAreaByLabel(ref Area? area, ref string? tmp, string key, Map? map)
        {
            if (Scribe.mode == LoadSaveMode.Saving) tmp = area?.Label;
            Scribe_Values.Look(ref tmp, key);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && map != null)
            {
                area = tmp.NullOrEmpty() ? null : map.areaManager.GetLabeled(tmp);
                tmp = null;
            }
        }

        /// <summary>Uses the scribed map id: LoadedObjectDirectory registers jobs before the Map reference resolves.</summary>
        public string GetUniqueLoadID() => $"RimBridge_StockJob_{_mapId}_{Id}";

        public override string ToString() => $"{Kind}#{Id} {Label} [{Trigger?.StatusLine}]";
    }

    internal static class FluentExt
    {
        public static T Also<T>(this T self, Action<T> act) { act(self); return self; }
    }
}
