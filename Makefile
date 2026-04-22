.PHONY: ci test-rust test-dotnet test-python test-e2e-python test-e2e-full build fmt clean

# Absolute path to this Makefile's directory — the repo root. Lets targets
# work regardless of where `make` is invoked from.
ROOT := $(abspath $(dir $(lastword $(MAKEFILE_LIST))))

ci: test-rust test-dotnet test-python

test-rust:
	cd $(ROOT)/crates && cargo test --workspace

test-dotnet:
	cd $(ROOT)/dotnet && dotnet build --configuration Release
	cd $(ROOT)/dotnet && dotnet test --configuration Release --no-build

test-python:
	cd $(ROOT)/python && pip install -q -e ".[test]" && pytest

# End-to-end runtime anchor for the Python generator: generates a CLI from
# the TestSdk fixture, spawns `python -m testsdk_cli --help` via PYTHONPATH,
# and snapshots the stdout. Lives in `crates/gen-python/tests/e2e.rs` as a
# cargo integration test. Separate from test-rust because it requires
# Python+click on PATH; test-rust must stay runnable on machines with no
# Python.
#
# Uses a venv under /tmp to avoid PEP 668 (externally-managed-environment)
# failures on modern Debian/Ubuntu/WSL. Click pinned to 8.x to match CI.
E2E_VENV := /tmp/cli-builder-e2e-venv

test-e2e-python:
	python3 -m venv $(E2E_VENV)
	$(E2E_VENV)/bin/pip install -q "click==8.*"
	cd $(ROOT)/crates && PATH=$(E2E_VENV)/bin:$$PATH cargo test --package cli-builder-gen-python --test e2e -- --nocapture

# Stub for the full venv+pip+console-script E2E, not yet implemented.
# When implemented, this target will replace the #[ignore] gate in
# tests/e2e.rs::console_script_entry_point_end_to_end. See docs/FUTURE.md.
test-e2e-full:
	@echo "test-e2e-full not yet implemented — see docs/FUTURE.md (Other → Full venv+pip console-script E2E)"
	@exit 1

build:
	cd $(ROOT)/crates && cargo build --release

fmt:
	cd $(ROOT)/crates && cargo fmt --all
	cd $(ROOT)/dotnet && dotnet format

clean:
	cd $(ROOT)/crates && cargo clean
	cd $(ROOT)/dotnet && dotnet clean
	find $(ROOT)/python -type d -name __pycache__ -exec rm -rf {} + 2>/dev/null || true
