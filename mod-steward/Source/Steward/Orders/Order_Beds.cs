// Written for RimBridge (2026): the beds standing order (spec order 5). Uses the game's own Pawn_Ownership /
// CompAssignableToPawn_Bed rules so assignments look exactly like the player's.
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RimBridge.Steward.Orders
{
    /// <summary>
    /// Free colonists without a bed claim one (lovers share a double bed when one has room, a couple with no beds gets an
    /// empty double bed, else the nearest empty bed); prisoners without a bed claim a prisoner bed in their own cell;
    /// beds in rooms whose role is Hospital become medical. A bed the director assigned/pressed by hand, or whose
    /// owners changed outside this order, is left alone for 2 days.
    /// </summary>
    public sealed class Order_Beds : Order
    {
        public override string Id => "beds";
        public override string Label => "Beds: owners, couples, prisoners, hospital";
        public override string Doc =>
            "Every 2500 ticks. Beds in a room with the Hospital role (some medical bed, no prisoner bed) are all set medical. Prisoners with no " +
            "bed claim a free prisoner bed in the room they stand in. Free colonists (not slaves, not babies) with no bed claim one: their love " +
            "partner's bed if it has a free slot, an empty double bed if the couple has no bed, else the nearest empty bed they can use " +
            "(ideology respected, never a bed with a stranger in it). Beds pressed by hand (ui.press) or whose owners changed outside this " +
            "order are hands-off for 2 days; existing owners are never reassigned.";
        public override int IntervalTicks => 2500;
        static readonly string[] Scopes = { "ui.press", "engine.set" };
        public override IReadOnlyList<string>? TouchScopes => Scopes;

        const string KeyPrefix = "bed:";
        static readonly string[] Prefixes = { KeyPrefix };
        public override IReadOnlyList<string> OwnedPrefixes => Prefixes;

        public override IEnumerable<string> Explain()
        {
            yield return "hospital: every humanlike, non-prisoner bed that can be medical in a room whose Role is Hospital → Medical = true (owners cleared by the game)";
            yield return "prisoners: prisoner without a bed → nearest free prisoner bed (ForPrisoners, not medical) in the same room";
            yield return "colonists: free colonist (not slave/baby) without a bed → partner's bed with a free slot; else (couple without beds) nearest empty double bed; else nearest empty usable bed; CanUseBedEver + CanAssignTo + ideology checked";
            yield return "never: reassign an owned bed; touch a bed pressed by hand (ui.press) or whose owners changed outside this order in the last 2 days; medical beds";
            yield return "assignment goes through Pawn_Ownership.ClaimBedIfNonMedical, exactly like the bed's assign gizmo";
        }

        public override OrderReport Run(Map map)
        {
            var g = StewardGame.Current;
            if (g == null) return OrderReport.Idle("no game state");
            int tick = Find.TickManager.TicksGame;
            var report = new OrderReport();
            var beds = map.listerBuildings.AllBuildingsColonistOfClass<Building_Bed>()
                .Where(b => b.Spawned && b.def.building != null && b.def.building.bed_humanlike).ToList();
            if (beds.Count == 0) { report.Summary = "no beds"; return report; }

            // ── manual-change detection ──
            var alive = new HashSet<string>();
            int manual = 0;
            foreach (var b in beds)
            {
                string key = KeyPrefix + b.ThingID;
                alive.Add(key);
                if (StandingOrders.IsTouched(b.ThingID, TouchScopes)) g.owned.MarkManual(key, tick, OwnedValues.TwoDays);
                if (g.owned.Observe(key, OwnersKey(b), tick, OwnedValues.TwoDays)) manual++;
            }
            g.owned.Prune(tick, alive.Where(k => k.StartsWith(KeyPrefix)).ToList().Concat(g.owned.Set.Keys.Where(k => !k.StartsWith(KeyPrefix))).ToList());
            bool Manual(Building_Bed b) => g.owned.IsManual(KeyPrefix + b.ThingID, tick);

            // ── hospital rooms ──
            int medical = 0;
            foreach (var b in beds)
            {
                if (b.Medical || b.ForPrisoners || Manual(b) || !b.def.building.bed_canBeMedical) continue;
                var room = b.GetRoom();
                if (room == null || room.PsychologicallyOutdoors || room.Role != RoomRoleDefOf.Hospital) continue;
                b.Medical = true;
                g.owned.Record(KeyPrefix + b.ThingID, OwnersKey(b));
                medical++; report.Act(b.ThingID);
            }

            // ── prisoners ──
            int prisoners = 0;
            foreach (var p in map.mapPawns.PrisonersOfColonySpawned.ToList())
            {
                if (p.ownership == null || p.ownership.OwnedBed != null || p.Dead) continue;
                var room = p.GetRoom();
                if (room == null) continue;
                var bed = beds.Where(b => b.ForPrisoners && !b.Medical && !Manual(b) && b.AnyUnownedSleepingSlot && b.OwnersForReading.Count == 0
                                          && b.GetRoom() == room && RestUtility.CanUseBedEver(p, b.def) && b.CompAssignableToPawn.CanAssignTo(p).Accepted)
                              .OrderBy(b => b.Position.DistanceToSquared(p.Position)).FirstOrDefault();
                if (bed == null) continue;
                if (p.ownership.ClaimBedIfNonMedical(bed)) { g.owned.Record(KeyPrefix + bed.ThingID, OwnersKey(bed)); prisoners++; report.Act(bed.ThingID); }
            }

            // ── colonists ──
            int assigned = 0, couples = 0, homeless = 0;
            var colonists = map.mapPawns.FreeColonistsSpawned.Where(p => p.IsFreeColonist && !p.IsSlaveOfColony && !p.Dead && p.ownership != null && !p.DevelopmentalStage.Baby()).ToList();
            foreach (var p in colonists)
            {
                if (p.ownership.OwnedBed != null && p.ownership.OwnedBed.Map == map) continue;
                var partner = LovePartnerRelationUtility.ExistingMostLikedLovePartner(p, false);
                bool partnerHere = partner != null && !partner.Dead && partner.Spawned && partner.Map == map && partner.IsFreeColonist && partner.ownership != null;
                var partnerBed = partnerHere ? partner!.ownership!.OwnedBed : null;
                bool partnerNeedsBed = partnerHere && partnerBed == null;
                var cands = new List<BedCandidate>();
                var byId = new Dictionary<string, Building_Bed>();
                foreach (var b in beds)
                {
                    if (b.ForPrisoners || b.ForSlaves || b.ForHumanBabies) continue;
                    var comp = b.CompAssignableToPawn;
                    bool usable = RestUtility.CanUseBedEver(p, b.def) && comp != null && comp.CanAssignTo(p).Accepted && !comp.IdeoligionForbids(p);
                    cands.Add(new BedCandidate(b.ThingID, b.SleepingSlotsCount, b.OwnersForReading.Count, b.Position.DistanceToSquared(p.Position),
                        partnerOwns: partnerBed == b, manual: Manual(b), medical: b.Medical, usable: usable));
                    byId[b.ThingID] = b;
                }
                var pick = BedPairing.Choose(cands, partnerNeedsBed);
                if (pick == null) { homeless++; continue; }
                var chosen = byId[pick];
                bool shared = chosen.OwnersForReading.Count > 0;
                if (!p.ownership.ClaimBedIfNonMedical(chosen)) { homeless++; continue; }
                g.owned.Record(KeyPrefix + chosen.ThingID, OwnersKey(chosen));
                assigned++; if (shared) couples++;
                report.Act(chosen.ThingID);
            }

            var parts = new List<string>();
            if (assigned > 0) parts.Add($"{assigned} colonist bed(s) assigned" + (couples > 0 ? $" ({couples} shared with a partner)" : ""));
            if (prisoners > 0) parts.Add($"{prisoners} prisoner bed(s) assigned");
            if (medical > 0) parts.Add($"{medical} hospital bed(s) set medical");
            if (homeless > 0) parts.Add($"{homeless} colonist(s) without a free bed");
            if (manual > 0) parts.Add($"{manual} bed(s) changed by hand (hands-off 2 days)");
            if (parts.Count == 0) parts.Add("everyone has a bed");
            report.Summary = string.Join(", ", parts);
            return report;
        }

        static string OwnersKey(Building_Bed b)
        {
            var ids = b.OwnersForReading.Where(o => o != null).Select(o => o.ThingID).OrderBy(s => s, StringComparer.Ordinal);
            return (b.Medical ? "M|" : "") + string.Join(",", ids);
        }
    }
}
