# Journal — lessons that survive between games


## 2026-09-16 04:20 (episode 1): Watchers: pick pawns by live role, not name
In a watcher, never hardcode colonist names/ids — they differ every game. Instead call ctx.bridge.call("state.pawns", filter="colonists") at event time and select by live role (e.g. weapon string contains rifle/revolver/pistol/musket/bow for shooters; fallback to others). The `state.pawns` result has name, weapon, top_skills, pos, mood, health. This makes the hostile_draft watcher (draft up to 2 best shooters on hostile_group) game-agnostic. Same idea for any reactive: read state, pick the right pawn, act.

## 2026-09-16 04:20 (episode 1): Furniture needs a free interaction cell next to fires/stoves
A campfire/stove/table/bed needs a free adjacent cell for a pawn to interact. Placing a 2x2 table directly against a campfire blocks that interaction cell, so the cook/fuel job stalls (rw_ui_build may return a failed cell, or the pawn just stops). Leave one open approach cell. If a placement fails on a blocked-interaction reason, shift the item one cell rather than onto the fire's reach.

## 2026-09-16 04:47 (episode 1): Hunting herbivores costs mood
Killing an "innocent" herbivore (ibex, deer, elk, boar, turkey) triggers the "KilledInnocentAnimal_Horrible" moodlet (-15) on the hunter. This is a real tradeoff: those are the 0%-revenge species that are safe to hunt, but the hunter eats a -15 mood hit that lingers for days. For a small colony where a single colonist's mood is fragile (neurotic/depressive), prefer to (a) hunt sparingly, (b) rotate who hunts, or (c) accept the hit only when food is genuinely critical. If the hunter's mood is already low, the -15 can push them to a minor break.

## 2026-09-16 04:51 (episode 1): Stockpiles need roofs or items degrade
Operator tip: outdoor stockpiles with no roof cause items to degrade (steel, components, food, apparel all lose HP in rain). Check the roof layer over your main stockpile early (rw_map_view layer=roof). If it's all dots, build a roofed storage room or add a roof. Also check `outside_storage.storage_cells_free` in state.summary — if it's low, your stockpile is full and new items pile up loose/outside where they degrade. A roofed storage room (walls + roof + door) covering the stockpile is cheap insurance (~30 wall cells + roof) and protects 20+ stacks.

## 2026-09-16 05:06 (episode 1): Confined interior (-10) is a real break trigger; bedrooms need >= 25 tiles
A bedroom smaller than ~25 interior tiles gives the "Confined interior" moodlet (-10). A 2-cell room (just a bed + 1 free cell) is the worst case and on its own can push a fragile colonist (neurotic/depressive) to a mental break — verified day 10 with a 2-cell bedroom. Always build bedrooms at least 5x5 interior (>= 25 tiles). Check room size with rw_state_rooms; if a colonist's bedroom is small, expand it (deconstruct the wall, rebuild bigger) rather than leaving the -10.
