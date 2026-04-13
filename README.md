# cli-builder

Generate agent-ready CLIs from SDK packages — any language in, any language out.

## Problem

AI agents work best with CLI tools — structured output, discoverable commands, composable via pipes. But most SDKs ship without CLIs. Building a CLI by hand for each SDK is tedious, repetitive, and falls out of sync as SDKs evolve.

cli-builder eliminates the manual step: point it at an SDK package, get a fully functional CLI back.

## Architecture

```
SDK package  -->  Native adapter  -->  SdkMetadata JSON  -->  Rust generator  -->  CLI project
```

**Adapters** extract metadata from SDKs in their native language (no cross-language FFI):
- **.NET adapter** — reflection via `MetadataLoadContext` (no code execution)
- **Python adapter** — `inspect` + `typing.get_type_hints()`, with `.pyi` stub fallback

**Generators** produce CLI projects from the shared `SdkMetadata` JSON contract:
- **Python generator** (Rust) — `click`-based CLI with auth, JSON/table output
- **C# generator** (.NET) — `System.CommandLine` CLI with Scriban templates

All generators share a Rust core: `ModelMapper`, `ParameterFlattener`, `IdentifierValidator` with a pluggable `LanguageProfile` trait. Adding a new target language requires ~500 lines of templates.

See [ADR-016](docs/ADR.md#adr-016-subprocess-based-adapter-architecture--rust-migration-path) (adapter architecture) and [ADR-017](docs/ADR.md#adr-017-all-generators-in-rust--shared-modelmapper-language-specific-templates) (generator architecture).

## Quick start

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), [Rust](https://rustup.rs/), Python 3.10+

### Generate a C# CLI from a .NET SDK

```bash
dotnet build
./scripts/demo.sh                                    # TestSdk demo
STRIPE_API_KEY=sk_test_... ./scripts/demo-stripe.sh  # Stripe CLI
OPENAI_APIKEY=sk-... ./scripts/demo-openai.sh        # OpenAI CLI
```

### Generate a Python CLI from a Python SDK

```bash
# Extract metadata from a Python package
cd cli-builder-adapter-python
python -m cli_builder_adapter --package test_sdk --module test_sdk.services --json > /tmp/metadata.json

# Generate click-based Python CLI
cd ../cli-builder-rust
cargo run -p cli-builder-gen-python -- --input /tmp/metadata.json --output /tmp/my-cli --cli-name my-cli

# Install and run
cd /tmp/my-cli && pip install -e .
my-cli --help
my-cli customer get --id-value cust_123 --json
```

## Validated SDKs

| SDK | Adapter | Resources | Operations wired | Live API tested |
|-----|---------|-----------|-----------------|----------------|
| TestSdk (.NET) | .NET | 7 | 100% | Yes (23 E2E tests) |
| OpenAI 2.9.1 | .NET | 20 | 41/169 (24%) | Yes |
| Stripe.net 51.0.0 | .NET | 196 | ~93% | Yes |
| TestSdk (Python) | Python | 3 | 100% | Yes |
| stripe-python 15.x | Python | 105 | Yes (classmethod extraction) | Metadata only |

## Agent-readiness

Every generated CLI satisfies:

| Requirement | Implementation |
|-------------|---------------|
| Structured output | `--json` flag on every command |
| Human-readable default | Table format when `--json` absent |
| Discoverable commands | `--help` at root, noun, and verb levels |
| Noun-verb structure | `<tool> <resource> <action> [--params]` |
| Semantic exit codes | 0=success, 1=user error, 2=auth/env error |
| Non-interactive auth | Env var > `--api-key` flag |
| Pipe-friendly | No color when stdout is redirected |

## Test suite

| Component | Tests | Covers |
|-----------|-------|--------|
| .NET (xUnit) | 397 | Adapter, generator, model mapping, golden files, OpenAI/Stripe compile tests |
| Rust (cargo test) | 90 | Shared core (ModelMapper, ParameterFlattener, IdentifierValidator), Python generator templates, golden file snapshots |
| Python (pytest) | 108 | Type mapper, auth detector, extractor, error paths, integration, Stripe validation, stub parser |
| **Total** | **595** | |

## Project structure

```
cli-builder/
  src/                          # .NET source (adapter, generator, orchestrator)
  cli-builder-adapter-python/   # Python adapter (standalone package)
  cli-builder-rust/             # Rust workspace (shared core + generators)
    crates/
      cli-builder-core/         # Shared: models, ModelMapper, ParameterFlattener
      cli-builder-gen-python/   # Python CLI generator (click + Tera templates)
  tests/                        # .NET test projects
  docs/                         # Spec, ADRs, design notes, roadmap
```

## Documentation

| Document | Contents |
|----------|----------|
| [AGENTS.md](AGENTS.md) | Quick-start context for AI agents and contributors |
| [docs/cli-builder-spec.md](docs/cli-builder-spec.md) | Specification — interfaces, metadata model, config schema |
| [docs/ADR.md](docs/ADR.md) | 17 Architecture Decision Records |
| [docs/design-notes.md](docs/design-notes.md) | Edge-case policies, diagnostic codes, generator architecture |
| [docs/FUTURE.md](docs/FUTURE.md) | Roadmap — next steps |
| [docs/sdk-metadata-schema.json](docs/sdk-metadata-schema.json) | JSON Schema for the cross-adapter SdkMetadata contract |

## License

Licensed under the [European Union Public Licence v. 1.2](LICENSE) (EUPL-1.2).

SPDX-License-Identifier: `EUPL-1.2`
