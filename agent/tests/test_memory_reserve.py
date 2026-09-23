"""The last tool calls of a step belong to memory.

At the cap the tool list was narrowed to the end tools alone, so a step that ran long could not write the
notebook at the moment it most needed to. Episode 3, day 6: the step that watched a colonist die to the
colony's own spike trap used 32 calls, diagnosed it exactly, was denied `notebook_write`, and put the
diagnosis in its end_turn notes -- which no later step reads. Six steps later the model was working out who
had died and how, from the colonist count, because the notebook still described her as alive with mood 64.
"""
from __future__ import annotations

from rimagent.loop import MEMORY_TOOLS, tools_for_budget

END = ("end_turn", "end_episode")
TOOLS = [{"function": {"name": n}} for n in
         ("rw_map_view", "rw_ui_build", "notebook_write", "notebook_append", "journal_append",
          "journal_read", "end_turn", "end_episode")]


def names(pair):
    return {t["function"]["name"] for t in pair[0]}


def phase(pair):
    return pair[1]


def test_the_whole_toolbox_is_available_early():
    r = tools_for_budget(TOOLS, used=0, max_calls=30, reserve=3, end_tools=END)
    assert phase(r) == "full"
    assert "rw_map_view" in names(r)


def test_the_last_call_before_the_reserve_still_has_everything():
    r = tools_for_budget(TOOLS, used=26, max_calls=30, reserve=3, end_tools=END)
    assert phase(r) == "full" and "rw_ui_build" in names(r)


def test_the_reserved_tail_keeps_the_memory_tools():
    r = tools_for_budget(TOOLS, used=27, max_calls=30, reserve=3, end_tools=END)
    assert phase(r) == "reserve"
    for m in MEMORY_TOOLS:
        assert m in names(r), f"{m} must survive into the reserved tail"
    assert "end_turn" in names(r)


def test_the_reserved_tail_drops_ordinary_tools():
    r = tools_for_budget(TOOLS, used=27, max_calls=30, reserve=3, end_tools=END)
    assert "rw_map_view" not in names(r)
    assert "rw_ui_build" not in names(r)
    assert "journal_read" not in names(r), "the tail is for writing, not reading"


def test_the_hard_cap_still_offers_only_the_end_tools():
    """A step must not be able to spend the reserve on notes and then carry on."""
    r = tools_for_budget(TOOLS, used=30, max_calls=30, reserve=3, end_tools=END)
    assert phase(r) == "cap"
    assert names(r) == {"end_turn", "end_episode"}


def test_past_the_cap_stays_at_the_cap():
    r = tools_for_budget(TOOLS, used=41, max_calls=30, reserve=3, end_tools=END)
    assert phase(r) == "cap" and names(r) == {"end_turn", "end_episode"}


def test_a_stream_without_memory_tools_is_not_left_with_an_empty_toolbox():
    """The improve and watchdog streams do not carry brain tools; narrowing must not strand them."""
    lean = [{"function": {"name": n}} for n in ("read_source", "end_turn")]
    r = tools_for_budget(lean, used=28, max_calls=30, reserve=3, end_tools=END)
    assert "end_turn" in names(r)
    assert names(r), "never hand back an empty tool list"


def test_the_reserve_boundary_is_inclusive():
    """reserve=3 of 30 means calls 27, 28 and 29 are the tail, and 30 is the cap."""
    assert phase(tools_for_budget(TOOLS, 26, 30, 3, END)) == "full"
    for used in (27, 28, 29):
        assert phase(tools_for_budget(TOOLS, used, 30, 3, END)) == "reserve"
    assert phase(tools_for_budget(TOOLS, 30, 30, 3, END)) == "cap"
