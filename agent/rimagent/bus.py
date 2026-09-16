"""In-process event bus shared by the runner (producer) and the dashboard (consumer).

Every event is {"seq": int, "t": epoch seconds, "kind": str, "data": dict}. Kinds emitted by the runner/loop:
  status        data: game.status dict + {"episode", "seed", "phase": idle|thinking|playing|reflecting|loading}
  think_start   data: {"trigger": str, "step": int}
  reasoning     data: {"text": str}                         model thinking text (may be long)
  assistant     data: {"text": str}                         model visible text
  tool_call     data: {"name": str, "args": dict, "id": str}
  tool_result   data: {"name": str, "id": str, "ok": bool, "text": str (truncated), "elapsed": float}
  think_end     data: {"notes": str, "wake": {...}, "calls": int, "elapsed": float}
  ledger        data: one game ledger event (kind, text, tick, day, ...)
  watcher       data: {"name": str, "action"|"alert"|"error": ...}
  brain_change  data: {"kind": skill|tool|watcher|notebook|journal|git, "name"?: str, "action": str}
  episode_start data: {"episode": int, "seed": str}
  episode_end   data: {"episode": int, "score": float, "reason": str, "assisted": bool, "brain_sha": str}
  operator      data: {"text": str}                         message typed by the human on the dashboard
  reply         data: {"text": str}                         the agent's reply_to_operator
  log           data: {"text": str, ...}
  error         data: {"text": str}
"""
from __future__ import annotations

import collections
import json
import threading
import time
from typing import Any, Callable, Deque

from .paths import RUNS


class Bus:
    def __init__(self, capacity: int = 5000, log_file: bool = True):
        self._events: Deque[dict[str, Any]] = collections.deque(maxlen=capacity)
        self._seq = 0
        self._lock = threading.Lock()
        self._cond = threading.Condition(self._lock)
        self._subs: list[Callable[[dict[str, Any]], None]] = []
        self.state: dict[str, Any] = {"phase": "idle"}
        self._fh = (RUNS / f"run-{time.strftime('%Y%m%d-%H%M%S')}.jsonl").open("a", encoding="utf-8") if log_file else None

    def emit(self, kind: str, data: dict[str, Any] | None = None) -> dict[str, Any]:
        with self._lock:
            self._seq += 1
            ev = {"seq": self._seq, "t": time.time(), "kind": kind, "data": data or {}}
            self._events.append(ev)
            if kind == "status":
                self.state.update(ev["data"])
            self._cond.notify_all()
        if self._fh:
            try:
                self._fh.write(json.dumps(ev, default=str) + "\n")
                self._fh.flush()
            except Exception:  # noqa: BLE001
                pass
        for s in list(self._subs):
            try:
                s(ev)
            except Exception:  # noqa: BLE001
                pass
        return ev

    def since(self, seq: int, limit: int = 500, kinds: set[str] | None = None) -> list[dict[str, Any]]:
        with self._lock:
            out = [e for e in self._events if e["seq"] > seq and (kinds is None or e["kind"] in kinds)]
        return out[-limit:]

    def wait(self, seq: int, timeout: float = 15.0) -> list[dict[str, Any]]:
        """Block until an event newer than seq arrives (or timeout); returns new events."""
        with self._cond:
            if not (self._events and self._events[-1]["seq"] > seq):
                self._cond.wait(timeout)
            return [e for e in self._events if e["seq"] > seq]

    def subscribe(self, fn: Callable[[dict[str, Any]], None]) -> None:
        self._subs.append(fn)

    @property
    def last_seq(self) -> int:
        return self._seq


BUS = Bus()
