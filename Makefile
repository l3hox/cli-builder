.PHONY: ci test-rust test-dotnet test-python build fmt clean

ci: test-rust test-dotnet test-python

test-rust:
	cd cli-builder-rust && cargo test --workspace

test-dotnet:
	dotnet build --configuration Release
	dotnet test --configuration Release --no-build

test-python:
	cd cli-builder-adapter-python && pip install -q -e ".[test]" && pytest

build:
	cd cli-builder-rust && cargo build --release

fmt:
	cd cli-builder-rust && cargo fmt --all
	dotnet format

clean:
	cd cli-builder-rust && cargo clean
	dotnet clean
	find cli-builder-adapter-python -type d -name __pycache__ -exec rm -rf {} + 2>/dev/null || true
