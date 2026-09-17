using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBridge.Engine;
using RimBridge.Server;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RimBridge.State
{
    public static class StateRpc
    {
        static Map Map() { GameCtl.GameControl.RequirePlaying(); return Find.CurrentMap; }

        [Rpc("state.summary", "colony overview: date, colonists (brief), wealth, food, mood, threats, alerts, research, zones, power, key stocks, plus one named block per loaded add-on (e.g. steward {scorer, stock, posture, stock_brief, problems, orders_active: [order id], rally: bool} if the optional Steward mod is loaded)")]
        public static JToken Summary(JObject p)
        {
            var map = Map();
            var o = Snapshot.ColonySummary(map);
            foreach (var contribute in Hooks.SummaryContributors)
            {
                try { var (key, value) = contribute(map); o[key] = value; }
                catch (Exception ex) { BridgeLog.Warning("state.summary contributor: " + ex.Message); }
            }
            return o;
        }

        [Rpc("state.pawns", "{filter?: colonists|prisoners|animals|hostiles|all} list pawns (brief)")]
        public static JToken Pawns(JObject p)
        {
            var map = Map();
            string f = P.Str(p, "filter", "colonists");
            IEnumerable<Pawn> q = f switch
            {
                "colonists" => map.mapPawns.FreeColonists,
                "prisoners" => map.mapPawns.PrisonersOfColony,
                "animals" => map.mapPawns.SpawnedColonyAnimals,
                "hostiles" => map.mapPawns.AllPawnsSpawned.Where(x => x.HostileTo(Faction.OfPlayer)),
                "wild" => map.mapPawns.AllPawnsSpawned.Where(x => x.Faction == null && x.RaceProps.Animal),
                "all" => map.mapPawns.AllPawnsSpawned,
                _ => throw new RpcError("filter must be colonists|prisoners|animals|hostiles|wild|all"),
            };
            return new JArray(q.Take(200).Select(x => x.IsColonist ? Snapshot.PawnBrief(x) : (JToken)Render.PawnHandle(x)));
        }

        [Rpc("state.pawn", "{pawn: id|name} full pawn detail: skills, traits, health, needs, mood thoughts, gear, work priorities, schedule, policies, relations")]
        public static JToken Pawn(JObject p)
        {
            var pawn = Lookup.Pawn(P.Str(p, "pawn"));
            var o = Snapshot.PawnBrief(pawn);
            o["gender"] = pawn.gender.ToString();
            o["age"] = pawn.ageTracker.AgeBiologicalYears;
            o["race"] = pawn.def.defName;
            if (pawn.genes?.Xenotype != null) o["xenotype"] = pawn.genes.Xenotype.defName;
            if (pawn.Ideo != null) o["ideo"] = pawn.Ideo.name;
            if (pawn.story != null)
            {
                o["childhood"] = pawn.story.Childhood?.TitleCapFor(pawn.gender);
                o["adulthood"] = pawn.story.Adulthood?.TitleCapFor(pawn.gender);
                o["traits"] = new JArray(pawn.story.traits.allTraits.Select(t => (JToken)Render.Value(t, 0)));
                var disabled = pawn.GetDisabledWorkTypes(true).Select(w => w.defName).ToList();
                if (disabled.Count > 0) o["disabled_work"] = new JArray(disabled);
            }
            if (pawn.skills != null) o["skills"] = new JObject(pawn.skills.skills.Select(s => new JProperty(s.def.defName, s.TotallyDisabled ? "disabled" : (JToken)$"{s.Level}{(s.passion == Passion.Major ? "!!" : s.passion == Passion.Minor ? "!" : "")}")));
            // Health
            var hediffs = pawn.health.hediffSet.hediffs.Where(h => h.Visible).Select(h => (JToken)Render.Value(h, 0)).ToList();
            o["hediffs"] = new JArray(hediffs);
            o["capacities"] = new JObject(DefDatabase<PawnCapacityDef>.AllDefs.Where(c => c.showOnHumanlikes).Select(c => new JProperty(c.defName, Math.Round(pawn.health.capacities.GetLevel(c) * 100))));
            o["pain"] = Math.Round(pawn.health.hediffSet.PainTotal * 100);
            o["bleeding"] = Math.Round(pawn.health.hediffSet.BleedRateTotal, 2);
            o["needs_tending"] = pawn.health.HasHediffsNeedingTend();
            o["in_bed"] = pawn.InBed();
            o["medical_care"] = pawn.playerSettings?.medCare.ToString();
            o["hostility_response"] = pawn.playerSettings?.hostilityResponse.ToString();
            // Needs & mood
            if (pawn.needs != null)
            {
                o["needs"] = new JObject(pawn.needs.AllNeeds.Select(n => new JProperty(n.def.defName, Math.Round(n.CurLevelPercentage * 100))));
                if (pawn.needs.mood != null)
                {
                    var thoughts = new List<Thought>();
                    pawn.needs.mood.thoughts.GetDistinctMoodThoughtGroups(thoughts);
                    o["thoughts"] = new JArray(thoughts.OrderBy(t => t.MoodOffset()).Select(t => (JToken)Render.Value(t, 0)));
                    o["break_thresholds"] = new JArray(Math.Round(pawn.mindState.mentalBreaker.BreakThresholdMinor * 100), Math.Round(pawn.mindState.mentalBreaker.BreakThresholdMajor * 100), Math.Round(pawn.mindState.mentalBreaker.BreakThresholdExtreme * 100));
                }
            }
            // Gear
            o["equipment"] = new JArray(pawn.equipment?.AllEquipmentListForReading.Select(e => (JToken)Render.ThingHandle(e)) ?? Enumerable.Empty<JToken>());
            o["apparel"] = new JArray(pawn.apparel?.WornApparel.Select(a => (JToken)Render.ThingHandle(a)) ?? Enumerable.Empty<JToken>());
            o["inventory"] = new JArray(pawn.inventory?.innerContainer.Select(i => (JToken)Render.ThingHandle(i)) ?? Enumerable.Empty<JToken>());
            // Work / schedule / policies
            if (pawn.workSettings != null && pawn.workSettings.EverWork)
                o["work_priorities"] = new JObject(DefDatabase<WorkTypeDef>.AllDefs.OrderByDescending(w => w.naturalPriority).Select(w => new JProperty(w.defName, pawn.WorkTypeIsDisabled(w) ? "X" : (JToken)pawn.workSettings.GetPriority(w))));
            if (pawn.timetable != null) o["schedule"] = string.Concat(pawn.timetable.times.Select(t => t.defName[0]));
            if (pawn.outfits != null) o["apparel_policy"] = pawn.outfits.CurrentApparelPolicy?.label;
            if (pawn.foodRestriction != null) o["food_policy"] = pawn.foodRestriction.CurrentFoodPolicy?.label;
            if (pawn.drugs != null) o["drug_policy"] = pawn.drugs.CurrentPolicy?.label;
            if (pawn.reading != null) o["reading_policy"] = pawn.reading.CurrentPolicy?.label;
            if (pawn.playerSettings != null) o["allowed_area"] = pawn.playerSettings.AreaRestrictionInPawnCurrentMap?.Label ?? "Unrestricted";
            // Job
            if (pawn.CurJob != null) o["job_detail"] = Render.Value(pawn.CurJob, 0);
            o["queued_jobs"] = pawn.jobs?.jobQueue?.Count ?? 0;
            if (pawn.GetRoom() is { } room) o["room"] = Render.Value(room, 0);
            // Relations
            if (pawn.relations != null)
                o["relations"] = new JArray(pawn.relations.DirectRelations.Take(20).Select(r => new JObject { ["rel"] = r.def.defName, ["with"] = r.otherPawn?.LabelShort, ["with_id"] = r.otherPawn?.ThingID }));
            if (pawn.guest != null && pawn.IsPrisoner)
            {
                o["prisoner"] = new JObject { ["mode"] = pawn.guest.ExclusiveInteractionMode?.defName, ["resistance"] = Math.Round(pawn.guest.resistance, 1), ["will"] = Math.Round(pawn.guest.will, 1), ["recruitable"] = pawn.guest.Recruitable };
            }
            if (pawn.RaceProps.Animal && pawn.training != null)
                o["training"] = new JObject(DefDatabase<TrainableDef>.AllDefs.Where(t => pawn.training.CanAssignToTrain(t).Accepted).Select(t => new JProperty(t.defName, pawn.training.HasLearned(t) ? "learned" : pawn.training.GetWanted(t) ? "wanted" : "no")));
            return o;
        }

        [Rpc("state.stocks", "{category?: Foods|Manufactured|ResourcesRaw|Medicine|Weapons|Apparel|... , min?: 1} counted resources on the map (stored + loose, unforbidden), grouped by defName")]
        public static JToken Stocks(JObject p)
        {
            var map = Map();
            string? cat = P.OptStr(p, "category");
            int min = P.Int(p, "min", 1);
            var o = new JObject();
            ThingCategoryDef? cdef = cat != null ? Lookup.Def<ThingCategoryDef>(cat) : null;
            foreach (var kv in map.resourceCounter.AllCountedAmounts.OrderByDescending(kv => kv.Value))
            {
                if (kv.Value < min) continue;
                if (cdef != null && !(kv.Key.thingCategories?.Any(c => c == cdef || c.Parents.Contains(cdef)) ?? false)) continue;
                o[kv.Key.defName] = kv.Value;
            }
            return new JObject { ["counted"] = o, ["nutrition"] = Math.Round(map.resourceCounter.TotalHumanEdibleNutrition, 1), ["note"] = "counted = in stockpiles/storage only; use map.find for loose items" };
        }

        [Rpc("state.research", "current project, available projects (with prerequisites met), finished count")]
        public static JToken Research(JObject p)
        {
            Map();
            var cur = Find.ResearchManager.GetProject();
            var avail = DefDatabase<ResearchProjectDef>.AllDefs.Where(r => !r.IsFinished && r.CanStartNow).OrderBy(r => r.baseCost)
                .Select(r => new JObject { ["def"] = r.defName, ["label"] = r.label, ["cost"] = r.baseCost, ["tech"] = r.techLevel.ToString(), ["bench"] = r.requiredResearchBuilding?.defName, ["progress"] = Math.Round(r.ProgressPercent * 100), ["unlocks"] = string.Join(", ", r.UnlockedDefs.Take(8).Select(d => d.label)) });
            return new JObject
            {
                ["current"] = cur?.defName,
                ["current_progress"] = cur != null ? Math.Round(cur.ProgressPercent * 100) : 0,
                ["finished"] = new JArray(DefDatabase<ResearchProjectDef>.AllDefs.Where(r => r.IsFinished).Select(r => r.defName)),
                ["available"] = new JArray(avail),
                ["benches"] = new JArray(Find.CurrentMap.listerBuildings.allBuildingsColonist.Where(b => b is Building_ResearchBench).Select(b => Render.ThingHandle(b))),
            };
        }

        [Rpc("state.letters", "{include_archived?: false} letters waiting on screen (with choices where applicable)")]
        public static JToken Letters(JObject p)
        {
            Map();
            var arr = new JArray();
            foreach (var l in Find.LetterStack.LettersListForReading)
            {
                var o = new JObject { ["id"] = l.ID, ["label"] = l.Label.ToString().StripTags(), ["def"] = l.def.defName, ["tick"] = l.arrivalTick };
                if (l is ChoiceLetter cl)
                {
                    o["text"] = cl.Text.ToString().StripTags();
                    try { o["choices"] = new JArray(cl.Choices.Select(c => ((string)HarmonyLib.AccessTools.Field(typeof(DiaOption), "text").GetValue(c)).StripTags())); } catch { }
                    if (cl.quest != null) o["quest"] = cl.quest.id;
                }
                if (l.lookTargets != null && l.lookTargets.IsValid())
                {
                    var t = l.lookTargets.PrimaryTarget;
                    if (t.HasThing) o["target"] = t.Thing.ThingID; else if (t.Cell.IsValid) o["cell"] = Snapshot.Cell(t.Cell);
                }
                arr.Add(o);
            }
            return arr;
        }

        [Rpc("state.alerts", "active alert bar entries with explanations")]
        public static JToken Alerts(JObject p) { Map(); return Snapshot.Alerts(); }

        [Rpc("state.factions", "all factions with relations")]
        public static JToken Factions(JObject p)
        {
            Map();
            return new JArray(Find.FactionManager.AllFactionsVisible.Where(f => !f.IsPlayer).Select(f => new JObject
            {
                ["name"] = f.Name, ["def"] = f.def.defName, ["goodwill"] = f.PlayerGoodwill, ["relation"] = f.PlayerRelationKind.ToString(), ["hostile"] = f.HostileTo(Faction.OfPlayer), ["defeated"] = f.defeated, ["tech"] = f.def.techLevel.ToString(),
            }));
        }

        [Rpc("state.quests", "active and available quests")]
        public static JToken Quests(JObject p)
        {
            Map();
            return new JArray(Find.QuestManager.QuestsListForReading.Where(q => !q.hidden && (q.State == QuestState.NotYetAccepted || q.State == QuestState.Ongoing)).Select(q => new JObject
            {
                ["id"] = q.id, ["name"] = q.name, ["state"] = q.State.ToString(), ["description"] = q.description.ToString().StripTags(), ["expires_in_ticks"] = q.State == QuestState.NotYetAccepted ? q.TicksUntilExpiry : -1, ["challenge"] = q.challengeRating,
            }));
        }

        [Rpc("state.rooms", "rooms with role, cells, stats (impressiveness, beauty, cleanliness, temperature)")]
        public static JToken Rooms(JObject p)
        {
            var map = Map();
            var arr = new JArray();
            foreach (var r in map.regionGrid.AllRooms)
            {
                if (r.TouchesMapEdge || r.CellCount > 4000 || r.Role == null || r.Role == RoomRoleDefOf.None && r.CellCount > 500) continue;
                var o = new JObject { ["id"] = r.ID, ["role"] = r.Role?.defName, ["cells"] = r.CellCount, ["outdoors"] = r.PsychologicallyOutdoors, ["temp"] = Math.Round(r.Temperature), ["at"] = Snapshot.Cell(r.Cells.FirstOrDefault()) };
                if (!r.PsychologicallyOutdoors)
                {
                    o["impressiveness"] = Math.Round(r.GetStat(RoomStatDefOf.Impressiveness));
                    o["beauty"] = Math.Round(r.GetStat(RoomStatDefOf.Beauty));
                    o["cleanliness"] = Math.Round(r.GetStat(RoomStatDefOf.Cleanliness), 1);
                    o["owners"] = new JArray(r.Owners.Select(x => x.LabelShort));
                }
                arr.Add(o);
            }
            return arr;
        }

        [Rpc("state.bills", "{thing: id} bills on a work table")]
        public static JToken Bills(JObject p)
        {
            var t = Lookup.Thing(P.Str(p, "thing"));
            if (!(t is IBillGiver bg)) throw new RpcError($"{t.ThingID} has no bill stack");
            return new JArray(bg.BillStack.Bills.Select(b => new JObject
            {
                ["id"] = b.GetUniqueLoadID(), ["recipe"] = b.recipe.defName, ["label"] = b.LabelCap, ["suspended"] = b.suspended,
                ["mode"] = (b as Bill_Production)?.repeatMode?.defName, ["target"] = (b as Bill_Production)?.targetCount, ["repeat"] = (b as Bill_Production)?.repeatCount,
                ["ingredient_radius"] = b.ingredientSearchRadius, ["paused"] = (b as Bill_Production)?.paused,
            }));
        }

        [Rpc("state.designations", "{def?: Mine|CutPlant|Hunt|Haul|Deconstruct|...} designations on the map grouped by def with counts and sample cells")]
        public static JToken Designations(JObject p)
        {
            var map = Map();
            string? def = P.OptStr(p, "def");
            var groups = map.designationManager.AllDesignations.Where(d => def == null || d.def.defName == def).GroupBy(d => d.def.defName);
            return new JObject(groups.Select(g => new JProperty(g.Key, new JObject { ["count"] = g.Count(), ["sample"] = new JArray(g.Take(10).Select(d => d.target.HasThing ? (JToken)d.target.Thing.ThingID : Snapshot.Cell(d.target.Cell))) })));
        }

        [Rpc("state.storage", "all stockpiles/shelves with priority and allowed categories summary")]
        public static JToken Storage(JObject p)
        {
            var map = Map();
            var arr = new JArray();
            foreach (var z in map.zoneManager.AllZones.OfType<Zone_Stockpile>())
                arr.Add(new JObject { ["zone"] = z.label, ["priority"] = z.settings.Priority.ToString(), ["cells"] = z.Cells.Count, ["at"] = Snapshot.Cell(z.Cells[0]), ["allowed_count"] = z.settings.filter.AllowedDefCount, ["items"] = z.AllContainedThings.Count() });
            foreach (var b in map.listerBuildings.allBuildingsColonist.OfType<Building_Storage>())
                arr.Add(new JObject { ["thing"] = b.ThingID, ["def"] = b.def.defName, ["priority"] = b.settings.Priority.ToString(), ["at"] = Snapshot.Cell(b.Position), ["allowed_count"] = b.settings.filter.AllowedDefCount });
            return arr;
        }

        [Rpc("state.areas", "allowed areas (Home, animal pens, custom)")]
        public static JToken Areas(JObject p)
        {
            var map = Map();
            return new JArray(map.areaManager.AllAreas.Select(a => new JObject { ["label"] = a.Label, ["cells"] = a.TrueCount, ["mutable"] = a.Mutable }));
        }

        [Rpc("state.policies", "apparel/food/drug/reading policies available")]
        public static JToken Policies(JObject p)
        {
            Map();
            return new JObject
            {
                ["apparel"] = new JArray(Current.Game.outfitDatabase.AllOutfits.Select(x => x.label)),
                ["food"] = new JArray(Current.Game.foodRestrictionDatabase.AllFoodRestrictions.Select(x => x.label)),
                ["drug"] = new JArray(Current.Game.drugPolicyDatabase.AllPolicies.Select(x => x.label)),
                ["reading"] = new JArray(Current.Game.readingPolicyDatabase.AllReadingPolicies.Select(x => x.label)),
            };
        }

        [Rpc("state.threats", "hostile pawns/things on the map with positions, weapons, and distance to home; plus storyteller threat points")]
        public static JToken Threats(JObject p)
        {
            var map = Map();
            var home = Snapshot.HomeCenter(map);
            var arr = new JArray();
            foreach (var t in map.attackTargetsCache.TargetsHostileToColony)
            {
                var th = t.Thing;
                if (!th.Spawned) continue;
                var o = th is Pawn hp ? Render.PawnHandle(hp) : Render.ThingHandle(th);
                o["dist_home"] = (int)th.Position.DistanceTo(home);
                o["fogged"] = th.Position.Fogged(map);
                if (th is Pawn pp)
                {
                    o["weapon"] = pp.equipment?.Primary?.def.defName;
                    o["health"] = Math.Round(pp.health.summaryHealth.SummaryHealthPercent * 100);
                    o["lord"] = pp.GetLord()?.LordJob?.GetType().Name;
                    o["mental"] = pp.InMentalState ? pp.MentalStateDef?.defName : null;
                }
                arr.Add(o);
            }
            return new JObject { ["danger"] = map.dangerWatcher.DangerRating.ToString(), ["threat_points"] = Math.Round(StorytellerUtility.DefaultThreatPointsNow(map)), ["hostiles"] = arr, ["home_center"] = Snapshot.Cell(home) };
        }
    }
}
