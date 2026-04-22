# Roadmap

Production roadmap for cli-builder — generate agent-ready CLIs from SDK packages in any language.

**Current version: v2.0** — Single Rust binary, Python + C# generators, .NET + Python adapters.

---

## Next up

### CI/CD integration
GitHub Action, Docker image, output stability guarantees. Automated test runs for all 670 tests across Rust, .NET, and Python.

### Package publishing
- `cargo install cli-builder` — Rust binary distribution
- Generated C# CLIs: `dotnet tool install` packaging
- Generated Python CLIs: PyPI publishing guide
- Homebrew formula, self-contained single-file binaries

### Incremental streaming output
Streaming operations (`IAsyncEnumerable<T>`) currently collect all items before formatting. True incremental streaming (emit each item as it arrives). NDJSON for pipe-friendly output.

### DI/factory pattern support
34 Stripe services without parameterless constructors need `IStripeClient` injection.

---

## Later

### New adapters
- **Kotlin** — JVM reflection or kotlinx-metadata
- **Go** — AST parsing, struct tags
- **OpenAPI** — spec parsing (overlaps with existing tools — lower unique value)

### New generators
Kotlin (clikt), Go (cobra), TypeScript (commander) — each ~500 lines of Tera templates + a `LanguageProfile` implementation.

### Agent-assisted enrichment
- `--enrich` flag with pluggable LLM provider (design approved, see ADR-014)

### Other
- Incremental regeneration (detect SDK changes)
- Test generation for generated CLIs
- Config file (`cli-builder.toml`) per-SDK customization
- Token caching (auth handler writes credentials to config)
- GUI / VS Code plugin
- **Full venv+pip console-script E2E for generated Python CLI**. Current `help_output_snapshot` test uses PYTHONPATH + `python -m testsdk_cli` — bypasses the `[project.scripts]` entry point. A nightly job could: create venv → `pip install -e python/tests/test_sdk` (needs minimal `pyproject.toml` added) → `pip install -e <generated-cli-dir>` → invoke `testsdk-cli customer get --id-value cust_123 --json` → assert exit 0 + JSON shape. Placeholder `#[ignore]`'d test at `crates/gen-python/tests/e2e.rs::console_script_entry_point_end_to_end`.

---

## Completed

### v2.0 — Rust migration (Steps 12b-15)
- **Step 12b**: Python adapter hardening — 109 pytest tests, JSON schema contract (`docs/sdk-metadata-schema.json`), module-level auth detection, Stripe validation (105 resources), `.pyi` stub parser (ADR-013 compliance)
- **Step 13**: Python CLI generator in Rust — shared core (`ModelMapper`, `ParameterFlattener`, `IdentifierValidator` with `LanguageProfile` trait) + click-based Python templates via Tera. Council-reviewed. Golden file snapshots via insta.
- **Step 14**: C# generator ported to Rust/Tera — `CSharpProfile` + 6 Tera templates + 6 post-processing transforms. Compile-validated (`dotnet build`). OpenAI 20 resources, Stripe 196 resources.
- **Step 15**: Rust orchestrator — single `cli-builder` binary with `generate`/`inspect` commands, adapter subprocess management, embedded Python + C# generators.
- All generators share Rust core. Adapters stay native forever (ADR-016).

### v1.x — .NET foundation (Steps 1-12)
- Steps 1-9: Architecture, .NET adapter, C# generator, real SDK calls, multi-arg constructors, static auth, --json-input deserialization, noun collision resolution
- Step 9B: Direct param deserialization (IEnumerable, Dictionary, Array, bare Class via --json-input)
- Step 10: CLI entry point (`cli-builder generate`, `cli-builder inspect`), `dotnet tool` packaging
- Step 11: SdkMetadata abstraction (language-neutral field names, TypeKind.Other)
- Step 12: Python adapter MVP — cross-adapter architecture proof

### Validated SDKs
- TestSdk (.NET): 7 resources, 23 E2E tests
- OpenAI 2.9.1: 20 resources, 169 ops, 41 wired
- Stripe.net 51.0.0: 196 resources, compile validated
- TestSdk (Python): 3 resources
- stripe-python 15.x: 105 resources, classmethod extraction

### Test totals
397 .NET + 164 Rust + 109 Python = **670 tests**, 0 failures
