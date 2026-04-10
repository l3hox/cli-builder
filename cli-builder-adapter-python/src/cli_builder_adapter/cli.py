"""CLI entry point for the Python adapter."""

from __future__ import annotations

import argparse
import sys


def main() -> None:
    parser = argparse.ArgumentParser(
        prog="cli-builder-adapter-python",
        description="Extract SdkMetadata from Python SDK packages",
    )
    parser.add_argument("--package", required=True, help="Python package name to inspect")
    parser.add_argument("--module", default=None, help="Specific module within the package")
    parser.add_argument("--json", action="store_true", help="Output as JSON (required)")

    args = parser.parse_args()

    if not args.json:
        print("Error: --json flag is required", file=sys.stderr)
        sys.exit(2)

    # TODO: Phase 3-4 will implement extraction
    print("Not yet implemented", file=sys.stderr)
    sys.exit(2)
