"""The episode reflection's history across a restart.

A resume of the same game restored the counters but not the events and step notes, so the reflection saw
only the part of the episode after the restart, and nothing told it which days were missing.
"""
from __future__ import annotations

from types import SimpleNamespace
from typing import Any

import pytest

from rimagent import reflect, runner
from rimagent.runner import Runner


@pytest.fixture
def history_file(tmp_path, monkeypatch):
    path = tmp_path / "episode_history.jsonl"
    monkeypatch.setattr(runner, "HISTORY_FILE", path)
    return path


class FakeBus:
    def emit(self, kind, data=None):
        pass


class FakeBridge:
    def __init__(self, ledger: list[dict[str, Any]]):
        self.ledger = ledger

    def events(self, since, limit):  # noqa: ARG002
        evs, self.ledger = self.ledger, []
        return {"events": evs, "last_seq": since + len(evs)}


def _poller(ledger):
    r = SimpleNamespace(bridge=FakeBridge(ledger), bus=FakeBus(), ctx=SimpleNamespace(last_seq=0, recent_events=[]),
                        deaths=0, raids=0, events=[], pending_events=[], step_notes=[])
    r.add_note = lambda note: Runner.add_note(r, note)
    return r


def test_events_and_notes_survive_a_restart(history_file):  # noqa: ARG001
    runner._history_reset()
    r = _poller([{"kind": "colonist_died", "day": 6, "text": "Candice died"}])
    Runner.poll_events(r)
    r.add_note("the trap in the egress lane killed Candice")
    events, notes = runner._history_load()
    assert [e["text"] for e in events] == ["Candice died"]
    assert notes == ["the trap in the egress lane killed Candice"]


def test_a_reset_empties_the_history(history_file):  # noqa: ARG001
    runner._history_append([{"note": "from the last game"}])
    runner._history_reset()
    assert runner._history_load() == ([], [])


def test_the_reflection_is_told_which_days_its_timeline_holds():
    evs = [{"kind": "day", "day": 8}, {"kind": "day", "day": 15}]
    assert reflect.coverage(evs, 0, 15) == ("Timeline covers game days 8-15. The episode started on game day 0 "
                                            "and ended on game day 15. Days 0-7 are not in it.")
    assert "not in it" not in reflect.coverage([{"kind": "day", "day": 0}], 0, 15)
