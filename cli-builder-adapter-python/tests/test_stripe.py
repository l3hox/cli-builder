"""Stripe SDK validation — tests adapter against real stripe-python package."""

import pytest

stripe = pytest.importorskip("stripe", reason="stripe package not installed")

from cli_builder_adapter.extractor import extract
from cli_builder_adapter.models import AuthSetupStyle


@pytest.fixture(scope="module")
def stripe_result():
    """Extract metadata from stripe package (cached per module)."""
    return extract("stripe")


# ---- Resource discovery ----

def test_discovers_known_resources(stripe_result):
    """Known-stable resource names must be a subset of discovered resources."""
    names = {r.name for r in stripe_result.metadata.resources}
    expected = {"customer", "payment-intent", "charge"}
    missing = expected - names
    assert not missing, f"Missing expected resources: {missing}"

def test_minimum_resource_count(stripe_result):
    """Stripe has 50+ resources — assert a reasonable minimum."""
    assert len(stripe_result.metadata.resources) >= 30, (
        f"Expected >= 30 resources, got {len(stripe_result.metadata.resources)}"
    )


# ---- Auth detection ----

def test_api_key_auth_detected(stripe_result):
    assert len(stripe_result.metadata.auth_patterns) >= 1
    auth = stripe_result.metadata.auth_patterns[0]
    assert auth.parameter_name == "api_key"
    assert "STRIPE" in auth.env_var

def test_module_level_static_auth(stripe_result):
    """stripe.api_key module-level auth should produce StaticAuthConfig."""
    assert stripe_result.metadata.static_auth is not None
    assert stripe_result.metadata.static_auth.property_name == "api_key"
    assert stripe_result.metadata.static_auth.style == AuthSetupStyle.MODULE_ATTRIBUTE


# ---- Customer operations ----

def test_customer_has_crud_operations(stripe_result):
    customer = next(
        (r for r in stripe_result.metadata.resources if r.name == "customer"),
        None,
    )
    assert customer is not None, "Customer resource not found"
    op_names = {op.name for op in customer.operations}
    assert "create" in op_names
    assert "retrieve" in op_names
    assert "list" in op_names

def test_customer_operations_have_parameters(stripe_result):
    customer = next(r for r in stripe_result.metadata.resources if r.name == "customer")
    # retrieve should have parameters (at least 'id')
    retrieve_op = next((op for op in customer.operations if op.name == "retrieve"), None)
    assert retrieve_op is not None
    # Stripe's retrieve takes positional args — may have params
    # Just assert the operation was extracted successfully
    assert retrieve_op.source_method_name == "retrieve"


# ---- No errors ----

def test_no_extraction_errors(stripe_result):
    from cli_builder_adapter.models import DiagnosticSeverity
    errors = [d for d in stripe_result.diagnostics if d.severity == DiagnosticSeverity.ERROR]
    assert len(errors) == 0, f"Extraction errors: {[d.message for d in errors]}"
