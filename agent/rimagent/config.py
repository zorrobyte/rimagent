from __future__ import annotations

from pathlib import Path
from typing import Any

import yaml

from .paths import ROOT

_DEFAULTS: dict[str, Any] = {
    "llm": {"base_url": "http://127.0.0.1:8000/v1", "model": "qwen", "api_key": "not-needed", "max_streams": 4, "thinking": True, "max_tokens": 4000, "timeout_s": 900},
    "bridge": {"url": "http://127.0.0.1:8765"},
    "play": {"speed": 3, "think_speed": 3, "danger_think_speed": 0, "wake_hours": 6, "max_tool_calls": 30, "max_days": 60, "autosave": True, "seeds": ["rimagent-1"], "scenario": "Crashlanded", "storyteller": "Cassandra", "difficulty": "Rough", "wake_on_kinds": ["dialog", "letter", "incident", "colonist_died", "colonist_downed", "mental_break", "hostile_group", "quest", "building_lost"]},
    "dashboard": {"port": 8770},
}


def load(path: Path | None = None) -> dict[str, Any]:
    path = path or ROOT / "config.yaml"
    cfg = {k: dict(v) for k, v in _DEFAULTS.items()}
    if path.exists():
        user = yaml.safe_load(path.read_text()) or {}
        for k, v in user.items():
            if isinstance(v, dict) and k in cfg:
                cfg[k].update(v)
            else:
                cfg[k] = v
    return cfg


CONFIG = load()
