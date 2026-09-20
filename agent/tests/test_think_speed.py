"""The think speed a step ran at must be in the run log, not inferred from rate-limited status events."""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from rimagent import loop
from rimagent.context import Context
from rimagent.registry import Registry
from rimagent.runner import Runner


class FakeBridge:
    def call(self, method, **params):  # noqa: ARG002
        return {}


class FakeBus:
    def __init__(self):
        self.events: list[tuple[str, dict[str, Any]]] = []

    def emit(self, kind, data=None):
        self.events.append((kind, data or {}))


@dataclass
class FakeReply:
    content: str = "done"
    reasoning: str = ""
    tool_calls: list[dict[str, Any]] = field(default_factory=list)


class FakeLLM:
    def chat(self, messages, tools, thinking=None, meta=None):  # noqa: ARG002
        return FakeReply()

    def assistant_message(self, reply):
        return {"role": "assistant", "content": reply.content}


class FakeRunner:
    """Just the attributes Runner.with_pause touches, so the real method can be called against it."""

    def __init__(self, play: dict[str, Any]):
        self.cfg = {"play": play}
        self.bridge = FakeBridge()
        self.bus = FakeBus()
        self.thinking = False
        self.ctx = Context(bridge=self.bridge, llm=None, registry=Registry(), config=self.cfg, emit=lambda k, d: None)  # type: ignore[arg-type]


def _run_step(play: dict[str, Any], urgent: bool) -> dict[str, Any]:
    r = FakeRunner(play)
    seen: dict[str, Any] = {}
    Runner.with_pause(r, lambda: seen.update(r.ctx.extra), urgent=urgent)
    return seen


def test_with_pause_records_calm_speed_for_a_calm_step():
    seen = _run_step({"speed": 3, "think_speed": 2, "danger_think_speed": 0}, urgent=False)
    assert seen["think_speed"] == 2
    assert seen["config_think_speed"] == 2
    assert seen["config_danger_think_speed"] == 0
    assert seen["urgent"] is False


def test_with_pause_records_danger_speed_for_an_urgent_step():
    seen = _run_step({"speed": 3, "think_speed": 2, "danger_think_speed": 1}, urgent=True)
    assert seen["think_speed"] == 1
    assert seen["config_think_speed"] == 2
    assert seen["config_danger_think_speed"] == 1
    assert seen["urgent"] is True


def test_with_pause_falls_back_to_play_speed_when_think_speed_is_unset():
    seen = _run_step({"speed": 4}, urgent=False)
    assert seen["think_speed"] == 4
    assert seen["config_think_speed"] == 4
    assert seen["config_danger_think_speed"] == 0


def test_with_pause_clears_the_keys_after_the_step():
    r = FakeRunner({"speed": 3, "think_speed": 3, "danger_think_speed": 0})
    Runner.with_pause(r, lambda: None, urgent=False)
    for key in loop.THINK_SPEED_KEYS:
        assert key not in r.ctx.extra


def _think_start(extra: dict[str, Any]) -> dict[str, Any]:
    events: list[tuple[str, dict[str, Any]]] = []
    ctx = Context(bridge=FakeBridge(), llm=FakeLLM(), registry=Registry(), config={"play": {}},  # type: ignore[arg-type]
                  emit=lambda k, d: events.append((k, d)), extra=dict(extra))
    loop.think(ctx, "situation", system="system prompt", max_calls=1)
    return next(d for k, d in events if k == "think_start")


def test_think_start_carries_the_step_speed():
    data = _think_start({"think_speed": 0, "config_think_speed": 3, "config_danger_think_speed": 0, "urgent": True})
    assert data["think_speed"] == 0
    assert data["config_think_speed"] == 3
    assert data["config_danger_think_speed"] == 0
    assert data["urgent"] is True
    assert data["trigger"] == "scheduled" and data["stream"] == "play"


def test_think_start_omits_the_speed_fields_outside_a_step():
    data = _think_start({})
    for key in loop.THINK_SPEED_KEYS:
        assert key not in data
