"""Core extraction logic — discovers service classes and extracts SdkMetadata."""

from __future__ import annotations

import ast
import functools
import importlib
import inspect
import sys
import typing
from typing import Any, ForwardRef

# typing_extensions normalizes Unpack / get_origin / get_args across 3.10/3.11/3.12.
# Hard dependency declared in pyproject.toml (see ADR-022).
from typing_extensions import NotRequired, Required, Unpack, get_args, get_origin

from .auth_detector import AUTH_PARAM_NAMES, detect_constructor_auth, detect_module_auth
from .models import (
    AdapterResult,
    AuthPattern,
    AuthType,
    ConstructorParam,
    Diagnostic,
    DiagnosticSeverity,
    Operation,
    Parameter,
    Resource,
    SdkMetadata,
    TypeKind,
)
from .stub_parser import find_stubs, parse_stub_file
from .type_mapper import map_type
from ._utils import SERVICE_SUFFIXES, RESOURCE_CRUD_METHODS, class_to_noun, pascal_to_kebab


def extract(package_name: str, module_name: str | None = None) -> AdapterResult:
    """Extract SdkMetadata from a Python package.

    Args:
        package_name: Name of the installed Python package
        module_name: Optional specific module within the package
    """
    diagnostics: list[Diagnostic] = []

    # Fallback chain: try .pyi stubs first (ADR-013 compliance)
    stub_dir = find_stubs(package_name)
    if stub_dir is not None:
        target_module = module_name or package_name
        # Look for a matching .pyi file
        pyi_name = target_module.split(".")[-1] + ".pyi"
        pyi_path = stub_dir / pyi_name
        if not pyi_path.exists():
            pyi_path = stub_dir / "__init__.pyi"
        if pyi_path.exists():
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.INFO, "CB605",
                f"Using .pyi stubs from {stub_dir} (no runtime import needed)",
            ))
            resources = parse_stub_file(pyi_path, target_module, diagnostics)

            # Run auth detection on stub-derived constructor params
            auth_patterns: list[AuthPattern] = []
            for resource in resources:
                if resource.constructor_params:
                    auth = _detect_stub_constructor_auth(
                        resource.constructor_params, resource.source_class_name or "",
                        target_module, diagnostics,
                    )
                    if auth and auth not in auth_patterns:
                        auth_patterns.append(auth)
                        # Mark the auth param in constructor_params
                        for cp in resource.constructor_params:
                            if cp.name == auth.parameter_name:
                                cp.is_auth = True

            metadata = SdkMetadata(
                name=package_name,
                version="0.0.0",  # Cannot determine version from stubs alone
                resources=resources,
                auth_patterns=auth_patterns,
            )
            return AdapterResult(metadata=metadata, diagnostics=diagnostics)

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

        noun = class_to_noun(name) if is_service else pascal_to_kebab(name)
        if noun in seen_nouns:
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.WARNING, "CB202",
                f"Noun collision: '{name}' maps to '{noun}' which is already used",
            ))
            continue

        seen_nouns.add(noun)
        services.append((noun, obj))

    return services


def _detect_stub_constructor_auth(
    constructor_params: list[ConstructorParam],
    class_name: str,
    module_name: str,
    diagnostics: list[Diagnostic],
) -> AuthPattern | None:
    """Detect auth pattern from stub-derived constructor params (no runtime class)."""
    for cp in constructor_params:
        if cp.name.lower() in AUTH_PARAM_NAMES and cp.type_name in ("str", "string", "None"):
            prefix = module_name.split(".")[0].upper()
            env_var = f"{prefix}_{cp.name.upper()}"
            return AuthPattern(
                type=AuthType.API_KEY,
                env_var=env_var,
                parameter_name=cp.name,
            )
    return None


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
        # Unwrap decorators (@classmethod, @functools.wraps, etc.) so signature
        # inspection and the Unpack[TypedDict] AST walk both see the real function.
        method = inspect.unwrap(method)
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
        params = _extract_params(method, sig, hints, diagnostics)

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
    method: Any,
    sig: inspect.Signature,
    hints: dict[str, Any],
    diagnostics: list[Diagnostic],
) -> list[Parameter]:
    """Extract parameters from a method signature.

    For VAR_KEYWORD params annotated as `Unpack[TypedDict]` (PEP 692), the
    TypedDict's fields are lifted into flat parameters. See ADR-022.
    """
    params = []

    for pname, param in sig.parameters.items():
        if pname == "self":
            continue

        if param.kind == inspect.Parameter.VAR_POSITIONAL:
            continue

        if param.kind == inspect.Parameter.VAR_KEYWORD:
            unpacked = _try_resolve_unpack_kwargs(method, param, diagnostics)
            if unpacked is not None:
                params.extend(unpacked)
            # No Unpack annotation → silent skip (preserves pre-Step-17 behavior
            # for SDKs that use plain **kwargs).
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


# ---- PEP 692 Unpack[TypedDict] resolution (ADR-022) -----------------------
# Methods like `def list(**params: Unpack[CustomerListParams])` express their
# kwargs shape via a TypedDict that's typically imported only under
# `if TYPE_CHECKING:`. `typing.get_type_hints()` cannot resolve those refs at
# runtime. We AST-parse the defining module to discover the import target,
# `importlib.import_module` it, then walk `__required_keys__` /
# `__optional_keys__` / `__annotations__` to emit one flat Parameter per field.
#
# See docs/internal/step-17-pep692-unpack.md for the full design.


def _try_resolve_unpack_kwargs(
    method: Any,
    param: inspect.Parameter,
    diagnostics: list[Diagnostic],
) -> list[Parameter] | None:
    """Resolve `**kwargs: Unpack[TypedDict]` into flat parameters.

    Returns None when the annotation is not Unpack-shaped — caller preserves
    the existing zero-param skip behavior for plain **kwargs.

    Reads `param.annotation` directly (NOT a precomputed `hints` dict), because
    `typing.get_type_hints()` is exactly what fails on these ForwardRefs.
    """
    ann = param.annotation
    if ann is inspect.Parameter.empty:
        return None
    if get_origin(ann) is not Unpack:
        return None

    args = get_args(ann)
    if not args:
        return None
    target = args[0]

    td_cls = _resolve_unpack_target(method, target, diagnostics)
    if td_cls is None:
        return None  # CB607 already emitted by _resolve_unpack_target

    return _walk_typed_dict(method, td_cls, diagnostics)


def _resolve_unpack_target(
    method: Any,
    target: Any,
    diagnostics: list[Diagnostic],
) -> type | None:
    """Resolve `Unpack[X]`'s inner type to a concrete TypedDict class.

    `X` may be the class itself (rare — only when imported eagerly) or a
    `ForwardRef`. For ForwardRef, look up the name in the defining module's
    `if TYPE_CHECKING:` import table and `importlib.import_module` the target.
    """
    # Already a class? Validate it's TypedDict-shaped.
    if inspect.isclass(target):
        if hasattr(target, "__required_keys__"):
            return target
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.WARNING, "CB607",
            f"Unpack[{target!r}] resolved to a non-TypedDict class; skipping",
        ))
        return None

    # ForwardRef case: dig out the name and resolve via the module AST.
    name: str | None = None
    if isinstance(target, ForwardRef):
        name = target.__forward_arg__
    elif isinstance(target, str):
        name = target

    if name is None:
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.WARNING, "CB607",
            f"Unpack[?] has an unexpected annotation shape on {_method_label(method)}: {target!r}",
        ))
        return None

    module_name = getattr(method, "__module__", None) or ""
    module = sys.modules.get(module_name)
    module_file = getattr(module, "__file__", None) if module is not None else None
    if not module_file:
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.WARNING, "CB607",
            f"Unpack[ForwardRef({name!r})] on {_method_label(method)}: defining module has no source file; cannot resolve",
        ))
        return None

    import_table = _collect_type_checking_imports(module_file, module_name)
    target_module = import_table.get(name)
    if target_module is None:
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.WARNING, "CB607",
            f"Unpack[ForwardRef({name!r})] on {_method_label(method)}: "
            f"name not found in any `if TYPE_CHECKING:` import in {module_file}",
        ))
        return None

    try:
        mod = importlib.import_module(target_module)
        cls = getattr(mod, name, None)
    except ImportError as e:
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.WARNING, "CB607",
            f"Unpack[ForwardRef({name!r})] on {_method_label(method)}: "
            f"failed to import {target_module}: {e}",
        ))
        return None

    if cls is None or not hasattr(cls, "__required_keys__"):
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.WARNING, "CB607",
            f"Unpack[ForwardRef({name!r})] on {_method_label(method)}: "
            f"resolved {target_module}.{name} is not a TypedDict",
        ))
        return None

    return cls


def _walk_typed_dict(
    method: Any,
    td_cls: type,
    diagnostics: list[Diagnostic],
) -> list[Parameter]:
    """Emit one Parameter per TypedDict field.

    Source of truth for required/optional: `__required_keys__` /
    `__optional_keys__`. PEP 589 metaclass aggregates inherited fields into
    those frozensets automatically — no manual MRO walk needed for the key set.
    Field annotations may not be on the direct class dict (parent classes
    contribute too), so look up names walking `__mro__` as a fallback.

    Field-level ForwardRef resolution (e.g., `NotRequired[ForwardRef('str')]`)
    lands in PR 2 — for now, unwrapped types pass through `map_type` directly
    and unresolvable shapes fall back to `TypeKind.Other` via the existing
    map_type behavior.
    """
    required_keys = getattr(td_cls, "__required_keys__", frozenset())
    optional_keys = getattr(td_cls, "__optional_keys__", frozenset())
    all_keys = required_keys | optional_keys

    annotations: dict[str, Any] = {}
    for klass in reversed(td_cls.__mro__):
        annotations.update(getattr(klass, "__annotations__", {}))

    params: list[Parameter] = []
    for field_name in all_keys:
        raw_ann = annotations.get(field_name, Any)
        unwrapped = _strip_required_marker(raw_ann)
        type_ref = map_type(unwrapped, diagnostics)
        params.append(Parameter(
            name=field_name,
            type=type_ref,
            required=(field_name in required_keys),
            description=None,
        ))

    diagnostics.append(Diagnostic(
        DiagnosticSeverity.INFO, "CB606",
        f"Resolved Unpack[{td_cls.__name__}] on {_method_label(method)} "
        f"into {len(params)} parameter(s)",
    ))
    return params


def _strip_required_marker(annotation: Any) -> Any:
    """Unwrap `Required[X]` / `NotRequired[X]` to `X`."""
    origin = get_origin(annotation)
    if origin is Required or origin is NotRequired:
        args = get_args(annotation)
        if args:
            return args[0]
    return annotation


@functools.lru_cache(maxsize=None)
def _collect_type_checking_imports(module_file: str, module_name: str) -> dict[str, str]:
    """AST-parse `module_file` and return a name → absolute-source-module map.

    Scope: top-level `if TYPE_CHECKING:` blocks only. Direct `ImportFrom`
    statements only — `ast.Import`, star imports (`from x import *`), and
    nested conditions are intentionally ignored (callers see them as
    name-not-found and emit CB607).

    Relative imports (`from .params import X`) are resolved to their
    absolute module path using `module_name` as the anchor. Stripe-style
    SDKs use absolute imports, but synthetic fixtures and some libraries
    use relative imports; both must work.

    Cached: Stripe re-resolves the same ~41 TYPE_CHECKING imports across
    hundreds of operations in a single extract() call.
    """
    try:
        source = open(module_file, encoding="utf-8").read()
        tree = ast.parse(source, filename=module_file)
    except (OSError, SyntaxError):
        return {}

    table: dict[str, str] = {}
    for node in ast.iter_child_nodes(tree):
        if not isinstance(node, ast.If):
            continue
        if not _is_type_checking_guard(node.test):
            continue
        for stmt in node.body:
            if not isinstance(stmt, ast.ImportFrom):
                continue
            target = _resolve_import_from_target(stmt, module_name)
            if target is None:
                continue
            for alias in stmt.names:
                if alias.name == "*":
                    continue  # star imports out of scope
                imported_as = alias.asname or alias.name
                table[imported_as] = target
    return table


def _resolve_import_from_target(stmt: ast.ImportFrom, module_name: str) -> str | None:
    """Resolve `ImportFrom`'s target module name, handling relative imports.

    For `level=0` (absolute), returns `stmt.module` as-is.
    For `level>=1` (relative), strips `level` segments off `module_name`
    and appends `stmt.module` if present.
    """
    if stmt.level == 0:
        return stmt.module
    parts = module_name.split(".")
    if stmt.level > len(parts):
        return None  # invalid: `..` from a top-level module
    base = ".".join(parts[: -stmt.level])
    if stmt.module:
        return f"{base}.{stmt.module}" if base else stmt.module
    return base or None


def _is_type_checking_guard(test: ast.expr) -> bool:
    """Match `TYPE_CHECKING`, `typing.TYPE_CHECKING`, `t.TYPE_CHECKING`, etc."""
    if isinstance(test, ast.Name) and test.id == "TYPE_CHECKING":
        return True
    if isinstance(test, ast.Attribute) and test.attr == "TYPE_CHECKING":
        return True
    return False


def _method_label(method: Any) -> str:
    """Best-effort `Class.method` label for diagnostics."""
    qualname = getattr(method, "__qualname__", None) or getattr(method, "__name__", "<method>")
    return qualname
