# cli-builder

Generate agent-ready CLIs from SDK packages — any language in, any language out.

**v0.2.2** — Single Rust binary. Python + C# generators. .NET + Python adapters. PEP 692 `Unpack[TypedDict]` resolution. Single-client SDK shape discovery (PyGithub, Notion, Slack, Anthropic, …). 707 tests.

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
| stripe-python 15.x | Python | 105 | PEP 692 `Unpack[TypedDict]` resolved (ADR-022) | `customer list --help` / `customer create --help` validated; nested params via `--json-input` |
| PyGithub 2.x | Python | 33 | Single-client discovery (ADR-023) — `github-cli` with `--entry-class Github` | Live `api.github.com/users/octocat` call validated via `github-cli user get --json-input '{"login": "octocat"}'`; per-param flags route through `--json-input` (see Known Limitations) |

## Known limitations

cli-builder is pre-1.0 — the tool works for several real-world SDKs but has rough edges worth knowing about.

**Sub-resource discovery deferred.** For SDKs using the single-client model (PyGithub, Notion, Linear, Slack, Anthropic), cli-builder walks the entry class's methods into top-level commands but does NOT recurse into returned objects. PyGithub's `Github.get_repo("owner/name")` returns a `Repository` with its own methods (`get_issues`, `create_pull_request`, …) — those are not yet surfaced as nested CLI commands like `github-cli repo --full-name owner/name issue list`. Workaround: use `--json` on the parent command to inspect the returned object, then call the SDK directly for sub-operations. Tracked in [ADR-023](docs/ADR.md#adr-023-single-client-sdk-shape-discovery-via-verb-noun-method-grouping) consequences.

**Sentinel-Union type aliases unrecognized.** Some SDKs define `Opt[T] = Union[T, _NotSetType]` (PyGithub's pattern) as their optional-parameter convention — equivalent to `Optional[T]` but with a sentinel class instead of `None`. The Python adapter's type mapper doesn't yet recognize this shape as Optional, so parameters annotated `Opt[X]` emit as `TypeKind.Other` and route through `--json-input`. Result: generated CLIs work end-to-end (auth, SDK call, result), but per-parameter flags aren't emitted on operations that use this idiom. **Workaround:** pass nested JSON via `--json-input '{"login": "octocat"}'` instead of `--login octocat`.

**Ambiguous entry-class auto-detection requires `--entry-class`.** When multiple classes match the single-client heuristic (e.g., PyGithub has `Github`, `GithubIntegration`, `GithubRetry` all matching), the adapter emits `CB609` warning and exits with zero resources. The user must disambiguate explicitly: `cli-builder generate --adapter python --package github --entry-class Github --output ...`.

**Service-pattern (nested sub-clients) not supported.** Stripe's modern surface `StripeClient.v1.customers.list(params=...)` uses a nested service tree. Cli-builder currently discovers the legacy `stripe.Customer.list(**params)` surface (Step 17 / ADR-022) but not the modern one. OpenAI's `OpenAI().chat.completions.create(...)` is the same shape and is also not yet handled.

**`NotGiven` / `Omit` sentinel defaults.** OpenAI Python uses sentinel objects as default values (`= NotGiven`). These don't cleanly serialize and aren't yet handled by the adapter's parameter extraction.

**.NET ↔ Python type-name divergence.** The .NET adapter emits `string` / `int32` / `bool` for primitives while the Python adapter emits `str` / `int` / `bool`. The generators normalize internally but the raw `SdkMetadata` JSON is not language-neutral — see [ADR-011](docs/ADR.md#adr-011-cross-platform-support--windows-linux-macos) for context.

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
