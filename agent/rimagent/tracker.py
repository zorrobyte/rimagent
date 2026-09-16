"""Tracked values: the model names what it wants to watch; every packet opens with their trend."""
from __future__ import annotations

import json
from typing import Any

from .paths import MEMORY

WATCH_FILE = MEMORY / "watch.json"
HISTORY = 8

DEFAULTS = [
    {"label": "food_days", "path": "summary.food_days"},
    {"label": "colonists", "path": "summary.colonists"},
    {"label": "mood_avg", "path": "summary.mood_avg"},
    {"label": "wood", "path": "summary.key_stocks.WoodLog"},
    {"label": "outside_storage", "path": "summary.outside_storage.stacks"},
    {"label": "threat_points", "path": "summary.threat_points"},
    {"label": "blueprints", "path": "summary.blueprints"},
]


def _load() -> dict[str, Any]:
    if WATCH_FILE.exists():
        try:
            return json.loads(WATCH_FILE.read_text())
        except Exception:  # noqa: BLE001
            pass
    return {"watch": list(DEFAULTS), "history": {}}


def _save(d: dict[str, Any]) -> None:
    WATCH_FILE.write_text(json.dumps(d, indent=1))


def reset() -> None:
    _save({"watch": list(DEFAULTS), "history": {}})


def add(label: str, path: str) -> str:
    d = _load()
    d["watch"] = [w for w in d["watch"] if w["label"] != label] + [{"label": label, "path": path}]
    _save(d)
    return f"watching {label} = {path}"


def remove(label: str) -> str:
    d = _load()
    n = len(d["watch"])
    d["watch"] = [w for w in d["watch"] if w["label"] != label]
    d["history"].pop(label, None)
    _save(d)
    return "removed" if len(d["watch"]) < n else "no such label"


def listing() -> str:
    d = _load()
    return "\n".join(f"- {w['label']}: {w['path']}" for w in d["watch"]) or "(nothing tracked)"


def _dig(obj: Any, dotted: str) -> Any:
    cur = obj
    for part in dotted.split("."):
        if isinstance(cur, dict):
            cur = cur.get(part)
        elif isinstance(cur, list) and part.lstrip("-").isdigit():
            cur = cur[int(part)]
        else:
            return None
        if cur is None:
            return None
    return cur


def _fmt(v: Any) -> str:
    if isinstance(v, float):
        return f"{v:.1f}" if abs(v) < 100 else f"{v:.0f}"
    if isinstance(v, (dict, list)):
        return json.dumps(v)[:40]
    return str(v)


def sample(bridge: Any, summary: dict[str, Any] | None) -> str:
    """Evaluate every watched path, append to history, return the trend block."""
    d = _load()
    lines = []
    for w in d["watch"]:
        label, path = w["label"], w["path"]
        try:
            if path.startswith("summary."):
                v = _dig(summary or {}, path[len("summary."):])
            elif path.startswith("stock."):
                v = (summary or {}).get("key_stocks", {}).get(path[len("stock."):])
                if v is None:
                    v = bridge.call("engine.call", path="Map.resourceCounter.GetCount", args=[path[len("stock."):]]).get("result")
            elif path.startswith("engine:"):
                v = bridge.call("engine.get", path=path[len("engine:"):], depth=0)
            elif path.startswith("count:"):
                v = bridge.call("map.find", **json.loads(path[len("count:"):]), limit=1).get("count")
            else:
                v = bridge.call("engine.get", path=path, depth=0)
        except Exception as e:  # noqa: BLE001
            v = f"err:{str(e)[:30]}"
        if isinstance(v, (int, float)):
            v = round(float(v), 2)
        hist = d["history"].setdefault(label, [])
        if not hist or hist[-1] != v:
            hist.append(v)
        del hist[:-HISTORY]
        nums = [x for x in hist if isinstance(x, (int, float))]
        arrow = ""
        if len(nums) >= 2:
            arrow = " ↑" if nums[-1] > nums[-2] else " ↓" if nums[-1] < nums[-2] else " ="
        lines.append(f"- {label}: {'→'.join(_fmt(x) for x in hist[-5:])}{arrow}")
    _save(d)
    return "\n".join(lines) or "(nothing tracked — use watch_add)"
