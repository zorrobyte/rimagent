// Written for RimBridge (2026): the corpses standing order. Butcher-bill/spot logic adapted from Autopilot's
// ButcherReflex (MIT, the user's own code); routing per STANDING_ORDERS_SPEC order 4.
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBridge.Steward.Stock;
using RimWorld;
using Verse;

namespace RimBridge.Steward.Orders
{
    /// <summary>
    /// Human corpses → haul designation toward a free grave/sarcophagus, else strip + haul to a stockpile that accepts
    /// corpses, else a standing cremation bill. Fresh animal corpses → one standing butcher bill (a butcher spot is
    /// placed once when no table exists). Rotten corpses → haul to a stockpile that accepts rotten corpses (a dumping
    /// stockpile), else cremation for humans, else nothing. Never during combat, never inside hostile structures.
    /// </summary>
    public sealed class Order_Corpses : Order
    {
        public override string Id => "corpses";
        public override string Label => "Corpses: bury, butcher, burn";
        public override string Doc =>
            "Every 1500 ticks. Human corpses: if an empty grave or sarcophagus accepts the corpse it is unforbidden and gets a Haul designation " +
            "(one corpse per free grave); else, if a stockpile/shelf accepts corpses, it is stripped (Strip designation) and hauled there; else, " +
            "if a work table with the cremation recipe exists, one standing 'cremate corpse' bill (forever, humanlike corpses, rotten allowed) is " +
            "kept and the corpse is unforbidden. Fresh animal corpses: one standing 'butcher creature' bill (forever) on a butcher table/spot; " +
            "if none exists a butcher spot is placed once near the kitchen or the main stockpile. Rotten/dessicated corpses: hauled to a stockpile " +
            "that accepts rotten corpses (dumping stockpile) if one exists, else rotten human corpses go to cremation, else nothing. A standing bill " +
            "that exists in any state (suspended too) is never duplicated; a bill this order created that the director deleted is not re-created " +
            "for 2 days. Skipped: while combat is engaged, corpses inside hostile-owned rooms, corpses the director forbade by hand, fogged or " +
            "unreachable cells.";
        public override int IntervalTicks => 1500;
        static readonly string[] Scopes = { "ui.designate" };
        public override IReadOnlyList<string>? TouchScopes => Scopes;
        static readonly string[] Prefixes = { "bill:" };
        public override IReadOnlyList<string> OwnedPrefixes => Prefixes;

        public const string ButcherRecipe = "ButcherCorpseFlesh";
        public const string CremateRecipe = "CremateCorpse";
        public const string ButcherSpotDef = "ButcherSpot";

        public override IEnumerable<string> Explain()
        {
            yield return "guard: nothing while the combat order is engaged; corpses in rooms with hostile-faction buildings, fogged, unreachable or forbidden by hand (ui.designate) are left alone";
            yield return "rotten/dessicated (any race): Haul designation + unforbid if a stockpile accepts rotten corpses; else humans → cremation bill; else nothing";
            yield return "human, fresh: empty grave/sarcophagus that accepts it → unforbid + Haul designation (one per grave); else stockpile accepting corpses → Strip + Haul; else crematorium/campfire with the cremate recipe → one standing cremate bill + unforbid";
            yield return $"animal, fresh: one standing '{ButcherRecipe}' bill (repeat forever, rotten disallowed) on the first butcher table/spot; corpses near the base are unforbidden; no table → one {ButcherSpotDef} placed near the kitchen/stockpile (once)";
            yield return "bills: a bill of the recipe in any state (suspended counts) means present; a bill this order created and the director deleted is not re-created for 2 days (owned key bill:<recipe>)";
            yield return "ledger: corpses {buried, butcher_bill, burned, hauled, dumped} only when something new was designated";
            var g = StewardGame.Current;
            if (g != null && !string.IsNullOrEmpty(g.butcherSpotId)) yield return $"butcher spot placed by this order: {g.butcherSpotId}";
        }

        public override OrderReport Run(Map map)
        {
            var g = StewardGame.Current;
            if (g == null) return OrderReport.Idle("no game state");
            if (g.CombatEngaged(map)) return OrderReport.Idle("combat engaged; corpses left alone");
            int tick = Find.TickManager.TicksGame;
            var report = new OrderReport();
            var corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>().Where(c => c.Spawned && c.InnerPawn != null).ToList();
            if (corpses.Count == 0) { report.Summary = "no corpses on the map"; return report; }

            var home = map.areaManager.Home;
            bool hasHome = home != null && home.TrueCount > 0;
            var center = ProductCounter.GetBaseCenter(map);
            var roomHostile = new Dictionary<int, bool>();
            var freeGraves = map.listerBuildings.AllBuildingsColonistOfClass<Building_Grave>().Where(gr => !gr.HasCorpse && !gr.HasAnyContents).ToList();
            var slotGroups = map.haulDestinationManager.AllGroupsListForReading;
            var butcherRecipe = DefDatabase<RecipeDef>.GetNamedSilentFail(ButcherRecipe);
            var cremateRecipe = DefDatabase<RecipeDef>.GetNamedSilentFail(CremateRecipe);
            var tables = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().ToList();
            var butcherTables = butcherRecipe == null ? new List<Building_WorkTable>() : tables.Where(t => t.def.AllRecipes != null && t.def.AllRecipes.Contains(butcherRecipe)).ToList();
            var cremateTables = cremateRecipe == null ? new List<Building_WorkTable>() : tables.Where(t => t.def.AllRecipes != null && t.def.AllRecipes.Contains(cremateRecipe)).ToList();

            int buried = 0, hauled = 0, burned = 0, dumped = 0, animals = 0, skipped = 0, stored = 0;
            int newBuried = 0, newBurned = 0, newHauled = 0, newDumped = 0;
            bool butcherBillCreated = false, cremateBillCreated = false;
            string? spotNote = null;

            foreach (var corpse in corpses)
            {
                if (corpse.Position.Fogged(map)) continue;
                var race = corpse.InnerPawn.RaceProps;
                if (race == null || race.IsMechanoid) continue;
                var rot = corpse.GetRotStage();
                bool inScope = hasHome && home![corpse.Position] || corpse.Position.DistanceTo(center) <= UnforbidRules.BaseRadius;
                var f = new CorpseFacts
                {
                    Humanlike = race.Humanlike,
                    Animal = race.Animal,
                    Rotten = rot != RotStage.Fresh,
                    CombatActive = false,
                    Touched = Touched(corpse),
                    HostileCamp = Order_Unforbid.InHostileStructure(map, corpse.Position, roomHostile),
                    Crematorium = cremateTables.Count > 0,
                };
                if (f.Touched || f.HostileCamp) { skipped++; continue; }
                if (!map.reachability.CanReachColony(corpse.Position)) { skipped++; continue; }
                Building_Grave? grave = null;
                if (f.Humanlike && !f.Rotten)
                {
                    grave = freeGraves.FirstOrDefault(gr => gr.Accepts(corpse));
                    f.FreeGrave = grave != null;
                }
                if (grave == null) f.StockpileAccepts = slotGroups.Any(sg => sg.Settings != null && sg.Settings.AllowedToAccept(corpse));

                var action = CorpseRouting.Decide(f);
                switch (action)
                {
                    case CorpseAction.Bury:
                        freeGraves.Remove(grave!);
                        if (Route(map, corpse, strip: false)) newBuried++;
                        if (Pending(map, corpse)) { buried++; report.Act(corpse.ThingID); } else stored++;
                        break;
                    case CorpseAction.StripAndHaul:
                        if (Route(map, corpse, strip: true)) newHauled++;
                        if (Pending(map, corpse)) { hauled++; report.Act(corpse.ThingID); } else stored++;
                        break;
                    case CorpseAction.Dump:
                        if (Route(map, corpse, strip: false)) newDumped++;
                        if (Pending(map, corpse)) { dumped++; report.Act(corpse.ThingID); } else stored++;
                        break;
                    case CorpseAction.Cremate:
                        if (!cremateBillCreated && EnsureBill(cremateTables, cremateRecipe!, cremate: true, g, tick, ref g.cremateBillId)) cremateBillCreated = true;
                        if (corpse.IsForbidden(Faction.OfPlayer)) { corpse.SetForbidden(false, false); newBurned++; }
                        else if (cremateBillCreated) newBurned++;
                        burned++; report.Act(corpse.ThingID);
                        break;
                    case CorpseAction.Butcher:
                        animals++; report.Act(corpse.ThingID);
                        if (inScope && corpse.IsForbidden(Faction.OfPlayer)) corpse.SetForbidden(false, false);
                        break;
                }
            }

            string butcherState = "standing";
            if (animals > 0 && butcherRecipe != null)
            {
                if (butcherTables.Count > 0)
                {
                    butcherBillCreated = EnsureBill(butcherTables, butcherRecipe, cremate: false, g, tick, ref g.butcherBillId);
                    if (butcherBillCreated) butcherState = "added";
                    else if (butcherTables.Any(t => t.BillStack.Bills.OfType<Bill_Production>().Any(b => b.recipe == butcherRecipe && b.suspended))) butcherState = "suspended by hand";
                    else if (!butcherTables.Any(t => t.BillStack.Bills.OfType<Bill_Production>().Any(b => b.recipe == butcherRecipe))) butcherState = "deleted by hand (not re-created for 2 days)";
                }
                else spotNote = PlaceButcherSpot(map, g);
            }

            if (newBuried > 0 || newBurned > 0 || newHauled > 0 || newDumped > 0 || butcherBillCreated || cremateBillCreated)
            {
                var bits = new List<string>();
                if (newBuried > 0) bits.Add($"{newBuried} to graves");
                if (newHauled > 0) bits.Add($"{newHauled} stripped+hauled");
                if (newBurned > 0) bits.Add($"{newBurned} to cremation");
                if (newDumped > 0) bits.Add($"{newDumped} rotten dumped");
                if (butcherBillCreated) bits.Add("butcher bill added");
                if (cremateBillCreated) bits.Add("cremate bill added");
                StewardLedger.Orders("corpses", string.Join(", ", bits), new JObject
                {
                    ["buried"] = newBuried, ["butcher_bill"] = butcherBillCreated ? 1 : 0, ["burned"] = newBurned,
                    ["hauled"] = newHauled, ["dumped"] = newDumped, ["animal_corpses"] = animals,
                });
            }

            var parts = new List<string>();
            if (buried > 0) parts.Add($"{buried} to graves");
            if (hauled > 0) parts.Add($"{hauled} strip+haul");
            if (burned > 0) parts.Add($"{burned} cremating" + (cremateBillCreated ? " (bill added)" : ""));
            if (dumped > 0) parts.Add($"{dumped} rotten to dump");
            if (animals > 0) parts.Add($"{animals} animal corpse(s)" + (butcherTables.Count > 0 ? $", butcher bill {butcherState}" : ", no butcher table"));
            if (spotNote != null) parts.Add(spotNote);
            if (stored > 0) parts.Add($"{stored} already stored");
            if (skipped > 0) parts.Add($"{skipped} skipped (hands-off/hostile/unreachable)");
            if (parts.Count == 0) parts.Add($"{corpses.Count} corpse(s), nothing routable (no grave, corpse stockpile or crematorium)");
            report.Summary = string.Join(", ", parts);
            return report;
        }

        /// <summary>Unforbid + Haul designation (+ Strip). Returns true when something changed.</summary>
        static bool Route(Map map, Corpse corpse, bool strip)
        {
            bool changed = false;
            if (corpse.IsForbidden(Faction.OfPlayer)) { corpse.SetForbidden(false, false); changed = true; }
            var dm = map.designationManager;
            if (strip && StrippableUtility.CanBeStrippedByColony(corpse) && dm.DesignationOn(corpse, DesignationDefOf.Strip) == null)
            {
                dm.AddDesignation(new Designation(corpse, DesignationDefOf.Strip));
                changed = true;
            }
            if (!corpse.IsInValidBestStorage() && dm.DesignationOn(corpse, DesignationDefOf.Haul) == null)
            {
                dm.AddDesignation(new Designation(corpse, DesignationDefOf.Haul));
                changed = true;
            }
            return changed;
        }

        /// <summary>A haul/strip is still outstanding (designated or not yet in its best storage).</summary>
        static bool Pending(Map map, Corpse corpse)
        {
            var dm = map.designationManager;
            return dm.DesignationOn(corpse, DesignationDefOf.Haul) != null || dm.DesignationOn(corpse, DesignationDefOf.Strip) != null || !corpse.IsInValidBestStorage();
        }

        /// <summary>
        /// One standing bill of this recipe across the tables (a suspended one counts as present); returns true when it was
        /// added now. The created bill's id is remembered in `ownId`: when it disappears the director deleted it and the bill
        /// is not re-created for 2 days (owned key "bill:&lt;recipe&gt;").
        /// </summary>
        static bool EnsureBill(List<Building_WorkTable> tables, RecipeDef recipe, bool cremate, StewardGame g, int tick, ref string? ownId)
        {
            if (tables.Count == 0) return false;
            string key = "bill:" + recipe.defName;
            Bill_Production? existing = null;
            foreach (var t in tables)
            {
                existing = t.BillStack.Bills.OfType<Bill_Production>().FirstOrDefault(b => b.recipe == recipe);
                if (existing != null) break;
            }
            if (existing != null)
            {
                if (ownId == null) ownId = existing.GetUniqueLoadID();   // adopt a bill that was already there
                return false;
            }
            if (ownId != null)
            {
                // the bill we created is gone: the director deleted it on purpose
                ownId = null;
                g.owned.MarkManual(key, tick, OwnedValues.TwoDays);
                StewardLog.Message($"orders: corpses: the standing {recipe.defName} bill was deleted by hand; not re-created for 2 days");
                return false;
            }
            if (g.owned.IsManual(key, tick)) return false;
            var table = tables.OrderByDescending(t => t.def.defName.IndexOf("Electric", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0).First();
            var bill = recipe.MakeNewBill() as Bill_Production;
            if (bill == null) return false;
            bill.repeatMode = BillRepeatModeDefOf.Forever;
            var rotten = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail("AllowRotten");
            var animal = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("CorpsesAnimal");
            var human = DefDatabase<ThingCategoryDef>.GetNamedSilentFail("CorpsesHumanlike");
            var colonist = DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail("AllowCorpsesColonist");
            var filter = bill.ingredientFilter;
            if (filter != null)
            {
                try
                {
                    if (cremate)
                    {
                        if (human != null) filter.SetAllow(human, true);
                        if (animal != null) filter.SetAllow(animal, false);
                        if (rotten != null) filter.SetAllow(rotten, true);
                        if (colonist != null) filter.SetAllow(colonist, true);
                    }
                    else
                    {
                        if (human != null) filter.SetAllow(human, false);
                        if (rotten != null) filter.SetAllow(rotten, false);
                    }
                }
                catch (Exception ex) { StewardLog.Warning($"orders: corpses could not shape the {recipe.defName} bill filter: {ex.Message}"); }
            }
            table.BillStack.AddBill(bill);
            ownId = bill.GetUniqueLoadID();
            g.owned.ClearManual(key);
            StewardLog.Message($"orders: corpses added a standing {recipe.defName} bill on {table.LabelShort}");
            return true;
        }

        /// <summary>Places one butcher spot (direct spawn when WorkToBuild is 0, else a blueprint) if none exists or is pending.</summary>
        static string? PlaceButcherSpot(Map map, StewardGame g)
        {
            var def = DefDatabase<ThingDef>.GetNamedSilentFail(ButcherSpotDef);
            if (def == null) return "no butcher table and no ButcherSpot def";
            bool existsOrPending = map.listerThings.ThingsOfDef(def).Any(t => t.Faction == Faction.OfPlayer)
                || map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Any(t => t is Blueprint bp && bp.def.entityDefToBuild == def)
                || map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Any(t => t is Frame fr && fr.def.entityDefToBuild == def);
            if (existsOrPending) return "butcher spot pending";
            // anchor: a cooking table, else the largest stockpile, else the base centre
            IntVec3 near = IntVec3.Invalid;
            var kitchen = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>()
                .Where(t => t.def.AllRecipes != null && t.def.AllRecipes.Any(r => r.defName == "CookMealSimple") && t.def != ThingDefOf.Campfire)
                .OrderBy(t => t.Position.DistanceToSquared(ProductCounter.GetBaseCenter(map))).FirstOrDefault();
            if (kitchen != null) near = kitchen.Position;
            else
            {
                var zone = map.zoneManager.AllZones.OfType<Zone_Stockpile>().OrderByDescending(z => z.CellCount).FirstOrDefault();
                if (zone != null && zone.CellCount > 0) near = zone.Cells[zone.CellCount / 2];
            }
            if (!near.IsValid) near = ProductCounter.GetBaseCenter(map);
            var flames = map.listerThings.ThingsOfDef(ThingDefOf.Campfire).Select(t => t.Position).ToList();
            IntVec3 best = IntVec3.Invalid;
            foreach (var c in GenRadial.RadialCellsAround(near, 10f, true))
            {
                if (!c.InBounds(map) || c.Fogged(map) || !c.Standable(map)) continue;
                if (c.DistanceTo(near) < 2f) continue;
                if (flames.Any(fl => fl.DistanceTo(c) < 3f)) continue;
                var room = c.GetRoom(map);
                if (room != null && (room.Role == RoomRoleDefOf.Bedroom || room.Role == RoomRoleDefOf.Hospital || room.Role == RoomRoleDefOf.PrisonCell)) continue;
                if (!GenConstruct.CanPlaceBlueprintAt(def, c, Rot4.North, map).Accepted) continue;
                best = c; break;
            }
            if (!best.IsValid) return "no cell for a butcher spot";
            if (def.GetStatValueAbstract(StatDefOf.WorkToBuild) <= 0f)
            {
                var spot = ThingMaker.MakeThing(def);
                spot.SetFactionDirect(Faction.OfPlayer);
                GenSpawn.Spawn(spot, best, map, Rot4.North);
                g.butcherSpotId = spot.ThingID;
            }
            else
            {
                var bp = GenConstruct.PlaceBlueprintForBuild(def, best, map, Rot4.North, Faction.OfPlayer, null);
                g.butcherSpotId = bp?.ThingID;
            }
            StewardLog.Message($"orders: corpses placed a butcher spot at {best.x},{best.z}");
            return $"butcher spot placed at {best.x},{best.z}";
        }
    }
}
