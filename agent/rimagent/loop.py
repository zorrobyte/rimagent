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

LOG_CLIP = 3000
MARK = '\n…(log clipped {n} chars; the model was given the full result)'


def clip(text: str, limit: int = LOG_CLIP) -> str:
    """Shorten a tool result for the log, and say that is what happened."""
    return text if len(text) <= limit else text[:limit] + MARK.format(n=len(text) - limit)


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
    end_turn_called: bool = False
    end_turn_retried: bool = False
    transcript: list[dict[str, Any]] = field(default_factory=list)


END_TURN_RETRY = "You did not call end_turn, so this step recorded no notes and no wake plan. Call end_turn now with your notes and your wake plan."


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
        notebook=memory.notebook_read() or "(empty, start one)",
        journal=memory.journal_read(12) or "(empty)",
        operator="",
        scores=scorecard.history_text(8),
        tool_errors="\n".join(errs) or "(none)",
    )


def think(ctx: Context, user_message: str, situation_hint: str = "", *, max_calls: int | None = None, tool_groups: set[str] | None = None, thinking: bool | None = None, trigger: str = "scheduled", tool_allow=None, system: str | None = None, end_tools: tuple[str, ...] = ("end_turn", "end_episode")) -> StepResult:
    """Run a bounded tool-use loop. Returns when the model calls a terminal tool (end_turn/end_episode by default),
    stops calling tools, or hits max_calls. `system` overrides the play system prompt (streams that are not playing
    the colony, e.g. the watchdog, pass their own); `end_tools` are the only tools still offered once the cap is hit."""
    cfg = ctx.config
    max_calls = max_calls or int(cfg["play"].get("max_tool_calls", 30))
    ctx.reset_turn()
    ctx.registry.reload_brain()
    t0 = time.time()
    res = StepResult()
    system = system or build_system(ctx, situation_hint or user_message[:2000])
    messages: list[dict[str, Any]] = [{"role": "system", "content": system}, {"role": "user", "content": user_message}]
    tools = ctx.registry.specs(groups=tool_groups, allow=tool_allow)
    st = ctx.stream
    ctx.emit("think_start", {"trigger": trigger, "prompt_chars": len(system) + len(user_message), "tools": len(tools), "stream": st})
    step = 0
    # The end_turn retry spends one unit of the tool-call budget, so a step cannot run away on it.
    spent = 0
    while True:
        if res.calls + spent >= max_calls:
            messages.append({"role": "user", "content": f"You have used {res.calls} tool calls, the limit for this step. Call {end_tools[0]} now to finish."})
            tools_now = [t for t in tools if t["function"]["name"] in end_tools]
        else:
            tools_now = tools
        reply = None
        for attempt in range(2):
            try:
                reply = ctx.llm.chat(messages, tools_now, thinking=thinking if attempt == 0 else False, meta={"episode": ctx.episode, "seed": ctx.seed, "stream": st, "trigger": trigger, "step": step})
                break
            except Exception as e:  # noqa: BLE001
                ctx.emit("error", {"text": f"LLM call failed (attempt {attempt + 1}): {e}"})
        if reply is None:
            res.notes = "LLM error: gave up after 2 attempts"
            break
        step += 1
        if reply.reasoning:
            ctx.emit("reasoning", {"text": reply.reasoning[:20000], "stream": st})
        if reply.content:
            ctx.emit("assistant", {"text": reply.content, "stream": st})
        res.transcript.append({"role": "assistant", "content": reply.content, "reasoning": reply.reasoning[:4000], "tool_calls": reply.tool_calls})
        messages.append(ctx.llm.assistant_message(reply))
        if not reply.tool_calls:
            # Narration without action. Nudge back into the loop a couple of times before accepting it as the notes.
            nudges = res.transcript.count({"role": "nudge"})
            if nudges < 2 and res.calls + spent < max_calls:
                res.transcript.append({"role": "nudge"})
                messages.append({"role": "user", "content": "You wrote text but called no tool. Continue with tool calls, or call end_turn(notes, wake_in_hours, wake_on) if you are done with this step."})
                continue
            if not res.end_turn_called and not res.end_turn_retried and ctx.end_episode_reason is None:
                res.end_turn_retried = True
                spent += 1
                ctx.emit("log", {"text": "step ended without end_turn; asking for it once", "stream": st})
                messages.append({"role": "user", "content": END_TURN_RETRY})
                continue
            res.notes = reply.content.strip()
            break
        image_msgs: list[dict[str, Any]] = []
        for tc in reply.tool_calls:
            name, args, cid = tc["name"], tc["arguments"], tc["id"]
            ctx.emit("tool_call", {"name": name, "args": args, "id": cid, "stream": st})
            t1 = time.time()
            result, ok = ctx.registry.execute(ctx, name, args)
            res.calls += 1
            if ok and name == "end_turn":
                res.end_turn_called = True
            image = None
            if isinstance(result, dict) and "_image_png_b64" in result:
                image = result.pop("_image_png_b64")
            text = to_text(result)
            # The model gets `text` in full; the event and the transcript are clipped for size. Clipping them
            # without saying so makes the run log misrepresent what the model was given -- a result cut mid-JSON
            # reads as a malformed tool result rather than a long one. to_text already marks its own truncation.
            logged = clip(text)
            ctx.emit("tool_result", {"name": name, "id": cid, "ok": ok, "text": logged, "elapsed": round(time.time() - t1, 2), "stream": st})
            res.transcript.append({"role": "tool", "name": name, "ok": ok, "text": logged})
            messages.append({"role": "tool", "tool_call_id": cid, "content": text})
            if image:
                image_msgs.append({"role": "user", "content": [{"type": "text", "text": f"Image from {name}:"}, {"type": "image_url", "image_url": {"url": f"data:image/png;base64,{image}"}}]})
            if ctx.stop_turn:
                break
        messages.extend(image_msgs)
        if ctx.interrupt_check is not None and not ctx.stop_turn:
            try:
                urgent = ctx.interrupt_check()
            except Exception as e:  # noqa: BLE001
                urgent = []
                ctx.emit("error", {"text": f"interrupt check failed: {e}"})
            if urgent:
                ctx.emit("log", {"text": "urgent events delivered mid-step: " + "; ".join(u[:60] for u in urgent)})
                messages.append({"role": "user", "content": "## URGENT, happened while you were thinking (the game is now paused)\n" + "\n".join(f"- {u}" for u in urgent) + "\nDeal with these first (dialogs: rw_ui_dialog; threats: draft/position; downed: rescue), then continue."})
        inbox = ctx.extra.get("operator_inbox")
        if inbox:
            msgs, inbox[:] = list(inbox), []
            for m in msgs:
                ctx.emit("log", {"text": f"operator message delivered mid-step: {m[:80]}"})
            messages.append({"role": "user", "content": "## Message from the human operator\nAnswer it NOW with the reply_to_operator tool (one or two sentences). "
                             "If it is a tip or instruction about how to play, LEARN it: edit the most relevant skill with skill_write so it says this from now on "
                             "(mark the line 'operator tip'), and act on it in the colony if it applies right now. Then continue.\n" + "\n".join(f"- {m}" for m in msgs)})
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
    if not res.end_turn_called and ctx.end_episode_reason is None:
        ctx.emit("log", {"text": "step ended without end_turn: no notes and no wake plan were recorded" + (" (the retry also failed)" if res.end_turn_retried else ""), "stream": st})
    ctx.emit("think_end", {"notes": res.notes, "wake": {"in_hours": ctx.wake.in_hours, "on_kinds": ctx.wake.on_kinds}, "calls": res.calls,
                           "end_turn": res.end_turn_called, "end_turn_retried": res.end_turn_retried,
                           "elapsed": round(res.elapsed, 1), "end_episode": ctx.end_episode_reason, "stream": st})
    return res


# ---------------------------------------------------------------- steward block

STEWARD_UNAVAILABLE = "steward: unavailable"
_STEWARD_MAX_STOCK, _STEWARD_MAX_PROBLEMS, _STEWARD_MAX_PAWNS, _STEWARD_MAX_ORDER_LINES = 10, 5, 6, 8
_STEWARD_TOOLS = "Director tools: rw_steward_stock_set (target/suspend/allow), rw_steward_posture (temporary bias with hours), rw_steward_pawn (managed=false takes a pawn manual), rw_steward_explain (why a priority), rw_steward_stock_run (run a job now), rw_steward_orders_set (toggle a standing order), rw_steward_orders_explain (what an order does, what it leaves alone)."
# Standing orders (mod-side reflexes) in the canonical order the packet lists them; unknown ids from the mod are appended.
ORDER_IDS = ("combat", "rescue", "unforbid", "corpses", "beds", "policies", "blueprints", "fire")
TICKS_PER_HOUR = 2500
RALLY_MIN_COLONISTS = 3
RALLY_REMINDER = "rally: none — set one with rw_steward_orders_rally (rect inside the walls, near the hospital) so the combat order has somewhere to hold"


def _hours_ago(h: Any) -> str:
    if h is None:
        return "never"
    try:
        h = float(h)
    except (TypeError, ValueError):
        return str(h)
    if h < 1:
        return f"{int(round(h * 60))}m ago"
    return f"{h:.0f}h ago" if h >= 10 else f"{h:.1f}h ago".replace(".0h", "h")


def steward_stock_line(row: dict[str, Any]) -> str:
    """One stock job as a line: `wood 420/500 forestry ok (last run 2h ago)`; ✗ + the summary when short or stalled."""
    label = row.get("label") or row.get("kind") or "?"
    kind = row.get("kind") or "?"
    target, current = row.get("target"), row.get("current")
    failures = int(row.get("failures") or 0)
    designations = int(row.get("designations") or 0)
    summary = str(row.get("summary") or "").strip()
    below = isinstance(target, (int, float)) and isinstance(current, (int, float)) and current < target
    stalled = failures >= 3 or "stall" in summary.lower()
    bad = False
    if not row.get("enabled", True):
        state = "off"
    elif row.get("suspended"):
        state = "suspended"
    elif row.get("managed") is False:
        state = "manual"
    elif stalled:
        state, bad = f"STALLED ({failures} failed runs)", True
    elif below and designations > 0:
        state = f"ok, {designations} designated"      # under target but work is queued: the job is doing its thing
    elif below and row.get("last_run_hours_ago") is None:
        state = "pending first run"
    elif below:
        state, bad = "below target, nothing designated", True
    else:
        state = "ok"
    head = f"{label} {current if current is not None else '?'}/{target if target is not None else '?'} {kind} {state}"
    if bad and summary:
        head += f": {summary[:120]}"
    return ("✗ " if bad else "") + head + f" (last run {_hours_ago(row.get('last_run_hours_ago'))})"


def rally_is_set(rally: Any) -> bool:
    """steward.status.rally is a rect [x, z, w, h] or null (or missing on an older mod)."""
    return isinstance(rally, (list, tuple)) and len(rally) >= 4 and all(isinstance(v, (int, float)) for v in rally[:4])


def _num(v: Any) -> float | None:
    try:
        return float(v) if v is not None and not isinstance(v, bool) else None
    except (TypeError, ValueError):
        return None


def order_acted_since_step(o: dict[str, Any], since_hours: float | None) -> tuple[bool, str]:
    """(acted since the previous step ended?, suffix for the line). Orders run every 300-2500 ticks and a step is 2500+ ticks
    apart, so the last pass's `acting_on` alone misses most one-shot actions (unforbid, beds, policies): also honour, when the
    mod reports them, `acted_since_read` (actions accumulated since the last steward.status read), `last_acted_hours_ago`
    (compared with `since_hours`, the hours since the previous step ended) and `last_acted_tick` (vs `since_tick`)."""
    n = _num(o.get("acting_on")) or 0
    if n > 0:
        return True, f" (acting on {int(n)})"
    acc = _num(o.get("acted_since_read")) or 0
    if acc > 0:
        return True, f" (acted on {int(acc)} since your last step)"
    ago = _num(o.get("last_acted_hours_ago"))
    if ago is not None and since_hours is not None and ago <= since_hours:
        return True, f" (acted {_hours_ago(ago)})"
    return False, ""


def steward_orders_lines(status: dict[str, Any], colonists: int | None = None, since_hours: float | None = None) -> list[str]:
    """Standing orders as packet lines, all omitted when the mod does not report them (older mod):
    `orders: combat(rally set) rescue ✗corpses …` (✗ = disabled), one `- id: summary (acting on N)` line per order that
    acted since the last step (see order_acted_since_step), a `- id: state` line for any order reporting a persistent
    `state` (combat engaged, food switch active), and the rally reminder when no rally rect exists and colonists >= 3."""
    lines: list[str] = []
    rally_set = rally_is_set(status.get("rally"))
    orders = status.get("orders")
    rows: dict[str, dict[str, Any]] = {str(o["id"]): o for o in orders if isinstance(o, dict) and o.get("id")} if isinstance(orders, list) else {}
    if rows:
        ordered = [i for i in ORDER_IDS if i in rows] + [i for i in rows if i not in ORDER_IDS]
        words: list[str] = []
        acting: list[str] = []
        for oid in ordered:
            o = rows[oid]
            on = o.get("enabled", True) is not False
            word = oid if on else "✗" + oid
            if oid == "combat" and on and rally_set:
                word += "(rally set)"
            words.append(word)
            if not on:
                continue
            acted, suffix = order_acted_since_step(o, since_hours)
            state = str(o.get("state") or "").strip()[:140]
            if acted:
                acting_now = (_num(o.get("acting_on")) or 0) > 0
                summary = str((o.get("summary") if acting_now else o.get("last_acted_summary") or o.get("summary")) or "").strip()[:140] or "active"
                acting.append(f"- {oid}: {summary}{suffix}" + (f"; {state}" if state and state != summary else ""))
            elif state:
                acting.append(f"- {oid}: {state}")
        lines.append("orders: " + " ".join(words))
        lines += acting[:_STEWARD_MAX_ORDER_LINES]
        if len(acting) > _STEWARD_MAX_ORDER_LINES:
            lines.append(f"- … {len(acting) - _STEWARD_MAX_ORDER_LINES} more orders acting (rw_steward_orders)")
    combat_on = "combat" in rows and rows["combat"].get("enabled", True) is not False
    if "rally" in status and not rally_set and combat_on:
        try:
            enough = colonists is not None and int(colonists) >= RALLY_MIN_COLONISTS
        except (TypeError, ValueError):
            enough = False
        if enough:
            lines.append(RALLY_REMINDER)
    return lines


def steward_text(status: Any, colonists: int | None = None, since_hours: float | None = None) -> str:
    """Render steward.status as the packet's Steward block body (no heading). Pure; ~25 lines max.
    `colonists` (from state.summary) gates the rally reminder; None = never remind. `since_hours` = hours since the previous
    step ended (None = unknown), used to pick the orders that acted since then."""
    if not isinstance(status, dict):
        return STEWARD_UNAVAILABLE
    lines: list[str] = []
    en = status.get("enabled") or {}
    if isinstance(en, dict) and not (en.get("scorer", True) or en.get("stock", True)):
        lines.append("steward OFF (scorer and stock jobs disabled): you set priorities and designations yourself.")
    elif isinstance(en, dict) and (not en.get("scorer", True) or not en.get("stock", True)):
        lines.append("steward partly off: " + ", ".join(f"{k} {'on' if v else 'OFF'}" for k, v in en.items()))
    posture = status.get("posture")
    if isinstance(posture, dict) and posture:
        bits = []
        exp = posture.get("expires_in_hours")
        if exp is not None:
            bits.append(f"expires in {float(exp):.0f}h")
        for key, fmt in (("work", "{k} {v:+.1f}"), ("weights", "{k} x{v}"), ("targets", "{k} x{v}")):
            d = posture.get(key) or {}
            if isinstance(d, dict) and d:
                bits.append(key + ": " + ", ".join(fmt.format(k=k, v=v) for k, v in list(d.items())[:6]))
        lines.append(f"posture: {posture.get('label', '?')}" + (f" ({'; '.join(bits)})" if bits else ""))
    else:
        lines.append("posture: none (steady state)")
    stock = status.get("stock") or []
    if stock:
        lines.append("stock (target met = the job idles; raise the target to get more):")
        rows = [steward_stock_line(r) for r in stock if isinstance(r, dict)]
        rows.sort(key=lambda l: not l.startswith("✗"))   # problems first
        lines += ["- " + r for r in rows[:_STEWARD_MAX_STOCK]]
        if len(rows) > _STEWARD_MAX_STOCK:
            lines.append(f"- … {len(rows) - _STEWARD_MAX_STOCK} more jobs (rw_steward_stock_list)")
    else:
        lines.append("stock: no jobs (rw_steward_stock_add kind=forestry target=500 …)")
    problems = [str(x) for x in (status.get("problems") or []) if x]
    if problems:
        lines.append("problems:")
        lines += ["- " + x[:160] for x in problems[:_STEWARD_MAX_PROBLEMS]]
        if len(problems) > _STEWARD_MAX_PROBLEMS:
            lines.append(f"- … {len(problems) - _STEWARD_MAX_PROBLEMS} more")
    pawns = [p for p in (status.get("pawns") or []) if isinstance(p, dict) and p.get("managed") is False]
    if pawns:
        def prio(p: dict[str, Any]) -> str:
            pr = p.get("priorities") or {}
            items = sorted(pr.items(), key=lambda kv: (kv[1], kv[0]))[:4] if isinstance(pr, dict) else []
            return ", ".join(f"{k} {v}" for k, v in items) or "nothing enabled"
        lines.append("unmanaged pawns (their priorities are yours to keep; rw_steward_pawn managed=true hands them back):")
        lines += [f"- {p.get('name') or p.get('id')}: {prio(p)}" for p in pawns[:_STEWARD_MAX_PAWNS]]
        if len(pawns) > _STEWARD_MAX_PAWNS:
            lines.append(f"- … {len(pawns) - _STEWARD_MAX_PAWNS} more")
    lines += steward_orders_lines(status, colonists, since_hours)
    research = status.get("research")
    if isinstance(research, dict):
        research = research.get("queue")
    if isinstance(research, list) and research:
        lines.append("research queue: " + ", ".join(str(r.get("label") or r.get("def") or r) if isinstance(r, dict) else str(r) for r in research[:5]) + (" …" if len(research) > 5 else ""))
    if problems or any(l.startswith("- ✗") for l in lines):
        lines.append(_STEWARD_TOOLS)
    return "\n".join(lines)


def hours_since_last_step(ctx: Context) -> float | None:
    """Hours between the previous step's end and now, from the ticks the runner keeps in ctx.extra (None when unknown)."""
    tick = _num(ctx.extra.get("tick"))
    last = _num(ctx.extra.get("last_step_end_tick"))
    if tick is None or last is None or tick < last:
        return None
    return (tick - last) / TICKS_PER_HOUR


def steward_block(ctx: Context, colonists: int | None = None) -> str:
    """The packet's Steward section: rendered from steward.status, or `steward: unavailable` when the RPC is missing/failing."""
    try:
        status = ctx.bridge.call("steward.status")
    except Exception:  # noqa: BLE001  (BridgeError for unknown method / ok:false, or the bridge being down)
        status = None
    body = steward_text(status, colonists, hours_since_last_step(ctx)) if isinstance(status, dict) else STEWARD_UNAVAILABLE
    return "## Steward (sets work priorities, keeps stock targets, runs the standing orders; you direct it)\n" + body


def _colonist_count(summary: dict[str, Any]) -> int | None:
    n = summary.get("colonists")
    if isinstance(n, (int, float)):
        return int(n)
    if isinstance(n, list):
        return len(n)
    lst = summary.get("colonist_list")
    return len(lst) if isinstance(lst, list) else None


def situation_packet(ctx: Context, trigger: str, events: list[dict[str, Any]], alerts: list[dict[str, Any]], extra: str = "") -> tuple[str, str]:
    """Build the user message for a play step: change first, then objects, then raw state. Returns (message, hint)."""
    from . import tracker, worlddiff
    parts: list[str] = [f"## Wake trigger\n{trigger}"]
    hint = trigger
    if ctx.extra.get("sandbox"):
        parts.append("## SANDBOX EPISODE (not scored)\nGod mode is on: blueprints complete instantly and cost nothing; all research is unlocked. Use this game to EXPERIMENT and LEARN: build layouts you were unsure about, wire power grids and check state.power / map.power, test defenses with rw_dev_incident, try mechanics you have not used. After each experiment write what you learned into the relevant skill (with numbers) and, if it is a repeatable check, into a tool or watcher. Do not optimise the colony; optimise your skills.")
        hint += " experiment sandbox"
    summary: dict[str, Any] = {}
    base: dict[str, Any] = {}
    try:
        summary = ctx.bridge.call("state.summary")
    except Exception as e:  # noqa: BLE001
        parts.append(f"## Colony summary unavailable: {e}")
    try:
        base = ctx.bridge.call("state.base")
    except Exception as e:  # noqa: BLE001
        parts.append(f"## Base graph unavailable: {e}")
    if extra:
        parts.append(extra)
    try:
        dialogs = ctx.bridge.call("state.dialogs")
        if dialogs:
            parts.append("## OPEN DIALOGS, the game is paused until you answer with rw_ui_dialog(choice=...)\n" + json.dumps(dialogs, ensure_ascii=False)[:6000])
            hint += " dialog choice " + " ".join(str(d.get("text", ""))[:100] for d in dialogs)
    except Exception:  # noqa: BLE001
        pass
    if summary:
        try:
            parts.append("## Tracked values (trend, oldest→newest)\n" + tracker.sample(ctx.bridge, summary))
        except Exception as e:  # noqa: BLE001
            parts.append(f"## Tracked values unavailable: {e}")
    if summary and base:
        try:
            parts.append("## What changed since your last step\n" + worlddiff.diff_text(summary, base))
        except Exception as e:  # noqa: BLE001
            parts.append(f"## Diff unavailable: {e}")
    parts.append(steward_block(ctx, _colonist_count(summary) if summary else None))
    hint += " steward"
    if events:
        lines = [f"- [{e.get('day', '?')}d {e.get('hour', '?')}h] {e.get('kind')}: {e.get('text', '')}" for e in events[-80:]]
        parts.append(f"## New events ({len(events)})\n" + "\n".join(lines))
        hint += " " + " ".join(e.get("kind", "") for e in events[-30:])
    if alerts:
        parts.append("## Watcher alerts\n" + "\n".join(f"- {a.get('watcher')}: {a.get('text')}" for a in alerts))
    if summary.get("alerts"):
        parts.append("## Game alerts\n" + "\n".join(f"- [{a.get('priority')}] {a.get('label')}: {str(a.get('explanation', ''))[:160]}" for a in summary["alerts"][:12]))
        hint += " " + " ".join(str(a.get("label", "")) for a in summary["alerts"])
    try:
        letters = ctx.bridge.call("state.letters")
        if letters:
            parts.append("## Letters waiting (respond with rw_ui_letter or they pile up)\n" + json.dumps(letters, ensure_ascii=False)[:5000])
            hint += " " + " ".join(l.get("label", "") for l in letters)
    except Exception:  # noqa: BLE001
        pass
    if base:
        parts.append("## The base as objects (state.base)\n" + worlddiff.base_text(base))
    if summary:
        cols = summary.get("colonist_list") or []
        col_lines = [f"- {c.get('name')} ({c.get('id')}) at {c.get('pos')}: mood {c.get('mood')}, health {c.get('health')}, {c.get('job')}; {c.get('top_skills')}; weapon {c.get('weapon')}" + (" DOWNED" if c.get("downed") else "") + (f" MENTAL: {c.get('mental_state')}" if c.get("mental_state") else "") for c in cols]
        parts.append("## Colonists\n" + "\n".join(col_lines))
        slim = {k: v for k, v in summary.items() if k not in ("colonist_list", "alerts", "zones", "hostiles")}
        if summary.get("hostiles"):
            slim["hostiles"] = summary["hostiles"][:20]
        if summary.get("zones"):
            slim["zones"] = summary["zones"][:12]
        parts.append("## Colony numbers (state.summary)\n" + json.dumps(slim, ensure_ascii=False))
    parts.append("Act now. End with end_turn (notes + wake plan).")
    try:
        tracked = next((p for p in parts if p.startswith("## Tracked values")), "")
        changes = next((p for p in parts if p.startswith("## What changed")), "")
        ctx.emit("situation", {"trigger": trigger, "tracked": tracked.split("\n", 1)[-1] if tracked else "", "changes": changes.split("\n", 1)[-1] if changes else "", "day": summary.get("day"), "hour": summary.get("hour"), "chars": sum(len(p) for p in parts)})
    except Exception:  # noqa: BLE001
        pass
    return "\n\n".join(parts), hint
