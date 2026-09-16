---
name: bridge-manual
description: Operator's manual for the RimBridge tools — how to sense the map, which control altitude to use for each kind of action, the engine escape hatch, the think-step protocol, and the pitfalls that waste steps.
tags: [tools, bridge, protocol, manual, always]
always: true
---
# RimBridge operator's manual

Every bridge RPC `a.b` is a tool named `rw_a_b`; its documented params go at the top level of the call (e.g. `rw_ui_build(def="Wall", rect=[60,60,5,4])`). Results are JSON, truncated at ~8k chars — ask narrowly (filters, `limit`, small `w`/`h`) instead of dumping everything. Bridge errors come back as `{"error": ...}`; read them, they name the missing param or the reason a cell failed.

## 1. The three senses

**Structured reads (facts).** Start every step with `rw_state_summary` (date, colonists with id/name/pos/mood/job/top skills, wealth, food_days, threat_points, alerts, pending_letters, zones, blueprints, power, key_stocks). Then narrow down:
- `rw_state_alerts` — the alert bar with explanations; `rw_state_letters` — letters on screen with `id` and choices.
- `rw_state_pawns(filter=colonists|prisoners|animals|hostiles)`; `rw_state_pawn(pawn="Sparky")` — skills, traits, health, needs, mood thoughts, gear, work priorities.
- `rw_state_threats` — hostiles with weapons and distance to home, plus storyteller threat points.
- `rw_state_stocks(category=Foods)`, `rw_state_storage`, `rw_state_research`, `rw_state_rooms`, `rw_state_designations`, `rw_state_bills(thing=...)`, `rw_state_quests`.
- `rw_map_find(kind=item|tree|resource_rock|animal|corpse|chunk|building|blueprint, def=..., near=[x,z], radius=, forbidden=true, limit=)` — things sorted by distance from home. This is how you find loose wood, forbidden crash-pod steel, huntable deer, ore.
- `rw_map_cell(cell=[x,z])`, `rw_map_open_rects(w=8,h=6,near=,limit=)` (free buildable rectangles, returns min corners), `rw_map_terrain_stats`, `rw_map_path`, `rw_map_reachable`.
- `rw_defs_buildable(category=Structure|Production|Furniture|Power|Security|Misc|Floors)` — what you can build right now with costs; `rw_defs_get(def=...)` for any def's stats/recipes/research; `rw_defs_search(query=)`; `rw_defs_work_types`.

**`rw_map_view` (layout).** ASCII, one char per cell. Params `x, z` = **min corner** (or `center=true`), `w, h` up to 150, `layer=all|terrain|buildings|zones|pawns|items|roof|fog|home`. Default is centred on home, 60x40. Legend:
`? fog | @ colonist | ! hostile | a colony animal | w wild animal | n other pawn | # wall | + door | ^ rock | o ore | b bed | t work table | s stove/campfire | r research | g power | % turret | x other building | p blueprint/frame | S stockpile | G growing zone | ~ water | T tree | , plant/crop | i item | * fire | f fertile soil | : sand/gravel | - floor/road | . ground`.
The roof layer uses `R` thick rock (unminable-overhead, no drop pods), `r` thin natural, `c` constructed, `.` none. The home layer marks `H`.

**Coordinate convention.** x grows to the **right**, z grows **up** (the top printed row is max z). Every cell is a `[x, z]` array. Every rect is `[minX, minZ, w, h]`, so `[60,60,5,4]` covers x 60..64, z 60..63. Row labels on the left are z; the header digits mark every 10th x. Pawn `pos` and thing `pos` use the same `[x,z]`.

**`look` (a picture).** `look(x, z, w=60)` renders the real map as an image. Use it for a sanity check of a layout or to understand something the ASCII cannot show; use `rw_map_view` for exact coordinates. It does not move the human's camera; `rw_ui_select(thing=...)` does, for the human watching.

## 2. Control altitudes (pick the lowest that works)

1. **Right-click menu — `rw_ui_orders_at` / `rw_ui_order`.** Exactly what a player gets by right-clicking with a pawn selected: pick up, equip, eat, rescue, tend, prioritize hauling, prioritize construction, attack, capture. `rw_ui_orders_at(pawn="Manu", at="Steel2851")` lists `[{label, disabled, priority}]`; `rw_ui_order(pawn="Manu", at="Steel2851", label="haul")` runs one (label is a substring match; `i` picks by index). A `disabled` entry tells you why ("incapable of violence", "forbidden", "no path"). Works on cells (`at=[x,z]`) and thing ids.
2. **Buttons — `rw_ui_gizmos` / `rw_ui_press`.** The gizmo bar of a selected thing: Draft, Fire at will, Hold fire, Rest until healed, Copy/Paste bills, Toggle power, Rearm, Rename, Prioritise. `rw_ui_gizmos(thing="Human102")` then `rw_ui_press(thing="Human102", label="draft")`. Targeted gizmos (throw, cast, fire mortar) take `target`.
3. **Designators — `rw_ui_designate`.** `designator=mine|cut|harvest|harvestwood|hunt|haul|deconstruct|cancel|uninstall|tame|slaughter|strip|open|smooth|removefloor|claim|forbid|unforbid|plan|unplan` (or any `Designator_ClassName`), applied to `cells=[[x,z],...]`, `rect=[x,z,w,h]`, or `things=[ids]`. This is how you queue work for the whole colony rather than one pawn.
4. **Blueprints — `rw_ui_build`.** `def` (ThingDef or TerrainDef), then one of `at=[x,z]`, `line=[[x1,z1],[x2,z2]]`, `rect=[x,z,w,h]` (+`fill=true` for a filled area, default outline). `rot=N|E|S|W` for beds/tables/doors when orientation matters. `stuff` is chosen automatically (most plentiful allowed material) unless you pass e.g. `stuff="BlocksGranite"`. Pass `dry_run=true` first for big placements: it returns `placed`, `failed` (cell + reason), `cost_each` and `work`. Floors are TerrainDefs (e.g. `WoodPlankFloor`), placed the same way.
5. **Zones — `rw_ui_zone`.** `action=create_stockpile|create_growing|delete|add_cells|remove_cells|set_plant|rename|set_priority`, with `rect`/`cells`, `label`, `plant="Plant_Rice"`, `priority=Low|Normal|Preferred|Important|Critical`, `preset=DefaultStockpile|DumpingStockpile`. Storage filters: `rw_ui_storage(zone="main", allow=[...], disallow=[...], priority=...)`. Home area: `rw_ui_area(action=home_add, rect=...)`.
6. **Colony management.** `rw_ui_set_work(pawn, priorities={"Cooking":1,"Growing":2,"Hauling":3})` (1 = highest, 4 = lowest, 0 = off; switches on manual priorities). `rw_ui_set_schedule(pawn, hours="SSSSSSWWWWWWWWWWWWJJJJSS")` (24 chars, hour 0 first, A/S/W/J/M). `rw_ui_set_policies(pawn, food=, apparel=, drug=, area=, medical=NoCare|NoMeds|HerbalOrWorse|NormalOrWorse|Best, hostility=Flee|Attack|Ignore, self_tend=)`. `rw_ui_set_research(def="Electricity")`. `rw_ui_add_bill(thing=<table id>, recipe="CookMealSimple", mode=TargetCount, count=10)` then `rw_ui_bill(thing, index, action=set|suspend|resume|delete|top, count=, radius=)`. `rw_ui_prisoner`, `rw_ui_animal`.
7. **Combat — `rw_ui_draft` / `rw_ui_goto` / `rw_ui_attack`.** `rw_ui_draft(pawn, drafted=true)`; `rw_ui_goto(pawn, cell=[x,z])` drafts automatically (pass `draft=false` for an undrafted walk); `rw_ui_attack(pawn, target=<hostile id>, melee=false)`. Drafted pawns do not eat, sleep or work: undraft when the fight ends. `rw_ui_cancel_job(pawn)` interrupts.
8. **Letters and quests — `rw_ui_letter`.** `rw_state_letters` gives `id` and `choices`; `rw_ui_letter(id, action=choose, choice="Accept")` or `action=dismiss`. Unanswered letters pile up and some expire.
9. **Direct jobs — `rw_ui_job` (last resort).** `rw_ui_job(pawn, job="Ingest", target="MealSimple1234")`, `job="Equip"`, `"Wear"`, `"Rescue"`, `"TendPatient"`, `"HaulToCell"` (`target` = thing, `target_b` = cell), `"Research"`. Use it only when no order/gizmo/designator does the thing; it bypasses the game's own checks and often fails silently if the pawn cannot reach or is incapable.

Game clock: `rw_game_speed(speed=0..3)` (0 pause, 1 normal, 2 fast, 3 superfast), `rw_game_pause(paused=)`, `rw_game_status`, `rw_game_save(name=)`. The runner slows the game to normal speed while you think and restores fast speed after `end_turn`; do not leave the game paused on purpose.

## 3. The escape hatch: engine access

`rw_engine_get(path)`, `rw_engine_set(path, value)`, `rw_engine_call(path, args=[...])`, `rw_engine_members(path|type)`, `rw_engine_types(query)`, `rw_engine_new(type, args|fields)` walk the live object graph by reflection. Path roots: `Find`, `Current`, `Map`, `World`, `Game`, `Player`, `Thing:<id>`, `Pawn:<name|id>`, `Def:<DefType>:<defName>`, `Type:<Full.Name>`, `Zone:<label>`, `Area:<label>`, `Faction:<name>`, `Room:<id>`. Segments: `.member`, `.method()` (parameterless only in paths; methods with args go through `rw_engine_call` with `args`), `[index|key|defName]`. Args are coerced: cells as `[x,z]`, things by id, defs by defName, enums by name.
Examples that work: `Pawn:Sparky.needs.food.CurLevel` -> `0.79`; `Find.CurrentMap.weatherManager.curWeather.defName`; `Def:ThingDef:Plant_Rice.plant.growDays` -> `3.0`; `Map.mapTemperature.OutdoorTemp` (a property, so no `()`); `Zone:rice1.cells`; `rw_engine_call(path="Map.listerThings.ThingsOfDef", args=["Steel"])`.
Before guessing, find the real name: `search_source("class Building_Turret")`, `find_source_files("StorytellerUtility")`, `read_source(path, start, end)` on the decompiled 1.6 source, or `rw_engine_members(path="Pawn:Sparky.needs")` to list fields/properties/methods. Reading is free; `engine.set`/`engine.call` can put the game in states the UI never would — prefer the UI tools, use the engine when they cannot express what you need (e.g. reading `Find.Storyteller.difficulty`, a hediff's severity, a plant's growth percent). Some namespaces (System.IO, Prefs, mod loading) are blocked.

## 4. The ledger, events and wake-ups

The mod keeps an append-only event ledger: `letter`, `message`, `incident`, `hostile_group`, `hostile_group_gone`, `colonist_downed`, `colonist_died`, `pawn_died`, `mental_break`, `research_finished`, `built`, `building_lost`, `quest`, `colonist_joined`, `colonist_left`, `day`, `game`. Each has `seq`, `kind`, `text`, `tick/day/hour`, optional `cell`, `thing`, `data`. The runner hands you the events since your last step at the start of each step, and watchers (`brain/watchers/*.py`) see them every ~0.5 s of real time without the LLM. Use `wake_on` in `end_turn` to be woken by a kind (default kinds: letter, incident, colonist_died, colonist_downed, mental_break, hostile_group, quest, building_lost).

## 5. Dev tools and the "assisted" flag

`rw_dev_spawn`, `rw_dev_spawn_pawn`, `rw_dev_incident(def="RaidEnemy", points=)`, `rw_dev_god_mode`, `rw_dev_heal`, `rw_dev_set_need`, `rw_dev_finish_research`, `rw_dev_weather`, `rw_dev_destroy`, `rw_dev_damage`, `rw_dev_kill_hostiles`, `rw_dev_reveal_map` exist for drills. **Any dev call permanently marks the current game `assisted: true`** (visible in `rw_game_status`, the ledger and the scorecard). Assisted games are recorded separately and never count as honest scores. Never use them to rescue a scored run.

## 6. The think-step protocol

A step is one LLM conversation with a tool budget (~30 calls). Every step **must end with `end_turn(notes, wake_in_hours, wake_on)`** — until you call it the game crawls at normal speed; pawns do execute your orders as soon as you give them. `wake_in_hours` is in-game hours (default 6; use 1-2 during a raid or a fire, 8-12 when things are calm), `wake_on` is a list of event kinds that should wake you early. `end_episode(reason)` declares the game lost or hopeless. Order of work inside a step: read (`rw_state_summary`, alerts, letters, new events) -> decide the single most urgent thing -> act -> a quick verification read (`rw_state_designations`, blueprint count, `rw_ui_orders_at` result) -> `notebook_append` if something notable happened -> `end_turn`.

## 7. Pitfalls

- **Crash-landed items start forbidden.** `rw_map_find(kind=item, forbidden=true)` lists them; `rw_ui_designate(designator=unforbid, things=[ids])` or a `rect` over the drop site. Nobody hauls forbidden things.
- **`rw_state_stocks` counts unforbidden things on the map, `key_stocks` in the summary only stored ones.** Loose logs in the forest are invisible to both until they lie in a stockpile; use `rw_map_find(def="WoodLog")`.
- **You need a stockpile before hauling works** (`rw_ui_zone(action=create_stockpile, rect=..., label="main")`). Put it under a roof; steel and food in the rain is fine, but corpses and rot are not.
- **Walls need a door** or the room is sealed and pawns path around; **roofs need walls** (a roof grows automatically over an enclosed room; unsupported roof further than 6 cells from a wall collapses). A room is only "indoors" (temperature, mood) when enclosed and roofed.
- **Work priorities: 1 = highest, 4 = lowest, 0 = off.** Firefighter/Patient/BedRest at 1 for everyone. A pawn "incapable" of a work type cannot be assigned it (the tool errors).
- **Speed.** The runner runs the game at normal speed while you think (a step costs 1-3 in-game hours) and at fast speed between steps. Do not call `rw_game_pause(paused=true)` yourself except mid-combat for a single precise order, and unpause before `end_turn`.
- **Truncation.** Results are cut at ~8k chars. Use `limit`, `category`, `filter`, small map windows, and `layer=` to keep results short; a truncated result is a wasted call.
- **Thing ids** look like `Steel2851`, `Human102`, `WoodLog2861`; pawns are accepted by name (`"Sparky"`) or id. Ids change between games — never hardcode them into skills.
- **Blueprints need materials on the map and a builder with Construction enabled.** `rw_state_summary.blueprints` staying constant across steps means nobody is building: check materials (`failed` reasons, stocks), priorities, forbids and reachability.
- **Growing zones only accept fertile terrain** (`f` in the view, fertility > 0); soil under trees must be cleared with `cut` first. `set_plant` requires the plant's research.
- **Drafted pawns freeze colony work.** Undraft after combat; check `drafted: true` in the summary at the start of each calm step.
- Bills need a work table id from `rw_map_find(kind=building, def="Campfire")` or `rw_state_summary`; `rw_defs_get(def="Campfire")` lists the recipe defNames.

## 8. Worked examples

**A. Day-1 unforbid and stockpile**
1. `rw_map_find(kind="item", forbidden=true, limit=40)` -> ids and positions of the crash-pod loot.
2. `rw_ui_designate(designator="unforbid", things=["Steel2851","Steel2848","WoodLog2861", ...])`.
3. `rw_map_open_rects(w=8, h=6, near=[102,122], limit=3)` -> `[{at:[98,114]}...]`.
4. `rw_ui_zone(action="create_stockpile", rect=[98,114,8,6], label="main")`.
5. `rw_ui_storage(zone="main", priority="Important")`.

**B. Rice field**
1. `rw_map_view(x=90, z=118, w=30, h=15, layer="terrain")` -> pick a block of `f` cells.
2. `rw_ui_zone(action="create_growing", rect=[101,123,6,6], plant="Plant_Rice", label="rice1")` -> `{cells: 36, failed: [...]}`.
3. `rw_ui_set_work(pawn="Jen", priorities={"Growing":1, "PlantCutting":2})`.

**C. A wooden room with a door, then beds**
1. `rw_ui_build(def="Wall", rect=[106,114,8,6], dry_run=true)` -> check `failed`.
2. `rw_ui_build(def="Wall", rect=[106,114,8,6])` (outline).
3. `rw_ui_build(def="Door", at=[110,114])` — the door replaces one wall blueprint on the south edge.
4. After `built` events / `blueprints: 0`: `rw_ui_build(def="Bed", at=[107,118], rot="S")` x3, then `rw_ui_build(def="Campfire", at=[112,111])` outside.

**D. Cutting trees for wood and mining steel**
1. `rw_map_find(kind="tree", near=[102,122], radius=25, limit=30)` -> `rw_ui_designate(designator="harvestwood", things=[...])` (or `rect`).
2. `rw_map_find(kind="resource_rock", def="MineableSteel", limit=20)` -> `rw_ui_designate(designator="mine", things=[...])`.
3. Verify: `rw_state_designations` shows `HarvestPlant`/`Mine` counts.

**E. First raid**
1. Woken by `hostile_group`. `rw_state_threats` -> ids, weapons, distance.
2. `rw_ui_draft(pawn="Manu", drafted=true)`; `rw_ui_goto(pawn="Manu", cell=[109,113])` (inside the doorway, behind wall corners); same for the other shooter. Keep the pawn incapable of violence indoors.
3. `rw_ui_attack(pawn="Manu", target="Human2331")` when within range; `end_turn(notes="raid: 2 drafted at door", wake_in_hours=1, wake_on=["colonist_downed","hostile_group_gone"])`.
4. On `hostile_group_gone`: `rw_ui_draft(drafted=false)` for all; `rw_map_find(kind="corpse")`; rescue the downed with `rw_ui_order(pawn=, at=<downed id>, label="rescue")`.

**F. Cooking**
1. `rw_map_find(kind="building", def="Campfire")` -> `Campfire2977`.
2. `rw_ui_add_bill(thing="Campfire2977", recipe="CookMealSimple", mode="TargetCount", count=8)`.
3. `rw_ui_set_work(pawn="Sparky", priorities={"Cooking":1})`; check `rw_state_bills(thing="Campfire2977")` later.

**G. A letter with choices**
1. `rw_state_letters` -> `[{id: "Letter_1203", label: "Quest: ...", choices: ["Accept","Reject"]}]`.
2. `rw_ui_letter(id="Letter_1203", action="choose", choice="Reject")`.

**H. Engine read you cannot get elsewhere**
1. `rw_engine_members(path="Pawn:Sparky.health.hediffSet")` -> find `hediffs`.
2. `rw_engine_get(path="Pawn:Sparky.health.hediffSet.hediffs", depth=2)` -> each hediff with `def`, `Severity`, `Part`.
3. `rw_engine_get(path="Find.Storyteller.difficulty.threatScale")` -> `1.0` on Rough.

## The building camera: `rw_map_detail` (use it for every build)

`rw_map_detail(x=, z=, w=, h=)` or `rw_map_detail(around=<thingId|pawn>)` is a zoomed view (up to 60x60) where **every column is numbered** (read x down the three header rows: hundreds / tens / units; z is the row label), each building type gets its own letter (UPPER = built, lower = blueprint/frame, legend included), `*` marks interaction spots that must stay clear (benches, stoves, beds, tables), `+` doors, `_` stockpile, `,` growing zone, `i` items. It also returns `things` in view with id, rot, size and interaction_cell. Workflow for any construction:
1. `rw_map_detail` around the site → pick exact cells on the numbered grid.
2. `rw_ui_build(..., dry_run=true)` for anything with an interaction spot or footprint > 1x1; the failure reason tells you what blocks it; adjust `rot` (N/E/S/W moves the interaction spot) or the cell.
3. For a whole room or layout use one `rw_ui_build_many(ops=[...])`: e.g. `[{"def":"Wall","stuff":"WoodLog","rect":[130,120,9,7]}, {"def":"Door","stuff":"WoodLog","at":[134,120]}, {"def":"WoodPlankFloor","rect":[131,121,7,5],"fill":true}, {"def":"Bed","stuff":"WoodLog","at":[132,124],"rot":"N"}]` — walls as rect outline, floors as filled rects, then furniture. It returns one result per op; failed cells list the reason.
4. `rw_map_detail` again to verify (lowercase letters = your blueprints).
Rooms: leave at least one free cell around furniture, put the door on the side facing the base, keep 2-3 cells of walking space; a bedroom is at least 5x5 interior, a workshop 8x8. To enlarge an existing room, designate `deconstruct` on the wall segment, build the new outline, then a door.
