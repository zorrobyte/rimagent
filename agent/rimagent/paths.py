"""Filesystem layout. Everything lives under the repo root (~/rimagent by default)."""
from __future__ import annotations

import os
from pathlib import Path

ROOT = Path(os.environ.get("RIMAGENT_ROOT", Path(__file__).resolve().parents[2]))
BRAIN = ROOT / "brain"
SKILLS = BRAIN / "skills"
TOOLS = BRAIN / "tools"
WATCHERS = BRAIN / "watchers"
MEMORY = BRAIN / "memory"
NOTEBOOK = MEMORY / "notebook.md"
JOURNAL = MEMORY / "journal.md"
OPERATOR = MEMORY / "operator.md"
SCORES = BRAIN / "scores.jsonl"
KNOWLEDGE = ROOT / "knowledge"
WIKI = KNOWLEDGE / "wiki"
SOURCE_16 = KNOWLEDGE / "source-1.6"
SOURCE_LEGACY = KNOWLEDGE / "source-legacy"
RUNS = ROOT / "runs"
MOD_LEDGER = ROOT / "mod" / "ledger.jsonl"

for _d in (SKILLS, TOOLS, WATCHERS, MEMORY, WIKI, RUNS):
    _d.mkdir(parents=True, exist_ok=True)
