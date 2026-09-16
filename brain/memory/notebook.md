# Episode 3 — seed rimagent-3 (TemperateForest, Spring)

## Colonists
- Kena (Human926): Shooting 13!, Artistic 9, Construction 8 — BUILDER. Incapable of Cooking/Growing/PlantCutting.
- Lumi (Human929): Shooting 12!!, Melee 12!!, Social 7 — GROWER/COOK + hunter.
- Kat (Human932): Medicine 11!!, Intellectual 11!!, Melee 7 — DOCTOR/RESEARCHER.

## Roles
- Kena: Construction 1, BasicWorker 2, Hauling/Cleaning 3
- Lumi: Growing/PlantCutting 1, Cooking/Hunting 2, Hauling 3
- Kat: Doctor 1, Research 2, Hauling 3
- Everyone: Firefighter/Patient/BedRest 1

## FOOD POLICY — CRITICAL (verified from source FoodRestrictionDatabase.cs:109)
- **"Simple" does NOT include survival packs.** Simple blocks preferability>=9 AND explicitly SetAllow(MealSurvivalPack, false).
- "Raw" also excludes survival packs. Only "Any" or "Survival" allow them.
- 50 survival packs in stockpile. To eat them the policy MUST be "Any" or "Survival".
- Kat starved twice because policy was "Simple" while only survival packs were in stock. FIX: set all 3 to "Any" (or "Survival") so the packs are edible. Do NOT trust "Simple" for survival packs.
- Rice field "rice1" [113,113,6,5] (27 cells) — once rice is cooked into simple meals, policy can go back to "Simple"/"Cooked".

## Base
- Shelter built+roofed 10x8, 3 beds, campfire, research bench, door [117,117] (south chokepoint).
- 3 spike traps placed [117,116/114/112] in south approach lane.
- Stockpile "main" [118,110,8,6].
- Table [114,121] in barracks.

## Research
- SolarPanels in progress (Batteries done).

## Threats / notes
- Quail attack day 3: Kat injured (quail bites, bleeding stopped, in bed), Lumi minor injuries. Both tending.
- Wood walls = fire risk; plan steel fire break when steel flows.
- First raid ~day 8-10. Chokepoint = door [117,117].
- Kat consciousness recovering; check in ~4h.

## Open / next
- SET FOOD POLICY TO "Any" (survival packs won't eat under "Simple").
- Verify rice harvest + cooking bill running.
- Unforbid+haul steel for turrets/walls later.
- Fire safety: steel fire break.
