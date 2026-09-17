// Written for RimBridge (2026): the rescue standing order. Rescue/tend job shapes follow the game's
// FloatMenuOptionProvider_RescuePawn and WorkGiver_Tend; sleeping-spot fallback adapted from Autopilot's
// SleepingSpotReflex (MIT, the user's own code).
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBridge.Steward.Stock;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimBridge.Steward.Orders
{
    /// <summary>
    /// Downed free colonists and colony animals that are not in a bed get a Rescue job from the nearest capable
    /// colonist (a medical sleeping spot is placed once when a colonist is downed and no bed exists). Colonists bleeding
    /// out within 6 hours with nobody tending get the best available doctor.
    /// </summary>
    public sealed class Order_Rescue : Order
    {
        public override string Id => "rescue";
        public override string Label => "Rescue: carry the downed, tend the bleeding";
        public override string Doc =>
            "Downed free colonists and colony animals lying outside a bed are carried to a free bed (JobDefOf.Rescue) by the nearest " +
            "non-downed, awake, undrafted colonist who can reach them. If a colonist is downed and no bed can take them, one medical " +
            "sleeping spot is placed near the nearest bed (or the base centre), once. Colonists bleeding with less than 6 in-game hours " +
            "to death and nobody tending them get a TendPatient job from the doctor with the best Medicine skill (patients must be " +
            "lying down; the rescue comes first). A patient the director already sent someone to (ui.job Rescue/TendPatient, or a right-click " +
            "Rescue/Tend order) is skipped for an hour; other touches on the patient (ui.attack, ui.draft, ui.goto) do not stop a rescue. A " +
            "downed or bleeding patient that is skipped for any reason still counts as acted on so the reason reaches the situation packet.";
        public override int IntervalTicks => 300;
        static readonly string[] Scopes = { "ui.job", "ui.order", "ui.draft", "ui.goto", "ui.attack" };
        public override IReadOnlyList<string>? TouchScopes => Scopes;

        public const int BleedOutTicks = 6 * 2500;
        const int ReissueTicks = 600;

        /// <summary>patient id → tick a job was issued (not persisted; avoids re-issuing every pass while the rescuer walks).</summary>
        private readonly Dictionary<string, int> _issued = new Dictionary<string, int>();

        public override IEnumerable<string> Explain()
        {
            yield return "rescue: free colonists and colony animals that are downed, alive and not in a bed; skipped while a Rescue job already targets them or the director already sent someone (ui.job:Rescue, ui.job:TendPatient, a right-click Rescue/Tend order) — ui.attack/draft/goto on the patient do not count";
            yield return "rescuer: nearest free colonist that is not downed, awake, not in a mental state, not drafted (nor drafted by the combat order), not on a player-forced job, not touched (ui.job/order/draft/goto/attack), and can reach the patient";
            yield return "packet: a downed/bleeding patient that is skipped (hands-off, no bed, nobody can rescue, no doctor) still counts as acted on, with the reason in the summary";
            yield return "bed: RestUtility.FindBedFor (own/medical/any suitable bed, then ignoring other reservations); none + downed colonist → one medical sleeping spot placed near the nearest bed or the base centre";
            yield return $"tend: colonists with time-to-death by blood loss < 6 h ({BleedOutTicks} ticks) needing tending, nobody tending → doctor with best Medicine skill (Caring allowed, Doctor work not disabled) gets TendPatient with the best medicine allowed";
            yield return "ledger: rescue {pawn, by, action: rescue|tend}";
        }

        public override OrderReport Run(Map map)
        {
            var g = StewardGame.Current;
            if (g == null) return OrderReport.Idle("no game state");
            int tick = Find.TickManager.TicksGame;
            var report = new OrderReport();
            var notes = new List<string>();
            var colonists = map.mapPawns.FreeColonistsSpawned.ToList();

            // ── downed → rescue ──
            var downed = new List<Pawn>();
            foreach (var p in colonists) if (p.Downed && !p.Dead && !p.InBed()) downed.Add(p);
            foreach (var a in map.mapPawns.SpawnedColonyAnimals) if (a.Downed && !a.Dead && !a.InBed()) downed.Add(a);

            var combat = g.Combat(map);
            int rescued = 0, waiting = 0;
            foreach (var patient in downed)
            {
                if (Handled(g, patient, tick, out string handledBy)) { report.Act(patient.ThingID); notes.Add($"{patient.LabelShort}: hands-off ({handledBy})"); continue; }
                if (BeingRescued(colonists, patient)) { report.Act(patient.ThingID); waiting++; continue; }
                if (_issued.TryGetValue(patient.ThingID, out int at) && tick - at < ReissueTicks) { report.Act(patient.ThingID); waiting++; continue; }
                var rescuer = PickRescuer(map, combat, colonists, patient);
                if (rescuer == null) { report.Act(patient.ThingID); notes.Add($"{patient.LabelShort}: nobody can rescue"); continue; }
                var bed = FindBed(patient, rescuer);
                if (bed == null && patient.RaceProps.Humanlike)
                {
                    var spot = PlaceMedicalSpot(map, g);
                    if (spot != null) { notes.Add($"placed medical sleeping spot at {spot.Position.x},{spot.Position.z}"); bed = FindBed(patient, rescuer); }
                }
                if (bed == null) { report.Act(patient.ThingID); notes.Add($"{patient.LabelShort}: no bed"); continue; }
                var job = JobMaker.MakeJob(JobDefOf.Rescue, patient, bed);
                job.count = 1;
                if (!rescuer.jobs.TryTakeOrderedJob(job, JobTag.Misc)) { report.Act(patient.ThingID); notes.Add($"{patient.LabelShort}: {rescuer.LabelShort} refused the job"); continue; }
                _issued[patient.ThingID] = tick;
                report.Act(patient.ThingID);
                rescued++;
                StewardLedger.Orders("rescue", $"{rescuer.LabelShort} rescues {patient.LabelShort}",
                    new JObject { ["pawn"] = patient.ThingID, ["name"] = patient.LabelShort, ["by"] = rescuer.ThingID, ["action"] = "rescue", ["bed"] = bed.ThingID });
            }

            // ── bleeding → tend ──
            int tended = 0;
            foreach (var patient in colonists)
            {
                if (patient.Dead || patient.health == null) continue;
                int ttd = HealthUtility.TicksUntilDeathDueToBloodLoss(patient);
                if (ttd >= BleedOutTicks) continue;
                if (!patient.health.HasHediffsNeedingTendByPlayer()) continue;
                if (Handled(g, patient, tick, out string handledBy)) { report.Act(patient.ThingID); notes.Add($"{patient.LabelShort}: bleeding ({ttd / 2500f:0.0}h), hands-off ({handledBy})"); continue; }
                if (BeingTended(colonists, patient)) { report.Act(patient.ThingID); continue; }
                if (_issued.TryGetValue("tend:" + patient.ThingID, out int at) && tick - at < ReissueTicks) { report.Act(patient.ThingID); continue; }
                if (patient.RaceProps.Humanlike ? !patient.InBed() : patient.GetPosture() == PawnPosture.Standing)
                {
                    report.Act(patient.ThingID);
                    notes.Add($"{patient.LabelShort}: bleeding ({ttd / 2500f:0.0}h) but not lying down");
                    continue;
                }
                var doctor = PickDoctor(map, combat, colonists, patient);
                if (doctor == null) { report.Act(patient.ThingID); notes.Add($"{patient.LabelShort}: bleeding ({ttd / 2500f:0.0}h), no doctor available"); continue; }
                var job = MakeTendJob(doctor, patient);
                if (!doctor.jobs.TryTakeOrderedJob(job, JobTag.Misc)) { report.Act(patient.ThingID); notes.Add($"{patient.LabelShort}: {doctor.LabelShort} refused to tend"); continue; }
                _issued["tend:" + patient.ThingID] = tick;
                report.Act(patient.ThingID);
                tended++;
                StewardLedger.Orders("rescue", $"{doctor.LabelShort} tends {patient.LabelShort} ({ttd / 2500f:0.0}h to bleed out)",
                    new JObject { ["pawn"] = patient.ThingID, ["name"] = patient.LabelShort, ["by"] = doctor.ThingID, ["action"] = "tend", ["hours_to_death"] = Math.Round(ttd / 2500f, 1) });
            }

            // forget stale issue records
            foreach (var k in _issued.Keys.ToList()) if (tick - _issued[k] > ReissueTicks * 4) _issued.Remove(k);

            var parts = new List<string>();
            if (rescued > 0) parts.Add($"{rescued} rescue job(s) issued");
            if (waiting > 0) parts.Add($"{waiting} being carried");
            if (tended > 0) parts.Add($"{tended} tend job(s) issued");
            if (parts.Count == 0 && notes.Count == 0) parts.Add(downed.Count == 0 ? "nobody downed" : "nothing to do");
            report.Summary = string.Join(", ", parts.Concat(notes.Take(3)));
            return report;
        }

        static bool BeingRescued(List<Pawn> colonists, Pawn patient)
        {
            foreach (var c in colonists)
            {
                var j = c.CurJob;
                if (j != null && (j.def == JobDefOf.Rescue || j.def == JobDefOf.TakeToBedToOperate || j.def == JobDefOf.CarryDownedPawnToExit) && j.targetA.Thing == patient) return true;
                if (c.jobs?.jobQueue != null)
                    foreach (var q in c.jobs.jobQueue) if (q.job?.def == JobDefOf.Rescue && q.job.targetA.Thing == patient) return true;
            }
            return false;
        }

        static bool BeingTended(List<Pawn> colonists, Pawn patient)
        {
            foreach (var c in colonists)
            {
                var j = c.CurJob;
                if (j != null && j.def == JobDefOf.TendPatient && j.targetA.Thing == patient) return true;
            }
            return false;
        }

        /// <summary>The director already sent someone to this patient (ui.job Rescue/TendPatient or a right-click Rescue/Tend order still in cooldown).</summary>
        static bool Handled(StewardGame g, Pawn patient, int tick, out string by)
        {
            by = "";
            foreach (var (reason, ticksLeft) in g.touches.ReasonsFor(patient.ThingID, tick))
            {
                if (!RescueRules.PatientHandled(reason)) continue;
                by = $"{reason}, {ticksLeft / 2500f:0.0} h left";
                return true;
            }
            return false;
        }

        static bool Available(Pawn c, CombatState combat, IReadOnlyList<string>? scopes)
        {
            if (c.Dead || c.Downed || !c.Spawned || !c.Awake() || c.InMentalState) return false;
            if (c.Drafted || combat.drafted.Contains(c.ThingID)) return false;
            if (c.CurJob != null && c.CurJob.playerForced) return false;
            if (StandingOrders.IsTouched(c.ThingID, scopes)) return false;
            return true;
        }

        Pawn? PickRescuer(Map map, CombatState combat, List<Pawn> colonists, Pawn patient)
        {
            Pawn? best = null; float bd = float.MaxValue;
            foreach (var c in colonists)
            {
                if (c == patient || !Available(c, combat, TouchScopes)) continue;
                if (c.DevelopmentalStage.Baby()) continue;
                if (!c.CanReserveAndReach(patient, PathEndMode.OnCell, Danger.Deadly, 1, -1, null, true)) continue;
                float d = c.Position.DistanceToSquared(patient.Position);
                if (d < bd) { bd = d; best = c; }
            }
            return best;
        }

        Pawn? PickDoctor(Map map, CombatState combat, List<Pawn> colonists, Pawn patient)
        {
            Pawn? best = null; int bestSkill = -1; float bd = float.MaxValue;
            foreach (var c in colonists)
            {
                if (c == patient || !Available(c, combat, TouchScopes)) continue;
                if (c.WorkTagIsDisabled(WorkTags.Caring)) continue;
                if (c.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)) continue;
                if (!c.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Deadly, 1, -1, null, true)) continue;
                int skill = c.skills?.GetSkill(SkillDefOf.Medicine)?.Level ?? 0;
                float d = c.Position.DistanceToSquared(patient.Position);
                if (skill > bestSkill || (skill == bestSkill && d < bd)) { bestSkill = skill; bd = d; best = c; }
            }
            return best;
        }

        static Job MakeTendJob(Pawn doctor, Pawn patient)
        {
            Thing? med = HealthAIUtility.FindBestMedicine(doctor, patient);
            Job job;
            if (med != null && med.SpawnedParentOrMe != med) job = JobMaker.MakeJob(JobDefOf.TendPatient, patient, med, med.SpawnedParentOrMe);
            else if (med != null) job = JobMaker.MakeJob(JobDefOf.TendPatient, patient, med);
            else job = JobMaker.MakeJob(JobDefOf.TendPatient, patient);
            job.count = 1;
            return job;
        }

        static Building_Bed? FindBed(Pawn patient, Pawn rescuer)
        {
            return RestUtility.FindBedFor(patient, rescuer, false)
                ?? RestUtility.FindBedFor(patient, rescuer, false, true);
        }

        /// <summary>One free medical sleeping spot (WorkToBuild 0 → spawned directly like Designator_Build does), only while the previous one is gone.</summary>
        Building_Bed? PlaceMedicalSpot(Map map, StewardGame g)
        {
            var def = ThingDefOf.SleepingSpot;
            if (def == null) return null;
            if (!string.IsNullOrEmpty(g.rescueSpotId))
            {
                bool exists = map.listerThings.ThingsOfDef(def).Any(t => t.ThingID == g.rescueSpotId && !t.Destroyed);
                if (exists) return null;
                g.rescueSpotId = null;
            }
            var beds = map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>().Where(b => b.def.building.bed_humanlike && !b.ForPrisoners).ToList();
            var center = ProductCounter.GetBaseCenter(map);
            IntVec3 near = beds.Count > 0 ? beds.OrderBy(b => b.Position.DistanceToSquared(center)).First().Position : center;
            IntVec3 best = IntVec3.Invalid;
            foreach (var c in GenRadial.RadialCellsAround(near, 14f, true))
            {
                if (!c.InBounds(map) || !c.Standable(map) || c.Fogged(map)) continue;
                if (!GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map).Accepted) continue;
                var room = c.GetRoom(map);
                bool indoors = room != null && !room.PsychologicallyOutdoors && c.Roofed(map);
                if (indoors) { best = c; break; }
                if (!best.IsValid) best = c;
            }
            if (!best.IsValid) return null;
            var spot = ThingMaker.MakeThing(def) as Building_Bed;
            if (spot == null) return null;
            spot.SetFactionDirect(Faction.OfPlayer);
            GenSpawn.Spawn(spot, best, map, Rot4.North);
            spot.Medical = true;
            g.rescueSpotId = spot.ThingID;
            StewardLog.Message($"orders: rescue placed a medical sleeping spot at {best.x},{best.z}");
            return spot;
        }
    }
}
