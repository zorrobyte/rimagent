"""Watchdog stream: path allowlist, verify-before-commit enforcement, error collection and the cadence gate.

These are the guardrails that must hold whatever the model does, so they are tested against the module functions
the tools call, not against a prompt. RIMAGENT_ROOT points at the throwaway root (conftest), so `repo_*` operates
on a fake mod/Source + agent/rimagent tree there and can never touch the real repo.
"""
from __future__ import annotations

import os
import time
from types import SimpleNamespace

import pytest

from rimagent import watchdog as wd
from rimagent.bus import Bus
from rimagent.tools import watchdog as wt


@pytest.fixture
def repo(tmp_root):
    """A miniature repo under the temp root: mod/Source/Ui/UiRpc.cs and agent/rimagent/loop.py."""
    (tmp_root / "mod" / "Source" / "Ui").mkdir(parents=True, exist_ok=True)
    (tmp_root / "agent" / "rimagent").mkdir(parents=True, exist_ok=True)
    (tmp_root / "brain" / "memory").mkdir(parents=True, exist_ok=True)
    cs = tmp_root / "mod" / "Source" / "Ui" / "UiRpc.cs"
    py = tmp_root / "agent" / "rimagent" / "loop.py"
    cs.write_text("// original C#\n", encoding="utf-8")
    py.write_text("# original python\n", encoding="utf-8")
    yield tmp_root
    for f in (cs, py):
        if f.exists():
            f.unlink()


class Ctx(SimpleNamespace):
    def __init__(self):
        super().__init__(extra={}, episode=1, stop_turn=False, wake=SimpleNamespace(notes=""), events=[])

    def emit(self, kind, data=None):
        self.events.append((kind, data or {}))


# ---------------------------------------------------------------- path allowlist

@pytest.mark.parametrize("good", [
    "mod/Source/Ui/UiRpc.cs",
    "mod/Source",
    "agent/rimagent/loop.py",
    "./agent/rimagent/loop.py",
    "agent/rimagent",
])
def test_allowlist_accepts_the_two_roots(repo, good):
    assert wd.safe_path(good).startswith(("mod/Source", "agent/rimagent"))


@pytest.mark.parametrize("bad", [
    "",
    "   ",
    "..",
    "../../etc/passwd",
    "mod/Source/../../brain/memory/notebook.md",
    "agent/rimagent/../../config.local.yaml",
    "brain/skills/core-doctrine.md",
    "brain/memory/notebook.md",
    "config.yaml",
    "config.local.yaml",
    "CLAUDE.md",
    "knowledge/wiki/x.md",
    "mod/1.6/Assemblies/RimBridge.dll",
    "mod/Tests/Foo.cs",
    "script/build.sh",
    ".git/config",
    "mod/Source/.git/config",
    "agent/rimagent/__pycache__/loop.pyc",
    "mod/Source/obj/x.dll",
    "/etc/passwd",
    "/tmp/elsewhere.txt",
])
def test_allowlist_rejects_everything_else(repo, bad):
    with pytest.raises(wd.WatchdogError):
        wd.safe_path(bad)


def test_allowlist_rejects_absolute_path_inside_repo_but_out_of_scope(repo):
    with pytest.raises(wd.WatchdogError):
        wd.safe_path(str(repo / "brain" / "memory" / "notebook.md"))


def test_allowlist_accepts_absolute_path_inside_scope(repo):
    assert wd.safe_path(str(repo / "mod" / "Source" / "Ui" / "UiRpc.cs")) == "mod/Source/Ui/UiRpc.cs"


def test_allowlist_rejects_symlink_escape(repo):
    outside = repo / "brain" / "memory" / "notebook.md"
    outside.parent.mkdir(parents=True, exist_ok=True)
    outside.write_text("secret", encoding="utf-8")
    link = repo / "mod" / "Source" / "escape.cs"
    if link.exists() or link.is_symlink():
        link.unlink()
    os.symlink(outside, link)
    try:
        with pytest.raises(wd.WatchdogError):
            wd.safe_path("mod/Source/escape.cs")
    finally:
        link.unlink()


def test_root_key_maps_each_root_to_its_verify_tool():
    assert wd.root_key("mod/Source/Ui/UiRpc.cs") == "mod"
    assert wd.root_key("agent/rimagent/loop.py") == "agent"
    assert wd.VERIFY_TOOL["mod"] == "watchdog_verify_mod"
    assert wd.VERIFY_TOOL["agent"] == "watchdog_verify_python"


# ---------------------------------------------------------------- read/patch tools

def test_repo_read_and_patch_round_trip(repo):
    ctx = Ctx()
    out = wt.repo_read(ctx, "mod/Source/Ui/UiRpc.cs")
    assert "original C#" in out["text"]
    res = wt.repo_patch(ctx, "mod/Source/Ui/UiRpc.cs", "// patched\n")
    assert res["changed"] is True
    assert (repo / "mod" / "Source" / "Ui" / "UiRpc.cs").read_text() == "// patched\n"
    st = wd.state_for(ctx)
    assert st.touched == {"mod/Source/Ui/UiRpc.cs": "patched"}
    assert st.unverified == {"mod"}


def test_repo_patch_refuses_out_of_scope_and_empty(repo):
    ctx = Ctx()
    with pytest.raises(wd.WatchdogError):
        wt.repo_patch(ctx, "brain/memory/notebook.md", "x")
    with pytest.raises(wd.WatchdogError):
        wt.repo_patch(ctx, "mod/Source/Ui/UiRpc.cs", "   ")
    assert wd.state_for(ctx).touched == {}


def test_repo_read_refuses_missing_file(repo):
    with pytest.raises(wd.WatchdogError):
        wt.repo_read(Ctx(), "mod/Source/Nope.cs")


# ---------------------------------------------------------------- commit gating

def _patch(ctx, path="mod/Source/Ui/UiRpc.cs", body="// patched\n"):
    return wt.repo_patch(ctx, path, body)


def test_commit_refuses_without_any_patch(repo):
    with pytest.raises(wd.WatchdogError, match="nothing to commit"):
        wt.watchdog_commit(Ctx(), "noop")


def test_commit_refuses_without_a_prior_verify(repo, monkeypatch):
    ctx = Ctx()
    _patch(ctx)
    monkeypatch.setattr(wd, "git_commit", lambda paths, msg: pytest.fail("committed without verifying"))
    with pytest.raises(wd.WatchdogError, match="watchdog_verify_mod"):
        wt.watchdog_commit(ctx, "fix")


def test_commit_refuses_when_the_verify_failed(repo, monkeypatch):
    ctx = Ctx()
    _patch(ctx)
    monkeypatch.setattr(wd, "verify_mod", lambda: (False, "1 test failed"))
    monkeypatch.setattr(wd, "git_commit", lambda paths, msg: pytest.fail("committed a failing patch"))
    assert wt.watchdog_verify_mod(ctx)["ok"] is False
    with pytest.raises(wd.WatchdogError, match="failed"):
        wt.watchdog_commit(ctx, "fix")


def test_commit_refuses_when_the_wrong_root_was_verified(repo, monkeypatch):
    """A green python run does not license a C# commit."""
    ctx = Ctx()
    _patch(ctx, "mod/Source/Ui/UiRpc.cs")
    monkeypatch.setattr(wd, "verify_python", lambda: (True, "87 passed"))
    monkeypatch.setattr(wd, "git_commit", lambda paths, msg: pytest.fail("committed on the wrong suite"))
    wt.watchdog_verify_python(ctx)
    with pytest.raises(wd.WatchdogError, match="watchdog_verify_mod"):
        wt.watchdog_commit(ctx, "fix")


def test_commit_refuses_when_patched_again_after_verifying(repo, monkeypatch):
    ctx = Ctx()
    _patch(ctx)
    monkeypatch.setattr(wd, "verify_mod", lambda: (True, "104 passed"))
    wt.watchdog_verify_mod(ctx)
    _patch(ctx, body="// patched again\n")           # invalidates the proof
    monkeypatch.setattr(wd, "git_commit", lambda paths, msg: pytest.fail("committed a re-patched file"))
    with pytest.raises(wd.WatchdogError):
        wt.watchdog_commit(ctx, "fix")


def test_commit_stages_exactly_the_touched_paths_after_a_green_verify(repo, monkeypatch):
    ctx = Ctx()
    _patch(ctx, "mod/Source/Ui/UiRpc.cs")
    _patch(ctx, "agent/rimagent/loop.py", "# patched\n")
    monkeypatch.setattr(wd, "verify_mod", lambda: (True, "104 passed"))
    monkeypatch.setattr(wd, "verify_python", lambda: (True, "87 passed"))
    wt.watchdog_verify_mod(ctx)
    wt.watchdog_verify_python(ctx)
    seen: dict = {}
    monkeypatch.setattr(wd, "git_commit", lambda paths, msg: seen.update(paths=paths, msg=msg) or "abc1234")
    res = wt.watchdog_commit(ctx, "fix two things")
    assert res["committed"] == "abc1234"
    assert res["pushed"] is False
    assert seen["paths"] == ["agent/rimagent/loop.py", "mod/Source/Ui/UiRpc.cs"]
    assert all(p.startswith(("mod/Source/", "agent/rimagent/")) for p in seen["paths"])


def test_commit_trailer_is_appended_by_the_tool_not_the_model(monkeypatch):
    sent: dict = {}
    monkeypatch.setattr(wd, "_git", lambda *a, **k: sent.setdefault("args", []).append(a) or "abc1234")
    wd.git_commit(["mod/Source/Ui/UiRpc.cs"], "fix ui.add_bill aliases")
    commit_args = [a for a in sent["args"] if a[0] == "commit"][0]
    assert wd.COMMIT_TRAILER in commit_args[commit_args.index("-m") + 1]
    add_args = [a for a in sent["args"] if a[0] == "add"][0]
    assert "-A" not in add_args and add_args[-1] == "mod/Source/Ui/UiRpc.cs"



def test_commit_is_authored_by_the_watchdog_not_the_operator(monkeypatch):
    sent: dict = {}
    monkeypatch.setattr(wd, "_git", lambda *a, **k: sent.setdefault("args", []).append(a) or "abc1234")
    wd.git_commit(["mod/Source/Ui/UiRpc.cs"], "fix ui.add_bill aliases")
    commit_args = [a for a in sent["args"] if a[0] == "commit"][0]
    assert commit_args[commit_args.index("--author") + 1] == "rimagent (watchdog) <rimagent@rimagent.invalid>"


def test_git_commit_refuses_out_of_scope_paths(monkeypatch):
    monkeypatch.setattr(wd, "_git", lambda *a, **k: pytest.fail("git ran on an out-of-scope path"))
    with pytest.raises(wd.WatchdogError):
        wd.git_commit(["brain/memory/notebook.md"], "sneaky")


# ---------------------------------------------------------------- submodule-owned paths
#
# In production mod/ is a submodule: the parent repository tracks nothing under mod/Source/, so
# `git add/commit -- mod/Source/...` run in the parent dies with a pathspec error AFTER the patch
# already verified green. These tests build real git repos (only real git exhibits the failure) under
# tmp_path and point _repo_root at them; the shared RIMAGENT_ROOT tree is never a git repo, so the
# tests above keep exercising the non-submodule fallback.

def _git_in(cwd, *args):
    import subprocess
    env = {**os.environ, "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t",
           "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t"}
    r = subprocess.run(["git", *args], cwd=str(cwd), capture_output=True, text=True, timeout=60, env=env)
    assert r.returncode == 0, f"git {' '.join(args)} failed: {r.stderr or r.stdout}"
    return r.stdout.strip()


@pytest.fixture
def submod_root(tmp_path, monkeypatch):
    """Fake project root: a git repo whose mod/ is a real submodule holding mod/Source/Foo.cs,
    plus a parent-owned agent/rimagent/loop.py."""
    parent = tmp_path / "proj"
    sub = tmp_path / "sub"
    sub.mkdir()
    _git_in(sub, "init", "-q", ".")
    (sub / "Source").mkdir()
    (sub / "Source" / "Foo.cs").write_text("// v1\n", encoding="utf-8")
    _git_in(sub, "add", "-A")
    _git_in(sub, "commit", "-qm", "init")
    parent.mkdir()
    _git_in(parent, "init", "-q", ".")
    _git_in(parent, "commit", "-q", "--allow-empty", "-m", "init")
    _git_in(parent, "-c", "protocol.file.allow=always", "submodule", "-q", "add", str(sub), "mod")
    _git_in(parent, "commit", "-qm", "add mod")
    (parent / "agent" / "rimagent").mkdir(parents=True)
    (parent / "agent" / "rimagent" / "loop.py").write_text("# v1\n", encoding="utf-8")
    _git_in(parent, "add", "-A")
    _git_in(parent, "commit", "-qm", "add agent")
    monkeypatch.setattr(wd, "_repo_root", lambda: parent.resolve())
    return parent


def test_commit_writes_a_submodule_path_inside_the_submodule(submod_root):
    (submod_root / "mod" / "Source" / "Foo.cs").write_text("// v2\n", encoding="utf-8")
    parent_head_before = _git_in(submod_root, "rev-parse", "--short", "HEAD")
    sha = wd.git_commit(["mod/Source/Foo.cs"], "fix the bridge")
    assert sha == _git_in(submod_root / "mod", "rev-parse", "--short", "HEAD")
    assert _git_in(submod_root / "mod", "show", "HEAD:Source/Foo.cs").strip() == "// v2"
    assert wd.COMMIT_TRAILER in _git_in(submod_root / "mod", "log", "-1", "--format=%B")
    # The parent repo itself gains no commit; its submodule pointer is left dirty for the human deploy step
    # (git reports a submodule with new commits as "M mod", a dirty worktree as " m mod").
    assert _git_in(submod_root, "rev-parse", "--short", "HEAD") == parent_head_before
    assert _git_in(submod_root, "status", "--porcelain").strip() == "M mod"


def test_commit_routes_each_path_to_the_repo_that_owns_it(submod_root):
    (submod_root / "mod" / "Source" / "Foo.cs").write_text("// v2\n", encoding="utf-8")
    (submod_root / "agent" / "rimagent" / "loop.py").write_text("# v2\n", encoding="utf-8")
    sha = wd.git_commit(["mod/Source/Foo.cs", "agent/rimagent/loop.py"], "fix both")
    assert sha == _git_in(submod_root, "rev-parse", "--short", "HEAD")
    assert _git_in(submod_root, "show", "HEAD:agent/rimagent/loop.py").strip() == "# v2"
    assert _git_in(submod_root / "mod", "show", "HEAD:Source/Foo.cs").strip() == "// v2"


def test_commit_still_stages_exactly_the_given_paths(submod_root):
    (submod_root / "mod" / "Source" / "Foo.cs").write_text("// v2\n", encoding="utf-8")
    (submod_root / "mod" / "Source" / "Other.cs").write_text("// unrelated\n", encoding="utf-8")
    wd.git_commit(["mod/Source/Foo.cs"], "fix one file")
    assert _git_in(submod_root / "mod", "status", "--porcelain") == "?? Source/Other.cs"


def test_revert_restores_a_submodule_file(submod_root):
    (submod_root / "mod" / "Source" / "Foo.cs").write_text("// v2\n", encoding="utf-8")
    assert wd.git_revert("mod/Source/Foo.cs") == "reverted mod/Source/Foo.cs to HEAD"
    assert (submod_root / "mod" / "Source" / "Foo.cs").read_text(encoding="utf-8") == "// v1\n"


# ---------------------------------------------------------------- revert

def test_revert_clears_the_unverified_flag_for_that_root(repo, monkeypatch):
    ctx = Ctx()
    _patch(ctx)
    monkeypatch.setattr(wd, "git_revert", lambda rel: f"reverted {rel}")
    wt.repo_revert(ctx, "mod/Source/Ui/UiRpc.cs")
    st = wd.state_for(ctx)
    assert st.touched["mod/Source/Ui/UiRpc.cs"] == "reverted"
    assert st.pending_paths() == []
    assert st.unverified == set()


def test_revert_refuses_out_of_scope(repo, monkeypatch):
    monkeypatch.setattr(wd, "git_revert", lambda rel: pytest.fail("git checkout ran out of scope"))
    with pytest.raises(wd.WatchdogError):
        wt.repo_revert(Ctx(), "brain/memory/notebook.md")


# ---------------------------------------------------------------- error collection

def _error_stream(bus, n=3):
    bus.emit("think_start", {"trigger": "scheduled", "stream": "play"})
    for i in range(n):
        bus.emit("tool_call", {"name": "rw_ui_add_bill", "args": {"station": f"Thing_{i}"}, "id": f"c{i}"})
        bus.emit("tool_result", {"name": "rw_ui_add_bill", "id": f"c{i}", "ok": False, "text": "missing param 'thing'"})
    bus.emit("tool_call", {"name": "rw_state_summary", "args": {}, "id": "ok1"})
    bus.emit("tool_result", {"name": "rw_state_summary", "id": "ok1", "ok": True, "text": "{}"})
    bus.emit("tool_call", {"name": "end_turn", "args": {}, "id": "e1"})
    bus.emit("tool_result", {"name": "end_turn", "id": "e1", "ok": False, "text": "ignored"})


def test_recent_errors_pairs_failures_with_their_call_and_skips_successes():
    bus = Bus(log_file=False)
    _error_stream(bus, n=3)
    errs = wd.recent_errors(bus, 0)
    assert len(errs) == 3                       # 3 failures; the success and end_turn are not defects
    assert {e["tool"] for e in errs} == {"rw_ui_add_bill"}
    assert errs[0]["args"] == {"station": "Thing_0"}
    assert errs[0]["trigger"] == "scheduled" and errs[0]["stream"] == "play"


def test_recent_errors_only_looks_after_the_given_sequence():
    bus = Bus(log_file=False)
    _error_stream(bus, n=2)
    mark = bus.last_seq
    assert len(wd.recent_errors(bus, 0)) == 2
    assert wd.recent_errors(bus, mark) == []
    _error_stream(bus, n=1)
    assert len(wd.recent_errors(bus, mark)) == 1


def test_recent_errors_includes_runner_errors():
    bus = Bus(log_file=False)
    bus.emit("error", {"text": "improvement pass failed: boom"})
    errs = wd.recent_errors(bus, 0)
    assert len(errs) == 1 and errs[0]["kind"] == "runner_error"


def test_format_errors_groups_repeats():
    bus = Bus(log_file=False)
    _error_stream(bus, n=5)
    text = wd.format_errors(wd.recent_errors(bus, 0))
    assert "rw_ui_add_bill — 5x" in text
    assert "and 2 more identical failures" in text


# ---------------------------------------------------------------- cadence

CFG = {"watchdog": {"enabled": True, "every_hours": 6, "min_errors": 8}}
NOW = 1_000_000.0


def test_due_requires_both_the_clock_and_the_error_count():
    old = NOW - 7 * 3600
    assert wd.due(CFG, old, 10, now=NOW)[0] is True
    # enough time, not enough errors
    ok, why = wd.due(CFG, old, 7, now=NOW)
    assert ok is False and "7 errors" in why
    # enough errors, not enough time
    ok, why = wd.due(CFG, NOW - 3600, 50, now=NOW)
    assert ok is False and "next pass in" in why


def test_due_is_off_when_disabled():
    cfg = {"watchdog": {"enabled": False, "every_hours": 0, "min_errors": 0}}
    assert wd.due(cfg, 0, 9999, now=NOW) == (False, "watchdog disabled in config")
    assert wd.due({}, 0, 9999, now=NOW)[0] is False


def test_due_boundary_is_inclusive():
    assert wd.due(CFG, NOW - 6 * 3600, 8, now=NOW)[0] is True


# ---------------------------------------------------------------- end_watchdog / log

def test_end_watchdog_writes_the_log_and_emits_an_event(repo):
    ctx = Ctx()
    st = wd.state_for(ctx)
    st.errors_seen = 12
    st.committed = ["abc1234"]
    res = wt.end_watchdog(ctx, "one real defect, the rest noise", fixes=["ui.add_bill alias"], skipped=["bad defName"])
    assert res["logged"] is True
    assert ctx.stop_turn is True
    text = wd.read_log()
    assert "12 errors reviewed" in text
    assert "- fixed: ui.add_bill alias" in text
    assert "- skipped (noise): bad defName" in text
    assert "abc1234" in text
    assert [k for k, _ in ctx.events if k == "watchdog"]


def test_log_appends_rather_than_replaces(repo):
    wd.log_pass(wd.PassState(errors_seen=1, summary="first"))
    wd.log_pass(wd.PassState(errors_seen=2, summary="second"))
    text = wd.read_log()
    assert "first" in text and "second" in text
    assert text.index("first") < text.index("second")


# ---------------------------------------------------------------- tool scoping

def test_watchdog_role_allows_only_the_scoped_tools():
    from rimagent import roles
    from rimagent.registry import Tool

    def mk(name, source="builtin"):
        return Tool(name=name, description="", fn=lambda ctx: None, schema={}, source=source)

    for allowed in ("repo_read", "repo_grep", "repo_patch", "repo_revert",
                    "watchdog_verify_mod", "watchdog_verify_python", "watchdog_commit", "end_watchdog",
                    "search_source", "read_source"):
        assert roles.allow_watchdog(mk(allowed)), allowed
    for denied in ("run_python", "rpc", "rw_ui_build", "skill_write", "tool_write", "notebook_append",
                   "end_turn", "end_episode", "brain_revert", "look"):
        assert not roles.allow_watchdog(mk(denied)), denied
    # brain-authored tools are shared with every PLAY role; the watchdog does not get them
    assert not roles.allow_watchdog(mk("my_brain_tool", source="brain"))


def test_the_watchdog_is_not_a_parallel_play_stream():
    from rimagent import roles
    assert "watchdog" not in roles.ROLES


def test_verify_mod_does_not_write_the_live_assemblies_dir():
    """The running game loaded mod/1.6/Assemblies/RimBridge.dll; a verification build must not replace it."""
    import inspect
    src = inspect.getsource(wd.verify_mod)
    assert "OutputPath" in src and "BUILD_OUT" in src
    assert "1.6/Assemblies" not in str(wd.BUILD_OUT)
    assert str(wd.BUILD_OUT).endswith("watchdog-build")


# ---------------------------------------------------------------- runner trigger

def _runner(watchdog_cfg, bus=None):
    """A bare Runner (no bridge, no LLM) for the cadence/trigger logic only."""
    from rimagent import runner as runner_mod
    r = runner_mod.Runner.__new__(runner_mod.Runner)
    r.cfg = {"play": {}, "watchdog": watchdog_cfg}
    r.bus = bus or Bus(log_file=False)
    r._watchdog_at = time.time() - 24 * 3600
    r._watchdog_seq = 0
    r._watchdog_thread = None
    r._improve_thread = None
    r.started = []
    r.start_watchdog_thread = lambda day, errors=None, why="": r.started.append((day, len(errors or []), why))
    return r


def test_runner_starts_a_pass_only_when_both_gates_open():
    bus = Bus(log_file=False)
    r = _runner({"enabled": True, "every_hours": 6, "min_errors": 4}, bus)
    _error_stream(bus, n=2)
    r.maybe_start_watchdog(day=3)
    assert r.started == []                       # 2 errors < min_errors
    _error_stream(bus, n=3)
    r.maybe_start_watchdog(day=4)
    assert len(r.started) == 1 and r.started[0][1] == 5


def test_runner_respects_the_clock_gate():
    bus = Bus(log_file=False)
    r = _runner({"enabled": True, "every_hours": 6, "min_errors": 1}, bus)
    r._watchdog_at = time.time()                 # just ran
    _error_stream(bus, n=9)
    r.maybe_start_watchdog(day=5)
    assert r.started == []


def test_runner_does_not_run_a_pass_beside_an_improvement_pass():
    bus = Bus(log_file=False)
    r = _runner({"enabled": True, "every_hours": 0, "min_errors": 1}, bus)
    _error_stream(bus, n=9)
    r._improve_thread = SimpleNamespace(is_alive=lambda: True)
    r.maybe_start_watchdog(day=6)
    assert r.started == []
    r._improve_thread = SimpleNamespace(is_alive=lambda: False)
    r.maybe_start_watchdog(day=6)
    assert len(r.started) == 1


def test_runner_does_not_run_two_passes_at_once():
    bus = Bus(log_file=False)
    r = _runner({"enabled": True, "every_hours": 0, "min_errors": 1}, bus)
    _error_stream(bus, n=9)
    r._watchdog_thread = SimpleNamespace(is_alive=lambda: True)
    r.maybe_start_watchdog(day=7)
    assert r.started == []


def test_runner_is_off_when_the_config_is_off():
    bus = Bus(log_file=False)
    r = _runner({"enabled": False, "every_hours": 0, "min_errors": 0}, bus)
    _error_stream(bus, n=9)
    r.maybe_start_watchdog(day=8)
    assert r.started == []


def test_runner_errors_are_scoped_to_the_window_since_the_last_pass():
    bus = Bus(log_file=False)
    r = _runner({"enabled": True, "every_hours": 0, "min_errors": 1}, bus)
    _error_stream(bus, n=3)
    assert len(r.watchdog_errors()) == 3
    r._watchdog_seq = bus.last_seq
    assert r.watchdog_errors() == []


# ---------------------------------------------------------------- dashboard

def test_dashboard_exposes_the_watchdog_log(repo):
    from fastapi.testclient import TestClient

    from rimagent.dashboard.app import create_app
    from tests.test_dashboard import FakeBridge, FakeControls

    wd.log_pass(wd.PassState(errors_seen=4, summary="nothing but noise this time"))
    c = TestClient(create_app(Bus(log_file=False), FakeBridge(), FakeControls()))
    j = c.get("/api/watchdog").json()
    assert "nothing but noise this time" in j["text"]
    assert j["last_run"] is not None
    assert "Watchdog" in c.get("/").text and "loadWatchdog" in c.get("/").text


def test_watchdog_tools_are_hidden_from_every_other_stream():
    """The tools are registered globally (one registry), so the play step — which asks for no groups at all and
    therefore gets everything — must still not be offered repo_patch."""
    from rimagent.registry import Registry
    from rimagent.tools import knowledge as knowledge_tools
    from rimagent.tools import meta as meta_tools
    from rimagent.tools import watchdog as watchdog_tools

    r = Registry()
    for m in (knowledge_tools, meta_tools, watchdog_tools):
        r.add_module(m)
    names = lambda specs: {s["function"]["name"] for s in specs}  # noqa: E731

    play = names(r.specs())                                   # play step: no group filter
    assert "run_python" in play and "look" in play
    assert not (play & {"repo_patch", "repo_read", "repo_revert", "watchdog_commit", "end_watchdog"})

    improve = names(r.specs(groups={"brain", "knowledge", "meta"}))   # improvement / reflection passes
    assert not (improve & {"repo_patch", "watchdog_commit"})

    from rimagent import roles
    wdog = names(r.specs(groups={"watchdog", "knowledge"}, allow=roles.allow_watchdog))
    assert {"repo_read", "repo_patch", "repo_revert", "watchdog_verify_mod", "watchdog_verify_python",
            "watchdog_commit", "end_watchdog", "search_source"} <= wdog
    assert not (wdog & {"run_python", "rpc", "look", "end_turn"})


# ---------------------------------------------------------------- end-to-end pass

class ScriptedLLM:
    """Replays a scripted list of tool calls through loop.think, so the whole pass is exercised for real."""

    def __init__(self, script):
        self.script = list(script)
        self.systems: list[str] = []
        self.prompts: list[str] = []
        self.tool_names: list[set] = []

    def chat(self, messages, tools=None, thinking=None, **kw):
        from rimagent.llm import LLMReply
        self.systems.append(messages[0]["content"])
        self.prompts.append(messages[1]["content"])
        self.tool_names.append({t["function"]["name"] for t in (tools or [])})
        if not self.script:
            return LLMReply(content="done")
        name, args = self.script.pop(0)
        return LLMReply(content="", tool_calls=[{"id": f"t{len(self.systems)}", "name": name, "arguments": args}])

    def assistant_message(self, reply):
        from rimagent.llm import LLM
        return LLM.assistant_message(self, reply)


def test_run_pass_end_to_end(repo, monkeypatch):
    from rimagent.context import Context
    from rimagent.registry import Registry
    from rimagent.tools import knowledge as knowledge_tools
    from rimagent.tools import watchdog as watchdog_tools

    reg = Registry()
    for m in (knowledge_tools, watchdog_tools):
        reg.add_module(m)
    monkeypatch.setattr(wd, "verify_mod", lambda: (True, "104 passed"))
    monkeypatch.setattr(wd, "git_commit", lambda paths, msg: "deadbee")
    llm = ScriptedLLM([
        ("repo_read", {"path": "mod/Source/Ui/UiRpc.cs"}),
        ("repo_patch", {"path": "mod/Source/Ui/UiRpc.cs", "content": "// fixed\n"}),
        ("watchdog_commit", {"message": "too early"}),          # refused: no verify yet
        ("watchdog_verify_mod", {}),
        ("watchdog_commit", {"message": "accept station as an alias for thing"}),
        ("end_watchdog", {"summary": "one real defect", "fixes": ["ui.add_bill alias"], "skipped": ["bad defName"]}),
    ])
    events = []
    ctx = Context(bridge=None, llm=llm, registry=reg, config={"play": {}}, emit=lambda k, d: events.append((k, d)), episode=7)
    bus = Bus(log_file=False)
    _error_stream(bus, n=9)
    st = wd.run_pass(ctx, wd.recent_errors(bus, 0), max_calls=10)

    assert st.ended is True and st.errors_seen == 9
    assert st.committed == ["deadbee"]
    assert st.fixes == ["ui.add_bill alias"] and st.skipped == ["bad defName"]
    # the premature commit was refused, and the tool error was fed back to the model rather than raised
    results = [d for k, d in events if k == "tool_result"]
    refused = [d for d in results if d["name"] == "watchdog_commit" and not d["ok"]]
    assert len(refused) == 1 and "watchdog_verify_mod" in refused[0]["text"]
    # it ran on the watchdog system prompt, not the colony one, and never saw a play tool
    assert "You are the watchdog stream" in llm.systems[0] and "RimWorld colony" not in llm.systems[0]
    assert all(not (names & {"run_python", "rpc", "end_turn", "skill_write"}) for names in llm.tool_names)
    # the prompt it was given is the error stream itself, with the args that produced each failure
    assert "9 tool calls failed" in llm.prompts[0]
    assert "rw_ui_add_bill" in llm.prompts[0] and '"station"' in llm.prompts[0]
    assert "one real defect" in wd.read_log()
