"""Harness-computed diff of the world between think steps: change is the signal, snapshots are noise."""
from __future__ import annotations

from typing import Any

_last: dict[str, Any] | None = None


def reset() -> None:
    global _last
    _last = None


def _room_line(r: dict[str, Any]) -> str:
    probs = ", ".join(r.get("problems") or [])
    doors = "; ".join(f"door→{d.get('leads_to')}" for d in r.get("doors") or []) or "no door"
    contents = ", ".join(f"{k} x{v}" if isinstance(v, int) else f"{k} x{len(v)}" for k, v in (r.get("contents") or {}).items())
    return f"{r.get('ref')} {r.get('role')} {r.get('size')} free={r.get('free_floor')} {doors} [{contents}]" + (f" PROBLEMS: {probs}" if probs else "") + (f" owner={r.get('owners')}" if r.get("owners") else "")


def base_text(base: dict[str, Any], max_rooms: int = 14) -> str:
    rooms = base.get("rooms") or []
    lines = [f"- {_room_line(r)}" for r in rooms[:max_rooms]]
    if len(rooms) > max_rooms:
        lines.append(f"- …and {len(rooms) - max_rooms} more rooms (state.base for all)")
    out = ["Rooms:"] + (lines or ["- (no enclosed rooms yet)"])
    if base.get("furniture_not_in_any_room"):
        out.append("FURNITURE NOT IN ANY ENCLOSED ROOM (open to the sky / walls missing): " + base["furniture_not_in_any_room"])
    so = base.get("structures_outside_rooms") or {}
    if so:
        out.append("Structures outside rooms: " + ", ".join(f"{k} x{len(v) if isinstance(v, list) else str(v).split(' ')[0]}" for k, v in list(so.items())[:12]))
    anchors = base.get("anchors") or []
    if anchors:
        out.append("Anchors: " + ", ".join(f"{a['name']} ({a['size']}, {a['from_home']})" for a in anchors[:16]))
    else:
        out.append("Anchors: none yet — name your rooms/sites with rw_anchor_set so you can refer to them.")
    tr = base.get("trapped_colonists") or []
    if tr:
        out.append("TRAPPED: " + "; ".join(f"{t['pawn']} at {t['at']} ({t.get('room')})" for t in tr))
    out.append(f"Blueprints pending: {base.get('blueprints_pending')}, frames in progress: {base.get('frames_in_progress')}")
    return "\n".join(out)


def snapshot(summary: dict[str, Any], base: dict[str, Any]) -> dict[str, Any]:
    rooms = {}
    for r in base.get("rooms") or []:
        contents = {k: (v if isinstance(v, int) else len(v)) for k, v in (r.get("contents") or {}).items()}
        rooms[r["ref"]] = {"role": r.get("role"), "size": r.get("size"), "free": r.get("free_floor"), "problems": tuple(r.get("problems") or []), "contents": contents, "doors": len(r.get("doors") or [])}
    outside = {k: (len(v) if isinstance(v, list) else str(v).split(" ")[0]) for k, v in (base.get("structures_outside_rooms") or {}).items()}
    cols = {c.get("name"): (c.get("mood"), c.get("job"), c.get("health")) for c in summary.get("colonist_list") or []}
    nums = {k: summary.get(k) for k in ("colonists", "wealth", "mood_avg", "food_days", "threat_points", "research_done", "blueprints", "frames", "designations", "temp_outdoor")}
    nums.update({"stock." + k: v for k, v in (summary.get("key_stocks") or {}).items()})
    return {"rooms": rooms, "outside": outside, "cols": cols, "nums": nums, "research": summary.get("research_current"), "day": summary.get("day"), "hour": summary.get("hour"), "trapped": [t.get("pawn") for t in base.get("trapped_colonists") or []], "furniture_out": base.get("furniture_not_in_any_room")}


def diff_text(summary: dict[str, Any], base: dict[str, Any]) -> str:
    """Compare with the previous step's snapshot and describe what changed. Also stores the new snapshot."""
    global _last
    cur = snapshot(summary, base)
    prev, _last = _last, cur
    if prev is None:
        return "(first step of this session — no diff yet)"
    out: list[str] = []
    dt = (cur["day"] or 0) - (prev["day"] or 0)
    out.append(f"time passed: {dt} days ({prev.get('hour')}h → {cur.get('hour')}h)")
    for k in cur["nums"]:
        a, b = prev["nums"].get(k), cur["nums"].get(k)
        if a is None and b is None:
            continue
        if isinstance(a, (int, float)) and isinstance(b, (int, float)):
            if abs(b - a) >= max(1, abs(a) * 0.05):
                out.append(f"{k}: {a:g} → {b:g}")
        elif a != b:
            out.append(f"{k}: {a} → {b}")
    if prev["research"] != cur["research"]:
        out.append(f"research: {prev['research']} → {cur['research']}")
    for ref, r in cur["rooms"].items():
        p = prev["rooms"].get(ref)
        if p is None:
            out.append(f"NEW room {ref} {r['role']} {r['size']} {('PROBLEMS: ' + ', '.join(r['problems'])) if r['problems'] else ''}")
            continue
        ch = []
        if p["size"] != r["size"]:
            ch.append(f"size {p['size']} → {r['size']}")
        if p["role"] != r["role"]:
            ch.append(f"role {p['role']} → {r['role']}")
        for d in set(r["contents"]) | set(p["contents"]):
            a, b = p["contents"].get(d, 0), r["contents"].get(d, 0)
            if a != b:
                ch.append(f"{d} {a}→{b}")
        if set(p["problems"]) != set(r["problems"]):
            gone = set(p["problems"]) - set(r["problems"]); new = set(r["problems"]) - set(p["problems"])
            if gone:
                ch.append("fixed: " + ", ".join(gone))
            if new:
                ch.append("NEW PROBLEM: " + ", ".join(new))
        if ch:
            out.append(f"{ref} {r['role']}: " + "; ".join(ch))
    for ref in set(prev["rooms"]) - set(cur["rooms"]):
        out.append(f"room {ref} gone (merged/opened/destroyed)")
    for d in set(cur["outside"]) | set(prev["outside"]):
        a, b = prev["outside"].get(d, 0), cur["outside"].get(d, 0)
        try:
            a, b = int(a), int(b)
        except (TypeError, ValueError):
            continue
        if a != b:
            out.append(f"outside: {d} {a}→{b}")
    for name, (mood, job, hp) in cur["cols"].items():
        p = prev["cols"].get(name)
        if p is None:
            out.append(f"colonist joined: {name}")
            continue
        if p[0] is not None and mood is not None and abs(mood - p[0]) >= 8:
            out.append(f"{name} mood {p[0]:g} → {mood:g}")
        if p[2] is not None and hp is not None and abs(hp - p[2]) >= 10:
            out.append(f"{name} health {p[2]:g} → {hp:g}")
    for name in set(prev["cols"]) - set(cur["cols"]):
        out.append(f"colonist gone: {name}")
    if cur["trapped"]:
        out.append("TRAPPED colonists: " + ", ".join(cur["trapped"]))
    if cur.get("furniture_out") != prev.get("furniture_out"):
        out.append(f"furniture outside enclosed rooms: {prev.get('furniture_out') or 'none'} → {cur.get('furniture_out') or 'none'}")
    return "\n".join(f"- {l}" for l in out[:40]) if len(out) > 1 else "- nothing notable changed"
