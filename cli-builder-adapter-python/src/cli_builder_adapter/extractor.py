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

# CRUD classmethod names that indicate a resource class (e.g., stripe.Customer)
RESOURCE_CRUD_METHODS = {"create", "retrieve", "list", "delete"}


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
    """Find service and resource classes.

    Discovery strategies (in order):
    1. Classes matching *Client/*Service/*Api suffixes (standard pattern)
    2. Classes with CRUD classmethods (resource pattern, e.g., stripe.Customer)

    Uses dir(module) + lazy-load registries (e.g., _import_map) to handle
    modules that use __getattr__ for deferred imports.
    """
    services = []
    seen_nouns: set[str] = set()

    # Collect candidate names from dir() and any lazy-load registry
    candidate_names: set[str] = set()
    for name in dir(module):
        if not name.startswith("_"):
            candidate_names.add(name)
    # Lazy-loaded modules (e.g., Stripe) may expose names via _import_map
    import_map = getattr(module, "_import_map", None)
    if isinstance(import_map, dict):
        for name, entry in import_map.items():
            if not name.startswith("_") and name[0:1].isupper():
                # Only non-submodule entries (actual classes)
                if isinstance(entry, tuple) and len(entry) == 2 and not entry[1]:
                    candidate_names.add(name)

    for name in sorted(candidate_names):
        try:
            obj = getattr(module, name)
        except Exception:
            continue

        if not inspect.isclass(obj):
            continue

        # Strategy 1: service naming pattern (*Client, *Service, *Api)
        is_service = any(name.endswith(suffix) for suffix in SERVICE_SUFFIXES)

        # Strategy 2: resource class with CRUD classmethods
        is_resource = False
        if not is_service:
            crud_count = sum(
                1 for m in RESOURCE_CRUD_METHODS
                if isinstance(inspect.getattr_static(obj, m, None), classmethod)
            )
            is_resource = crud_count >= 2  # At least 2 CRUD methods

        if not is_service and not is_resource:
            continue

        # Module origin check — skip re-exports from other packages
        obj_module = getattr(obj, "__module__", "")
        module_root = module.__name__.split(".")[0]
        if not obj_module.startswith(module_root):
            continue

        noun = _class_to_noun(name) if is_service else _pascal_to_kebab(name)
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
    """Extract public methods as operations.

    Handles both instance methods (inspect.isfunction) and classmethods
    (for resource-class patterns like stripe.Customer.create).
    Skips async variants (*_async) when the sync version exists.
    """
    operations = []
    seen_verbs: set[str] = set()

    # Collect both instance methods and classmethods
    methods_to_process: list[tuple[str, Any]] = []

    for name in dir(cls):
        if name.startswith("_"):
            continue

        # Skip async variants — prefer sync methods
        if name.endswith("_async"):
            continue

        raw = inspect.getattr_static(cls, name, None)
        if raw is None:
            continue

        if isinstance(raw, classmethod):
            # Classmethod — get the underlying function
            bound = getattr(cls, name)
            methods_to_process.append((name, bound))
        elif inspect.isfunction(raw):
            # Instance method
            methods_to_process.append((name, raw))

    for name, method in methods_to_process:
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

        # Deduplicate verbs (classmethods + instance methods may overlap)
        if verb in seen_verbs:
            continue
        seen_verbs.add(verb)

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
