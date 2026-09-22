"""Skill library: markdown files with frontmatter in brain/skills. Selected by relevance (BM25) or always-on."""
from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

import frontmatter
from rank_bm25 import BM25Okapi

from .paths import SKILLS

_TOKEN = re.compile(r"[a-z0-9]+")


def _tok(s: str) -> list[str]:
    return _TOKEN.findall(s.lower())


def _slug(name: str) -> str:
    s = re.sub(r"[^a-z0-9]+", "-", name.lower()).strip("-")
    return s[:80] or "skill"


@dataclass
class Skill:
    name: str
    description: str
    body: str
    always: bool
    tags: list[str]
    path: Path

    @property
    def chars(self) -> int:
        return len(self.body)


def load_all() -> list[Skill]:
    out = []
    for p in sorted(SKILLS.glob("*.md")):
        try:
            post = frontmatter.load(p)
        except Exception:  # noqa: BLE001
            continue
        out.append(Skill(
            name=str(post.get("name") or p.stem),
            description=str(post.get("description") or ""),
            body=post.content.strip(),
            always=bool(post.get("always", False)),
            tags=[str(t) for t in (post.get("tags") or [])],
            path=p,
        ))
    return out


def index_text(skills: list[Skill] | None = None) -> str:
    skills = skills if skills is not None else load_all()
    if not skills:
        return "(no skills yet)"
    return "\n".join(f"- {s.name}{' [always]' if s.always else ''}: {s.description} ({s.chars} chars)" for s in skills)


def select(query: str, k: int = 4, skills: list[Skill] | None = None) -> list[Skill]:
    skills = skills if skills is not None else load_all()
    cands = [s for s in skills if not s.always]
    if not cands or not query.strip():
        return []
    bm = BM25Okapi([_tok(s.name + " " + s.description + " " + " ".join(s.tags) + " " + s.body[:2000]) for s in cands])
    scores = bm.get_scores(_tok(query))
    order = sorted(range(len(cands)), key=lambda i: -scores[i])
    return [cands[i] for i in order[:k] if scores[i] > 0]


def read(name: str) -> str:
    for s in load_all():
        if s.name == name or s.path.stem == name or s.path.stem == _slug(name):
            return s.path.read_text(encoding="utf-8")
    raise FileNotFoundError(f"no skill {name!r}; known: {[s.name for s in load_all()]}")


def write(name: str, description: str, body: str, tags: list[str] | None = None, always: bool | None = None) -> Path:
    """Write a skill, keeping the frontmatter the caller did not mention.

    `tags` and `always` used to default to `[]` and `False`, so an edit that passed only the body erased
    them. Episode 3 rewrote `early-game-food.md` and its seven tags became `tags: []` -- and tags are part of
    the BM25 key in `select`, so the edit quietly made the skill harder to retrieve. The same call on an
    always-on skill would have demoted it out of every prompt without saying so.

    Pass `tags=[]` or `always=False` explicitly to clear them.
    """
    path = SKILLS / f"{_slug(name)}.md"
    if tags is None or always is None:
        prev = next((s for s in load_all() if s.path == path), None)
        if tags is None:
            tags = list(prev.tags) if prev else []
        if always is None:
            always = prev.always if prev else False
    post = frontmatter.Post(body.strip() + "\n", name=name, description=description, tags=tags, always=always)
    path.write_text(frontmatter.dumps(post), encoding="utf-8")
    return path


def delete(name: str) -> bool:
    for s in load_all():
        if s.name == name or s.path.stem == name or s.path.stem == _slug(name):
            s.path.unlink()
            return True
    return False
