---
always: false
description: Pull in when a pawn is injured, bleeding, downed, sick (infection, flu,
  plague, malaria), when setting medical policies, assigning a doctor, or deciding
  what medicine to use or grow.
name: medicine-and-health
tags:
- medicine
- health
- infection
- disease
- bleeding
- burns
- fire
- rescue
---

# Medicine and health

## Setup (do this on day 1)
- Doctor: highest Medicine skill gets Doctor priority 1 via rw_ui_set_work, a backup at 2. Pawns incapable of Caring never doctor. Enable self-tend only if nobody else can tend (x0.7 quality).
- Medical policy (rw_ui_set_policies medical): colonists default to Best, which wastes industrial medicine on bruises. Set colonists to HerbalOrWorse early and reserve industrial medicine for surgery, infections and plague. Prisoners default to herbal; keep HerbalOrWorse for recruits, NoMeds otherwise. Switch a patient to NormalOrWorse or Best the moment infection or plague appears.
- Grow healroot (Plant_Healroot) via rw_ui_zone once a grower has Plants 8: 7 grow days, min fertility 0.7, 1 herbal medicine per plant, survives winter. Plant 20+ tiles; one fight can cause half a dozen wounds.

## Medicine tiers
| Medicine | Potency | Max tend quality | Notes |
|---|---|---|---|
| None (doctor care) | 0.3 | 70% | Slow, one wound per tend; use for bruises |
| Herbal (MedicineHerbal) | 0.6 | 70% | Bleeding cuts, flu, cheap surgery |
| Industrial (MedicineIndustrial) | 1.0 | 100% | Infections, plague, surgery; 3 cloth + 1 herbal + 1 neutroamine at a drug lab (Medicine production) |
| Glitterworld (MedicineUltratech) | 1.6 | 130% | Trade only; save for lethal disease or risky surgery |

Tend quality = base (Medicine skill 0: 20%, 6: 80%, 8: 100%, 20: 155%) x potency, +0.1 hospital bed, +0.07 vitals monitor, x0.7 self-tend, randomised 0.75-1.25, capped at the medicine max. Doctor Manipulation and Sight scale it. Cleanliness affects infection chance, not tend quality; light affects tend speed and surgery. Surgery: Medicine 8 with industrial medicine, lit clean room and hospital bed hits the 98% cap; Medicine 11 without the hospital bed.

## Infection (the early-game killer)
- Chance per wound: bites/burns 30%, frostbite 25%, shredding 20%, other bleeding wounds 15%; bruises and lost parts never infect. Tending multiplies the chance by 85% at 0% quality down to 5% at 100%; a clean floored room halves it, sterile tile x0.32, outdoors x1.0. Tend every cut, indoors, promptly.
- Then it is a race: severity +0.84/day untreated, immunity +0.644/day (about 1.5 days), 100% tend slows it by 0.53/day. Untreated it kills in under 1.25 days. A rested, fed pawn needs at least 15% average tend quality, re-tended every 12 hours; herbal with a skill 6-10 doctor works if started within hours. Extreme stage (78%+) means unconsciousness; the Medical emergency alert fires at 80%.

## Burns (the fire-specific killer)
- Burns are the #1 cause of death in fire events. Burn severity: 0.5-1.5 minor (treatable), 2-4 moderate (needs herbal medicine), 5-8 severe (needs industrial medicine, hospital bed), 8+ lethal (torso burns at 8+ are almost always fatal without ultratech medicine).
- **Burns + heatstroke compound:** a pawn in a 1000C room gets both. Heatstroke alone kills in ~1 hour at extreme severity. Burns prevent the pawn from leaving the hot room (they're downed). This is the "death trap" pattern: downed in a hot room, no one can rescue them because all beds are in the fire zone.
- **Prevention beats treatment:** the best burn treatment is not being in the fire zone. Replace wood walls with steel/stone, keep a fire break between kitchen and bedrooms, and ensure at least one bed is OUTSIDE the main building (outdoor sleeping spot or a separate small room).
- **If a fire starts:** undraft all colonists immediately (drafted pawns cannot fight fires). If a colonist is downed in the fire zone, the rescue order will fail if no bed exists in safe temperature. Place a bed blueprint in safe ground and have a builder construct it while the fire burns out.
- **After the fire:** check all colonists for burns + heatstroke + bleeding. Burns have 30% infection chance. If you have no medicine, the best you can do is keep the patient warm, fed, and rested — immunity will fight it, but severe burns will likely kill.

## Other diseases (tend, feed, bed rest)
| Disease | Severity/day | Immunity/day | Max treatment slowdown | Kills untreated | Notes |
|---|---|---|---|---|---|
| Flu | 0.249 | 0.239 | 0.077 | 4.01 days | Survivable with bed rest alone; tend every 12 h |
| Malaria | 0.370 | 0.314 | 0.232 | 2.70 days | Needs >=24% average tend; lowers blood filtration, so rest matters |
| Plague | 0.666 | 0.522 | 0.363 | 1.5 days | Needs >=15% tend, every 15 h; use industrial medicine |

Immunity gain speed falls when hungry or tired and rises with bed type (ground 1.0, bed 1.07, hospital bed 1.11). Penoxycyline prevents malaria, plague and sleeping sickness.

## Bleeding and blood loss
- Blood loss stages: minor 15%, moderate 30%, severe 45%, extreme 60% (life threatening), death at 100%. Bleeding stops only when tended. Read "bleeding out in X hours" from rw_state_pawn and tend the fastest bleeder first.

## When a colonist is downed
1. Rescue with a right-click order: rw_ui_order(pawn=<healthy pawn>, at=<downed pawn id>, label="rescue") carries them to a Bed or sleeping spot in a safe-temperature room; a sleeping spot has only 0.7 surgery success.
2. **If the rescue order fails** (no bed in safe temperature, or the downed pawn is in a sealed room): check `rw_state_base` for TRAPPED colonists. Deconstruct the blocking wall if sealed. If no bed exists in safe temperature, place a bed blueprint in safe ground and have a builder construct it.
3. Doctor priority 1 and undrafted; tending and feeding happen in bed.
4. Later build a Hospital bed (Hospital bed research, Construction 8, 40 stuff + 80 steel + components: +0.1 tend, x1.1 surgery, 1.11 immunity), keep the room floored and clean, add sterile tile when steel allows.

## Prisoners
Capture downed raiders to a prisoner bed; the same doctor tends them at their own policy (herbal by default). Untended prisoners infect and die, so tend anyone you want to recruit.

Sources: Medicine; Herbal medicine; Glitterworld medicine; Doctoring; Infection; Disease; Flu; Plague; Malaria; Immunity Gain Speed; Healroot; Hospital bed; Hediffs/Core/Global/Misc/Blood loss; Rescue; Prisoner; Sleeping spot; Bed