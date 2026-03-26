# Architecture Decision Records

This document captures the key architectural decisions made for cli-builder, following the [Nygard ADR format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions).

---

## ADR-001: Reflection-based discovery over static analysis or schema parsing

**Date:** 2026-03-25
**Status:** Accepted

### Context

cli-builder needs to extract type metadata (classes, methods, parameters, return types) from .NET SDK assemblies to generate CLI tools. Three approaches were considered:

1. **Runtime reflection** — load the assembly and inspect types using .NET reflection APIs
2. **Static analysis** — parse C# source code or IL bytecode using Roslyn or ECMA-335 metadata readers
3. **Schema parsing** — consume an existing description format (OpenAPI spec, XML doc file, NuGet metadata)

### Decision

We use **runtime reflection** via `MetadataLoadContext` as the primary discovery mechanism.

### Rationale

- **Reflection sees what the runtime sees.** Compiled assemblies are the ground truth — source code may differ from what ships, and schemas may be incomplete or stale.
- **Reflection is already built.** .NET provides `MetadataLoadContext` for safe, read-only metadata inspection. Roslyn or IL parsing would require building an equivalent type resolver from scratch.
- **Generics, nullability, attributes are first-class.** Reflection APIs surface these directly. Static analysis or schema approaches require additional mapping layers.
- **Schema parsing assumes a schema exists.** Most .NET SDKs don't ship OpenAPI specs. XML doc comments are supplementary, not structural — they describe methods but don't define the type graph.

### Consequences

- **Positive:** Works with any compiled .NET assembly, regardless of source availability. Leverages battle-tested .NET APIs. Single approach covers all .NET SDKs.
- **Negative:** Requires the assembly and all its dependencies to be available at analysis time. Cannot discover internal or source-only patterns (extension methods on static classes, source generators). The dependency resolution strategy must be robust (see ADR-003).
- **Mitigated by:** Configuration overrides (`cli-builder.json`) handle cases where reflection alone is insufficient — rename nouns/verbs, exclude operations, override parameter mappings.

---

## ADR-002: C# / .NET 8 LTS as the implementation language

**Date:** 2026-03-25
**Status:** Accepted

### Context

cli-builder's v1 supports .NET as both the source (SDK to analyze) and target (CLI to generate). The tool itself needs a language. Candidates considered: C#, Rust, Go, TypeScript.

### Decision

cli-builder is written in **C# targeting .NET 8** (Long-Term Support, supported until November 2026).

### Rationale

The core operation — loading .NET assemblies and extracting type metadata — is native to .NET:

- `MetadataLoadContext` for safe assembly loading without code execution
- Full type system fidelity: generics, nullable annotations, custom attributes
- `PathAssemblyResolver` for dependency resolution + NuGet cache scanning
- XML documentation comment extraction

Doing this from Rust or Go would mean either reimplementing a significant portion of the CLR metadata reader (ECMA-335 tables, generics encoding, nullability annotation parsing) using immature libraries, or shelling out to a .NET helper process — reintroducing the .NET dependency.

The concern about future non-.NET adapters is addressed architecturally, not by language choice. Source adapters naturally want their source language's runtime (Python adapter needs Python, Kotlin adapter needs JVM). They will cross process boundaries regardless of what the host tool is written in. `SdkMetadata` is the serializable contract at that boundary (see ADR-005).

Target generators are just code emitters (string templates) — they don't need the target language's runtime and can all live in a single process.

### Consequences

- **Positive:** Zero friction for v1. Full access to .NET reflection APIs. Single toolchain for development.
- **Negative:** Future contributors must know C#. Distribution requires .NET runtime (or self-contained publish).
- **Mitigated by:** .NET 8 supports single-file self-contained publishing. The adapter interface is language-agnostic by design — non-.NET adapters run as subprocesses.

---

## ADR-003: MetadataLoadContext only — no code execution during analysis

**Date:** 2026-03-25
**Status:** Accepted

### Context

.NET offers two mechanisms for loading assemblies:

- **`MetadataLoadContext`** — loads assemblies for metadata inspection only. No code is executed: no static constructors, no module initializers, no type initializers.
- **`AssemblyLoadContext`** — loads assemblies for execution. Types are fully usable but any code in the assembly runs (static constructors fire when types are first accessed).

cli-builder loads user-provided SDK assemblies (DLLs from NuGet packages or local builds). These are third-party binaries.

### Decision

`MetadataLoadContext` is the **only** permitted assembly loading mechanism. `AssemblyLoadContext` is explicitly prohibited for SDK analysis.

### Rationale

`AssemblyLoadContext` would execute code in third-party assemblies during what appears to be a read-only analysis step. A trojanized NuGet package (a real-world supply chain attack vector) could achieve arbitrary code execution on the developer's machine via static constructors or module initializers.

`MetadataLoadContext` provides all the type information cli-builder needs (class names, method signatures, parameter types, attributes, nullability annotations) without executing any code.

### Consequences

- **Positive:** No arbitrary code execution risk from analyzed assemblies. Clear security boundary.
- **Negative:** `MetadataLoadContext` requires a `PathAssemblyResolver` with all dependency paths pre-enumerated. This means cli-builder must implement a dependency resolution strategy:
  1. Scan the assembly's directory for sibling DLLs
  2. Scan the NuGet global packages cache (`~/.nuget/packages/`)
  3. Scan .NET runtime reference assemblies (for `System.*` types)
  4. If a dependency cannot be resolved: log a warning and skip types that depend on it — never silently drop types
- **Negative:** Cannot inspect runtime-only behavior (method bodies, dynamic dispatch). Reflection is limited to the type surface.
- **Mitigated by:** The dependency resolution order covers the common cases. The diagnostic system ensures missing dependencies are visible, not silent.

---

## ADR-004: Monolith with clean internal boundaries for v1

**Date:** 2026-03-25
**Status:** Accepted

### Context

cli-builder has two extension points: source adapters (extract metadata from SDKs) and target generators (emit CLI projects). These could be:

1. **In-process interfaces** — single .NET solution, adapters and generators are class library references
2. **Plugin architecture** — runtime plugin loading via `AssemblyLoadContext` or MEF
3. **Microservice / subprocess architecture** — each adapter/generator is a separate process communicating via IPC or stdin/stdout

### Decision

v1 is a **single .NET solution** with in-process interfaces. No plugin loading, no IPC, no microservices.

### Rationale

v1 ships one source adapter (.NET) and one target generator (C#). There is no need for runtime extensibility yet. Adding plugin loading or IPC introduces complexity (versioning, error handling, serialization) with zero benefit until a second adapter language exists.

The `ISdkAdapter` and `ICliGenerator` interfaces exist as in-process abstractions with `SdkMetadata` as the contract between them. Because `SdkMetadata` is JSON-serializable from day one (see ADR-005), the internal interface can become an external process boundary later with zero refactoring.

### Consequences

- **Positive:** Simple build, simple debugging, single deployment artifact. No serialization overhead.
- **Negative:** Adding a Python adapter later requires introducing a process boundary that doesn't exist in v1.
- **Mitigated by:** `SdkMetadata` JSON serialization is tested from day one (round-trip tests in the test strategy). When process boundaries are needed, the schema is already proven.

---

## ADR-005: SdkMetadata as the serializable contract between adapters and generators

**Date:** 2026-03-25
**Status:** Accepted

### Context

Adapters extract metadata, generators consume it. The model between them determines:

- Whether adapters and generators can be developed independently
- Whether adapters can run in separate processes (needed for non-.NET languages)
- Whether metadata can be inspected, debugged, and tested in isolation

### Decision

`SdkMetadata` is a **JSON-serializable, language-agnostic model** that serves as the contract between adapters and generators. It is serializable from day one, even though v1 only uses it in-memory.

The model includes:

- **`TypeRef`** — a discriminated union (Kind, Name, IsNullable, GenericArguments, EnumValues, Properties, ElementType) that fully represents .NET types including generics, enums, and complex objects
- **`AuthPattern`** — detected auth mechanisms (Type, EnvVar, ParameterName, HeaderName)
- **`DefaultValue: JsonElement?`** — preserves type fidelity (integer 10 is distinct from string "10")

### Rationale

A well-defined, serializable contract enables:

- **Testing in isolation.** Adapters can be tested by asserting on serialized `SdkMetadata` JSON. Generators can be tested with hand-crafted JSON fixtures.
- **Future process boundaries.** A Python adapter can emit `SdkMetadata` JSON to stdout. A Kotlin adapter can do the same. The host tool consumes JSON regardless of source language.
- **Debugging.** `cli-builder generate --dump-metadata` can output the intermediate model for inspection.

`JsonElement?` for `DefaultValue` was chosen over `string?` because string serialization loses type fidelity — an integer default of `10` and a string default of `"10"` become indistinguishable.

### Consequences

- **Positive:** Clean boundary, testable in isolation, future-proof for multi-language support.
- **Negative:** `TypeRef` is recursive (generics contain TypeRefs). JSON serialization must handle circular references — tested via round-trip tests.
- **Positive:** Async types (`Task<T>`, `ValueTask<T>`, `IAsyncEnumerable<T>`) are unwrapped by the adapter, so generators never see async wrappers — simpler generator logic.

---

## ADR-006: Generated source code, not a runtime wrapper

**Date:** 2026-03-25
**Status:** Accepted

### Context

cli-builder could produce CLIs in two ways:

1. **Code generation** — emit a compilable C# project that users build and distribute independently
2. **Runtime wrapper** — ship a generic CLI binary that loads the SDK at runtime and dynamically invokes methods

### Decision

cli-builder generates **standalone source code**. The output is a compilable C# project with no dependency on cli-builder itself.

### Rationale

- **No runtime dependency.** Generated CLIs can be built, published, and distributed without cli-builder installed. Users own the output.
- **Debuggable.** Generated code is readable C# — users can modify, extend, or fix it.
- **Auditable.** The generated project can be reviewed for security before deployment. A runtime wrapper hides the invocation logic.
- **Distributable.** Generated projects can be published as `dotnet tool` packages, standalone binaries, or Docker images using standard .NET tooling.

### Consequences

- **Positive:** Zero runtime dependency on cli-builder. Users can modify generated code. Standard .NET build/publish workflow.
- **Negative:** SDK version bumps require regeneration. No incremental updates — the entire project is regenerated.
- **Negative:** Generated code safety is a concern — SDK metadata strings flow into source code and must be sanitized (identifier validation, string escaping, no credential echo).
- **Mitigated by:** Regeneration is a single `cli-builder generate` command. Generated code safety rules are specified in the spec and enforced by the generator.

---

## ADR-007: Complex parameter flattening with configurable threshold

**Date:** 2026-03-25
**Status:** Accepted

### Context

Many .NET SDK methods take "options" or "input" classes rather than individual primitive parameters. For example, Stripe's `CustomerService.CreateAsync(CustomerCreateOptions options)` takes an object with 25+ properties, some of which are nested objects (Address, Shipping).

A CLI needs to surface these as command-line flags. The naive approach of flattening every property to a `--flag` creates unusable commands with dozens of flags.

### Decision

Apply a **configurable flattening threshold**:

- **10 or fewer** scalar properties: flatten all to individual `--flag` parameters
- **More than 10** scalar properties: flatten the first 10, add `--json-input` flag for the full object
- Nested object properties (non-scalar) are always surfaced via `--json-input`, never flattened
- The threshold is configurable via `flattenThreshold` in `cli-builder.json`

### Rationale

- A flat flag list is the most agent-friendly interface — agents can discover parameters via `--help` and compose commands.
- But 25+ flags makes `--help` output unusable and parameter discovery impractical.
- The `--json-input` escape hatch preserves full expressiveness for complex objects while keeping common operations simple.
- 10 was chosen as a reasonable default — most CRUD operations have fewer than 10 parameters. Configurable for SDKs where this doesn't hold.

### Consequences

- **Positive:** Common operations (`create`, `update` with a few fields) have clean `--flag` interfaces. Complex operations are still possible via `--json-input`.
- **Negative:** The "first 10" heuristic is arbitrary — important properties might be sorted after the cutoff. Future improvement: sort by `Required` first, then alphabetical.
- **Negative:** `--json-input` is less discoverable than flags. Agents must know to check `--help` for the JSON schema.
- **Mitigated by:** Configurable threshold. Config overrides can rename, exclude, or reorder parameters per operation.

---

## ADR-008: Naming conventions with strict collision handling

**Date:** 2026-03-25
**Status:** Accepted

### Context

The adapter maps .NET PascalCase names to CLI-friendly names:

- Service class `PaymentIntentService` → resource noun
- Method `CreateAsync` → operation verb
- Parameter `customerId` → CLI flag

Collisions are possible: two classes producing the same noun, overloaded methods producing the same verb.

### Decision

- **PascalCase to kebab-case:** `PaymentIntentService` → `payment-intent`, `CreateAsync` → `create`
- **Suffix stripping:** configurable via `resourcePattern` and `operationPattern` (default: strip `Service`/`Client`/`Api` from nouns, strip `Async` from verbs)
- **Noun collisions are errors:** if two classes produce the same noun, the adapter emits an error and requires a `cli-builder.json` override. No silent last-wins.
- **Verb collisions (overloads):** prefer the overload with the richest parameter set (most parameters). Other overloads are discarded with a diagnostic. Config overrides can select a specific overload.

### Rationale

- **Kebab-case** is the standard convention for CLI commands (`git cherry-pick`, `docker-compose`, `stripe customer list`).
- **Hard errors on collisions** prevent silent correctness bugs. A generated CLI that calls the wrong method because of a naming collision is worse than a build failure.
- **Richest overload wins** is a pragmatic default — the overload with more parameters is usually the most capable. This can be wrong (e.g., a convenience overload with fewer params but better defaults), hence config overrides.

### Consequences

- **Positive:** Predictable, standard naming. Collisions are caught early.
- **Negative:** Users with colliding service names must write config overrides.
- **Mitigated by:** Collision errors include both conflicting class names and suggest the config override syntax.

---

## ADR-009: Test-driven development

**Date:** 2026-03-25
**Status:** Accepted

### Context

cli-builder has unusually well-defined boundaries: the adapter takes an assembly and produces `SdkMetadata`, the generator takes `SdkMetadata` and produces source files. Both have concrete, assertable inputs and outputs. The question is whether to write tests before implementation (TDD) or after (spike-first, then lock down).

### Decision

Use **test-driven development** as the primary workflow. Write tests first at each boundary, then implement until they pass.

### Rationale

- **The contracts are already specified.** `ISdkAdapter`, `ICliGenerator`, `SdkMetadata`, `TypeRef`, `AuthPattern` — all have defined shapes in the spec. We know what correct output looks like before writing any production code.
- **Partial failures are the norm.** The adapter will encounter missing dependencies, unresolvable types, ambiguous overloads. TDD forces us to define the failure behavior (diagnostics, skip-with-warning) before we write the happy path — preventing the "silent drop" anti-pattern the council flagged.
- **Generated code correctness is hard to verify by inspection.** A test that compiles the generated output and checks `--help` structure catches regressions that code review would miss.
- **The spec's "first spike" becomes a test fixture.** Instead of exploring OpenAI.dll and eyeballing the output, we write expected `SdkMetadata` assertions first, run the adapter, and diff. The committed JSON fixture becomes a golden-file test.

### Workflow

1. **Adapter tests first:** load a known assembly (or a purpose-built test assembly), assert on expected `SdkMetadata` — resource count, operation names, parameter types, auth patterns.
2. **Implement adapter** until tests pass.
3. **Generator tests first:** take hand-crafted `SdkMetadata` JSON fixtures, assert on generated file structure, compilability, `--help` output, exit codes.
4. **Implement generator** until tests pass.
5. **Integration tests last:** wire adapter + generator end-to-end against real SDKs (OpenAI, Stripe).

A purpose-built **test SDK assembly** (a small class library with known service classes, methods, overloads, generics, and edge cases) provides a stable, fast, deterministic test target — decoupled from external SDK version changes.

### Consequences

- **Positive:** Regressions caught immediately. Failure modes defined upfront. Golden-file tests lock down generator output stability. The test SDK assembly gives sub-second test runs.
- **Positive:** The first spike (spec step 5) produces a committed fixture with acceptance criteria, not an ad-hoc inspection.
- **Negative:** Slower initial velocity — writing tests for behavior that doesn't exist yet requires understanding the spec deeply.
- **Mitigated by:** The spec already defines the contracts in detail. The test SDK assembly is small and fast to build.

---

## ADR-010: Scriban for code generation templates

**Date:** 2026-03-25
**Status:** Accepted

### Context

The CLI generator needs to emit C# source files from `SdkMetadata`. For large SDKs like Stripe (340+ service classes), the generation technique must be maintainable and produce correctly formatted code. Four approaches were evaluated:

1. **Roslyn `SyntaxFactory`** — programmatic AST construction, guarantees valid syntax
2. **String interpolation / StringBuilder** — zero dependencies, what Roslyn's own source generator cookbook recommends
3. **Scriban** — text templating engine with scripting capabilities (40.2M NuGet downloads, 3.8k GitHub stars)
4. **Fluid** — Liquid-standard template engine used by NSwag (35.5M NuGet downloads, 1.7k GitHub stars)

### Decision

Use **Scriban** as the template engine for C# code generation.

### Rationale

**Why not Roslyn SyntaxFactory:**
SyntaxFactory is designed for compiler internals and source generators, not standalone code generation. It is extremely verbose — a simple method declaration takes 20-40 lines of nested factory calls. The official Roslyn issue #43979 documents community consensus that it is "far too complex for purely additive code generation." Even Microsoft's own source generator cookbook recommends emitting raw strings via `CSharpSyntaxTree.ParseText()` instead. No major standalone .NET code generator uses SyntaxFactory — NSwag uses Liquid templates, OpenAPI Generator uses Mustache, EF Core uses T4.

**Why not string interpolation:**
Viable for small generators but becomes unmaintainable at Stripe scale (potentially hundreds of generated files). Template files are readable and diffable — someone modifying the output format edits a template, not C# string manipulation code.

**Why Scriban over Fluid:**
Both are mature, single-maintainer projects with comparable download volumes. The deciding factors are features specific to code generation:

| Capability | Scriban | Fluid |
|-----------|---------|-------|
| Built-in `string.indent` | Yes | No (must write custom filter) |
| Local variable scope | Yes (`$` prefix) | No (global-only `assign`) |
| In-template functions | Yes (`func` keyword) | No |
| Arithmetic expressions | Native (`x = 1 + 2`) | Filter-pipe only (`x \| plus: 2`) |
| Open issues | 1 | 31 |

The `string.indent` filter is critical — we generate nested C# code (classes inside namespaces, methods inside classes, statements inside methods). Without it, every template must manually manage indentation.

Local variable scope prevents bugs in templates with nested loops over resources and operations. Fluid's global-only `assign` is a known footgun for complex templates.

**On single-maintainer risk:**
Scriban (Alexandre Mutel / xoofx) has bus factor 1, as does Fluid (Sebastien Ros / Microsoft). Fluid has stronger institutional gravity (maintainer is on the ASP.NET team, OrchardCore depends on it). However, Scriban templates are plain text files. If the library were abandoned, migration to Fluid's Liquid syntax is mechanical — Scriban even has a Liquid compatibility mode. The risk is contained.

### Consequences

- **Positive:** Templates look like the generated output with `{{ }}` placeholders — readable, diffable, maintainable. Built-in indentation and scoping. One sanitization pass at render time for generated code safety.
- **Positive:** Template files can be modified without recompiling cli-builder (loaded from embedded resources or disk).
- **Negative:** One additional dependency (`Scriban` NuGet package). Template errors are runtime, not compile-time.
- **Negative:** Single-maintainer project (bus factor 1).
- **Mitigated by:** Templates are text files — portable to any Liquid-compatible engine if needed. Template errors are caught by the test suite (TDD, golden-file tests).

---

## ADR-011: Cross-platform support — Windows, Linux, macOS

**Date:** 2026-03-25
**Status:** Accepted

### Context

cli-builder must run on Windows, Linux, and eventually macOS. This applies to both the tool itself and the CLIs it generates. The development environment is currently WSL2 (Linux on Windows), but the tool will be used on native Windows and Linux machines, with macOS support following.

### Decision

Both cli-builder and all generated CLI projects must be **cross-platform from day one**. No platform-specific code without an abstraction. CI tests run on Windows and Linux at minimum.

### Constraints

This decision affects every other architectural choice and must be treated as a hard requirement, not a nice-to-have:

**File system:**
- Use `Path.Combine()` and `Path.DirectorySeparatorChar` — never hardcode `/` or `\` in file paths
- Generated `.csproj` files and templates must use forward slashes (MSBuild normalizes them)
- Line endings: generate with `\n` (LF), not `\r\n`. Git's `core.autocrlf` handles platform conversion. Scriban templates must be configured to emit LF.
- File path comparisons must be case-insensitive on Windows, case-sensitive on Linux — use `StringComparison.OrdinalIgnoreCase` for path matching

**Dependency resolution:**
- NuGet global packages cache location differs: `~/.nuget/packages/` (Linux/macOS) vs `%USERPROFILE%\.nuget\packages\` (Windows). Use `NuGetPathUtility` or environment variable resolution, not hardcoded paths.
- .NET runtime reference assembly paths differ per platform. Use `RuntimeEnvironment.GetRuntimeDirectory()` or equivalent runtime APIs.

**Generated CLIs:**
- Must target `net8.0` (not `net8.0-windows` or platform-specific TFMs)
- No P/Invoke, no Windows Registry, no platform-specific APIs
- TTY detection via `Console.IsOutputRedirected` (cross-platform in .NET 8)
- Auth credential storage: environment variables and config files only — no Windows Credential Manager or macOS Keychain in v1

**Testing:**
- CI pipeline runs on both `ubuntu-latest` and `windows-latest`
- Golden-file tests must normalize line endings before comparison
- Path assertions must account for platform separator differences

**Distribution:**
- `dotnet publish` with Runtime Identifier (RID): `win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`
- Self-contained publish option for environments without .NET runtime installed
- No platform-specific build steps

### Consequences

- **Positive:** Single codebase, single set of templates, works everywhere .NET 8 runs. Users don't need to think about platform.
- **Negative:** Every file I/O operation, every path construction, and every test assertion must be platform-aware. This is ongoing discipline, not a one-time cost.
- **Negative:** Cannot use platform-native credential stores (Windows Credential Manager, macOS Keychain) — env vars and config files only for v1.
- **Mitigated by:** .NET 8 has excellent cross-platform support. `System.IO.Path` APIs handle most concerns. CI on both platforms catches regressions. Credential store abstraction can be added in a future version behind a platform adapter.

---

## ADR-012: System.CommandLine for generated CLIs

**Date:** 2026-03-25
**Status:** Accepted

### Context

Generated CLIs need a command-line parsing framework. The choice affects the generated code structure, the `--help` output format, and what the end user sees. This is for the *generated* CLIs, not cli-builder itself (though cli-builder will use the same framework for consistency).

Candidates evaluated:

| | System.CommandLine | Spectre.Console.Cli | CliFx | Cocona |
|---|---|---|---|---|
| **Stars** | 3,600 | 11,300 | 1,600 | 3,500 |
| **NuGet downloads** | 73.2M | 9.3M | 1.6M | 1.8M |
| **Latest version** | 2.0.5 (Mar 2026) | 0.53.1 (Nov 2024) | 2.3.6 (May 2025) | 2.2.0 (Mar 2023) |
| **Stable release?** | Yes (2.0.0 Nov 2025) | No (1.0 alpha in new repo) | Yes but frozen | Archived Dec 2025 |
| **License** | MIT | MIT | MIT | MIT |
| **Command model** | Builder/programmatic | Attribute-based | Attribute-based | Convention + attribute |
| **Help output** | Plain text, no ANSI | ANSI-styled (must opt out) | Plain text, no ANSI | Plain text |
| **Maintained?** | Active (Microsoft) | Active (splitting repos) | Maintenance mode | Dead |

**Cocona** was eliminated — archived December 2025, no successor.
**CliFx** was eliminated — maintenance mode, 18-month release gap, single-author ecosystem.

### Decision

Use **System.CommandLine** (stable 2.0.5) for both cli-builder itself and all generated CLIs.

### Rationale

The choice came down to System.CommandLine vs Spectre.Console.Cli. System.CommandLine wins for this project because we are *generating* CLI code, not hand-writing it, and our consumers are agents, not humans.

**Builder API + Scriban = clean templates.** System.CommandLine's programmatic builder API maps directly to template loops over `SdkMetadata`. Generating a command is:
```
var customerCmd = new Command("customer");
rootCommand.AddCommand(customerCmd);
```
With Spectre.Console.Cli, we'd need to generate entire class files with inheritance (`AsyncCommand<TSettings>`), settings classes with decorated properties (`[CommandOption]`), and `AddBranch()` registration — significantly more template complexity for the same result.

**Agent-friendly by default.** System.CommandLine emits plain text help with no ANSI escape codes. The spec's agent-readiness requirements (`--help` at every level, pipe-friendly, no color when stdout is not a TTY) are satisfied without fighting the framework's defaults. Spectre's signature feature is its beautiful ANSI-styled output — but for agent consumption, we'd be disabling it in every generated CLI.

**Now stable.** System.CommandLine reached 2.0.0 in November 2025 after the "Powderhouse" reset, which separated parsing from invocation, cut library size 32%, and added NativeAOT support. The multi-year preview concern — previously the strongest argument against it — is resolved. Spectre.Console.Cli, by contrast, has no stable standalone release (0.53.1 was the last pre-split version; the new repo has only alpha builds).

**Noun-verb model is native.** `RootCommand` → `Command` (noun) → `Command` (verb) with `Subcommands.Add()`. Direct mapping to the spec's `<tool> <resource> <action> [--params]` pattern. Spectre.Console.Cli achieves this via `AddBranch()`, which is underdocumented (issue #661) and has binding bugs (issue #1612).

**The `dotnet` CLI runs on it.** Powers `dotnet build`, `dotnet publish`, `dotnet tool`. Battle-tested at massive scale.

**MIT-licensed.** Compatible with EUPL-1.2 (see ADR license audit).

**Why Spectre.Console.Cli was a strong contender:** 11.3k GitHub stars (highest community traction), excellent developer ergonomics for hand-written CLIs, typed attribute-based settings with compile-time checking, and the richest help output in the .NET ecosystem. For a hand-written CLI project, it would likely be the better choice. But for *generated* code where the developer never sees it and agents consume the output, its strengths become irrelevant or counterproductive.

### Consequences

- **Positive:** Direct mapping from `SdkMetadata` → command tree. Agent-friendly plain text help. Battle-tested by `dotnet` CLI. Consistent framework between cli-builder and generated CLIs. NativeAOT and trim support.
- **Positive:** Builder API is template-friendly — `new Command()`, `.AddOption()`, `.SetHandler()` are straightforward to emit from Scriban templates. Simple templates generate correct commands.
- **Negative:** Help output is functional but plain compared to Spectre.Console.Cli.
- **Negative:** 453 open issues on the GitHub repo (though unclear how many are stale).
- **Mitigated by:** Plain help output is actually preferable for agent consumption — our primary use case. Generated CLIs pin a specific package version. Generator templates absorb any future API changes — users just regenerate.

---

## ADR-013: Package artifacts over raw source code — per-language native metadata

**Date:** 2026-03-26
**Status:** Accepted

### Context

cli-builder extracts SDK metadata to generate CLIs. For each supported language, the input could come from:

1. **Package artifacts** — the published, versioned distribution format (NuGet DLLs for .NET, wheels for Python, JARs for Kotlin)
2. **Raw source code** — parsing source files directly (Roslyn for C#, AST for Python, etc.)

Source analysis was considered for a sense of completeness — covering SDKs that aren't packaged, or extracting information not present in package metadata. However, each language has a native metadata format that provides type information without parsing source:

| Language | Package artifact | Metadata extraction | Executes code? |
|----------|-----------------|---------------------|----------------|
| .NET | NuGet DLL | `MetadataLoadContext` reflection | No |
| Python | Installed wheel + type stubs/annotations | Static type checker (pyright/mypy) or `.pyi` stubs | No |
| Kotlin/Java | JAR/AAR | JVM reflection or `kotlinx-metadata` | No |
| OpenAPI | Spec file (JSON/YAML) | Schema parsing | No |

### Decision

Each adapter operates on the language's **native package artifact and metadata format**, never raw source code. The principle is: **use the published contract, not the intermediate source.**

For v1 (.NET): compiled assemblies only (DLLs from NuGet packages or local `dotnet build` output).

For future adapters, the same principle applies per language:
- Python adapter would consume installed packages and extract types via static analysis (pyright, `.pyi` stubs, or inline type hints) — not by parsing `.py` source directly
- Kotlin adapter would consume JARs — not `.kt` source files
- OpenAPI adapter would consume the spec file — which is already a metadata format

### Rationale

**Package artifacts are versioned, testable, publishable contracts.** A NuGet package has a version number, a defined public API surface, and is the artifact users actually consume. Testing cli-builder against `Stripe.net 45.3.0` or `stripe-python 10.2.0` is deterministic and reproducible. Testing against "the current state of some source tree" is not — what version does it match? Which build configuration? Which conditional branches are active?

**Raw source analysis is a fundamentally harder problem in every language.** For .NET, it means Roslyn semantic analysis with full compilation context, handling partial classes, `#if` branches, and source generators. For Python, it means resolving imports across packages, handling dynamic typing, monkey-patching, and `__all__` exports. Each language's source-level complexity is unique and enormous. Package artifacts abstract all of this away — the type surface is already resolved.

**Modern SDKs increasingly ship rich type metadata in their packages.** Python's ecosystem has converged on inline type hints (PEP 484) and `.pyi` stubs. Stripe's Python SDK has full type annotations. OpenAI's does too. The trend is toward richer package-level metadata, making source analysis less necessary over time.

**No validated demand.** No user has asked for source analysis in any language. If someone does, that's validation the feature is needed.

**The workflow is always: install package → extract metadata → generate CLI.** Users don't want to point cli-builder at a Git repo. They want to point it at the SDK they're already using.

### Consequences

- **Positive:** Dramatically simpler adapter implementation per language. Deterministic, versioned inputs. Testable against pinned package versions.
- **Positive:** Consistent principle across all future adapters — each uses the language's native metadata, not a universal source parser.
- **Positive:** Does not block future adapters for interpreted languages (Python, Ruby) — those languages have package formats and type metadata that can be extracted without parsing source.
- **Negative:** Cannot analyze SDKs that are not packaged or published.
- **Negative:** Python adapters depend on the SDK having type annotations or stubs. Untyped Python SDKs would produce degraded metadata (parameters typed as `Any`).
- **Mitigated by:** For .NET, `dotnet build` always produces a DLL. For Python, `pip install` always produces an installed package. The untyped Python SDK case is a graceful degradation, not a failure — the adapter still extracts method names and parameter names, just without type information.

---

## ADR-014: Agent-assisted metadata enrichment with pluggable LLM provider

**Date:** 2026-03-26
**Status:** Accepted (design approved, implementation deferred past v1)

### Context

The mechanical pipeline (reflection → `SdkMetadata` → generated CLI) produces structurally correct but bare CLIs. Command descriptions come from XML doc comments (often terse: "Gets a customer"), parameter help text is just the parameter name, there are no usage examples, and auth setup instructions are absent.

Meanwhile, NuGet packages often ship with README files, getting-started guides, code examples, and API reference documentation that contains exactly the information needed to make a CLI's `--help` output genuinely useful.

An LLM agent can read this documentation and enrich the `SdkMetadata` with better descriptions, examples, parameter explanations, and naming suggestions — the kind of human-facing polish that code alone can't provide.

### Decision

Design an **optional agent enrichment pass** (`--enrich` flag) that sits between adapter and generator, transforming `SdkMetadata` into enriched `SdkMetadata`. The LLM provider is **pluggable** — no hardcoded dependency on any specific LLM vendor.

### Architecture

The enricher is a middleware on the `SdkMetadata` model:

```
ISdkAdapter ──▶ SdkMetadata (mechanical) ──▶ IMetadataEnricher ──▶ SdkMetadata (enriched) ──▶ ICliGenerator
```

Two new interfaces:

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

`ILlmProvider` implementations: Claude API, OpenAI API, Ollama (local), Azure OpenAI, or any future provider. Configured via `cli-builder.json` or environment variables.

### Rationale

**The mechanical and creative parts of CLI generation are distinct problems.** Reflection excels at extracting the type surface — what commands exist, what parameters they take, what types they return. It cannot tell you *how to explain them to a human*. An LLM excels at reading documentation and producing natural-language descriptions. Combining both produces CLIs that are both structurally correct and genuinely usable.

**Pluggable LLM provider avoids vendor lock-in.** The enricher depends on an abstraction (`ILlmProvider`), not a specific API. Users choose their provider based on their constraints — cloud API for quality, local Ollama for privacy, Azure OpenAI for enterprise compliance.

**The enrichment is additive, not foundational.** The pure-code pipeline must produce a fully functional CLI without any LLM. The agent only improves the human-facing text. This means:
- v1 ships without enrichment and is fully useful
- Enrichment can never break a working CLI — it only adds/improves descriptions
- Users without LLM access lose nothing

**`SdkMetadata` already supports it.** Every model has `Description` fields. The enricher fills them in. No schema changes needed.

### Enrichment rules

- **Never override explicit config.** If `cli-builder.json` sets a noun, verb, or description, the enricher does not touch it.
- **Only fill empty/terse fields.** If XML docs already have a good description, the enricher leaves it alone.
- **Cache enrichment results.** Enriched metadata is written to `.enrichment-cache.json` alongside the output. Regeneration reuses the cache — no LLM call unless the cache is invalidated (SDK version change, explicit `--enrich --no-cache`).
- **Cache is committable.** The cache file is designed to be committed to version control. Enrichment is a one-time cost per SDK version, not a per-build cost.
- **Sanitize all enriched text.** Agent-generated strings go through the same Generated Code Safety pipeline (identifier validation, string escaping) as mechanical metadata.

### What the enricher can improve

| Field | Without enrichment | With enrichment |
|-------|-------------------|-----------------|
| Resource description | XML doc or empty | Rich description from README/guides |
| Operation description | XML doc or empty | User-friendly explanation with context |
| Parameter description | Parameter name only | Meaningful help text with format hints |
| Auth setup | Detected from constructor heuristics | Step-by-step instructions with URLs |
| Usage examples | None | Realistic command examples |
| Resource grouping | Flat list | Categorized (e.g., "Payments", "Billing") |
| Naming quality | Mechanical kebab-case | Agent-suggested noun-verb improvements |

### Consequences

- **Positive:** Generated CLIs with `--enrich` have dramatically better `--help` output — more discoverable by both agents and humans. No other CLI generator in the landscape offers this.
- **Positive:** Pluggable provider means no vendor lock-in. Works with any LLM that can complete a prompt.
- **Positive:** Cacheable and committable — enrichment cost is amortized.
- **Positive:** Architecture slots cleanly into existing pipeline. `SdkMetadata` is the contract; the enricher is just another transform.
- **Negative:** Non-deterministic output. Two enrichment runs may produce different text. Mitigated by caching.
- **Negative:** Token cost for large SDKs (Stripe with 340+ services). Mitigated by caching and incremental enrichment (only enrich new/changed resources).
- **Negative:** Adds a dependency on an external service (LLM API). Mitigated by being strictly optional — `--enrich` is opt-in, the pure-code pipeline always works.

---

## ADR-015: Diagnostics collection pattern for error handling

**Date:** 2026-03-26
**Status:** Accepted

### Context

cli-builder's core operations — extracting metadata from assemblies and generating CLI projects — are rarely binary success/failure. A typical run against a large SDK like Stripe.net might:

- Successfully extract 48 of 50 service classes
- Skip 2 services due to unresolvable transitive dependencies
- Rename 3 parameters that contained invalid C# identifier characters
- Discard 5 method overloads in favor of richer alternatives
- Generate a fully functional CLI with minor gaps

This is not a failure. It's not an unqualified success either. It's a **result with diagnostics**.

Three patterns were considered:

1. **Exceptions** — standard .NET, but partial failures are expected, not exceptional. Forces try/catch around every operation. Callers can't see failure modes in signatures.
2. **Result\<T\>** — explicit success/failure in return types, but forces binary thinking. "48 out of 50 services extracted" doesn't fit success or failure.
3. **Diagnostics collection** — always return a result (possibly degraded) plus a list of diagnostics with severity, code, and message.

### Decision

Use the **diagnostics collection pattern**. Both the adapter and generator always return a result paired with `List<Diagnostic>`. Exceptions are reserved for environment failures only.

### Design

```csharp
// Adapter always returns metadata + diagnostics
public record AdapterResult(
    SdkMetadata Metadata,
    List<Diagnostic> Diagnostics
);

// Generator already returns this (from interface signatures in spec)
public record GeneratorResult(
    string ProjectDirectory,
    List<string> GeneratedFiles,
    List<Diagnostic> Diagnostics
);

// Shared diagnostic type
public record Diagnostic(
    DiagnosticSeverity Severity,  // Info, Warning, Error
    string Code,                   // e.g., "CB001"
    string Message
);

public enum DiagnosticSeverity { Info, Warning, Error }
```

**The boundary rule:** If it's about the SDK being analyzed, it's a diagnostic. If it's about the environment cli-builder is running in, it's an exception.

| Situation | Handling | Example |
|-----------|----------|---------|
| Missing transitive dependency | Warning diagnostic | "CB001: Could not resolve Newtonsoft.Json. 3 types skipped." |
| Overloaded method disambiguated | Info diagnostic | "CB002: CreateAsync has 2 overloads. Using 5-parameter variant." |
| Naming collision | Error diagnostic | "CB003: CustomerService and CustomerApiService both map to 'customer'. Add override." |
| Identifier sanitized | Warning diagnostic | "CB004: Parameter name 'class' is a C# keyword. Renamed to 'class_'." |
| Assembly file not found | Exception | `FileNotFoundException` |
| Corrupted assembly | Exception | `BadImageFormatException` |
| Out of memory | Exception | `OutOfMemoryException` |
| Disk full during generation | Exception | `IOException` |

### Diagnostic codes

All diagnostic codes use the `CB` prefix (cli-builder) followed by a three-digit number:

- `CB0xx` — Adapter: dependency resolution
- `CB1xx` — Adapter: type extraction and discovery
- `CB2xx` — Adapter: naming conventions and collisions
- `CB3xx` — Generator: code emission
- `CB4xx` — Generator: output validation
- `CB5xx` — Enricher (future): LLM interaction

### CLI exit code mapping

When cli-builder runs as a CLI tool, diagnostics map to exit codes:

- All diagnostics are Info or Warning → exit 0 (success with warnings printed to stderr)
- Any diagnostic is Error → exit 1 (partial failure, output may be incomplete)
- Exception thrown → exit 2 (environment failure, no output)

### Rationale

**Partial success is the norm, not the edge case.** Real SDKs have unresolvable dependencies, ambiguous overloads, and edge-case types. The tool must handle these gracefully and continue, not abort on the first problem.

**Same pattern as Roslyn.** The C# compiler always produces a compilation result — even if it has errors. Diagnostics are collected, categorized by severity, and reported. Developers understand this model.

**Diagnostics are observable.** Every decision the adapter and generator make — every skipped type, every renamed parameter, every discarded overload — is visible in the diagnostic output. No silent behavior. This directly addresses the council's finding that "the adapter must never silently drop types."

**Composable with the enricher.** The future `IMetadataEnricher` returns `SdkMetadata` + diagnostics too. The diagnostics from all three stages (adapter, enricher, generator) are collected and reported together.

### Consequences

- **Positive:** Partial success is a first-class outcome. Users always get the best result possible, plus a clear report of what was degraded and why.
- **Positive:** Every silent decision is eliminated. Diagnostic codes make issues searchable and documentable.
- **Positive:** Maps cleanly to CLI exit codes. Composable across pipeline stages.
- **Positive:** Same mental model as Roslyn — familiar to .NET developers.
- **Negative:** Callers must check diagnostics after every operation — easy to ignore.
- **Mitigated by:** The CLI wrapper checks diagnostics and prints warnings/errors to stderr automatically. The exit code reflects the worst diagnostic severity. Tests assert on expected diagnostics, not just on the returned metadata.
