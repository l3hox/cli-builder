# cli-builder — Project Specification (v0.1)
*Draft: 2026-03-25*

---

## Problem Statement

AI agents work best with CLI tools — structured output, discoverable commands, composable via pipes. But most SDKs ship without CLIs. Building a CLI by hand for each SDK is tedious, repetitive, and falls out of sync as SDKs evolve. The result: agents can't use these SDKs, and engineers resort to manual portal work or one-off scripts.

cli-builder generates agent-ready CLIs directly from SDK type information, eliminating the manual step.

---

## Core Concept

**Input:** A .NET SDK assembly (DLL/NuGet package)
**Output:** A fully functional, agent-ready CLI tool

The generated CLI follows agent-friendly patterns:
- Noun-verb command structure (`stripe customer list`, `openai model get`)
- `--json` flag for structured output on every command
- Semantic exit codes (0 success, 1 user error, 2+ app-specific)
- Structured error output (JSON, not just stderr text)
- Discoverable via `--help` at every level (root, noun, verb)

---

## Architecture

### Design Principles
- **Language-agnostic design, .NET-first implementation.** The core architecture assumes nothing about the source language. An adapter interface defines how SDK metadata is extracted. v1 ships only the .NET adapter.
- **Reflection-based discovery.** The .NET adapter uses runtime reflection to discover public types, methods, parameters, and return types from SDK assemblies.
- **Convention + configuration.** Sensible defaults from reflection (public service classes → nouns, public methods → verbs), overridable via a config file for edge cases.
- **Generated, not interpreted.** cli-builder generates source code for a standalone CLI, not a runtime wrapper. The output is a compilable project with no dependency on cli-builder itself.
- **SdkMetadata as the contract.** The metadata model is the boundary between source adapters and target generators. It is serializable to JSON from day one, even though v1 only uses it in-memory. This makes it trivial to introduce process boundaries later without refactoring.
- **Cross-platform from day one.** Both cli-builder and generated CLIs run on Windows, Linux, and macOS. No platform-specific code without an abstraction. Target `net8.0` (not platform-specific TFMs). CI tests on Windows and Linux at minimum.

### Key Decisions

All architectural decisions are documented with full rationale in [docs/ADR.md](docs/ADR.md). Summary:

| ADR | Decision |
|-----|----------|
| [001](docs/ADR.md#adr-001-reflection-based-discovery-over-static-analysis-or-schema-parsing) | Reflection-based discovery over static analysis or schema parsing |
| [002](docs/ADR.md#adr-002-c--net-8-lts-as-the-implementation-language) | C# / .NET 8 LTS — reflection APIs are native, Rust would fight the domain |
| [003](docs/ADR.md#adr-003-metaloadcontext-only--no-code-execution-during-analysis) | `MetadataLoadContext` only — no code execution during analysis |
| [004](docs/ADR.md#adr-004-monolith-with-clean-internal-boundaries-for-v1) | Monolith with clean boundaries — single solution, `SdkMetadata` as future process boundary |
| [005](docs/ADR.md#adr-005-sdkmetadata-as-the-serializable-contract-between-adapters-and-generators) | `SdkMetadata` as JSON-serializable contract |
| [006](docs/ADR.md#adr-006-generated-source-code-not-a-runtime-wrapper) | Generated source code, not a runtime wrapper |
| [007](docs/ADR.md#adr-007-complex-parameter-flattening-with-configurable-threshold) | Complex parameter flattening with configurable threshold |
| [008](docs/ADR.md#adr-008-naming-conventions-with-strict-collision-handling) | Naming conventions with strict collision handling |
| [009](docs/ADR.md#adr-009-test-driven-development) | Test-driven development |
| [010](docs/ADR.md#adr-010-scriban-for-code-generation-templates) | Scriban for code generation templates |
| [011](docs/ADR.md#adr-011-cross-platform-support--windows-linux-macos) | Cross-platform — Windows, Linux, macOS |
| [012](docs/ADR.md#adr-012-systemcommandline-for-generated-clis) | System.CommandLine for generated CLIs |
| [013](docs/ADR.md#adr-013-package-artifacts-over-raw-source-code--per-language-native-metadata) | Package artifacts over raw source code |
| [014](docs/ADR.md#adr-014-agent-assisted-metadata-enrichment-with-pluggable-llm-provider) | Agent-assisted enrichment with pluggable LLM (future) |
| [015](docs/ADR.md#adr-015-diagnostics-collection-pattern-for-error-handling) | Diagnostics collection for error handling |

### Component Overview

```
┌───────────────────────────────────────────────────────────────┐
│                     cli-builder (.NET 8)                       │
│                                                               │
│  ┌──────────────┐  ┌───────────────┐  ┌─────────────────────┐│
│  │  ISdkAdapter  │─▶│  SdkMetadata  │─▶│    ICliGenerator    ││
│  │  (extract)    │  │  (JSON-ready) │  │    (emit)           ││
│  └──────┬───────┘  └───────┬───────┘  └──────────┬──────────┘│
│         │        ▲ future  │ process boundary ▲   │           │
│  ┌──────▼───────┐  ┌──────▼────────┐  ┌─────────▼──────────┐│
│  │ DotNetAdapter │  │ IMetadata     │  │  CSharpCli         ││
│  │ (reflection)  │  │ Enricher      │  │  Generator         ││
│  └──────────────┘  │ (optional,    │  └──────────┬──────────┘│
│                    │  --enrich)     │             │           │
│                    └──────┬────────┘  ┌──────────▼──────────┐│
│                           │           │  Output Project      ││
│                    ┌──────▼────────┐  │  (standalone)        ││
│                    │ LLM Provider  │  └─────────────────────┘│
│                    │ (pluggable)   │                          │
│                    └───────────────┘                          │
└───────────────────────────────────────────────────────────────┘

Future source adapters (subprocess, emit SdkMetadata JSON):
┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
│  Python Adapter  │  │ Kotlin Adapter  │  │  OpenAPI Adapter │
│  (AST/inspect)   │  │  (reflection)   │  │  (spec parsing)  │
└─────────────────┘  └─────────────────┘  └─────────────────┘

Future target generators (in-process, no target runtime needed):
┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐
│ Python CLI output│  │  Rust CLI output │  │ Kotlin CLI out  │
│  (click-based)   │  │  (clap-based)    │  │  (clikt-based)  │
└─────────────────┘  └─────────────────┘  └─────────────────┘
```

### Interface Signatures

```csharp
// Source adapter — extracts metadata from an SDK
public interface ISdkAdapter
{
    AdapterResult Extract(AdapterOptions options);
}

public record AdapterResult(
    SdkMetadata Metadata,             // best metadata we could extract (may be degraded)
    List<Diagnostic> Diagnostics      // warnings, skipped types, errors
);

public record AdapterOptions(
    string AssemblyPath,              // path to the SDK DLL
    string? ConfigPath = null,        // optional cli-builder.json overrides
    string? XmlDocPath = null         // optional XML documentation file
);

// Target generator — emits a CLI project from metadata
public interface ICliGenerator
{
    GeneratorResult Generate(SdkMetadata metadata, GeneratorOptions options);
}

public record GeneratorOptions(
    string OutputDirectory,           // where to write the generated project
    string? CliName = null,           // override default CLI name (default: SDK name lowercase)
    bool OverwriteExisting = false
);

public record GeneratorResult(
    string ProjectDirectory,          // path to generated project root
    List<string> GeneratedFiles,      // all files written
    List<Diagnostic> Diagnostics      // warnings, skipped types, etc.
);

public record Diagnostic(
    DiagnosticSeverity Severity,      // Info, Warning, Error
    string Code,                      // e.g., "CB001" for missing dependency
    string Message
);
```

### Adapter Interface (language-agnostic)

The adapter produces an `SdkMetadata` model:

```
SdkMetadata
├── Name: string                    # SDK name (e.g., "Stripe")
├── Version: string                 # SDK version
├── Resources: Resource[]           # nouns
│   ├── Name: string                # e.g., "Customer", "Model"
│   ├── Description: string?        # from XML docs or annotations
│   └── Operations: Operation[]     # verbs
│       ├── Name: string            # e.g., "List", "Get", "Create"
│       ├── Description: string?
│       ├── Parameters: Parameter[]
│       │   ├── Name: string
│       │   ├── Type: TypeRef
│       │   ├── Required: bool
│       │   ├── DefaultValue: JsonElement?  # preserves type fidelity (10 vs "10")
│       │   └── Description: string?
│       └── ReturnType: TypeRef
└── AuthPatterns: AuthPattern[]     # detected auth mechanisms

TypeRef (discriminated union — recursive for generics)
├── Kind: enum                      # Primitive, Enum, Class, Generic, Array, Dictionary
├── Name: string                    # e.g., "string", "Customer", "StripeList"
├── IsNullable: bool                # from nullability annotations
├── GenericArguments: TypeRef[]     # e.g., Task<StripeList<Customer>> → [StripeList<Customer>]
├── EnumValues: string[]?           # populated when Kind = Enum
├── Properties: Parameter[]?        # populated when Kind = Class (for options/input objects)
└── ElementType: TypeRef?           # populated when Kind = Array

AuthPattern
├── Type: enum                      # ApiKey, BearerToken, OAuth, Custom
├── EnvVar: string                  # e.g., "STRIPE_API_KEY"
├── ParameterName: string           # constructor/method parameter name in the SDK
├── HeaderName: string?             # e.g., "Authorization", "X-Api-Key"
└── Description: string?            # how this auth mechanism works
```

**TypeRef design notes:**
- `Task<T>`, `ValueTask<T>`, and `IAsyncEnumerable<T>` are **unwrapped** by the adapter — the generator sees the inner type, not the async wrapper. The adapter handles async detection separately.
- When `Kind = Class` and the type is used as a method parameter (e.g., Stripe's `CustomerCreateOptions`), `Properties` is populated so the generator can decide how to surface them (see "Complex parameter policy" below).
- When `Kind = Generic`, `GenericArguments` carries the type parameters recursively (e.g., `StripeList<Customer>` → Kind=Generic, Name="StripeList", GenericArguments=[TypeRef{Kind=Class, Name="Customer"}]).

### .NET Adapter — Discovery Strategy

1. Load target assembly via `MetadataLoadContext` (**not** `AssemblyLoadContext` — see "Assembly loading security" below)
2. Scan for public classes matching service patterns:
   - Classes ending in `Service`, `Client`, `Api` (configurable via `resourcePattern`)
   - Classes with public async methods returning typed results
3. For each service class → Resource (noun = class name minus suffix, kebab-cased)
4. For each public method → Operation (verb = method name, minus `Async` suffix, kebab-cased)
5. For each method parameter → Parameter (type, name, nullability)
6. Unwrap async return types: `Task<T>` → `T`, `ValueTask<T>` → `T`, `IAsyncEnumerable<T>` → `T` (marked as streaming)
7. Extract XML documentation comments where available
8. Detect auth patterns (constructor parameters taking keys, tokens, credentials)

**Assembly loading security:** `MetadataLoadContext` is the **only** permitted assembly loading mechanism. `AssemblyLoadContext` is explicitly prohibited. See [ADR-003](docs/ADR.md#adr-003-metaloadcontext-only--no-code-execution-during-analysis) for rationale.

**Dependency resolution strategy:** `MetadataLoadContext` requires a `PathAssemblyResolver` with all dependency paths pre-enumerated. The adapter resolves dependencies in this order:
1. Scan the assembly's directory for sibling DLLs
2. Scan the NuGet global packages cache (resolved at runtime via `NuGetEnvironment` or `NUGET_PACKAGES` env var — `~/.nuget/packages/` on Linux/macOS, `%USERPROFILE%\.nuget\packages\` on Windows)
3. Scan .NET runtime reference assemblies via `RuntimeEnvironment.GetRuntimeDirectory()`
4. If a dependency cannot be resolved: **log a warning** with the missing assembly name, and skip types that depend on it. The adapter must never silently drop types — every skipped type produces a diagnostic in the output.

**Naming conventions:**
- Resource names: class name minus suffix, PascalCase → kebab-case (`PaymentIntentService` → `payment-intent`)
- Operation names: method name minus `Async` suffix, PascalCase → kebab-case (`CreateAsync` → `create`, `ListAutoPayments` → `list-auto-payments`)
- Collision resolution: if two classes produce the same noun (e.g., `CustomerService` and `CustomerApiService` → both `customer`), the adapter emits an error and requires a config override to disambiguate. No silent last-wins.
- Overloaded methods: when multiple overloads map to the same verb, prefer the overload with the richest parameter set (most parameters). Other overloads are discarded with a diagnostic. Config overrides can select a specific overload by parameter count.

**Complex parameter policy:** When a method takes an options/input class (e.g., Stripe's `CustomerCreateOptions`) rather than primitives:
- If the class has **10 or fewer** scalar properties: flatten to individual `--flag` parameters
- If the class has **more than 10** scalar properties: flatten the first 10 and add a `--json-input` flag that accepts the full object as JSON
- Nested object properties (e.g., `Address` inside `CustomerCreateOptions`) are always surfaced via `--json-input`, not flattened
- These thresholds are configurable via `cli-builder.json`

### Generated Code Safety

All SDK metadata strings (type names, method names, parameter names, XML doc descriptions) flow into generated C# source code. To prevent code injection:

- **Identifier validation:** All generated C# identifiers (class names, method names, variable names) are validated against `[a-zA-Z_][a-zA-Z0-9_]*`. Any metadata string that fails this check is sanitized (non-matching characters replaced with `_`) and a diagnostic is emitted.
- **String content escaping:** XML doc descriptions and any user-visible strings are emitted using C# verbatim string literals (`@"..."`) or escaped via `SecurityElement.Escape()`. Never use raw string interpolation of metadata into generated source.
- **No credential echo:** Generated error messages and `--json` error output must never include credential values. Auth parameters are masked in all diagnostic output.

### Agent-Assisted Metadata Enrichment (future, not v1)

Optional `--enrich` flag improves human-facing quality (descriptions, examples, naming) via a pluggable LLM. See [ADR-014](docs/ADR.md#adr-014-agent-assisted-metadata-enrichment-with-pluggable-llm-provider) for full design and rationale.

```csharp
public interface IMetadataEnricher
{
    SdkMetadata Enrich(SdkMetadata metadata, EnricherOptions options);
}

public interface ILlmProvider
{
    Task<string> CompleteAsync(string prompt, LlmOptions? options = null);
}
```

**Enrichment rules:**
- Only fills empty/terse fields — never overrides explicit config overrides
- Results cached to `.enrichment-cache.json` (committable, stable across regenerations)
- All enriched text goes through Generated Code Safety sanitization

### Configuration Override (`cli-builder.json`)

For cases where reflection alone isn't enough:

```json
{
  "assembly": "Stripe.net.dll",
  "resourcePattern": "*Service",
  "operationPattern": "*Async",
  "exclude": ["BaseService", "InternalService"],
  "flattenThreshold": 10,
  "overrides": {
    "CustomerService": {
      "noun": "customer",
      "operations": {
        "ListAsync": { "verb": "list" },
        "GetAsync": { "verb": "get" },
        "InternalMethod": { "exclude": true }
      }
    }
  },
  "parameterOverrides": {
    "customerId": { "cliName": "customer-id" },
    "apiVersion": { "exclude": true }
  },
  "auth": {
    "type": "api-key",
    "envVar": "STRIPE_API_KEY",
    "parameterName": "apiKey"
  }
}
```

Configuration fields:
- `resourcePattern` — glob pattern for service class discovery (default: `*Service,*Client,*Api`)
- `operationPattern` — glob pattern for method name matching; suffix is stripped from verb name (default: `*Async` strips `Async`)
- `exclude` — class names to skip entirely
- `flattenThreshold` — max scalar properties before adding `--json-input` (default: 10)
- `overrides.<Class>.operations.<Method>.exclude` — skip individual operations
- `parameterOverrides.<name>.cliName` — rename a parameter's CLI flag globally
- `parameterOverrides.<name>.exclude` — hide a parameter from the CLI globally

### CLI Generator — Output Shape

For input SDK "Stripe", generates a project:

```
stripe-cli/
├── stripe-cli.csproj          # standalone, no cli-builder dependency
├── Program.cs                 # entry point, command registration
├── Commands/
│   ├── CustomerCommands.cs    # customer list|get|create|update|delete
│   ├── PaymentIntentCommands.cs
│   └── ...
├── Output/
│   ├── JsonFormatter.cs       # --json output
│   └── TableFormatter.cs      # human-readable default
└── Auth/
    └── AuthHandler.cs         # env var / config file auth, token caching
                               # cache location: <AppData>/<cli-name>/ (cross-platform)
```

Generated CLI usage:

```bash
# Auth
stripe-cli login --api-key sk_test_...
stripe-cli login              # reads from STRIPE_API_KEY env var

# CRUD operations
stripe-cli customer list --limit 10 --json
stripe-cli customer get --id cus_123
stripe-cli customer create --email "test@example.com" --name "Test"

# Discoverable
stripe-cli --help              # lists all resources (nouns)
stripe-cli customer --help     # lists all operations (verbs)
stripe-cli customer create --help  # lists all parameters
```

---

## Agent-Readiness Requirements

Every generated CLI must satisfy:

| Requirement | Implementation |
|-------------|---------------|
| Structured output | `--json` flag on every command, JSON to stdout |
| Human-readable default | Table/text format when `--json` absent |
| Discoverable commands | `--help` at root, noun, and verb levels |
| Noun-verb structure | `<tool> <resource> <action> [--params]` |
| Semantic exit codes | 0=success, 1=user error, 2=auth error, 3+=app-specific |
| Structured errors | JSON error object to stderr with code + message |
| Non-interactive auth | Env var (preferred), config file, or `--api-key` flag (last resort — leaks to `ps`/shell history). No browser popups. |
| Pipe-friendly | No color/spinners when stdout is not a TTY (detect via `Console.IsOutputRedirected`) |

---

## v1 Scope Boundary

### In scope
- .NET reflection adapter
- CLI code generator (C# output, System.CommandLine-based)
- Config override file support
- Agent-readiness features (table above)
- Two reference SDKs working end-to-end:
  - **OpenAI .NET SDK** — headline demo (no official CLI exists)
  - **Stripe.net** — scale proof (340M+ downloads, huge typed surface)
- README with architecture explanation and judgment calls
- ADR for the core design decision (reflection vs static analysis vs schema)
- FUTURE.md listing out-of-scope ideas

### Out of scope (FUTURE.md)
- **Source adapters:** Python (AST/inspect), Kotlin (reflection), OpenAPI spec (would overlap with existing tools — intentionally deferred)
- **Target language emitters:** Python (click-based), Rust (clap-based), Kotlin (clikt-based) — v1 emits C#/System.CommandLine only
- Runtime wrapper mode (interpret SDK at runtime instead of generating code)
- GUI / web interface
- Package publishing (NuGet tool, Homebrew, etc.)
- Incremental regeneration (detect SDK changes and update CLI)
- Test generation for generated CLIs
- Agent-assisted metadata enrichment (`--enrich` flag, pluggable LLM provider)

---

## Test Strategy

### Generation tests (no API account needed)
- Load SDK assembly → verify `SdkMetadata` is correct:
  - Assert expected resource count, operation count, parameter count per operation
  - Assert `TypeRef` correctly unwraps async types (`Task<T>` → `T`)
  - Assert overloaded methods are disambiguated per policy (not silently collapsed)
  - Assert naming conventions applied (kebab-case, suffix stripping)
- Generate CLI project → verify it compiles in isolation (clean machine, no cli-builder dependency)
- Verify `--help` output structure at all levels (root, noun, verb)
- Verify `--json` flag produces valid JSON
- Verify exit codes for all defined codes: 0 (success), 1 (user error), 2 (auth error)
- Golden-file / snapshot tests: generated output is diffed against a committed baseline to detect unintended generator changes
- Config override tests: verify `exclude`, `noun`/`verb` rename, `parameterOverrides`, `operationPattern` all work
- Degenerate input tests: empty assembly (no services), assembly with zero public methods, malformed `cli-builder.json`
- Metadata string sanitization: fuzz SDK metadata with C# metacharacters (`"`, `{`, `//`, `\`) and verify generated output still compiles
- `SdkMetadata` round-trip: serialize to JSON and deserialize, verify equality (no circular reference failures)

### Integration tests (account needed, optional)
- **Stripe test mode** — `sk_test_` keys, free, no credit card. Best for live demos.
- **OpenAI** — API key needed, free tier available for basic calls.
- **GitHub/Octokit** — personal access token, free.

All generation tests are runnable against the assembly alone, with no API credentials. Integration tests validate runtime behavior only.

### Quality bar
- Generated CLI compiles without modification on a clean machine
- `--help` is accurate and complete
- Agent can discover and call commands without prior knowledge (just `--help` chain)
- Generated CLI calls the correct SDK method for each command (behavioral correctness, validated via integration tests)
- No credential values appear in error output or diagnostics

---

## Target SDKs (ranked)

| Priority | SDK | NuGet Package | Why |
|----------|-----|--------------|-----|
| 1 | OpenAI .NET | `OpenAI` | No official CLI, hottest API, clean typed surface |
| 2 | Stripe | `Stripe.net` | Gold standard typed SDK, test mode, proves scale |
| 3 | Internal enterprise SDK | Internal | Validates against real production SDK with no public API/CLI |

Optional/later:
- Octokit.NET — `gh` doesn't cover full API surface
- Elastic.Clients.Elasticsearch — genuine gap, harder fluent API pattern
- SendGrid — small surface, good for tutorials

---

## First Actions

1. ~~Spec out architecture~~ ← this document
2. ~~Create repo, README with problem statement and v1 scope~~
3. ~~ADRs for all architectural decisions~~ ← [docs/ADR.md](docs/ADR.md)
4. Scaffold: adapter interface, .NET adapter, metadata model
5. First spike: OpenAI .NET SDK → `SdkMetadata` → inspect what comes out
   - **Acceptance criteria:** adapter extracts resources with correct names, operations with correct verbs, parameters with resolved `TypeRef` (no opaque `object` types for known SDK types). Output `SdkMetadata` as JSON, commit as a reference fixture.
6. CLI generator: metadata → compilable C# project
7. Validate: does the generated CLI actually work against Stripe test mode?

---

## Success Criteria

cli-builder v0.1 is done when:
- `cli-builder generate --assembly OpenAI.dll` produces a working CLI
- `cli-builder generate --assembly Stripe.net.dll` produces a working CLI
- Both generated CLIs pass agent-readiness requirements (table above)
- Adapter interface is documented and a second adapter could be built without touching core
- Architecture is explained in README with visible judgment calls (ADRs)

---

## References

### Agent-friendly CLI design patterns
- [Making your CLI agent-friendly — Speakeasy](https://www.speakeasy.com/blog/engineering-agent-friendly-cli)
- [Keep the Terminal Relevant: Patterns for AI Agent Driven CLIs — InfoQ](https://www.infoq.com/articles/ai-agent-cli/)
- [Writing CLI Tools That AI Agents Actually Want to Use — DEV.to](https://dev.to/uenyioha/writing-cli-tools-that-ai-agents-actually-want-to-use-39no)
- [CLI-First Skill Design — Awesome Agentic Patterns](https://agentic-patterns.com/patterns/cli-first-skill-design/)
- [Structured Output Specification — Awesome Agentic Patterns](https://agentic-patterns.com/patterns/structured-output-specification/)
- [Dual-Use Tool Design — Awesome Agentic Patterns](https://github.com/nibzard/awesome-agentic-patterns/blob/main/patterns/dual-use-tool-design.md)
- [Command Line Interface Guidelines](https://clig.dev/)

### MCP vs CLI landscape (March 2026)
- [MCP is Dead; Long Live MCP!](https://chrlschn.dev/blog/2026/03/mcp-is-dead-long-live-mcp/)
- [Why CLI Tools Are Beating MCP for AI Agents](https://jannikreinhard.com/2026/02/22/why-cli-tools-are-beating-mcp-for-ai-agents/)
- [MCP vs CLI: Benchmarking AI Agent Cost & Reliability](https://www.scalekit.com/blog/mcp-vs-cli-use)
- [The MCP vs. CLI Debate Is the Wrong Fight](https://medium.com/@tobias_pfuetze/the-mcp-vs-cli-debate-is-the-wrong-fight-a87f1b4c8006)
- [MCP vs CLI: What Perplexity's Move Actually Means](https://repello.ai/blog/mcp-vs-cli)
- [MCP vs CLI: A Practical Decision Framework](https://manveerc.substack.com/p/mcp-vs-cli-ai-agents)

### Existing tools (landscape)
- [mcp2cli — MCP/OpenAPI/GraphQL to CLI at runtime](https://github.com/knowsuchagency/mcp2cli)
- [openapi-cli-generator — CLI from OpenAPI 3 spec](https://github.com/danielgtaylor/openapi-cli-generator)
- [OpenAPI Generator — SDK/CLI generation ecosystem](https://github.com/OpenAPITools/openapi-generator)

---

*This spec is the canonical reference for cli-builder behavior — interfaces, models, config schema, test strategy, and scope. Architectural decisions and their rationale live in [docs/ADR.md](docs/ADR.md). Each piece of information should exist in exactly one place.*
