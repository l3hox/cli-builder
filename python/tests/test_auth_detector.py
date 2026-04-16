"""Unit tests for auth_detector — constructor auth + module-level auth."""

import types

from cli_builder_adapter.auth_detector import (
    detect_constructor_auth,
    detect_module_auth,
    _derive_env_var,
)
from cli_builder_adapter.models import AuthSetupStyle, AuthType


# ---- Auth detection ----

def test_api_key_str_detected():
    class FakeClient:
        def __init__(self, api_key: str): pass

    auth = detect_constructor_auth(FakeClient, [])
    assert auth is not None
    assert auth.type == AuthType.API_KEY
    assert auth.parameter_name == "api_key"

def test_token_str_detected():
    class FakeClient:
        def __init__(self, token: str): pass

    auth = detect_constructor_auth(FakeClient, [])
    assert auth is not None
    assert auth.type == AuthType.API_KEY
    assert auth.parameter_name == "token"

def test_secret_key_str_detected():
    class FakeClient:
        def __init__(self, secret_key: str): pass

    auth = detect_constructor_auth(FakeClient, [])
    assert auth is not None
    assert auth.parameter_name == "secret_key"

def test_no_auth_param():
    class FakeClient:
        def __init__(self, base_url: str): pass

    auth = detect_constructor_auth(FakeClient, [])
    assert auth is None

def test_non_string_auth_param_not_detected():
    class FakeClient:
        def __init__(self, api_key: int): pass

    auth = detect_constructor_auth(FakeClient, [])
    assert auth is None

def test_multiple_auth_candidates_first_wins():
    """First by parameter iteration order in inspect.signature."""
    class FakeClient:
        def __init__(self, api_key: str, token: str): pass

    auth = detect_constructor_auth(FakeClient, [])
    assert auth is not None
    assert auth.parameter_name == "api_key"  # First in parameter order

def test_no_init():
    """Class with no explicit __init__ — no auth."""
    class FakeClient:
        pass

    auth = detect_constructor_auth(FakeClient, [])
    assert auth is None


# ---- Env var derivation ----

def test_env_var_from_module():
    class FakeClient:
        pass
    FakeClient.__module__ = "stripe.services"

    result = _derive_env_var(FakeClient, "api_key")
    assert result == "STRIPE_API_KEY"

def test_env_var_from_class_name():
    class FakeClient:
        pass
    FakeClient.__module__ = ""

    result = _derive_env_var(FakeClient, "token")
    assert result == "FAKECLIENT_TOKEN"


# ---- Module-level auth detection ----

def _make_module(name: str, **attrs) -> types.ModuleType:
    mod = types.ModuleType(name)
    for k, v in attrs.items():
        setattr(mod, k, v)
    return mod

def test_module_level_api_key_detected():
    mod = _make_module("stripe", api_key=None)
    auth_patterns = []
    result = detect_module_auth(mod, auth_patterns, [])
    assert result is not None
    assert result.property_name == "api_key"
    assert result.style == AuthSetupStyle.MODULE_ATTRIBUTE
    assert result.type_module == "stripe"
    # Also adds AuthPattern
    assert len(auth_patterns) == 1
    assert auth_patterns[0].env_var == "STRIPE_API_KEY"

def test_module_level_api_key_with_string_value():
    mod = _make_module("mylib", api_key="sk_test_123")
    result = detect_module_auth(mod, [], [])
    assert result is not None
    assert result.property_name == "api_key"

def test_module_level_secret_key_detected():
    mod = _make_module("sdk", secret_key=None)
    result = detect_module_auth(mod, [], [])
    assert result is not None
    assert result.property_name == "secret_key"

def test_module_level_non_string_attr_ignored():
    """Non-string module attribute (e.g., api_key = 42) should be ignored."""
    mod = _make_module("sdk", api_key=42)
    result = detect_module_auth(mod, [], [])
    assert result is None

def test_module_level_deduplicates_auth_pattern_but_still_returns_static_auth():
    """If constructor auth already covers api_key, module-level should
    still return StaticAuthConfig but NOT add a duplicate AuthPattern."""
    mod = _make_module("sdk", api_key=None)
    from cli_builder_adapter.models import AuthPattern
    auth_patterns = [AuthPattern(type=AuthType.API_KEY, env_var="SDK_API_KEY", parameter_name="api_key")]
    result = detect_module_auth(mod, auth_patterns, [])
    # StaticAuthConfig is still returned (module-level auth exists)
    assert result is not None
    assert result.property_name == "api_key"
    # No duplicate AuthPattern added
    assert len(auth_patterns) == 1

def test_module_level_no_auth_attrs():
    """Module without any known auth attributes returns None."""
    mod = _make_module("plain_sdk", version="1.0")
    result = detect_module_auth(mod, [], [])
    assert result is None
