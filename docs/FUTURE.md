# Roadmap

Production roadmap for cli-builder — a .NET SDK CLI generator, with multi-language support planned.

---

## Next up

### Step 12: Python adapter as standalone subprocess
Standalone `cli-builder-adapter-python` package (PyPI). Extracts metadata from installed Python packages via `inspect` module and type annotations. Outputs `SdkMetadata` JSON to stdout. Validates the subprocess-based adapter architecture — adapters are permanent executables in their native language, called by the orchestrator as child processes. See [ADR-016](ADR.md#adr-016-subprocess-based-adapter-architecture--rust-migration-path).

---

## After that

### Incremental streaming output
Streaming operations (`IAsyncEnumerable<T>`) currently collect all items before formatting. True incremental streaming (emit each item as it arrives). NDJSON for pipe-friendly output.

### Package publishing
Generated CLIs need distribution: `dotnet tool install`, Homebrew, self-contained single-file.

### DI/factory pattern support
34 Stripe services without parameterless constructors need `IStripeClient` injection.

### CI/CD integration
GitHub Action, Docker image, output stability guarantees, webhook triggers.

### Token caching
Auth handler writes resolved credentials to config file for reuse.

---

## Later

### Source adapters
- **Kotlin** — JVM reflection or kotlinx-metadata
- **Go** — AST parsing, struct tags
- **OpenAPI** — spec parsing (overlaps with existing tools — lower unique value)

### Target language generators
- **Python** — click-based CLI output
- **Rust** — clap-based CLI output

### Agent-assisted enrichment
- `--enrich` flag with pluggable LLM provider (design approved, see ADR-014)

### v2.0: Rust orchestrator migration
Rewrite the `cli-builder` CLI in Rust (clap, single binary). Calls adapters as subprocesses, reads `SdkMetadata` JSON from stdout. Adapters remain in native languages permanently. C# generator optionally moves to Rust (Tera templates). Distribution: `cargo install cli-builder` — no .NET runtime needed for orchestration. See [ADR-016](ADR.md#adr-016-subprocess-based-adapter-architecture--rust-migration-path).

### Other
- Incremental regeneration (detect SDK changes)
- Test generation for generated CLIs
- Config file (`cli-builder.json`) per-SDK customization
- GUI / VS Code plugin

---

## Completed

- Steps 1-9: Architecture, adapter, generator, real SDK calls, multi-arg constructors, static auth, --json-input deserialization, noun collision resolution
- Step 10: CLI entry point — `cli-builder generate` and `cli-builder inspect` commands, `dotnet tool` packaging, structured diagnostics, exit codes 0/1/2
- Step 9B: Direct param deserialization — IEnumerable&lt;T&gt;, Dictionary&lt;K,V&gt;, Array, bare Class via `--json-input`. CB307 abstract type diagnostics. IsAbstract on TypeRef. Dictionary GenericArguments preserved.
- TestSdk: 7 resources (incl. MessageClient with abstract Message type), 23 E2E tests
- OpenAI 2.9.1: 20 resources, 169 ops, 41 wired (1 pre-existing struct type issue in compile test)
- Stripe.net 51.0.0: 196 resources (was 136 — collisions now resolved), compile validated
- Step 11: SdkMetadata abstraction — StaticAuthSetup→StaticAuthConfig (structured record), AssemblyPath→ArtifactPath, XmlDocPath→DocsPath, Namespace→Module renames, TypeKind.Other added
- 396 tests (all pass), 93.4% line coverage, 96.4% method coverage

**Deferred from Step 11 council (Step 12 prerequisites):**
- StaticAuthConfig Style discriminator (`StaticProperty` vs `ModuleAttribute`) for Python auth patterns
- Language-neutrality reflection guard test (assert no .NET-specific field names via reflection)
- TypeKind may need further values for Python-specific concepts (tuple, union)
