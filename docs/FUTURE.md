# Roadmap

Production roadmap for cli-builder — a .NET SDK CLI generator, with multi-language support planned.

---

## Next up

### Step 9B polish
Deferred from council review:
- `MessageSend_FlatFlagOverridesOptions_NotDirectParam` E2E test (flat flag wins over JSON options in mixed direct-param+options operation)
- `CanWireSdkCall_StreamParam_ReturnsFalse` + tests for all 5 IsBinaryType entries (BinaryData, Stream, ReadOnlyMemory, ReadOnlySpan — only BinaryContent tested)
- OpenAI/Stripe wire-count pinning integration tests (`OpenAI_InfraParamsFiltered_WireCount`, `Stripe_NoRegressions_WireCount`)
- OpenAI struct type classification fix (MessageRole, GeneratedSpeechVoice classified as bare Class instead of Enum — pre-existing, exposed by 9B wiring more operations)

### Step 11: SdkMetadata abstraction
Remove .NET-specific leaks from the metadata contract (`StaticAuthSetup` stores C# expressions, `AdapterOptions.AssemblyPath` is .NET-specific). Prepare for multi-language adapters.

### Step 12: Python adapter proof-of-concept
Second source language. Extracts metadata from Python packages via AST/inspect or type stubs. Proves the adapter interface is truly language-agnostic.

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
- TestSdk: 6 resources, 15 E2E tests (including --json-input merge/override/error)
- OpenAI 2.9.1: 20 resources, 169 ops, 41 wired, live API validated
- Stripe.net 51.0.0: 196 resources (was 136 — collisions now resolved), live API validated
- 367 tests, 93.4% line coverage, 96.4% method coverage
