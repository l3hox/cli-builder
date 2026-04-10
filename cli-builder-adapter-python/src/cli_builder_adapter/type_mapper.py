"""Map Python type annotations to TypeRef."""

from __future__ import annotations

import collections.abc
import datetime as dt
import inspect
import types
import typing
from abc import ABC
from enum import Enum as PyEnum
from typing import Any, get_args, get_origin

from .models import Diagnostic, DiagnosticSeverity, Parameter, TypeKind, TypeRef


def map_type(annotation: Any, diagnostics: list[Diagnostic] | None = None) -> TypeRef:
    """Map a Python type annotation to a TypeRef."""
    if annotation is None or annotation is inspect.Parameter.empty:
        return TypeRef(kind=TypeKind.OTHER, name="object")

    # typing.Any → Other (must check before isinstance(annotation, type))
    if annotation is typing.Any:
        return TypeRef(kind=TypeKind.OTHER, name="object")

    # Handle None / NoneType
    if annotation is type(None):
        return TypeRef(kind=TypeKind.PRIMITIVE, name="None")

    # Normalize PEP 604 union (str | None) to typing.Union
    origin = get_origin(annotation)
    args = get_args(annotation)
    if isinstance(annotation, types.UnionType):
        origin = typing.Union
        args = get_args(annotation)

    if origin is typing.Union:
        # Optional[T] is Union[T, None]
        non_none = [a for a in args if a is not type(None)]
        if len(non_none) == 1 and type(None) in args:
            inner = map_type(non_none[0], diagnostics)
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
        # Non-Optional union
        return TypeRef(kind=TypeKind.OTHER, name="Union")

    # list[T] → Array
    if origin is list:
        if args:
            element = map_type(args[0], diagnostics)
            return TypeRef(kind=TypeKind.ARRAY, name="list", element_type=element)
        return TypeRef(kind=TypeKind.ARRAY, name="list")

    # dict[K, V] → Dictionary
    if origin is dict:
        if len(args) == 2:
            ga = [map_type(a, diagnostics) for a in args]
            return TypeRef(kind=TypeKind.DICTIONARY, name="dict", generic_arguments=ga)
        return TypeRef(kind=TypeKind.DICTIONARY, name="dict")

    # tuple → Other
    if origin is tuple:
        return TypeRef(kind=TypeKind.OTHER, name="Tuple")

    # Literal["a", "b"] → Enum
    if origin is typing.Literal:
        values = [str(a) for a in args]
        return TypeRef(kind=TypeKind.ENUM, name="Literal", enum_values=values)

    # AsyncIterator[T] → Generic (streaming)
    if origin is collections.abc.AsyncIterator:
        if args:
            ga = [map_type(a, diagnostics) for a in args]
            return TypeRef(kind=TypeKind.GENERIC, name="AsyncIterator", generic_arguments=ga)
        return TypeRef(kind=TypeKind.GENERIC, name="AsyncIterator")

    # Now handle concrete types (not generic aliases)
    if not isinstance(annotation, type):
        # String annotation or other non-type — treat as Other
        return TypeRef(kind=TypeKind.OTHER, name=str(annotation))

    # Primitive types
    primitives = {str: "str", int: "int", float: "float", bool: "bool", bytes: "bytes"}
    if annotation in primitives:
        return TypeRef(kind=TypeKind.PRIMITIVE, name=primitives[annotation])

    # datetime types
    if annotation is dt.datetime:
        return TypeRef(kind=TypeKind.PRIMITIVE, name="datetime")
    if annotation is dt.date:
        return TypeRef(kind=TypeKind.PRIMITIVE, name="date")
    if annotation is dt.timedelta:
        return TypeRef(kind=TypeKind.PRIMITIVE, name="timedelta")

    # Enum subclass
    if issubclass(annotation, PyEnum):
        values = [member.name for member in annotation]
        module = annotation.__module__ if annotation.__module__ != "builtins" else None
        return TypeRef(kind=TypeKind.ENUM, name=annotation.__name__, enum_values=values, module=module)

    # ABC / abstract class
    is_abstract = inspect.isabstract(annotation) or ABC in annotation.__bases__

    # Class with typed fields (dataclass or typed class)
    hints = {}
    try:
        hints = typing.get_type_hints(annotation)
    except Exception:
        pass

    if hints:
        # Has typed fields → Class with Properties
        properties = []
        for field_name, field_type in hints.items():
            if field_name.startswith("_"):
                continue
            field_ref = map_type(field_type, diagnostics)
            # Determine if required (no default value)
            has_default = hasattr(annotation, field_name)
            properties.append(Parameter(
                name=field_name,
                type=field_ref,
                required=not has_default,
            ))
        module = annotation.__module__ if annotation.__module__ != "builtins" else None
        return TypeRef(
            kind=TypeKind.CLASS, name=annotation.__name__,
            properties=properties if properties else None,
            is_abstract=is_abstract,
            module=module,
        )

    # Plain class (no typed fields)
    module = annotation.__module__ if annotation.__module__ != "builtins" else None
    return TypeRef(
        kind=TypeKind.CLASS, name=annotation.__name__,
        is_abstract=is_abstract,
        module=module,
    )
