"""One think step: a fresh, bounded tool-use conversation with the model."""
from __future__ import annotations

import json
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from . import memory, scorecard, skills
from .context import Context
from .registry import to_text

PROMPTS = Path(__file__).parent / "prompts"

DEFAULT_SYSTEM = """You are rimagent. You run this RimWorld colony by yourself through tools, and you improve your own skills, tools and reflexes between games. Nobody else will help.

Protocol for every step: read the situation, act on the most urgent thing with tools, update the notebook if something important changed, then call end_turn with a wake plan. Be terse in visible text; do the work with tool calls. Tool results are truncated at ~8k chars, so ask narrowly.

## Skills index
{skills_index}

## Always-on skills
{always_skills}

## Relevant skills for this step
{selected_skills}

## Colony notebook
{notebook}

## Journal (cross-game lessons, latest)
{journal}

## Standing instructions from the human operator (follow these; they override skills)
{operator}

## Episode scores
{scores}

## Brain load errors
{tool_errors}
"""


def load_prompt(name: str, default: str) -> str:
    p = PROMPTS / f"{name}.md"
    return p.read_text(encoding="utf-8") if p.exists() else default


@dataclass
class StepResult:
    notes: str = ""
    calls: int = 0
    elapsed: float = 0.0
    ended_by_tool: bool = False
    transcript: list[dict[str, Any]] = field(default_factory=list)


def _fmt(template: str, **kw: str) -> str:
    out = template
    for k, v in kw.items():
        out = out.replace("{" + k + "}", v)
    return out


def build_system(ctx: Context, situation_hint: str) -> str:
    all_skills = skills.load_all()
    always = [s for s in all_skills if s.always]
    selected = skills.select(situation_hint, k=4, skills=all_skills)
    errs = []
    errs += [f"tool file {f}: {e.strip().splitlines()[-1]}" for f, e in ctx.registry.load_errors.items()]
    errs += [f"watcher {f}: {e.strip().splitlines()[-1]}" for f, e in ctx.registry.watcher_errors.items()]
    return _fmt(
        load_prompt("system", DEFAULT_SYSTEM),
        skills_index=skills.index_text(all_skills),
        always_skills="\n\n".join(f"### {s.name}\n{s.body}" for s in always) or "(none)",
        selected_skills="\n\n".join(f"### {s.name}\n{s.body}" for s in selected) or "(none)",
        notebook=memory.notebook_read() or "(empty — start one)",
        journal=memory.journal_read(12) or "(empty)",
        operator=memory.operator_read() or "(none yet)",
        scores=scorecard.history_text(8),
        tool_errors="\n".join(errs) or "(none)",
    )


def think(ctx: Context, user_message: str, situation_hint: str = "", *, max_calls: int | None = None, tool_groups: set[str] | None = None, thinking: bool | None = None, trigger: str = "scheduled") -> StepResult:
    """Run a bounded tool-use loop. Returns when the model calls end_turn/end_episode, stops calling tools, or hits max_calls."""
    cfg = ctx.config
    max_calls = max_calls or int(cfg["play"].get("max_tool_calls", 30))
    ctx.reset_turn()
    ctx.registry.reload_brain()
    t0 = time.time()
    res = StepResult()
    system = build_system(ctx, situation_hint or user_message[:2000])
    messages: list[dict[str, Any]] = [{"role": "system", "content": system}, {"role": "user", "content": user_message}]
    tools = ctx.registry.specs(groups=tool_groups)
    ctx.emit("think_start", {"trigger": trigger, "prompt_chars": len(system) + len(user_message), "tools": len(tools)})
    step = 0
    while True:
        if res.calls >= max_calls:
            messages.append({"role": "user", "content": f"You have used {res.calls} tool calls, the limit for this step. Call end_turn now with your notes and wake plan."})
            tools_now = [t for t in tools if t["function"]["name"] in ("end_turn", "end_episode")]
        else:
            tools_now = tools
        try:
            reply = ctx.llm.chat(messages, tools_now, thinking=thinking)
        except Exception as e:  # noqa: BLE001
            ctx.emit("error", {"text": f"LLM call failed: {e}"})
            res.notes = f"LLM error: {e}"
            break
        step += 1
        if reply.reasoning:
            ctx.emit("reasoning", {"text": reply.reasoning[:20000]})
        if reply.content:
            ctx.emit("assistant", {"text": reply.content})
        res.transcript.append({"role": "assistant", "content": reply.content, "reasoning": reply.reasoning[:4000], "tool_calls": reply.tool_calls})
        messages.append(ctx.llm.assistant_message(reply))
        if not reply.tool_calls:
            # Narration without action. Nudge back into the loop a couple of times before accepting it as the notes.
            nudges = res.transcript.count({"role": "nudge"})
            if nudges < 2 and res.calls < max_calls:
                res.transcript.append({"role": "nudge"})
                messages.append({"role": "user", "content": "You wrote text but called no tool. Continue with tool calls, or call end_turn(notes, wake_in_hours, wake_on) if you are done with this step."})
                continue
            res.notes = reply.content.strip()
            break
        image_msgs: list[dict[str, Any]] = []
        for tc in reply.tool_calls:
            name, args, cid = tc["name"], tc["arguments"], tc["id"]
            ctx.emit("tool_call", {"name": name, "args": args, "id": cid})
            t1 = time.time()
            result, ok = ctx.registry.execute(ctx, name, args)
            res.calls += 1
            image = None
            if isinstance(result, dict) and "_image_png_b64" in result:
                image = result.pop("_image_png_b64")
            text = to_text(result)
            ctx.emit("tool_result", {"name": name, "id": cid, "ok": ok, "text": text[:3000], "elapsed": round(time.time() - t1, 2)})
            res.transcript.append({"role": "tool", "name": name, "ok": ok, "text": text[:3000]})
            messages.append({"role": "tool", "tool_call_id": cid, "content": text})
            if image:
                image_msgs.append({"role": "user", "content": [{"type": "text", "text": f"Image from {name}:"}, {"type": "image_url", "image_url": {"url": f"data:image/png;base64,{image}"}}]})
            if ctx.stop_turn:
                break
        messages.extend(image_msgs)
        inbox = ctx.extra.get("operator_inbox")
        if inbox:
            msgs, inbox[:] = list(inbox), []
            for m in msgs:
                ctx.emit("log", {"text": f"operator message delivered mid-step: {m[:80]}"})
            messages.append({"role": "user", "content": "## Message from the human operator\nAnswer it NOW with the reply_to_operator tool (one or two sentences), then act on it if it asks for something, then continue.\n" + "\n".join(f"- {m}" for m in msgs)})
        if ctx.stop_turn:
            res.ended_by_tool = True
            res.notes = ctx.wake.notes
            break
        # Keep the conversation bounded: if it grows huge, drop the oldest tool results' bodies.
        total = sum(len(m.get("content") or "") if isinstance(m.get("content"), str) else 0 for m in messages)
        if total > 160_000:
            for m in messages[2:]:
                if m.get("role") == "tool" and isinstance(m.get("content"), str) and len(m["content"]) > 400:
                    m["content"] = m["content"][:400] + "…(elided)"
                total = sum(len(m.get("content") or "") if isinstance(m.get("content"), str) else 0 for m in messages)
                if total < 120_000:
                    break
    res.elapsed = time.time() - t0
    ctx.emit("think_end", {"notes": res.notes, "wake": {"in_hours": ctx.wake.in_hours, "on_kinds": ctx.wake.on_kinds}, "calls": res.calls, "elapsed": round(res.elapsed, 1), "end_episode": ctx.end_episode_reason})
    return res


def situation_packet(ctx: Context, trigger: str, events: list[dict[str, Any]], alerts: list[dict[str, Any]], extra: str = "") -> tuple[str, str]:
    """Build the user message for a play step. Returns (message, hint-for-skill-selection)."""
    parts: list[str] = [f"## Wake trigger\n{trigger}"]
    try:
        summary = ctx.bridge.call("state.summary")
        parts.append("## Colony summary (state.summary)\n" + json.dumps(summary, ensure_ascii=False))
        hint = " ".join(str(a.get("label", "")) for a in summary.get("alerts", [])) + " " + trigger
    except Exception as e:  # noqa: BLE001
        parts.append(f"## Colony summary unavailable: {e}")
        hint = trigger
    try:
        dialogs = ctx.bridge.call("state.dialogs")
        if dialogs:
            parts.append("## OPEN DIALOGS — the game is paused until you answer with rw_ui_dialog(choice=...)\n" + json.dumps(dialogs, ensure_ascii=False)[:6000])
            hint += " dialog choice " + " ".join(str(d.get("text", ""))[:100] for d in dialogs)
    except Exception:  # noqa: BLE001
        pass
    try:
        letters = ctx.bridge.call("state.letters")
        if letters:
            parts.append("## Letters waiting (respond with rw_ui_letter or they pile up)\n" + json.dumps(letters, ensure_ascii=False)[:6000])
            hint += " " + " ".join(l.get("label", "") for l in letters)
    except Exception:  # noqa: BLE001
        pass
    if events:
        lines = [f"- [{e.get('day', '?')}d {e.get('hour', '?')}h] {e.get('kind')}: {e.get('text', '')}" for e in events[-80:]]
        parts.append(f"## New events since your last step ({len(events)})\n" + "\n".join(lines))
        hint += " " + " ".join(e.get("kind", "") for e in events[-30:])
    if alerts:
        parts.append("## Watcher alerts\n" + "\n".join(f"- {a.get('watcher')}: {a.get('text')}" for a in alerts))
    if extra:
        parts.append(extra)
    parts.append("Act now. End with end_turn (notes + wake plan).")
    return "\n\n".join(parts), hint
