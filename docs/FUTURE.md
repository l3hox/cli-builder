# FUTURE.md — Out of Scope for v1

Ideas and features intentionally deferred. Not prioritized — this is a parking lot, not a roadmap.

## Source adapters
- **Python** — AST/inspect, type stubs, pyright for static analysis
- **Kotlin** — JVM reflection or kotlinx-metadata
- **OpenAPI** — spec parsing (overlaps with existing tools like NSwag, OpenAPI Generator)

## Target language generators
- **Python** — click-based CLI output
- **Rust** — clap-based CLI output
- **Kotlin** — clikt-based CLI output

## Agent-assisted enrichment
- `--enrich` flag with pluggable LLM provider (design approved, see ADR-014)
- Enrichment cache (`.enrichment-cache.json`)
- Data minimization policy for enterprise SDK metadata sent to LLMs

## Step 9 candidates (next up)
- **`--json-input` deserialization** — the option exists on commands but doesn't deserialize. Need deep merge with flat flag override. Key challenge: abstract SDK types (`ChatMessage`) need SDK-specific serialization (e.g., `BinaryData.FromString()`). Would unblock ~78 more OpenAI operations.
- **Incremental streaming output** — streaming operations currently collect all items before formatting. True incremental streaming (emit each item as it arrives) improves UX for long-running streams.
- **Stripe test mode validation** — generate CLI from Stripe.net SDK, validate with `sk_test_` keys against live Stripe API.
- **Token caching** — auth handler writes resolved credentials to config file for reuse.

## Tool features
- Runtime wrapper mode (interpret SDK at runtime instead of generating code)
- Incremental regeneration (detect SDK changes and update CLI without full regen)
- `--dump-metadata` flag for debugging (requires auth redaction policy)
- Package publishing (NuGet tool, Homebrew, etc.)
- Test generation for generated CLIs

## Usage model analysis
- How is cli-builder used in practice? One-off generation, recurring CI/CD pipeline step, agentic workflow component?
- Implications for: idempotency (same input → same output?), caching/incremental regen, change detection (diff previous output?), non-interactive mode, stdout/stderr contracts, logging verbosity
- Should inform versioning policy, breaking change definitions, and output stability guarantees

## Distribution
- GUI / web interface
- VS Code / JetBrains plugin for in-IDE generation
