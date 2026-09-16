"""Shared context handed to every tool and watcher."""
from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field
from typing import Any, Callable

from .bridge import Bridge
from .llm import LLM
from .registry import Registry


@dataclass
class WakePlan:
    in_hours: float | None = None
    on_kinds: list[str] = field(default_factory=list)
    notes: str = ""


@dataclass
class Context:
    bridge: Bridge
    llm: LLM
    registry: Registry
    config: dict[str, Any]
    emit: Callable[[str, dict[str, Any]], None] = lambda kind, data: None
    episode: int = 0
    seed: str = ""
    last_seq: int = 0
    # per think-step control flags set by meta tools
    stop_turn: bool = False
    wake: WakePlan = field(default_factory=WakePlan)
    end_episode_reason: str | None = None
    recent_events: list[dict[str, Any]] = field(default_factory=list)
    watcher_alerts: list[dict[str, Any]] = field(default_factory=list)
    lock: threading.RLock = field(default_factory=threading.RLock)
    started_at: float = field(default_factory=time.time)
    extra: dict[str, Any] = field(default_factory=dict)
    # set by the runner: returns urgent events that arrived while a step is running (already logged/tracked)
    interrupt_check: Callable[[], list[str]] | None = None
    stream: str = "play"   # which LLM stream this context drives (play | improve | reflect)

    def fork(self, stream: str) -> "Context":
        """A sibling context sharing bridge/llm/registry/emit but with its own turn flags (for a concurrent stream)."""
        import copy
        c = copy.copy(self)
        c.stream = stream
        c.extra = {k: v for k, v in self.extra.items() if k != "operator_inbox"}
        c.interrupt_check = None
        c.recent_events = list(self.recent_events)
        c.reset_turn()
        return c

    def reset_turn(self) -> None:
        self.stop_turn = False
        self.wake = WakePlan()

    def log(self, text: str, **data: Any) -> None:
        self.emit("log", {"text": text, **data})
