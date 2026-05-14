# AGENTS.md — cli-builder

Quick-start orientation for AI agents and contributors.

## What is cli-builder?

A tool that generates agent-ready CLIs from SDK packages in any language. Input: an SDK package (.NET assembly or Python package). Output: a compilable CLI project (C# or Python) that wraps the original SDK. Single Rust binary: `cli-builder generate --adapter python --package stripe --output ./output`.

## Tech stack

- **Orchestrator + generators:** Rust (clap CLI, Tera templates, shared ModelMapper core)
- **Adapters:** C# / .NET 8 (reflection adapter), Python 3.10+ (inspect adapter)
- **Generated CLIs:** System.CommandLine (C#), click (Python)
- **Testing:** cargo test (Rust), xUnit (.NET), pytest (Python) — 670 tests total
- **License:** EUPL-1.2

## Architecture (one paragraph)

Each adapter is a standalone executable in its native language (.NET adapter in C#, Python adapter in Python) that extracts `SdkMetadata` and emits it as JSON to stdout. Each generator is a Rust library crate that reads `SdkMetadata` JSON and emits a CLI project. `SdkMetadata` JSON is the universal contract between all adapters and generators. The orchestrator (`cli-builder` Rust binary) calls adapters as subprocesses and generators as embedded library calls. Adapters are permanent — never rewritten when generators change. All generators share a Rust core (`ModelMapper`, `ParameterFlattener`, `IdentifierValidator`) via the `LanguageProfile` trait. See [ADR-016](docs/ADR.md#adr-016-subprocess-based-adapter-architecture--rust-migration-path) and [ADR-017](docs/ADR.md#adr-017-all-generators-in-rust--shared-modelmapper-language-specific-templates).

## Documentation hierarchy

Each piece of information exists in exactly one place:

| Document | Level | Contains |
|----------|-------|----------|
| [docs/cli-builder-spec.md](docs/cli-builder-spec.md) | **Spec** | Interfaces, metadata model, config schema, requirements, scope, test strategy |
| [docs/ADR.md](docs/ADR.md) | **Decisions** | 17 architecture decision records — the "why" behind each choice |
| [docs/design-notes.md](docs/design-notes.md) | **Design** | Edge-case policies, behavioral rules, diagnostic codes, generator architecture |
| [docs/process.md](docs/process.md) | **Process** | Development methodology (7-phase agent-orchestrated workflow) |
| `docs/internal/` | **Plans** | Agent implementation plans — step-by-step build instructions |
| [docs/FUTURE.md](docs/FUTURE.md) | **Deferred** | Roadmap and deferred features |

**When looking for something:** check the spec first (contracts and requirements), then design notes (behavioral details and edge cases), then ADRs (rationale for a decision).

**When changing documentation:** every change must be checked for duplication and proper placement across all levels — this file, the spec, ADRs, design notes, and agent execution plans. Information must exist in exactly one place at the correct granularity level. If a change introduces duplication or puts detail at the wrong level, fix the placement before committing.

## Architectural constraints (must not violate)

- **`MetadataLoadContext` only** — .NET adapter never uses `AssemblyLoadContext` ([ADR-003](docs/ADR.md#adr-003-metaloadcontext-only--no-code-execution-during-analysis))
- **Cross-platform** — Windows, Linux, macOS. No hardcoded paths, no platform-specific APIs ([ADR-011](docs/ADR.md#adr-011-cross-platform-support--windows-linux-macos))
- **Generated CLI wraps the original SDK** — depends on SDK, not on cli-builder ([ADR-006](docs/ADR.md#adr-006-generated-cli-wrapper-over-the-original-sdk))
- **No silent failures** — every skipped type, renamed parameter, or discarded overload produces a `Diagnostic` ([ADR-015](docs/ADR.md#adr-015-diagnostics-collection-pattern-for-error-handling))
- **Package artifacts only** — compiled assemblies/packages, never raw source code ([ADR-013](docs/ADR.md#adr-013-package-artifacts-over-raw-source-code--per-language-native-metadata))
- **Sanitize all metadata strings** — core does structural validation, generators do template-engine escaping ([ADR-017](docs/ADR.md#adr-017-all-generators-in-rust--shared-modelmapper-language-specific-templates))
- **SdkMetadata JSON is the universal contract** — all adapters produce it, all generators consume it ([ADR-005](docs/ADR.md#adr-005-sdkmetadata-as-the-serializable-contract-between-adapters-and-generators))

## Start here

**v0.2.2 (current)** — Single `cli-builder` Rust binary orchestrating the full pipeline. Python adapter resolves PEP 692 `Unpack[TypedDict]` kwargs ([ADR-022](docs/ADR.md#adr-022-pep-692-unpacktypeddict-resolution-via-ast-walk-of-type_checking-imports)) and single-client SDK shapes like PyGithub ([ADR-023](docs/ADR.md#adr-023-single-client-sdk-shape-discovery-via-verb-noun-method-grouping)). Pipeline:
- Adapters: .NET (subprocess) + Python (subprocess)
- Generators: C# + Python (embedded Rust library calls, Tera templates)
- `cli-builder generate --adapter {dotnet,python} --generator {csharp,python} --output ./output`
- 397 .NET + 177 Rust + 133 Python (109 base + 10 PEP 692 + 13 single-client + 1 auth) = **707 tests**, 0 failures
- CI/CD: 15-job matrix (3 OS × Rust + .NET + Python 3.10/3.11/3.12) green on every push

**What's next:** package publishing (`cargo install`, PyPI), incremental streaming, DI/factory pattern for Stripe, Kotlin/Go generators, `--enrich` flag. See [docs/FUTURE.md](docs/FUTURE.md).
