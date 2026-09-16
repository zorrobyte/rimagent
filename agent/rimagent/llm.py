"""OpenAI-compatible chat client for the chaos-srv vLLM endpoint (Qwen with thinking + native tool calls)."""
from __future__ import annotations

import json
import threading
import time
from dataclasses import dataclass, field
from typing import Any

from openai import OpenAI

from .config import CONFIG


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

    def chat(self, messages: list[dict[str, Any]], tools: list[dict[str, Any]] | None = None, thinking: bool | None = None, max_tokens: int | None = None, temperature: float = 0.6, tool_choice: str | None = None) -> LLMReply:
        thinking = self.default_thinking if thinking is None else thinking
        kwargs: dict[str, Any] = {
            "model": self.model,
            "messages": messages,
            "max_tokens": max_tokens or self.max_tokens,
            "temperature": temperature,
            "extra_body": {"chat_template_kwargs": {"enable_thinking": thinking}},
        }
        if tools:
            kwargs["tools"] = tools
            if tool_choice:
                kwargs["tool_choice"] = tool_choice
        t0 = time.time()
        with self._sem:
            resp = self.client.chat.completions.create(**kwargs)
        msg = resp.choices[0].message
        reply = LLMReply(elapsed=time.time() - t0)
        reply.content = msg.content or ""
        raw = msg.model_dump()
        reply.raw_message = raw
        reply.reasoning = raw.get("reasoning_content") or raw.get("reasoning") or ""
        if msg.tool_calls:
            for tc in msg.tool_calls:
                try:
                    args = json.loads(tc.function.arguments or "{}")
                except json.JSONDecodeError:
                    args = {"_raw": tc.function.arguments}
                reply.tool_calls.append({"id": tc.id, "name": tc.function.name, "arguments": args})
        if resp.usage:
            reply.usage = resp.usage.model_dump()
        # Qwen sometimes returns nothing after a long think; retry once without thinking.
        if thinking and not reply.content.strip() and not reply.tool_calls:
            return self.chat(messages, tools, thinking=False, max_tokens=max_tokens, temperature=temperature, tool_choice=tool_choice)
        return reply

    def assistant_message(self, reply: LLMReply) -> dict[str, Any]:
        """The assistant turn to append to history (tool calls preserved, reasoning dropped)."""
        m: dict[str, Any] = {"role": "assistant", "content": reply.content or ""}
        if reply.tool_calls:
            m["tool_calls"] = [{"id": tc["id"], "type": "function", "function": {"name": tc["name"], "arguments": json.dumps(tc["arguments"])}} for tc in reply.tool_calls]
        return m
