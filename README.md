# cli-builder

Generate agent-ready CLIs from SDK packages — any language in, any language out.

**v2.0** — Single Rust binary. Python + C# generators. .NET + Python adapters. 670 tests.

## Problem

AI agents work best with CLI tools — structured output, discoverable commands, composable via pipes. But most SDKs ship without CLIs. Building a CLI by hand for each SDK is tedious, repetitive, and falls out of sync as SDKs evolve.

cli-builder eliminates the manual step: point it at an SDK package, get a fully functional CLI back.

## Architecture

```
SDK package  -->  Native adapter (subprocess)  -->  SdkMetadata JSON  -->  Rust generator  -->  CLI project
```

**Adapters** extract metadata from SDKs in their native language (no cross-language FFI):
- **.NET adapter** — reflection via `MetadataLoadContext` (no code execution)
- **Python adapter** — `inspect` + `typing.get_type_hints()`, with `.pyi` stub fallback

**Generators** produce CLI projects from the shared `SdkMetadata` JSON contract:
- **Python generator** (Rust) — `click`-based CLI with auth, JSON/table output
- **C# generator** (Rust) — `System.CommandLine` CLI with Tera templates

**Orchestrator** — a single Rust binary (`cli-builder`) that invokes adapters as subprocesses and calls generators as embedded library functions. Distributed via `cargo install cli-builder`.

All generators share a Rust core: `ModelMapper`, `ParameterFlattener`, `IdentifierValidator` with a pluggable `LanguageProfile` trait. Adding a new target language requires ~500 lines of templates.

See [ADR-016](docs/ADR.md#adr-016-subprocess-based-adapter-architecture--rust-migration-path) (adapter architecture) and [ADR-017](docs/ADR.md#adr-017-all-generators-in-rust--shared-modelmapper-language-specific-templates) (generator architecture).

## Quick start

**Prerequisites:** [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), [Rust](https://rustup.rs/), Python 3.10+

### Unified CLI

```bash
cd crates
cargo build

# Generate a Python CLI from a Python SDK
cargo run -p cli-builder -- generate \
  --adapter python --package stripe \
  --generator python --output /tmp/stripe-cli

# Generate a C# CLI from a .NET SDK
cargo run -p cli-builder -- generate \
  --adapter dotnet --assembly path/to/Sdk.dll \
  --generator csharp --output /tmp/my-cli

# Inspect metadata without generating
cargo run -p cli-builder -- inspect --adapter python --package stripe --json
cargo run -p cli-builder -- inspect --adapter python --package stripe  # human-readable summary
```

### Legacy .NET scripts (demo)

```bash
cd dotnet && dotnet build
./scripts/demo.sh                                    # TestSdk demo
STRIPE_API_KEY=sk_test_... ./scripts/demo-stripe.sh  # Stripe CLI
OPENAI_APIKEY=sk-... ./scripts/demo-openai.sh        # OpenAI CLI
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
| .NET (xUnit) | 397 | Adapter, model mapping, golden files, OpenAI/Stripe compile tests |
| Rust (cargo test) | 164 | Shared core (64), C# generator (63), Python generator (26), orchestrator (11) |
| Python (pytest) | 109 | Type mapper, auth detector, extractor, error paths, integration, Stripe validation, stub parser |
| **Total** | **670** | |

## Project structure

```
cli-builder/
  crates/                           # Rust workspace — orchestrator + generators
    cli/                            # Orchestrator binary (main entry point)
    core/                           # Shared: models, ModelMapper, ParameterFlattener, IdentifierValidator
    gen-python/                     # Python CLI generator (click + Tera templates)
    gen-csharp/                     # C# CLI generator (System.CommandLine + Tera templates)
    mock-adapter/                   # Cross-platform test fixture binary
  dotnet/                           # .NET adapter + legacy generator + tests
    src/                            # CliBuilder.Core, Adapter.DotNet, Generator.CSharp
    tests/                          # xUnit test projects + golden files
  python/                           # Python adapter (standalone package, subprocess)
  tests/fixtures/                   # Shared JSON metadata fixtures (Rust + .NET)
  docs/                             # Spec, ADRs, design notes, roadmap, JSON schema
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
