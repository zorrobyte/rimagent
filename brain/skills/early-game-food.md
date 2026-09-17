---
always: false
description: Pull in when food stock is under ~10 days, when placing the first growing
  zones or choosing a crop, before designating any animal for hunting, and when deciding
  how to cook (campfire vs stove, raw food, food poisoning).
name: early-game-food
tags: []
---

# Early-game-food

## Setup (do this when food stock is under ~10 days, when placing the first growing zones or choosing a crop, before designating any animal for hunting, and when deciding how to cook (campfire vs stove, raw food, food poisoning))

## The Food Crisis
- If `food_days` is below 3, it's an emergency; below 6 is the top task.
- Starvation causes mental breaks, then deaths.
- Raw food is better than nothing, but cooked meals are much better for mood.
- Simple meals are essential for survival to prevent food poisoning and provide nutrition.

## Crops and Hunting
- Rice is the fastest first crop (see early-game-food).
- Plant on fertile (`f`) soil.
- Hunting is a backup; only the pawn holding the rifle hunts. Without a ranged weapon, Hunting never happens.

## Cooking Strategy
- Campfire: early, cheap, but slow and no protection.
- Fueled Stove/Electric Stove: better, requires fuel/power.
- Cook simple meals: `TargetCount` of 10 or 20.
- Ensure the bill is added to the table after building.

## Pitfalls
- Raw food but no meals -> check bills and cooking priorities.
- Biasing steward for food (e.g., Ayers and F to prioritize cooking).
- Ensure the stove actually has a CookMealSimple bill.