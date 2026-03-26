# AGENTS.md — cli-builder

Quick-start context for AI agents and contributors working on this codebase.

## What is cli-builder?

A tool that generates agent-ready CLIs from .NET SDK assemblies via reflection. Input: a DLL. Output: a standalone, compilable C# CLI project.

## Tech stack

- **Language:** C# / .NET 8 LTS
- **Template engine:** Scriban (code generation)
- **CLI framework:** System.CommandLine (both cli-builder itself and generated CLIs)
- **Testing:** xUnit, TDD workflow
- **License:** EUPL-1.2

## Architecture (one paragraph)

`ISdkAdapter` extracts `SdkMetadata` from an SDK assembly via `MetadataLoadContext` (read-only, no code execution). An optional `IMetadataEnricher` improves descriptions via a pluggable LLM. `ICliGenerator` takes `SdkMetadata` and emits a standalone C# CLI project using Scriban templates. `SdkMetadata` is the JSON-serializable contract between all stages. All operations return results + `List<Diagnostic>` (never throw for SDK analysis issues — exceptions are reserved for environment failures).

## Key files

| File | Purpose |
|------|---------|
| `cli-builder-spec.md` | Full specification — metadata model, interfaces, config schema, test strategy, scope |
| `docs/ADR.md` | All 15 architecture decision records with rationale |
| `README.md` | Project overview |

## Architectural constraints (must not violate)

- **`MetadataLoadContext` only** — never use `AssemblyLoadContext` to load SDK assemblies (arbitrary code execution risk)
- **Cross-platform** — Windows, Linux, macOS. No hardcoded paths, no platform-specific APIs. Target `net8.0` only.
- **Generated code is standalone** — no runtime dependency on cli-builder
- **No silent failures** — every skipped type, renamed parameter, or discarded overload produces a `Diagnostic`
- **Package artifacts only** — operate on compiled assemblies/packages, never raw source code
- **Sanitize all metadata strings** before emitting into generated C# source (identifier validation, string escaping)

## Pipeline

```
ISdkAdapter ──▶ SdkMetadata ──▶ [IMetadataEnricher] ──▶ SdkMetadata ──▶ ICliGenerator ──▶ CLI Project
                                  (optional, --enrich)
```

## Naming conventions

- Resources: PascalCase class name → kebab-case, suffix stripped (`PaymentIntentService` → `payment-intent`)
- Operations: method name → kebab-case, `Async` stripped (`CreateAsync` → `create`)
- Collisions: hard error, require config override. No silent last-wins.

## Error handling

- SDK analysis issues → `Diagnostic` (severity: Info/Warning/Error, code: `CB0xx`–`CB5xx`)
- Environment failures → exceptions (file not found, corrupted assembly, disk full)
- CLI exit codes: 0 = success/warnings, 1 = error diagnostics, 2 = environment exception

## Config overrides

`cli-builder.json` — resource/operation patterns, excludes, parameter renames, flatten threshold, auth config. See spec for full schema.

## Testing approach

TDD. Purpose-built test SDK assembly for deterministic tests. Golden-file/snapshot tests for generator output. Fuzz tests for metadata string sanitization. CI on Windows + Linux.

## v1 scope

- .NET reflection adapter + C# / System.CommandLine generator
- Two reference SDKs: OpenAI .NET SDK, Stripe.net
- Agent-readiness: `--json`, `--help` at all levels, semantic exit codes, structured errors, pipe-friendly

## Out of v1 scope

- Python/Kotlin/OpenAPI source adapters
- Python/Rust/Kotlin target generators
- Agent-assisted enrichment (`--enrich`)
- Runtime wrapper mode, GUI, package publishing
