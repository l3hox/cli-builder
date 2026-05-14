# Changelog

All notable changes to cli-builder.

## v0.2.2 — 2026-05-14

### Features

- **Single-client SDK shape discovery** in the Python adapter ([ADR-023](docs/ADR.md#adr-023-single-client-sdk-shape-discovery-via-verb-noun-method-grouping)). When the multi-service path finds zero `*Service`/`*Client`/`*Api`-suffixed classes, the adapter now falls back to picking one entry class (heuristic + method-count threshold) and walks its verb-noun methods into CLI operations. Activated automatically for SDKs like PyGithub, Notion, Linear, Slack, Anthropic. Explicit override via the new `--entry-class <ClassName>` flag on `cli-builder inspect` / `cli-builder generate`. Pre-v0.2.2, PyGithub generated a CLI with zero resources; v0.2.2 generates **33 resources** with operations wired to real SDK calls.
- **Naming policy isolated in `python/src/cli_builder_adapter/_naming.py`** — owns `VERB_WHITELIST` (8 verbs), `DESCRIPTIVE_NOUN_PREFIXES`, `MIN_ENTRY_CLASS_METHODS` (10), `parse_verb_noun()`, `skip_reason()`. Future sub-resource walkers import from this module without coupling to extractor.
- **`SdkMetadata.discovery_mode` field** — string Literal[`"multi_service"`, `"single_client"`], default `"multi_service"`. Provides stable provenance to downstream consumers without requiring re-derivation from diagnostic codes. Round-trip compat for v0.2.0/v0.2.1 emissions via Serde `default`.
- **`SdkMetadata.pypi_name` field** — resolves the PyPI distribution name when it differs from the Python import name. PyGithub installs as `PyGithub` but imports as `github`; pre-fix, the generated pyproject's dependency was `github` (a different unrelated package). Resolved via `importlib.metadata.packages_distributions()`. Applies generally to SDKs with name divergence (Pillow/PIL, beautifulsoup4/bs4, psycopg2-binary/psycopg2).
- **Generator-side sub-resource note** — when `discovery_mode == "single_client"` and any operation has a non-primitive return type, the generated `cli.py` header includes a documentation note explaining that returned objects' own methods aren't surfaced as nested commands (sub-resource discovery deferred to a future step).
- **New diagnostic codes (CB6xx Python adapter range)**:
  - `CB609` WARNING — single-client entry-class resolution failed (zero/multiple candidates or invalid `--entry-class`).
  - `CB610` WARNING — method skipped from single-client extraction. Reason string names which filter rule fired.
  - `CB611` INFO — single-client discovery mode auto-engaged.

### Bug fixes

- **Auth detector** missed parameter names ending in common auth suffixes. Previously only exact matches against `{api_key, apikey, secret_key, secret, api_secret, token}` were recognized. Now also matches `*_token`, `*_key`, `*_secret` suffixes — catches PyGithub's `login_or_token`, OAuth-style `access_token`/`bearer_token`, plus future variants. Test in `test_auth_detector.py::test_pygithub_auth_detector_finds_login_or_token`.
- **`_extract_single_client_resources` attached constructor params to only the first sorted resource**. All resources in single-client mode share the same entry-class ctor; the generator's `can_construct` gate fired `false` on all but the first resource, generating `"client construction not available"` stubs instead of real SDK calls. Now attached uniformly. Caught by PR 2 PyGithub validation; new regression gate in the manual-test script greps the generated `user.py` to detect the stub string.
- **Rust `SdkMetadata` struct** was missing `discovery_mode` (PR 1 added it on the Python adapter + JSON schema but not on the Rust consumer side, where Serde silently dropped the field). Now mirrored on both sides with `#[serde(default)]` for round-trip compat.

### Tooling

- **`scripts/manual-test-python-sdk.sh`** gained `PYTHON_MODULE` and `ENTRY_CLASS` env vars to handle SDKs where PyPI install name ≠ Python import name (PyGithub case) and where auto-detection is ambiguous. Plus a new Phase 7c "GitHub regression gate" that proves PyGithub operations are wired to real SDK calls (not stubs) by grepping the generated `user.py`.

### Dependencies

- No new runtime dependencies. `typing_extensions >= 4.6` from v0.2.1 is unchanged.

### Stats

- Python adapter: 119 → 133 tests (+14: 13 new single-client extraction tests in PR 1 + 1 auth detector test in PR 2)
- Total: 707 tests across all three languages (397 .NET + 177 Rust + 133 Python)
- Stripe regression check: 9/9 phases pass (Step 17 functionality preserved unchanged)
- PyGithub end-to-end (with `GITHUB_TOKEN`): 10/10 phases pass including a live `api.github.com/users/octocat` call

### Known limitation (deferred to a future step)

- **`Opt[T]` sentinel-Union type aliases** (e.g., PyGithub's `Union[T, _NotSetType]`) aren't recognized as Optional by the adapter's type mapper. Result: PyGithub operations work end-to-end (auth + SDK call + result) but per-parameter flags (like `--login`) aren't emitted — parameters route through `--json-input`. See README "Known Limitations" for the documented workaround.

## v0.2.1 — 2026-05-13

### Features

- **PEP 692 `Unpack[TypedDict]` resolution in the Python adapter** ([ADR-022](docs/ADR.md#adr-022-pep-692-unpacktypeddict-resolution-via-ast-walk-of-type_checking-imports)). Methods with `def f(**params: Unpack[X])` now extract one structured `Parameter` per TypedDict field. Strategy: AST-walk the defining module's `if TYPE_CHECKING:` blocks to discover where X is imported from, then `importlib.import_module` + read `__required_keys__` / `__optional_keys__` / `__annotations__`. Per-field ForwardRefs (e.g. Stripe's `NotRequired[ForwardRef('str | None')]`) resolve against the TypedDict's defining module — `str | None` flattens to nullable `str`, not `TypeKind.Other`. Nested TypedDicts emit as `TypeKind.Other` + `CB608` and route through `--json-input` (mirrors C# ADR-007 flattening policy).
- **Diagnostic codes `CB606` / `CB607` / `CB608`** in the `CB6xx` Python-adapter namespace, documented in `docs/design-notes.md`. `CB606` info when Unpack resolution succeeded, `CB607` warning when an Unpack `ForwardRef` could not be resolved, `CB608` warning for per-field resolution failures (recursive types, missing imports, malformed `ForwardRef`s).

### Bug fixes

- Pre-v0.2.1, every Python SDK using PEP 692 (Stripe, increasingly OpenAI, many modern libraries) rendered zero CLI flags on every CRUD method — 313 of 922 Stripe operations were functionally empty. The Python adapter at `extractor.py:324-329` was unconditionally skipping `**kwargs`. Now structured.

### Dependencies

- `typing_extensions >= 4.6` is a hard runtime dependency of the Python adapter (was optional). Python 3.10 requires it for `Unpack` / `Required` / `NotRequired`; declaring as hard prevents clean-install regressions on the CI matrix.

### Stats

- Python adapter: 109 → 119 tests (+10 in `tests/test_extractor_unpack.py`)
- Total: 693 tests across all three languages (397 .NET + 177 Rust + 119 Python)
- Stripe 15.x end-to-end:
  - Pre-v0.2.1: `stripe-cli customer list --help` → 0 flags + `--json-input`
  - v0.2.1: 11 typed flags (`--limit`, `--email`, `--starting_after`, `--ending_before`, `--stripe_version`, …) + `--json-input` for nested
  - `customer create --help`: 17 typed scalar flags + `--json-input` for nested address/metadata/payment_method_data

## v0.2.0 — 2026-04-22

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
- 21 ADRs (ADR-016 through ADR-021 added in v0.2.0)
- 2 step plans added: `docs/internal/step-13b-python-generator-finalize.md`, `docs/internal/step-16-ci-cd.md`

## v0.1.1 — 2026-04-04

### Features
- **`--json-input` deserialization** — JSON deserialized into options classes, flat flags override on top. Nested SDK objects (Stripe `Recurring`, `ProductData`, `ShippingAddress`) now populatable.
- **Noun collision resolution** — namespace-qualified disambiguation instead of dropping colliding resources. `Stripe.Tax.CustomerService` → `tax-customer`. Stripe: 136 → 196 resources.
- **Null guard system** — value-type CLI options made nullable for `--json-input` operations to prevent System.CommandLine defaults clobbering JSON values.

### Stats
- 347 tests (52 Core + 252 Generator + 43 Integration)
- 93.4% line coverage, 96.4% method coverage
- Stripe: 196 resources (was 136)

## v0.1.0 — 2026-04-04

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
