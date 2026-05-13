"""CLI entry point for the Python adapter."""

from __future__ import annotations

import argparse
import sys

from .extractor import extract
from .json_output import serialize_adapter_result
from .models import DiagnosticSeverity


def main() -> None:
    parser = argparse.ArgumentParser(
        prog="cli-builder-adapter-python",
        description="Extract SdkMetadata from Python SDK packages",
    )
    parser.add_argument("--package", required=True, help="Python package name to inspect")
    parser.add_argument("--module", default=None, help="Specific module within the package")
    parser.add_argument("--json", action="store_true", help="Output as JSON (required)")
    parser.add_argument(
        "--entry-class",
        default=None,
        help="Force single-client discovery mode with this class as the entry "
             "(ADR-023). When omitted, the adapter tries multi-service first "
             "and falls back to single-client auto-detection.",
    )

    args = parser.parse_args()

    if not args.json:
        print("Error: --json flag is required", file=sys.stderr)
        sys.exit(2)

    try:
        result = extract(args.package, args.module, entry_class=args.entry_class)
    except Exception as e:
        print(f"Error: {e}", file=sys.stderr)
        sys.exit(2)

    # Print diagnostics to stderr
    for d in result.diagnostics:
        label = {
            DiagnosticSeverity.ERROR: "ERROR",
            DiagnosticSeverity.WARNING: "WARN ",
            DiagnosticSeverity.INFO: "INFO ",
        }[d.severity]
        print(f"[{label}] {d.code}  {d.message}", file=sys.stderr)

    # Print JSON to stdout
    print(serialize_adapter_result(result))

    # Exit code: 2 for environment failure (import errors), 1 for extraction errors, 0 for success
    has_env_failure = any(
        d.severity == DiagnosticSeverity.ERROR and d.code == "CB600"
        for d in result.diagnostics
    )
    if has_env_failure:
        sys.exit(2)
    has_errors = any(d.severity == DiagnosticSeverity.ERROR for d in result.diagnostics)
    sys.exit(1 if has_errors else 0)
