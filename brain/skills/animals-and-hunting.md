---
always: false
description: Pull in before designating hunt or tame on any wild animal, when a predator
  or manhunter pack is on the map, or when deciding which animals to keep, pen, train
  or butcher.
name: animals-and-hunting
tags:
- animals
- hunting
- taming
- predators
- manhunter
- pens
- food
---

# Animals and hunting

## Temperate-forest wildlife
Meat = 140 x body size (butcher spot 70%; kills by damage 66%). Revenge chance triples at close range.

| Animal | Revenge hurt | Revenge tame fail | Size / ~meat | Wildness | Notes |
|---|---|---|---|---|---|
| Squirrel, Hare | 0% | 0% | 0.2 / ~30 | 75% | Low value |
| Turkey | 0% | 0% | 0.6 / 84 | 45% | Easy meat |
| Wild boar | 0% | 0% | 0.85 / 119 | 50% | Herd |
| Deer | 0% | 0% | 1.2 / 168 | 75% | Best early hunt target |
| Alpaca | 0% | 0% | 1.0 / 140 | 25% | 45 wool per shear, pack animal |
| Muffalo | 10% | 0% | 2.4 / 336 | 60% | 120 wool per shear, pack animal |
| Elk | 0% | 0% | 2.1 / 294 | 75% | Herd, milkable |
| Timber wolf | 100% | 30% | 0.85 / 119 | 85% | Predator |
| Cougar | 50% | 30% | 1.0 / 140 | 80% | Predator, 6.7 s stun |
| Grizzly bear | 50% | 30% | 2.15 / 301 | 80% | Predator, 7 s stun, power 200 |

## Hunting rules
1. Ranged only: rw_ui_designate hunt on one animal at a time (rw_map_find kind animal). Hunters shoot from max range, finish the downed animal and haul it. Melee provokes any species.
2. Prefer 0% revenge species (deer, elk, boar, turkey). Never hunt predators with one pawn.
3. Do not hunt while rw_state_threats shows hostiles, in rain or snow (accuracy penalty), or into a muffalo herd. Wounded animals bleed out; do not chase them.
4. **Distance rule (episode 2 lesson):** Never send a hunter more than **30 cells from home** unless the colony has a second armed pawn to respond to threats. Episode 2: Onesan was 61 cells from base when a cougar found her; the colony could not respond in time and she died. If the animal is 30+ cells away, either (a) wait for it to come closer, (b) send two hunters, or (c) skip it.
5. Butcher promptly (rw_ui_add_bill on a butcher table); corpses rot in ~2 days when warm.

## Predators
Wolves, cougars and bears hunt anything smaller than themselves, pets and colonists included, when no meat or corpses are nearby. Their first strike stuns, and they keep attacking downed prey.
- On a "predator hunting" alert: rw_ui_draft 2-3 armed pawns and kill it together, or keep everyone indoors and let it eat wildlife. Rescue a downed victim immediately.
- **If the predator is 30+ cells from home:** do NOT chase it. Keep everyone indoors and let it eat wildlife. The colony cannot respond in time if the hunter is far from base.
- Predators ignore penned animals unless they wander in.

## Manhunter packs and mad animals
Pack points are 40% above a raid's; only fence-passing species are picked. They cannot open doors but bash one they saw you use, and linger 24-54 hours.
- Response: everyone indoors, doors closed, pets restricted inside. Fight only through a held-open door or 1-wide gap with melee blockers. Scaria may rot corpses.
- A mad animal charges the nearest human: draft 2-3 pawns and shoot it.

## Taming and training
- Tame chance multiplier is 2 x (1 - wildness): dogs/chickens 2x, alpaca 1.5x, muffalo 0.8x, deer 0.5x, bear/cougar 0.4x, wolf 0.3x. Bear, wolf and cougar attack 30% of the time on failure. Handlers need non-meal food matching the diet.
- Tame early: alpaca (wool, caravans, easy), muffalo (wool, pack animal), chickens (population doubles every ~5.7 days, eggs keep 15 days), labrador or husky (0% wildness, advanced trainability).
- Animals above 10.1% wildness lose training and tameness unless penned; keep a handler on Animals (rw_ui_set_work).
- Training: Guard (3 steps) follows a master; Attack (5) can be released on enemies; Rescue (2) and Haul (7) need advanced intelligence (dogs, wolves, cougars, bears). Attack-capable animals add 8% of combat power to raid points.
- Hunger per day: muffalo/elk 0.535, husky 0.5, alpaca 0.275, chicken 0.14; 1 hay or meat = 0.05 nutrition.

## Pens (1.6)
Farm animals (trainability "none": muffalo, alpaca, boar, chicken, turkey, deer, elk) ignore allowed areas and roam off the map unless roped into a pen: a pen marker (30 stuff) enclosed by fences, walls or doors with at least one gate (rw_ui_build Fence / FenceGate / PenMarker). A 20x20 grass pen barely feeds 4 pigs; build big and stock hay for winter. Raiders never target pen animals; cap herds with auto-slaughter.

Sources: Animals; Animal husbandry; Pen; Meat Amount; Wildness; Training; Deer; Elk; Wild boar; Grizzly bear; Timber wolf; Cougar; Alpaca; Muffalo; Hare; Squirrel; Turkey; Chicken; Labrador retriever; Husky; Events