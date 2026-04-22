# Changelog

All notable changes to cli-builder.

## v2.0.0 — 2026-04-22

Multi-language rewrite. The orchestrator moves to Rust, a Python adapter and Python CLI generator ship, the C# generator is re-implemented in Rust with Tera templates. Single binary distribution is now possible.

### Breaking

- **Orchestrator binary is now Rust, not .NET.** `cli-builder generate` / `cli-builder inspect` are invoked from `crates/cli` instead of the previous .NET tool. The adapter subprocess contract is unchanged — .NET adapter still invokable standalone.
- **Generated Python CLIs render optional bool parameters as `type=click.BOOL, default=None`** (not `is_flag=True, default=False`). This fixes a silent kwargs-overwrite bug where omitted flags clobbered SDK defaults. Any downstream code that relied on the old behavior must be re-generated.
- **Repo layout**: `crates/` + `dotnet/` + `python/` at the root (ADR-018). Previous `src/` + `cli-builder-rust/` + `cli-builder-adapter-python/` are gone. Affects anyone with bookmarks into the old paths.

### Features

- **Python adapter** (`python/`) — `inspect` + `typing.get_type_hints()` + `.pyi` stub fallback. 109 pytest tests. Stripe 15.x validated (105 resources extracted). ADR-013 compliance (package artifacts, not raw source).
- **Python CLI generator** (`crates/gen-python`) — click-based, Tera templates, shared Rust core. Sanitization via `py_str` filter (escapes `\` and `"` in descriptions). Golden-file regression via `insta` snapshots. 36 tests including a PYTHONPATH-based runtime anchor that spawns `python -m <cli> --help` against the generated output.
- **C# generator in Rust** (`crates/gen-csharp`) — `CSharpProfile` + 6 Tera templates + 6 post-processing transforms (`ComputeConversion`, `SanitizeDefaultValue`, `MakeValueTypesNullable`, `BuildConstructorExpression`, etc.). Compile-validated on OpenAI (20 resources) and Stripe (196 resources). Replaces the v1 .NET/Scriban generator.
- **Rust orchestrator** (`crates/cli`) — single `cli-builder` binary. Calls adapters as subprocesses, generators as embedded libraries. Diagnostic colorization, exit-code contract preserved from v1.
- **Shared Rust core** (`crates/core`) — `ModelMapper`, `ParameterFlattener`, `IdentifierValidator` via `LanguageProfile` trait. ~1,500 LOC, 64 tests. Adding a new target language is ~500 lines of Tera templates.
- **Cross-platform CI/CD** — 15-job matrix (3 OS × Rust + .NET + Python 3.10/3.11/3.12), `fail-fast: false`, `concurrency` groups, ~3 min wall time. Dependabot on four ecosystems (ADR-021).
- **Test path centralization** — `dotnet/Directory.Build.props` injects `$(RepoRoot)` via `AssemblyMetadata`; `crates/core/src/test_support.rs` exposes `workspace_root()` with a `.git` sentinel check. Eliminated 9 duplicated 6-level `../..` traversals in .NET + 3 in Rust. ADR-019.
- **Mock adapter crate** (`crates/mock-adapter`) — Rust binary replacing 5 shell-script test fixtures. `MOCK_ADAPTER_MODE` env var selects `ok`/`degraded`/`fail`/`bad-json`/`empty` behavior. Cross-platform.

### Bug fixes

- **Optional-bool kwargs overwrite** in the Python generator. Previously `is_flag=True, default=False` rendering meant click delivered `False` for omitted flags; the `is not None` guard then passed `False` into the SDK kwargs, clobbering SDK-side defaults. Fix: tri-state `type=click.BOOL, default=None`. Required bools still use `is_flag=True, default=False, required=True`. Class-level scan test gates against regression.
- **Em dash in `cli_description`** broke `--help` on default Windows consoles (cp1252 mojibake). Replaced with ASCII hyphen in `crates/core/src/model_mapper.rs`.
- **Description containing `"` or `\`** would produce unterminated Python string literals in the generated `help="..."` clause. `py_str` Tera filter escapes both.
- **xUnit `Console.SetOut` parallel-test race** in the .NET integration tests. Fix: `[Collection("StdoutCapture")]` + static lock.
- **macOS CI Debug/Release file-lock race** in `GeneratedCliFixture` — both test hosts built the TestSdk in Debug while CI runs Release, racing on `deps.json`. Fix: fixture reads Configuration from assembly path and propagates through all `dotnet build`/`run` calls.
- **Windows Python `ast.parse` path unicode escape**: `C:\Users\...` contains `\U` / `\t` interpreted as escape sequences when passed inside a Python string literal. Fix: pass path via `sys.argv[1]`, not string interpolation.

### Stats

- 683 tests total (397 .NET + 177 Rust + 109 Python), 0 failures
- 15-job CI matrix green on every push
- 21 ADRs (ADR-016 through ADR-021 added in v2.0)
- 2 step plans added: `docs/internal/step-13b-python-generator-finalize.md`, `docs/internal/step-16-ci-cd.md`

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
