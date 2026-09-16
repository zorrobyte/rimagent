---
always: false
description: Pull in when food stock is under ~10 days, when placing the first growing
  zones or choosing a crop, before designating any animal for hunting, and when deciding
  how to cook (campfire vs stove, raw food, food poisoning). Also pull in when food_days
  is 0 or negative and you need to understand why the colony is starving.
name: early-game-food
tags: []
---

# Early-game food

## Core numbers
- An adult human burns **1.6 nutrition/day**, stores 1.0 max. Below 25% saturation = Hungry (-6 mood), below 12.5% = Ravenously hungry (-12), 0% = malnutrition (+2%/hour, death at 100%; ~72.5 h from full to death).
- 1 raw item (rice, potato, corn, meat, berries) = **0.05 nutrition**. A simple meal costs **0.5 nutrition (10 raw items)** and gives **0.9** (180% efficiency). Budget **~2 simple meals or ~32 raw units per colonist per day**.
- Raw rice/potatoes/corn/meat/eggs: **Ate raw food -7 mood** and 2% food-poisoning. Berries and milk: no mood penalty, still 2%.

## Survival packs and the food policy trap (the #1 repeated starvation cause)
Survival packs (MealSurvivalPack) are a **distinct food category** — they are NOT "simple meals" and NOT "raw food". If the food policy is set to "Simple" or "Raw", colonists will NOT eat survival packs even if they are the only food in the stockpile.
- **Rule: when your only food is survival packs, set the food policy to "Any" (or "Survival" if available).** Check `rw_state_summary` for `food_days` — if it is 0 but survival packs are in the stockpile, the policy is wrong.
- **On refugee intake:** a new colonist may arrive with a food policy that excludes survival packs. Reset it to "Any" immediately.
- **On any food crisis:** first check `rw_state_summary` → `food_policy` (or read the pawn's policy). If the policy excludes the food you actually have, fix the policy before doing anything else.
- **Cooking survival packs:** you cannot cook them. They are pre-cooked. The only way to make them edible is to have the policy allow them.

## Crop comparison (normal soil; all need fertility >= 70%)
| Crop (def) | Grow days | Yield | Nutrition/harvest | Fertility sens. | Notes |
|---|---|---|---|---|---|
| Rice (Plant_Rice) | 3 | 6 | 0.30 | 100% | Fastest and most stable; 183% the labor of potatoes, 366% of corn. |
| Potato (Plant_Potato) | 5.8 | 11 | 0.55 | 40% | Best on gravel/stony soil; gains little from rich soil. |
| Corn (Plant_Corn) | 11.3 | 22 | 1.10 | 100% | Least labor; 150 HP; keeps without a freezer. One lost harvest hurts. |

Per tile per day all three are within ~5% (rice slightly ahead). Grow days assume full light and ideal temperature; night rest roughly doubles real time (potatoes ~10.7 days).

**Do this:** sow rice first. Once ~10 days of meals are banked, move most tiles to corn (less work per food). Use potatoes only when the fertile ground is gravel/stony. Within ~12 days of winter or a forecast cold snap, sow only rice; corn will not reach maturity and plants die below their minimum growth temperature.

## Field sizing and placement
- **10+ tiles per colonist** with year-round growing; **25 tiles per colonist** feeds one pawn indefinitely on Losing is Fun with simple meals, a Plants-6 grower and Growing at priority 1. Add more for unskilled growers or short seasons.
- `rw_ui_zone`: only on unroofed soil with fertility >= 70% and light >= 51%, near the kitchen/stockpile. Leave 4-tile gaps between fields (blight radius) and strip flammable plants within 2 tiles (raiders light fields).
- `rw_ui_set_work`: Growing = 1 for the best Plants pawn.

## "It will self-correct" is only true if BOTH hold (the #1 repeated failure)
A rice harvest "in 0.5 days" only saves you if:
1. **A grower has Growing 1 AND PlantCutting 1** — otherwise nobody cuts the rice and it just sits at 100% while the colony starves. When a new colonist joins, the new roster's priorities often reset; re-audit Growing/PlantCutting on everyone.
2. **A cook bill is running** (CookMealSimple on a campfire/stove) — harvested raw rice is useless until cooked, and raw food gives -7 mood. Check `rw_state_bills` / `food_outlook.cooking_bills`; if empty, queue the bill the same step.
3. **The food policy allows the food you have.** If you only have survival packs, the policy must be "Any" or "Survival". If you have raw rice, the policy must allow "Raw" or "Any". Check `food_outlook` → `food_policy` before calling a food crisis "self-correcting."
If any of the three is missing, the harvest ETA is meaningless. Verify all three before calling a food crisis "self-correcting."

## Foraging
Wild berry bushes give berries (14 days to rot). Find them with `rw_map_find` and harvest via `rw_ui_designate`. This bridges days 1-5 until the first rice comes in. In a crisis, designate 40-50 berry bushes for immediate food with no mood penalty.

## Hunting safely
- Only pawns holding a **ranged weapon** hunt; never send melee. Hunters fire from max range; long-range, high-damage-per-shot weapons (bolt-action rifle, greatbow) are safest. Revenge chance is **3x higher at close range**.
- Check **Revenge chance on harm** (`rw_defs_get` or the Wildlife list). Prefer **0%** animals: deer, gazelle, alpaca, dromedary. Do NOT hunt predators, boomrats/boomalopes (explode and start fires), or herd species with revenge chance: one manhunter can pull every same-species animal within 25 tiles.
- **Distance rule (episode 2 lesson):** Never send a hunter more than **30 cells from home** unless the colony has a second armed pawn to respond to threats. Episode 2: Onesan was 61 cells from base when a cougar found her; the colony could not respond in time and she died. If the animal is 30+ cells away, either (a) wait for it to come closer, (b) send two hunters, or (c) skip it.
- Hunting stealth = 5% per Shooting level + 5% per Animals level (cap 90%); low-skill hunters take only safe or already-injured prey. No incendiary weapons.
- **Hunted herbivores cost the hunter -15 mood** ("killed innocent animal") for days — in a small fragile colony, hunt sparingly and rotate who hunts.
- Hunted corpses are auto-unforbidden and hauled by the hunter.

## Butchering and cooking
- Drop a butcher spot immediately (free, 0 work) but it yields only 70% meat/leather; build a butcher table when materials allow. Raw meat rots in 2 days, vegetables ~30 days longer.
- `rw_ui_build` def **Campfire**: 20 wood, burns 10 wood/day, holds 20, must sit under a roof (rain burns extra fuel). `rw_ui_add_bill` "simple meal, do until you have 10-15". Campfire work speed factor is 0.5 (a 300-work meal takes 600); a fueled stove cooks 2x faster and unlocks fine meals.
- Give Cooking to the highest-skill cook. Food-poison chance by Cooking level: 0 = 5%, 3 = 2%, 4 = 1.5%, 6 = 0.5%, 8+ = 0.15% or less, scaled by kitchen cleanliness and difficulty (Losing is Fun x1.2). Skill 3+ in a clean room already beats raw food. Nutrient paste (dispenser + power) is 300% efficient and never poisons.
- Simple meals rot in 4 days at room temperature: cook small batches until a freezer exists.

Sources: Rice plant; Potato plant; Corn plant; Nutrition; Food; Saturation; Growing zone; Simple meal; Meals; Campfire; Food Poison Chance; Hunt; Hunting Stealth; Food production; Raw food; Berries; Butcher spot