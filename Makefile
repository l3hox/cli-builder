.PHONY: ci test-rust test-dotnet test-python build fmt clean

ci: test-rust test-dotnet test-python

test-rust:
	cd crates && cargo test --workspace

test-dotnet:
	cd dotnet && dotnet build --configuration Release
	cd dotnet && dotnet test --configuration Release --no-build

test-python:
	cd python && pip install -q -e ".[test]" && pytest

build:
	cd crates && cargo build --release

fmt:
	cd crates && cargo fmt --all
	cd dotnet && dotnet format

clean:
	cd crates && cargo clean
	cd dotnet && dotnet clean
	find python -type d -name __pycache__ -exec rm -rf {} + 2>/dev/null || true
