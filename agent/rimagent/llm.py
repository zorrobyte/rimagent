"""OpenAI-compatible chat client for the OpenAI-compatible endpoint (Qwen with thinking + native tool calls)."""
from __future__ import annotations

import hashlib
import json
import os
import re
import threading
import time
from dataclasses import dataclass, field
from typing import Any

from openai import OpenAI

from .config import CONFIG
from .paths import SFT_RAW

_TOOLS_DIR = SFT_RAW / "tools"
_TOOLS_DIR.mkdir(parents=True, exist_ok=True)


_LEAKED_TOKEN = re.compile(r"<\|[^<>|]{0,32}\|>")


def _delk(s: str) -> str:
    """Hacky cleanup: DiffusionGemma occasionally leaks raw special-token markers like <|"|> into its
    text/tool-arg output, which breaks downstream JSON/kv parsing (e.g. end_turn notes). Strip them."""
    return _LEAKED_TOKEN.sub("", s) if s else s


def _tools_ref(tools: list[dict[str, Any]] | None) -> str | None:
    """Dedup tool schemas (repeated near-identically on every call) into sft/raw/tools/<hash>.json, return the hash."""
    if not tools:
        return None
    blob = json.dumps(tools, sort_keys=True).encode()
    h = hashlib.sha256(blob).hexdigest()[:16]
    p = _TOOLS_DIR / f"{h}.json"
    if not p.exists():
        p.write_bytes(blob)
    return h


@dataclass
class LLMReply:
    content: str = ""
    reasoning: str = ""
    tool_calls: list[dict[str, Any]] = field(default_factory=list)
    raw_message: dict[str, Any] = field(default_factory=dict)
    usage: dict[str, Any] = field(default_factory=dict)
    elapsed: float = 0.0


class LLM:
    def __init__(self):
        c = CONFIG["llm"]
        self.model = c["model"]
        self.client = OpenAI(base_url=c["base_url"], api_key=c["api_key"], timeout=c.get("timeout_s", 240), max_retries=0)  # the loop handles retries; a hung socket must not freeze play
        self._sem = threading.Semaphore(int(c.get("max_streams", 4)))
        self.default_thinking = bool(c.get("thinking", True))
        self.max_tokens = int(c.get("max_tokens", 4000))
        self.omit_sampling_params = bool(c.get("omit_sampling_params", False))  # some backends (e.g. diffusion models) reject temperature/seed/etc.
        self._capture_fh = None
        if bool(c.get("capture", True)):
            self._capture_fh = (SFT_RAW / f"capture-{time.strftime('%Y%m%d-%H%M%S')}-{os.getpid()}.jsonl").open("a", encoding="utf-8")
        self._capture_lock = threading.Lock()

    def _capture(self, messages: list[dict[str, Any]], tools: list[dict[str, Any]] | None, reply: "LLMReply", meta: dict[str, Any] | None) -> None:
        if not self._capture_fh:
            return
        rec = {"t": time.time(), "model": self.model, "meta": meta or {}, "messages": messages,
               "tools_ref": _tools_ref(tools), "reply": {"content": reply.content, "reasoning": reply.reasoning, "tool_calls": reply.tool_calls},
               "usage": reply.usage, "elapsed": reply.elapsed}
        try:
            with self._capture_lock:
                self._capture_fh.write(json.dumps(rec, default=str) + "\n")
                self._capture_fh.flush()
        except Exception:  # noqa: BLE001 — capture must never break play
            pass

    def chat(self, messages: list[dict[str, Any]], tools: list[dict[str, Any]] | None = None, thinking: bool | None = None, max_tokens: int | None = None, temperature: float = 0.6, tool_choice: str | None = None, meta: dict[str, Any] | None = None) -> LLMReply:
        thinking = self.default_thinking if thinking is None else thinking
        kwargs: dict[str, Any] = {
            "model": self.model,
            "messages": messages,
            "max_tokens": max_tokens or self.max_tokens,
            "extra_body": {"chat_template_kwargs": {"enable_thinking": thinking}},
        }
        if not self.omit_sampling_params:
            kwargs["temperature"] = temperature
        if tools:
            kwargs["tools"] = tools
            if tool_choice:
                kwargs["tool_choice"] = tool_choice
        t0 = time.time()
        with self._sem:
            resp = self.client.chat.completions.create(**kwargs)
        msg = resp.choices[0].message
        reply = LLMReply(elapsed=time.time() - t0)
        reply.content = _delk(msg.content or "")
        raw = msg.model_dump()
        reply.raw_message = raw
        reply.reasoning = _delk(raw.get("reasoning_content") or raw.get("reasoning") or "")
        if msg.tool_calls:
            for tc in msg.tool_calls:
                try:
                    args = json.loads(_delk(tc.function.arguments or "{}"))
                except json.JSONDecodeError:
                    args = {"_raw": tc.function.arguments}
                reply.tool_calls.append({"id": tc.id, "name": tc.function.name, "arguments": args})
        if resp.usage:
            reply.usage = resp.usage.model_dump()
        # Qwen sometimes returns nothing after a long think; retry once without thinking.
        if thinking and not reply.content.strip() and not reply.tool_calls:
            return self.chat(messages, tools, thinking=False, max_tokens=max_tokens, temperature=temperature, tool_choice=tool_choice, meta=meta)
        self._capture(messages, tools, reply, meta)
        return reply

    def assistant_message(self, reply: LLMReply) -> dict[str, Any]:
        """The assistant turn to append to history (tool calls preserved, reasoning dropped)."""
        m: dict[str, Any] = {"role": "assistant", "content": reply.content or ""}
        if reply.tool_calls:
            m["tool_calls"] = [{"id": tc["id"], "type": "function", "function": {"name": tc["name"], "arguments": json.dumps(tc["arguments"])}} for tc in reply.tool_calls]
        return m
