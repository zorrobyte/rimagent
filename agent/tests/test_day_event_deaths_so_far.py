"""The day event carries deaths_so_far, the count the score uses, so the reflection timeline does not print `?`."""
from __future__ import annotations

from types import SimpleNamespace
from typing import Any

from rimagent import reflect
from rimagent.runner import Runner


class FakeBus:
    def __init__(self):
        self.events: list[tuple[str, dict[str, Any]]] = []

    def emit(self, kind, data=None):
        self.events.append((kind, data or {}))


class FakeBridge:
    def __init__(self, ledger: list[dict[str, Any]]):
        self.ledger = ledger

    def events(self, since, limit):  # noqa: ARG002
        evs, self.ledger = self.ledger, []
        return {"events": evs, "last_seq": since + len(evs)}


def _poller(ledger):
    return SimpleNamespace(bridge=FakeBridge(ledger), bus=FakeBus(), ctx=SimpleNamespace(last_seq=0, recent_events=[]),
                           deaths=0, raids=0, events=[], pending_events=[])


def test_the_day_event_carries_the_death_count_the_score_uses():
    r = _poller([{"kind": "colonist_died", "day": 6, "text": "Candice died"},
                 {"kind": "day", "day": 7, "data": {"colonists": 2}}])
    Runner.poll_events(r)
    day = r.events[-1]
    assert day["data"] == {"colonists": 2, "deaths_so_far": 1}
    assert "deaths_so_far=1" in reflect.compress_timeline(r.events, [])


def test_the_ledger_emit_carries_the_count():
    r = _poller([{"kind": "colonist_died", "day": 6}, {"kind": "day", "day": 7, "data": {}}])
    Runner.poll_events(r)
    assert r.bus.events[-1] == ("ledger", {"kind": "day", "day": 7, "data": {"deaths_so_far": 1}})


def test_a_day_event_with_no_count_still_prints_a_placeholder():
    assert "deaths_so_far=?" in reflect.compress_timeline([{"kind": "day", "day": 1, "data": {}}], [])
