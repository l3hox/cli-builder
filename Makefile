.PHONY: ci test-rust test-dotnet test-python build fmt clean

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

build:
	cd $(ROOT)/crates && cargo build --release

fmt:
	cd $(ROOT)/crates && cargo fmt --all
	cd $(ROOT)/dotnet && dotnet format

clean:
	cd $(ROOT)/crates && cargo clean
	cd $(ROOT)/dotnet && dotnet clean
	find $(ROOT)/python -type d -name __pycache__ -exec rm -rf {} + 2>/dev/null || true
