"""Strings that a model sends where JSON is meant.

Finding 25. `dry_run="False"` is a non-empty string, so it is true. `room` ran its dry-run branch, returned a
payload shaped like a real placement, and the model wrote "the room is built" into its notebook. The night's
work was not done and nothing anywhere said so. The token the model sent is Python's repr of a bool, and
Python's repr is capitalised where JSON's is not.
"""
from __future__ import annotations

import pytest

from rimagent.registry import coerce_param


@pytest.mark.parametrize("sent", ["False", "false", "FALSE", " False "])
def test_every_spelling_of_false_becomes_the_boolean(sent):
    assert coerce_param(sent) is False


@pytest.mark.parametrize("sent", ["True", "true", "TRUE"])
def test_every_spelling_of_true_becomes_the_boolean(sent):
    assert coerce_param(sent) is True


@pytest.mark.parametrize("sent", ["null", "Null", "NULL"])
def test_every_spelling_of_null_becomes_none(sent):
    assert coerce_param(sent) is None


def test_none_is_left_alone_because_it_is_a_plausible_label():
    assert coerce_param("None") == "None"


def test_the_existing_coercions_still_work():
    assert coerce_param("[97, 98]") == [97, 98]
    assert coerce_param("80") == 80
    assert coerce_param("-1.5") == -1.5
    assert coerce_param('{"a": 1}') == {"a": 1}


def test_ordinary_strings_are_untouched():
    for s in ("WoodLog", "hello", "", "Truex", "falsey"):
        assert coerce_param(s) == s


def test_coercion_reaches_inside_lists_and_dicts():
    assert coerce_param({"dry_run": "False", "at": "[1, 2]"}) == {"dry_run": False, "at": [1, 2]}
    assert coerce_param(["True", "3"]) == [True, 3]


def test_a_real_boolean_is_not_disturbed():
    assert coerce_param(False) is False
    assert coerce_param(True) is True
