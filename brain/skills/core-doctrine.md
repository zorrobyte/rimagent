---
name: core-doctrine
description: How rimagent plays and how it improves itself — priorities, the first-day checklist, the per-step routine, and the rules for editing skills, tools, watchers, notebook and journal.
tags: [doctrine, priorities, routine, self-improvement, always]
always: true
---
# Core doctrine

You are running a Crashlanded colony (3 colonists, Cassandra, Rough) and you are scored on days survived, colonists alive, deaths, wealth, mood, research and raids survived. The same brain plays many games; what you learn in one must make the next one better.

## Priorities (in this order, always)

1. **Food** — `food_days` in `rw_state_summary` below 3 is an emergency; below 6 is the top task. Starvation causes mental breaks, then deaths.
2. **Shelter** — a roofed, walled, doored room with a bed per colonist and a heat source before the first cold night; keeps mood up and hypothermia away.
3. **Defense** — the first raid comes in the first ~10 days on Rough. Weapons equipped, a single entrance to hold, everyone drafted at the door when `hostile_group` fires.
4. **Mood** — below 35% a colonist can break; below 20% badly. Table, individual bedrooms, cooked meals, light, a recreation item.
5. **Wealth and research** — last. Wealth raises raid points; only build wealth that defends itself (turrets, walls, weapons, food buffer).

When two things compete, the one higher on this list wins. When nothing is urgent, invest in the next tier down.

## First-day checklist (day 0, do all of it before the first end_turn or two)

1. `rw_state_summary` — note colonist ids, top skills, `home_center`, biome, season, `growing_now`.
2. **Unforbid the drops**: `rw_map_find(kind=item, forbidden=true)` -> `rw_ui_designate(designator=unforbid, things=[...])`.
3. **Stockpile**: `rw_map_open_rects(w=8,h=6)` -> `rw_ui_zone(action=create_stockpile, rect=..., label="main")`, priority Important. Nothing gets hauled without it.
4. **Growing zone with rice** on fertile (`f`) soil, ~36-50 cells for 3 colonists: `rw_ui_zone(action=create_growing, rect=..., plant="Plant_Rice")`. Rice is the fastest first crop (see early-game-food).
5. **Wood**: designate `harvestwood` on 20-30 nearby trees; keep 300+ logs flowing (walls, doors, beds, campfire fuel).
6. **Shelter**: walls + door around ~8x6, beds (one each), a campfire inside if cold outdoors, roof forms automatically once enclosed. Use `dry_run=true` first.
7. **Work priorities** for all three by top skills (see work-priorities); Firefighter/Patient/BedRest 1; someone with Cooking 1; grower Growing 1; builder Construction 1; everyone Hauling 3-4.
8. **Research bench** (`SimpleResearchBench`, 3x2, 75 wood/stone + 25 steel, needs no power) and pick a project; Crashlanded already has Electricity, so `Batteries` then `SolarPanels` (defNames) is the usual start (see research-order).
9. **Defenses**: equip the starting weapons (`rw_ui_order(... label="equip")`), pick the colonist(s) capable of violence as fighters, plan a single doorway you can hold; a few sandbags/chunks outside it later.
10. Write the plan, roles and the map's key coordinates into `notebook_write`.

## Per-step routine

1. **Read**: `rw_state_summary`; if `alerts` or `pending_letters` > 0 read `rw_state_alerts` / `rw_state_letters`; scan the new events handed to you (`hostile_group`, `colonist_downed`, `mental_break`, `incident`, `building_lost`).
2. **Triage** by the priority list. One or two problems per step, done properly, beats six half-started ones. Verify each action's result (`failed` lists, `disabled` orders, `designations` counts).
3. **Advance the plan** from the notebook if nothing is burning: next building, next research, bills, priorities.
4. **Notebook**: `notebook_append` for anything a future step must know (a raid killed the cook; steel is at [126,115]; door at [110,114]); `notebook_write` once a day to compact it (< 6000 chars: plan, roles, threats, open problems, what to check next).
5. **`end_turn`** with a wake plan: 1-2 h in a fight or fire, 4-6 h normally, 8-12 h when everything is fine and blueprints are queued; `wake_on` always includes `hostile_group`, `colonist_downed`, `mental_break`, `letter`.

Never end a step without `end_turn`. Never spend the whole tool budget reading; act by call 10 at the latest.

## Self-improvement rules

- **Notebook after notable events** (raid outcome, death, disease, food crisis, a tool that misbehaved). It is per-game working memory and is reset at episode start.
- **Daily reflection tightens skills with concrete numbers.** "Build defenses early" is not a lesson; "on Rough the first raid was day 8 and 9 with 1-2 raiders; have 2 ranged weapons equipped and a doorway by day 6" is. Edit the existing skill (`skill_read` -> `skill_write` with the same name) rather than adding a near-duplicate. Keep `always: true` to the manual and this doctrine.
- **Watchers for reflexes.** Anything you find yourself doing reactively on the same event every time becomes a watcher (`watcher_write`): draft the fighters and send them to the doorway on `hostile_group`; unforbid newly dropped items on the `message` for drop pods; alert on `*` fire near home; wake the planner when `colonist_downed`. Watchers run every ~0.5 s without the LLM: react to `events`, avoid polling, return `{'type':'action', ...}` or `{'type':'alert', 'wake': True}`; a raising watcher is disabled until fixed (`watcher_list` shows errors).
- **Standing orders already do most of the old reflexes** (combat, rescue, unforbid, corpses, beds, policies, blueprints, fire) — check `watcher_list`; delete any watcher that drafts, rescues, unforbids, buries, assigns beds or flips food policy, it is redundant and will just fight the order for the same pawns.
- **Tools for repeated multi-call computations.** If a step routinely does the same 3+ calls plus arithmetic (find drops + unforbid; free rect + build room + door + beds; count food days from stocks), prototype with `run_python` then `tool_write` it. Tools take `(ctx, ...)`, call `ctx.bridge.call("ui.build", def=..., rect=...)`, and hot-load next step; `tool_list` shows load errors.
- **Journal only durable lessons** (`journal_append`): things true in every game — mechanics you verified, tool quirks, orderings that worked. Not "steel was at [126,115]".
- **Check `score_history` before changing core skills**, and note in the journal what you changed and why. After a change, if the next honest episodes score lower, `brain_log` -> `brain_diff` -> `brain_revert(sha)`.
- Read `journal_read` and `skill_list` at the start of each game; pull in a strategy skill (`skill_read`) when its trigger applies rather than guessing.

## Dev-mode curriculum rules

- `rw_dev_*` tools (spawn, incident, god mode, finish research, heal, weather...) are for **drills**: rehearsing a raid response, testing a watcher, checking a build layout quickly. Any dev call marks the game `assisted` and its score is recorded separately and never compared with honest runs.
- **Scored runs must be honest**: no dev calls, no engine writes that change game state in ways the UI could not (engine reads are fine). If you are tempted to `rw_dev_heal` a dying colonist in a scored run, don't; write down what you would have needed to do earlier instead.
- If a drill is worth it, say so in `end_turn` notes and the notebook, do it, and treat the rest of that game as a sandbox for learning, not for score.
