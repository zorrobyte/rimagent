from types import SimpleNamespace

import pytest

from rimagent import llm as llm_mod


class FakeCompletions:
    def __init__(self):
        self.kwargs = None

    def create(self, **kwargs):
        self.kwargs = kwargs
        msg = SimpleNamespace(content="ok", tool_calls=None, model_dump=lambda: {"content": "ok"})
        return SimpleNamespace(choices=[SimpleNamespace(message=msg)], usage=None)


def make(monkeypatch, max_tokens):
    cfg = {"base_url": "http://x/v1", "model": "m", "api_key": "k", "capture": False}
    if max_tokens != "unset":
        cfg["max_tokens"] = max_tokens
    monkeypatch.setitem(llm_mod.CONFIG, "llm", cfg)
    client = llm_mod.LLM()
    fake = FakeCompletions()
    client.client = SimpleNamespace(chat=SimpleNamespace(completions=fake))
    return client, fake


@pytest.mark.parametrize("value", [None, 0])
def test_no_cap_sends_no_max_tokens(monkeypatch, value):
    client, fake = make(monkeypatch, value)
    client.chat([{"role": "user", "content": "hi"}])
    assert "max_tokens" not in fake.kwargs


def test_configured_cap_is_sent(monkeypatch):
    client, fake = make(monkeypatch, 16000)
    client.chat([{"role": "user", "content": "hi"}])
    assert fake.kwargs["max_tokens"] == 16000


def test_default_cap_is_unchanged(monkeypatch):
    client, fake = make(monkeypatch, "unset")
    client.chat([{"role": "user", "content": "hi"}])
    assert fake.kwargs["max_tokens"] == 4000


def test_per_call_cap_wins(monkeypatch):
    client, fake = make(monkeypatch, None)
    client.chat([{"role": "user", "content": "hi"}], max_tokens=400)
    assert fake.kwargs["max_tokens"] == 400
