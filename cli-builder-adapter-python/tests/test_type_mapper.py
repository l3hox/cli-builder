"""Unit tests for type_mapper.map_type()."""

import collections.abc
import datetime as dt
import typing
from enum import Enum

from cli_builder_adapter.models import TypeKind
from cli_builder_adapter.type_mapper import map_type

# Import TestSdk types for realistic test cases
from test_sdk.models import Address, Customer, CustomerStatus, Message


# ---- Primitives ----

def test_str():
    ref = map_type(str)
    assert ref.kind == TypeKind.PRIMITIVE
    assert ref.name == "str"

def test_int():
    ref = map_type(int)
    assert ref.kind == TypeKind.PRIMITIVE
    assert ref.name == "int"

def test_float():
    ref = map_type(float)
    assert ref.kind == TypeKind.PRIMITIVE
    assert ref.name == "float"

def test_bool():
    ref = map_type(bool)
    assert ref.kind == TypeKind.PRIMITIVE
    assert ref.name == "bool"

def test_bytes():
    ref = map_type(bytes)
    assert ref.kind == TypeKind.PRIMITIVE
    assert ref.name == "bytes"

def test_none_type():
    ref = map_type(type(None))
    assert ref.kind == TypeKind.PRIMITIVE
    assert ref.name == "None"

def test_datetime():
    ref = map_type(dt.datetime)
    assert ref.kind == TypeKind.PRIMITIVE
    assert ref.name == "datetime"

def test_date():
    ref = map_type(dt.date)
    assert ref.kind == TypeKind.PRIMITIVE
    assert ref.name == "date"

def test_timedelta():
    ref = map_type(dt.timedelta)
    assert ref.kind == TypeKind.PRIMITIVE
    assert ref.name == "timedelta"


# ---- Nullable / Optional ----

def test_optional_str():
    ref = map_type(typing.Optional[str])
    assert ref.kind == TypeKind.PRIMITIVE
    assert ref.name == "str"
    assert ref.is_nullable is True

def test_pep604_str_or_none():
    ref = map_type(str | None)
    assert ref.kind == TypeKind.PRIMITIVE
    assert ref.name == "str"
    assert ref.is_nullable is True

def test_non_optional_union():
    ref = map_type(typing.Union[str, int])
    assert ref.kind == TypeKind.OTHER
    assert ref.name == "Union"


# ---- Containers ----

def test_list_str():
    ref = map_type(list[str])
    assert ref.kind == TypeKind.ARRAY
    assert ref.name == "list"
    assert ref.element_type is not None
    assert ref.element_type.name == "str"

def test_dict_str_int():
    ref = map_type(dict[str, int])
    assert ref.kind == TypeKind.DICTIONARY
    assert ref.name == "dict"
    assert len(ref.generic_arguments) == 2
    assert ref.generic_arguments[0].name == "str"
    assert ref.generic_arguments[1].name == "int"

def test_tuple():
    """Bare tuple (no generic args) falls through to Class (it's a type)."""
    ref = map_type(tuple)
    assert ref.kind == TypeKind.CLASS
    assert ref.name == "tuple"

def test_nested_optional_list_customer():
    """Council fix: test recursive type resolution with nested containers."""
    ref = map_type(typing.Optional[list[Customer]])
    assert ref.kind == TypeKind.ARRAY
    assert ref.is_nullable is True
    assert ref.element_type is not None
    assert ref.element_type.kind == TypeKind.CLASS
    assert ref.element_type.name == "Customer"


# ---- Enums ----

def test_enum_subclass():
    ref = map_type(CustomerStatus)
    assert ref.kind == TypeKind.ENUM
    assert ref.name == "CustomerStatus"
    assert set(ref.enum_values) == {"ACTIVE", "INACTIVE", "SUSPENDED"}
    assert ref.module is not None

def test_literal():
    ref = map_type(typing.Literal["a", "b", "c"])
    assert ref.kind == TypeKind.ENUM
    assert ref.name == "Literal"
    assert ref.enum_values == ["a", "b", "c"]


# ---- Classes ----

def test_dataclass_with_fields():
    ref = map_type(Customer)
    assert ref.kind == TypeKind.CLASS
    assert ref.name == "Customer"
    assert ref.properties is not None
    field_names = {p.name for p in ref.properties}
    assert "id" in field_names
    assert "email" in field_names

def test_abstract_class():
    ref = map_type(Message)
    assert ref.kind == TypeKind.CLASS
    assert ref.name == "Message"
    assert ref.is_abstract is True

def test_unknown_class():
    class SomeNewType:
        pass
    ref = map_type(SomeNewType)
    assert ref.kind == TypeKind.CLASS
    assert ref.name == "SomeNewType"


# ---- Generics ----

def test_async_iterator():
    ref = map_type(collections.abc.AsyncIterator[Customer])
    assert ref.kind == TypeKind.GENERIC
    assert ref.name == "AsyncIterator"
    assert ref.generic_arguments is not None
    assert ref.generic_arguments[0].name == "Customer"


# ---- Edge cases ----

def test_typing_any():
    ref = map_type(typing.Any)
    assert ref.kind == TypeKind.OTHER
    assert ref.name == "object"

def test_none_annotation():
    ref = map_type(None)
    assert ref.kind == TypeKind.OTHER
    assert ref.name == "object"

def test_class_with_nested_typed_field():
    """Address has typed fields including Optional[str]."""
    ref = map_type(Address)
    assert ref.kind == TypeKind.CLASS
    assert ref.properties is not None
    line1 = next(p for p in ref.properties if p.name == "line1")
    assert line1.type.kind == TypeKind.PRIMITIVE
    assert line1.type.name == "str"
