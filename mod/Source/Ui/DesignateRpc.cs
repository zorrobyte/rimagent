using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using RimBridge.Engine;
using RimBridge.Server;
using RimWorld;
using Verse;

namespace RimBridge.Ui
{
    public static class DesignateRpc
    {
        static Verse.Map Map() { GameCtl.GameControl.RequirePlaying(); return Find.CurrentMap; }

        static IEnumerable<IntVec3> Cells(JObject p, Verse.Map map)
        {
            if (p["rect"] is JArray r && r.Count == 4)
            {
                var rect = new CellRect((int)r[0]!, (int)r[1]!, (int)r[2]!, (int)r[3]!).ClipInsideMap(map);
                foreach (var c in rect) yield return c;
            }
            else if (p["cells"] is JArray cs)
                foreach (var c in cs) yield return Lookup.Cell(c);
            else if (p["cell"] != null) yield return Lookup.Cell(p["cell"]);
        }

        static readonly Dictionary<string, string> DesignatorAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mine"] = "Designator_Mine", ["cut"] = "Designator_PlantsCut", ["cutplant"] = "Designator_PlantsCut", ["chop"] = "Designator_PlantsCut", ["harvest"] = "Designator_PlantsHarvest", ["harvestwood"] = "Designator_PlantsHarvestWood",
            ["hunt"] = "Designator_Hunt", ["haul"] = "Designator_Haul", ["deconstruct"] = "Designator_Deconstruct", ["cancel"] = "Designator_Cancel", ["uninstall"] = "Designator_Uninstall",
            ["tame"] = "Designator_Tame", ["slaughter"] = "Designator_Slaughter", ["strip"] = "Designator_Strip", ["open"] = "Designator_Open", ["smooth"] = "Designator_SmoothSurface", ["removefloor"] = "Designator_RemoveFloor",
            ["claim"] = "Designator_Claim", ["forbid"] = "Designator_Forbid", ["unforbid"] = "Designator_Unforbid", ["plan"] = "Designator_PlanAdd", ["unplan"] = "Designator_PlanRemove", ["removebridge"] = "Designator_RemoveBridge",
            ["releaseanimal"] = "Designator_ReleaseAnimalToWild", ["extractskull"] = "Designator_ExtractSkull", ["study"] = "Designator_Study", ["paint"] = "Designator_PaintBuilding",
        };

        [Rpc("ui.designate", "{designator: mine|cut|harvest|harvestwood|hunt|haul|deconstruct|cancel|uninstall|tame|slaughter|strip|open|smooth|removefloor|claim|forbid|unforbid|plan|unplan|<Designator_ClassName>, cells?: [[x,z]...], rect?: [x,z,w,h], things?: [ids]} apply an orders/architect designator")]
        public static JToken Designate(JObject p)
        {
            var map = Map();
            string name = P.Str(p, "designator");
            string cls = DesignatorAliases.TryGetValue(name, out var alias) ? alias : (name.StartsWith("Designator_") ? name : "Designator_" + name);
            var type = Lookup.TypeOrNull(cls) ?? throw new RpcError($"unknown designator '{name}'. Aliases: {string.Join(", ", DesignatorAliases.Keys)}; or any Designator_* class (engine.types Designator_)");
            if (!typeof(Designator).IsAssignableFrom(type) || type.IsAbstract) throw new RpcError($"{cls} is not a concrete Designator");
            if (type == typeof(Designator_Build) || typeof(Designator_Place).IsAssignableFrom(type)) throw new RpcError("use ui.build for construction");
            Designator d;
            try { d = (Designator)Activator.CreateInstance(type)!; }
            catch (Exception ex) { throw new RpcError($"cannot instantiate {cls}: {ex.Message}"); }
            int ok = 0; var failed = new JArray();
            var things = P.Arr(p, "things");
            if (things != null)
                foreach (var id in things)
                {
                    var t = Lookup.ThingOrNull(id.ToString()) ?? Lookup.PawnOrNull(id.ToString());
                    if (t == null) { failed.Add(new JObject { ["thing"] = id.ToString(), ["reason"] = "not found" }); continue; }
                    var r = d.CanDesignateThing(t);
                    if (!r.Accepted) { failed.Add(new JObject { ["thing"] = t.ThingID, ["reason"] = r.Reason ?? "not applicable" }); continue; }
                    d.DesignateThing(t); ok++;
                }
            var cells = Cells(p, map).ToList();
            if (cells.Count > 0)
            {
                var good = new List<IntVec3>();
                foreach (var c in cells)
                {
                    if (!c.InBounds(map)) { failed.Add(new JObject { ["cell"] = State.Snapshot.Cell(c), ["reason"] = "out of bounds" }); continue; }
                    var r = d.CanDesignateCell(c);
                    if (!r.Accepted) { if (failed.Count < 20) failed.Add(new JObject { ["cell"] = State.Snapshot.Cell(c), ["reason"] = r.Reason ?? "not applicable" }); continue; }
                    good.Add(c);
                }
                if (good.Count > 0) { d.DesignateMultiCell(good); ok += good.Count; }
            }
            if (ok == 0 && things == null && cells.Count == 0) throw new RpcError("give cells, rect or things");
            return new JObject { ["designator"] = cls, ["applied"] = ok, ["failed"] = failed };
        }

        [Rpc("ui.build_many", "{ops: [ {same params as ui.build}, ... ], stop_on_error?: false} place a whole layout in one call (walls as rect outlines, floors as filled rects, doors/furniture as single cells). Returns one result per op. Use map.detail before and after.")]
        public static JToken BuildMany(JObject p)
        {
            Map();
            var ops = P.Arr(p, "ops") ?? throw new RpcError("missing ops");
            bool stop = P.Bool(p, "stop_on_error", false);
            var results = new JArray();
            int i = 0;
            foreach (var op in ops.OfType<JObject>())
            {
                try { var r = (JObject)Build(op); r["op"] = i; results.Add(r); }
                catch (RpcError e) { results.Add(new JObject { ["op"] = i, ["error"] = e.Message, ["def"] = (string?)op["def"] }); if (stop) break; }
                i++;
            }
            return new JObject { ["results"] = results, ["placed_total"] = results.Sum(r => (r["placed"] as JArray)?.Count ?? 0), ["failed_total"] = results.Sum(r => (r["failed"] as JArray)?.Count ?? (r["error"] != null ? 1 : 0)) };
        }

        [Rpc("ui.build", "{def: buildable defName (ThingDef or TerrainDef), at?: [x,z], rot?: N|E|S|W, stuff: ThingDef (required for stuff-made things; omit once to get the options), line?: [[x1,z1],[x2,z2]], rect?: [x,z,w,h], fill?: bool (rect: fill vs outline), dry_run?: bool} place blueprints; picks a stuff automatically if omitted (most plentiful allowed). Returns placed and failed cells with reasons.")]
        public static JToken Build(JObject p)
        {
            var map = Map();
            string defName = P.Str(p, "def");
            BuildableDef def = (BuildableDef?)Lookup.DefOrNull(typeof(ThingDef), defName) ?? (BuildableDef?)Lookup.DefOrNull(typeof(TerrainDef), defName) ?? throw new RpcError($"no buildable def '{defName}'");
            if (def.designationCategory == null && !(def is ThingDef td0 && td0.BuildableByPlayer)) throw new RpcError($"{defName} is not buildable by the player");
            if (def is ThingDef td && td.building == null && td.category == ThingCategory.Item) throw new RpcError($"{defName} is an item, not a building");
            if (!def.IsResearchFinished) throw new RpcError($"{defName} requires research: {string.Join(", ", def.researchPrerequisites?.Where(r => !r.IsFinished).Select(r => r.defName) ?? Enumerable.Empty<string>())}");
            var rot = p["rot"] != null ? (Rot4)Coerce.To(p["rot"], typeof(Rot4))! : Rot4.North;
            ThingDef? stuff = null;
            if (def.MadeFromStuff)
            {
                var allowed = GenStuff.AllowedStuffsFor(def).ToList();
                if (p["stuff"] != null) { stuff = Lookup.Def<ThingDef>(P.Str(p, "stuff")); if (!allowed.Contains(stuff)) throw new RpcError($"{stuff.defName} is not a valid stuff for {defName}. Valid: " + string.Join(", ", allowed.Select(a => a.defName))); }
                else
                {
                    // No guessing on the model's behalf: report the options (with what is actually on the map) and let it choose.
                    var opts = allowed.Select(a => new { a, n = map.listerThings.ThingsOfDef(a).Where(t => t.Spawned && !t.Position.Fogged(map)).Sum(t => t.stackCount) }).OrderByDescending(x => x.n).Take(12)
                        .Select(x => $"{x.a.defName}({x.n} on map, x{def.CostStuffCount * (x.a.smallVolume ? 10 : 1)} needed)");
                    throw new RpcError($"{defName} is made from stuff; pass stuff=<ThingDef>. Options: " + string.Join(", ", opts));
                }
            }
            bool dry = P.Bool(p, "dry_run", false);
            var cells = new List<IntVec3>();
            if (p["line"] is JArray line && line.Count == 2)
            {
                var a = Lookup.Cell(line[0]); var b = Lookup.Cell(line[1]);
                foreach (var c in GenSight.PointsOnLineOfSight(a, b)) cells.Add(c);
                if (!cells.Contains(b)) cells.Add(b);
            }
            else if (p["rect"] is JArray r && r.Count == 4)
            {
                var rect = new CellRect((int)r[0]!, (int)r[1]!, (int)r[2]!, (int)r[3]!);
                cells.AddRange(P.Bool(p, "fill", def is TerrainDef) ? rect.Cells : rect.EdgeCells);
            }
            else cells.Add(Lookup.Cell(p["at"], "at"));

            var placed = new JArray(); var failed = new JArray();
            foreach (var c in cells)
            {
                if (!c.InBounds(map)) { failed.Add(new JObject { ["cell"] = State.Snapshot.Cell(c), ["reason"] = "out of bounds" }); continue; }
                // skip cells that already have this blueprint/frame/building
                if (c.GetThingList(map).Any(t => (t is Blueprint_Build bb && bb.def.entityDefToBuild == def) || (t is Frame f && f.def.entityDefToBuild == def) || (def is ThingDef tdd && t.def == tdd))) { continue; }
                var rep = GenConstruct.CanPlaceBlueprintAt(def, c, rot, map, false, null, null, stuff);
                if (!rep.Accepted) { if (failed.Count < 25) failed.Add(new JObject { ["cell"] = State.Snapshot.Cell(c), ["reason"] = rep.Reason?.StripTags() ?? "blocked" }); continue; }
                if (dry) { placed.Add(State.Snapshot.Cell(c)); continue; }
                // Same as Designator_Build: zero-work things (crafting/butcher/sleeping spots, plan markers) and god mode spawn instantly.
                if (DebugSettings.godMode || def.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff) == 0f)
                {
                    if (def is TerrainDef terr)
                    {
                        map.terrainGrid.RemoveTempTerrain(c);
                        if (terr.isFoundation) { if (map.terrainGrid.CanRemoveTopLayerAt(c)) map.terrainGrid.RemoveTopLayer(c, !DebugSettings.godMode); map.terrainGrid.SetFoundation(c, terr); }
                        else if (terr.temporary) map.terrainGrid.SetTempTerrain(c, terr);
                        else map.terrainGrid.SetTerrain(c, terr);
                        placed.Add(State.Snapshot.Cell(c));
                    }
                    else
                    {
                        var thing = ThingMaker.MakeThing((ThingDef)def, stuff);
                        thing.SetFactionDirect(Faction.OfPlayer);
                        var spawned = GenSpawn.Spawn(thing, c, map, rot);
                        placed.Add(Render.ThingHandle(spawned));
                    }
                    continue;
                }
                GenSpawn.WipeExistingThings(c, rot, def.blueprintDef, map, DestroyMode.Deconstruct);
                var bp = GenConstruct.PlaceBlueprintForBuild(def, c, map, rot, Faction.OfPlayer, stuff);
                placed.Add(bp != null ? (JToken)Render.ThingHandle(bp) : State.Snapshot.Cell(c));
            }
            var cost = def.CostListAdjusted(stuff);
            return new JObject
            {
                ["def"] = def.defName, ["stuff"] = stuff?.defName, ["placed"] = placed, ["failed"] = failed, ["dry_run"] = dry,
                ["cost_each"] = new JObject(cost.Select(c => new JProperty(c.thingDef.defName, c.count))),
                ["work"] = Math.Round(def.GetStatValueAbstract(StatDefOf.WorkToBuild, stuff)),
            };
        }

        [Rpc("ui.zone", "{action: create_stockpile|create_growing|delete|add_cells|remove_cells|set_plant|rename|set_priority, label?, cells?/rect?, plant?: ThingDef (e.g. Plant_Rice), priority?, preset?: DefaultStockpile|DumpingStockpile} create/edit zones")]
        public static JToken Zone(JObject p)
        {
            var map = Map();
            string action = P.Str(p, "action");
            var cells = Cells(p, map).ToList();
            switch (action)
            {
                case "create_stockpile":
                case "create_growing":
                {
                    if (cells.Count == 0) throw new RpcError("give cells or rect");
                    Verse.Zone z;
                    if (action == "create_stockpile")
                    {
                        var preset = P.Str(p, "preset", "DefaultStockpile") == "DumpingStockpile" ? StorageSettingsPreset.DumpingStockpile : StorageSettingsPreset.DefaultStockpile;
                        z = new Zone_Stockpile(preset, map.zoneManager);
                    }
                    else z = new Zone_Growing(map.zoneManager);
                    map.zoneManager.RegisterZone(z);
                    int added = 0; var failed = new JArray();
                    foreach (var c in cells)
                    {
                        var r = Designator_ZoneAdd.IsZoneableCell(c, map);
                        if (!r.Accepted || map.zoneManager.ZoneAt(c) != null) { if (failed.Count < 20) failed.Add(new JObject { ["cell"] = State.Snapshot.Cell(c), ["reason"] = r.Reason ?? "occupied" }); continue; }
                        if (z is Zone_Growing && (c.GetTerrain(map).fertility <= 0f || c.GetEdifice(map) != null)) { if (failed.Count < 20) failed.Add(new JObject { ["cell"] = State.Snapshot.Cell(c), ["reason"] = "not growable" }); continue; }
                        z.AddCell(c); added++;
                    }
                    if (added == 0) { z.Delete(false); throw new RpcError("no valid cells: " + failed.ToString(Newtonsoft.Json.Formatting.None)); }
                    if (p["label"] != null) z.label = P.Str(p, "label");
                    if (z is Zone_Growing zg)
                    {
                        if (p["plant"] != null) zg.SetPlantDefToGrow(Lookup.Def<ThingDef>(P.Str(p, "plant")));
                    }
                    if (z is Zone_Stockpile zs && p["priority"] != null) zs.settings.Priority = (StoragePriority)Enum.Parse(typeof(StoragePriority), P.Str(p, "priority"), true);
                    return new JObject { ["zone"] = z.label, ["cells"] = z.Cells.Count, ["failed"] = failed, ["plant"] = (z as Zone_Growing)?.GetPlantDefToGrow()?.defName };
                }
                default:
                {
                    var z = Lookup.ZoneOrNull(P.Str(p, "label")) ?? throw new RpcError("no zone with that label");
                    switch (action)
                    {
                        case "delete": z.Delete(false); return new JObject { ["deleted"] = true };
                        case "add_cells": { int n = 0; foreach (var c in cells) if (Designator_ZoneAdd.IsZoneableCell(c, map).Accepted && map.zoneManager.ZoneAt(c) == null) { z.AddCell(c); n++; } return new JObject { ["zone"] = z.label, ["added"] = n, ["cells"] = z.Cells.Count }; }
                        case "remove_cells": { int n = 0; foreach (var c in cells) if (z.ContainsCell(c)) { z.RemoveCell(c); n++; } return new JObject { ["zone"] = z.label, ["removed"] = n, ["cells"] = z.Cells.Count }; }
                        case "set_plant": { if (!(z is Zone_Growing zg)) throw new RpcError("not a growing zone"); var pd = Lookup.Def<ThingDef>(P.Str(p, "plant")); if (pd.plant == null || !pd.plant.Sowable) throw new RpcError($"{pd.defName} is not sowable"); if (!pd.IsResearchFinished) throw new RpcError("plant requires research"); zg.SetPlantDefToGrow(pd); return new JObject { ["zone"] = z.label, ["plant"] = pd.defName }; }
                        case "rename": z.label = P.Str(p, "new_label"); return new JObject { ["zone"] = z.label };
                        case "set_priority": { if (!(z is Zone_Stockpile zs)) throw new RpcError("not a stockpile"); zs.settings.Priority = (StoragePriority)Enum.Parse(typeof(StoragePriority), P.Str(p, "priority"), true); return new JObject { ["zone"] = z.label, ["priority"] = zs.settings.Priority.ToString() }; }
                        default: throw new RpcError("unknown zone action");
                    }
                }
            }
        }

        [Rpc("ui.area", "{action: home_add|home_remove|create|delete|add|remove, label?, cells?/rect?} edit the home area or allowed areas")]
        public static JToken Area(JObject p)
        {
            var map = Map();
            string action = P.Str(p, "action");
            var cells = Cells(p, map).Where(c => c.InBounds(map)).ToList();
            switch (action)
            {
                case "home_add": foreach (var c in cells) map.areaManager.Home[c] = true; return new JObject { ["home_cells"] = map.areaManager.Home.TrueCount };
                case "home_remove": foreach (var c in cells) map.areaManager.Home[c] = false; return new JObject { ["home_cells"] = map.areaManager.Home.TrueCount };
                case "create":
                {
                    if (!map.areaManager.TryMakeNewAllowed(out var area)) throw new RpcError("cannot create more areas");
                    if (p["label"] != null) area.RenamableLabel = P.Str(p, "label");
                    foreach (var c in cells) area[c] = true;
                    return new JObject { ["area"] = area.Label, ["cells"] = area.TrueCount };
                }
                case "delete": { var a = Lookup.AreaOrNull(P.Str(p, "label")) ?? throw new RpcError("no such area"); if (!a.Mutable) throw new RpcError("area cannot be deleted"); a.Delete(); return new JObject { ["deleted"] = true }; }
                case "add": { var a = Lookup.AreaOrNull(P.Str(p, "label")) ?? throw new RpcError("no such area"); foreach (var c in cells) a[c] = true; return new JObject { ["area"] = a.Label, ["cells"] = a.TrueCount }; }
                case "remove": { var a = Lookup.AreaOrNull(P.Str(p, "label")) ?? throw new RpcError("no such area"); foreach (var c in cells) a[c] = false; return new JObject { ["area"] = a.Label, ["cells"] = a.TrueCount }; }
                default: throw new RpcError("unknown area action");
            }
        }

        [Rpc("ui.letter", "{id, action: choose|dismiss, choice?: label|index} respond to a letter (quests, events with choices)")]
        public static JToken Letter(JObject p)
        {
            Map();
            int id = P.Int(p, "id");
            var letter = Find.LetterStack.LettersListForReading.FirstOrDefault(l => l.ID == id) ?? throw new RpcError("no letter with that id (state.letters)");
            string action = P.Str(p, "action");
            if (action == "dismiss")
            {
                if (letter is ChoiceLetter cl0 && cl0.Choices.Any())
                {
                    // Prefer the explicit close/dismiss/postpone option if any; otherwise just remove.
                    var close = cl0.Choices.FirstOrDefault(c => { var t = OptionText(c); return t.IndexOf("close", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("dismiss", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("postpone", StringComparison.OrdinalIgnoreCase) >= 0; });
                    if (close != null && !close.disabled) { close.action?.Invoke(); }
                }
                Find.LetterStack.RemoveLetter(letter);
                return new JObject { ["dismissed"] = id };
            }
            if (!(letter is ChoiceLetter cl)) throw new RpcError("letter has no choices");
            var choices = cl.Choices.ToList();
            DiaOption? opt = null;
            if (p["choice"]?.Type == JTokenType.Integer) opt = choices.ElementAtOrDefault(P.Int(p, "choice"));
            else { string label = P.Str(p, "choice"); opt = choices.FirstOrDefault(c => OptionText(c).Equals(label, StringComparison.OrdinalIgnoreCase)) ?? choices.FirstOrDefault(c => OptionText(c).IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0); }
            if (opt == null) throw new RpcError("no such choice. Available: " + string.Join(" | ", choices.Select(OptionText)));
            if (opt.disabled) throw new RpcError("choice disabled: " + opt.disabledReason);
            opt.action?.Invoke();
            if (Find.LetterStack.LettersListForReading.Contains(letter) && opt.action == null) Find.LetterStack.RemoveLetter(letter);
            return new JObject { ["chose"] = OptionText(opt), ["letter"] = id };
        }

        static string OptionText(DiaOption o) => ((string)HarmonyLib.AccessTools.Field(typeof(DiaOption), "text").GetValue(o) ?? "").StripTags();

        [Rpc("ui.prisoner", "{pawn, mode?: NoInteraction|MaintainOnly|ReduceResistance|AttemptRecruit|Release|Execution|Enslave|Convert|..., medical?} set prisoner interaction")]
        public static JToken Prisoner(JObject p)
        {
            Map();
            var pawn = Lookup.Pawn(P.Str(p, "pawn"));
            if (!pawn.IsPrisonerOfColony) throw new RpcError("not a prisoner of the colony");
            var o = new JObject { ["pawn"] = pawn.LabelShort };
            if (p["mode"] != null)
            {
                var def = Lookup.Def<PrisonerInteractionModeDef>(P.Str(p, "mode"));
                pawn.guest.SetExclusiveInteraction(def);
                o["mode"] = def.defName;
            }
            return o;
        }

        [Rpc("ui.animal", "{pawn, train?: {TrainableDef: bool}, master?: colonist|none, follow_field?: bool, follow_draft?: bool} animal training and master")]
        public static JToken Animal(JObject p)
        {
            Map();
            var pawn = Lookup.Pawn(P.Str(p, "pawn"));
            if (!pawn.RaceProps.Animal || pawn.Faction != Faction.OfPlayer) throw new RpcError("not a colony animal");
            var o = new JObject { ["pawn"] = pawn.LabelShort };
            if (p["train"] is JObject tr && pawn.training != null)
                foreach (var kv in tr)
                {
                    var td = Lookup.Def<TrainableDef>(kv.Key);
                    var can = pawn.training.CanAssignToTrain(td);
                    if (!can.Accepted) { o["skipped_" + kv.Key] = can.Reason; continue; }
                    pawn.training.SetWantedRecursive(td, (bool)kv.Value!);
                }
            if (p["master"] != null && pawn.playerSettings != null)
            {
                string m = P.Str(p, "master");
                pawn.playerSettings.Master = m.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : Lookup.Colonist(m);
                o["master"] = pawn.playerSettings.Master?.LabelShort;
            }
            if (p["follow_field"] != null && pawn.playerSettings != null) pawn.playerSettings.followFieldwork = P.Bool(p, "follow_field", true);
            if (p["follow_draft"] != null && pawn.playerSettings != null) pawn.playerSettings.followDrafted = P.Bool(p, "follow_draft", true);
            return o;
        }

        [Rpc("ui.select", "{thing|cell} select a thing in the game UI and jump the player's camera there (for the human watching)")]
        public static JToken Select(JObject p)
        {
            Map();
            if (p["thing"] != null)
            {
                var t = Lookup.ThingOrNull(P.Str(p, "thing")) ?? Lookup.Pawn(P.Str(p, "thing"));
                Find.Selector.ClearSelection(); Find.Selector.Select(t);
                CameraJumper.TryJump(t);
                return Render.ThingHandle(t);
            }
            var c = Lookup.Cell(p["cell"]);
            CameraJumper.TryJump(c, Find.CurrentMap);
            return State.Snapshot.Cell(c);
        }
    }
}
