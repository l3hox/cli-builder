"""Tests for error paths — import failures, missing types, edge cases."""

import subprocess
import sys
from pathlib import Path
from unittest.mock import patch

from cli_builder_adapter.extractor import extract
from cli_builder_adapter.models import DiagnosticSeverity


# ---- Import failures ----

def test_package_not_found():
    result = extract("nonexistent_package_xyz_12345")
    errors = [d for d in result.diagnostics if d.severity == DiagnosticSeverity.ERROR]
    assert len(errors) >= 1
    assert errors[0].code == "CB600"
    assert result.metadata.resources == []

def test_module_not_found():
    result = extract("nonexistent_package_xyz", "nonexistent_module_abc")
    errors = [d for d in result.diagnostics if d.severity == DiagnosticSeverity.ERROR]
    assert len(errors) >= 1
    assert errors[0].code == "CB600"


# ---- CLI exit codes ----

def test_cli_exit_2_on_import_failure():
    """CLI should exit with code 2 when package import fails."""
    result = subprocess.run(
        [sys.executable, "-m", "cli_builder_adapter", "--package", "nonexistent_xyz", "--json"],
        capture_output=True,
        text=True,
        cwd=str(Path(__file__).parent.parent),
    )
    assert result.returncode == 2

def test_cli_exit_0_on_success():
    """CLI should exit with code 0 for TestSdk."""
    result = subprocess.run(
        [sys.executable, "-m", "cli_builder_adapter",
         "--package", "test_sdk", "--module", "test_sdk.services", "--json"],
        capture_output=True,
        text=True,
        cwd=str(Path(__file__).parent.parent),
        env={**__import__("os").environ, "PYTHONPATH": str(Path(__file__).parent)},
    )
    assert result.returncode == 0
    assert '"schemaVersion"' in result.stdout


# ---- Empty module ----

def test_empty_module_no_errors():
    """A module with no service classes should return empty resources, no errors."""
    # Use the models module — it has classes but none ending in Client/Service/Api
    result = extract("test_sdk", "test_sdk.models")
    assert result.metadata.resources == []
    errors = [d for d in result.diagnostics if d.severity == DiagnosticSeverity.ERROR]
    assert len(errors) == 0


# ---- Signature inspection failure ----

def test_signature_failure_emits_cb602():
    """When inspect.signature raises, CB602 should be emitted and method skipped."""
    from cli_builder_adapter.extractor import _extract_operations

    class BrokenClient:
        def working_method(self) -> str:
            return "ok"

    # Patch inspect.signature to fail for all methods on BrokenClient
    original_signature = __import__("inspect").signature

    def failing_signature(obj, **kwargs):
        # Fail for the bound method on BrokenClient
        if hasattr(obj, "__qualname__") and "BrokenClient" in getattr(obj, "__qualname__", ""):
            raise ValueError("Cannot inspect")
        return original_signature(obj, **kwargs)

    diagnostics = []
    with patch("cli_builder_adapter.extractor.inspect.signature", side_effect=failing_signature):
        ops = _extract_operations(BrokenClient, diagnostics)

    # CB602 should be emitted for the broken method
    cb602 = [d for d in diagnostics if d.code == "CB602"]
    assert len(cb602) >= 1, f"Expected CB602 diagnostic, got: {[d.code for d in diagnostics]}"
    # The method should be skipped (not in operations)
    assert all(op.source_method_name != "working_method" for op in ops)


# ---- Version fallback ----

def test_version_fallback_when_missing():
    """Package without __version__ should default to 0.0.0."""
    # test_sdk.services module doesn't have __version__
    result = extract("test_sdk", "test_sdk.services")
    assert result.metadata.version == "0.0.0"
