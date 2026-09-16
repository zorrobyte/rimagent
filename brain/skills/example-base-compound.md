---
name: example-base-compound
description: A worked, verified example of a compact early base (hall + 2 bedrooms + kitchen + freezer + power) with exact relative rects, the order of operations, and the pitfalls that were hit while building it. Pull in when planning a base or extending one.
tags: [base, layout, example, building, kitchen, freezer, power]
always: false
---
# Example base: the "compound" (built day 12, episode 1, by the operator through the same tools you have)

Everything below is relative to an **origin O** = the SW corner of the hall (it was [140,116]). Rooms share walls, so steel goes further and heat stays in.

| anchor  | rect (x,z,w,h) relative to O | exterior | purpose | contents |
|---------|-------------------------------|----------|---------|----------|
| hall    | O + (0,0,15,8)                | 15x8     | dining/common room, hub | Table2x2c at `hall:C`, DiningChair at `hall:C +W1` and `+E2`, Campfire at `hall:inset:1:NE +W2 +S1` (heat + early cooking) |
| bed1    | O + (0,-6,8,7)                | 8x7      | bedroom, shares the hall's south wall | Bed at `bed1:inset:1:NW +E1 +S1` rot N |
| bed2    | O + (7,-6,8,7)                | 8x7      | bedroom, shares bed1's east wall | Bed at `bed2:inset:1:NW +E1 +S1` rot N |
| kitchen | O + (14,0,9,8)                | 9x8      | shares the hall's east wall | FueledStove at `kitchen:inset:2:NW +E2` rot S, TableButcher at `kitchen:inset:2:SW +E2` rot N |
| freezer | O + (14,-7,9,8)               | 9x8      | shares kitchen's south wall and bed2's east wall | 2x Cooler in the SOUTH wall at `freezer:S +W2` and `freezer:S +E2`, rot N (cold side faces into the room); stockpile `freezer:inset:1`, Foods only, priority Important |
| power   | O + (24,-4,5,6)               | outside  | WoodFiredGenerator at `power:C`, Battery at `power:NW +E1 +S1`; PowerConduit lines from the generator to the coolers (conduits may run under walls) |

Doors (steel): `hall:N` (to the older room — a door was also cut into that room's south wall), `bed1:N`, `bed2:N` (bedrooms open into the hall), `kitchen:W` (hall↔kitchen), `freezer:N` (kitchen↔freezer), `kitchen:E` (outside). Plus a DumpingStockpile outside the kitchen for corpses/rubble.

Materials: ~100 wall cells x5 steel, 6 doors x25, 2 coolers (90 steel + 3 components each), generator (100 + 2 comp), battery (70 + 2 comp), stove 80, butcher table 120 steel, beds 45 wood each. Total ≈ 1,200 steel, 8 components, 90 wood. Crashlanded loot has ~1,450 steel and 30 components lying around the pods — use it.

## Order of operations (this is what made it work in 4 calls)
1. `rw_map_detail` around the site (w=60) to find open ground with room for the whole block; `rw_anchor_set` every rect first.
2. `rw_ui_build_many` with **doors and wall-mounted things (coolers) BEFORE the wall rects** — walls then skip those cells ("Space already occupied" on exactly the door/cooler cells is expected and fine).
3. Furniture with `dry_run=true` first; read `failed[].reason` and the `camera` in the result, then place for real.
4. `rw_map_detail` again to verify; lowercase letters are your blueprints.
5. Put every colonist on Construction 1 / Hauling 2 until the frames are done, keep the generator fueled with wood, assign bedroom beds to owners.

## Pitfalls hit (and the fix)
- A 1x2 thing with rot N occupies its cell AND the cell above it. Place beds at least one cell below a wall (`...:NW +S1`).
- Stoves, butcher tables, benches, tables need a free **interaction cell** (marked `*` on the camera). Against a wall they fail with "Interaction spot will be blocked by wall"; step them one cell in (`inset:2`).
- `ui.build` needs `stuff` for stuff-made things; it lists the options with on-map counts when you omit it. Steel is a fine wall material when wood is short (fireproof, strong).
- Blueprints do not consume materials until built; check `rw_map_find(def="Steel")` for loose stock, not just `rw_state_stocks` (which counts stored items only).
- The camera hides conduits (they are clutter); check power by reading `rw_state_summary.power` once built.

## Why this shape
Shared walls (5 steel per cell saved twice), one hub room so pawns walk short paths, bedrooms private (mood), freezer adjacent to the kitchen (haul distance), power outside (fire risk), doors facing inward toward the hub. Extend by adding rooms to the free walls: `hall:extend:W:8` for a workshop, `kitchen:extend:N:8` for storage.
