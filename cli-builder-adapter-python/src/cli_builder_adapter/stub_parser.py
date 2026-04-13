"""Parse .pyi type stubs to extract metadata without runtime import.

ADR-013 compliance: .pyi stubs are package artifacts that describe types
without executing code. This module extracts service classes and operations
from stubs via ast.parse.
"""

from __future__ import annotations

import ast
import importlib.util
import sys
from pathlib import Path
from typing import Any

from .models import (
    ConstructorParam,
    Diagnostic,
    DiagnosticSeverity,
    Operation,
    Parameter,
    Resource,
    TypeKind,
    TypeRef,
)

# Service class name suffixes (same as extractor)
SERVICE_SUFFIXES = ("Client", "Service", "Api")

# CRUD classmethod names for resource class detection
RESOURCE_CRUD_METHODS = {"create", "retrieve", "list", "delete"}

# Primitive type name mapping
PRIMITIVE_NAMES = {
    "str", "int", "float", "bool", "bytes", "None",
    "datetime", "date", "timedelta",
}


def find_stubs(package_name: str) -> Path | None:
    """Find .pyi stub directory for a package.

    Checks in order:
    1. Inline stubs: .pyi files alongside .py in the package directory
    2. Stub-only package: {package_name}-stubs in site-packages
    """
    # 1. Inline stubs
    spec = importlib.util.find_spec(package_name)
    if spec and spec.origin:
        pkg_dir = Path(spec.origin).parent
        pyi_files = list(pkg_dir.glob("*.pyi"))
        if pyi_files:
            return pkg_dir

    # 2. Stub-only package ({package}-stubs)
    stub_name = f"{package_name}-stubs"
    for path in sys.path:
        stub_dir = Path(path) / stub_name
        if stub_dir.is_dir():
            pyi_files = list(stub_dir.glob("*.pyi"))
            if pyi_files:
                return stub_dir

    return None


def parse_stub_file(
    pyi_path: Path,
    module_name: str,
    diagnostics: list[Diagnostic],
) -> list[Resource]:
    """Parse a .pyi file and extract service/resource classes."""
    try:
        source = pyi_path.read_text(encoding="utf-8")
        tree = ast.parse(source, filename=str(pyi_path))
    except SyntaxError as e:
        diagnostics.append(Diagnostic(
            DiagnosticSeverity.WARNING, "CB604",
            f"Malformed stub file {pyi_path}: {e}",
        ))
        return []

    resources = []
    for node in ast.iter_child_nodes(tree):
        if isinstance(node, ast.ClassDef):
            resource = _extract_class(node, module_name, diagnostics)
            if resource:
                resources.append(resource)

    return resources


def _extract_class(
    node: ast.ClassDef,
    module_name: str,
    diagnostics: list[Diagnostic],
) -> Resource | None:
    """Extract a Resource from an AST class definition."""
    name = node.name

    # Check if it's a service or resource class
    is_service = any(name.endswith(suffix) for suffix in SERVICE_SUFFIXES)
    is_resource = _has_crud_classmethods(node)

    if not is_service and not is_resource:
        return None

    # Extract noun
    if is_service:
        noun = _class_to_noun(name)
    else:
        noun = _pascal_to_kebab(name)

    # Extract operations
    operations = []
    constructor_params: list[ConstructorParam] = []
    has_parameterless_ctor = True

    for item in node.body:
        if isinstance(item, ast.FunctionDef):
            if item.name == "__init__":
                constructor_params, has_parameterless_ctor = _extract_init_params(
                    item, diagnostics
                )
            elif not item.name.startswith("_"):
                op = _extract_method(item, diagnostics)
                if op:
                    operations.append(op)

    return Resource(
        name=noun,
        description=ast.get_docstring(node),
        operations=operations,
        source_class_name=name,
        source_module=module_name,
        constructor_params=constructor_params if constructor_params else None,
        has_parameterless_ctor=has_parameterless_ctor,
    )


def _has_crud_classmethods(node: ast.ClassDef) -> bool:
    """Check if a class has >= 2 CRUD classmethods."""
    crud_count = 0
    for item in node.body:
        if isinstance(item, ast.FunctionDef) and item.name in RESOURCE_CRUD_METHODS:
            for decorator in item.decorator_list:
                if isinstance(decorator, ast.Name) and decorator.id == "classmethod":
                    crud_count += 1
                    break
    return crud_count >= 2


def _extract_method(
    node: ast.FunctionDef,
    diagnostics: list[Diagnostic],
) -> Operation | None:
    """Extract an Operation from an AST method definition."""
    # Skip async variants
    if node.name.endswith("_async"):
        return None

    params = _extract_params(node, diagnostics)
    return_type = _annotation_to_typeref(node.returns, diagnostics)

    verb = node.name.replace("_", "-")
    is_streaming = (
        return_type.kind == TypeKind.GENERIC and return_type.name == "AsyncIterator"
    )

    return Operation(
        name=verb,
        description=ast.get_docstring(node),
        parameters=params,
        return_type=return_type,
        is_streaming=is_streaming,
        source_method_name=node.name,
    )


def _extract_params(
    node: ast.FunctionDef,
    diagnostics: list[Diagnostic],
) -> list[Parameter]:
    """Extract parameters from an AST function definition."""
    params = []
    args = node.args

    # Count defaults to determine which args are required
    num_defaults = len(args.defaults)
    num_args = len(args.args)

    for i, arg in enumerate(args.args):
        if arg.arg in ("self", "cls"):
            continue

        type_ref = _annotation_to_typeref(arg.annotation, diagnostics)
        # Required if no default value
        has_default = i >= (num_args - num_defaults)
        params.append(Parameter(
            name=arg.arg,
            type=type_ref,
            required=not has_default,
        ))

    return params


def _extract_init_params(
    node: ast.FunctionDef,
    diagnostics: list[Diagnostic],
) -> tuple[list[ConstructorParam], bool]:
    """Extract __init__ parameters for constructor info."""
    params = []
    has_parameterless = True
    args = node.args
    num_defaults = len(args.defaults)
    num_args = len(args.args)

    for i, arg in enumerate(args.args):
        if arg.arg == "self":
            continue

        has_default = i >= (num_args - num_defaults)
        if not has_default:
            has_parameterless = False

        type_name = _annotation_to_name(arg.annotation)
        params.append(ConstructorParam(
            name=arg.arg,
            type_name=type_name,
            type_module=None,
            is_auth=False,  # Auth detection happens later
            is_required=not has_default,
        ))

    return params, has_parameterless


def _annotation_to_typeref(
    node: ast.expr | None,
    diagnostics: list[Diagnostic],
) -> TypeRef:
    """Convert an AST annotation node to a TypeRef."""
    if node is None:
        return TypeRef(kind=TypeKind.OTHER, name="object")

    # ast.Constant(None) — NoneType
    if isinstance(node, ast.Constant) and node.value is None:
        return TypeRef(kind=TypeKind.PRIMITIVE, name="None")

    # ast.Name("str"), ast.Name("int"), etc.
    if isinstance(node, ast.Name):
        name = node.id
        if name in PRIMITIVE_NAMES:
            return TypeRef(kind=TypeKind.PRIMITIVE, name=name)
        if name == "None":
            return TypeRef(kind=TypeKind.PRIMITIVE, name="None")
        # Unknown class
        return TypeRef(kind=TypeKind.CLASS, name=name)

    # ast.Subscript — generic types: list[T], dict[K,V], Optional[T]
    if isinstance(node, ast.Subscript):
        return _subscript_to_typeref(node, diagnostics)

    # ast.BinOp with BitOr — PEP 604 union: str | None
    if isinstance(node, ast.BinOp) and isinstance(node.op, ast.BitOr):
        return _union_to_typeref([node.left, node.right], diagnostics)

    # ast.Attribute — qualified name: typing.Optional, etc.
    if isinstance(node, ast.Attribute):
        return TypeRef(kind=TypeKind.CLASS, name=node.attr)

    # Fallback
    return TypeRef(kind=TypeKind.OTHER, name="object")


def _subscript_to_typeref(
    node: ast.Subscript,
    diagnostics: list[Diagnostic],
) -> TypeRef:
    """Handle subscript annotations: list[T], Optional[T], dict[K,V]."""
    base_name = ""
    if isinstance(node.value, ast.Name):
        base_name = node.value.id
    elif isinstance(node.value, ast.Attribute):
        base_name = node.value.attr

    # Optional[T] → inner type with is_nullable
    if base_name == "Optional":
        inner = _annotation_to_typeref(node.slice, diagnostics)
        return TypeRef(
            kind=inner.kind, name=inner.name,
            is_nullable=True,
            is_abstract=inner.is_abstract,
            generic_arguments=inner.generic_arguments,
            enum_values=inner.enum_values,
            properties=inner.properties,
            element_type=inner.element_type,
            module=inner.module,
        )

    # list[T] → Array
    if base_name == "list":
        elem = _annotation_to_typeref(node.slice, diagnostics)
        return TypeRef(kind=TypeKind.ARRAY, name="list", element_type=elem)

    # dict[K, V] → Dictionary
    if base_name == "dict":
        if isinstance(node.slice, ast.Tuple) and len(node.slice.elts) == 2:
            ga = [_annotation_to_typeref(e, diagnostics) for e in node.slice.elts]
            return TypeRef(kind=TypeKind.DICTIONARY, name="dict", generic_arguments=ga)
        return TypeRef(kind=TypeKind.DICTIONARY, name="dict")

    # AsyncIterator[T] → Generic
    if base_name == "AsyncIterator":
        ga = [_annotation_to_typeref(node.slice, diagnostics)]
        return TypeRef(kind=TypeKind.GENERIC, name="AsyncIterator", generic_arguments=ga)

    # Other generic
    if isinstance(node.slice, ast.Tuple):
        ga = [_annotation_to_typeref(e, diagnostics) for e in node.slice.elts]
    else:
        ga = [_annotation_to_typeref(node.slice, diagnostics)]
    return TypeRef(kind=TypeKind.GENERIC, name=base_name, generic_arguments=ga)


def _union_to_typeref(
    members: list[ast.expr],
    diagnostics: list[Diagnostic],
) -> TypeRef:
    """Handle union types: str | None → Optional[str]."""
    # Flatten nested unions
    flat: list[ast.expr] = []
    for m in members:
        if isinstance(m, ast.BinOp) and isinstance(m.op, ast.BitOr):
            flat.extend([m.left, m.right])
        else:
            flat.append(m)

    non_none = [m for m in flat if not _is_none_node(m)]
    has_none = len(non_none) < len(flat)

    if len(non_none) == 1 and has_none:
        inner = _annotation_to_typeref(non_none[0], diagnostics)
        return TypeRef(
            kind=inner.kind, name=inner.name,
            is_nullable=True,
            is_abstract=inner.is_abstract,
            generic_arguments=inner.generic_arguments,
            enum_values=inner.enum_values,
            properties=inner.properties,
            element_type=inner.element_type,
            module=inner.module,
        )

    return TypeRef(kind=TypeKind.OTHER, name="Union")


def _is_none_node(node: ast.expr) -> bool:
    """Check if an AST node represents None."""
    if isinstance(node, ast.Constant) and node.value is None:
        return True
    if isinstance(node, ast.Name) and node.id == "None":
        return True
    return False


def _annotation_to_name(node: ast.expr | None) -> str:
    """Get a simple string name from an annotation (for constructor params)."""
    if node is None:
        return "object"
    if isinstance(node, ast.Name):
        return node.id
    if isinstance(node, ast.Attribute):
        return node.attr
    if isinstance(node, ast.Constant) and node.value is None:
        return "None"
    return "object"


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
