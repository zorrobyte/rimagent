---
always: false
description: Pull in when planning or placing walls, doors, roofs, floors, bedrooms/barracks,
  the home area, or choosing wood vs stone; also when a roof collapse, fire or "sleeping
  outside" mood problem shows up.
name: base-building
tags:
- building
- construction
- rooms
- layout
- materials
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
- Roofs are free and auto-designated over new rooms. Removing the last support within 6 tiles collapses the roof: thin/constructed roof deals **15-30 crush damage to head/neck** (roughly 1 in 3 kills an unhelmeted pawn); **overhead mountain** collapse destroys everything beneath. Lay a Remove-roof area before mining or deconstructing support walls. Trees cannot grow under roofs.
- Indoor items never deteriorate. Dark rooms give 80% work/move speed; a torch lamp is 10 wood for 10 days.

## Bedroom vs barracks
- Bedroom = only the owner's bed(s) (a lover pair is fine), no medical/prisoner bed; more than one unassigned bed makes a barracks. A barracks costs about **-5 mood vs an equivalent private bedroom** (-4 at higher quality). One barracks is fine on day 1; split into bedrooms within the first season.
- Impressiveness (bedroom/dining/rec mood): <20 awful, 20-30 dull, 30-40 mediocre, 40-50 decent, 50-65 slightly impressive, 65-85 impressive, 85-120 very impressive. It is weighted to the weakest of wealth/beauty/space/cleanliness, so a dirty floor drops a level. "Very impressive" needs a **5x5 or 4x6 interior**; 4x4 struggles. Returns diminish sharply.
- Space need (radius 7 walkable tiles): 1-3 = Confined -10, 4-10 = Cramped -5, 41+ = Spacious +5. Avoid 1-wide corridors and closet workstations.

## Floors
Floors stop wild plant growth, speed movement and remove the terrain cleanliness penalty (kitchen food poisoning, hospital, research). A 2-wide non-flammable strip is a fire break. Pawns pick up filth on soil (10%/step) and drop it on floors (5%/step), so floor the paths into the kitchen.

## Interaction clearance (furniture near fires/stoves)
- A pawn needs a **free adjacent cell** to use a campfire, stove, table or bed. Placing a 2x2 table directly against a campfire blocks the interaction cell and the bill/fuel job stalls (`rw_ui_build` returns a `failed` cell, or the pawn just stops). Leave at least one open cell on the side pawns approach from. When a placement fails with a blocked-interaction reason, shift the item one cell, not onto the fire's reach.

## Layout checklist (first days)
1. `rw_ui_zone` stockpile where the base will be and build around it (outdoor items take months to deteriorate).
2. Priority 1: walls + door for one ~7x7 room, beds (a normal bed saves ~1 hour sleep/day, avoids Slept on the ground), a 1x2 table + stools (avoids Ate without table).
3. Kitchen: raw-food shelf/stockpile **adjacent** to the stove (otherwise the cook hauls one meal's ingredients per trip); meal stockpile next door, later a freezer (coolers blue side in, fully enclosed and roofed, usually 2+). Keep fields, kitchen and freezer within a short walk.
4. Priority 2: wood-fired generator, conduits (buildings connect within 6 tiles), lamps, end table + dresser.

## Home area
Colonists **repair, clean and extinguish fires only inside the home area** (`rw_ui_area(action=home_add, rect=...)` / `home_remove`). Keep it tight around buildings and fields, add a 1-wide strip along critical conduits so short-circuit breaks self-repair, and widen it temporarily when a wildfire approaches.

Sources: Wall; Door; Roof; Rooms; Space; Thoughts; Wood; Stone blocks; Granite blocks; Sandstone blocks; Steel; Flammability; Floors; Home area; Basics