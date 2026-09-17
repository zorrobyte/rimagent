"""Steward on the Python side: packet block, roles allow-lists, config defaults, bridge doc notes."""
from __future__ import annotations

from pathlib import Path

from rimagent import config as config_mod
from rimagent import loop, roles
from rimagent.bridge import BridgeError
from rimagent.context import Context
from rimagent.registry import BRIDGE_DOC_NOTES, Registry, Tool

SAMPLE_STATUS = {
    "enabled": {"scorer": True, "stock": True},
    "posture": {"label": "winter prep", "expires_in_hours": 36.0, "work": {"Growing": 0.5, "Construction": -0.2}, "weights": {}, "targets": {"forestry": 1.5}},
    "pawns": [
        {"id": "Thing_Human_1", "name": "Jen", "managed": True, "priorities": {"Growing": 1, "Cooking": 2}, "top": [{"work": "Growing", "priority": 1, "why": "passion; crops ready"}]},
        {"id": "Thing_Human_2", "name": "Bob", "managed": False, "priorities": {"Construction": 1, "Mining": 2, "Hauling": 3, "Cleaning": 4, "Research": 4}, "top": []},
    ],
    "stock": [
        {"id": "j1", "kind": "forestry", "label": "wood", "target": 500, "current": 420, "enabled": True, "suspended": False, "managed": True, "last_run_hours_ago": 2.0, "designations": 6, "failures": 0, "summary": None, "notes": []},
        {"id": "j2", "kind": "hunting", "label": "meat", "target": 200, "current": 40, "enabled": True, "suspended": False, "managed": True, "last_run_hours_ago": 5.0, "designations": 0, "failures": 3, "summary": "no safe hunting targets", "notes": []},
        {"id": "j3", "kind": "mining", "label": "steel", "target": 300, "current": 120, "enabled": True, "suspended": True, "managed": True, "last_run_hours_ago": None, "designations": 0, "failures": 0, "summary": None, "notes": []},
    ],
    "problems": ["hunting: no safe hunting targets for a day", "Bob is unmanaged with Research at 4"],
}


class FakeBridge:
    def __init__(self, status=SAMPLE_STATUS, fail=False):
        self._status, self._fail = status, fail
        self.calls: list[str] = []

    def call(self, method, **params):
        self.calls.append(method)
        if method == "steward.status":
            if self._fail:
                raise BridgeError("unknown method steward.status")
            return self._status
        if method in ("state.summary", "state.base", "state.dialogs"):
            return {}
        if method == "state.letters":
            return []
        raise BridgeError(f"unknown method {method}")


def _ctx(bridge) -> Context:
    return Context(bridge=bridge, llm=None, registry=Registry(), config={"play": {}}, emit=lambda k, d: None)  # type: ignore[arg-type]


# ---------------------------------------------------------------- packet block

def test_steward_text_renders_posture_stock_problems_and_unmanaged():
    text = loop.steward_text(SAMPLE_STATUS)
    lines = text.splitlines()
    assert lines[0].startswith("posture: winter prep (expires in 36h")
    assert "Growing +0.5" in lines[0] and "forestry x1.5" in lines[0]
    assert "- wood 420/500 forestry ok, 6 designated (last run 2h ago)" in lines
    assert "- ✗ meat 40/200 hunting STALLED (3 failed runs): no safe hunting targets (last run 5h ago)" in lines
    assert "- steel 120/300 mining suspended (last run never)" in lines
    # problems first among stock rows
    stock_rows = [l for l in lines if l.startswith("- ") and ("forestry" in l or "hunting" in l or "mining" in l)]
    assert stock_rows[0].startswith("- ✗")
    assert "- hunting: no safe hunting targets for a day" in lines
    assert any(l.startswith("- Bob: Construction 1, Mining 2, Hauling 3") for l in lines)
    assert not any("Jen" in l for l in lines)  # managed pawns are not listed
    assert lines[-1].startswith("Director tools: rw_steward_stock_set")
    assert len(lines) <= 25


def test_steward_text_below_target_is_flagged_and_quiet_when_fine():
    st = {"enabled": {"scorer": True, "stock": True}, "posture": None, "pawns": [], "stock": [{"kind": "forestry", "label": "wood", "target": 500, "current": 120, "enabled": True, "failures": 0, "summary": "cut 4 trees", "last_run_hours_ago": 0.5}], "problems": []}
    text = loop.steward_text(st)
    assert "posture: none" in text
    assert "- ✗ wood 120/500 forestry below target, nothing designated: cut 4 trees (last run 30m ago)" in text
    pending = loop.steward_stock_line({"kind": "forestry", "label": "wood", "target": 500, "current": 120, "enabled": True})
    assert pending == "wood 120/500 forestry pending first run (last run never)"
    assert "Director tools" in text
    ok = dict(st, stock=[dict(st["stock"][0], current=600)])
    text2 = loop.steward_text(ok)
    assert "- wood 600/500 forestry ok (last run 30m ago)" in text2
    assert "Director tools" not in text2


def test_steward_text_truncates_and_reports_off():
    st = {"enabled": {"scorer": False, "stock": False}, "posture": None, "pawns": [{"name": f"P{i}", "managed": False, "priorities": {}} for i in range(10)],
          "stock": [{"kind": "forestry", "label": f"w{i}", "target": 10, "current": 0, "enabled": True} for i in range(15)], "problems": [f"p{i}" for i in range(9)]}
    text = loop.steward_text(st)
    assert text.startswith("steward OFF")
    assert "… 5 more jobs" in text and "… 4 more" in text
    assert len(text.splitlines()) <= 32


def test_steward_block_unavailable_when_rpc_missing():
    ctx = _ctx(FakeBridge(fail=True))
    block = loop.steward_block(ctx)
    assert block.splitlines()[0].startswith("## Steward")
    assert block.splitlines()[1] == loop.STEWARD_UNAVAILABLE == "steward: unavailable"
    assert loop.steward_text(None) == "steward: unavailable"
    assert loop.steward_text("nope") == "steward: unavailable"


def test_situation_packet_contains_steward_block_and_survives_failure():
    msg, hint = loop.situation_packet(_ctx(FakeBridge()), "scheduled check-in", [], [])
    assert "## Steward" in msg
    assert "- wood 420/500 forestry ok, 6 designated (last run 2h ago)" in msg
    assert "steward" in hint
    assert msg.index("## Steward") < msg.index("Act now.")
    msg2, _ = loop.situation_packet(_ctx(FakeBridge(fail=True)), "scheduled check-in", [], [])
    assert "## Steward" in msg2 and "steward: unavailable" in msg2
    assert msg2.endswith("Act now. End with end_turn (notes + wake plan).")


def test_steward_block_sits_after_diff_and_before_raw_numbers():
    class Bridge(FakeBridge):
        def call(self, method, **params):
            if method == "state.summary":
                return {"colonists": 1, "colonist_list": [{"name": "Jen", "id": "h1"}], "day": 3, "hour": 9}
            if method == "state.base":
                return {}
            return super().call(method, **params)

    msg, _ = loop.situation_packet(_ctx(Bridge()), "scheduled check-in", [{"kind": "steward", "text": "stock_stalled hunting"}], [])
    i_tracked, i_steward, i_events, i_numbers = msg.find("## Tracked values"), msg.index("## Steward"), msg.index("## New events"), msg.index("## Colony numbers")
    assert i_steward < i_events < i_numbers
    assert i_tracked == -1 or i_tracked < i_steward


# ---------------------------------------------------------------- roles

def _tool(name: str, source: str = "bridge") -> Tool:
    return Tool(name=name, description="", fn=lambda ctx: None, schema={}, source=source)


def test_roles_caretaker_replaces_steward_and_allow_lists():
    assert "caretaker" in roles.ROLES and "steward" not in roles.ROLES
    assert roles.CARETAKER == "caretaker"
    assert list(roles.ROLES) == ["econ", "build", "guard", "caretaker"]
    econ, build, guard, care = (roles.allow_for(r) for r in ("econ", "build", "guard", "caretaker"))
    assert econ(_tool("rw_steward_stock_set")) and econ(_tool("rw_steward_stock_add")) and econ(_tool("rw_steward_posture")) and econ(_tool("rw_steward_settings"))
    assert not build(_tool("rw_steward_stock_set")) and not build(_tool("rw_steward_posture")) and not build(_tool("rw_steward_pawn"))
    assert guard(_tool("rw_steward_posture")) and not guard(_tool("rw_steward_stock_set"))
    assert care(_tool("rw_steward_pawn")) and care(_tool("rw_steward_explain")) and not care(_tool("rw_steward_stock_set"))
    # the research queue is a write (replaces the queue unless append=true): caretaker only
    assert care(_tool("rw_steward_research")) and care(_tool("rw_ui_set_research"))
    assert not econ(_tool("rw_steward_research")) and not build(_tool("rw_steward_research")) and not guard(_tool("rw_steward_research"))
    # per-pawn override (take manual -> set -> hand back) lives in one stream
    assert care(_tool("rw_ui_set_work")) and care(_tool("rw_ui_set_work_many"))
    assert not econ(_tool("rw_ui_set_work")) and not econ(_tool("rw_ui_set_work_many"))
    assert not build(_tool("rw_ui_set_work")) and not guard(_tool("rw_ui_set_work"))
    # shared reads for everyone
    for ok in (econ, build, guard, care):
        assert ok(_tool("rw_steward_status")) and ok(_tool("rw_steward_explain")) and ok(_tool("rw_steward_stock_list"))
        assert not ok(_tool("rw_steward_enable"))
    assert "steward" in roles.ROLES["econ"]["brief"].lower() and "rw_steward_stock_set" in roles.ROLES["econ"]["brief"]
    assert "rw_steward_explain" in roles.ROLES["caretaker"]["brief"] and "rw_steward_research" in roles.ROLES["caretaker"]["brief"]
    assert "rw_ui_set_work takes the pawn" not in roles.ROLES["econ"]["brief"]
    assert "Caretaker" in roles.brief_for("econ") and "Three other streams" in roles.brief_for("caretaker")


# ---------------------------------------------------------------- config

def test_config_defaults_and_repo_yaml_have_steward_section():
    d = config_mod._DEFAULTS["steward"]
    assert d == {"enabled": True, "scorer": True, "stock": True, "research_queue_default": [], "orders": {"enabled": True, "off": [], "superseded_watchers": None}}
    repo_yaml = Path(__file__).resolve().parents[2] / "config.yaml"
    cfg = config_mod.load(repo_yaml)
    st = cfg["steward"]
    assert st["enabled"] is True and st["scorer"] is True and st["stock"] is True
    assert st["research_queue_default"] == []
    assert st["orders"] == {"enabled": True, "off": []}
    assert False not in st["orders"]   # the yaml key is quoted: a bare `off` parses as boolean False
    assert "steward" in cfg["play"]["wake_on_kinds"] and "orders" in cfg["play"]["wake_on_kinds"]
    assert "steward" not in cfg["play"].get("critical_kinds", []) and "orders" not in cfg["play"].get("critical_kinds", [])
    # a missing section falls back to the defaults
    empty = config_mod.load(Path("/nonexistent/config.yaml"))
    assert empty["steward"]["enabled"] is True


# ---------------------------------------------------------------- registry doc notes

def test_bridge_doc_notes_appended_without_breaking_calls():
    reg = Registry()
    reg.add_bridge_methods([{"method": "ui.set_work", "doc": "Set a pawn's work priority."}, {"method": "ui.designate", "doc": "Designate cells."}, {"method": "state.summary", "doc": "x"}])
    sw, dz = reg.tools["rw_ui_set_work"], reg.tools["rw_ui_designate"]
    assert sw.description.startswith("[RimBridge ui.set_work] Set a pawn's work priority.")
    assert "takes the pawn out of steward management" in sw.description
    assert "prefer rw_steward_stock_set" in dz.description and "already designate" in dz.description
    assert "steward" in sw.schema["description"] and sw.schema["additionalProperties"] is True
    assert "steward" not in reg.tools["rw_state_summary"].description
    assert set(BRIDGE_DOC_NOTES) >= {"ui.set_work", "ui.designate"}
    # idempotent re-registration
    reg.add_bridge_methods([{"method": "ui.set_work", "doc": "Set a pawn's work priority."}])
    assert reg.tools["rw_ui_set_work"].description.count("steward management") == 1

    calls = []

    class B:
        def call(self, method, **params):
            calls.append((method, params)); return {"ok": 1}

    class Ctx:
        bridge = B(); extra: dict = {}

    result, ok = reg.execute(Ctx(), "rw_ui_set_work", {"pawn": "Jen", "work": "Growing", "priority": "1"})
    assert ok and calls == [("ui.set_work", {"pawn": "Jen", "work": "Growing", "priority": 1})]


# ---------------------------------------------------------------- standing orders (pass 2)

ORDERS_IDLE = [
    {"id": "combat", "label": "Combat", "enabled": True, "interval_ticks": 60, "last_run_hours_ago": 0.1, "summary": "no hostiles", "acting_on": 0},
    {"id": "rescue", "enabled": True, "summary": "", "acting_on": 0},
    {"id": "unforbid", "enabled": True, "summary": "3 items unforbidden", "acting_on": 0},
    {"id": "corpses", "enabled": False, "summary": "off", "acting_on": 0},
    {"id": "beds", "enabled": True, "acting_on": 0},
    {"id": "policies", "enabled": True, "acting_on": 0},
    {"id": "blueprints", "enabled": True, "acting_on": 0},
    {"id": "fire", "enabled": True, "acting_on": 0},
]


def _with_orders(orders, rally, **extra):
    return {**SAMPLE_STATUS, "orders": orders, "rally": rally, **extra}


def test_orders_line_idle_and_disabled_marks():
    text = loop.steward_text(_with_orders(ORDERS_IDLE, [40, 40, 6, 6]), colonists=5)
    lines = text.splitlines()
    assert "orders: combat(rally set) rescue unforbid ✗corpses beds policies blueprints fire" in lines
    # idle orders do not get a summary line, even with a summary string
    assert not any(l.startswith("- unforbid:") or l.startswith("- combat:") for l in lines)
    assert "rally: none" not in text
    assert "rw_steward_orders_set" in lines[-1]   # director tools line lists the order tools (problems present in SAMPLE_STATUS)


def test_orders_acting_summaries_only_when_acting_on_positive():
    acting = [dict(o) for o in ORDERS_IDLE]
    acting[0] = dict(acting[0], summary="drafted 4 to rally, 2 hostiles", acting_on=4)
    acting[1] = dict(acting[1], summary="rescuing Bob", acting_on="1")     # string count is tolerated
    acting[3] = dict(acting[3], summary="buried 2", acting_on=2)           # disabled: never listed even if the mod reports acting_on
    text = loop.steward_text(_with_orders(acting, [40, 40, 6, 6]), colonists=5)
    lines = text.splitlines()
    i = lines.index("orders: combat(rally set) rescue unforbid ✗corpses beds policies blueprints fire")
    assert lines[i + 1] == "- combat: drafted 4 to rally, 2 hostiles (acting on 4)"
    assert lines[i + 2] == "- rescue: rescuing Bob (acting on 1)"
    assert not any(l.startswith("- corpses") for l in lines)
    # unknown ids from a newer mod are appended, missing summary -> "active"
    more = acting + [{"id": "weird", "enabled": True, "acting_on": 3}]
    text2 = loop.steward_text(_with_orders(more, None), colonists=1)
    assert "orders: combat rescue unforbid ✗corpses beds policies blueprints fire weird" in text2
    assert "- weird: active (acting on 3)" in text2


def test_rally_reminder_present_and_absent():
    st = _with_orders(ORDERS_IDLE, None)
    assert loop.RALLY_REMINDER in loop.steward_text(st, colonists=3)
    assert loop.RALLY_REMINDER.startswith("rally: none — set one with rw_steward_orders_rally")
    assert loop.RALLY_REMINDER not in loop.steward_text(st, colonists=2)          # too few colonists
    assert loop.RALLY_REMINDER not in loop.steward_text(st)                       # unknown colonist count
    assert loop.RALLY_REMINDER not in loop.steward_text(_with_orders(ORDERS_IDLE, [1, 2, 3, 4]), colonists=8)   # rally set
    assert loop.RALLY_REMINDER not in loop.steward_text(SAMPLE_STATUS, colonists=8)                            # older mod: no orders/rally keys
    off = [dict(o, enabled=False) if o["id"] == "combat" else o for o in ORDERS_IDLE]
    assert loop.RALLY_REMINDER not in loop.steward_text(_with_orders(off, None), colonists=8)                  # combat off: nothing to rally
    assert "orders: ✗combat rescue" in loop.steward_text(_with_orders(off, None), colonists=8)
    # a malformed rect counts as none
    assert loop.RALLY_REMINDER in loop.steward_text(_with_orders(ORDERS_IDLE, [1, 2]), colonists=4)
    assert not loop.rally_is_set("40,40,6,6") and loop.rally_is_set([40, 40, 6, 6]) and loop.rally_is_set((1, 2, 3, 4))


def test_orders_omitted_when_mod_lacks_them_and_packet_wires_colonists():
    text = loop.steward_text(SAMPLE_STATUS, colonists=5)
    assert "orders:" not in text and "rally" not in text
    assert loop.steward_orders_lines({"orders": [], "rally": None}, 9) == []
    assert loop.steward_orders_lines({"orders": "nope", "rally": None}, 9) == []
    assert loop.steward_orders_lines({"orders": [{"enabled": True}, "x"], "rally": None}, 9) == []   # rows without ids are skipped

    class Bridge(FakeBridge):
        def call(self, method, **params):
            if method == "state.summary":
                return {"colonists": 4, "colonist_list": [{"name": "Jen"}] * 4, "day": 3, "hour": 9}
            if method == "state.base":
                return {}
            return super().call(method, **params)

    msg, _ = loop.situation_packet(_ctx(Bridge(_with_orders(ORDERS_IDLE, None))), "scheduled check-in", [], [])
    assert "## Steward" in msg and "standing orders" in msg.split("\n")[msg.split("\n").index(next(l for l in msg.splitlines() if l.startswith("## Steward")))]
    assert "orders: combat rescue unforbid ✗corpses beds policies blueprints fire" in msg
    assert loop.RALLY_REMINDER in msg
    msg2, _ = loop.situation_packet(_ctx(Bridge(_with_orders(ORDERS_IDLE, [40, 40, 6, 6]))), "scheduled check-in", [], [])
    assert loop.RALLY_REMINDER not in msg2 and "combat(rally set)" in msg2
    assert loop._colonist_count({"colonists": 3}) == 3 and loop._colonist_count({"colonist_list": [1, 2]}) == 2 and loop._colonist_count({}) is None


def test_roles_standing_orders_allow_lists():
    econ, build, guard, care = (roles.allow_for(r) for r in ("econ", "build", "guard", "caretaker"))
    for ok in (econ, build, guard, care):
        assert ok(_tool("rw_steward_orders"))            # read for everyone
    for name in ("rw_steward_orders_set", "rw_steward_orders_rally", "rw_steward_orders_explain", "rw_steward_orders_run"):
        assert guard(_tool(name)), name
        assert not econ(_tool(name)) and not build(_tool(name)), name
    assert care(_tool("rw_steward_orders_set")) and care(_tool("rw_steward_orders_explain"))
    assert not care(_tool("rw_steward_orders_rally")) and not care(_tool("rw_steward_orders_run"))
    assert "rw_steward_orders_rally" in roles.ROLES["guard"]["brief"] and "rally" in roles.ROLES["guard"]["brief"].lower()
    assert "rw_steward_orders_set" in roles.ROLES["caretaker"]["brief"]
    assert "orders" not in roles.ROLES["econ"]["allow"].pattern and "orders" not in roles.ROLES["build"]["allow"].pattern


def test_system_prompt_has_standing_orders_paragraph():
    text = (Path(__file__).resolve().parents[1] / "rimagent" / "prompts" / "system.md").read_text()
    para = next(l for l in text.splitlines() if l.startswith("- Standing orders:"))
    for needle in ("rw_steward_orders_rally", "rw_steward_orders_set", "combat", "rescue", "unforbid", "corpses", "beds", "policies", "blueprints", "fire", "first walls", "Manual actions pause the matching order"):
        assert needle in para, needle
    assert "standing orders" in text.split("Altitude ladder", 1)[1].split("\n", 1)[0]
    assert "combat standing order" in BRIDGE_DOC_NOTES["ui.draft"] and "ui.goto" in BRIDGE_DOC_NOTES and "ui.attack" in BRIDGE_DOC_NOTES


# ---------------------------------------------------------------- runner: config applied at new_game / recover, dashboard toggles

class _Bus:
    def __init__(self):
        self.events: list[tuple[str, dict]] = []

    def emit(self, kind, data):
        self.events.append((kind, data))


class _OrdersBridge:
    def __init__(self, fail=False):
        self.calls: list[tuple[str, dict]] = []
        self.fail = fail

    def call(self, method, **params):
        self.calls.append((method, params))
        if self.fail and method.startswith("steward.orders"):
            raise BridgeError(f"unknown method {method}")
        if method == "steward.enable":
            return params
        if method == "steward.orders.set":
            return {"id": params["id"], "enabled": params["enabled"], "summary": "ok"}
        if method == "steward.orders.rally":
            return None if params.get("clear") else {"rect": params["rect"]}
        return {}


def _runner(orders_cfg=None, bridge=None):
    from rimagent import runner as runner_mod
    r = runner_mod.Runner.__new__(runner_mod.Runner)
    r.cfg = {"play": {}, "steward": {"enabled": True, "scorer": True, "stock": True, "orders": orders_cfg if orders_cfg is not None else {"enabled": True, "off": []}}}
    r.bus = _Bus(); r.bridge = bridge or _OrdersBridge()
    r.steward = True; r.force_think = None
    ocfg = r.cfg["steward"]["orders"]
    r.orders_enabled = bool(ocfg.get("enabled", True)); r.orders_off = {str(x) for x in (ocfg.get("off") or [])}
    from rimagent.watchers import superseded_mapping
    r.registry = Registry(); r.superseded_watchers = superseded_mapping(ocfg.get("superseded_watchers")); r.orders_supported = None
    return r


def test_runner_applies_orders_config_after_steward_enable():
    r = _runner({"enabled": True, "off": ["corpses", "policies"]})
    r.apply_steward(True)
    sets = [(p["id"], p["enabled"]) for m, p in r.bridge.calls if m == "steward.orders.set"]
    assert sets == [("all", True), ("corpses", False), ("policies", False)]
    assert r.bridge.calls[0][0] == "steward.enable"
    assert any(k == "log" and d["text"] == "standing orders: on except corpses, policies" for k, d in r.bus.events)
    # the watchers those orders replaced are skipped, except the ones whose order is off (rotting_corpses, food_policy_watcher cover again)
    assert r.orders_supported is True
    assert r.registry.watcher_superseded == {"hostile_draft": "combat", "undraft_after_fight": "combat", "fire_alert": "fire", "rescue_downed": "rescue",
                                             "unforbid_drops_watcher": "unforbid", "build_stall": "blueprints"}
    assert any(k == "log" and d["text"].startswith("superseded watchers") and "hostile_draft->combat" in d["text"] for k, d in r.bus.events)
    # steward off -> all orders off; orders.enabled false -> all off too; every watcher runs again
    r.bridge.calls.clear(); r.apply_steward(False)
    assert [(p["id"], p["enabled"]) for m, p in r.bridge.calls if m == "steward.orders.set"] == [("all", False)]
    assert r.registry.watcher_superseded == {}
    r2 = _runner({"enabled": False, "off": []}); r2.apply_orders()
    assert [(p["id"], p["enabled"]) for m, p in r2.bridge.calls] == [("all", False)] and r2.registry.watcher_superseded == {}
    # config override of the mapping
    r4 = _runner({"enabled": True, "off": [], "superseded_watchers": {"my_drafter": "combat"}}); r4.apply_orders()
    assert r4.registry.watcher_superseded == {"my_drafter": "combat"}
    r5 = _runner({"enabled": True, "off": [], "superseded_watchers": {}}); r5.apply_orders()
    assert r5.registry.watcher_superseded == {}
    # older mod without the RPC: logged, not raised, and no watcher is superseded (they still cover)
    r3 = _runner(bridge=_OrdersBridge(fail=True))
    assert r3.apply_orders() is None and any(k == "error" and "steward.orders.set failed" in d["text"] for k, d in r3.bus.events)
    assert r3.orders_supported is False and r3.registry.watcher_superseded == {}
    # the orders ledger kind is a wake kind, never an interrupt
    assert "critical_kinds.discard(\"orders\")" in (Path(__file__).resolve().parents[1] / "rimagent" / "runner.py").read_text()


def test_runner_order_toggle_and_rally():
    from rimagent.runner import Controls
    r = _runner(); c = Controls.__new__(Controls); c.r = r; c.paused = False
    row = c.set_order("corpses", False)
    assert row == {"id": "corpses", "enabled": False, "summary": "ok"} and c.orders_off == ["corpses"]
    assert r.force_think.startswith("standing order corpses switched off")
    assert r.bridge.calls[-1] == ("steward.orders.set", {"id": "corpses", "enabled": False})
    assert r.orders_supported is True and "rotting_corpses" not in r.registry.watcher_superseded and r.registry.watcher_superseded["hostile_draft"] == "combat"
    c.set_order("corpses", True); assert c.orders_off == []
    assert r.registry.watcher_superseded["rotting_corpses"] == "corpses"
    c.set_order("all", False); assert set(c.orders_off) == set(loop.ORDER_IDS)
    assert r.registry.watcher_superseded == {}
    c.set_order("all", True); assert c.orders_off == []
    assert len(r.registry.watcher_superseded) == 8
    assert c.set_rally([40, 40, 6, 6]) == {"rect": [40, 40, 6, 6]} and r.bridge.calls[-1] == ("steward.orders.rally", {"rect": [40, 40, 6, 6]})
    assert c.set_rally(None) is None and r.bridge.calls[-1] == ("steward.orders.rally", {"clear": True})
    assert c.set_rally([1, 2, 0, 5]) is None and any(k == "error" and "rally" in d["text"] for k, d in r.bus.events)
    # the remembered off-set is what the next new_game/recover applies
    c.set_order("beds", False); r.bridge.calls.clear(); r.apply_orders()
    assert [(p["id"], p["enabled"]) for m, p in r.bridge.calls] == [("all", True), ("beds", False)]


# ---------------------------------------------------------------- orders that acted since the last step / persistent state

def test_orders_acted_since_last_step_without_acting_on():
    rows = [dict(o) for o in ORDERS_IDLE]
    # unforbid acted 2h ago (last pass idle); the previous step ended 6h ago -> printed with the last acted summary
    rows[2] = {"id": "unforbid", "enabled": True, "summary": "nothing forbidden near the base", "acting_on": 0,
               "last_acted_hours_ago": 2.0, "last_acted_summary": "unforbade 14 (crash pod loot)"}
    # beds: accumulated count since the last steward.status read
    rows[4] = {"id": "beds", "enabled": True, "summary": "all colonists own a bed", "acting_on": 0, "acted_since_read": 3}
    # combat idle (last acted long before the step), corpses acted but disabled: neither printed
    rows[0] = dict(rows[0], last_acted_hours_ago=30.0, last_acted_summary="drafted 4")
    rows[3] = dict(rows[3], last_acted_hours_ago=0.5, last_acted_summary="buried 2")
    text = loop.steward_text(_with_orders(rows, [1, 2, 3, 4]), colonists=5, since_hours=6.0)
    assert "- unforbid: unforbade 14 (crash pod loot) (acted 2h ago)" in text
    assert "- beds: all colonists own a bed (acted on 3 since your last step)" in text
    assert "- combat:" not in text and "- corpses:" not in text
    # an older step boundary: unforbid's action predates it -> silent; acting_on still wins regardless of since_hours
    text2 = loop.steward_text(_with_orders(rows, [1, 2, 3, 4]), colonists=5, since_hours=1.0)
    assert "- unforbid:" not in text2 and "- beds:" in text2
    rows[2] = dict(rows[2], acting_on=2)
    text3 = loop.steward_text(_with_orders(rows, [1, 2, 3, 4]), colonists=5, since_hours=None)
    assert "- unforbid: nothing forbidden near the base (acting on 2)" in text3
    # unknown since_hours (first step) never hides an accumulated action, but does hide hours-based ones
    assert "- beds:" in text3
    rows[2] = dict(rows[2], acting_on=0)
    assert "- unforbid:" not in loop.steward_text(_with_orders(rows, [1, 2, 3, 4]), colonists=5)


def test_orders_persistent_state_line_always_printed():
    rows = [dict(o) for o in ORDERS_IDLE]
    rows[5] = {"id": "policies", "enabled": True, "summary": "kept 3 on steward-raw", "acting_on": 0, "state": "food switch ACTIVE, 3 pawns on steward-raw"}
    rows[0] = {"id": "combat", "enabled": True, "summary": "holding 4 at rally", "acting_on": 4, "state": "engaged: 2 hostiles"}
    rows[3] = {"id": "corpses", "enabled": False, "summary": "off", "acting_on": 0, "state": "should not show"}
    lines = loop.steward_text(_with_orders(rows, [1, 2, 3, 4]), colonists=5, since_hours=3.0).splitlines()
    assert "- policies: food switch ACTIVE, 3 pawns on steward-raw" in lines
    assert "- combat: holding 4 at rally (acting on 4); engaged: 2 hostiles" in lines
    assert not any(l.startswith("- corpses") for l in lines)
    # state identical to the summary is not repeated
    rows[0] = dict(rows[0], state="holding 4 at rally")
    assert "- combat: holding 4 at rally (acting on 4)" in loop.steward_text(_with_orders(rows, [1, 2, 3, 4]), colonists=5).splitlines()


def test_steward_block_uses_runner_ticks_for_since_hours():
    rows = [dict(o) for o in ORDERS_IDLE]
    rows[2] = {"id": "unforbid", "enabled": True, "summary": "idle", "acting_on": 0, "last_acted_hours_ago": 2.0, "last_acted_summary": "unforbade 14"}
    ctx = _ctx(FakeBridge(_with_orders(rows, [1, 2, 3, 4])))
    assert loop.hours_since_last_step(ctx) is None                     # no ticks known: hours-based rows stay silent
    assert "- unforbid:" not in loop.steward_block(ctx, 4)
    ctx.extra.update({"tick": 30000, "last_step_end_tick": 30000 - 4 * 2500})
    assert loop.hours_since_last_step(ctx) == 4.0
    assert "- unforbid: unforbade 14 (acted 2h ago)" in loop.steward_block(ctx, 4)
    ctx.extra.update({"tick": 30000, "last_step_end_tick": 30000 - 2500})
    assert "- unforbid:" not in loop.steward_block(ctx, 4)
    ctx.extra.update({"tick": 100, "last_step_end_tick": 500})        # a recovered game with an older tick: unknown, not negative
    assert loop.hours_since_last_step(ctx) is None


# ---------------------------------------------------------------- prose matches the mod's cooldown contract

def _repo(*parts: str) -> str:
    return (Path(__file__).resolve().parents[2].joinpath(*parts)).read_text(encoding="utf-8")


def _repo_if_exists(*parts: str) -> str | None:
    """Like _repo, but None for a brain/ file that no longer exists — brain/ is agent-authored and evolves
    across real episodes (skill_write/skill_delete), so a specific seeded filename is not guaranteed to persist."""
    p = Path(__file__).resolve().parents[2].joinpath(*parts)
    return p.read_text(encoding="utf-8") if p.exists() else None


def test_skill_prose_matches_order_contracts():
    med = _repo("brain", "skills", "medicine-and-health.md")
    assert "resets to that after 2 days if you set a pawn's care by hand" in med   # Order_Policies.RunMedical: OwnedValues.TwoDays
    assert "never touches a pawn again once you set its care by hand" not in med
    assert "waits until the combat order releases" not in med                     # Order_Rescue has no combat gate
    assert "keeps running every 300 ticks during a raid" in med
    manual = _repo("brain", "skills", "bridge-manual.md")
    assert "heater/cooler targets changed by hand or outside the order: never touched again" in manual   # Order_Policies.RunTemperature: OwnedValues.Forever
    system = _repo("agent", "rimagent", "prompts", "system.md")
    assert "heater/cooler targets: until you change it back, the order never resets it" in system
    for name in ("animals-and-hunting.md", "example-base-compound.md", "early-game-food.md"):
        body = _repo_if_exists("brain", "skills", name)
        if body is None:
            continue
        assert 'recipe="ButcherCorpse"' not in body and "standing `ButcherCorpse` bill" not in body   # the RecipeDef is ButcherCorpseFlesh
    doctrine = _repo("brain", "skills", "core-doctrine.md")
    assert "`watcher_list`; delete any watcher that drafts, rescues, unforbids, buries, assigns beds or flips food policy" in doctrine
