"""A skill edit must not erase the frontmatter it did not mention.

Episode 3 rewrote `early-game-food.md` and its seven tags became `tags: []`. Tags are part of the BM25 key
in `skills.select`, so the edit made the skill harder to retrieve and said nothing. The same call on
`bridge-manual` or `core-doctrine` would have demoted an always-on skill out of every prompt.
"""
from __future__ import annotations

from rimagent import skills


def test_an_edit_that_omits_tags_keeps_them(clean_brain):  # noqa: ARG001
    skills.write("food", "when food is low", "rice first", tags=["food", "farming"])
    skills.write("food", "when food is low", "rice first, then corn")
    s = next(s for s in skills.load_all() if s.name == "food")
    assert s.tags == ["food", "farming"]


def test_an_edit_that_omits_always_does_not_demote_an_always_on_skill(clean_brain):  # noqa: ARG001
    skills.write("manual", "how the tools work", "body", always=True)
    skills.write("manual", "how the tools work", "a longer body")
    assert next(s for s in skills.load_all() if s.name == "manual").always is True


def test_empty_tags_still_clears_them(clean_brain):  # noqa: ARG001
    skills.write("food", "when food is low", "body", tags=["food"])
    skills.write("food", "when food is low", "body", tags=[])
    assert next(s for s in skills.load_all() if s.name == "food").tags == []


def test_always_false_still_demotes(clean_brain):  # noqa: ARG001
    skills.write("manual", "how the tools work", "body", always=True)
    skills.write("manual", "how the tools work", "body", always=False)
    assert next(s for s in skills.load_all() if s.name == "manual").always is False


def test_a_new_skill_defaults_to_no_tags_and_not_always(clean_brain):  # noqa: ARG001
    skills.write("fresh", "brand new", "body")
    s = next(s for s in skills.load_all() if s.name == "fresh")
    assert s.tags == [] and s.always is False


def test_a_body_only_edit_leaves_the_skill_retrievable_by_its_tags(clean_brain):  # noqa: ARG001
    """The consequence, end to end: tags are part of the BM25 key, so erasing them loses the skill."""
    skills.write("alpha", "one line", "body text", tags=["blight"])
    for name in ("beta", "gamma", "delta"):
        skills.write(name, "another line", "unrelated text", tags=["combat"])
    assert [s.name for s in skills.select("blight", k=1)] == ["alpha"]

    skills.write("alpha", "one line", "a rewritten body")  # the episode-3 edit
    assert [s.name for s in skills.select("blight", k=1)] == ["alpha"]
