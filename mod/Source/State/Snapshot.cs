using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimBridge.State
{
    /// <summary>Shared summary builders used by state.* RPCs and the daily ledger snapshot.</summary>
    public static class Snapshot
    {
        public static JObject Daily(Map map)
        {
            var cols = map.mapPawns.FreeColonists;
            return new JObject
            {
                ["colonists"] = cols.Count,
                ["downed"] = cols.Count(p => p.Downed),
                ["prisoners"] = map.mapPawns.PrisonersOfColonyCount,
                ["animals"] = map.mapPawns.SpawnedColonyAnimals.Count,
                ["wealth"] = Math.Round(map.wealthWatcher.WealthTotal),
                ["wealth_items"] = Math.Round(map.wealthWatcher.WealthItems),
                ["wealth_buildings"] = Math.Round(map.wealthWatcher.WealthBuildings),
                ["mood_avg"] = cols.Count > 0 ? Math.Round(cols.Where(p => p.needs?.mood != null).Select(p => p.needs.mood.CurLevelPercentage).DefaultIfEmpty(0).Average() * 100) : 0,
                ["nutrition"] = Math.Round(map.resourceCounter.TotalHumanEdibleNutrition, 1),
                ["food_days"] = cols.Count > 0 ? Math.Round(map.resourceCounter.TotalHumanEdibleNutrition / (cols.Count * 1.6f), 1) : 0,
                ["threat_points"] = Math.Round(StorytellerUtility.DefaultThreatPointsNow(map)),
                ["research_done"] = DefDatabase<ResearchProjectDef>.AllDefs.Count(r => r.IsFinished),
                ["temp_outdoor"] = Math.Round(map.mapTemperature.OutdoorTemp),
                ["danger"] = map.dangerWatcher.DangerRating.ToString(),
                ["assisted"] = Ledger.EventLedger.Assisted,
            };
        }

        public static JObject ColonySummary(Map map)
        {
            var o = Daily(map);
            var cols = map.mapPawns.FreeColonists;
            o["date"] = GenDate.DateFullStringAt(Find.TickManager.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile));
            o["day"] = GenDate.DaysPassed;
            o["hour"] = GenLocalDate.HourInteger(map);
            o["season"] = GenLocalDate.Season(map).ToString();
            o["weather"] = map.weatherManager.curWeather?.defName;
            o["temp_seasonal"] = Math.Round(map.mapTemperature.SeasonalTemp);
            o["biome"] = map.Biome?.defName;
            o["growing_now"] = map.mapTemperature.SeasonalTemp > 0; // rough; plants need >0C (>10C for most crops)
            o["home_center"] = Cell(HomeCenter(map));
            o["colonist_list"] = new JArray(cols.Select(p => PawnBrief(p)));
            o["hostiles"] = new JArray(map.attackTargetsCache.TargetsHostileToColony.Where(t => t.Thing.Spawned && !t.ThreatDisabled(null)).Take(60).Select(t => t.Thing is Pawn hp ? (JToken)Engine.Render.PawnHandle(hp) : Engine.Render.ThingHandle(t.Thing)));
            o["alerts"] = Alerts();
            o["research_current"] = Find.ResearchManager.GetProject()?.defName;
            o["research_progress"] = Find.ResearchManager.GetProject() is { } rp ? Math.Round(rp.ProgressPercent * 100) : 0;
            o["pending_letters"] = Find.LetterStack.LettersListForReading.Count;
            o["zones"] = new JArray(map.zoneManager.AllZones.Select(z => new JObject { ["label"] = z.label, ["type"] = z is Zone_Growing zg ? "growing:" + zg.GetPlantDefToGrow()?.defName : (z is Zone_Stockpile ? "stockpile" : z.GetType().Name), ["cells"] = z.Cells.Count, ["at"] = Cell(z.Cells.Count > 0 ? z.Cells[0] : IntVec3.Invalid) }));
            o["blueprints"] = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count(b => b.Faction == Faction.OfPlayer);
            o["frames"] = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count(b => b.Faction == Faction.OfPlayer);
            o["designations"] = map.designationManager.AllDesignations.Count;
            o["power"] = PowerSummary(map);
            o["key_stocks"] = KeyStocks(map);
            o["outside_storage"] = OutsideStorage(map);
            o["room_digest"] = RoomDigest(map);
            o["speed"] = (int)Find.TickManager.CurTimeSpeed;
            o["paused"] = Find.TickManager.Paused;
            return o;
        }

        public static JObject PawnBrief(Pawn p)
        {
            var o = Engine.Render.PawnHandle(p);
            o["mood"] = p.needs?.mood != null ? Math.Round(p.needs.mood.CurLevelPercentage * 100) : (double?)null;
            o["health"] = Math.Round(p.health.summaryHealth.SummaryHealthPercent * 100);
            o["job"] = JobText(p);
            if (p.InMentalState) o["mental_state"] = p.MentalStateDef?.defName;
            if (p.health.hediffSet.BleedRateTotal > 0.01f) o["bleeding"] = Math.Round(p.health.hediffSet.BleedRateTotal, 2);
            if (p.health.HasHediffsNeedingTend()) o["needs_tending"] = true;
            var top = p.skills?.skills?.Where(s => !s.TotallyDisabled).OrderByDescending(s => s.Level).Take(3).Select(s => $"{s.def.defName} {s.Level}{(s.passion == Passion.Major ? "!!" : s.passion == Passion.Minor ? "!" : "")}");
            if (top != null) o["top_skills"] = string.Join(", ", top);
            o["weapon"] = p.equipment?.Primary?.LabelCap.ToString();
            return o;
        }

        public static string JobText(Pawn p)
        {
            try
            {
                if (p.Dead) return "dead";
                if (p.Downed) return "downed";
                if (p.jobs?.curDriver != null) return p.jobs.curDriver.GetReport().StripTags();
                return p.CurJob?.def?.defName ?? "idle";
            }
            catch { return p.CurJob?.def?.defName ?? "?"; }
        }

        public static JArray Alerts()
        {
            var arr = new JArray();
            try
            {
                if (!(Find.UIRoot is UIRoot_Play play)) return arr;
                var field = AccessTools.Field(typeof(AlertsReadout), "AllAlerts");
                if (field?.GetValue(play.alerts) is List<Alert> all)
                    foreach (var a in all)
                    {
                        if (!a.Active) continue;
                        string label;
                        try { label = a.GetLabel().StripTags(); } catch { continue; }
                        var jo = new JObject { ["label"] = label, ["priority"] = a.Priority.ToString() };
                        try { jo["explanation"] = Trunc(a.GetExplanation().ToString().StripTags(), 400); } catch { }
                        arr.Add(jo);
                    }
            }
            catch (Exception ex) { arr.Add(new JObject { ["error"] = ex.Message }); }
            return arr;
        }

        public static JObject PowerSummary(Map map)
        {
            float gen = 0, use = 0, stored = 0, cap = 0; int nets = 0;
            foreach (var net in map.powerNetManager.AllNetsListForReading)
            {
                nets++;
                gen += net.CurrentEnergyGainRate() * 60f; // Wd/tick -> W
                stored += net.CurrentStoredEnergy();
                foreach (var b in net.batteryComps) cap += b.Props.storedEnergyMax;
                foreach (var c in net.powerComps) if (c.PowerOn && c.PowerOutput < 0) use += -c.PowerOutput;
            }
            return new JObject { ["nets"] = nets, ["stored_wd"] = Math.Round(stored), ["capacity_wd"] = Math.Round(cap), ["consumption_w"] = Math.Round(use), ["net_gain_w"] = Math.Round(gen) };
        }

        static readonly string[] KeyDefs = { "WoodLog", "Steel", "Plasteel", "ComponentIndustrial", "Silver", "Gold", "MedicineHerbal", "MedicineIndustrial", "Cloth", "Leather_Plain", "MealSimple", "MealFine", "Pemmican", "RawRice", "RawPotatoes", "RawCorn", "Hay", "Kibble", "Chemfuel", "Uranium", "Jade", "Beer", "SmokeleafJoint", "Penoxycyline" };

        public static JObject KeyStocks(Map map)
        {
            var o = new JObject();
            foreach (var d in KeyDefs)
            {
                var def = DefDatabase<ThingDef>.GetNamedSilentFail(d);
                if (def == null) continue;
                int n = map.resourceCounter.GetCount(def);
                if (n > 0) o[d] = n;
            }
            o["meat_all"] = map.resourceCounter.GetCountIn(ThingCategoryDefOf.MeatRaw);
            o["stone_blocks"] = map.resourceCounter.GetCountIn(ThingCategoryDefOf.StoneBlocks);
            o["meals_all"] = map.resourceCounter.GetCountIn(DefDatabase<ThingCategoryDef>.GetNamed("FoodMeals"));
            return o;
        }

        /// <summary>Haulable items lying outside any storage — the things that rot, deteriorate and get stolen.</summary>
        public static JObject OutsideStorage(Map map)
        {
            int stacks = 0, food = 0, rotting = 0, corpses = 0, forbidden = 0, unroofed = 0, damaged = 0;
            var byCat = new Dictionary<string, int>();
            foreach (var t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (!t.Spawned || t.Position.Fogged(map) || t.IsInAnyStorage()) continue;
                if (t.def.thingCategories != null && t.def.thingCategories.Any(c => c.defName == "Chunks" || c.defName == "StoneChunks")) continue; // natural debris, not stock
                stacks++;
                if (t.IsForbidden(Faction.OfPlayer)) forbidden++;
                string cat = t.def.FirstThingCategory?.defName ?? t.def.category.ToString();
                byCat[cat] = byCat.TryGetValue(cat, out var n) ? n + 1 : 1;
                if (t.def.IsNutritionGivingIngestible) food++;
                if (t is Corpse) corpses++;
                var rot = t.TryGetComp<CompRottable>();
                if (rot != null && rot.Stage != RotStage.Fresh) rotting++;
                if (t.def.CanEverDeteriorate && !t.Position.Roofed(map)) unroofed++;   // deteriorates in the open (rain/sun)
                if (t.def.useHitPoints && t.HitPoints < t.MaxHitPoints) damaged++;
            }
            return new JObject { ["stacks"] = stacks, ["forbidden"] = forbidden, ["food_stacks"] = food, ["rotting"] = rotting, ["corpses"] = corpses, ["unroofed_deteriorating"] = unroofed, ["damaged"] = damaged, ["by_category"] = JObject.FromObject(byCat.OrderByDescending(kv => kv.Value).Take(10).ToDictionary(kv => kv.Key, kv => kv.Value)), ["storage_cells_free"] = FreeStorageCells(map) };
        }

        static int FreeStorageCells(Map map)
        {
            int free = 0;
            foreach (var z in map.zoneManager.AllZones.OfType<Zone_Stockpile>())
                foreach (var c in z.Cells) if (!c.GetThingList(map).Any(t => t.def.EverStorable(false))) free++;
            return free;
        }

        /// <summary>Indoor rooms by role with sizes — makes "everything is a 3x3 box" visible.</summary>
        public static JArray RoomDigest(Map map)
        {
            var arr = new JArray();
            foreach (var r in map.regionGrid.AllRooms.Where(r => !r.PsychologicallyOutdoors && !r.TouchesMapEdge && r.CellCount < 2000 && r.Role != null && r.Role != RoomRoleDefOf.None).OrderByDescending(r => r.CellCount).Take(12))
                arr.Add(new JObject { ["role"] = r.Role.defName, ["cells"] = r.CellCount, ["temp"] = Math.Round(r.Temperature), ["impressiveness"] = Math.Round(r.GetStat(RoomStatDefOf.Impressiveness)), ["owners"] = string.Join(",", r.Owners.Select(x => x.LabelShort)), ["at"] = Cell(r.Cells.FirstOrDefault()) });
            return arr;
        }

        public static IntVec3 HomeCenter(Map map)
        {
            var home = map.areaManager.Home;
            if (home != null && home.ActiveCells.Any())
            {
                long x = 0, z = 0; int n = 0;
                foreach (var c in home.ActiveCells) { x += c.x; z += c.z; n++; }
                return new IntVec3((int)(x / n), 0, (int)(z / n));
            }
            var bs = map.listerBuildings.allBuildingsColonist;
            if (bs.Count > 0) return new IntVec3((int)bs.Average(b => b.Position.x), 0, (int)bs.Average(b => b.Position.z));
            var cols = map.mapPawns.FreeColonistsSpawned;
            if (cols.Count > 0) return new IntVec3((int)cols.Average(p => p.Position.x), 0, (int)cols.Average(p => p.Position.z));
            return map.Center;
        }

        public static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        public static JToken Cell(IntVec3 c) => c.IsValid ? new JArray(c.x, c.z) : JValue.CreateNull();
    }
}
