"""git helpers for the brain directory (the agent commits its own changes per episode)."""
from __future__ import annotations

import subprocess

from .paths import BRAIN, ROOT


def _git(*args: str) -> str:
    r = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, timeout=60)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.strip() or r.stdout.strip())
    return r.stdout.strip()


# The agent is not the person running it. Without this, everything it writes to
# brain/ is authored as whoever configured git on the machine, and `git log`
# cannot tell what the model wrote from what a human did. Author only: the
# committer stays the human, which is accurate -- their machine made the commit.
AUTHOR = "rimagent (brain) <rimagent@rimagent.invalid>"


def commit(message: str) -> str | None:
    _git("add", "-A", str(BRAIN.relative_to(ROOT)))
    try:
        _git("commit", "-q", "--author", AUTHOR, "-m", message, "--", str(BRAIN.relative_to(ROOT)))
    except RuntimeError as e:
        if "nothing to commit" in str(e) or "no changes added" in str(e):
            return None
        raise
    return head()


def head() -> str:
    try:
        return _git("rev-parse", "HEAD")
    except RuntimeError:
        return ""


def log(n: int = 15) -> str:
    try:
        return _git("log", f"-{n}", "--date=short", "--pretty=%h %ad %s", "--", str(BRAIN.relative_to(ROOT)))
    except RuntimeError as e:
        return f"(no history: {e})"


def diff(sha: str) -> str:
    try:
        return _git("show", "--stat", "--patch", "--no-color", sha, "--", str(BRAIN.relative_to(ROOT)))[:20000]
    except RuntimeError as e:
        return f"(error: {e})"


def revert_brain_to(sha: str) -> str:
    """Restore brain/ to the state at `sha` (as a new commit), keeping history."""
    _git("checkout", sha, "--", str(BRAIN.relative_to(ROOT)))
    return commit(f"brain: revert to {sha[:7]}") or head()
