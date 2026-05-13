"""Parent TypedDict for inheritance test.

NOTE: deliberately does NOT use `from __future__ import annotations` — that
would stringify Required/NotRequired markers and break the TypedDict
metaclass's required/optional classification. Stripe's real param modules
follow the same convention.
"""

from typing import TypedDict

from typing_extensions import NotRequired


class BaseListParams(TypedDict, total=False):
    """Pagination fields shared across list methods."""

    limit: int
    starting_after: NotRequired[str]
