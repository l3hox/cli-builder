"""Detect auth patterns in Python SDK packages."""

from __future__ import annotations

import inspect
import typing
from typing import Any

from .models import AuthPattern, AuthType, Diagnostic, DiagnosticSeverity


# Known auth parameter names
AUTH_PARAM_NAMES = {"api_key", "apikey", "secret_key", "secret", "api_secret", "token"}


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


def _derive_env_var(cls: type, param_name: str) -> str:
    """Derive environment variable name from class and parameter.

    CustomerClient + api_key → CUSTOMERCLIENT_API_KEY
    """
    # Use the module's top-level package name if available
    module = cls.__module__
    prefix = module.split(".")[0].upper() if module else cls.__name__.upper()
    suffix = param_name.upper()
    return f"{prefix}_{suffix}"
