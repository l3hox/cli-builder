# Roadmap

Production roadmap for cli-builder — a .NET SDK CLI generator, with multi-language support planned.

---

## Next up

### Step 12b: Python adapter hardening + real SDK validation
- Write missing test files: `test_type_mapper.py`, `test_auth_detector.py`, `test_extractor.py`, `test_error_paths.py`, `test_integration.py` (needs pytest)
- Create `docs/sdk-metadata-schema.json` — machine-readable JSON schema for cross-adapter contract validation
- ADR-013 compliant extraction: `.pyi` stub parsing via `ast.parse` (currently uses controlled import with CB601 diagnostic)
- Stripe validation: service/auth detection against `stripe-python` (StripeObject handling)
- Wire `--adapter python` into `cli-builder generate` (blocked on StaticAuthConfig.Style discriminator)

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

### Rust generator migration (ADR-017)

All generators consolidated in Rust with shared ModelMapper + Tera templates:
- **Step 13**: Python generator in Rust — **DONE** (shared core + click templates, 90 Rust tests, golden files)
- **Step 14**: C# generator ported to Rust/Tera — **DONE** (CSharpProfile, 6 Tera templates, compile-validated, 62 gen-csharp tests, golden snapshots)
- **Step 15**: Rust orchestrator (single `cli-builder` binary, `cargo install cli-builder`)
- Later: Kotlin, Go, TypeScript generators (~500 lines of templates each)

Adapters stay native forever (ADR-016). Only generators + orchestrator move to Rust.

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
- TestSdk: 7 resources (incl. MessageClient with abstract Message type), 23 E2E tests
- OpenAI 2.9.1: 20 resources, 169 ops, 41 wired (1 pre-existing struct type issue in compile test)
- Stripe.net 51.0.0: 196 resources (was 136 — collisions now resolved), compile validated
- Step 11: SdkMetadata abstraction — StaticAuthSetup→StaticAuthConfig (structured record), AssemblyPath→ArtifactPath, XmlDocPath→DocsPath, Namespace→Module renames, TypeKind.Other added
- Step 12 MVP: Python adapter (`cli-builder-adapter-python`) — extracts SdkMetadata from Python TestSdk, JSON schema compatible with .NET adapter. Architecture proof: cross-adapter key structure match.
- Step 13: Python CLI generator in Rust (`cli-builder-rust/`) — shared core (ModelMapper, ParameterFlattener, IdentifierValidator with LanguageProfile trait) + click-based Python templates via Tera. Council-reviewed with fixes: static_auth validation, auth.env_var escaping, sentinel nullability decoupling. Golden file snapshots via insta.
- Step 14: C# generator ported to Rust/Tera (`cli-builder-gen-csharp`) — CSharpProfile + 6 Tera templates + 6 post-processing transforms. Compile-validated (dotnet build passes). OpenAI 20 resources, Stripe 196 resources — matching .NET generator.
- 397 .NET tests + 152 Rust tests (64 core + 62 gen-csharp + 26 gen-python) + 109 Python tests

**Deferred from Step 11 council (Step 12 prerequisites):**
- StaticAuthConfig Style discriminator (`StaticProperty` vs `ModuleAttribute`) for Python auth patterns
- Language-neutrality reflection guard test (assert no .NET-specific field names via reflection)
- TypeKind may need further values for Python-specific concepts (tuple, union)
