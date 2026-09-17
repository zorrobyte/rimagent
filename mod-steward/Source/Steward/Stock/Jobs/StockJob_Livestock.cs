// Written for RimBridge (2026): a reduced-scope livestock job in the style of the other StockJob subclasses.
// Semantics follow Colony Manager Redux's ManagerJob_Livestock (MIT, see THIRD_PARTY_NOTICES.md) without copying it:
// per species keep the tame count within [min, max] (total, or per adult/juvenile × male/female bucket), taming wild
// animals of the species when short and slaughtering surplus, optionally restricting tamed animals to an area and
// setting training goals. Taming and slaughtering are Handling work, which the scorer already prioritises.
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimBridge.Steward.Stock
{
    public class StockJob_Livestock : StockJob
    {
        public sealed class WorkData
        {
            public LivestockRule.Plan Plan = new LivestockRule.Plan();
            public List<Pawn> ToTame = new List<Pawn>();
            public List<Pawn> ToSlaughter = new List<Pawn>();
            public List<Designation> TameToRelease = new List<Designation>();
            public List<Pawn> AreaTargets = new List<Pawn>();
            public List<(Pawn pawn, TrainableDef def)> TrainTargets = new List<(Pawn, TrainableDef)>();
            public bool Empty => ToTame.Count + ToSlaughter.Count + TameToRelease.Count + AreaTargets.Count + TrainTargets.Count == 0;
        }

        public PawnKindDef? Species;
        /// <summary>Lower bound in total mode (the upper bound is Trigger.TargetCount).</summary>
        public int Min;
        /// <summary>Per-bucket bounds (index = LivestockRule bucket; max &lt; 0 = unbounded); null = total mode.</summary>
        public List<int>? BucketMin;
        public List<int>? BucketMax;
        public bool Tame = true;
        public bool Slaughter = true;
        /// <summary>When set, every tame animal of the species is restricted to this area; null = leave areas alone.</summary>
        public Area? RestrictArea;
        public List<TrainableDef> Train = new List<TrainableDef>();

        private List<Designation> _slaughterDesignations = new List<Designation>();
        private string? _restrictAreaScribe;

        public StockJob_Livestock() { }

        public StockJob_Livestock(Map map, PawnKindDef species, int min, int max) : base(map)
        {
            Species = species;
            Label = $"Livestock ({species.label})";
            Trigger = new Trigger_Threshold(this, ThresholdMath.AccumulationOnlyOps, 3000);
            Trigger.TargetCount = Math.Max(0, max);
            Min = Math.Max(0, min);
        }

        public override string Kind => "Livestock";
        public override WorkTypeDef? WorkType => WorkTypeDefOf.Handling;
        public override DesignationDef? DesignationDef => DesignationDefOf.Tame;
        public override IEnumerable<string> Targets => Species != null ? new[] { Species.LabelCap.Resolve() } : Enumerable.Empty<string>();
        public IReadOnlyList<Designation> SlaughterDesignations => _slaughterDesignations;
        protected override IEnumerable<Designation> OwnedDesignations => _designations.Concat(_slaughterDesignations);

        // ── Targets ─────────────────────────────────────────────────────────

        public int Max { get => Trigger.TargetCount; set => Trigger.TargetCount = Math.Max(0, value); }
        public bool PerBucket => BucketMin != null && BucketMax != null && BucketMin.Count == LivestockRule.BucketCount && BucketMax.Count == LivestockRule.BucketCount;

        /// <summary>Posture multipliers apply to min and max alike (when min &gt; max the rule lets max follow min).</summary>
        public int EffectiveMin => StewardTuning.EffectiveTarget(Kind, Min);

        public LivestockRule.Targets EffectiveTargets()
        {
            var t = new LivestockRule.Targets { Min = EffectiveMin, Max = EffectiveTarget };
            if (PerBucket)
            {
                t.BucketMin = new int[LivestockRule.BucketCount];
                t.BucketMax = new int[LivestockRule.BucketCount];
                for (int b = 0; b < LivestockRule.BucketCount; b++)
                {
                    t.BucketMin[b] = StewardTuning.EffectiveTarget(Kind, Math.Max(0, BucketMin![b]));
                    t.BucketMax[b] = BucketMax![b] < 0 ? LivestockRule.NoLimit : StewardTuning.EffectiveTarget(Kind, BucketMax[b]);
                }
            }
            return t;
        }

        public void SetBuckets(int[]? min, int[]? max)
        {
            if (min == null || max == null) { BucketMin = null; BucketMax = null; return; }
            BucketMin = min.Take(LivestockRule.BucketCount).ToList();
            BucketMax = max.Take(LivestockRule.BucketCount).ToList();
            while (BucketMin.Count < LivestockRule.BucketCount) BucketMin.Add(0);
            while (BucketMax.Count < LivestockRule.BucketCount) BucketMax.Add(LivestockRule.NoLimit);
        }

        // ── Counting ─────────────────────────────────────────────────────────

        public static int BucketOf(Pawn p) => LivestockRule.Bucket(p.ageTracker?.Adult ?? true, p.gender == Gender.Female);

        /// <summary>Live, colony-owned animals of the species on this map.</summary>
        public IEnumerable<Pawn> OwnedAnimals =>
            Map.mapPawns.PawnsInFaction(Faction.OfPlayer).Where(p => p.kindDef == Species && !p.Dead && p.IsAnimal);

        /// <summary>Counts per bucket of owned animals (those already marked for slaughter count as leaving when excludeSlaughter).</summary>
        public int[] Counts(bool excludeSlaughter = false)
        {
            var counts = new int[LivestockRule.BucketCount];
            if (Species == null) return counts;
            foreach (var p in OwnedAnimals)
            {
                if (excludeSlaughter && Map.designationManager.DesignationOn(p, DesignationDefOf.Slaughter) != null) continue;
                counts[BucketOf(p)]++;
            }
            return counts;
        }

        public override int CurrentCount => Species == null ? 0 : OwnedAnimals.Count();

        public override bool CountMeetsTarget(int count)
        {
            if (Species == null) return true;
            return LivestockRule.Meets(EffectiveTargets(), PerBucket ? Counts() : new[] { count, 0, 0, 0 });
        }

        public override bool WantsWork => Species != null && !LivestockRule.Meets(EffectiveTargets(), Counts());

        public string CountsSummary() => LivestockRule.Summary(EffectiveTargets(), Counts());

        // ── Candidate rules ──────────────────────────────────────────────────

        /// <summary>Predators and exploding animals need the HuntPredators setting; revenge-prone kinds are fine to tame.</summary>
        public static bool MayTameKind(PawnKindDef? kind)
        {
            var race = kind?.RaceProps;
            if (race == null) return false;
            if (!race.predator && !HuntSafety.Explodes(race)) return true;
            return StewardMod.Settings?.stock?.HuntPredators == true;
        }

        public static string DangerReason(PawnKindDef? kind)
        {
            var race = kind?.RaceProps;
            if (race == null) return "unknown";
            if (race.predator) return "predator";
            if (HuntSafety.Explodes(race)) return "explodes";
            return "";
        }

        private bool IsTameCandidate(Pawn p) =>
            p.kindDef == Species
            && p.Spawned && !p.Dead && p.Map == Map
            && p.Faction == null
            && TameUtility.CanTame(p)
            && !p.InAggroMentalState
            && (p.MentalStateDef == null || !p.MentalStateDef.IsAggro)
            && Map.designationManager.DesignationOn(p) == null
            && IsReachable(p, PathEndMode.Touch);

        /// <summary>Pending tame designations (ours or the player's) on wild animals of the species.</summary>
        private IEnumerable<Designation> LiveTameDesignations() =>
            Map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.Tame)
                .Where(d => d.target.HasThing && d.target.Thing is Pawn p && p.kindDef == Species && !p.Dead && p.Faction == null);

        public static bool IsBonded(Pawn p) => p.relations != null && p.relations.DirectRelations.Any(r => r.def == PawnRelationDefOf.Bond && r.otherPawn != null && !r.otherPawn.Dead);
        public static bool IsPregnant(Pawn p) => p.health?.hediffSet != null && p.health.hediffSet.HasHediff(HediffDefOf.Pregnant);
        public static bool IsReserved(Pawn p)
        {
            var lord = p.GetLord();
            if (lord?.LordJob is LordJob_FormAndSendCaravan) return true;
            if (lord?.LordJob is LordJob_Ritual) return true;
            return CaravanFormingUtility.IsFormingCaravanOrDownedPawnToBeTakenByCaravan(p);
        }

        /// <summary>Never bonded, pregnant, reserved for a caravan/ritual, aggressive or already designated.</summary>
        public static string? SlaughterVeto(Pawn p)
        {
            if (IsBonded(p)) return "bonded";
            if (IsPregnant(p)) return "pregnant";
            if (IsReserved(p)) return "caravan/ritual";
            if (p.InAggroMentalState) return "aggressive";
            return null;
        }

        private bool IsSlaughterCandidate(Pawn p) =>
            p.Spawned && !p.Dead && p.Map == Map
            && Map.designationManager.DesignationOn(p) == null
            && SlaughterVeto(p) == null;

        // ── Gather ───────────────────────────────────────────────────────────

        public override object? Gather()
        {
            if (Species == null) { Note("no species"); return null; }
            CleanDeadDesignations();
            CleanDeadSlaughterDesignations();
            AdoptGameDesignations();

            var targets = EffectiveTargets();
            var have = Counts(excludeSlaughter: true);

            var wildByBucket = new List<Pawn>[LivestockRule.BucketCount];
            var cullByBucket = new List<Pawn>[LivestockRule.BucketCount];
            var pendingByBucket = new List<Designation>[LivestockRule.BucketCount];
            for (int b = 0; b < LivestockRule.BucketCount; b++) { wildByBucket[b] = new List<Pawn>(); cullByBucket[b] = new List<Pawn>(); pendingByBucket[b] = new List<Designation>(); }

            bool mayTame = Tame && MayTameKind(Species);
            if (mayTame)
            {
                var wild = GetThingsSorted(
                    Map.mapPawns.AllPawnsSpawned.Where(p => p.kindDef == Species).ToList(),
                    IsTameCandidate,
                    (p, dist) => -(p.GetStatValue(StatDefOf.Wildness) * 100f + dist));   // tamest and nearest first
                foreach (var p in wild) wildByBucket[BucketOf(p)].Add(p);
            }
            foreach (var d in LiveTameDesignations()) pendingByBucket[BucketOf((Pawn)d.target.Thing)].Add(d);
            if (Slaughter)
            {
                foreach (var p in OwnedAnimals.Where(IsSlaughterCandidate).OrderByDescending(p => p.ageTracker?.AgeBiologicalTicks ?? 0))
                    cullByBucket[BucketOf(p)].Add(p);   // oldest first within each bucket
            }

            var pending = pendingByBucket.Select(l => l.Count).ToArray();
            var wildCounts = wildByBucket.Select(l => l.Count).ToArray();
            var cullCounts = cullByBucket.Select(l => l.Count).ToArray();
            int budget = StewardMod.Settings.stock.MaxDesignationsPerJob - (_designations.Count + _slaughterDesignations.Count);
            var plan = LivestockRule.Compute(targets, have, pending, wildCounts, cullCounts, budget, mayTame, Slaughter);

            var data = new WorkData { Plan = plan };
            for (int b = 0; b < LivestockRule.BucketCount; b++)
            {
                data.ToTame.AddRange(wildByBucket[b].Take(plan.Tame[b]));
                data.ToSlaughter.AddRange(cullByBucket[b].Take(plan.Slaughter[b]));
                data.TameToRelease.AddRange(pendingByBucket[b].Where(d => _designations.Contains(d)).Take(plan.ReleaseTame[b]));
            }

            // Short of the minimum with nothing tameable: report as "no targets" so the stall rule and problems fire.
            int expected = have.Sum() + pending.Sum();
            bool shortOfMin = targets.PerBucket
                ? Enumerable.Range(0, LivestockRule.BucketCount).Any(b => have[b] + pending[b] < targets.MinOf(b))
                : expected < targets.MinOf(0);
            if (shortOfMin && plan.TameTotal == 0)
            {
                RunsWithoutTargets++;
                if (!Tame) Note($"below min but taming is off ({CountsSummary()})");
                else if (!mayTame) Note($"below min but {Species.label} is {DangerReason(Species)} (hunt_predators=true to tame)");
                else Note($"no tameable wild {Species.label} on the map ({CountsSummary()})");
            }
            else RunsWithoutTargets = 0;

            foreach (var p in OwnedAnimals)
            {
                if (!p.Spawned || p.Map != Map) continue;
                if (RestrictArea != null && p.playerSettings != null && p.playerSettings.AreaRestrictionInPawnCurrentMap != RestrictArea)
                    data.AreaTargets.Add(p);
                if (Train.Count > 0 && p.training != null)
                    foreach (var td in Train)
                        if (td != null && !p.training.GetWanted(td) && !p.training.HasLearned(td) && p.training.CanAssignToTrain(td).Accepted)
                            data.TrainTargets.Add((p, td));
            }

            if (data.Empty)
            {
                LastRunSummary = CountsSummary();
                return null;
            }
            return data;
        }

        private void CleanDeadSlaughterDesignations()
        {
            if (_slaughterDesignations.Count == 0) return;
            var live = new HashSet<Designation>(Map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.Slaughter));
            _slaughterDesignations.RemoveAll(d => !live.Contains(d));
        }

        /// <summary>
        /// Track player-made tame/slaughter designations on this species for status only: the plan already counts
        /// every live tame designation (LiveTameDesignations) and every slaughter-marked animal (Counts), and the job
        /// never releases designations it did not create.
        /// </summary>
        private void AdoptGameDesignations()
        {
            RebuildAdopted(
                (DesignationDefOf.Tame, d => d.target.HasThing && d.target.Thing is Pawn p && p.kindDef == Species && !p.Dead && p.Faction == null),
                (DesignationDefOf.Slaughter, d => d.target.HasThing && d.target.Thing is Pawn p && p.kindDef == Species && p.Faction == Faction.OfPlayer));
        }

        // ── Execute ──────────────────────────────────────────────────────────

        public override bool Execute(object dataObj)
        {
            if (!(dataObj is WorkData data) || Species == null) return false;
            var actions = new List<string>();

            int released = 0;
            foreach (var d in data.TameToRelease) { d.Delete(); _designations.Remove(d); released++; }
            if (released > 0) actions.Add($"released {released} tame designations");

            int tamed = 0;
            foreach (var p in data.ToTame)
            {
                if (p.DestroyedOrNull() || !p.Spawned || p.Dead || p.Faction != null) continue;
                if (Map.designationManager.DesignationOn(p) != null) continue;
                AddDesignation(new Designation(p, DesignationDefOf.Tame));
                tamed++;
            }
            if (tamed > 0) actions.Add($"marked {tamed} wild {Species.label} for taming");

            int culled = 0;
            foreach (var p in data.ToSlaughter)
            {
                if (p.DestroyedOrNull() || !p.Spawned || p.Dead || p.Faction != Faction.OfPlayer) continue;
                if (Map.designationManager.DesignationOn(p) != null || SlaughterVeto(p) != null) continue;
                var d = new Designation(p, DesignationDefOf.Slaughter);
                Map.designationManager.AddDesignation(d);
                _slaughterDesignations.Add(d);
                culled++;
            }
            if (culled > 0) actions.Add($"marked {culled} surplus {Species.label} for slaughter");

            int moved = 0;
            foreach (var p in data.AreaTargets)
            {
                if (p.DestroyedOrNull() || p.playerSettings == null || RestrictArea == null) continue;
                p.playerSettings.AreaRestrictionInPawnCurrentMap = RestrictArea;
                moved++;
            }
            if (moved > 0) actions.Add($"restricted {moved} to {RestrictArea?.Label}");

            int trained = 0;
            foreach (var (p, td) in data.TrainTargets)
            {
                if (p.DestroyedOrNull() || p.training == null) continue;
                p.training.SetWantedRecursive(td, true);
                trained++;
            }
            if (trained > 0) actions.Add($"set {trained} training goals");

            bool workDone = actions.Count > 0;
            if (workDone) Note($"{string.Join(", ", actions)} ({CountsSummary()})");
            else LastRunSummary = CountsSummary();
            return workDone;
        }

        public override void CleanUp()
        {
            CleanDeadDesignations();
            CleanDeadSlaughterDesignations();
            base.CleanUp();
            CleanUpDesignations(_slaughterDesignations);
        }

        public override void Notify_AreaRemoved(Area area)
        {
            if (RestrictArea == area) RestrictArea = null;
        }

        /// <summary>Training goals this species can take (visible and assignable for its race).</summary>
        public IEnumerable<TrainableDef> AvailableTrainables()
        {
            if (Species?.race == null) yield break;
            foreach (var td in TrainableUtility.TrainableDefsInListOrder)
            {
                if (td == TrainableDefOf.Tameness) continue;
                if (Pawn_TrainingTracker.CanAssignToTrain(td, Species.race, out bool visible).Accepted && visible) yield return td;
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref Species, "species");
            Scribe_Values.Look(ref Min, "min");
            Scribe_Values.Look(ref Tame, "tame", true);
            Scribe_Values.Look(ref Slaughter, "slaughter", true);
            Scribe_Collections.Look(ref BucketMin, "bucketMin", LookMode.Value);
            Scribe_Collections.Look(ref BucketMax, "bucketMax", LookMode.Value);
            Scribe_Collections.Look(ref Train, "train", LookMode.Def);
            ScribeAreaByLabel(ref RestrictArea, ref _restrictAreaScribe, "restrictArea", Map);
            ScribeSlaughterDesignations();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Train ??= new List<TrainableDef>();
                Train.RemoveAll(t => t == null);
                _slaughterDesignations ??= new List<Designation>();
                if (!PerBucket) { BucketMin = null; BucketMax = null; }
                if (Species != null && string.IsNullOrEmpty(Label)) Label = $"Livestock ({Species.label})";
            }
        }

        private void ScribeSlaughterDesignations()
        {
            if (Scribe.mode == LoadSaveMode.Saving && Map != null)
                _slaughterDesignations.RemoveAll(d => !Map.designationManager.AllDesignations.Contains(d));
            Scribe_Collections.Look(ref _slaughterDesignations, "slaughterDesignations", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                _slaughterDesignations ??= new List<Designation>();
                if (Map != null)
                    for (int i = 0; i < _slaughterDesignations.Count; i++)
                    {
                        var thing = _slaughterDesignations[i].target.Thing;
                        if (thing == null) continue;
                        _slaughterDesignations[i] = Map.designationManager.DesignationOn(thing, DesignationDefOf.Slaughter) ?? _slaughterDesignations[i];
                    }
            }
        }
    }
}
