# Roadmap

Production roadmap for cli-builder — a .NET SDK CLI generator, with multi-language support planned.

---

## Next up

### Step 9B: Direct params + abstract type handling
Unblocks `chat complete-chat` and other OpenAI operations with complex direct params (`IEnumerable<ChatMessage>`). Two sub-problems:
- **Direct param deserialization** — method params like `messages` that are `IEnumerable<T>` need to accept JSON input (currently they're echo-stubbed via `CanWireSdkCall = false`)
- **Abstract type serialization** — `ChatMessage` is abstract (factory pattern: `ChatMessage.CreateUserMessage()`). `JsonSerializer.Deserialize` fails. Options: SDK's `BinaryData.FromString()` pass-through, custom `JsonConverter<T>`, or protocol-method routing that accepts raw JSON

This is primarily an OpenAI problem — Stripe uses concrete types everywhere.

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

## Completed

- Steps 1-9: Architecture, adapter, generator, real SDK calls, multi-arg constructors, static auth, --json-input deserialization, noun collision resolution
- TestSdk: 6 resources, 15 E2E tests (including --json-input merge/override/error)
- OpenAI 2.9.1: 20 resources, 169 ops, 41 wired, live API validated
- Stripe.net 51.0.0: 196 resources (was 136 — collisions now resolved), live API validated
- 343 tests, 93.4% line coverage, 96.4% method coverage
