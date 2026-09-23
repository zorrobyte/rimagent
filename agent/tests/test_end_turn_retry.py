"""A step that ends without end_turn records no notes and no wake plan. The harness used to accept
that in silence: a whole 15-day run passed with 0 end_turn calls and nobody saw it."""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from rimagent import loop
from rimagent.context import Context
from rimagent.registry import Registry
from rimagent.tools import meta


class FakeBridge:
    def call(self, method, **params):  # noqa: ARG002
        return {}


@dataclass
class FakeReply:
    content: str = ""
    reasoning: str = ""
    tool_calls: list[dict[str, Any]] = field(default_factory=list)


class ScriptedLLM:
    """Replies one per turn; the last reply repeats once the script runs out."""

    def __init__(self, replies: list[FakeReply]):
        self.replies = replies
        self.turns: list[list[dict[str, Any]]] = []

    def chat(self, messages, tools, thinking=None, meta=None):  # noqa: ARG002
        self.turns.append(list(messages))
        return self.replies[min(len(self.turns) - 1, len(self.replies) - 1)]

    def assistant_message(self, reply):
        return {"role": "assistant", "content": reply.content, "tool_calls": reply.tool_calls}


def _end_turn_call(notes: str = "did the thing") -> FakeReply:
    return FakeReply(tool_calls=[{"name": "end_turn", "arguments": {"notes": notes, "wake_in_hours": 6}, "id": "c1"}])


def _run(replies: list[FakeReply], max_calls: int = 8):
    registry = Registry()
    registry.add_module(meta)
    events: list[tuple[str, dict[str, Any]]] = []
    llm = ScriptedLLM(replies)
    ctx = Context(bridge=FakeBridge(), llm=llm, registry=registry, config={"play": {}},  # type: ignore[arg-type]
                  emit=lambda k, d: events.append((k, d)))
    res = loop.think(ctx, "situation", system="system prompt", max_calls=max_calls)
    return res, events, llm


def _logs(events) -> list[str]:
    return [d["text"] for k, d in events if k == "log"]


def _retry_messages(llm: ScriptedLLM) -> list[str]:
    last = llm.turns[-1]
    return [m["content"] for m in last if m.get("role") == "user" and m.get("content") == loop.END_TURN_RETRY]


def test_end_turn_called_is_recorded_and_no_retry_is_sent():
    res, events, llm = _run([_end_turn_call()])
    think_end = next(d for k, d in events if k == "think_end")
    assert res.end_turn_called is True
    assert res.end_turn_retried is False
    assert think_end["end_turn"] is True
    assert think_end["notes"] == "did the thing"
    assert _retry_messages(llm) == []
    assert not any("without end_turn" in t for t in _logs(events))


def test_prose_instead_of_end_turn_is_retried_once_and_then_succeeds():
    # Two nudges come first; the model keeps narrating, then complies on the retry.
    res, events, llm = _run([FakeReply(content="I will keep watching."), FakeReply(content="Still watching."),
                             FakeReply(content="Nothing to do."), _end_turn_call("watched, nothing to do")])
    think_end = next(d for k, d in events if k == "think_end")
    assert res.end_turn_called is True
    assert res.end_turn_retried is True
    assert think_end["end_turn"] is True
    assert think_end["wake"]["in_hours"] == 6
    assert _retry_messages(llm) == [loop.END_TURN_RETRY]
    assert not any("no notes and no wake plan" in t for t in _logs(events))


def test_prose_twice_is_logged_and_the_step_ends():
    res, events, llm = _run([FakeReply(content="I am done for now.")])
    think_end = next(d for k, d in events if k == "think_end")
    assert res.end_turn_called is False
    assert res.end_turn_retried is True
    assert think_end["end_turn"] is False
    assert think_end["end_turn_retried"] is True
    assert res.notes == "I am done for now."
    assert len(_retry_messages(llm)) == 1
    assert "step ended without end_turn: no notes and no wake plan were recorded (the retry also failed)" in _logs(events)


def test_the_retry_is_never_sent_twice_in_one_step():
    _, _, llm = _run([FakeReply(content="narration")], max_calls=40)
    assert len(_retry_messages(llm)) == 1


def test_the_retry_spends_one_unit_of_the_tool_call_budget():
    # max_calls=1: the single tool call uses the budget, so the retry turn is offered end tools only.
    look = FakeReply(tool_calls=[{"name": "watch_list", "arguments": {}, "id": "c0"}])
    _, _, llm = _run([look, FakeReply(content="narration"), FakeReply(content="narration")], max_calls=2)
    offered = [m["content"] for m in llm.turns[-1] if m.get("role") == "user"]
    assert any(t.startswith("You have used") for t in offered)


def test_end_episode_is_a_legitimate_finish_and_is_not_retried():
    res, events, llm = _run([FakeReply(tool_calls=[{"name": "end_episode", "arguments": {"reason": "colony lost"}, "id": "c1"}])])
    assert res.end_turn_called is False
    assert res.end_turn_retried is False
    assert _retry_messages(llm) == []
    assert not any("without end_turn" in t for t in _logs(events))
