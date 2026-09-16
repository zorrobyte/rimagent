"""Turn control, vision, and generic bridge access."""
from __future__ import annotations

import base64

from ..registry import tool


@tool("look", "Look at the game map as an image with a coordinate grid and numbered marks on every building/blueprint (Set-of-Mark). Returns the mark table (number -> id/def/cell) so you can refer to what you see. Use with rw_map_detail for exact placement.", {"x": "center x (default home)", "z": "center z", "w": "cells wide (default 60)", "around": "thing id or pawn to centre on", "marks": "draw numbered marks on things (default true)"}, group="meta")
def look(ctx, x: int | None = None, z: int | None = None, w: float = 60, around: str | None = None, marks: bool = True):
    if around:
        pos = ctx.bridge.call("map.detail", around=around, w=4, h=4)["centre"]
        x, z = pos[0], pos[1]
    if x is None or z is None:
        hc = ctx.bridge.call("state.base")["home_center"]
        x, z = hc[0], hc[1]
    wpx, hpx = 1024, 768
    png = ctx.bridge.screenshot(x, z, w, wpx, hpx)
    table = {}
    try:
        from .annotate import annotate
        detail = ctx.bridge.call("map.detail", x=x, z=z, w=min(60, int(w)), h=min(60, int(w * hpx / wpx) + 2))
        png, table = annotate(png, x, z, w, wpx, hpx, detail.get("things") or [], detail.get("anchors_in_view") or {}, marks=marks)
    except Exception as e:  # noqa: BLE001
        ctx.log(f"annotate failed: {e}")
    return {"_image_png_b64": base64.b64encode(png).decode(), "centre": [x, z], "cells_wide": w, "marks": table, "note": "grid lines every 5 cells, labels are x (top) and z (left); numbered circles = things in the mark table"}


@tool("rpc", "Call any RimBridge method by name (fallback if no rw_* tool fits). See rw_bridge_methods for the list.", {"method": "e.g. state.summary", "params": "object"}, group="meta")
def rpc(ctx, method: str, params: dict | None = None):
    return ctx.bridge.call(method, **(params or {}))


@tool("end_turn", "Finish this think step. Say what you decided, and when to wake next: after N in-game hours and/or on event kinds (letter, incident, colonist_downed, colonist_died, mental_break, hostile_group, quest, day, watcher). The game resumes after this.", {"notes": "1-3 sentences: what you did and what to check next", "wake_in_hours": "in-game hours until the next scheduled step (default from config)", "wake_on": "list of event kinds that should wake you early"}, group="meta")
def end_turn(ctx, notes: str = "", wake_in_hours: float | None = None, wake_on: list[str] | None = None):
    ctx.stop_turn = True
    ctx.wake.notes = notes
    ctx.wake.in_hours = wake_in_hours
    ctx.wake.on_kinds = wake_on or []
    return "turn ended; game resumes"


@tool("end_episode", "Declare this game over (colony lost, hopeless, or you want to start fresh). Triggers the long reflection and a new game.", {"reason": "why"}, group="meta")
def end_episode(ctx, reason: str):
    ctx.end_episode_reason = reason
    ctx.stop_turn = True
    return "episode will end after this step"


@tool("reply_to_operator", "Reply to the human operator watching the dashboard. Use this whenever you receive an operator message — even a greeting — before continuing your work. Short and direct.", {"text": "your reply"}, group="meta")
def reply_to_operator(ctx, text: str):
    ctx.emit("reply", {"text": text})
    return "delivered to the operator"


REPL_NS: dict = {}


def reset_repl() -> None:
    REPL_NS.clear()


@tool("run_python", "Persistent Python REPL (variables survive between calls and steps within a game). Available: rpc(method, **params), find(**params)=map.find, summary(), base()=state.base, detail(**p)=map.detail, build(**p)=ui.build, ctx, json, math, wiki, source. print() output is returned; set `result` for a value. Use it to compute over bridge data and keep references (e.g. beds = find(def='Bed')['things']).", {"code": "python code"}, group="meta")
def run_python(ctx, code: str):
    import contextlib
    import io
    import json
    import math

    from ..knowledge import source, wiki

    if not REPL_NS:
        REPL_NS.update({"ctx": ctx, "json": json, "math": math, "wiki": wiki, "source": source, "result": None,
                        "rpc": lambda method, **p: ctx.bridge.call(method, **p),
                        "find": lambda **p: ctx.bridge.call("map.find", **p),
                        "summary": lambda: ctx.bridge.call("state.summary"),
                        "base": lambda **p: ctx.bridge.call("state.base", **p),
                        "detail": lambda **p: ctx.bridge.call("map.detail", **p),
                        "build": lambda **p: ctx.bridge.call("ui.build", **p)})
    REPL_NS["ctx"] = ctx
    REPL_NS["result"] = None
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        exec(code, REPL_NS)  # noqa: S102 — the agent owns this machine
    out = buf.getvalue()
    res = REPL_NS.get("result")
    keys = [k for k, v in REPL_NS.items() if not k.startswith("_") and k not in ("ctx", "json", "math", "wiki", "source", "result", "rpc", "find", "summary", "base", "detail", "build") and not callable(v)]
    return {"result": res, "stdout": out[-6000:], "variables": keys[:30]}


@tool("watch_add", "Track a value; every step then opens with its recent trend (e.g. food_days 9→7→5 ↓). Paths: summary.<dotted key of state.summary> (e.g. summary.key_stocks.WoodLog, summary.outside_storage.rotting), stock.<ThingDef>, engine:<engine path> (e.g. engine:Pawn:Gamble.needs.mood.CurLevelPercentage), count:{\"def\":\"Plant_Rice\"} (map.find count).", {"label": "short name", "path": "what to read"}, group="meta")
def watch_add(ctx, label: str, path: str):
    from .. import tracker
    return tracker.add(label, path)


@tool("watch_remove", "Stop tracking a value.", {"label": "label"}, group="meta")
def watch_remove(ctx, label: str):
    from .. import tracker
    return tracker.remove(label)


@tool("watch_list", "List tracked values.", group="meta")
def watch_list(ctx):
    from .. import tracker
    return tracker.listing()
