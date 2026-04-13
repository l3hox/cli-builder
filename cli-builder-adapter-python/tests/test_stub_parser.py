"""Tests for .pyi stub parser — AST-based metadata extraction."""

import textwrap
from pathlib import Path

from cli_builder_adapter.models import TypeKind
from cli_builder_adapter.stub_parser import (
    parse_stub_file,
    _annotation_to_typeref,
)


def _write_stub(tmp_path: Path, content: str) -> Path:
    """Write a .pyi stub file and return its path."""
    pyi = tmp_path / "test_module.pyi"
    pyi.write_text(textwrap.dedent(content))
    return pyi


# ---- Basic class extraction ----

def test_extracts_service_class(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class CustomerClient:
            def __init__(self, api_key: str) -> None: ...
            def get(self, id: str) -> Customer: ...
            def create(self, name: str, email: str) -> Customer: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    assert len(resources) == 1
    assert resources[0].name == "customer"
    assert resources[0].source_class_name == "CustomerClient"

def test_extracts_operations(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class OrderClient:
            def get(self, id: str) -> Order: ...
            def list(self, limit: int = 10) -> list[Order]: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    assert len(resources) == 1
    ops = resources[0].operations
    assert len(ops) == 2
    assert ops[0].name == "get"
    assert ops[1].name == "list"

def test_skips_private_methods(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class ThingClient:
            def get(self, id: str) -> str: ...
            def _internal(self) -> None: ...
            def __repr__(self) -> str: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    ops = resources[0].operations
    assert len(ops) == 1
    assert ops[0].name == "get"

def test_skips_non_service_classes(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class Customer:
            name: str
            email: str

        class OrderClient:
            def get(self, id: str) -> Customer: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    assert len(resources) == 1
    assert resources[0].source_class_name == "OrderClient"


# ---- Resource class detection (CRUD classmethods) ----

def test_detects_resource_class_with_crud(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class Customer:
            @classmethod
            def create(cls, name: str) -> Customer: ...
            @classmethod
            def retrieve(cls, id: str) -> Customer: ...
            @classmethod
            def list(cls) -> list[Customer]: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    assert len(resources) == 1
    assert resources[0].name == "customer"


# ---- Parameter extraction ----

def test_required_parameter(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class TestClient:
            def get(self, id: str) -> str: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    param = resources[0].operations[0].parameters[0]
    assert param.name == "id"
    assert param.required is True
    assert param.type.kind == TypeKind.PRIMITIVE
    assert param.type.name == "str"

def test_optional_parameter(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class TestClient:
            def search(self, query: str, limit: int = 10) -> str: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    params = resources[0].operations[0].parameters
    assert params[0].required is True   # query
    assert params[1].required is False   # limit


# ---- Type annotation conversion ----

def test_primitive_types(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class TestClient:
            def a(self, x: str) -> int: ...
            def b(self, x: float) -> bool: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    ops = resources[0].operations
    assert ops[0].parameters[0].type.name == "str"
    assert ops[0].return_type.name == "int"
    assert ops[1].parameters[0].type.name == "float"
    assert ops[1].return_type.name == "bool"

def test_list_type(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class TestClient:
            def get(self) -> list[str]: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    ret = resources[0].operations[0].return_type
    assert ret.kind == TypeKind.ARRAY
    assert ret.element_type is not None
    assert ret.element_type.name == "str"

def test_dict_type(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class TestClient:
            def get(self) -> dict[str, int]: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    ret = resources[0].operations[0].return_type
    assert ret.kind == TypeKind.DICTIONARY
    assert len(ret.generic_arguments) == 2

def test_optional_type(tmp_path):
    pyi = _write_stub(tmp_path, """\
        from typing import Optional
        class TestClient:
            def get(self) -> Optional[str]: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    ret = resources[0].operations[0].return_type
    assert ret.kind == TypeKind.PRIMITIVE
    assert ret.name == "str"
    assert ret.is_nullable is True

def test_pep604_union_none(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class TestClient:
            def get(self) -> str | None: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    ret = resources[0].operations[0].return_type
    assert ret.name == "str"
    assert ret.is_nullable is True

def test_class_return_type(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class TestClient:
            def get(self) -> Customer: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    ret = resources[0].operations[0].return_type
    assert ret.kind == TypeKind.CLASS
    assert ret.name == "Customer"


# ---- Constructor params ----

def test_constructor_params(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class TestClient:
            def __init__(self, api_key: str) -> None: ...
            def get(self) -> str: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    ctor = resources[0].constructor_params
    assert ctor is not None
    assert len(ctor) == 1
    assert ctor[0].name == "api_key"
    assert ctor[0].type_name == "str"
    assert resources[0].has_parameterless_ctor is False

def test_parameterless_constructor(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class TestClient:
            def __init__(self) -> None: ...
            def get(self) -> str: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    assert resources[0].has_parameterless_ctor is True


# ---- Error handling ----

def test_malformed_stub_emits_diagnostic(tmp_path):
    pyi = tmp_path / "bad.pyi"
    pyi.write_text("class Broken(\n")  # SyntaxError
    diagnostics = []
    resources = parse_stub_file(pyi, "test_module", diagnostics)
    assert resources == []
    assert any(d.code == "CB604" for d in diagnostics)

def test_no_annotation_defaults_to_object(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class TestClient:
            def get(self, x) -> str: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    param = resources[0].operations[0].parameters[0]
    assert param.type.kind == TypeKind.OTHER
    assert param.type.name == "object"


# ---- Async variant skipping ----

def test_skips_async_variants(tmp_path):
    pyi = _write_stub(tmp_path, """\
        class TestClient:
            def get(self, id: str) -> str: ...
            def get_async(self, id: str) -> str: ...
    """)
    resources = parse_stub_file(pyi, "test_module", [])
    ops = resources[0].operations
    assert len(ops) == 1
    assert ops[0].source_method_name == "get"


# ---- Council fix: stub-path auth detection via extract() ----

def test_stub_path_detects_auth(tmp_path):
    """When extract() uses stubs, auth should still be detected from constructor params."""
    import sys
    from cli_builder_adapter.extractor import extract

    # Create a fake package with .pyi stubs
    pkg_dir = tmp_path / "fake_sdk"
    pkg_dir.mkdir()
    (pkg_dir / "__init__.py").write_text("")
    (pkg_dir / "__init__.pyi").write_text(textwrap.dedent("""\
        class CustomerClient:
            def __init__(self, api_key: str) -> None: ...
            def get(self, id: str) -> str: ...
    """))

    # Add to sys.path so importlib can find it
    sys.path.insert(0, str(tmp_path))
    try:
        result = extract("fake_sdk")

        # Should use stub path (CB605)
        assert any(d.code == "CB605" for d in result.diagnostics)

        # Auth should be detected from constructor params
        assert len(result.metadata.auth_patterns) >= 1
        assert result.metadata.auth_patterns[0].parameter_name == "api_key"

        # Constructor param should have is_auth=True
        customer = result.metadata.resources[0]
        assert customer.constructor_params is not None
        api_key_param = next(p for p in customer.constructor_params if p.name == "api_key")
        assert api_key_param.is_auth is True
    finally:
        sys.path.remove(str(tmp_path))
