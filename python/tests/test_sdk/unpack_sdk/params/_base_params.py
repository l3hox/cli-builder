"""Parent TypedDict for inheritance test.

NOTE: deliberately does NOT use `from __future__ import annotations` — that
would stringify Required/NotRequired markers and break the TypedDict
metaclass's required/optional classification. Stripe's real param modules
follow the same convention.
"""

# NOTE: import TypedDict from typing_extensions, not typing. PEP 655
# (Required/NotRequired) only landed in typing.TypedDict on Python 3.11+.
# On 3.10, typing.TypedDict ignores NotRequired markers from
# typing_extensions and classifies every field as required. Stripe and
# every other modern PEP 692 SDK ship the typing_extensions.TypedDict
# variant precisely for this reason.
from typing_extensions import NotRequired, TypedDict


class BaseListParams(TypedDict, total=False):
    """Pagination fields shared across list methods."""

    limit: int
    starting_after: NotRequired[str]
