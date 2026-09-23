"""dev.* is reserved from scored play; a sandbox episode unlocks it."""
from __future__ import annotations

import pytest

from rimagent.registry import Registry

pytestmark = pytest.mark.usefixtures("clean_brain")


def _registry() -> Registry:
    reg = Registry()
    reg.add_bridge_methods([
        {"method": "state.summary", "doc": ""},
        {"method": "dev.god_mode", "doc": ""},
        {"method": "dev.reveal_map", "doc": ""},
    ])
    return reg


def _names(specs) -> set[str]:
    return {s["function"]["name"] for s in specs}


def test_play_stream_does_not_get_dev_tools():
    play = _names(_registry().specs())
    assert "rw_state_summary" in play
    assert "rw_dev_god_mode" not in play
    assert "rw_dev_reveal_map" not in play


def test_unlock_adds_dev_and_keeps_everything_else():
    sandbox = _names(_registry().specs(unlock={"dev"}))
    assert "rw_dev_god_mode" in sandbox
    assert "rw_state_summary" in sandbox


def test_asking_for_dev_by_name_gives_only_dev():
    assert _names(_registry().specs(groups={"dev"})) == {"rw_dev_god_mode", "rw_dev_reveal_map"}
