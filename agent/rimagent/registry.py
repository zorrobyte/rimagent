"""Tool registry: built-in tools, bridge-generated tools, and hot-loaded brain tools.

A tool is a Python callable `fn(ctx, **kwargs) -> Any`. `ctx` is the Context (bridge, llm, brain helpers, emit).
The registry produces OpenAI tool specs and executes calls with error capture.
"""
from __future__ import annotations

import hashlib
import importlib.util
import inspect
import json
import re
import sys
import traceback
import typing
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable

from .paths import TOOLS, WATCHERS

_TYPE_MAP = {str: "string", int: "integer", float: "number", bool: "boolean", list: "array", dict: "object"}

# Tool groups that are never handed out unless a stream asks for them by name (see Registry.specs).
# "watchdog": reads and patches the project's own source; only the watchdog stream may ever see these.
# "dev": every one of its 13 tools -- reveal_map, god_mode, spawn,
# unlock_all_research -- was offered to the play model. The bridge gates them on Prefs.DevMode and stamps the
# game assisted on first use, so nothing was hidden; but on a machine where dev mode is habitually on, the play
# stream could cheat and only a ledger flag would record it. A sandbox episode unlocks dev deliberately.
RESERVED_GROUPS: set[str] = {"watchdog", "dev"}


def _schema_from_signature(fn: Callable) -> dict[str, Any]:
    sig = inspect.signature(fn)
    hints = typing.get_type_hints(fn) if hasattr(fn, "__annotations__") else {}
    props: dict[str, Any] = {}
    required: list[str] = []
    for name, p in sig.parameters.items():
        if name == "ctx" or p.kind in (p.VAR_POSITIONAL, p.VAR_KEYWORD):
            continue
        t = hints.get(name, str)
        origin = typing.get_origin(t)
        if origin is typing.Union or (origin is not None and str(origin) == "<class 'types.UnionType'>"):
            args = [a for a in typing.get_args(t) if a is not type(None)]
            t = args[0] if args else str
            origin = typing.get_origin(t)
        js: dict[str, Any]
        if origin in (list, typing.List):
            js = {"type": "array"}
        elif origin in (dict, typing.Dict):
            js = {"type": "object"}
        else:
            js = {"type": _TYPE_MAP.get(t, "string")}
        doc = getattr(fn, "_param_docs", {}).get(name)
        if doc:
            js["description"] = doc
        props[name] = js
        if p.default is inspect.Parameter.empty:
            required.append(name)
    return {"type": "object", "properties": props, "required": required}


@dataclass
class Tool:
    name: str
    description: str
    fn: Callable
    schema: dict[str, Any]
    source: str = "builtin"  # builtin | bridge | brain
    group: str = "general"

    def spec(self) -> dict[str, Any]:
        return {"type": "function", "function": {"name": self.name, "description": self.description[:1024], "parameters": self.schema}}


def tool(name: str | None = None, description: str | None = None, params: dict[str, str] | None = None, group: str = "general", **aliases: dict[str, str]):
    """Decorator for built-in and brain tools. `params` maps arg name -> description.
    Brain-authored tools sometimes guess a different keyword for the same thing (most often
    `args=`); accept it rather than fail to load over a naming choice the docs never forbade."""
    if params is None:
        for alt in ("args", "arguments", "argdocs", "parameters"):
            if alt in aliases:
                params = aliases.pop(alt)
                break
    if aliases:
        raise TypeError(f"tool() got unexpected keyword argument(s): {', '.join(aliases)}")

    def deco(fn: Callable) -> Callable:
        fn._tool_name = name or fn.__name__  # type: ignore[attr-defined]
        fn._tool_desc = description or (fn.__doc__ or "").strip()  # type: ignore[attr-defined]
        fn._param_docs = params or {}  # type: ignore[attr-defined]
        fn._tool_group = group  # type: ignore[attr-defined]
        return fn

    return deco


@dataclass
class Registry:
    tools: dict[str, Tool] = field(default_factory=dict)
    load_errors: dict[str, str] = field(default_factory=dict)
    # Content digest per brain file, not mtime. See _load_file.
    _digests: dict[str, str] = field(default_factory=dict)
    watchers: dict[str, Callable] = field(default_factory=dict)
    watcher_errors: dict[str, str] = field(default_factory=dict)
    # watcher stem -> standing order id that replaced it; run_all skips these while the runner keeps the mapping
    # (separate from watcher_errors: those render as load errors and are cleared on every successful reload)
    watcher_superseded: dict[str, str] = field(default_factory=dict)
    # stems the director rewrote or deleted through watcher_write/watcher_delete: never superseded again this process
    watcher_kept: set[str] = field(default_factory=set)

    # ---- registration ----
    def add(self, fn: Callable, source: str = "builtin") -> Tool:
        name = getattr(fn, "_tool_name", fn.__name__)
        t = Tool(name=name, description=getattr(fn, "_tool_desc", ""), fn=fn, schema=_schema_from_signature(fn), source=source, group=getattr(fn, "_tool_group", "general"))
        self.tools[name] = t
        return t

    def add_module(self, module: Any, source: str = "builtin") -> list[str]:
        names = []
        for _, obj in inspect.getmembers(module, inspect.isfunction):
            if hasattr(obj, "_tool_name"):
                self.add(obj, source)
                names.append(obj._tool_name)
        return names

    def add_bridge_methods(self, methods: list[dict[str, str]]) -> None:
        """One tool per RimBridge RPC method (dots -> underscores, prefixed rw_). Docs in BRIDGE_DOC_NOTES are appended."""
        for m in methods:
            method = m["method"]
            name = "rw_" + method.replace(".", "_")
            doc = m.get("doc", "")

            def make(method_name: str):
                def fn(ctx, **params):
                    params = {k: coerce_param(v) for k, v in params.items()}
                    result = ctx.bridge.call(method_name, **params)
                    if method_name in ("game.speed", "game.pause"):
                        # remember the model's explicit choice so the runner keeps it after the step
                        ctx.extra["model_speed"] = 0 if (method_name == "game.pause" and params.get("paused", True)) else int(params.get("speed", 1) if method_name == "game.speed" else ctx.extra.get("model_speed", 1))
                    return result
                return fn

            self.tools[name] = Tool(
                name=name,
                description=f"[RimBridge {method}] {doc}",
                fn=make(method),
                schema={"type": "object", "properties": {}, "additionalProperties": True, "description": "params as described: " + doc[:600]},
                source="bridge",
                group=method.split(".")[0],
            )
        self.annotate_bridge_docs()

    def annotate_bridge_docs(self, notes: dict[str, str] | None = None) -> None:
        """Append agent-side notes to bridge tool descriptions (and their schema hint) without touching the call path."""
        for method, note in (notes or BRIDGE_DOC_NOTES).items():
            t = self.tools.get("rw_" + method.replace(".", "_"))
            if t is None or t.source != "bridge" or note in t.description:
                continue
            t.description = (t.description.rstrip() + " " + note).strip()
            t.schema = {**t.schema, "description": (t.schema.get("description", "") + " " + note)[:900]}

    # ---- hot loading ----
    def reload_brain(self) -> None:
        """(Re)load brain/tools/*.py and brain/watchers/*.py when their mtime changed."""
        for path in sorted(TOOLS.glob("*.py")):
            self._load_file(path, kind="tool")
        for path in sorted(WATCHERS.glob("*.py")):
            self._load_file(path, kind="watcher")
        # drop tools whose files vanished
        live = {p.stem for p in TOOLS.glob("*.py")}
        for name, t in list(self.tools.items()):
            if t.source == "brain" and getattr(t.fn, "_brain_file", None) not in live:
                del self.tools[name]
        live_w = {p.stem for p in WATCHERS.glob("*.py")}
        for name in list(self.watchers):
            if name not in live_w:
                del self.watchers[name]
        for name in list(self.watcher_superseded):
            if name not in live_w:
                del self.watcher_superseded[name]

    def _load_file(self, path: Path, kind: str) -> None:
        """Load a brain file if its CONTENT changed since the last load.

        This compared st_mtime, and mtime is not a change signal. On Windows the file-time clock advances about
        every 15 ms: 200 writes to one file measured here produced 15 distinct mtimes, so two writes to the same
        file in quick succession are indistinguishable and the second one never loads. The agent edits its own
        tools and watchers, so that is the shape of it fixing something it just wrote -- and the failure is
        silent: tool_write reports which tools registered, and they can be the previous version's. mtime also
        moves for reasons that have nothing to do with content: a checkout, a copy that preserves timestamps, an
        unpacked archive.

        The source is then compiled here rather than through loader.exec_module, because __pycache__ validates a
        .pyc against the source's mtime AND SIZE -- the same weak signal again. A one-character fix under an
        unchanged mtime is the same size, so exec_module hands back the previous version's bytecode. Compiling
        the bytes already read has no cache to go stale.

        Cost: 18 brain files, 66 KB, 1.84 ms per reload_brain(), against LLM calls measured in seconds.
        """
        key = f"{kind}:{path.name}"
        try:
            src = path.read_bytes()
        except OSError:
            return  # vanished between the glob and here; reload_brain drops it below
        digest = hashlib.blake2b(src, digest_size=16).hexdigest()
        if self._digests.get(key) == digest:
            return
        self._digests[key] = digest
        modname = f"brain_{kind}_{re.sub(r'[^a-zA-Z0-9_]', '_', path.stem)}"
        try:
            spec = importlib.util.spec_from_file_location(modname, path)
            mod = importlib.util.module_from_spec(spec)  # type: ignore[arg-type]
            sys.modules[modname] = mod
            exec(compile(src, str(path), "exec"), mod.__dict__)  # noqa: S102
        except Exception:  # noqa: BLE001
            err = traceback.format_exc(limit=3)
            (self.load_errors if kind == "tool" else self.watcher_errors)[path.name] = err
            return
        (self.load_errors if kind == "tool" else self.watcher_errors).pop(path.name, None)
        if kind == "tool":
            # remove previous tools from this file
            for name, t in list(self.tools.items()):
                if t.source == "brain" and getattr(t.fn, "_brain_file", None) == path.stem:
                    del self.tools[name]
            for _, obj in inspect.getmembers(mod, inspect.isfunction):
                if hasattr(obj, "_tool_name"):
                    obj._brain_file = path.stem  # type: ignore[attr-defined]
                    self.add(obj, source="brain")
        else:
            fn = getattr(mod, "watch", None)
            if callable(fn):
                self.watchers[path.stem] = fn
            else:
                self.watcher_errors[path.name] = "no watch(ctx, events) function"

    def set_superseded(self, mapping: dict[str, str]) -> dict[str, str]:
        """Replace the superseded-watcher mapping (stem -> order id). Stems the director rewrote/deleted (`watcher_kept`)
        are never re-superseded; the mapping is not limited to loaded watchers (a file written later is covered too)."""
        self.watcher_superseded = {str(k): str(v) for k, v in (mapping or {}).items() if k and k not in self.watcher_kept}
        return self.watcher_superseded

    def release_watcher(self, stem: str) -> None:
        """The director edited or deleted this watcher: it runs again (if it still exists) and stays out of the superseded set."""
        self.watcher_kept.add(stem)
        self.watcher_superseded.pop(stem, None)

    # ---- execution ----
    def specs(self, groups: set[str] | None = None, exclude: set[str] | None = None, allow: Callable[[Tool], bool] | None = None, unlock: set[str] | None = None) -> list[dict[str, Any]]:
        """Tool specs for one stream. Groups in RESERVED_GROUPS are opt-in: a caller that passes no `groups` at all
        (the play step, which wants everything) still does not get them. They are privileged by design and belong to
        exactly one stream, which asks for them by name. `unlock` adds a reserved group to whatever the caller would
        otherwise get, for a stream that keeps its normal tools and needs one privileged group as well."""
        out = []
        for t in self.tools.values():
            if t.group in RESERVED_GROUPS and not (groups and t.group in groups) and not (unlock and t.group in unlock):
                continue
            if groups and t.group not in groups and t.source != "brain":
                continue
            if exclude and t.name in exclude:
                continue
            if allow is not None and not allow(t):
                continue
            out.append(t.spec())
        return out

    def execute(self, ctx: Any, name: str, args: dict[str, Any]) -> tuple[Any, bool]:
        """Returns (result, ok). Never raises."""
        t = self.tools.get(name)
        if t is None:
            return {"error": f"unknown tool {name!r}"}, False
        try:
            if t.source == "bridge":
                return t.fn(ctx, **args), True
            args = {k: coerce_param(v) if k not in ("code", "body", "text", "content") else v for k, v in args.items()}
            sig = inspect.signature(t.fn)
            accepts_kwargs = any(p.kind == p.VAR_KEYWORD for p in sig.parameters.values())
            if not accepts_kwargs:
                args = remap_params(sig, args)
                unknown = [k for k in args if k not in sig.parameters]
                if unknown:
                    return {"error": f"unknown parameter(s) {unknown} for {name}; accepted: {[p for p in sig.parameters if p != 'ctx']}"}, False
            return t.fn(ctx, **args), True
        except Exception as e:  # noqa: BLE001
            tb = traceback.format_exc(limit=4)
            return {"error": f"{type(e).__name__}: {e}", "trace": tb[-1200:] if t.source == "brain" else None}, False


# Agent-side additions to bridge tool docs: what the steward already does, so the director does not undercut it.
BRIDGE_DOC_NOTES: dict[str, str] = {
    "ui.set_work": "NOTE: takes the pawn out of steward management (the steward stops setting its priorities until rw_steward_pawn managed=true); prefer rw_steward_posture for a temporary bias.",
    "ui.set_work_many": "NOTE: takes every listed pawn out of steward management (rw_steward_pawn managed=true hands one back); prefer rw_steward_posture for a temporary bias.",
    "ui.designate": "NOTE: the steward's stock jobs already designate trees/plants/animals/ore toward their targets; prefer rw_steward_stock_set (raise the target) over hand designation while that job is enabled. For a one-off tree use designator=cut (forestry adopts harvestwood designations and releases them when its target is met).",
    "ui.draft": "NOTE: the combat standing order drafts and positions colonists at the rally point by itself; a pawn you draft/move by hand is hands-off for that order for a while (rw_steward_orders_explain combat lists them). Prefer rw_steward_orders_rally to move everyone.",
    "ui.goto": "NOTE: a pawn you move by hand is hands-off for the combat standing order for a while; to move the whole defense set the rally rect with rw_steward_orders_rally.",
    "ui.attack": "NOTE: a pawn you order to attack by hand is hands-off for the combat standing order for a while; use it to override (breachers, sappers, drop pods inside), not for every fight.",
    "steward.settings": "NOTE: writes the RimBridge mod settings file, which persists across games and episodes (the notebook does not); prefer rw_steward_posture for a per-colony bias. A JSON null removes a globalWorkAdjustments key.",
}

_ALIASES = {"content": "text", "body": "text", "markdown": "text", "note": "text", "message": "text", "notes": "text", "filename": "file", "path": "file", "source": "code", "python": "code", "skill": "name", "title": "name", "query": "q"}


def remap_params(sig: inspect.Signature, args: dict[str, Any]) -> dict[str, Any]:
    """Be lenient about parameter names: known aliases, then a single unknown -> single missing required."""
    params = [p for p in sig.parameters if p != "ctx"]
    out = dict(args)
    for k in list(out):
        if k not in params and k in _ALIASES and _ALIASES[k] in params and _ALIASES[k] not in out:
            out[_ALIASES[k]] = out.pop(k)
    unknown = [k for k in out if k not in params]
    missing = [p for p in params if p not in out and sig.parameters[p].default is inspect.Parameter.empty]
    if len(unknown) == 1 and len(missing) == 1:
        out[missing[0]] = out.pop(unknown[0])
    return out


def coerce_param(v: Any) -> Any:
    """Models often send JSON values as strings ("[97, 98]", "true", "80"). Undo that where it is unambiguous."""
    if isinstance(v, str):
        t = v.strip()
        if t and (t[0] in "[{" or t in ("true", "false", "null") or re.fullmatch(r"-?\d+(\.\d+)?", t)):
            try:
                return json.loads(t)
            except json.JSONDecodeError:
                return v
        return v
    if isinstance(v, list):
        return [coerce_param(x) for x in v]
    if isinstance(v, dict):
        return {k: coerce_param(x) for k, x in v.items()}
    return v


def to_text(result: Any, limit: int = 8000) -> str:
    if isinstance(result, str):
        s = result
    else:
        try:
            s = json.dumps(result, ensure_ascii=False, default=str)
        except Exception:  # noqa: BLE001
            s = str(result)
    if len(s) > limit:
        s = s[:limit] + f"\n…(truncated {len(s) - limit} chars; narrow the query)"
    return s
