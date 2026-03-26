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

## Tool features
- Runtime wrapper mode (interpret SDK at runtime instead of generating code)
- Incremental regeneration (detect SDK changes and update CLI without full regen)
- `--dump-metadata` flag for debugging (requires auth redaction policy)
- Package publishing (NuGet tool, Homebrew, etc.)
- Test generation for generated CLIs

## Distribution
- GUI / web interface
- VS Code / JetBrains plugin for in-IDE generation
