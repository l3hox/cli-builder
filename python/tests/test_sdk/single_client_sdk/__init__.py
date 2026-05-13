"""Synthetic SDK exercising single-client discovery (Step 18 / ADR-023).

Modelled on PyGithub: one `GithubClient` class with 40-ish verb_noun methods,
plus a few non-conforming methods that should be skipped (CB610) and value
objects with no methods. The ambiguity-test variant lives in `_ambiguous.py`.
"""

from ._client import Github
from ._types import Repo, User

__all__ = ["Github", "Repo", "User"]
