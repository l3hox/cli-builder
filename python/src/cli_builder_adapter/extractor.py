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
    TypeRef,
)
from ._naming import (
    MIN_ENTRY_CLASS_METHODS,
    noun_to_resource_name,
    parse_verb_noun,
    skip_reason,
)
from .stub_parser import find_stubs, parse_stub_file
from .type_mapper import map_type
from ._utils import SERVICE_SUFFIXES, RESOURCE_CRUD_METHODS, class_to_noun, pascal_to_kebab


def extract(
    package_name: str,
    module_name: str | None = None,
    entry_class: str | None = None,
) -> AdapterResult:
    """Extract SdkMetadata from a Python package.

    Args:
        package_name: Name of the installed Python package.
        module_name: Optional specific module within the package.
        entry_class: Optional explicit entry-class name for single-client
            discovery mode (ADR-023). When provided, multi-service discovery
            is skipped entirely and the named class is used as the single
            entry. When None, the adapter falls back to single-client
            discovery only if multi-service finds zero candidates.
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

    # Collect candidate classes once — shared between multi-service and
    # single-client discovery paths. See ADR-023.
    candidate_classes = _collect_candidate_classes(module)

    # Discover service classes (multi-service mode — the default).
    # Skipped entirely when `entry_class` is explicitly provided.
    service_classes: list[tuple[str, type]]
    if entry_class is None:
        service_classes = _discover_services(module, candidate_classes, diagnostics)
    else:
        service_classes = []

    discovery_mode = "multi_service"
    auth_patterns: list[AuthPattern] = []
    resources: list[Resource] = []

    if entry_class is not None or not service_classes:
        # Single-client mode (ADR-023): either explicitly requested via
        # `entry_class`, or multi-service discovery found nothing and we
        # fall back. Picks one entry class via name/method-count heuristic
        # (or by explicit name when provided) and walks its methods.
        entry_cls = _discover_single_client(
            module, candidate_classes, package_name, entry_class, diagnostics,
        )
        if entry_cls is not None:
            discovery_mode = "single_client"
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.INFO, "CB611",
                f"Single-client discovery mode engaged on entry class "
                f"'{entry_cls.__name__}' ({entry_cls.__module__})",
            ))
            auth = detect_constructor_auth(entry_cls, diagnostics)
            if auth and auth not in auth_patterns:
                auth_patterns.append(auth)
            ctor_params = _extract_constructor_params(entry_cls, auth)
            resources.extend(_extract_single_client_resources(
                entry_cls, ctor_params, diagnostics,
            ))

    # Multi-service path (when service_classes is non-empty AND no explicit
    # entry_class was passed). Each `*Service`/`*Client`/`*Api` class becomes
    # a resource; each public method becomes an operation.
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
        discovery_mode=discovery_mode,
    )

    return AdapterResult(metadata=metadata, diagnostics=diagnostics)


def _collect_candidate_classes(module: Any) -> list[tuple[str, type]]:
    """Return all top-level candidate classes from a module, sorted by name.

    Used by both `_discover_services` (multi-service mode) and
    `_discover_single_client` (single-client mode). Walks `dir(module)` plus
    any lazy-load registry (`_import_map`) so modules with `__getattr__`-based
    deferred imports (Stripe-style) surface their classes correctly.

    Returns `(name, class)` tuples. Filtering by name pattern / suffix is the
    caller's responsibility.
    """
    candidate_names: set[str] = set()
    for name in dir(module):
        if not name.startswith("_"):
            candidate_names.add(name)
    import_map = getattr(module, "_import_map", None)
    if isinstance(import_map, dict):
        for name, entry in import_map.items():
            if not name.startswith("_") and name[0:1].isupper():
                # Only non-submodule entries (actual classes)
                if isinstance(entry, tuple) and len(entry) == 2 and not entry[1]:
                    candidate_names.add(name)

    out: list[tuple[str, type]] = []
    module_root = module.__name__.split(".")[0]
    for name in sorted(candidate_names):
        try:
            obj = getattr(module, name)
        except Exception:
            continue
        if not inspect.isclass(obj):
            continue
        # Module-origin check — skip classes re-exported from other packages
        obj_module = getattr(obj, "__module__", "")
        if not obj_module.startswith(module_root):
            continue
        out.append((name, obj))
    return out


def _discover_services(
    module: Any,
    candidate_classes: list[tuple[str, type]],
    diagnostics: list[Diagnostic],
) -> list[tuple[str, type]]:
    """Find service and resource classes (multi-service discovery mode).

    Discovery strategies (in order):
    1. Classes matching *Client/*Service/*Api suffixes (standard pattern)
    2. Classes with CRUD classmethods (resource pattern, e.g., stripe.Customer)

    `candidate_classes` is the output of `_collect_candidate_classes(module)`.
    """
    services = []
    seen_nouns: set[str] = set()

    for name, obj in candidate_classes:
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


def _discover_single_client(
    module: Any,
    candidate_classes: list[tuple[str, type]],
    package_name: str,
    explicit_name: str | None,
    diagnostics: list[Diagnostic],
) -> type | None:
    """Select an entry class for single-client discovery mode (ADR-023).

    When `explicit_name` is provided, returns the class with that exact name
    if it meets the method-count threshold, otherwise emits CB609 + None.

    When `explicit_name` is None, auto-detects: candidate classes matching
    the entry-class name pattern (`<package>`, `<Package>`, `Client`, `Api`,
    `*Client`, `*Api`) AND having `>= MIN_ENTRY_CLASS_METHODS` public methods
    are entries. If exactly one match, returns it. Zero or multiple → CB609.
    """
    if explicit_name is not None:
        match = next((cls for name, cls in candidate_classes if name == explicit_name), None)
        if match is None:
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.WARNING, "CB609",
                f"--entry-class '{explicit_name}' not found in module "
                f"'{module.__name__}' (class not found)",
            ))
            return None
        method_count = _count_public_methods(match)
        if method_count < MIN_ENTRY_CLASS_METHODS:
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.WARNING, "CB609",
                f"--entry-class '{explicit_name}' has {method_count} public methods, "
                f"below threshold ({MIN_ENTRY_CLASS_METHODS}). Refusing to use as entry class.",
            ))
            return None
        return match

    # Auto-detection: filter candidates by name pattern + method count.
    pkg_capitalized = package_name.capitalize()
    matches: list[type] = []
    for name, cls in candidate_classes:
        if not _matches_entry_class_pattern(name, pkg_capitalized):
            continue
        if _count_public_methods(cls) < MIN_ENTRY_CLASS_METHODS:
            continue
        matches.append(cls)

    if not matches:
        # Note: we don't emit CB609 here. The "no entry class found" case is
        # only an error when the user EXPECTED single-client discovery. The
        # caller (extract()) decided to try single-client because multi-service
        # found nothing — if THIS also finds nothing, the SDK isn't supported.
        return None

    if len(matches) > 1:
        names = ", ".join(c.__name__ for c in matches)
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.WARNING, "CB609",
            f"Single-client discovery is ambiguous: multiple entry-class candidates "
            f"meet the heuristic ({names}). Pass --entry-class <Name> to disambiguate.",
        ))
        return None

    return matches[0]


def _matches_entry_class_pattern(name: str, package_capitalized: str) -> bool:
    """Whether `name` matches the entry-class name heuristic (ADR-023).

    Matches in order:
      1. equals package-capitalized name (PyGithub-style: `github` → `Github`)
      2. literally 'Client' or 'Api' (no namespacing)
      3. ends in 'Client' or 'Api' (catches `NotionClient`, `SlackApi`)
      4. starts with package-capitalized AND does NOT end in any multi-service
         suffix (catches `GithubMain`/`NotionAdmin` — single-client patterns
         that wouldn't be picked up by multi-service strategy 1)
    """
    if name == package_capitalized:
        return True
    if name in ("Client", "Api"):
        return True
    if name.endswith("Client") or name.endswith("Api"):
        return True
    if (
        package_capitalized
        and name.startswith(package_capitalized)
        and not any(name.endswith(suf) for suf in SERVICE_SUFFIXES)
    ):
        return True
    return False


def _count_public_methods(cls: type) -> int:
    """Count public methods on a class — includes def, classmethod, async def.

    Used by the entry-class threshold check. Must not silently undercount
    `@classmethod` or `async def` methods — both are first-class CLI op
    candidates on real SDKs (Slack, async HTTP clients).
    """
    count = 0
    for name in dir(cls):
        if name.startswith("_"):
            continue
        # `getattr_static` avoids descriptors that might fire on bound access.
        raw = inspect.getattr_static(cls, name, None)
        if raw is None:
            continue
        if isinstance(raw, classmethod) or inspect.isfunction(raw):
            count += 1
            continue
        # Async methods come through as functions when defined via `async def`.
        if callable(raw) and inspect.iscoroutinefunction(raw):
            count += 1
    return count


def _extract_single_client_resources(
    entry_cls: type,
    ctor_params: list[ConstructorParam],
    diagnostics: list[Diagnostic],
) -> list[Resource]:
    """Walk entry class's public methods → group into Resources by noun.

    Per ADR-023: single-client mode. This does NOT reuse `_extract_operations`
    because `_method_to_verb` flattens verb+noun into one CLI op name. Here we
    need the opposite — split verb from noun so each becomes its own dimension
    in the CLI (`github-cli repo get owner/name`).

    Shares `_extract_params` with multi-service mode (preserves Step 17
    Unpack[TypedDict] resolution).
    """
    resources_by_noun: dict[str, dict[str, Any]] = {}
    cls_source_module = getattr(entry_cls, "__module__", "")

    for name in sorted(dir(entry_cls)):
        if name.startswith("_"):
            continue
        # Async variants skipped — prefer sync (same convention as multi-service)
        if name.endswith("_async"):
            continue
        raw = inspect.getattr_static(entry_cls, name, None)
        if raw is None:
            continue

        # Resolve to the underlying function for signature inspection.
        if isinstance(raw, classmethod):
            method = getattr(entry_cls, name)
        elif inspect.isfunction(raw) or (callable(raw) and inspect.iscoroutinefunction(raw)):
            method = raw
        else:
            continue
        method = inspect.unwrap(method)

        parsed = parse_verb_noun(name)
        if parsed is None:
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.WARNING, "CB610",
                f"Skipped '{entry_cls.__name__}.{name}': {skip_reason(name)}",
            ))
            continue
        verb, noun = parsed

        try:
            sig = inspect.signature(method)
        except (ValueError, TypeError):
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.WARNING, "CB602",
                f"Could not inspect signature of '{entry_cls.__name__}.{name}' — skipping",
            ))
            continue

        # Rule 4: skip if any positional parameter (other than self/cls) has a
        # `type[T]` or `Type[T]` annotation — factory method, not a CLI op.
        if _has_type_param(sig):
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.WARNING, "CB610",
                f"Skipped '{entry_cls.__name__}.{name}': "
                f"first param is `type[T]` (factory method, not a CLI operation)",
            ))
            continue

        hints: dict[str, Any] = {}
        try:
            hints = typing.get_type_hints(method)
        except Exception:
            pass

        params = _extract_params(method, sig, hints, diagnostics)
        return_annotation = hints.get("return", sig.return_annotation)
        return_type = map_type(return_annotation, diagnostics)
        is_streaming = (
            return_type.kind == TypeKind.GENERIC and return_type.name == "AsyncIterator"
        )

        resource_name = noun_to_resource_name(noun)
        resource_entry = resources_by_noun.setdefault(resource_name, {
            "operations": [],
            "source_methods": [],
        })
        resource_entry["operations"].append(Operation(
            name=verb,
            description=inspect.getdoc(method),
            parameters=params,
            return_type=return_type,
            is_streaming=is_streaming,
            source_method_name=name,
        ))
        resource_entry["source_methods"].append(name)

    # Build Resource list deterministically (sorted by resource name)
    resources = []
    for resource_name in sorted(resources_by_noun):
        entry = resources_by_noun[resource_name]
        resources.append(Resource(
            name=resource_name,
            description=None,  # Inferred from entry class — no per-resource doc
            operations=entry["operations"],
            source_class_name=entry_cls.__name__,
            source_module=cls_source_module,
            # Constructor params are attached to the first resource only — all
            # resources share the same single-client entry. Generator wires
            # auth once.
            constructor_params=ctor_params if (ctor_params and resource_name == sorted(resources_by_noun)[0]) else None,
            has_parameterless_ctor=_has_parameterless_init(entry_cls),
        ))
    return resources


def _has_type_param(sig: inspect.Signature) -> bool:
    """Whether any non-self parameter has a `type`, `type[T]`, or `Type[T]` annotation.

    These are factory-style methods (`register_class(klass: type, ...)`,
    `create_from_raw_data(klass: type[T], ...)`), not CLI operations. Filter
    them out before extraction.
    """
    for pname, param in sig.parameters.items():
        if pname in ("self", "cls"):
            continue
        ann = param.annotation
        if ann is inspect.Parameter.empty:
            continue
        # Bare `type` annotation (`klass: type`)
        if ann is type:
            return True
        # `type[T]` (PEP 585) and `Type[T]` (typing) — both have `type` as origin
        origin = typing.get_origin(ann)
        if origin is type:
            return True
        # Pre-3.9 / explicit `typing.Type` fallback
        if hasattr(ann, "__origin__") and getattr(ann, "__origin__", None) is type:
            return True
    return False


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

    Composed from three single-responsibility helpers (per ADR-022):
    1. `_inspect_unpack_annotation` — pure annotation inspection.
    2. `_collect_type_checking_imports` — pure AST walk (cached).
    3. `_resolve_class` — importlib.import_module + getattr.
    """
    inner = _inspect_unpack_annotation(param.annotation)
    if inner is None:
        return None  # not an Unpack[...] annotation; preserve plain-kwargs skip

    # If inner is already a real class, validate and walk it directly.
    if inspect.isclass(inner):
        if not hasattr(inner, "__required_keys__"):
            diagnostics.append(Diagnostic(
                DiagnosticSeverity.WARNING, "CB607",
                f"Unpack[{inner!r}] on {_method_label(method)} resolved to a non-TypedDict class; skipping",
            ))
            return None
        return _walk_typed_dict(method, inner, diagnostics)

    # Inner is a ForwardRef — discover where it's imported from, then import it.
    if not isinstance(inner, ForwardRef):
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.WARNING, "CB607",
            f"Unpack[?] has an unexpected annotation shape on {_method_label(method)}: {inner!r}",
        ))
        return None

    td_cls = _resolve_class_from_method(method, inner, diagnostics)
    if td_cls is None:
        return None  # CB607 already emitted
    return _walk_typed_dict(method, td_cls, diagnostics)


def _inspect_unpack_annotation(annotation: Any) -> Any | None:
    """Pure annotation inspection. Returns Unpack's inner target, or None.

    No I/O, no diagnostics, no module lookup — just structural pattern matching
    on the annotation. Caller decides what to do with the result.
    """
    if annotation is inspect.Parameter.empty:
        return None
    if get_origin(annotation) is not Unpack:
        return None
    args = get_args(annotation)
    if not args:
        return None
    return args[0]


def _resolve_class_from_method(
    method: Any,
    forward: ForwardRef,
    diagnostics: list[Diagnostic],
) -> type | None:
    """Resolve a `ForwardRef` referenced by `method`'s annotations.

    Looks up the name in the defining module's `if TYPE_CHECKING:` import
    table and dynamically imports the target. Emits CB607 on failure.
    """
    name = forward.__forward_arg__
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

    cls = _resolve_class(target_module, name)
    if cls is None:
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.WARNING, "CB607",
            f"Unpack[ForwardRef({name!r})] on {_method_label(method)}: "
            f"failed to import {target_module}.{name}",
        ))
        return None

    if not hasattr(cls, "__required_keys__"):
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.WARNING, "CB607",
            f"Unpack[ForwardRef({name!r})] on {_method_label(method)}: "
            f"resolved {target_module}.{name} is not a TypedDict",
        ))
        return None

    return cls


def _resolve_class(module_path: str, name: str) -> type | None:
    """importlib.import_module + getattr, returning None on any failure.

    Pure resolution mechanic — no diagnostics. Caller wraps with the right
    error message for its context.
    """
    try:
        mod = importlib.import_module(module_path)
    except ImportError:
        return None
    return getattr(mod, name, None)


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

    Each field's annotation is resolved independently — a single bad
    annotation (recursive type, missing import, malformed ForwardRef) emits
    CB608 and falls back to TypeKind.Other, but does NOT abort the whole walk.
    Nested TypedDicts are intentionally NOT recursed into: emitted as
    TypeKind.Other + CB608 so the user routes them through `--json-input`
    (mirrors C# ADR-007 flattening policy).
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
        type_ref = _resolve_field_type(
            field_name=field_name,
            raw_ann=raw_ann,
            td_cls=td_cls,
            method=method,
            diagnostics=diagnostics,
        )
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


def _resolve_field_type(
    field_name: str,
    raw_ann: Any,
    td_cls: type,
    method: Any,
    diagnostics: list[Diagnostic],
) -> "TypeRef":
    """Resolve a single TypedDict field's annotation to a `TypeRef`.

    Each call is wrapped — failures emit CB608 and return TypeKind.Other so
    a single bad field doesn't poison the whole TypedDict's parameter list.

    Resolution pipeline:
    1. Strip `Required[X]` / `NotRequired[X]` wrappers.
    2. If inner is a ForwardRef or string, eval against the TypedDict's
       defining-module namespace.
    3. If the resolved type is itself a TypedDict, fall back to TypeKind.Other
       (don't recurse — see ADR-022 + C# ADR-007 alignment).
    4. Otherwise pass to `map_type`, which is pure and stays pure.
    """
    try:
        unwrapped = _strip_required_marker(raw_ann)
        resolved, ok = _try_eval_forward(unwrapped, td_cls)
        if not ok:
            _emit_cb608(field_name, td_cls, method, unwrapped, diagnostics)
            return TypeRef(kind=TypeKind.OTHER, name=str(unwrapped))

        if inspect.isclass(resolved) and hasattr(resolved, "__required_keys__"):
            # Nested TypedDict — by design, don't recurse. User routes via --json-input.
            _emit_cb608(
                field_name, td_cls, method,
                f"nested TypedDict {resolved.__name__} — use --json-input",
                diagnostics,
            )
            return TypeRef(kind=TypeKind.OTHER, name=resolved.__name__)

        return map_type(resolved, diagnostics)
    except Exception as e:  # defensive — never let one field crash extraction
        _emit_cb608(field_name, td_cls, method, f"exception: {e}", diagnostics)
        return TypeRef(kind=TypeKind.OTHER, name=str(raw_ann))


def _try_eval_forward(annotation: Any, td_cls: type) -> tuple[Any, bool]:
    """Evaluate string / ForwardRef annotations against td_cls's defining module.

    Returns (resolved_or_original, ok). `ok=False` when evaluation was needed
    but failed; caller treats that as the CB608 fallback path.
    """
    if isinstance(annotation, ForwardRef):
        name = annotation.__forward_arg__
    elif isinstance(annotation, str):
        name = annotation
    else:
        return annotation, True  # already a real type; nothing to do

    mod = sys.modules.get(getattr(td_cls, "__module__", "") or "")
    if mod is None:
        return annotation, False

    try:
        # Evaluate in the TypedDict's defining-module namespace. This handles
        # `'str'`, `'int | None'`, `'NestedClass'`, `'List[str]'`, etc.
        return eval(name, mod.__dict__, None), True
    except Exception:
        return annotation, False


def _emit_cb608(
    field_name: str,
    td_cls: type,
    method: Any,
    detail: Any,
    diagnostics: list[Diagnostic],
) -> None:
    diagnostics.append(Diagnostic(
        DiagnosticSeverity.WARNING, "CB608",
        f"TypedDict field '{td_cls.__name__}.{field_name}' on "
        f"{_method_label(method)}: could not resolve ({detail}); "
        f"emitted as TypeKind.Other — pass value via --json-input.",
    ))


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
