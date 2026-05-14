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


# Known auth parameter names (constructor params, exact match)
AUTH_PARAM_NAMES = {"api_key", "apikey", "secret_key", "secret", "api_secret", "token"}

# Suffixes that mark a parameter as auth-bearing even when the full name
# isn't in AUTH_PARAM_NAMES. Catches PyGithub's `login_or_token`, OAuth-style
# `access_token`, `bearer_token`, `api_token`, plus `*_key` / `*_secret`
# variants. Suffix must be preceded by `_` so we don't catch e.g. `apaToken`.
# Added in Step 18 / ADR-023 for single-client SDK support.
AUTH_PARAM_SUFFIXES = ("_token", "_key", "_secret")

# Known module-level auth attribute names (e.g., stripe.api_key)
MODULE_AUTH_ATTRS = {"api_key", "apikey", "secret_key", "api_secret"}


def _is_auth_param_name(name: str) -> bool:
    """Whether a parameter name suggests it carries auth credentials.

    Matches:
      1. Exact match against AUTH_PARAM_NAMES (api_key, token, secret, ...)
      2. Ends with any AUTH_PARAM_SUFFIXES entry (login_or_token, access_token, …)
    """
    lower = name.lower()
    if lower in AUTH_PARAM_NAMES:
        return True
    return any(lower.endswith(suf) for suf in AUTH_PARAM_SUFFIXES)


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
        if _is_auth_param_name(name):
            # Determine auth type. Accept `str` (typed) and `str | None` (Union
            # with None) — PyGithub-style ctors use the optional form.
            param_type = hints.get(name)
            if _is_string_or_optional_string(param_type) or param_type is None:
                env_var = _derive_env_var(cls, name)
                return AuthPattern(
                    type=AuthType.API_KEY,
                    env_var=env_var,
                    parameter_name=name,
                )

    return None


def _is_string_or_optional_string(t: Any) -> bool:
    """True for `str`, `str | None`, `Optional[str]`. False for everything else."""
    if t is str:
        return True
    # Union check — typing.Union[str, None] or PEP 604 str | None
    origin = typing.get_origin(t)
    if origin is typing.Union or (origin is not None and origin.__name__ == "UnionType"):
        args = set(typing.get_args(t))
        # `str | None` = {str, type(None)}; `Optional[str]` = same.
        return args == {str, type(None)}
    return False


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
            # Verify it's a string-typed attribute (or None — not yet set)
            value = getattr(module, attr_name)
            if value is not None and not isinstance(value, str):
                continue

            module_name = module.__name__
            prefix = module_name.split(".")[0].upper()
            env_var = f"{prefix}_{attr_name.upper()}"

            # Add AuthPattern if not already covered by constructor auth
            if attr_name not in existing_param_names:
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
