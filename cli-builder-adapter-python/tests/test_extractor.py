"""Unit tests for extractor — service discovery, operation extraction."""

import sys
from pathlib import Path
from unittest.mock import patch

from cli_builder_adapter.extractor import extract
from cli_builder_adapter.models import TypeKind


# Ensure test_sdk is importable
sys.path.insert(0, str(Path(__file__).parent))


# ---- Service discovery ----

def test_discovers_three_services():
    result = extract("test_sdk", "test_sdk.services")
    names = {r.name for r in result.metadata.resources}
    assert "customer" in names
    assert "order" in names
    assert "message" in names
    assert len(result.metadata.resources) == 3

def test_skips_imported_classes():
    """Classes imported into the module but defined elsewhere should be skipped."""
    result = extract("test_sdk", "test_sdk.services")
    # Customer, Order, Message models are imported but not services
    resource_class_names = {r.source_class_name for r in result.metadata.resources}
    assert "Customer" not in resource_class_names  # Model, not service
    assert "CustomerClient" in resource_class_names

def test_skips_private_classes():
    """Classes starting with _ should not be discovered."""
    result = extract("test_sdk", "test_sdk.services")
    resource_names = {r.name for r in result.metadata.resources}
    # No private classes should appear
    assert all(not name.startswith("_") for name in resource_names)


# ---- Operation extraction ----

def test_customer_operations():
    result = extract("test_sdk", "test_sdk.services")
    customer = next(r for r in result.metadata.resources if r.name == "customer")
    op_names = {op.name for op in customer.operations}
    assert "get" in op_names
    assert "create" in op_names
    assert "list" in op_names
    assert "delete" in op_names

def test_skips_private_methods():
    result = extract("test_sdk", "test_sdk.services")
    customer = next(r for r in result.metadata.resources if r.name == "customer")
    op_names = {op.name for op in customer.operations}
    # _api_key is a private attribute, not exposed as operation
    assert "_api_key" not in op_names
    assert all(not name.startswith("_") for name in op_names)

def test_operation_parameters():
    result = extract("test_sdk", "test_sdk.services")
    customer = next(r for r in result.metadata.resources if r.name == "customer")
    get_op = next(op for op in customer.operations if op.name == "get")
    assert len(get_op.parameters) == 1
    assert get_op.parameters[0].name == "id"
    assert get_op.parameters[0].required is True
    assert get_op.parameters[0].type.kind == TypeKind.PRIMITIVE
    assert get_op.parameters[0].type.name == "str"

def test_optional_parameter_detection():
    result = extract("test_sdk", "test_sdk.services")
    customer = next(r for r in result.metadata.resources if r.name == "customer")
    list_op = next(op for op in customer.operations if op.name == "list")
    limit_param = next(p for p in list_op.parameters if p.name == "limit")
    assert limit_param.required is False

def test_return_type_extracted():
    result = extract("test_sdk", "test_sdk.services")
    customer = next(r for r in result.metadata.resources if r.name == "customer")
    get_op = next(op for op in customer.operations if op.name == "get")
    assert get_op.return_type.kind == TypeKind.CLASS
    assert get_op.return_type.name == "Customer"

def test_source_method_name_preserved():
    result = extract("test_sdk", "test_sdk.services")
    customer = next(r for r in result.metadata.resources if r.name == "customer")
    get_op = next(op for op in customer.operations if op.name == "get")
    assert get_op.source_method_name == "get"

def test_options_class_parameter():
    result = extract("test_sdk", "test_sdk.services")
    customer = next(r for r in result.metadata.resources if r.name == "customer")
    create_op = next(op for op in customer.operations if op.name == "create")
    opts_param = next(p for p in create_op.parameters if p.name == "options")
    assert opts_param.type.kind == TypeKind.CLASS
    assert opts_param.type.name == "CreateCustomerOptions"
    assert opts_param.type.properties is not None


# ---- Constructor info ----

def test_constructor_params_with_auth():
    result = extract("test_sdk", "test_sdk.services")
    customer = next(r for r in result.metadata.resources if r.name == "customer")
    assert customer.constructor_params is not None
    assert len(customer.constructor_params) == 1
    assert customer.constructor_params[0].name == "api_key"
    assert customer.constructor_params[0].is_auth is True

def test_parameterless_init_detection():
    result = extract("test_sdk", "test_sdk.services")
    customer = next(r for r in result.metadata.resources if r.name == "customer")
    # CustomerClient requires api_key, so not parameterless
    assert customer.has_parameterless_ctor is False


# ---- Auth patterns ----

def test_auth_patterns_detected():
    result = extract("test_sdk", "test_sdk.services")
    assert len(result.metadata.auth_patterns) >= 1
    auth = result.metadata.auth_patterns[0]
    assert auth.parameter_name == "api_key"
    assert "TEST_SDK" in auth.env_var or "test_sdk" in auth.env_var.lower()


# ---- Streaming ----

def test_message_list_param():
    """MessageClient.send takes list[Message] — should be Array type."""
    result = extract("test_sdk", "test_sdk.services")
    message = next(r for r in result.metadata.resources if r.name == "message")
    send_op = next(op for op in message.operations if op.name == "send")
    messages_param = next(p for p in send_op.parameters if p.name == "messages")
    assert messages_param.type.kind == TypeKind.ARRAY


# ---- Diagnostics ----

def test_cb601_emitted():
    """Runtime import should produce CB601 info diagnostic."""
    result = extract("test_sdk", "test_sdk.services")
    codes = {d.code for d in result.diagnostics}
    assert "CB601" in codes


# ---- Version ----

def test_version_captured():
    result = extract("test_sdk", "test_sdk.services")
    # test_sdk may not have __version__, should fallback to 0.0.0
    assert result.metadata.version is not None
