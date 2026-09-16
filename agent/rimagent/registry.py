"""Tool registry: built-in tools, bridge-generated tools, and hot-loaded brain tools.

A tool is a Python callable `fn(ctx, **kwargs) -> Any`. `ctx` is the Context (bridge, llm, brain helpers, emit).
The registry produces OpenAI tool specs and executes calls with error capture.
"""
from __future__ import annotations

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


def tool(name: str | None = None, description: str | None = None, params: dict[str, str] | None = None, group: str = "general"):
    """Decorator for built-in and brain tools. `params` maps arg name -> description."""

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
    _mtimes: dict[str, float] = field(default_factory=dict)
    watchers: dict[str, Callable] = field(default_factory=dict)
    watcher_errors: dict[str, str] = field(default_factory=dict)

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
        """One tool per RimBridge RPC method (dots -> underscores, prefixed rw_)."""
        for m in methods:
            method = m["method"]
            name = "rw_" + method.replace(".", "_")
            doc = m.get("doc", "")

            def make(method_name: str):
                def fn(ctx, **params):
                    return ctx.bridge.call(method_name, **{k: coerce_param(v) for k, v in params.items()})
                return fn

            self.tools[name] = Tool(
                name=name,
                description=f"[RimBridge {method}] {doc}",
                fn=make(method),
                schema={"type": "object", "properties": {}, "additionalProperties": True, "description": "params as described: " + doc[:600]},
                source="bridge",
                group=method.split(".")[0],
            )

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

    def _load_file(self, path: Path, kind: str) -> None:
        key = f"{kind}:{path.name}"
        mtime = path.stat().st_mtime
        if self._mtimes.get(key) == mtime:
            return
        self._mtimes[key] = mtime
        modname = f"brain_{kind}_{re.sub(r'[^a-zA-Z0-9_]', '_', path.stem)}"
        try:
            spec = importlib.util.spec_from_file_location(modname, path)
            mod = importlib.util.module_from_spec(spec)  # type: ignore[arg-type]
            sys.modules[modname] = mod
            spec.loader.exec_module(mod)  # type: ignore[union-attr]
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

    # ---- execution ----
    def specs(self, groups: set[str] | None = None, exclude: set[str] | None = None) -> list[dict[str, Any]]:
        out = []
        for t in self.tools.values():
            if groups and t.group not in groups and t.source != "brain":
                continue
            if exclude and t.name in exclude:
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
