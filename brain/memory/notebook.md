# Colony notebook — episode 1, seed rimagent-1

## Roster (3 colonists, home [135,138], TemperateForest, Summer, 25C)
- **Gamble** (Human490): Construction 12!!, Social 6!, Shooting 5! → BUILDER. Mood 67% (Catharsis +40). Malnutrition moderate (0.53), Consciousness 65%, Moving 46%. Resting at Bed39188 (now medical). Wu tending. Depressive + Very neurotic traits.
- **Sock** (Human493): Mining 10!!, Medicine 5! → MINER/COOK. Mood 51%. Cooking. Revolver.
- **Wu** (Human496): Medicine 8!!, Animals 3!! → DOCTOR/RESEARCHER. Mood 48%. Tending Gamble. Knife.

## Day 12 9h status
- Gamble: mood 67% (Catharsis +40, will fade in ~2 days → mood drops to ~27% then). Malnutrition 0.53, recovering. Resting at medical bed.
- Food 2.6 days. Rice: 71 plants, avg growth 0.698, 0 harvestable, est 0.9 days to full harvest. est_nutrition 21.3.
- 17 wall blueprints + 1 door frame pending (storage room). Sock Construction 2 building.
- 27 unroofed deteriorating items — storage room will fix.
- No hostiles. threat_points 35.
- Research: SolarPanels 19%.
- Shepherd role unfilled (-5 mood all). Ritual "role change" pressed but didn't complete. Try again next step.
- Medical bed set on Bed39188.

## Open / next
- Gamble: watch mood after Catharsis fades (~day 14). If <35%, need to fix: private bedroom (Room:9 is 2x1, -10 confined), table, cooked meals.
- Rice harvest: 0.9 days to full harvest. Sock Growing 1 will harvest.
- Storage room: 17 blueprints, Sock building.
- Shepherd role: try ritual again or engine assign.
- 27 unroofed items: storage room will fix.
- 4 fleshbeasts ~111 tiles NE (fogged, not urgent).

## Key coordinates
- Shelter [145,126,8,6], door [149,131], spike traps [147,132][149,132][151,132]
- Stockpile [142,136,8,6], Rice1 [122,143,6,6], Rice2 [119,152,6,6]
- Campfire [150,129], Research bench [148,128], Table [148,130]
- Horseshoes pins [146,124][151,130][152,132], Darktorch [151,128]
- Beds: [146,129] normal (medical), [146,130] good, [146,127] normal, [154,129] excellent
- RitualSpot [151,127]

Day 12: Operator built new compound SE of old room (all blueprints, 153 total).
Anchors: hall (dining/chairs/campfire/door N to old room via [147,126]), bed1 (8x7, 19 cells S), bed2 (8x7, 20 cells S), kitchen (fueled stove + butcher table), freezer (2 coolers + food stockpile), power (wood-fired gen + battery + conduits), dump (stockpile).
All 3 set to Construction 1, Hauling 2. Generator is blueprint — fuel with wood once built.
Tip: beds rot N extend 1 cell up — place 1 cell below wall.

Day12 23h: FIRE at old campfire/research-bench area [150,126] (Critical alert). Gamble beat it out; no fires left near home (others 73+ tiles away). A wall near the fire took damage to ~85% (self-repairs). All 3 set Construction 1 / Firefighter 1 / Hauling 2 to push the 153-blueprint compound. Food 3.0 days. Mood avg 60.

Day12 0h: Operator built compound (153 blueprints, 3 builders on Construction 1). Food 2.8 days but rice harvests in ~0.7 days (+~2 days nutrition) so it self-corrects. 2x CookMealSimple bills running on Campfire39256. Shepherd role still unfilled (-5 mood); role-change ritual dialog opened but had no clear choice, closed it — revisit later. Mood avg 62, no hostiles, threat 35.

Day13 13h: Compound building well — 112 blueprints + 4 frames pending, all 3 on Construction 1 (Gamble/Wu/Sock), building ~25 walls/day. Wood: 61 logs + queued 14 trees for harvestwood (Poplar/Oak near home). Steel 549. Food 2.8 days but rice harvest in 0.6 days (+21.6 nutrition) → self-corrects. Mood avg 66. No hostiles, threat 36.
Shepherd role alert (-5): role-change ritual dialog force-pauses but presents no clear choice → closed it twice. DROPPED for now (no animals to tend; low value). Revisit only if mood drops.

Day14 3h: Shepherd role alert is a MOD role (not in ideo's cachedPossibleRoles: only Leader/Moralist/MeleeSpecialist/ShootingSpecialist). Not fixable without animals. -5 mood, low priority. Sock swimming at [155,62] for joy (Joy 34%). Rice: 6 harvestable, 0.2 days to full harvest. 25 blueprints + 2 frames pending, all 3 builders on Construction 1. Food 2.2 days → rice harvest will fix it.

Day15 13h: 6 colonists now (Jess, Cummings, Babs joined). Cummings has LungRot (minor, both lungs) + rot stink exposure. Wu tending. Rice harvest in 0.3 days (42.3 nutrition). Gamble in sad wandering break (mood 40, threshold 49). Rotting corpse at [147,128] (Razor, drifter) causing rot stink - need to haul to dump. Food 0.9 days → rice harvest will fix. 23 unroofed deteriorating items.

Day18 6h: FIRE in power room [164-166,115-118] — 7 fire cells, threatening WoodFiredGenerator (87% HP) and Battery (62% HP). Gamble + Babs beating it out. Sock undrafted (was 70 tiles away). Battery at 62% — check if it survives.

Food: 0 days, 6 colonists. Rice: 144 plants, 3 harvestable, 1.2 days to full harvest, 43.2 nutrition. Designated 7 deer for hunting (Sock Hunting 1). No rice/meals in stockpiles.

Beds: 6 found but alert says not enough — some may be prisoner beds. Need to check.

Wu now researching (was idle). Gamble cleaning dirt (was idle).

Tattered apparel: Cummings, Jess, Babs. Warm clothes needed for winter (3C). Tailoring bench at [156,119].

Fixed unforbid_drops tool (was broken — map.find returns dict not list).
