# Changelog

All notable changes to cli-builder.

## v1.1.0 — 2026-04-04

### Features
- **`--json-input` deserialization** — JSON deserialized into options classes, flat flags override on top. Nested SDK objects (Stripe `Recurring`, `ProductData`, `ShippingAddress`) now populatable.
- **Noun collision resolution** — namespace-qualified disambiguation instead of dropping colliding resources. `Stripe.Tax.CustomerService` → `tax-customer`. Stripe: 136 → 196 resources.
- **Null guard system** — value-type CLI options made nullable for `--json-input` operations to prevent System.CommandLine defaults clobbering JSON values.

### Stats
- 347 tests (52 Core + 252 Generator + 43 Integration)
- 93.4% line coverage, 96.4% method coverage
- Stripe: 196 resources (was 136)

## v1.0.0 — 2026-04-04

First release. .NET SDK adapter + C# CLI generator.

### Features

- **Adapter**: Extract `SdkMetadata` from .NET SDK assemblies via `MetadataLoadContext` (no code execution)
  - Service class discovery (`*Service`, `*Client`, `*Api` suffixes)
  - Constructor auth detection (ApiKeyCredential, TokenCredential, string apiKey)
  - Static auth detection (`*Configuration.ApiKey` pattern for Stripe-like SDKs)
  - Multi-arg constructor support (`ChatClient(string model, ApiKeyCredential cred)`)
  - Return type unwrapping (Task, ValueTask, ClientResult, IAsyncEnumerable)
  - Parameter flattening with threshold (10 scalar → flat flags, rest via `--json-input`)
  - Nullable reference type detection, read-only property filtering, abstract type detection

- **Generator**: Emit compilable C# CLI projects from `SdkMetadata`
  - System.CommandLine 2.0 with noun-verb structure
  - Real SDK method calls (not stubs) with type conversion expressions
  - Auth handler with env var > config file > `--api-key` flag precedence
  - `--json` flag with `JsonFormatter` / `TableFormatter` output
  - Streaming support via `await foreach` for `IAsyncEnumerable<T>`
  - `CanConstruct` / `CanWireSdkCall` gates with echo fallback for unwirable operations
  - Two-barrier sanitization (ModelMapper + Scriban escape_csharp)
  - Identifier validation (C# keyword denylist, path safety, variable name collision avoidance)

- **Validated SDKs**:
  - TestSdk: 4 resources, 12 E2E tests (generate → build → run → assert JSON)
  - OpenAI 2.9.1: 20 resources, 169 operations, 41 wired, live API validated
  - Stripe.net 51.0.0: 136 resources, 490/524 operations wired (93%), live API validated

### Stats

- 338 tests (52 Core + 248 Generator + 38 Integration)
- 83.8% line coverage, 95% method coverage
- ~2,400 LOC production code + templates

### Known Limitations

- `--json-input` option exists but doesn't deserialize (Step 9)
- No CLI entry point — cli-builder is a library, not a runnable tool (Step 10)
- Abstract SDK types (`ChatMessage`) can't be deserialized from JSON
- 34 Stripe services without parameterless constructors fall back to echo (need DI support)
- Generated `JsonFormatter` may produce empty objects for SDK types with non-public properties
