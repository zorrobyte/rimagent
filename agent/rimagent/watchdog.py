"""Watchdog stream: a privileged, low-frequency self-correction pass over the project's own source.

The play/improve streams only ever touch `brain/` (skills, tools, watchers, memory): hot-reloadable text,
safe by construction. The watchdog is the other thing entirely. It reads the recent tool-call ERROR stream,
decides which failures were model noise (a bad guess the model fixed on retry) and which were genuine defects
in `mod/Source/**` (RimBridge, C#), `mod-steward/Source/**` (the optional Steward add-on, C#) or
`agent/rimagent/**` (Python), and for the genuine ones reads the source, writes a minimal fix, and PROVES it
with the real build and the real test suites before committing it.

Three hard rules, enforced here in the module rather than in the prompt:
  1. Scope. Nothing outside `mod/Source/`, `mod-steward/Source/` and `agent/rimagent/` can be read, patched or
     reverted. No `..`, no absolute escapes, no symlink escapes, no `.git`, no `brain/`, no `config.local.yaml`.
  2. Proof. A patch marks its root "unverified". `watchdog_commit` refuses while any root is unverified,
     and verification is recorded here from the subprocess exit code, never from what the model claims.
  3. No deployment. The watchdog never restarts the game and never writes the live assembly: the
     verification build goes to a scratch output directory (`runs/watchdog-build/`), so `mod/1.6/Assemblies/`
     — the symlink RimWorld actually loads — is untouched. Fixes wait for a human to deploy at the next
     natural restart. It has no push access either; commits stay local.
"""
from __future__ import annotations

import os
import subprocess
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from .paths import ROOT, RUNS, WATCHDOG_LOG

# The only trees the watchdog may see or change, relative to the repo root.
ALLOWED_ROOTS: tuple[str, ...] = ("mod/Source", "mod-steward/Source", "agent/rimagent")
# Which verify tool proves a change under each root.
ROOT_KEY: dict[str, str] = {"mod/Source": "mod", "mod-steward/Source": "mod-steward", "agent/rimagent": "agent"}
VERIFY_TOOL = {"mod": "watchdog_verify_mod", "mod-steward": "watchdog_verify_mod_steward", "agent": "watchdog_verify_python"}
# Path components that are never legal inside an allowed root (build spoil, VCS internals).
FORBIDDEN_PARTS = {".git", "__pycache__", "obj", "bin", ".venv", "node_modules"}
COMMIT_TRAILER = "Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>"
# Verification build output: deliberately NOT mod/1.6/Assemblies (that symlink is what the running game loads).
BUILD_OUT = RUNS / "watchdog-build"
MAX_PATCH_BYTES = 400_000
VERIFY_TIMEOUT_S = 1200


class WatchdogError(Exception):
    """A guardrail refused. Raised out of the tools so the model sees the reason as a tool error."""


# ---------------------------------------------------------------- path allowlist

def _repo_root() -> Path:
    return Path(ROOT).resolve()


def safe_path(path: str) -> str:
    """Normalise `path` to a repo-relative posix path inside an allowed root, or raise WatchdogError.

    Rejects: empty, absolute paths outside the repo, any `..` component, anything not under
    mod/Source/, mod-steward/Source/ or agent/rimagent/, .git/build/venv internals, and symlinks that resolve outside
    their allowed root (checked with realpath, so a symlink planted in-tree cannot escape)."""
    raw = str(path or "").strip().replace("\\", "/")
    if not raw:
        raise WatchdogError("empty path")
    root = _repo_root()
    if raw.startswith("/"):
        try:
            raw = os.path.relpath(os.path.realpath(raw), str(root))
        except ValueError as e:  # different drive / unrelated path
            raise WatchdogError(f"{path!r} is outside the repository") from e
        raw = raw.replace("\\", "/")
    parts = [p for p in raw.split("/") if p not in ("", ".")]
    if any(p == ".." for p in parts):
        raise WatchdogError(f"{path!r}: '..' is not allowed; give a path relative to the repo root")
    rel = "/".join(parts)
    if not rel:
        raise WatchdogError("empty path")
    bad = sorted(set(parts) & FORBIDDEN_PARTS)
    if bad:
        raise WatchdogError(f"{rel}: {bad[0]!r} is off limits")
    allowed = next((r for r in ALLOWED_ROOTS if rel == r or rel.startswith(r + "/")), None)
    if allowed is None:
        raise WatchdogError(
            f"{rel} is outside the watchdog's scope. You may only read or change files under "
            + " or ".join(ALLOWED_ROOTS)
            + " (brain/, config files, knowledge/, mod/1.6/ and everything else are off limits)."
        )
    # Symlink escape: the real location must stay inside the real allowed root.
    real_root = os.path.realpath(str(root / allowed))
    real_target = os.path.realpath(str(root / rel))
    if real_target != real_root and not real_target.startswith(real_root + os.sep):
        raise WatchdogError(f"{rel} resolves outside {allowed}/ (symlink); refused")
    return rel


def abs_path(path: str) -> Path:
    return _repo_root() / safe_path(path)


def root_key(rel: str) -> str:
    """"mod", "mod-steward" or "agent": which verify tool proves a change to this file."""
    for r, key in ROOT_KEY.items():
        if rel == r or rel.startswith(r + "/"):
            return key
    raise WatchdogError(f"{rel} belongs to no verifiable root")


# ---------------------------------------------------------------- per-pass state

@dataclass
class PassState:
    """Server-side bookkeeping for one watchdog pass. The model never writes to this directly."""
    started: float = field(default_factory=time.time)
    touched: dict[str, str] = field(default_factory=dict)   # rel path -> patched | reverted
    unverified: set[str] = field(default_factory=set)       # root keys patched since their last GREEN verify
    verified: dict[str, bool] = field(default_factory=dict)  # root key -> last verify outcome
    committed: list[str] = field(default_factory=list)      # commit shas
    fixes: list[str] = field(default_factory=list)
    skipped: list[str] = field(default_factory=list)
    summary: str = ""
    ended: bool = False
    errors_seen: int = 0

    def pending_paths(self) -> list[str]:
        return sorted(p for p, action in self.touched.items() if action == "patched")


def state_for(ctx: Any) -> PassState:
    st = ctx.extra.get("watchdog_state")
    if not isinstance(st, PassState):
        st = PassState()
        ctx.extra["watchdog_state"] = st
    return st


# ---------------------------------------------------------------- verification

def _run(cmd: list[str], cwd: Path, timeout: int = VERIFY_TIMEOUT_S, env: dict[str, str] | None = None) -> tuple[bool, str]:
    e = dict(os.environ)
    e.update(env or {})
    try:
        r = subprocess.run(cmd, cwd=str(cwd), capture_output=True, text=True, timeout=timeout, env=e)
    except subprocess.TimeoutExpired:
        return False, f"TIMEOUT after {timeout}s: {' '.join(cmd)}"
    except OSError as ex:
        return False, f"could not run {' '.join(cmd)}: {ex}"
    return r.returncode == 0, ((r.stdout or "") + ("\n" + r.stderr if r.stderr else ""))


def _tail(text: str, limit: int = 4000) -> str:
    text = text.strip()
    return text if len(text) <= limit else "…(head elided)\n" + text[-limit:]


DOTNET_ROOT = os.environ.get("DOTNET_ROOT", "/opt/homebrew/opt/dotnet/libexec")


def verify_python() -> tuple[bool, str]:
    """`uv run pytest -q` in agent/."""
    ok, out = _run(["uv", "run", "pytest", "-q"], _repo_root() / "agent")
    return ok, _tail(out)


def verify_mod() -> tuple[bool, str]:
    """Build RimBridge (to a scratch output dir, never the live Assemblies symlink), then the mod test suite."""
    BUILD_OUT.mkdir(parents=True, exist_ok=True)
    env = {"DOTNET_ROOT": DOTNET_ROOT, "PATH": os.environ.get("PATH", "") + os.pathsep + str(Path(DOTNET_ROOT).parent / "bin")}
    ok, out = _run(
        ["dotnet", "build", "Source/RimBridge.csproj", "-c", "Release", "--nologo", "-v", "quiet",
         f"-p:OutputPath={BUILD_OUT.resolve()}{os.sep}"],
        _repo_root() / "mod", env=env,
    )
    if not ok:
        return False, "BUILD FAILED\n" + _tail(out)
    ok2, out2 = _run(["dotnet", "test", "--nologo"], _repo_root() / "mod" / "Tests", env=env)
    return ok2, ("build ok (scratch output, live assembly untouched)\n" + _tail(out2))


def verify_mod_steward() -> tuple[bool, str]:
    """Build RimBridgeSteward.csproj (to a scratch output dir, never the live Assemblies symlink), then the
    steward test suite. RimBridgeSteward.csproj references mod/1.6/Assemblies/RimBridge.dll — the live, already
    -built RimBridge — as a read-only compile-time reference; that's a read, never a write, so it never touches
    what the running game loaded. If mod/Source and mod-steward/Source were both patched in this pass, verify_mod
    must run first so that reference reflects the patched RimBridge."""
    BUILD_OUT.mkdir(parents=True, exist_ok=True)
    env = {"DOTNET_ROOT": DOTNET_ROOT, "PATH": os.environ.get("PATH", "") + os.pathsep + str(Path(DOTNET_ROOT).parent / "bin")}
    ok, out = _run(
        ["dotnet", "build", "Source/RimBridgeSteward.csproj", "-c", "Release", "--nologo", "-v", "quiet",
         f"-p:OutputPath={BUILD_OUT.resolve()}{os.sep}"],
        _repo_root() / "mod-steward", env=env,
    )
    if not ok:
        return False, "BUILD FAILED\n" + _tail(out)
    ok2, out2 = _run(["dotnet", "test", "--nologo"], _repo_root() / "mod-steward" / "Tests", env=env)
    return ok2, ("build ok (scratch output, live assembly untouched)\n" + _tail(out2))


# ---------------------------------------------------------------- git (commit / revert only)

def _git(*args: str, timeout: int = 120) -> str:
    r = subprocess.run(["git", *args], cwd=str(_repo_root()), capture_output=True, text=True, timeout=timeout)
    if r.returncode != 0:
        raise WatchdogError((r.stderr or r.stdout).strip() or f"git {args[0]} failed")
    return r.stdout.strip()


def git_revert(rel: str) -> str:
    """`git checkout -- <rel>`, scoped to one already-validated path."""
    _git("checkout", "--", rel)
    return f"reverted {rel} to HEAD"


def git_commit(paths: list[str], message: str) -> str:
    """Stage exactly `paths` (never -A) and commit them with the fixed trailer. No push, ever."""
    rels = [safe_path(p) for p in paths]
    if not rels:
        raise WatchdogError("nothing to commit")
    body = (message or "watchdog fix").strip()
    if COMMIT_TRAILER not in body:
        body = body + "\n\n" + COMMIT_TRAILER
    _git("add", "--", *rels)
    try:
        _git("commit", "-q", "-m", body, "--", *rels)
    except WatchdogError as e:
        if "nothing to commit" in str(e) or "no changes added" in str(e):
            raise WatchdogError("nothing to commit: the files are identical to HEAD") from e
        raise
    return _git("rev-parse", "--short", "HEAD")


# ---------------------------------------------------------------- error collection

_SKIP_TOOLS = {"end_turn", "end_episode", "reply_to_operator"}


def recent_errors(bus: Any, since_seq: int, limit: int = 5000) -> list[dict[str, Any]]:
    """Failed tool calls on the bus since `since_seq`, each with the call that produced it.

    A tool_result carries only (name, id, ok, text); the arguments live on the matching tool_call, and the
    think_start before it says which step/stream it came from. Stitch the three together so the watchdog can
    actually diagnose a failure instead of guessing what was passed."""
    calls: dict[str, dict[str, Any]] = {}
    ctxline = {"trigger": "?", "stream": "?"}
    out: list[dict[str, Any]] = []
    for ev in bus.since(since_seq, limit=limit, kinds={"think_start", "tool_call", "tool_result", "error"}):
        d = ev.get("data") or {}
        if ev["kind"] == "think_start":
            ctxline = {"trigger": str(d.get("trigger", "?")), "stream": str(d.get("stream", "play"))}
        elif ev["kind"] == "tool_call":
            calls[str(d.get("id"))] = {"name": d.get("name"), "args": d.get("args"), **ctxline}
        elif ev["kind"] == "tool_result" and not d.get("ok"):
            name = str(d.get("name") or "?")
            if name in _SKIP_TOOLS:
                continue
            call = calls.get(str(d.get("id")), {})
            out.append({"kind": "tool_error", "seq": ev["seq"], "t": ev.get("t"), "tool": name,
                        "args": call.get("args") or {}, "error": str(d.get("text") or "")[:1200],
                        "trigger": call.get("trigger", ctxline["trigger"]), "stream": call.get("stream", ctxline["stream"])})
        elif ev["kind"] == "error":
            out.append({"kind": "runner_error", "seq": ev["seq"], "t": ev.get("t"), "tool": "(runner)",
                        "args": {}, "error": str(d.get("text") or "")[:1200],
                        "trigger": ctxline["trigger"], "stream": ctxline["stream"]})
    return out


def format_errors(errors: list[dict[str, Any]], max_chars: int = 24000) -> str:
    """Group identical (tool, first line of message) failures so repetition is visible at a glance."""
    import collections
    import json as _json
    groups: dict[tuple[str, str], list[dict[str, Any]]] = collections.OrderedDict()
    for e in errors:
        head = (e["error"].strip().splitlines() or [""])[0][:160]
        groups.setdefault((e["tool"], head), []).append(e)
    lines: list[str] = []
    for (toolname, head), items in sorted(groups.items(), key=lambda kv: -len(kv[1])):
        lines.append(f"### {toolname} — {len(items)}x")
        lines.append(f"  message: {head}")
        for e in items[:3]:
            try:
                args = _json.dumps(e["args"], ensure_ascii=False, default=str)[:500]
            except Exception:  # noqa: BLE001
                args = str(e["args"])[:500]
            lines.append(f"  - [{e['stream']} · {e['trigger']}] args={args}")
            body = e["error"].strip()
            if body and body != head:
                lines.append("    " + body[:700].replace("\n", "\n    "))
        if len(items) > 3:
            lines.append(f"  … and {len(items) - 3} more identical failures")
        lines.append("")
    text = "\n".join(lines)
    return text[:max_chars] + ("\n…(truncated)" if len(text) > max_chars else "")


# ---------------------------------------------------------------- cadence

def due(cfg: dict[str, Any], last_run_at: float, error_count: int, now: float | None = None) -> tuple[bool, str]:
    """Is a watchdog pass due? Config `watchdog: {enabled, every_hours, min_errors}`.

    Both gates must pass: enough wall-clock time since the last pass AND at least `min_errors` failed tool
    calls recorded since it. The error gate is the point of the design: a cadence tick with a clean error
    stream has nothing to look at, so it fires nothing and costs nothing."""
    w = cfg.get("watchdog") or {}
    if not w.get("enabled", False):
        return False, "watchdog disabled in config"
    now = time.time() if now is None else now
    every = float(w.get("every_hours", 6) or 0) * 3600.0
    min_errors = int(w.get("min_errors", 8))
    waited = now - last_run_at
    if waited < every:
        return False, f"next pass in {(every - waited) / 3600.0:.1f}h"
    if error_count < min_errors:
        return False, f"only {error_count} errors since the last pass (need {min_errors})"
    return True, f"{error_count} errors in the last {waited / 3600.0:.1f}h"


# ---------------------------------------------------------------- the log

def log_pass(state: PassState, episode: int | None = None, day: int | None = None) -> str:
    """Append one human-readable entry to brain/memory/watchdog_log.md and return it."""
    stamp = time.strftime("%Y-%m-%d %H:%M")
    where = ", ".join(x for x in (f"episode {episode}" if episode is not None else "", f"day {day}" if day is not None else "") if x)
    head = f"\n## {stamp}" + (f" ({where})" if where else "") + f" — {state.errors_seen} errors reviewed\n"
    parts = [head]
    if state.summary:
        parts.append(state.summary.strip() + "\n")
    for f in state.fixes:
        parts.append(f"- fixed: {f}\n")
    for s in state.skipped:
        parts.append(f"- skipped (noise): {s}\n")
    if state.committed:
        parts.append(f"- commits: {', '.join(state.committed)} (local only; deploy at the next restart)\n")
    unpushed = state.pending_paths()
    if unpushed:
        parts.append(f"- left uncommitted in the working tree: {', '.join(unpushed)}\n")
    entry = "".join(parts)
    WATCHDOG_LOG.parent.mkdir(parents=True, exist_ok=True)
    with WATCHDOG_LOG.open("a", encoding="utf-8") as fh:
        if fh.tell() == 0:
            fh.write("# Watchdog log\n\nOne entry per self-correction pass. The watchdog reads the tool-call error\n"
                     "stream, fixes genuine defects in mod/Source, mod-steward/Source or agent/rimagent, verifies them with the build\n"
                     "and test suites, and commits locally. It never deploys: a human restarts the game.\n")
        fh.write(entry)
    return entry


def read_log(max_chars: int = 40000) -> str:
    if not WATCHDOG_LOG.exists():
        return ""
    return WATCHDOG_LOG.read_text(encoding="utf-8")[-max_chars:]


# ---------------------------------------------------------------- the pass

SYSTEM = """You are the watchdog stream of rimagent, an autonomous agent that plays RimWorld through a C# mod it also wrote.

You are not playing. You are reviewing the agent's own source code against the errors its tools produced, and fixing
the real defects among them. You have read and patch access to exactly three trees, mod/Source/** (the RimBridge C# mod),
mod-steward/Source/** (the optional Steward add-on) and agent/rimagent/** (the Python agent); everything else
is refused by the tools. Every patch must pass the real
build and the real test suite before it can be committed, and the commit tool verifies that for itself.

You never deploy: you cannot restart the game, the verification build does not replace the assembly the running game
loaded, and you have no push access. Your commits are prepared for a human to deploy at the next restart.

Work with tool calls, not prose. Keep visible text to a line or two per decision. Finish with end_watchdog."""

DEFAULT_PROMPT = """Watchdog pass. {n_errors} tool calls failed since your last pass.

Classify each as model noise or a genuine defect in mod/Source/**, mod-steward/Source/** or agent/rimagent/**. For
defects: repo_grep and repo_read the source, repo_patch a minimal fix, run watchdog_verify_mod()/
watchdog_verify_mod_steward()/watchdog_verify_python() as needed (verify mod before mod-steward if both changed),
then watchdog_commit(message). If a fix does not verify, repo_revert it. Finish with end_watchdog(summary, fixes, skipped).
Budget: {max_calls} tool calls.

## The errors

{errors}
"""


def run_pass(ctx: Any, errors: list[dict[str, Any]], max_calls: int = 40) -> PassState:
    """One bounded watchdog conversation on a forked context. Returns the (server-side) pass state.

    Mirrors loop.think's bounded tool-use step, but with its own system prompt (it is not playing a colony), its own
    small tool allowlist, and end_watchdog as the terminal tool."""
    from . import roles
    from .loop import _fmt, load_prompt, think

    st = state_for(ctx)
    st.errors_seen = len(errors)
    prompt = _fmt(load_prompt("watchdog", DEFAULT_PROMPT),
                  n_errors=str(len(errors)), max_calls=str(max_calls), errors=format_errors(errors) or "(none)")
    think(ctx, prompt, situation_hint="watchdog source review",
          max_calls=max_calls, tool_groups={"watchdog", "knowledge"}, tool_allow=roles.allow_watchdog,
          system=SYSTEM, end_tools=("end_watchdog",), trigger="watchdog pass")
    if not st.ended:
        # The model ran out of calls or stopped talking without calling end_watchdog: log the pass anyway, and say so.
        st.summary = (st.summary or "") + " (the pass ended without end_watchdog; the log below is reconstructed from what was touched)"
        log_pass(st, episode=getattr(ctx, "episode", None))
        ctx.emit("watchdog", {"summary": st.summary, "fixes": st.fixes, "skipped": st.skipped,
                              "commits": st.committed, "errors": st.errors_seen, "uncommitted": st.pending_paths()})
    return st
