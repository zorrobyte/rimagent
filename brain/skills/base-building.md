---
always: false
description: Pull in when planning or placing walls, doors, roofs, floors, bedrooms/barracks,
  the home area, or choosing wood vs stone; also when a roof collapse, fire or "sleeping
  outside" mood problem shows up.
name: base-building
tags: []
---

# Base building

## Structures: costs and numbers
| Def | Cost | Work | Base HP | Notes |
|---|---|---|---|---|
| Wall | 5 stuff | 135 x material factor | 300 x material | Supports roof up to 6 tiles away; 75% cover; blocks sight/fire lines. |
| Door | 25 stuff | 850 | 160 x material | Counts as wall for rooms/roof. Enemies treat closed doors like walls unless sapper/breacher. |

Materials: **Wood** 0.65x HP (195 HP wall), **100% flammable**, 0.7x work; **Steel** 300 HP, 40% flammable; ...rest ...

## "Placed" is not "built" and not "enclosed" (cost me 2 steps, ep.1)
- `ui.build` / `ui.build_many` / `room()` returning `placed` with **no `failed`** only means the **blueprints** went down. It says nothing about whether the walls got built or the room closed.
- A single **Door left as a frame** (blueprint, not built) keeps the room **"outdoors"**: beds/campfire count as outside and you get `slept outside` + `slept on the ground` + `slept in the cold` on every pawn (~-12 mood, mood 45 with food fine). The pawns will still build it eventually, but do not assume the room works until it is.
-Verify enclosure with state, not the build result:** `state.summary.room_digest` (or `state.rooms`) must show a `Barracks`/`Bedroom` row with a sane `cells` count and an indoor `temp`; `state.summary.blueprints` and `.frames` must both be **0**. Do not trust the build result; do not hand-designate the missing wall/door, just raise a Construction posture until frames==0.

 Rooms and roofs
- A room = area fully enclosed by walls/doors/coolers/rock; corners are optional (but leak heat).
- Roofs extend **6 tiles** from any wall/column; interiors up to 12 wide roof fully; wider needs interior columns/walls. Temperature control needs **>= 75% roofed**; under that the room snaps to outdoor temperature. 300+ unroofed tiles = "outdoors" (Slept outside, no room mood).
- Removing the last support within 6 tiles collapses the roof: thin/constructed roof deals **15-30 crush damage to head/neck** (roughly 1 in 3 kills an unhelmeted pawn); **overhead mountain** collapse destroys everything beneath. Lay a Remove-roof area before mining or deconstructing support walls. Trees cannot grow under roofs.

Layout checklist (first days)
1. `rw_ui_zone` stockpile where the base will be and build around it (outdoor items take months to deteriorate).
2. Priority 1: walls + door for one ~7x7 room, beds (one each, a normal bed saves ~1 hour sleep/day, avoids Slept on the ground), a 1x2 table + stools (avoids Ate without table).
3. Kitchen: raw-food shelf/stockpile **adjacent** to the stove (otherwise the cook hauls one meal's ingredients per trip); meal stockpile next door, later a freezer (coolers blue side in, fully enclosed and roofed, usually 2+). Keep fields, kitchen and freezer within a short walk of each other.
4. Priority 2: wood-fired generator, conduits (buildings connect within 6 tiles), lamps, end table + dresser.

Home area
- Colonists **repair, clean and extinguish fires only inside the home area** (`rw_ui_area(action=home_add, rect=...)` / `home_remove`). Keep it tight around buildings, a 1-wide strip along critical conduits so short-circuit breaks self-repair, and widen temporarily when a wildfire approaches.

Trap adjacency:
- Spike traps cannot be adjacent to another trap. The consequence is nastier than it seems: if you place a trap blueprint diagonally or orthogonally next an already-built trap, it simply NEVER fills — no error, no build icon change, just a blueprint count that stays put forever and looks like "no builder / no material". I lost several steps to one stray Blueprint_TrapSpike. Rule: after placing traps, list pending blueprints and CANCEL any trap blueprint that sits next to an existing trap; keep a lane's other column as fence/wall instead.