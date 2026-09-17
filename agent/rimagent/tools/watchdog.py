"""Watchdog tools: the only way the watchdog stream can touch the project's own source.

Every guardrail lives in the tool body (and in `rimagent.watchdog`), not in the prompt: a model that
ignores the instructions still cannot read outside scope, cannot commit an unverified patch, and cannot
reach git except through `repo_revert` and `watchdog_commit`. Deliberately absent from this stream:
`run_python` (an unsandboxed `exec` with a live bridge handle would make every path check decorative),
`rpc` / `rw_*` (no reason for a code reviewer to drive the colony), and the brain write tools.
"""
from __future__ import annotations

import subprocess

from .. import watchdog as wd
from ..registry import tool


def _state(ctx):
    return wd.state_for(ctx)


# ---------------------------------------------------------------- read

@tool("repo_read", "Read a file from the project's own source. Scope: mod/Source/** (the RimBridge C# mod) and agent/rimagent/** (the Python agent). Anything else is refused.",
      {"path": "repo-relative path, e.g. mod/Source/Ui/UiRpc.cs or agent/rimagent/loop.py", "start": "first line (default 1)", "end": "last line (default: 400 lines from start)"}, group="watchdog")
def repo_read(ctx, path: str, start: int = 1, end: int | None = None):
    p = wd.abs_path(path)
    if not p.exists():
        raise wd.WatchdogError(f"{wd.safe_path(path)} does not exist (use repo_grep or repo_list to find the real name)")
    lines = p.read_text(encoding="utf-8", errors="replace").splitlines()
    start = max(1, int(start))
    end = min(len(lines), int(end) if end else start + 399)
    body = "\n".join(f"{i:5d}  {lines[i - 1]}" for i in range(start, end + 1))
    return {"path": wd.safe_path(path), "lines": len(lines), "shown": [start, end], "text": body[:30000]}


@tool("repo_list", "List source files under a directory of the project (mod/Source/** or agent/rimagent/** only).",
      {"path": "directory, e.g. mod/Source/Ui", "pattern": "glob, default * (use **/* to recurse)"}, group="watchdog")
def repo_list(ctx, path: str = "mod/Source", pattern: str = "*"):
    if ".." in str(pattern) or str(pattern).startswith("/"):
        raise wd.WatchdogError("bad pattern")
    d = wd.abs_path(path)
    if not d.is_dir():
        raise wd.WatchdogError(f"{wd.safe_path(path)} is not a directory")
    out = []
    for f in sorted(d.glob(pattern)):
        try:
            rel = wd.safe_path(str(f))
        except wd.WatchdogError:
            continue
        if f.is_file():
            out.append({"path": rel, "bytes": f.stat().st_size})
    return {"dir": wd.safe_path(path), "files": out[:400]}


@tool("repo_grep", "Search the project's own source for a fixed string or regex. Scope: mod/Source/** and agent/rimagent/** only.",
      {"query": "text (or regex if regex=true)", "regex": "treat the query as a regex", "root": "limit to mod/Source or agent/rimagent (default: both)", "limit": "max hits (default 40)"}, group="watchdog")
def repo_grep(ctx, query: str, regex: bool = False, root: str | None = None, limit: int = 40):
    if not str(query).strip():
        raise wd.WatchdogError("empty query")
    roots = [wd.safe_path(root)] if root else list(wd.ALLOWED_ROOTS)
    cmd = ["grep", "-rnI", "--binary-files=without-match"]
    cmd += ["-E"] if regex else ["-F"]
    for part in wd.FORBIDDEN_PARTS:
        cmd += [f"--exclude-dir={part}"]
    cmd += ["--", str(query), *roots]
    r = subprocess.run(cmd, cwd=str(wd._repo_root()), capture_output=True, text=True, timeout=60)
    hits = [ln for ln in (r.stdout or "").splitlines() if ln.strip()][: max(1, int(limit))]
    return {"query": query, "roots": roots, "hits": hits, "count": len(hits)}


# ---------------------------------------------------------------- write

@tool("repo_patch", "Replace a source file with new content (whole file). Scope: mod/Source/** and agent/rimagent/** only. Keep the change MINIMAL: read the file first, change the smallest thing that fixes the defect, leave a short comment saying why. The file's root is then marked unverified until you run its verify tool; watchdog_commit refuses while anything is unverified.",
      {"path": "repo-relative path you already read", "content": "the complete new file contents"}, group="watchdog")
def repo_patch(ctx, path: str, content: str):
    rel = wd.safe_path(path)
    p = wd.abs_path(rel)
    if not p.exists():
        raise wd.WatchdogError(f"{rel} does not exist; the watchdog fixes existing files, it does not add new ones")
    if content is None or not str(content).strip():
        raise wd.WatchdogError("refusing to write an empty file")
    if len(content) > wd.MAX_PATCH_BYTES:
        raise wd.WatchdogError(f"content is {len(content)} bytes (cap {wd.MAX_PATCH_BYTES})")
    before = p.read_text(encoding="utf-8", errors="replace")
    if before == content:
        return {"path": rel, "changed": False, "note": "identical to what is already on disk"}
    p.write_text(content, encoding="utf-8")
    st = _state(ctx)
    key = wd.root_key(rel)
    st.touched[rel] = "patched"
    st.unverified.add(key)
    st.verified.pop(key, None)
    ctx.emit("brain_change", {"kind": "source", "action": "patch", "name": rel, "stream": "watchdog"})
    return {"path": rel, "changed": True, "lines_before": before.count("\n") + 1, "lines_after": content.count("\n") + 1,
            "next": f"run {wd.VERIFY_TOOL[key]}() before you can commit"}


@tool("repo_revert", "Undo your edits to one file (git checkout -- <path>), when a fix did not verify and you would rather start clean. Scope: mod/Source/** and agent/rimagent/** only.",
      {"path": "repo-relative path to restore to HEAD"}, group="watchdog")
def repo_revert(ctx, path: str):
    rel = wd.safe_path(path)
    msg = wd.git_revert(rel)
    st = _state(ctx)
    st.touched[rel] = "reverted"
    key = wd.root_key(rel)
    if not any(wd.root_key(p) == key for p in st.pending_paths()):
        st.unverified.discard(key)   # nothing of this root is outstanding any more
    ctx.emit("brain_change", {"kind": "source", "action": "revert", "name": rel, "stream": "watchdog"})
    return msg


# ---------------------------------------------------------------- verify

@tool("watchdog_verify_python", "Run the agent test suite (cd agent && uv run pytest -q). Required before committing any change under agent/rimagent/. Returns pass/fail and the tail of the output.", group="watchdog")
def watchdog_verify_python(ctx):
    ok, out = wd.verify_python()
    st = _state(ctx)
    st.verified["agent"] = ok
    if ok:
        st.unverified.discard("agent")
    else:
        st.unverified.add("agent")
    ctx.emit("log", {"text": f"watchdog: python verify {'PASSED' if ok else 'FAILED'}", "stream": "watchdog"})
    return {"ok": ok, "suite": "agent pytest", "output": out}


@tool("watchdog_verify_mod", "Build RimBridge and run the mod test suite (dotnet build, then cd mod/Tests && dotnet test). Required before committing any change under mod/Source/. The build writes to a scratch directory, so the assembly the running game loaded is NOT replaced. Returns pass/fail and the tail of the output.", group="watchdog")
def watchdog_verify_mod(ctx):
    ok, out = wd.verify_mod()
    st = _state(ctx)
    st.verified["mod"] = ok
    if ok:
        st.unverified.discard("mod")
    else:
        st.unverified.add("mod")
    ctx.emit("log", {"text": f"watchdog: mod build+test {'PASSED' if ok else 'FAILED'}", "stream": "watchdog"})
    return {"ok": ok, "suite": "mod build + dotnet test", "output": out}


@tool("watchdog_verify_mod_steward", "Build the Steward add-on (against the live mod/1.6/Assemblies/RimBridge.dll, read-only) and run its test suite. Required before committing any change under mod-steward/Source/. If you also patched mod/Source in this pass, run watchdog_verify_mod first so this build sees the patched RimBridge. The build writes to a scratch directory, so the assembly the running game loaded is NOT replaced. Returns pass/fail and the tail of the output.", group="watchdog")
def watchdog_verify_mod_steward(ctx):
    ok, out = wd.verify_mod_steward()
    st = _state(ctx)
    st.verified["mod-steward"] = ok
    if ok:
        st.unverified.discard("mod-steward")
    else:
        st.unverified.add("mod-steward")
    ctx.emit("log", {"text": f"watchdog: mod-steward build+test {'PASSED' if ok else 'FAILED'}", "stream": "watchdog"})
    return {"ok": ok, "suite": "mod-steward build + dotnet test", "output": out}


# ---------------------------------------------------------------- commit / end

@tool("watchdog_commit", "Commit the files you patched, once they verify. Stages exactly the paths you touched (never -A, never brain/, never anything outside mod/Source and agent/rimagent) and appends the co-author trailer for you. REFUSED while any patched root is unverified, or if its verify run failed. There is no push: the commit stays local for a human to deploy at the next restart.",
      {"message": "commit message: what was broken, what the fix does, one line of evidence from the error stream"}, group="watchdog")
def watchdog_commit(ctx, message: str):
    st = _state(ctx)
    pending = st.pending_paths()
    if not pending:
        raise wd.WatchdogError("you have not patched anything in this pass; nothing to commit")
    needed = {wd.root_key(p) for p in pending}
    missing = sorted(k for k in needed if k not in st.verified)
    if missing:
        raise wd.WatchdogError("refusing to commit: you have not run " + ", ".join(wd.VERIFY_TOOL[k] + "()" for k in missing)
                               + f" since patching {', '.join(pending)}. Verify first.")
    failed = sorted(k for k in needed if not st.verified.get(k))
    if failed:
        raise wd.WatchdogError("refusing to commit: " + ", ".join(wd.VERIFY_TOOL[k] + "()" for k in failed)
                               + " failed. Fix the patch and verify again, or repo_revert the file.")
    if st.unverified & needed:
        raise wd.WatchdogError("refusing to commit: " + ", ".join(sorted(st.unverified & needed))
                               + " was patched again after its last verify. Verify again.")
    sha = wd.git_commit(pending, message)
    for p in pending:
        st.touched[p] = "committed"
    st.committed.append(sha)
    ctx.emit("brain_change", {"kind": "source", "action": "commit", "name": sha, "stream": "watchdog"})
    return {"committed": sha, "paths": pending, "pushed": False,
            "note": "local commit only; a human deploys it at the next game restart"}


@tool("end_watchdog", "Finish the watchdog pass. Writes one entry to brain/memory/watchdog_log.md and tells the dashboard. Call this exactly once, at the end, even if you fixed nothing.",
      {"summary": "2-4 sentences: what the error stream showed and what you concluded", "fixes": "list of one-line descriptions of the defects you fixed (empty if none)", "skipped": "list of one-line descriptions of errors you classified as model noise, not defects"}, group="watchdog")
def end_watchdog(ctx, summary: str = "", fixes: list | None = None, skipped: list | None = None):
    st = _state(ctx)
    st.summary = str(summary or "").strip()
    st.fixes = [str(x)[:300] for x in (fixes or [])]
    st.skipped = [str(x)[:300] for x in (skipped or [])]
    st.ended = True
    ctx.stop_turn = True
    ctx.wake.notes = st.summary
    entry = wd.log_pass(st, episode=ctx.episode)
    ctx.emit("watchdog", {"summary": st.summary, "fixes": st.fixes, "skipped": st.skipped,
                          "commits": st.committed, "errors": st.errors_seen,
                          "uncommitted": st.pending_paths(), "entry": entry})
    return {"logged": True, "commits": st.committed, "uncommitted": st.pending_paths()}
