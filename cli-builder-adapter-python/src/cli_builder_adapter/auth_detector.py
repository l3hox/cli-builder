"""Detect auth patterns in Python SDK packages."""

from __future__ import annotations

import inspect
import typing
from typing import Any

from .models import (
    AuthPattern,
    AuthSetupStyle,
    AuthType,
    Diagnostic,
    DiagnosticSeverity,
    StaticAuthConfig,
)


# Known auth parameter names (constructor params)
AUTH_PARAM_NAMES = {"api_key", "apikey", "secret_key", "secret", "api_secret", "token"}

# Known module-level auth attribute names (e.g., stripe.api_key)
MODULE_AUTH_ATTRS = {"api_key", "apikey", "secret_key", "api_secret"}


def detect_constructor_auth(
    cls: type,
    diagnostics: list[Diagnostic],
) -> AuthPattern | None:
    """Detect auth pattern from __init__ parameters."""
    try:
        sig = inspect.signature(cls.__init__)
    except (ValueError, TypeError):
        return None

    hints = {}
    try:
        hints = typing.get_type_hints(cls.__init__)
    except Exception:
        pass

    for name, param in sig.parameters.items():
        if name == "self":
            continue
        if name.lower() in AUTH_PARAM_NAMES:
            # Determine auth type
            param_type = hints.get(name)
            if param_type is str or param_type is None:
                env_var = _derive_env_var(cls, name)
                return AuthPattern(
                    type=AuthType.API_KEY,
                    env_var=env_var,
                    parameter_name=name,
                )

    return None


def detect_module_auth(
    module: Any,
    auth_patterns: list[AuthPattern],
    diagnostics: list[Diagnostic],
) -> StaticAuthConfig | None:
    """Detect module-level auth attributes (e.g., stripe.api_key).

    Only detects if no constructor-level auth already covers the same
    parameter name. Returns StaticAuthConfig or None.
    """
    existing_param_names = {a.parameter_name for a in auth_patterns}

    for attr_name in MODULE_AUTH_ATTRS:
        if hasattr(module, attr_name):
            # Skip if constructor auth already covers this name
            if attr_name in existing_param_names:
                continue

            # Verify it's a string-typed attribute (or None — not yet set)
            value = getattr(module, attr_name)
            if value is not None and not isinstance(value, str):
                continue

            module_name = module.__name__
            prefix = module_name.split(".")[0].upper()
            env_var = f"{prefix}_{attr_name.upper()}"

            # Also add an AuthPattern so generators know about the env var
            auth_patterns.append(AuthPattern(
                type=AuthType.API_KEY,
                env_var=env_var,
                parameter_name=attr_name,
            ))

            return StaticAuthConfig(
                type_name=module_name,
                type_module=module_name,
                property_name=attr_name,
                style=AuthSetupStyle.MODULE_ATTRIBUTE,
            )

    return None


def _derive_env_var(cls: type, param_name: str) -> str:
    """Derive environment variable name from class and parameter.

    CustomerClient + api_key → CUSTOMERCLIENT_API_KEY
    """
    # Use the module's top-level package name if available
    module = cls.__module__
    prefix = module.split(".")[0].upper() if module else cls.__name__.upper()
    suffix = param_name.upper()
    return f"{prefix}_{suffix}"
