"""A brain commit is authored by the agent, and committed by whoever runs it."""
from __future__ import annotations

from rimagent import braingit


def test_brain_commit_is_authored_by_the_agent(monkeypatch):
    sent: list[tuple[str, ...]] = []
    monkeypatch.setattr(braingit, "_git", lambda *a: sent.append(a) or "abc1234")
    braingit.commit("episode 1 day 4: improvement pass")
    commit_args = next(a for a in sent if a[0] == "commit")
    assert commit_args[commit_args.index("--author") + 1] == braingit.AUTHOR
    assert "--committer" not in commit_args
