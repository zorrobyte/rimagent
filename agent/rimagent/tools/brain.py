"""Self-improvement tools: skills, tools, watchers, memory, scores, git."""
from __future__ import annotations

import re

from .. import braingit, memory, scorecard, skills
from ..paths import TOOLS, WATCHERS
from ..registry import tool


def _pyname(name: str) -> str:
    s = re.sub(r"[^a-z0-9_]+", "_", name.lower()).strip("_")
    if not s or s[0].isdigit():
        s = "t_" + s
    return s[:60]


# ---- skills ----

@tool("skill_list", "List all skills (name, description, size). Skills marked [always] are in every prompt.", group="brain")
def skill_list(ctx):
    return skills.index_text()


@tool("skill_read", "Read a skill's full text.", {"name": "skill name"}, group="brain")
def skill_read(ctx, name: str):
    return skills.read(name)


@tool("skill_write", "Create or overwrite a skill (markdown). Keep skills concrete: triggers, steps, numbers, pitfalls. Prefer editing an existing skill over adding a near-duplicate.", {"name": "short name", "description": "one line: when to use it", "body": "markdown body", "tags": "list of tags; omit to keep the skill's current tags, pass [] to clear them (tags are part of how a skill is retrieved)", "always": "true to include in every prompt (use sparingly); omit to keep the current setting"}, group="brain")
def skill_write(ctx, name: str, description: str, body: str, tags: list[str] | None = None, always: bool | None = None):
    p = skills.write(name, description, body, tags, always)
    ctx.emit("brain_change", {"kind": "skill", "name": name, "action": "write"})
    return f"wrote {p.name} ({len(body)} chars)"


@tool("skill_delete", "Delete a skill.", {"name": "skill name"}, group="brain")
def skill_delete(ctx, name: str):
    ok = skills.delete(name)
    ctx.emit("brain_change", {"kind": "skill", "name": name, "action": "delete"})
    return "deleted" if ok else "no such skill"


# ---- tools ----

TOOL_TEMPLATE = '''"""{doc}"""
from rimagent.registry import tool


@tool("{name}", "{doc}", {{"example_arg": "what it is"}}, group="brain")
def {pyname}(ctx, example_arg: str = ""):
    # ctx.bridge.call("state.summary") -> dict ; ctx.bridge.call("ui.build", def="Wall", at=[10, 10])
    # ctx.knowledge.wiki.search(q) ; ctx.log("text")
    return {{"ok": True}}
'''


@tool("tool_list", "List brain-authored tools and any load errors (syntax/runtime) in brain/tools.", group="brain")
def tool_list(ctx):
    reg = ctx.registry
    mine = [f"- {t.name}: {t.description[:100]} (file {getattr(t.fn, '_brain_file', '?')}.py)" for t in reg.tools.values() if t.source == "brain"]
    errs = [f"- {f}: {e.strip().splitlines()[-1]}" for f, e in reg.load_errors.items()]
    return "tools:\n" + ("\n".join(mine) or "(none)") + "\n\nload errors:\n" + ("\n".join(errs) or "(none)")


@tool("tool_read", "Read a brain tool's source file.", {"file": "file stem in brain/tools"}, group="brain")
def tool_read(ctx, file: str):
    p = TOOLS / f"{_pyname(file)}.py"
    if not p.exists():
        return f"no such tool file; have: {[x.stem for x in TOOLS.glob('*.py')]}"
    return p.read_text(encoding="utf-8")


@tool("tool_write", "Write a new Python tool into brain/tools/<file>.py. It hot-loads on the next step. The file must define functions decorated with @tool(name, description, {arg: doc}) taking (ctx, ...). ctx.bridge.call(method, **params) reaches RimBridge; ctx.knowledge has wiki/source; ctx.log(text) logs. Errors show in tool_list.", {"file": "file stem (snake_case)", "code": "full Python source"}, group="brain")
def tool_write(ctx, file: str, code: str):
    p = TOOLS / f"{_pyname(file)}.py"
    if "from rimagent.registry import tool" not in code and "@tool(" in code:
        code = "from rimagent.registry import tool\n" + code
    p.write_text(code, encoding="utf-8")
    ctx.registry.reload_brain()
    err = ctx.registry.load_errors.get(p.name)
    ctx.emit("brain_change", {"kind": "tool", "name": p.stem, "action": "write", "error": err})
    if err:
        return f"wrote {p.name} but it failed to load:\n{err}"
    names = [t.name for t in ctx.registry.tools.values() if getattr(t.fn, "_brain_file", None) == p.stem]
    if not names:
        return (f"wrote {p.name}: it parsed with no syntax error, but registered ZERO tools. "
                f"This is a failure, not a success: your file has no function decorated with @tool(...), "
                f"or the decorator/registry import is missing. Nothing is callable. Fix it now.")
    return f"wrote {p.name}; registered tools: {names}. They are callable from the next step."


@tool("tool_delete", "Delete a brain tool file.", {"file": "file stem"}, group="brain")
def tool_delete(ctx, file: str):
    p = TOOLS / f"{_pyname(file)}.py"
    if p.exists():
        p.unlink()
        ctx.registry.reload_brain()
        ctx.emit("brain_change", {"kind": "tool", "name": p.stem, "action": "delete"})
        return "deleted"
    return "no such file"


# ---- watchers ----

WATCHER_DOC = (
    "Watchers are fast reflexes that run every ~0.5s of real time WITHOUT the LLM. brain/watchers/<file>.py must define "
    "`def watch(ctx, events): ...` returning a list of dicts. `events` are new ledger events since the last poll. Return "
    "{'type':'action','method':'ui.draft','params':{...},'note':'why'} to act directly through the bridge, or "
    "{'type':'alert','text':'...','wake':True} to wake the planner. Keep them cheap: prefer reacting to events over polling "
    "state every tick; use ctx.state (last game.status) and ctx.bridge.call sparingly. A watcher that raises is disabled until edited."
)


@tool("watcher_list", "List watchers (reflexes) and their load/runtime errors. " + WATCHER_DOC, group="brain")
def watcher_list(ctx):
    reg = ctx.registry
    sup = getattr(reg, "watcher_superseded", {}) or {}
    names = [f"- {n}" + (f"  (SUPERSEDED by the {sup[n]} standing order: skipped while that order is on)" if n in sup else "") for n in reg.watchers]
    errs = [f"- {f}: {e.strip().splitlines()[-1]}" for f, e in reg.watcher_errors.items()]
    out = "watchers:\n" + ("\n".join(names) or "(none)") + "\n\nerrors:\n" + ("\n".join(errs) or "(none)")
    live = [n for n in sup if n in reg.watchers]
    if live:
        out += ("\n\nsuperseded (the mod's standing orders do this now; a watcher that drafts, rescues, unforbids, buries, assigns beds or flips "
                "food policy leaves manual touches that pause the order for those very pawns): " + ", ".join(f"{n} -> {sup[n]}" for n in live)
                + ". watcher_delete each, or watcher_write it as alert-only (either lifts the mark; the watcher then runs again).")
    return out


@tool("watcher_read", "Read a watcher's source.", {"file": "file stem"}, group="brain")
def watcher_read(ctx, file: str):
    p = WATCHERS / f"{_pyname(file)}.py"
    return p.read_text(encoding="utf-8") if p.exists() else f"no such watcher; have: {[x.stem for x in WATCHERS.glob('*.py')]}"


@tool("watcher_write", "Create/overwrite a watcher (reflex). " + WATCHER_DOC, {"file": "file stem", "code": "Python source defining watch(ctx, events)"}, group="brain")
def watcher_write(ctx, file: str, code: str):
    p = WATCHERS / f"{_pyname(file)}.py"
    p.write_text(code, encoding="utf-8")
    ctx.registry.release_watcher(p.stem)
    ctx.registry.reload_brain()
    err = ctx.registry.watcher_errors.get(p.name)
    ctx.emit("brain_change", {"kind": "watcher", "name": p.stem, "action": "write", "error": err})
    return f"wrote {p.name}" + (f" but it failed to load:\n{err}" if err else "; active from the next poll.")


@tool("watcher_delete", "Delete a watcher.", {"file": "file stem"}, group="brain")
def watcher_delete(ctx, file: str):
    p = WATCHERS / f"{_pyname(file)}.py"
    if p.exists():
        p.unlink()
        ctx.registry.release_watcher(p.stem)
        ctx.registry.reload_brain()
        ctx.emit("brain_change", {"kind": "watcher", "name": p.stem, "action": "delete"})
        return "deleted"
    return "no such file"


# ---- memory ----

@tool("notebook_read", "Read the colony notebook (your working memory for THIS game).", group="brain")
def notebook_read(ctx):
    return memory.notebook_read() or "(empty)"


@tool("notebook_write", "Replace the colony notebook. Keep it under ~6000 chars: current plan, pawn roles, threats, open problems, what to check next.", {"text": "full markdown text"}, group="brain")
def notebook_write(ctx, text: str):
    memory.notebook_write(text)
    ctx.emit("brain_change", {"kind": "notebook", "action": "write"})
    return f"notebook is now {len(text)} chars"


@tool("notebook_append", "Append a short note to the colony notebook.", {"text": "note"}, group="brain")
def notebook_append(ctx, text: str):
    memory.notebook_append(text)
    ctx.emit("brain_change", {"kind": "notebook", "action": "append"})
    return "appended"


@tool("journal_read", "Read the cross-game journal (lessons that survive between games).", {"last_n": "entries"}, group="brain")
def journal_read(ctx, last_n: int = 40):
    return memory.journal_read(last_n) or "(empty)"


@tool("journal_append", "Append a lesson to the cross-game journal. Only durable, general lessons, not colony-specific details.", {"title": "short title (optional; derived from the text if omitted)", "text": "the lesson"}, group="brain")
def journal_append(ctx, text: str, title: str | None = None):
    title = title or text.strip().splitlines()[0][:80]
    memory.journal_append(title, text, ctx.episode)
    ctx.emit("brain_change", {"kind": "journal", "action": "append", "title": title})
    return "recorded"


# ---- scores & git ----

@tool("score_history", "Past episodes with scores, seeds, and the brain commit they ran on.", group="brain")
def score_history(ctx, last: int = 12):
    return scorecard.history_text(last)


@tool("brain_log", "git log of brain/ (skills, tools, watchers, memory), one commit per episode.", group="brain")
def brain_log(ctx, n: int = 15):
    return braingit.log(n)


@tool("brain_diff", "Show what changed in a brain commit.", {"sha": "commit"}, group="brain")
def brain_diff(ctx, sha: str):
    return braingit.diff(sha)


@tool("brain_revert", "Restore brain/ to an earlier commit (when a change made play worse). Creates a new commit; history is kept.", {"sha": "commit to restore"}, group="brain")
def brain_revert(ctx, sha: str):
    new = braingit.revert_brain_to(sha)
    ctx.registry.reload_brain()
    ctx.emit("brain_change", {"kind": "git", "action": "revert", "sha": sha})
    return f"brain restored to {sha[:7]} (commit {new[:7]})"
