"""Service classes using `**kwargs: Unpack[TypedDict]` (PEP 692).

The TypedDicts are imported only under `TYPE_CHECKING`, exactly matching
Stripe's pattern. At runtime, `typing.get_type_hints()` cannot resolve the
ForwardRefs — the adapter must AST-walk this module to discover where each
ForwardRef points.

NOTE: deliberately NO `from __future__ import annotations`. Stripe's real
modules don't use it either; with future-annotations on, `Unpack["X"]`
becomes a bare string `'Unpack["X"]'` instead of the generic alias
`Unpack[ForwardRef('X')]`, breaking the whole resolution mechanic. The
real-world test fixture must mirror the real-world annotation eval mode.
"""

from typing import TYPE_CHECKING

from typing_extensions import Unpack

if TYPE_CHECKING:
    from .params import (
        BaseListParams,
        ChildListParams,
        CustomerCreateParams,
        CustomerListParams,
    )


class CustomerService:
    """Stripe-shaped service: list (total=False) and create (mixed required)."""

    def __init__(self, api_key: str) -> None:
        self._api_key = api_key

    def list(self, **params: Unpack["CustomerListParams"]) -> list[dict]:
        """List customers."""
        return []

    def create(self, **params: Unpack["CustomerCreateParams"]) -> dict:
        """Create a customer."""
        return {}


class InheritanceService:
    """Service method whose TypedDict inherits from another TypedDict."""

    def __init__(self, api_key: str) -> None:
        self._api_key = api_key

    def list(self, **params: Unpack["ChildListParams"]) -> list[dict]:
        """Should expose parent fields (limit, starting_after) AND child field (email)."""
        return []


class UnresolvableUnpackService:
    """Method whose Unpack[ForwardRef(...)] target is NOT in any TYPE_CHECKING import.

    Resolution must fail gracefully with a CB607 diagnostic and zero params —
    NOT a KeyError, NOT a silent zero-param return.
    """

    def __init__(self, api_key: str) -> None:
        self._api_key = api_key

    def list(self, **params: Unpack["NonExistentParams"]) -> list[dict]:  # type: ignore[name-defined]
        """Reference a name that resolves to nothing."""
        return []


class BareKwargsService:
    """Method with unannotated `**kwargs` — preserves pre-Step-17 zero-param behavior."""

    def __init__(self, api_key: str) -> None:
        self._api_key = api_key

    def list(self, **params) -> list[dict]:
        """Bare **kwargs, no annotation. Should yield zero params and NO CB606/CB607."""
        return []


class PlainParamsService:
    """Sanity check — a method with regular positional/keyword params alongside
    an Unpack-shaped sibling. Both must extract correctly."""

    def __init__(self, api_key: str) -> None:
        self._api_key = api_key

    def get(self, id: str) -> dict:
        return {}

    def list(self, **params: Unpack["BaseListParams"]) -> list[dict]:
        return []
