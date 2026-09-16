---
always: false
description: Pull in when a colonist's mood is below ~40%, a mental break alert fires,
  or when planning day 1-5 furniture (tables, beds, recreation) to prevent breaks.
name: mood-and-mental-breaks
tags:
- mood
- mental-break
- recreation
- rooms
- early-game
---

# Mood and mental breaks

## Thresholds (rw_state_pawn)
- Base mood by difficulty: Peaceful/Community builder 42, Adventure story 37, Strive to survive 32, Blood and dust 27, Losing is Fun 22; thoughts add to it.
- Base Mental Break Threshold is 35%. Major = 4/7 of it (20%), Extreme = 1/7 (5%). Minor threshold is capped between 1% and 50%.
- Traits shift it additively: Iron-willed and Steadfast lower it; Nervous, Volatile, Too smart, Neurotic, Very neurotic raise it. Read the per-pawn value instead of assuming 35%.
- Mean time to break below a line: minor 10 days, major 3 days, extreme 0.7 days. Sleeping/unconscious pawns cannot break.

## Crisis debuffs that recur (the ones that actually broke colonists in play)
These stack with each other and with low food; a food crisis + one of these = guaranteed break within ~1 day.
| Debuff | Mood | Source / fix |
|---|---|---|
| **Alcohol/drug withdrawal** | **-35** | A colonist with an addiction (alcohol, etc.) who has NO drug in the colony. This is the single largest early mood killer — it alone drives a pawn to 0% and a psychotic break. If a refugee arrives with an addiction, you MUST bank that drug (or accept the break). Check each new colonist's needs for a drug need; set drug policy to allow it and keep stock. |
| Ate corpse meat | -12 | During a food crisis pawns eat corpses; each is -12 and a rot-stink source. Avoid by keeping ANY food (berries, rice) above 0. |
| Ate raw food | -7 | Set food policy to cooked/simple once a stove exists; raw is only a crisis stopgap. |
| Killed innocent animal | -15 | Hunting herbivores (ibex, deer, elk, boar, turkey) — the hunter eats -15 for days. Hunt sparingly, rotate hunters, only when food is critical. |
| Observed/rotting corpse | -4 / -6 | Corpses near the base (drifters, desiccated, mechanoids) cause -6 to ALL colonists. Haul to a dump or deconstruct; desiccated corpses may not be haulable — leave them far from the base. |
| No shepherd role (mod) | -5 | Some mods add a "Shepherd" ideo role; unfilled it is -5 to everyone. Not fixable without animals; drop it. |

## Common early debuffs (exact values)
| Thought | Mood | Fix |
|---|---|---|
| Ate without table | -3 | Table + stool/chair adjacent |
| Slept outside | -4 | Enclosed, roofed room |
| Slept on ground | -4 | Real bed (sleeping spot still gives it) |
| Slept in the cold / heat | -4 each | Heater/cooler, insulate |
| Disturbed sleep | -1, stacks to -3 | Private bedroom per pawn |
| Soaking wet | -3 | Roof over paths, stay in when raining |
| Ratty apparel (20-50% HP) / Tattered (<20%) | -3 / -5 | Tailor replacements |
| Confined interior (room < ~25 tiles) | -10 | Bedroom must be >= 5x5 interior; a 2-cell room is a -10 trap |
| Unsightly/ugly environment | -3.5 to -4 | Clean filth (dirt -5, blood -30 beauty per tile), smooth floors/walls (+2) |
| Badly malnourished | -26 | Food crisis; a starving colonist breaks fast — fix food before mood |

## Confined interior — the small-bedroom trap (verified day 10)
A bedroom smaller than ~25 interior tiles gives "Confined interior" (-10). A 2-cell room (just a bed + 1 free cell) is the worst case and is a real break trigger on its own. When you build a bedroom, make it at least 5x5 interior (>= 25 tiles) so the debuff never fires. A 4x3 room (12 tiles) still triggers it. Check room size with rw_state_rooms; if a colonist's bedroom is small, expand it (deconstruct the wall, rebuild bigger) rather than leaving the -10.

## Common buffs
- Beauty need >65% gives Pretty environment (+2.5 to +4.5); average tile beauty of 6 within 8 tiles pushes it to 100%.
- Comfort >60% gives Comfortable; a normal bed is 0.75 (sleeping spot 0.4), end table and dresser +0.05 each.
- Recreation 70-85% = satisfied, 85-100% = fully satisfied; below 30% = unfulfilled, below 15% = deprived, 0 = starved.
- Room impressiveness thoughts (bedroom/dining/rec) start at decent (>=40): slightly impressive about +3, extremely +6, wondrously +8. The weakest of Wealth/Beauty/Space/Cleanliness counts 51%, so keep rooms clean and about 25 space (5x5).
- Fine meals give an Ate fine meal buff; catharsis after a break is a temporary buff (+40, fades in ~2 days — plan for the drop).

## Recreation
- Falls 2.5%/hour, gains 36%/hour times activity power. Provide 2 recreation types under 15,000 wealth, 3 from 15,000-81,000. Boredom starts when one type's tolerance passes 50%.
- Day 1: Horseshoes pin (10 stuff, 100 work, no research; dexterity play, trains Shooting; needs a standable cell 5 tiles away in line of sight). Chess table needs Complex furniture (70 stuff, 8000 work) plus adjacent chairs; cerebral play. Social relaxing is a third free type.

## What to build days 1-5 (rw_ui_build)
1. Table (1x2 suffices for 3 pawns) plus stools, indoors, before the first meal. Cook simple meals at a Campfire; never let pawns eat raw.
2. One Bed per pawn (45 stuff, 800 work, Complex furniture) in its own enclosed room of at least 5x5 interior; three rooms beat one barracks by 4-5 mood each and remove Disturbed sleep. A room smaller than 5x5 triggers "Confined interior" (-10).
3. Roof everything, light bedrooms and dining room, set Cleaning 3-4 on everyone.
4. Horseshoes pin by the dining room, chess table when wood allows. Keep corpses in a dumping zone off walking routes.

## Responding to a break (rw_state_threats, rw_state_pawn)
- Minor: sad wander, hide in room, food binge (needs >10 nutrition in stockpiles), insulting spree. Wait it out; keep others away from an insulter.
- Major: daze, social drug binge, tantrum, targeted tantrum (can destroy component or medicine stacks; move them), bedroom tantrum.
- Extreme: berserk and worse. Draft the others (rw_ui_draft) and keep them clear; a berserker is only stopped by downing with blunt melee.
- Two pawns in a social fight (one holding a gun) will shoot each other: draft both and rw_ui_goto them far apart, undraft when it ends.
- Arrest interrupts most breaks but makes the pawn a prisoner and may trigger berserk; only arrest a wanderer heading into danger.
- Thoughts do not reset after a break: fix the largest negatives shown by rw_state_pawn.

Sources: Mood; Mental break; Mental Break Threshold; Thoughts; Thoughts/Memory Misc; Thoughts/Situation Needs; Thoughts/Situation General; Rooms; Recreation; Horseshoes pin; Chess table; Comfort; Beauty; Table; Bed; Sleeping spot; Scenario system; Apparel; Genes; Move Speed; Lost Tribe Guide; Persona weapon