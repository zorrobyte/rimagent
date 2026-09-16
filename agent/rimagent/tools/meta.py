"""Turn control, vision, and generic bridge access."""
from __future__ import annotations

import base64

from ..registry import tool


@tool("look", "Look at the actual game map as an image (rendered from the engine camera; does not move the player's view). Use for layout sanity checks; use rw_map_view (ASCII) for exact coordinates.", {"x": "center x (default home)", "z": "center z", "w": "cells wide (default 60)"}, group="meta")
def look(ctx, x: int | None = None, z: int | None = None, w: float = 60):
    png = ctx.bridge.screenshot(x, z, w)
    return {"_image_png_b64": base64.b64encode(png).decode(), "note": f"image attached ({len(png)} bytes), {w} cells wide centred on {x},{z}"}


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


@tool("run_python", "Run a short Python snippet in the agent process with `ctx` available (ctx.bridge.call(...), ctx.knowledge, json, math). Use for one-off computations over bridge data before turning them into a real tool with tool_write. Return value = the `result` variable.", {"code": "python code that sets `result`"}, group="meta")
def run_python(ctx, code: str):
    import json
    import math

    from ..knowledge import source, wiki

    g = {"ctx": ctx, "json": json, "math": math, "wiki": wiki, "source": source, "result": None}
    exec(code, g)  # noqa: S102 — the agent owns this machine
    return g.get("result")
