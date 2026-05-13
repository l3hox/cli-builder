"""Integration tests — full pipeline: extract → serialize → parse → validate."""

import json
import subprocess
import sys
from pathlib import Path

import jsonschema

from cli_builder_adapter.extractor import extract
from cli_builder_adapter.json_output import serialize_adapter_result


TESTS_DIR = Path(__file__).parent
SCHEMA_PATH = TESTS_DIR.parent.parent / "docs" / "sdk-metadata-schema.json"


# ---- Round-trip ----

def test_testsdk_round_trip():
    """Extract TestSdk → serialize → parse JSON → verify structure."""
    result = extract("test_sdk", "test_sdk.services")
    json_str = serialize_adapter_result(result)
    parsed = json.loads(json_str)

    assert "schemaVersion" in parsed
    assert "metadata" in parsed
    assert "diagnostics" in parsed
    assert parsed["schemaVersion"] == "1"

def test_camelcase_field_names():
    """All JSON keys should be camelCase, not snake_case."""
    result = extract("test_sdk", "test_sdk.services")
    json_str = serialize_adapter_result(result)
    parsed = json.loads(json_str)

    # Top-level keys
    assert "schemaVersion" in parsed
    assert "schema_version" not in json_str

    # Metadata keys
    meta = parsed["metadata"]
    assert "authPatterns" in meta
    assert "auth_patterns" not in json.dumps(meta)

    # Resource keys
    if meta["resources"]:
        res = meta["resources"][0]
        assert "sourceClassName" in res
        assert "source_class_name" not in json.dumps(res)

def test_resource_count():
    """TestSdk should produce 3 resources."""
    result = extract("test_sdk", "test_sdk.services")
    assert len(result.metadata.resources) == 3

def test_operations_have_return_types():
    result = extract("test_sdk", "test_sdk.services")
    for resource in result.metadata.resources:
        for op in resource.operations:
            assert op.return_type is not None
            assert op.return_type.kind is not None


# ---- Schema validation ----

def test_python_output_validates_against_schema():
    """Python adapter output must conform to the JSON schema contract."""
    result = extract("test_sdk", "test_sdk.services")
    json_str = serialize_adapter_result(result)
    parsed = json.loads(json_str)

    schema = json.loads(SCHEMA_PATH.read_text())
    jsonschema.validate(parsed, schema)  # Raises on failure

def test_dotnet_fixture_validates_against_schema():
    """The .NET fixture must also conform to the same schema."""
    import pytest
    dotnet_fixture = TESTS_DIR.parent.parent / "tests" / "fixtures" / "testsdk-metadata.json"
    if not dotnet_fixture.exists():
        pytest.skip("No .NET fixture available")

    data = json.loads(dotnet_fixture.read_text())
    schema = json.loads(SCHEMA_PATH.read_text())

    # .NET fixture may lack schemaVersion (pre-Step 12) — schema allows it optional
    jsonschema.validate(data, schema)


# ---- Cross-adapter shape ----

def test_json_structure_matches_dotnet_fixture():
    """Python adapter JSON should have same top-level structure as .NET fixture."""
    dotnet_fixture = TESTS_DIR.parent.parent / "tests" / "fixtures" / "testsdk-metadata.json"
    if not dotnet_fixture.exists():
        import pytest
        pytest.skip("No .NET fixture available")

    dotnet_data = json.loads(dotnet_fixture.read_text())
    result = extract("test_sdk", "test_sdk.services")
    python_data = json.loads(serialize_adapter_result(result))

    # Same top-level keys (schemaVersion may be absent in pre-Step-12 .NET fixtures)
    dotnet_keys = set(dotnet_data.keys())
    python_keys = set(python_data.keys())
    # Both must have metadata and diagnostics
    assert "metadata" in dotnet_keys
    assert "metadata" in python_keys
    assert "diagnostics" in dotnet_keys
    assert "diagnostics" in python_keys

    # All .NET metadata keys must be present in Python output (Python may emit
    # extra fields the .NET adapter doesn't yet produce — e.g. `discoveryMode`
    # added in v0.2.2 / ADR-023 for single-client discovery provenance).
    dotnet_meta_keys = set(dotnet_data["metadata"].keys())
    python_meta_keys = set(python_data["metadata"].keys())
    assert dotnet_meta_keys.issubset(python_meta_keys), (
        f"Python adapter is missing keys present in .NET: {dotnet_meta_keys - python_meta_keys}"
    )

    # Both have resources with same shape
    if dotnet_data["metadata"]["resources"] and python_data["metadata"]["resources"]:
        dotnet_res_keys = set(dotnet_data["metadata"]["resources"][0].keys())
        python_res_keys = set(python_data["metadata"]["resources"][0].keys())
        assert dotnet_res_keys == python_res_keys


# ---- CLI pipeline ----

def test_cli_json_output_is_valid():
    """CLI --json flag should produce valid JSON to stdout."""
    result = subprocess.run(
        [sys.executable, "-m", "cli_builder_adapter",
         "--package", "test_sdk", "--module", "test_sdk.services", "--json"],
        capture_output=True,
        text=True,
        cwd=str(TESTS_DIR.parent),
        env={**__import__("os").environ, "PYTHONPATH": str(TESTS_DIR)},
    )
    assert result.returncode == 0
    parsed = json.loads(result.stdout)
    assert parsed["schemaVersion"] == "1"
    assert len(parsed["metadata"]["resources"]) == 3
