"""Customer-shaped TypedDicts — covers `total=False`, mixed Required/NotRequired,
inheritance, and nested TypedDicts (for PR 2 field-resolution tests).

NOTE: no `from __future__ import annotations` — that stringifies Required /
NotRequired markers and breaks the TypedDict metaclass's required/optional
classification. Stripe's real param modules follow the same convention.
"""

from typing import List, Literal, TypedDict

from typing_extensions import NotRequired, Required

from ._base_params import BaseListParams


class CustomerListParams(TypedDict, total=False):
    """All-optional shape mirroring Stripe's list params.

    `total=False` means every field is optional by default — the test
    asserts the adapter correctly classifies all fields as `required=False`.
    """

    email: str
    limit: int
    starting_after: str
    ending_before: str
    expand: List[str]
    is_active: bool
    plan: Literal["free", "pro", "enterprise"]


class CustomerCreateParams(TypedDict):
    """Mixed required/optional shape — the most common modern pattern.

    `total=True` (default) makes everything required UNLESS wrapped in
    `NotRequired[X]`. Stripe and OpenAI both use this pattern.
    """

    email: Required[str]
    name: NotRequired[str]
    description: NotRequired[str]
    metadata: NotRequired[dict]
    address: NotRequired["NestedAddressParams"]


class NestedAddressParams(TypedDict, total=False):
    """Nested TypedDict — for the PR 2 nested-fallback test.

    The PR 1 walker will produce `TypeKind.Other` for this field via
    `map_type`'s normal handling of unknown class references.
    """

    line1: str
    line2: NotRequired[str]
    city: str
    postal_code: str


class ChildListParams(BaseListParams, total=False):
    """Inheritance — should aggregate parent fields via PEP 589 metaclass.

    `__required_keys__` / `__optional_keys__` on the subclass include
    parent's fields automatically. The test asserts that walking these
    frozensets is the correct source of truth.
    """

    email: NotRequired[str]
