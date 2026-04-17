# tests/

This directory holds **shared test fixtures only** — no runnable test suites live here.

`fixtures/` contains `SdkMetadata` JSON files consumed by both Rust generators (`crates/core`, `crates/gen-python`, `crates/gen-csharp`) and the .NET test projects (`dotnet/tests/`). Keeping them at the repo root avoids cross-language path duplication.

Per-language test suites live with their code:

| Language | Location | Run with |
|----------|----------|----------|
| Rust | `crates/*/src/tests.rs`, `crates/*/tests/` | `cd crates && cargo test --workspace` |
| .NET | `dotnet/tests/` | `cd dotnet && dotnet test` |
| Python | `python/tests/` | `cd python && pytest` |

Top-level: `make ci` runs all three.
