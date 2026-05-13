"""Tests for PEP 692 `Unpack[TypedDict]` resolution in the Python adapter.

See `docs/internal/step-17-pep692-unpack.md` and ADR-022.

Each test exercises a service in `test_sdk.unpack_sdk.service` whose method
uses `**params: Unpack[X]` where X is a TypedDict imported only under
`TYPE_CHECKING`. The adapter must AST-walk the service module to discover
the import target and resolve the class.

NOTE: deliberately NO `from __future__ import annotations`. Inline test
functions like `_sample` in the cross-version test rely on annotations
being real objects at def time, not strings.
"""

import sys
from pathlib import Path

import pytest

from cli_builder_adapter.extractor import _extract_params, _try_resolve_unpack_kwargs
from cli_builder_adapter.models import DiagnosticSeverity

# Ensure test_sdk is importable
sys.path.insert(0, str(Path(__file__).parent))

import inspect  # noqa: E402

from test_sdk.unpack_sdk.service import (  # noqa: E402
    BareKwargsService,
    CustomerService,
    InheritanceService,
    PlainParamsService,
    UnresolvableUnpackService,
)


# ---- Helpers ---------------------------------------------------------------


def _extract(method):
    """Run `_extract_params` against a single method; return (params, diagnostics)."""
    method = inspect.unwrap(method)
    sig = inspect.signature(method)
    diagnostics: list = []
    params = _extract_params(method, sig, {}, diagnostics)
    return params, diagnostics


def _codes(diagnostics, severity=None) -> set[str]:
    return {
        d.code for d in diagnostics
        if severity is None or d.severity == severity
    }


# ---- PR 1 locked test set --------------------------------------------------


def test_plain_kwargs_without_unpack_still_skipped():
    """A method with bare `**kwargs` (no annotation) must produce zero params
    and emit no Unpack diagnostics. Backward compatibility for every non-PEP-692
    SDK depends on this path being a silent no-op."""
    params, diagnostics = _extract(BareKwargsService.list)

    assert params == []
    assert "CB606" not in _codes(diagnostics)
    assert "CB607" not in _codes(diagnostics)


def test_unpack_typed_dict_resolves_total_false_fields():
    """`CustomerListParams` is `total=False` — every field must come out as
    `required=False`. Field count matches the TypedDict exactly.
    """
    params, diagnostics = _extract(CustomerService.list)
    names = {p.name for p in params}

    expected = {"email", "limit", "starting_after", "ending_before", "expand", "is_active", "plan"}
    assert names == expected, f"missing: {expected - names}, extra: {names - expected}"
    assert all(not p.required for p in params), \
        f"total=False fields must all be optional; found required: {[p.name for p in params if p.required]}"
    assert "CB606" in _codes(diagnostics, DiagnosticSeverity.INFO)


def test_unpack_required_and_notrequired_classification():
    """`CustomerCreateParams` has `email: Required[str]` and `NotRequired[X]`
    for the rest. `__required_keys__` / `__optional_keys__` is the source of
    truth; the adapter must classify accordingly."""
    params, _ = _extract(CustomerService.create)
    by_name = {p.name: p for p in params}

    assert by_name["email"].required is True
    for opt_name in ("name", "description", "metadata", "address"):
        assert by_name[opt_name].required is False, \
            f"{opt_name} should be optional (NotRequired)"


def test_unpack_inheritance_aggregates_parent_fields():
    """`ChildListParams(BaseListParams)` — the child must expose parent fields
    (`limit`, `starting_after`) AND its own field (`email`). PEP 589 metaclass
    aggregates these into `__required_keys__` / `__optional_keys__`; the
    walker iterating those frozensets gets inheritance for free."""
    params, _ = _extract(InheritanceService.list)
    names = {p.name for p in params}

    assert "limit" in names, "parent field 'limit' missing — MRO aggregation failed"
    assert "starting_after" in names, "parent field 'starting_after' missing"
    assert "email" in names, "child field 'email' missing"


def test_unpack_unresolvable_forwardref_emits_diagnostic():
    """When `Unpack[ForwardRef(X)]` points at a name absent from any
    TYPE_CHECKING import, resolution must fail loudly: zero params AND a
    `CB607` warning. The silent-fallback path is the original-bug class —
    both assertions are required."""
    params, diagnostics = _extract(UnresolvableUnpackService.list)

    assert params == [], f"expected zero params, got: {[p.name for p in params]}"
    cb607 = [d for d in diagnostics if d.code == "CB607"]
    assert cb607, "expected CB607 diagnostic on unresolvable ForwardRef"
    assert cb607[0].severity == DiagnosticSeverity.WARNING, \
        f"CB607 must be WARNING (silent-fallback is the original bug), got: {cb607[0].severity}"


def test_unpack_resolves_alongside_normal_parameters():
    """A service whose methods mix normal positional params (`get(id: str)`)
    with `**kwargs: Unpack[X]` patterns must extract both correctly. Smoke
    check that the VAR_KEYWORD branch doesn't disturb the rest of
    `_extract_params`."""
    # Normal method — should yield exactly one param.
    get_params, _ = _extract(PlainParamsService.get)
    assert [p.name for p in get_params] == ["id"]
    assert get_params[0].required is True

    # Unpack method — should yield BaseListParams' two fields.
    list_params, _ = _extract(PlainParamsService.list)
    names = {p.name for p in list_params}
    assert names == {"limit", "starting_after"}


# ---- Cross-version Unpack origin normalization -----------------------------


def test_typing_extensions_unpack_get_origin_normalizes():
    """The adapter standardizes on `typing_extensions.get_origin` / `Unpack`
    because `typing.Unpack` ships only from 3.11+. Verify that `get_origin`
    on an `Unpack[X]` annotation returns the same sentinel regardless of
    which module the user imports `Unpack` from.

    This is the cross-version regression gate: if 3.10 CI starts failing
    after a typing_extensions update, this test pinpoints the cause."""
    from typing_extensions import Unpack as _UnpackExt, get_origin as _get_origin_ext

    def _sample(**kw: _UnpackExt["CustomerListParams"]) -> None:  # noqa: F821, ARG001
        pass

    sig = inspect.signature(_sample)
    param = sig.parameters["kw"]
    assert _get_origin_ext(param.annotation) is _UnpackExt

    # Sanity: typing.Unpack (if available) is interchangeable.
    try:
        from typing import Unpack as _UnpackTyping  # type: ignore[attr-defined]
    except ImportError:
        pytest.skip("typing.Unpack unavailable on this Python (3.10)")
    else:
        # typing_extensions.get_origin should recognize either form.
        def _sample2(**kw: _UnpackTyping["CustomerListParams"]) -> None:  # noqa: F821, ARG001
            pass
        sig2 = inspect.signature(_sample2)
        # Both should normalize to the same Unpack sentinel under
        # typing_extensions.get_origin (the function we use in the adapter).
        assert _get_origin_ext(sig2.parameters["kw"].annotation) in (_UnpackExt, _UnpackTyping)
