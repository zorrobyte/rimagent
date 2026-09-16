---
always: false
description: Pull in when a raid letter arrives, rw_state_threats shows hostiles,
  or when planning walls, traps, turrets, chokepoints and draft positioning for a
  small colony. Also pull in when a mech (Scyther, Centipede, Scorcher, Lancer) is
  on the map or sleeping nearby.
name: defense-basics
tags:
- defense
- raid
- traps
- turrets
- drafting
- mechs
---

# Defense basics

## What a raid costs
Raid points = (Wealth points + Pawn points) x Threat scale x Starting factor x Adaption factor. 1 point buys ~1 combat power; minimum 35 points, cap 10,000.
- Wealth points: 0 at storyteller wealth <= 14,000; 2,400 at 400,000 (~1 point per 161 wealth). Storyteller wealth = items + creatures + half of buildings.
- Pawn points: 15 per colonist at <= 10,000 wealth, up to 140 per colonist at 400,000. Attack-trainable animals add 8% of combat power.
- Starting factor 0.7 for days 0-10, 1.0 from day 40. Adaption factor starts 0.8, 30-day grace period (range 0.4-1.47).
- Threat scale: Adventure story 0.60, Strive to survive 1.00, Blood and dust 1.55, Losing is fun 2.20.
- Combat power: Drifter 35, Tribal archer 45, Warrior 50, Pirate gunner 65, Scyther 150, Centipede 400.
Human raiders flee once 40-70% of their group is downed or after 10-15 hours; mechanoids never flee.

## Raid types
| Letter | Behavior | Response |
|---|---|---|
| Walk in (45%) | Shortest open path to a colonist; walls only bashed if no open path | Fight at your chokepoint |
| "Preparing" | Idle near spawn first | Draft now, or snipe them |
| Drop pods (30%) | Land near a colonist (10% mid-base); ~9 s before pods open | Draft all armed pawns nearby, melee them as they emerge |
| Sappers / Breachers | Dig or smash walls straight toward an assigned bed; sappers avoid turret sight; smaller raid | Sortie and hit them at the wall; do not wait in the killbox |
| Siege | 2 mortars outside; assault after 1.5-3 days, or 8% chance per hit taken | Attack the camp early, or wound one to trigger the assault into your defenses |

## Structures
- Walls: 75% cover, block line of fire, pawns lean out at corners. Raiders take the quickest unobstructed route, so a perimeter with exactly ONE 1-wide entrance (bent so they cannot shoot in) turns every raid into a chokepoint fight. Stone is best; wood and steel burn. rw_ui_build def Wall.
- Sandbags/barricades: 55% cover, do not block line of fire, 5 cloth/leather or 5 blocks. Stone chunks: free 50%; trees 25%; bushes 20%. Cover is 100% effective at <15 degrees off-axis, 0% past 65 degrees, 33% at point-blank.
- A lone door is high cover; hold it open to fire through.
- Spike trap: 45 wood/stone/steel, single use, 5 stab hits from 100 base damage. Not placeable adjacent to another trap; **colonists CAN trigger them, raiders cannot see them.** Use a 2-wide entrance: traps in one lane, fences in the other so colonists take the fence lane.
- Mini-turret: Gun turrets research; 30 stuff + 70 steel + 3 components, 80 W, 60 shots per 80 steel reload. 12 damage 2-round burst, range 28.9, ~Shooting 8. 50% chance to explode (50 bomb, 3.9 radius) below 20% HP: space turrets 4+ tiles apart, off your firing line. Dead in a solar flare.

## Spike trap placement — the self-trigger trap (episode 3 lesson)
**Colonists trigger their own spike traps.** In episode 3, Kena was downed by a spike trap in the south approach lane [117,116] — the same lane she walked through to exit the barracks. The traps were meant for raiders but the colonist exit path went straight through them.

**Rule:** Place spike traps in the raider approach lane ONLY, offset from the colonist exit path. If the entrance is 2-wide: traps in the raider lane, a clear fence lane for colonists. If the entrance is 1-wide (a single door), put traps OUTSIDE the door on the raider approach side, not inside the barracks where colonists walk. A downed colonist in a spike trap is a medical emergency that wastes your doctor's time and can kill them if bleeding is severe.

## Sleeping mechs — the warning window (episode 2 lesson)
When `rw_state_threats` shows a mech with `LordJob_SleepThenAssaultColony` (or any mech within ~150 cells of home):
1. **This is a countdown, not a raid.** The mech will wake and assault with no warning letter. You have hours, not days.
2. **Assess immediately:** `rw_state_threats` → note the mech type, count, and distance. Scorcher (flameblaster) + Lancer (charge lance) = 2 mechs, ~300+ combat power combined. A 3-colonist colony cannot win this head-on.
3. **Build defenses in the window:**
   - If you have turrets: position them at the approach lane, fire-at-will on.
   - If you have steel: build sandbags (5 steel each) or a wall line at the chokepoint.
   - If you have wood: spike traps in the approach lane (5+ traps).
   - If you have cloth: sandbags (5 cloth each).
4. **Draft all shooters** and position them at the chokepoint BEFORE the mechs wake.
5. **If the odds are hopeless** (2+ mechs vs 2-3 colonists, no turrets, no walls): consider whether the colony is already lost. Do not waste steps on a lost cause. Note it in the notebook and end the episode honestly.
6. **Mechs never flee.** They do not retreat at 40% casualties. Plan for a total engagement or a total loss.

## Day 8-10 raid-prep checklist (3-colonist colony, walled barracks)
By day 8 you should have:
1. **Walled barracks** with one door (the chokepoint). Steel or stone walls preferred; wood is a fire risk.
2. **3 spike traps** in the raider approach lane (OUTSIDE the door, not in the colonist exit path).
3. **All 3 colonists armed and equipped:** best Shooting pawn holds the bolt-action rifle or revolver; second shooter holds the other ranged weapon; the third holds a melee weapon (plasteel knife is fine).
4. **Work priorities set:** the best shooter has Hunting 2 (so they can hunt if needed); the doctor has Doctor 1; the builder has Construction 1.
5. **Cooking bill running** (Forever CookMealSimple on campfire or stove). Food days >= 3.
6. **No forbidden items** in the approach lane (forbidden items block paths and cause pathing errors).
7. **Threat points read:** `rw_state_threats` shows the current threat level. At 35 points, expect a 1-2 raider raid (35-70 CP) within 2-3 days.
8. **Draft positions planned:** shooters at the door corners (1 tile inside, 1 tile apart), melee pawn just outside the door gap. Fire at will on all shooters.

**On the raid letter:**
1. `rw_state_threats` → note raider type, count, distance, and approach direction.
2. `rw_ui_draft` every violence-capable pawn BEFORE enemies are in range.
3. `rw_ui_goto` shooters to the door corners; melee pawn to the outside gap.
4. `rw_ui_attack` to focus the nearest raider or the one with a gun.
5. `rw_game_speed(speed=1)` — slow the fight down for precise orders.
6. After the raid: `rw_ui_draft(drafted=false)` for all, haul loot, capture downed raiders, rebuild traps, repair walls.

## Drafting checklist
1. On the letter: rw_state_threats, then rw_ui_draft every violence-capable pawn BEFORE enemies are in range. Drafted pawns ignore needs, so feed and rest them first if time allows.
2. rw_ui_goto shooters to wall corners or sandbags facing the approach, 1 tile apart. Up to 3 melee pawns stand just outside the door gap (not in it) to force a 1v3.
3. Fire at will handles targeting; rw_ui_attack to focus grenadiers or the nearest melee rusher. Never chase fleeing raiders.
4. Drag wounded out of the line of fire at once (a bleeding pawn has ~2 hours). Undraft when the raid flees so pawns eat, sleep and tend.
5. After: haul loot, capture downed raiders, rebuild traps, repair walls.

## First raid with 3 colonists
Expect 1-2 poorly armed raiders (35-50 points). Before day 10: walled bedroom block with one door, 3-5 wood spike traps in the approach lane, a chunk or sandbag line, best gun on the best Shooting pawn. Fight from the doorway, others beside a wall corner; never fight in the open.

## Desert biome note
In a desert biome wood is scarce (~150 logs total). Budget it: keep the day-1 shelter in wood (fast, cheap), but (a) build a 2-wide steel/stone fire break between the campfire/kitchen and the beds, (b) put at least one bed OUTSIDE the main building as a rescue target, and (c) only convert the walls adjacent to the fire source to steel/stone first. Do not queue a full steel re-wall until wood is no longer needed for beds/doors/research bench.

Sources: Raid points; Raider; Pirates/Pawns; Tribes/Pawns; Defense tactics; Defense structures; Cover; Sandbags; Spike trap; Mini-turret; Drafting