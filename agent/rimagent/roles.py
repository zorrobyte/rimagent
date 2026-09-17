"""Parallel mode: four specialist streams per calm step, each owning a slice of the write tools.

The steward (inside the mod) already sets work priorities and keeps stock targets; the streams direct it
through rw_steward_* and act at higher altitude (layout, defense, letters, policies)."""
from __future__ import annotations

import re
from typing import Callable

from .registry import Tool

# Tools every role may use (reads, knowledge, turn control, steward reads).
_SHARED = re.compile(r"^(rw_state_|rw_map_|rw_defs_|rw_engine_get$|rw_engine_members$|rw_engine_types$|rw_engine_call$|rw_anchor_list$|rw_game_status$|rw_game_log_tail$|rw_bridge_methods$|rw_steward_status$|rw_steward_explain$|rw_steward_stock_list$|rw_steward_orders$|search_|read_|find_source_files$|watch_|notebook_read$|skill_read$|skill_list$|journal_read$|score_history$|tool_list$|watcher_list$|run_python$|look$|rpc$|end_turn$|reply_to_operator$)")

ROLES: dict[str, dict] = {
    "econ": {
        "title": "Economy",
        "brief": "You run the economy at director altitude. The steward already sets everyone's work priorities and its stock jobs already designate trees, plants, animals and ore toward their targets (read them in the Steward block / rw_steward_status). Your levers: stock targets (rw_steward_stock_set / add / remove / run), a posture with a duration (rw_steward_posture), growing zones and crops, stockpiles and storage filters, bills (cooking, butchering, crafting), schedules. Keep food days above 6, wood and steel stocked. Do not set per-pawn priorities (the Caretaker owns per-pawn overrides through rw_steward_pawn + rw_ui_set_work) and do not hand-designate what a stock job covers; raise its target instead. Do not build structures, draft pawns, answer letters or edit skills; other streams do that.",
        "allow": re.compile(r"^(rw_ui_set_schedule|rw_ui_zone|rw_ui_storage|rw_ui_add_bill|rw_ui_bill|rw_ui_designate|rw_ui_area|rw_steward_stock_set|rw_steward_stock_add|rw_steward_stock_remove|rw_steward_stock_run|rw_steward_posture|rw_steward_settings|notebook_append)$"),
    },
    "build": {
        "title": "Builder",
        "brief": "You are the architect: rooms, walls, doors, roofs, furniture placement, base extensions, power grids (generators, batteries, conduits with rw_ui_wire), deconstruction. Use rw_map_detail and anchors; name every site with rw_anchor_set; verify with dry runs. The steward's forestry/mining jobs supply wood and steel: if a build is short on materials tell the notebook, do not cut or mine by hand. Do not change work priorities, draft pawns, answer letters or edit skills.",
        "allow": re.compile(r"^(rw_ui_build|rw_ui_build_many|rw_ui_wire|rw_anchor_set|rw_anchor_delete|rw_ui_designate|rw_ui_zone|rw_ui_area|notebook_append)$"),
    },
    "guard": {
        "title": "Guardian",
        "brief": "You keep people alive: threats and defense (draft, position, attack, retreat), fires, medical care and rescue, mood and mental-break prevention, hostility/medical policies, animals and prisoners, game speed during danger (rw_game_speed). The standing orders (mod-side reflexes: combat, rescue, unforbid, corpses, beds, policies, blueprints, fire) already draft everyone to the rally point when hostiles appear, release them after, rescue the downed and unforbid drops: you own them (rw_steward_orders to read, rw_steward_orders_set to toggle, rw_steward_orders_rally to set/clear the rally rect, rw_steward_orders_explain, rw_steward_orders_run). Set a rally point (inside the walls, one door, cover, near the hospital) as soon as the first walls stand; the Steward block reminds you while none exists. Draft or move a pawn by hand only to override (breachers, sappers, drop pods inside, a mech cluster): that pawn is then hands-off for the combat order for about an hour. During a raid, siege, fire or toxic fallout set a short posture with rw_steward_posture (e.g. label 'raid', hours 6, work Firefighter/Doctor up, targets hunting x0) instead of touching priorities; the steward applies it to every managed pawn. Do not build, set work priorities, answer letters or edit skills.",
        "allow": re.compile(r"^(rw_ui_draft|rw_ui_goto|rw_ui_attack|rw_ui_job|rw_ui_cancel_job|rw_ui_set_policies|rw_ui_order|rw_ui_orders_at|rw_ui_press|rw_ui_gizmos|rw_ui_animal|rw_ui_prisoner|rw_game_speed|rw_game_pause|rw_steward_posture|rw_steward_orders_\w+|notebook_append)$"),
    },
    "caretaker": {
        "title": "Caretaker",
        "brief": "You are the caretaker: letters, quests and dialogs (answer every open one), research choice (rw_steward_research queue, or rw_ui_set_research for one project now), trade, recruiting and prisoners, the operator's messages (reply first), and the colony's memory: keep the notebook current, edit skills, write tools and watchers, journal durable lessons, and decide the wake plan for the whole colony. The steward sets work priorities; when a pawn's priorities look wrong read rw_steward_explain first, and only then hand it over with rw_steward_pawn managed=false, set that one pawn's priorities with rw_ui_set_work, and hand it back with managed=true when the reason is gone. The beds, policies and rescue standing orders (bed ownership, hospital beds, food-policy switch when meals run low, medical care levels, rescue and tending) run inside the mod: read them with rw_steward_orders / rw_steward_orders_explain and switch one off with rw_steward_orders_set only when it fights a decision you made on purpose (a bed or policy you set by hand is already left alone for a while). You may end the episode if it is truly lost. Do not build or draft; set a pawn's priorities only after rw_steward_pawn managed=false.",
        "allow": re.compile(r"^(rw_ui_letter|rw_ui_dialog|rw_ui_set_research|rw_steward_research|rw_ui_set_work|rw_ui_set_work_many|rw_ui_prisoner|rw_game_save|rw_game_speed|rw_steward_pawn|rw_steward_explain|rw_steward_orders_set|rw_steward_orders_explain|notebook_|journal_|skill_|tool_|watcher_|brain_|end_episode$)"),
    },
}

# The stream that handles letters, dialogs and the operator (the runner routes those to it).
CARETAKER = "caretaker"


def allow_for(role: str) -> Callable[[Tool], bool]:
    own = ROLES[role]["allow"]

    def ok(t: Tool) -> bool:
        if t.source == "brain":
            return True  # the agent's own tools are shared
        return bool(_SHARED.match(t.name) or own.match(t.name))

    return ok


def brief_for(role: str) -> str:
    r = ROLES[role]
    others = ", ".join(v["title"] for k, v in ROLES.items() if k != role)
    return f"## Your role this step: {r['title']} (parallel mode)\n{r['brief']}\nThree other streams ({others}) are acting on the same colony right now; stay in your lane, read state before precise actions, and keep your notes short. End with end_turn."


# ---------------------------------------------------------------- watchdog (not a play stream)

# The watchdog is a role in the same sense — a named scope over the tool registry — but it is deliberately NOT in
# ROLES: ROLES is what `play_step_parallel` fans a calm step out to, and the watchdog never plays the colony.
# Its allowlist is exact, not a prefix match, and it ignores `Tool.source`: brain-authored tools are shared with every
# play role, but they are written by the model at runtime and carry a live bridge handle, so the watchdog does not get
# them. `run_python` and `rpc` are excluded for the same reason — an unsandboxed exec would make the path guards in
# rimagent.watchdog decorative. What is left: its own scoped repo tools, and the read-only RimWorld knowledge tools,
# which are genuinely useful for deciding whether a C# call was used correctly.
WATCHDOG = "watchdog"
_WATCHDOG_TOOLS = frozenset({
    "repo_read", "repo_list", "repo_grep", "repo_patch", "repo_revert",
    "watchdog_verify_python", "watchdog_verify_mod", "watchdog_verify_mod_steward", "watchdog_commit", "end_watchdog",
    "search_source", "find_source_files", "read_source", "search_wiki", "read_wiki",
})

WATCHDOG_BRIEF = (
    "## Your role: Watchdog\n"
    "You are not playing the colony this pass. You are the agent's own code reviewer: read the tool-call errors below, "
    "separate model noise from real defects, and fix the defects in the project's source with a verified, committed patch. "
    "You cannot restart the game or deploy anything; a human does that later."
)


def allow_watchdog(t: Tool) -> bool:
    return t.name in _WATCHDOG_TOOLS
