# Roadmap

Production roadmap for cli-builder — a .NET SDK CLI generator, with multi-language support planned.

---

## Next up

### Step 9: `--json-input` deserialization
**Status: Next.** The `--json-input` option exists on commands but doesn't deserialize. Without it, nested SDK objects (`PriceCreateOptions.Recurring`, `ChatCompletionOptions.Tools`) can't be populated. Blocks ~78 OpenAI and many Stripe operations with nested params.

### Step 10: cli-builder CLI entry point
`cli-builder generate --assembly Stripe.net.dll --output ./stripe-cli`. Currently a library — users can't run it without demo scripts. Need:
- `dotnet tool` packaging
- `generate` command with `--assembly`, `--output`, `--name`, `--config` flags
- `inspect` command to dump metadata without generating
- Structured diagnostics output

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

## Completed (v1.0)

- Steps 1-8: Architecture, adapter, generator, real SDK calls, multi-arg constructors, static auth
- TestSdk: 4 resources, 12 E2E tests
- OpenAI 2.9.1: 20 resources, 169 ops, 41 wired, live API validated
- Stripe.net 51.0.0: 136 resources, 490/524 ops wired (93%), live API validated
- 338 tests, 83.8% line coverage, 95% method coverage
