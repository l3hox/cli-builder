"""Core extraction logic — discovers service classes and extracts SdkMetadata."""

from __future__ import annotations

import importlib
import inspect
import sys
import typing
from typing import Any

from .auth_detector import detect_constructor_auth, detect_module_auth
from .models import (
    AdapterResult,
    AuthPattern,
    ConstructorParam,
    Diagnostic,
    DiagnosticSeverity,
    Operation,
    Parameter,
    Resource,
    SdkMetadata,
    TypeKind,
)
from .type_mapper import map_type

# Service class name suffixes (same as .NET adapter)
SERVICE_SUFFIXES = ("Client", "Service", "Api")


def extract(package_name: str, module_name: str | None = None) -> AdapterResult:
    """Extract SdkMetadata from a Python package.

    Args:
        package_name: Name of the installed Python package
        module_name: Optional specific module within the package
    """
    diagnostics: list[Diagnostic] = []

    # Import the package (controlled import with diagnostic)
    try:
        if module_name:
            module = importlib.import_module(module_name)
        else:
            module = importlib.import_module(package_name)
    except ImportError as e:
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.ERROR, "CB600",
            f"Could not import package '{package_name}': {e}",
        ))
        return AdapterResult(
            metadata=SdkMetadata(package_name, "0.0.0", [], []),
            diagnostics=diagnostics,
        )

    diagnostics.append(Diagnostic(
        DiagnosticSeverity.INFO, "CB601",
        f"Package '{package_name}' imported at runtime — side effects may occur",
    ))

    # Discover service classes
    service_classes = _discover_services(module, diagnostics)

    # Extract resources
    resources: list[Resource] = []
    auth_patterns: list[AuthPattern] = []

    for noun, cls in service_classes:
        auth = detect_constructor_auth(cls, diagnostics)
        if auth and auth not in auth_patterns:
            auth_patterns.append(auth)

        operations = _extract_operations(cls, diagnostics)
        ctor_params = _extract_constructor_params(cls, auth)

        resources.append(Resource(
            name=noun,
            description=inspect.getdoc(cls),
            operations=operations,
            source_class_name=cls.__name__,
            source_module=cls.__module__,
            constructor_params=ctor_params if ctor_params else None,
            has_parameterless_ctor=_has_parameterless_init(cls),
        ))

    # Detect module-level auth (e.g., stripe.api_key)
    static_auth = detect_module_auth(module, auth_patterns, diagnostics)

    # Derive SDK version
    version = getattr(module, "__version__", "0.0.0")

    metadata = SdkMetadata(
        name=package_name,
        version=str(version),
        resources=resources,
        auth_patterns=auth_patterns,
        static_auth=static_auth,
    )

    return AdapterResult(metadata=metadata, diagnostics=diagnostics)


def _discover_services(module: Any, diagnostics: list[Diagnostic]) -> list[tuple[str, type]]:
    """Find classes matching service naming patterns."""
    services = []
    seen_nouns: set[str] = set()

    for name, obj in inspect.getmembers(module, inspect.isclass):
        if name.startswith("_"):
            continue
        if not any(name.endswith(suffix) for suffix in SERVICE_SUFFIXES):
            continue
        if obj.__module__ != module.__name__:
            continue  # Skip imported classes

        noun = _class_to_noun(name)
        if noun in seen_nouns:
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.WARNING, "CB202",
                f"Noun collision: '{name}' maps to '{noun}' which is already used",
            ))
            continue

        seen_nouns.add(noun)
        services.append((noun, obj))

    return services


def _class_to_noun(class_name: str) -> str:
    """Convert class name to CLI noun: CustomerClient → customer."""
    for suffix in SERVICE_SUFFIXES:
        if class_name.endswith(suffix) and len(class_name) > len(suffix):
            class_name = class_name[:-len(suffix)]
            break
    return _pascal_to_kebab(class_name)


def _pascal_to_kebab(name: str) -> str:
    """Convert PascalCase to kebab-case."""
    import re
    s = re.sub(r"([A-Z]+)([A-Z][a-z])", r"\1-\2", name)
    s = re.sub(r"([a-z0-9])([A-Z])", r"\1-\2", s)
    return s.lower()


def _extract_operations(cls: type, diagnostics: list[Diagnostic]) -> list[Operation]:
    """Extract public methods as operations."""
    operations = []

    for name, method in inspect.getmembers(cls, predicate=inspect.isfunction):
        if name.startswith("_"):
            continue

        try:
            sig = inspect.signature(method)
        except (ValueError, TypeError):
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.WARNING, "CB602",
                f"Could not inspect signature of '{cls.__name__}.{name}' — skipping",
            ))
            continue

        hints = {}
        try:
            hints = typing.get_type_hints(method)
        except Exception:
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.INFO, "CB603",
                f"Could not resolve type hints for '{cls.__name__}.{name}' — using signatures only",
            ))

        # Extract parameters
        params = _extract_params(sig, hints, diagnostics)

        # Return type
        return_annotation = hints.get("return", sig.return_annotation)
        return_type = map_type(return_annotation, diagnostics)

        # Detect async
        is_streaming = (return_type.kind == TypeKind.GENERIC and return_type.name == "AsyncIterator")

        verb = _method_to_verb(name)

        operations.append(Operation(
            name=verb,
            description=inspect.getdoc(method),
            parameters=params,
            return_type=return_type,
            is_streaming=is_streaming,
            source_method_name=name,
        ))

    return operations


def _extract_params(
    sig: inspect.Signature,
    hints: dict[str, Any],
    diagnostics: list[Diagnostic],
) -> list[Parameter]:
    """Extract parameters from a method signature."""
    params = []

    for pname, param in sig.parameters.items():
        if pname == "self":
            continue

        # Handle *args, **kwargs
        if param.kind in (
            inspect.Parameter.VAR_POSITIONAL,
            inspect.Parameter.VAR_KEYWORD,
        ):
            continue

        annotation = hints.get(pname, param.annotation)
        type_ref = map_type(annotation, diagnostics)

        has_default = param.default is not inspect.Parameter.empty
        required = not has_default

        params.append(Parameter(
            name=pname,
            type=type_ref,
            required=required,
            description=None,
        ))

    return params


def _extract_constructor_params(cls: type, auth: AuthPattern | None) -> list[ConstructorParam]:
    """Extract __init__ parameters as constructor params."""
    try:
        sig = inspect.signature(cls.__init__)
    except (ValueError, TypeError):
        return []

    hints = {}
    try:
        hints = typing.get_type_hints(cls.__init__)
    except Exception:
        pass

    params = []
    for name, param in sig.parameters.items():
        if name == "self":
            continue
        annotation = hints.get(name, param.annotation)
        type_name = annotation.__name__ if isinstance(annotation, type) else str(annotation)
        type_module = (
            annotation.__module__
            if isinstance(annotation, type) and annotation.__module__ != "builtins"
            else None
        )
        is_auth = auth is not None and name == auth.parameter_name

        params.append(ConstructorParam(
            name=name,
            type_name=type_name,
            type_module=type_module,
            is_auth=is_auth,
            is_required=param.default is inspect.Parameter.empty,
        ))

    return params


def _has_parameterless_init(cls: type) -> bool:
    """Check if __init__ has only 'self' as a required parameter."""
    try:
        sig = inspect.signature(cls.__init__)
    except (ValueError, TypeError):
        return False
    for name, param in sig.parameters.items():
        if name == "self":
            continue
        if param.default is inspect.Parameter.empty:
            return False
    return True


def _method_to_verb(method_name: str) -> str:
    """Convert method name to CLI verb: get_customer → get-customer."""
    # Strip common async prefixes/suffixes
    name = method_name
    if name.startswith("async_"):
        name = name[6:]
    if name.endswith("_async"):
        name = name[:-6]
    return name.replace("_", "-")
