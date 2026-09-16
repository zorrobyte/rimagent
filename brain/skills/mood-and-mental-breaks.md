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
- catharsis
---

# Mood and mental breaks

## Thresholds (rw_state_pawn)
- Base mood by difficulty: Peaceful/Community builder 42, Adventure story 37, Strive to survive 32, Blood and dust 27, Losing is Fun 22; thoughts add to it.
- `break_thresholds` in `state.pawn` is `[minor, major, extreme]` as percentages (e.g. `[35, 20, 5]`). No engine call needed.
- Mean time to break below a line: minor 10 days, major 3 days, extreme 0.7 days. Sleeping/unconscious pawns cannot break.

## The catharsis-fade crash (the #1 pattern that catches you off guard)
After any mental break, the colonist gets **catharsis +40** (or +30 for minor). This buff fades over ~2 days. While it's active, the colonist's displayed mood looks fine (e.g. 50%) even though the underlying debuff (alcohol withdrawal -35, malnutrition -26) is still there. When the catharsis fades, mood crashes to (real_mood - catharsis_value) and the break threshold is crossed.

**Rule: when a colonist has a catharsis buff, compute the post-fade mood = displayed_mood - catharsis_value. If that number is below the major threshold, treat it as an emergency NOW, not after the crash.**

Example: Cummings at 50% mood with catharsis +40 → post-fade mood = 10% → below major threshold 20% → extreme break in ~1-2 days. Start brewing research immediately; don't wait for the crash.

`mood_triage` tool returns `catharsis_buffer` per flagged pawn so you can see this without reading each pawn individually.

## Simultaneous extreme-break crisis (episode 2 lesson)
When **two or more colonists are simultaneously below their major threshold** (or one is at extreme and the other's catharsis is fading below major), the colony is in a death spiral:
1. **No one can tend the other.** If both are in breaks, no one is tending the downed one, no one is building, no one is cooking.
2. **Check the root cause first.** In episode 2: both colonists were starving (food policy excluded survival packs) → malnutrition -26 → mood collapse → breaks. Fix the food policy BEFORE trying to fix mood.
3. **If the root cause is food:** set food policy to "Any" immediately. Survival packs are the only food. The malnutrition debuff clears within ~12h of eating.
4. **If the root cause is drug withdrawal:** you cannot fix it without the drug. Accept the break or trade for the drug.
5. **If both will break and there is no recovery path** (no food, no medicine, no recovery path): note it in the notebook and consider ending the episode honestly. Do not waste steps on a lost cause.
6. **Wake in 1-2 hours** when both are at extreme risk. Check: did they eat? Did the catharsis fade? Is the mood still below threshold?

## How to triage a low-mood colonist (use `mood_triage` tool)
One call: `mood_triage()` returns for every colonist below their major threshold:
- `mood`, `major_threshold`, `extreme_threshold`
- `catharsis_buffer`: the catharsis thought if present (check its `mood` value to compute post-fade mood)
- `top_negatives`: top 4 negative thoughts sorted by value

Fix the biggest negative first. The usual hierarchy:
1. **Alcohol/drug withdrawal (-35)** — finish Brewing research, build FermentingBarrel, make beer. No other fix.
2. **Malnutrition (-26)** — food crisis; fix food before mood. **Check the food policy first** (see early-game-food skill).
3. **Killed innocent animal (-15)** — hunt sparingly, rotate hunters.
4. **Confined interior (-10)** — expand bedroom to ≥5×5 interior.
5. **Rotting/observed corpse (-6)** — haul to dump; desiccated corpses can't be hauled, move them far from base.
6. **Darkness (-5), Unsightly (-5), Tattered apparel (-5)** — light, clean, tailor.

## Crisis debuffs that recur (the ones that actually broke colonists in play)
| Debuff | Mood | Source / fix |
|---|---|---|
| **Alcohol/drug withdrawal** | **-35** | A colonist with an addiction who has NO drug in the colony. The single largest early mood killer. If a refugee arrives with an addiction, you MUST bank that drug (or accept the break). Check each new colonist's `needs` for a drug need; set drug policy to allow it. |
| **Malnutrition** | **-26** | Food crisis. **Check the food policy first** — if the policy excludes the food you have (e.g. "Simple" when only survival packs are in stock), fix the policy before anything else. |
| Ate corpse meat | -12 | During a food crisis pawns eat corpses; each is -12 and a rot-stink source. Avoid by keeping ANY food above 0. |
| Ate raw food | -7 | Set food policy to cooked/simple once a stove exists. |
| Killed innocent animal | -15 | Hunting herbivores — the hunter eats -15 for days. Hunt sparingly, rotate hunters, only when food is critical. |
| Observed/rotting corpse | -4 / -6 | Corpses near the base cause -6 to ALL colonists. Haul to dump; desiccated corpses may not be hauled. |
| No shepherd role (mod) | -5 | Some mods add a "Shepherd" ideo role; unfilled it is -5 to everyone. |

## Common early debuffs (exact values)
| Thought | Mood | Fix |
|---|---|---|
| Ate without table | -3 | Table + stool/chair adjacent |
| Slept outside | -4 | Enclosed, roofed room |
| Slept on ground | -4 | Real bed |
| Slept in the cold / heat | -4 each | Heater/cooler, insulate |
| Disturbed sleep | -1, stacks to -3 | Private bedroom per pawn |
| Soaking wet | -3 | Roof over paths |
| Ratty apparel (20-50% HP) / Tattered (<20%) | -3 / -5 | Tailor replacements |
| Confined interior (room < ~25 tiles) | -10 | Bedroom must be >= 5x5 interior; a 2-cell room is a -10 trap |
| Unsightly/ugly environment | -3.5 to -4 | Clean filth, smooth floors/walls |
| Badly malnourished | -26 | Food crisis; a starving colonist breaks fast — fix food before mood |

## Confined interior — the small-bedroom trap (verified day 10)
A bedroom smaller than ~25 interior tiles gives "Confined interior" (-10). A 2-cell room is the worst case and is a real break trigger on its own. Always build bedrooms at least 5×5 interior (>= 25 tiles). Check room size with `rw_state_rooms`; if a colonist's bedroom is small, expand it rather than leaving the -10.

## Common buffs
- Beauty need >65% gives Pretty environment (+2.5 to +4.5).
- Comfort >60% gives Comfortable; a normal bed is 0.75.
- Recreation 70-85% = satisfied, below 30% = unfulfilled, 0 = starved.
- Catharsis after a break: +40 (major/extreme) or +30 (minor), fades in ~2 days — plan for the drop.

## Recreation
- Falls 2.5%/hour, gains 36%/hour times activity power. Provide 2 recreation types under 15,000 wealth, 3 from 15,000-81,000.
- Day 1: Horseshoes pin (10 stuff, 100 work, no research; trains Shooting; needs a standable cell 5 tiles away in line of sight). Chess table needs Complex furniture.

## What to build days 1-5
1. Table (1x2 suffices for 3 pawns) plus stools, indoors, before the first meal.
2. One Bed per pawn in its own enclosed room of at least 5x5 interior; three rooms beat one barracks by 4-5 mood each.
3. Roof everything, light bedrooms and dining room, set Cleaning 3-4 on everyone.
4. Horseshoes pin by the dining room, chess table when wood allows.

## Responding to a break
- Minor: sad wander, hide in room, food binge. Wait it out.
- Major: daze, social drug binge, tantrum, targeted tantrum. Move them; draft others clear.
- Extreme: berserk and worse. Draft the others and keep them clear; a berserker is only stopped by downing with blunt melee.
- Two pawns in a social fight (one holding a gun) will shoot each other: draft both and `rw_ui_goto` them far apart.
- Thoughts do not reset after a break: fix the largest negatives shown by `mood_triage`.

Sources: Mood; Mental break; Mental Break Threshold; Thoughts; Rooms; Recreation; Horseshoes pin; Bed; Sleeping Spot; Apparel