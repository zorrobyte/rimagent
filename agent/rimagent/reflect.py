"""Reflection steps: mid-episode improvement and end-of-episode learning."""
from __future__ import annotations

import json
from typing import Any

from . import memory, scorecard, skills
from .context import Context
from .loop import _fmt, load_prompt, think

DEFAULT_EPISODE = """# Episode reflection

This game is over ({reason}) after {days} days. Below is the timeline, your notebook, and the score history.
Your job now is to make the NEXT game go better. Do this concretely:
1. Identify the 2-4 decisions or omissions that mattered most (with evidence from the timeline).
2. Edit or create skills so the same situation is handled better next time (numbers, triggers, steps).
3. If something needed a fast reaction that you were late on, write or fix a watcher. If a multi-step check was repeated, write a tool.
4. Append ONE journal entry with durable lessons (not colony specifics).
5. If score_history shows the last brain change made things worse, consider brain_revert.
Finish with end_turn(notes=<one-paragraph summary of what you changed and why>).

## Tips from the human operator this run (make sure each one is reflected in a skill)
{operator}

## Timeline
{timeline}

## Notebook at the end
{notebook}

## Scores
{scores}

## Skills index
{skills_index}
"""

DEFAULT_IMPROVE = """# Mid-game improvement pass (day {days})

Take a step back from the colony. Look at what you have been doing every step (below) and turn repetition into automation:
- reactions you keep doing by hand → a watcher (draft on hostile_group, unforbid drops, flee fire, rescue downed, haul corpses…)
- multi-call checks you keep doing → a tool
- strategies that worked or failed → tighten the relevant skill with concrete numbers
Do not touch the colony in this step (no rw_ui_* calls). Finish with end_turn(notes=<what you changed>).

## Recent step notes
{timeline}

## Notebook
{notebook}

## Skills index
{skills_index}
"""


def compress_timeline(events: list[dict[str, Any]], step_notes: list[str], limit_chars: int = 14000) -> str:
    keep = ("day", "colonist_died", "colonist_downed", "incident", "hostile_group", "hostile_group_gone", "letter", "mental_break", "research_finished", "building_lost", "colonist_joined", "colonist_left", "quest", "assisted", "game")
    lines: list[str] = []
    for e in events:
        k = e.get("kind")
        if k not in keep:
            continue
        if k == "day":
            d = e.get("data") or {}
            lines.append(f"[day {e.get('day')}] colonists={d.get('colonists')} deaths_so_far=? wealth={d.get('wealth')} mood={d.get('mood_avg')} food_days={d.get('food_days')} threat={d.get('threat_points')} research={d.get('research_done')}")
        else:
            lines.append(f"[{e.get('day', '?')}d {e.get('hour', '?')}h] {k}: {e.get('text', '')}")
    text = "\n".join(lines)
    notes = "\n".join(f"- {n}" for n in step_notes[-40:] if n)
    out = text[-limit_chars:] + "\n\n## Your step notes\n" + notes[-4000:]
    return out


def episode(ctx: Context, events: list[dict[str, Any]], step_notes: list[str], reason: str, days: int) -> str:
    prompt = _fmt(
        load_prompt("reflect_episode", DEFAULT_EPISODE),
        reason=reason,
        days=str(days),
        timeline=compress_timeline(events, step_notes),
        operator=memory.operator_read(3000) or "(none)",
        notebook=memory.notebook_read() or "(empty)",
        journal=memory.journal_read(20) or "(empty)",
        scores=scorecard.history_text(12),
        skills_index=skills.index_text(),
    )
    res = think(ctx, prompt, situation_hint="reflection lessons " + reason, max_calls=30, tool_groups={"brain", "knowledge", "meta"}, trigger="episode reflection")
    return res.notes


def improve(ctx: Context, step_notes: list[str], days: int) -> str:
    prompt = _fmt(
        load_prompt("reflect_improve", DEFAULT_IMPROVE),
        days=str(days),
        timeline="\n".join(f"- {n}" for n in step_notes[-30:] if n) or "(none)",
        notebook=memory.notebook_read() or "(empty)",
        skills_index=skills.index_text(),
    )
    res = think(ctx, prompt, situation_hint="automation watcher tool skill", max_calls=20, tool_groups={"brain", "knowledge", "meta"}, trigger="improvement pass")
    return res.notes
