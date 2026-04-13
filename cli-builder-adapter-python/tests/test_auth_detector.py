"""Unit tests for auth_detector.detect_constructor_auth()."""

from cli_builder_adapter.auth_detector import detect_constructor_auth, _derive_env_var
from cli_builder_adapter.models import AuthType


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
