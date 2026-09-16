---
always: false
description: Pull in when planning or placing walls, doors, roofs, floors, bedrooms/barracks,
  the home area, or choosing wood vs stone; also when a roof collapse, fire or "sleeping
  outside" mood problem shows up, or when expanding a day-1 shelter into a multi-room
  base.
name: base-building
tags:
- building
- construction
- rooms
- layout
- materials
- stockpile
- fire
- cooler
- trapped
---

# Base building

## Structures: costs and numbers
| Def | Cost | Work | Base HP | Notes |
|---|---|---|---|---|
| Wall | 5 stuff | 135 x material factor | 300 x material | Supports roof up to 6 tiles away; 75% cover; blocks sight/fire lines. |
| Door | 25 stuff | 850 | 160 x material | Counts as wall for rooms/roof. Enemies treat closed doors like walls unless sapper/breacher. |

Materials: **Wood** 0.65x HP (195 HP wall), **100% flammable**, 0.7x work; **Steel** 300 HP, 40% flammable; **Sandstone** 420 HP, 0%, 5x work +140; **Granite** 510 HP, 0%, 6x work +140. Stone doors open at 0.45x speed (wood 1.2x). Blocks come 20 per chunk from a stonecutter's table (Stonecutting research). Only marble walls add beauty; granite is toughest.

**Rule:** first shelter in wood (fast, cheap); replace **walls** with stone as soon as blocks flow; keep **doors and furniture** wooden (fastest to open, safe inside non-flammable walls). Do not keep wood walls once electricity exists: a short circuit or dry thunderstorm burns the base. Stone wall sections act as fire breaks; fire crosses diagonal gaps, pawns cannot.

## Rooms and roofs
- A room = area fully enclosed by walls/doors/coolers/rock; corners are optional (but leak more heat). Use `rw_ui_build` with `rect` for walls and `at` for a Door on the traffic side.
- Roofs extend **6 tiles** from any wall/column, so interiors up to **12 wide** roof fully; wider needs interior columns/walls. Temperature control needs **>= 75% roofed**; under that the room snaps to outdoor temperature. 300+ unroofed tiles = "outdoors" (Slept outside, no room mood).
- Roofs are free and auto-designated over new rooms. Removing the last support within 6 tiles collapses the roof: thin/constructed roof deals **15-30 crush damage to head/neck** (roughly 1 in 3 kills an unhelmeted pawn); **overhead mountain** collapse destroys everything beneath. Trees cannot grow under roofs.
- Indoor items never deteriorate. Dark rooms give 80% work/move speed; a torch lamp is 10 wood for 10 days.

## Fire safety (the #1 base-killer after raids)
- **Wood walls are 100% flammable.** A single campfire or short-circuit can consume the entire base in 2-4 hours. The fire that killed the episode 1 colony started day 25 and destroyed every wall, bed, stove, and research bench over 24h.
- **Replace wood walls with steel or stone as soon as you have the material.** Steel walls are 40% flammable (they smoulder but don't burn); stone is 0%. Once electricity exists, wood walls are a death sentence.
- **Keep a fire break:** a 2-wide non-flammable (steel/stone) wall strip between the kitchen/campfire area and the bedrooms. Fire crosses diagonal gaps but not a 2-cell gap.
- **Campfires must be under a roof** (rain burns extra fuel) and have a free interaction cell. A campfire inside a wood-walled room is a bomb.
- **Drafted pawns cannot put out fires.** If you drafted everyone for a raid and a fire starts, undraft the firefighters first. The `fire_alert` watcher drafts one colonist on fire events, but if they're already drafted for combat, the draft order is a no-op.
- **Fire destroys blueprints too.** If a fire is burning through your base, stop queuing new blueprints in the fire zone — they'll be destroyed before they're built. Move new builds to safe ground.

## Cooler setup (freezer)
- Coolers start at a **21C target** — after building a freezer, press the cooler gizmo `-10C` three times to get to 11C (or lower for a proper freezer). Check with `rw_ui_gizmos` on the cooler; verify via `rw_state_base` room temperature.
- Coolers must be **fully enclosed and roofed** (cold side facing into the room). A cooler in an open wall with no roof will not hold temperature.
- Place 2+ coolers in the south wall of a freezer room (cold side faces north into the room). Stockpile inside with `Foods` only, priority Important.
- **Batteries must be roofed.** Unroofed batteries can explode. Keep them inside the power room.

## Trapped pawns (a colonist stuck inside a sealed room)
This happened twice in episode 1: a pawn was inside a room whose door was blocked by a wall, and the rescue order failed because the room was sealed.
1. **Diagnose:** `rw_state_base` shows `TRAPPED` colonists. `rw_state_pawns` shows their position. If they're inside a room with no open door, they cannot be rescued normally.
2. **Force-deconstruct the blocking wall:** `rw_ui_designate(designator="deconstruct", things=[<wall id>])` or `rw_ui_designate(designator="deconstruct", cells=[[x,z]])`. The wall must be deconstructed (not just designated) for the pawn to walk out.
3. **If the pawn is downed and the room is sealed:** the rescue order will fail (no path to a bed). Deconstruct the wall first, then rescue.
4. **Prevention:** when building a new room, always verify the door is placed and the room is reachable. `rw_map_detail` after building shows `+` for doors; if there's no `+`, the room is sealed.

## Sealed-room check (after any build)
After placing walls + door for a new room, run `rw_map_detail` around the room and confirm:
- There is a `+` (door) on at least one wall.
- The room interior is reachable from outside (no wall blocking the door cell).
- If a pawn is inside, they can walk out.
If the door cell is blocked by another wall (doubled-up wall), deconstruct the blocking wall immediately.

## Stockpile roofing (operator tip)
- **Stockpiles need a roof (or some structure overhead) or items will degrade** — food rots faster, leather/cloth deteriorate, and in rain everything takes water damage. An unroofed stockpile in a temperate forest will lose food to rot within days.
- Check `rw_state_storage` for `storage_cells_free`: if it's 0-1 the zone is full and items are spilling out. Expand with `rw_ui_zone(action=add_cells, label=..., rect=...)` or create a second stockpile.
- A simple roofed shed (4 walls + roof) over the stockpile is worth the ~20 wall cells + door. Prioritise this after the first raid.
- **Dumping zone:** create a `DumpingStockpile` preset zone outside the home area for corpses, rotting food, and filth. Keep it off walking paths. `rw_ui_zone(action=create_stockpile, preset=DumpingStockpile, rect=..., label="dump")`.

## Bedroom vs barracks
- Bedroom = only the owner's bed(s) (a lover pair is fine), no medical/prison bed; more than one unassigned bed makes a barracks. A barracks costs about **-5 mood vs an equivalent private bedroom** (-4 at higher quality). One barracks is fine on day 1; split into bedrooms within the first season.
- Impressiveness (bedroom/dining/rec mood): <20 awful, 20-30 dull, 30-40 mediocre, 40-50 decent, 50-65 slightly impressive, 65-85 impressive, 85-120 very impressive. It is weighted to the weakest of wealth/beauty/space/cleanliness, so a dirty floor drops a level. "Very impressive" needs a **5x5 or 4x6 interior**; 4x4 struggles. Returns diminish sharply.
- Space need (radius 7 walkable tiles): 1-3 = Confined -10, 4-10 = Cramped -5, 41+ = Spacious +5. Avoid 1-wide corridors and closet workstations.
- **Bedroom minimum: 5x5 interior (>= 25 tiles).** A 2-cell room gives -10 "Confined interior" and is a real break trigger on its own. Always build bedrooms at least 5x5 interior.

## Expanding a day-1 shelter into a multi-room base (operator pattern)
When the single ~7x7 day-1 room becomes a barracks + everything-in-one, split it. Concrete procedure:
1. **Plan the rooms first, then deconstruct.** Target: a private bedroom per colonist, a kitchen (stove + raw-food shelf adjacent + meal shelf next door), a roofed storage room over the main stockpile, and a research/work room. Keep the original door + chokepoint for defense.
2. **Deconstruct the old interior** that you are replacing (beds, old walls you'll redo) with `rw_ui_designate(designator=deconstruct, ...)` — you get ~70% of material back. Do NOT deconstruct walls that still hold a roof or the chokepoint.
3. **Build with shared walls** so two rooms share one wall (cheaper than two). Each room: walls + one door + roof (auto). Interior up to 12 wide roofs fully.
4. **Bedroom**: 5x5 interior minimum, one bed, an end table + dresser + recreation (horseshoes pin / darktorch / etc) to lift impressiveness and joy. Set the owner's bed preference via `rw_ui_set_policies` or right-click so they actually use it (clears the barracks -7).
5. **Kitchen**: raw-food stockpile **adjacent** to the stove (else the cook hauls one meal's ingredients per trip); meal stockpile next door. Leave one free approach cell on the stove (see interaction clearance).
6. **Roofed storage room** over the main stockpile: ~20 wall cells + door + auto roof. This is the single best anti-degradation investment; do it before the next rain.
7. **Verify with `blueprint_check`** (brain tool): it reports per-def blueprint counts, total stuff needed vs raw material on hand, which blueprints are unreachable, and whether any colonist has Construction>0. If `material_starved` or `no_builder`, that's why nothing is building.
8. **Order of build**: roofed storage + bedroom first (mood + anti-degradation), then kitchen, then research room. Queue all blueprints, set Construction=1 on the builder, and let it flow.
9. **After the build:** run the sealed-room check above on every new room. Verify doors are placed and rooms are reachable.

## Floors
Floors stop wild plant growth, speed movement and remove the terrain cleanliness penalty (kitchen food poisoning, hospital, research). A 2-wide non-flammable strip is a fire break. Pawns pick up filth on soil (10%/step) and drop it on floors (5%/step), so floor the paths into the kitchen.

## Interaction clearance (furniture near fires/stoves)
- A pawn needs a **free adjacent cell** to use a campfire, stove, table or bed. Placing a 2x2 table directly against a campfire blocks the interaction cell and the bill/fuel job stalls (`rw_ui_build` may return a `failed` cell, or the pawn just stops). Leave at least one open cell on the side pawns approach from. When a placement fails with a blocked-interaction reason, shift the item one cell, not onto the fire's reach.
- **Concrete failure:** a stove at [140,128] inside a 6x5 bedroom failed placement because the bed/wall blocked its approach cell. Move the stove to a cell with a free neighbor, or place the bed one cell away.
- **1x2 things with rot N** occupy their cell AND the cell above it. Place beds at least one cell below a wall (`...:NW +S1`).

## Layout checklist (first days)
1. `rw_ui_zone` stockpile where the base will be and build around it (outdoor items take months to deteriorate). **Roof the stockpile** — unroofed stockpiles degrade food and leather in rain.
2. Priority 1: walls + door for one ~7x7 room, beds (a normal bed saves ~1 hour sleep/day, avoids Slept on the ground), a 1x2 table + stools (avoids Ate without table).
3. Kitchen: raw-food shelf/stockpile **adjacent** to the stove (otherwise the cook hauls one meal's ingredients per trip); meal stockpile next door, later a freezer (coolers blue side in, fully enclosed and roofed, usually 2+). Keep fields, kitchen and freezer within a short walk.
4. Priority 2: wood-fired generator, conduits (buildings connect within 6 tiles), lamps, end table + dresser. **Keep batteries inside a roofed room.**
5. **Dumping zone** outside the home area for corpses/rot/filth, off walking paths.
6. **Fire break:** steel/stone wall strip between kitchen and bedrooms.

## Home area
Colonists **repair, clean and extinguish fires only inside the home area**. Add the whole base (including the freezer and power room) to the home area with `rw_ui_area(action=home_add, rect=...)`.