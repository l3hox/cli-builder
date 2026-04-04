# AGENTS.md — cli-builder

Quick-start orientation for AI agents and contributors.

## What is cli-builder?

A tool that generates agent-ready CLIs from .NET SDK assemblies via reflection. Input: a DLL. Output: a compilable C# CLI project that wraps the original SDK.

## Tech stack

- **Language:** C# / .NET 8 LTS
- **Template engine:** Scriban (code generation)
- **CLI framework:** System.CommandLine (both cli-builder itself and generated CLIs)
- **Testing:** xUnit, TDD workflow
- **License:** EUPL-1.2

## Architecture (one paragraph)

`ISdkAdapter` extracts `SdkMetadata` from an SDK assembly via `MetadataLoadContext` (read-only, no code execution). An optional `IMetadataEnricher` (future) improves descriptions via a pluggable LLM. `ICliGenerator` takes `SdkMetadata` and emits a C# CLI project that wraps the original SDK using Scriban templates. `SdkMetadata` is the JSON-serializable contract between all stages. All operations return results + `List<Diagnostic>` — exceptions are reserved for environment failures only.

## Documentation hierarchy

Each piece of information exists in exactly one place:

| Document | Level | Contains |
|----------|-------|----------|
| [docs/cli-builder-spec.md](docs/cli-builder-spec.md) | **Spec** | Interfaces, metadata model, config schema, requirements, scope, test strategy |
| [docs/ADR.md](docs/ADR.md) | **Decisions** | 15 architecture decision records — the "why" behind each choice |
| [docs/design-notes.md](docs/design-notes.md) | **Design** | Edge-case policies, behavioral rules, diagnostic codes, test SDK manifest |
| [docs/process.md](docs/process.md) | **Process** | Development methodology (7-phase agent-orchestrated workflow) |
| `docs/internal/` | **Plans** | Agent implementation plans — step-by-step build instructions |
| [docs/FUTURE.md](docs/FUTURE.md) | **Deferred** | Out-of-scope ideas and deferred features |

**When looking for something:** check the spec first (contracts and requirements), then design notes (behavioral details and edge cases), then ADRs (rationale for a decision).

**When changing documentation:** every change must be checked for duplication and proper placement across all levels — this file, the spec, ADRs, design notes, and agent execution plans. Information must exist in exactly one place at the correct granularity level. If a change introduces duplication or puts detail at the wrong level, fix the placement before committing.

## Architectural constraints (must not violate)

- **`MetadataLoadContext` only** — never use `AssemblyLoadContext` ([ADR-003](docs/ADR.md#adr-003-metaloadcontext-only--no-code-execution-during-analysis))
- **Cross-platform** — Windows, Linux, macOS. No hardcoded paths, no platform-specific APIs. `net8.0` only. ([ADR-011](docs/ADR.md#adr-011-cross-platform-support--windows-linux-macos))
- **Generated CLI wraps the original SDK** — depends on SDK + System.CommandLine, not on cli-builder ([ADR-006](docs/ADR.md#adr-006-generated-cli-wrapper-over-the-original-sdk))
- **No silent failures** — every skipped type, renamed parameter, or discarded overload produces a `Diagnostic` ([ADR-015](docs/ADR.md#adr-015-diagnostics-collection-pattern-for-error-handling))
- **Package artifacts only** — compiled assemblies/packages, never raw source code ([ADR-013](docs/ADR.md#adr-013-package-artifacts-over-raw-source-code--per-language-native-metadata))
- **Sanitize all metadata strings** before emitting into generated C# source ([spec](docs/cli-builder-spec.md#generated-code-safety))

## Start here

[First Actions](docs/cli-builder-spec.md#first-actions) — steps 1-8 complete.

**What's done:** The adapter extracts `SdkMetadata` from .NET assemblies. The generator produces compilable CLI projects with real SDK method calls. Multi-arg constructors + static auth (`StripeConfiguration.ApiKey`) support. Validated against three SDKs: TestSdk (12 E2E tests), OpenAI (20 resources, 41 wired, live API), Stripe (136 resources, 490/524 ops wired, live API with `sk_test_` keys). 332 tests, 83.8% coverage.

**What's next:** Step 9 — `--json-input` deserialization for complex params (`IEnumerable<ChatMessage>`, etc.). See [docs/FUTURE.md](docs/FUTURE.md).
