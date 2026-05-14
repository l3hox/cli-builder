# Design Notes

Behavioral rules and edge-case policies that refine the spec. These bridge the gap between the high-level spec (interfaces, models, requirements) and the implementation plans in `docs/internal/`.

Sourced from developer council review (2026-03-26).

---

## Return type unwrapping rules

The adapter unwraps async and wrapper types to expose the "real" return type to the generator. Unwrapping is applied in order until no more rules match:

1. **`Task<T>`** → unwrap to `T`
2. **`ValueTask<T>`** → unwrap to `T`
3. **`IAsyncEnumerable<T>`** → unwrap to `T` (mark operation as streaming)
4. **`AsyncCollectionResult<T>`** → unwrap to `T` (mark operation as streaming — OpenAI SDK pattern for streaming responses)
5. **`ClientResult<T>`** → unwrap to `T` (SDK-specific wrapper)
6. **`CollectionResult<T>`** → unwrap to `T` (sync paginated results — OpenAI SDK pattern)

Rules 4-6 handle the OpenAI .NET SDK where methods return `Task<ClientResult<ChatCompletion>>` (rules 1 + 5 → `ChatCompletion`) and `AsyncCollectionResult<StreamingChatCompletionUpdate>` (rule 4 → `StreamingChatCompletionUpdate`, marked streaming).

If a wrapper type is not in this list, it is not unwrapped — it appears as `TypeRef(Generic, "WrapperName", [T])` in the metadata.

**Dictionary special case:** `Dictionary<TKey, TValue>` is not unwrapped. It maps to `TypeRef(Dictionary, "Dictionary")`.

---

## Auth generation contract

The spec names `Auth/AuthHandler.cs` in the generated output but does not define its behavior. This section specifies what the generator must produce.

**Generated `AuthHandler.cs` responsibilities:**

1. **Credential resolution** with strict precedence:
   - Environment variable (from `AuthPattern.EnvVar`) — checked first
   - Config file at `<AppData>/<cli-name>/config.json` — checked second
   - `--api-key` flag — last resort only
2. **Emit a stderr warning** when `--api-key` flag is used: "Warning: passing credentials via command-line flags exposes them to process listings and shell history. Prefer environment variables."
3. **Token caching** — write to `<AppData>/<cli-name>/config.json` (cross-platform via `Environment.GetFolderPath(SpecialFolder.ApplicationData)`)
4. **Credential masking** — never include credential values in error messages, `--json` error output, or diagnostics. Mask as `***`.
5. **Auth config override precedence** — if `cli-builder.json` specifies an `auth` block, it overrides detected `AuthPattern`. If both exist, config wins completely (detection is suppressed for auth).

**Generated auth interface:**
```
AuthHandler
├── ResolveCredential() → string?     # returns credential or null
├── Source: enum                       # EnvVar, ConfigFile, Flag, None
└── Warn() → void                     # emits warning if Source = Flag
```

---

## Identifier validation — complete rules

The spec's regex `[a-zA-Z_][a-zA-Z0-9_]*` is necessary but insufficient. Full validation is:

1. **Regex check:** must match `[a-zA-Z_][a-zA-Z0-9_]*`
2. **C# keyword denylist:** reject all C# reserved keywords (`abstract`, `as`, `base`, `bool`, `break`, `byte`, `case`, `catch`, `char`, `checked`, `class`, `const`, `continue`, `decimal`, `default`, `delegate`, `do`, `double`, `else`, `enum`, `event`, `explicit`, `extern`, `false`, `finally`, `fixed`, `float`, `for`, `foreach`, `goto`, `if`, `implicit`, `in`, `int`, `interface`, `internal`, `is`, `lock`, `long`, `namespace`, `new`, `null`, `object`, `operator`, `out`, `override`, `params`, `private`, `protected`, `public`, `readonly`, `ref`, `return`, `sbyte`, `sealed`, `short`, `sizeof`, `stackalloc`, `static`, `string`, `struct`, `switch`, `this`, `throw`, `true`, `try`, `typeof`, `uint`, `ulong`, `unchecked`, `unsafe`, `ushort`, `using`, `virtual`, `void`, `volatile`, `while`)
3. **Contextual keywords** to also check: `var`, `dynamic`, `async`, `await`, `value`, `get`, `set`, `add`, `remove`, `global`, `partial`, `where`, `when`, `yield`, `nameof`
4. **Rename strategy:** prefix with `@` (C# verbatim identifier) for generated parameter names. For CLI flag names, append `_value` (e.g., `class` → `--class-value`). Emit diagnostic `CB004`.
5. **Collision with generated boilerplate names:** parameter names that match generated class names (`JsonFormatter`, `TableFormatter`, `AuthHandler`, `Program`) must also be renamed. Emit diagnostic `CB004`.

---

## Exit code contracts

Two separate binaries, two separate contracts.

**cli-builder tool exit codes:**

| Code | Meaning | Trigger |
|------|---------|---------|
| 0 | Success (possibly with warnings) | All diagnostics are Info or Warning |
| 1 | Partial failure | Any diagnostic has Error severity |
| 2 | Environment failure | Exception thrown (file not found, corrupted assembly, etc.) |

**Generated CLI exit codes:**

| Code | Meaning | Trigger |
|------|---------|---------|
| 0 | Success | Command executed successfully |
| 1 | User error | Missing required parameter, invalid argument |
| 2 | Auth error | No credential found, credential rejected by SDK |
| 3+ | App-specific | SDK-specific errors (e.g., resource not found, rate limited) |

These are independent contracts. Tests must declare which binary they validate.

---

## Verb collision — non-overload same-name methods

The spec defines overload collision behavior (richest parameter set wins) but not the case where distinct methods produce the same verb after stripping.

**Rule:** If two methods on the same service class produce the same kebab-case verb after `Async` suffix stripping (e.g., `Get` and `GetAsync` → both `get`), and they are **not** overloads of each other (different method names), the adapter treats this as a **collision error** — same behavior as noun collisions. Requires a config override to disambiguate.

**Diagnostic:** `CB201` — "Methods '{method1}' and '{method2}' on {class} both map to verb '{verb}'. Add an override in cli-builder.json."

---

## Flattening ordering rule

The spec says "flatten the first 10 scalar properties" but doesn't define ordering.

**Rule for v1:** Sort properties by:
1. **Required first** (Required=true before Required=false)
2. **Alphabetical** within each group

If a required property falls outside the flattened set (more than 10 required scalar properties), emit diagnostic `CB301` — "Required parameter '{name}' is only accessible via --json-input due to flatten threshold."

---

## Direct param JSON deserialization (step 9B)

When an operation has complex direct parameters (Generic, Array, Dictionary, bare Class that isn't binary or infrastructure), `--json-input` accepts a JSON object where **parameter names are keys**:

```bash
# IEnumerable<ChatMessage> direct param + ChatCompletionOptions options class
my-cli chat complete-chat \
  --json-input '{"messages":[{"role":"user","content":"hello"}],"temperature":0.7}'
```

**Type mapping** (`BuildDeserializationTypeName`):
- `IEnumerable<T>`, `IReadOnlyList<T>`, `IList<T>` → `List<T>` (concrete for instantiation)
- `IDictionary<K,V>` → `Dictionary<K,V>`
- `T[]` → `T[]` (already concrete)
- Bare Class → class name directly

**Template behavior**: JSON parsed once via `using var _jsonInputDoc = JsonDocument.Parse(jsonInputValue)`. Each direct param extracted by name: `_jsonInputDoc.RootElement.TryGetProperty("paramName", ...)`. Per-param deserialization wrapped in try/catch for `json_input_error`. Required params get null guard → `missing_required_param` error with exit code 1.

**Coexistence with options class**: direct param keys extracted by name, options class deserializes from the full JSON (ignoring unknown keys via `PropertyNameCaseInsensitive`). Flat flags still override options class properties. Direct params have no flat flags — they're JSON-only.

**ParameterFlattener**: complex direct params are skipped entirely (no CLI `--flag` generated). They only appear via `--json-input`.

---

## `operationPattern` semantics

The spec says `operationPattern` is a "glob pattern" that also strips a suffix. This is ambiguous.

**Rule:** `operationPattern` is a **suffix match and strip**, not a full glob. The default `*Async` means:
- If the method name ends with `Async`, strip the suffix and use the remainder as the verb
- If the method name does **not** end with `Async`, use the full method name as the verb
- The `*` is not a glob wildcard — it means "any prefix"

Multiple patterns can be comma-separated (e.g., `*Async,*Task`). First match wins.

---

## `--json-input` behavior

**Schema exposure:** When a command has a `--json-input` flag, the `--help` output must include the JSON schema (property names, types, required markers) for the input object. Format: a condensed property list, not a full JSON Schema document.

**Precedence (implemented step 9):** When both flat flags and `--json-input` are provided:
1. Construct empty options class instance
2. If `--json-input` provided: `JsonSerializer.Deserialize<T>(jsonInputValue, _jsonInputOptions)` replaces the instance
3. Flat flags override individual properties on top: `if (xValue is not null) opts.X = xValue`
4. This allows: `--json-input '{"email":"a@b.com","name":"Test"}' --name "Override"` where `--name` wins

**Null guard rule:** For operations with `NeedsJsonInput`, all value-type CLI options are made nullable (`bool` → `bool?`, `int` → `int?`) to distinguish "user didn't provide" (`null`) from "user set the default" (`false`/`0`). Every flat flag assignment is guarded with `if (xValue is not null)`. Operations without `--json-input` keep unconditional assignment.

**Static JsonSerializerOptions:** `_jsonInputOptions` is a `static readonly` field with `PropertyNameCaseInsensitive = true` (handles PascalCase SDK vs camelCase user JSON). Not inline per-call.

---

## Noun collision resolution (step 9)

When multiple service classes map to the same noun (e.g., `Stripe.CustomerService` and `Stripe.TestHelpers.CustomerService` both → `customer`), the adapter disambiguates by namespace prefix:

1. Find the common root namespace across all types (e.g., `Stripe`)
2. For each colliding type, compute the relative namespace (e.g., `TestHelpers` from `Stripe.TestHelpers`)
3. Prefix the noun: `test-helpers-customer`
4. Types in the root namespace keep the original noun: `customer`
5. If types are in the SAME namespace (can't disambiguate by namespace), fall back to full class name kebab-cased: `ShippingService` → `shipping-service`

CB202 diagnostic changed from Error (drop both) to Info (resolved with qualified name).

---

## Diagnostic code assignments

Expanding the ranges from ADR-015 with specific codes:

**CB0xx — Dependency resolution (adapter):**
- `CB001` — Missing transitive dependency (assembly not found)
- `CB002` — Dependency resolved from fallback location (NuGet cache vs sibling)

**CB1xx — Type extraction (adapter):**
- `CB101` — Type skipped due to unresolvable dependency
- `CB102` — Generic type argument partially resolved (fell back to `object`)
- `CB103` — Extension method class skipped (not matching service pattern)

**CB2xx — Naming (adapter):**
- `CB201` — Verb collision (non-overload same-name methods)
- `CB202` — Noun collision (two classes → same resource name)
- `CB203` — Overload disambiguated (richest parameter set selected)
- `CB204` — Identifier sanitized (regex failure, non-matching chars replaced)

**CB3xx — Code emission (generator):**
- `CB301` — Required parameter hidden behind `--json-input` (flatten threshold)
- `CB302` — Scriban template rendering warning
- `CB303` — Generated file path exceeds platform limit (Windows 260 char)
- `CB306` — Operation has unconvertible parameter (binary type like BinaryContent/Stream, or infrastructure type like RequestOptions) — falls back to echo stub
- `CB307` — Abstract type in generic argument (e.g., `IEnumerable<ChatMessage>` where ChatMessage is abstract) — deserialization requires SDK-registered JsonConverters. Info-level, not a blocker.

**CB4xx — Output validation (generator):**
- `CB401` — Generated project failed `dotnet build` verification
- `CB402` — Generated `--help` output missing expected sections

**CB5xx — Enrichment (future):**
- `CB501` — LLM provider unreachable
- `CB502` — Enrichment cache miss (re-enriching)
- `CB503` — Enriched text failed sanitization

**CB6xx — Python adapter (extraction + PEP 692 resolution + single-client discovery):**
- `CB600` — Error: cannot import package
- `CB601` — Info: package imported at runtime — side effects may occur
- `CB602` — Warning: cannot inspect signature, skipping method
- `CB603` — Info: could not resolve type hints — using signatures only
- `CB604` — Warning: malformed `.pyi` stub file
- `CB605` — Info: using `.pyi` stubs (no runtime import needed)
- `CB606` — Info: `Unpack[TypedDict]` successfully resolved via `TYPE_CHECKING` AST walk (ADR-022)
- `CB607` — Warning: `Unpack[ForwardRef(X)]` could not be resolved; param dropped (ADR-022)
- `CB608` — Warning: TypedDict field's ForwardRef could not be resolved; emitted as `TypeKind.Other` (route via `--json-input`)
- `CB609` — Warning: single-client entry-class resolution failed (auto-detect found zero or multiple candidates; explicit `--entry-class` named a missing or under-threshold class) (ADR-023)
- `CB610` — Warning: method skipped from single-client extraction. Reason string names which filter rule fired (no underscore / verb not in whitelist / descriptive noun prefix / `type[T]` first param) (ADR-023)
- `CB611` — Info: single-client discovery mode auto-engaged; observation, not discard signal (ADR-023)

---

## Test SDK assembly manifest

The purpose-built test SDK assembly must contain:

**Service classes (resource discovery):**
- `CustomerService` — standard service, matches `*Service` pattern
- `OrderClient` — matches `*Client` pattern
- `ProductApi` — matches `*Api` pattern
- `MessageClient` — matches `*Client`, has `IEnumerable<Message>` (abstract) and `IEnumerable<string>` direct params (step 9B)
- `InternalHelper` — should NOT be discovered (no matching suffix)
- `CustomerApiService` — collides with `CustomerService` on noun `customer`

**Methods (operation discovery):**
- `CreateAsync(CreateOptions)` — standard async, options class
- `GetAsync(string id)` — primitive parameter
- `ListAsync(int limit, string? cursor)` — multiple params, one nullable
- `Get(string id)` — non-async, collides with `GetAsync` after stripping
- `CreateAsync(CreateOptions, RequestOptions)` — overload, fewer useful params
- `DeleteAsync(string id)` — for behavioral correctness testing

**Type edge cases:**
- `Task<Customer>` return type — async unwrapping
- `ValueTask<bool>` return type — ValueTask unwrapping
- `IAsyncEnumerable<Order>` return type — streaming marker
- `List<Customer>` return type — generic
- `Dictionary<string, object>` return type — dictionary kind
- `CustomerStatus` enum parameter — enum values extraction
- `string?` nullable parameter — nullability annotation
- `IEnumerable<Message>` direct param — abstract generic argument, CB307 diagnostic (step 9B)
- `IEnumerable<string>` direct param — concrete generic, JSON deserialization (step 9B)
- `Message` abstract class — `[JsonDerivedType]` with UserMessage/SystemMessage subclasses (step 9B)

**Options classes (flattening):**
- `SmallOptions` — exactly 10 scalar properties (boundary: all flattened)
- `LargeOptions` — 15 scalar properties (boundary: 10 flat + `--json-input`)
- `NestedOptions` — contains `Address` sub-object (always `--json-input`)

**Sanitization edge cases:**
- Parameter named `class` — C# keyword
- Parameter named `event` — C# keyword
- Method named `GetClass` — produces verb that's a keyword after processing? No — `get-class` is fine. Use `ClassService.EventAsync` → verb `event` instead.
- Type with description containing `"; Process.Start("malware");//` — injection attempt

**Auth patterns:**
- Constructor taking `string apiKey` — detected as ApiKey auth
- Constructor taking `TokenCredential credential` — detected as BearerToken

---

## SDK call wiring rules (step 7)

### Constructor auth dispatch

Each resource's constructor may take a different auth parameter type. The adapter extracts `ConstructorAuthTypeName` per resource. The ModelMapper computes `ConstructorAuthExpression`:

- `string` or null → `"credential"` (pass the resolved string directly)
- Any `*Credential` type → `"new {TypeName}(credential)"` (wrap the string in the SDK's credential type)

The type name is validated via `IdentifierValidator.IsValidIdentifier` before interpolation into the expression (defense-in-depth — adapter inputs are already valid CLR identifiers).

### Type conversion expressions

`FlatParameter.ConversionExpression` is a C# expression format string with `{0}` as the variable placeholder. Null means identity (no conversion needed). Computed by `ParameterFlattener.ComputeConversion`:

| SDK Type | Nullable | ConversionExpression |
|----------|----------|---------------------|
| string, int, bool, decimal, etc. | any | `null` (CLI type matches) |
| Enum (e.g., CustomerStatus) | no | `Enum.Parse<CustomerStatus>({0})` |
| Enum | yes | `{0} is not null ? Enum.Parse<CustomerStatus>({0}) : (CustomerStatus?)null` |
| TimeSpan, DateTime, DateTimeOffset, Guid | no | `TimeSpan.Parse({0})` (etc.) |
| TimeSpan, DateTime, DateTimeOffset, Guid | yes | `{0} is not null ? TimeSpan.Parse({0}) : (TimeSpan?)null` |
| Class, Array, Generic, Dictionary | any | `null` (handled via --json-input, deferred) |

Enum names are validated via `IdentifierValidator.IsValidIdentifier` before interpolation into `Enum.Parse<>`. Invalid names fall back to null (identity).

### Value type nullability rule

`NullableContextAttribute` on a declaring class only affects reference types. Value types (`bool`, `int`, `decimal`, etc.) are nullable only when explicitly declared as `Nullable<T>` (i.e., `bool?`). The adapter's `IsNullableProperty` and `IsNullableParameter` enforce this with a `!IsValueType` guard before checking context attributes.

### Multi-options-class parameter tracking

When an SDK method takes multiple class-typed parameters (e.g., `CreateAsync(CreateOptions opts, RequestOptions reqOpts)`), the `ParameterFlattener` merges all scalar properties into one flat list but tracks which options class each property came from via `FlatParameter.SourceOptionsClassName`. The template uses this to group property assignments by options class when constructing SDK calls.

### Required namespaces

Options classes, auth credential types, and service classes may live in different namespaces. `ResourceModel.RequiredNamespaces` collects all distinct namespaces needed by a resource's generated code — from `SourceNamespace`, `ConstructorAuthTypeNamespace`, and all `MethodParamModel.Namespace` values. Entries are validated as dotted identifiers, deduplicated, and sorted alphabetically for deterministic golden file output.

### Non-instantiable type policy (step 7D)

The adapter skips property extraction for types that can't be instantiated in generated handlers:
- **Abstract types** (`type.IsAbstract`) — e.g., `BinaryContent`, `Stream`
- **Types without a public parameterless constructor** — e.g., `GetResponseOptions(string responseId)`, `BinaryData`

These types become plain `string` CLI parameters (via `forCliParam: true` mapping). The generated handler passes the string value directly. Future `--json-input` can handle proper deserialization.

### Read-only property filtering (step 7D)

`ExtractClassProperties` only includes properties with a public setter (`prop.CanWrite && prop.SetMethod?.IsPublic == true`). Read-only properties like `Stream.CanRead`, `BinaryData.Length` are excluded — they can't be assigned in generated handlers.

### Constructor preference rule (step 8A — replaces step 7D rule)

`ExtractConstructorParams` finds all constructors with at least one auth param, then prefers the one with the MOST user-facing (non-infrastructure) params. This picks `ChatClient(string model, ApiKeyCredential cred)` over `ChatClient(ApiKeyCredential cred)`, giving us `--model` as a CLI config option. Non-auth required params become resource-level options via `AddGlobalOption`. Stable tiebreaker on parameter names.

The `IsApiKeyParameter` heuristic uses an exact-match allowlist (`apikey`, `api_key`, `secretkey`, `secret`, `apisecret`, `api_secret`) — not `Contains("key")`. `IsInfrastructureParam` matches `CancellationToken`, `RequestOptions` (any namespace), and types ending with `ClientOptions` or `ClientSettings`.

### Static auth configuration (step 8B)

Some SDKs (Stripe) use static properties for auth instead of constructor injection. The adapter scans the assembly for `*Configuration` classes with a static writable `ApiKey`/`SecretKey`/`ApiSecret` property. If found, it stores the fully qualified path (e.g., `Stripe.StripeConfiguration.ApiKey`) as `StaticAuthSetup` on `SdkMetadata`.

Services with parameterless constructors in static-auth SDKs are constructable — the template emits `Stripe.StripeConfiguration.ApiKey = credential;` before `new PaymentIntentService()`. This unblocked 93% of Stripe operations (490/524).

Services WITHOUT parameterless constructors (e.g., nested services requiring `IStripeClient` injection) remain as echo stubs — they need DI support which is deferred.

### Options class properties never required (step 8B)

Options class properties are never marked `Required` in the CLI. SDK options classes are configuration objects — all properties are optional by nature. Many SDKs (Stripe, older .NET) have non-nullable strings without annotations but handle null gracefully at runtime.

### Infrastructure parameter filtering (step 7D)

The adapter recognizes infrastructure types that should not be exposed as CLI parameters:

- **`CancellationToken`**: Skipped entirely from parameter extraction (never user-facing).
- **`RequestOptions`** (`System.ClientModel.Primitives`): Kept in the parameter list but property extraction is skipped. This makes it a bare `Class` (no properties) → `CanWireOperation` detects it as unconvertible → operation falls back to echo. Constructing `RequestOptions` with defaults causes SDK errors (`Value cannot be null`), so it must not be instantiated in generated handlers.

The overload selector also excludes infrastructure types from the parameter count, preferring convenience methods (e.g., `GetModelsAsync()`) over protocol methods (`GetModelsAsync(RequestOptions)`).

### CanConstruct / CanWireSdkCall gates (step 7D)

Two gates control whether generated handlers emit real SDK calls or fall back to the echo stub:

- **`CanConstruct`** (per resource): `true` when the adapter found a valid single-param auth constructor. `false` for clients like `RealtimeSessionClient` that require multi-arg constructors.
- **`CanWireSdkCall`** (per operation): `true` when all direct parameters are convertible from CLI types AND the return type is awaitable. `false` when:
  - Any direct param is `Generic`, `Array`, `Dictionary`, or bare `Class` (without properties — includes `RequestOptions`)
  - The return type matches known non-awaitable suffixes (`*Client`, `*Service`, `*Api`, `*ClientSettings`, `*Options`, `AsyncCollectionResult`, `CollectionResult`)

Operations with `CanWireSdkCall = false` emit a `CB306` warning diagnostic and fall back to the echo stub.

### Value type property rule (step 7D)

Value type properties (`bool`, `int`, `enum`) in options classes are never marked as `Required`. Unlike reference types, value types always have implicit defaults (`false`, `0`, first enum value) and the CLI can't distinguish "user didn't set" from "user set the default". This prevents SDK infrastructure properties like `BufferResponse` (`bool`) and `ErrorOptions` (`enum`) from becoming required CLI flags.

---

## Generator sanitization surfaces

The generator converts metadata strings into three distinct output formats, each requiring its own sanitization:

1. **C# source code** — descriptions, identifiers flow into `.cs` files. Defense: `SanitizeString` (Scriban syntax neutralization) + `escape_csharp` (verbatim string literals) + `IdentifierValidator` (keyword denylist, path safety, `IsValidIdentifier`/`IsValidNamespace` for type names and namespaces that flow into `new T()` expressions and `using` directives).
2. **XML (`.csproj`)** — `SdkName`, `SdkVersion`, `SdkPackageName` flow into `PackageReference` attributes. Defense: `SanitizeXmlValue` (escapes `<`, `>`, `"`, `&`, `'`). Without this, a crafted SDK name achieves arbitrary code execution via MSBuild injection during `dotnet build`.
3. **Scriban templates** — all metadata strings pass through the template engine before reaching output. Defense: `SanitizeString` neutralizes `{{`, `}}`, `{%`, `%}` at the model mapping layer, before strings reach the template engine. The `escape_csharp` filter in templates is defense-in-depth only.

**`DefaultValue` numeric validation:** `JsonElement.GetRawText()` output for numbers is validated against `^-?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?$` before emitting into C# source. This is defense-in-depth — `System.Text.Json` constrains the format, but the assumption is unverified without the regex check.

**Template model contract:** All Scriban template models must use typed records (e.g., `GeneratorModel`, `CommandFileModel`), not anonymous types. Scriban's `ScriptObject.Import` applies a custom `MemberRenamer` (PascalCase → snake_case) only to the top-level object. Anonymous types work by naming coincidence but break if the renamer diverges. Typed records make the template contract explicit and testable.

### Python generator — click conventions and sanitization

The Python generator (`crates/gen-python`) uses Tera templates + click as the CLI runtime. Two conventions are load-bearing:

**Optional bool parameters render as `type=click.BOOL, default=None` — NOT `is_flag=True, default=False`.** The older flag-style rendering silently overrode SDK defaults: when the user omitted `--some-flag`, click delivered `False`, the kwargs guard (`if x is not None`) let the `False` through, and the SDK's own default was clobbered. Tri-state (`type=click.BOOL, default=None`) gives click `None` when absent, a real bool only when the user explicitly passes `--x true` / `--x false`. Required bools still use `is_flag=True, default=False, required=True` — required means the user must pass something, and flag ergonomics are fine there. Enforced by the class-level scan in `tests/e2e.rs::no_generated_sdk_param_has_unguarded_is_flag` (rendered as a top-of-file test in the `template_rendering` module before it was moved).

**`py_str` Tera filter escapes backslashes and double quotes.** Parameter descriptions flow into click's `help="..."` argument. An SDK whose parameter documentation contains `"` or `\` (common for real APIs) would otherwise produce unterminated Python string literals. The filter runs `.replace('\\', '\\\\').replace('"', '\\"')` — order matters so backslashes added by the second step don't get re-escaped. Registered in `crates/gen-python/src/renderer.rs` and invoked in the template as `{{ param.description | py_str }}`.

**Template clauses use `{% set_global %}`, not Tera macros.** The `@click.option(...)` line is built from four clause variables (`required_clause`, `type_clause`, `help_clause`, plus an intermediate `enum_joined` for the enum branch). Tera's plain `{% set %}` scopes to the enclosing `{% if %}` block; the clause wouldn't survive to the decorator line. `{% set_global %}` is the correct tool. Tera macros were considered and rejected — they require `{% import %}` wiring in `renderer.rs` and are overkill for a single template.

**`cli_description` uses ASCII `-`, not the em dash `—`.** `model_mapper.rs` formats the description as `"{cli_name} - CLI for {sdk_name}"`. The em dash would survive in UTF-8 pipelines but Windows Python defaults stdout to cp1252 and the byte sequence comes back as mojibake — breaking the generated `--help` output on any default Windows console.

### Python generator — test organization and runtime anchor

The Python generator has two test layers:

1. **Unit + golden tests (`crates/gen-python/src/tests.rs`)** — the `#[cfg(test)]` module covers model mapping, template rendering branches, and `insta` golden snapshots for the generated output. 35 tests, fast, no subprocesses.

2. **Runtime anchor (`crates/gen-python/tests/e2e.rs`)** — a cargo integration test binary. `help_output_snapshot` generates the TestSdk CLI, spawns `python -m testsdk_cli --help` via `PYTHONPATH`, and snapshots the stdout. Catches click semantic drift (e.g. 8 → 9 formatting changes) and generated-CLI import regressions that pure string scans can miss.

The e2e test uses **PYTHONPATH + `python -m`**, not `venv + pip install`. Rationale:
- `python/tests/test_sdk/` has no `pyproject.toml` — `pip install` would fail at step 1.
- PYTHONPATH runs in ~1s vs ~30s for venv creation + pip install.
- Eliminates three cross-platform failure modes (PyPI network, `pyproject.toml` parsing, Windows `Scripts/` vs `bin/` venv layout).
- Gap accepted: `python -m` bypasses the `[project.scripts]` console-script entry point. That gap is tracked as a `#[ignore]`'d placeholder test and a `docs/FUTURE.md` entry; CI has a `grep -q` step that fails if the tracking entry is removed.

### Python adapter — PEP 692 `Unpack[TypedDict]` resolution (ADR-022)

The Python adapter resolves `**kwargs: Unpack[TypedDict]` annotations (PEP 692) by AST-walking the defining module's `if TYPE_CHECKING:` blocks to discover where each `ForwardRef` is imported from, then `importlib.import_module` + `getattr` to materialize the class. Without this path, every Stripe (and structurally similar) SDK's CRUD methods generate zero CLI flags because `typing.get_type_hints()` cannot resolve TYPE_CHECKING-only imports at runtime. Four conventions are load-bearing:

**`map_type` purity contract.** `python/src/cli_builder_adapter/type_mapper.py::map_type` accepts only fully resolved type objects — never raw `ForwardRef`s, never quoted strings. ForwardRef resolution is the caller's responsibility (specifically `_resolve_field_type` in `extractor.py`, which evaluates against the TypedDict's defining-module namespace). Extending `map_type` with a `globals=` resolution context is explicitly declined — see ADR-022 alternatives. Future contributors: if you find yourself wanting to thread `globals` through `map_type`, resolve at the call site instead.

**`if TYPE_CHECKING:` AST walk scope.** `_collect_type_checking_imports` parses only top-level `ast.ImportFrom` nodes inside `if TYPE_CHECKING:` blocks. `ast.Import`, star imports (`from x import *`), and nested conditional blocks are intentionally ignored — names from those shapes surface as not-found and emit `CB607` (warning) instead of trying to be clever. Relative imports (`from .params import X`) are resolved to absolute module paths using the host module's `__name__` as anchor.

**`functools.lru_cache` on `_collect_type_checking_imports`.** Stripe has ~41 `TYPE_CHECKING` imports per resource module, and the adapter touches each module once per operation (~300+ operation extractions across 100+ resources in a single `extract()` call). Without caching, the AST parser would re-run hundreds of times against the same file. Keyed by `(module_file, module_name)` — module name is needed to resolve relative imports correctly; file path alone is insufficient.

**TypedDict source-of-truth for required/optional.** Iterate `__required_keys__ | __optional_keys__`. PEP 589's `_TypedDictMeta` aggregates inherited keys into those frozensets at class-creation time — no manual MRO walk needed for the *key set*. Field annotations may live on parent classes, so `__annotations__` IS walked across `__mro__` for name → annotation lookup. Per-field resolution is wrapped in try/except so a single bad annotation (recursive type, missing import, malformed `ForwardRef`) emits `CB608` and falls back to `TypeKind.Other` without aborting the rest of the walk.

**Nested TypedDicts route through `--json-input`, not recursion.** When a field's resolved annotation is itself a TypedDict (`hasattr(__required_keys__)`), the field is emitted as `TypeKind.Other` + `CB608` regardless of resolution success. This is intentional — recursive flattening would produce combinatorial CLI flag explosions on multi-level nested params (Stripe's `customer create` has `address`, `payment_method_data`, `shipping`, each with their own nested shapes). Mirrors C# ADR-007 flattening policy.

**`typing_extensions` is a hard runtime dependency.** Python 3.10 ships only `typing_extensions.Unpack`; 3.11+ adds `typing.Unpack`. The adapter standardizes on `typing_extensions.Unpack` / `get_origin` / `get_args` for cross-version normalization. Declared in `python/pyproject.toml` under `[project.dependencies]`, not `[project.optional-dependencies]` — leaving it optional would let 3.10 CI pass locally and fail on clean install.

### Python adapter — single-client SDK shape discovery (ADR-023)

When the multi-service path finds zero `*Service`/`*Client`/`*Api`-suffixed classes, the adapter falls back to single-client discovery: pick one entry class whose verb-noun methods become CLI operations grouped by noun → resource. Activated automatically for SDKs like PyGithub, Notion, Linear, Slack, Anthropic; can be forced explicitly via `--entry-class <ClassName>`. Six conventions are load-bearing:

**Naming policy in `_naming.py`, not `extractor.py`.** Verb whitelist (`get`/`list`/`create`/`update`/`delete`/`search`/`find`/`retrieve`), descriptive-noun prefix filter (`from_`/`to_`/`with_`/`for_`), `MIN_ENTRY_CLASS_METHODS` threshold, `parse_verb_noun()` helper, and `skip_reason()` for CB610 messages live in a dedicated `python/src/cli_builder_adapter/_naming.py` module. The `_utils.py` module holds string-conversion mechanics (`class_to_noun`, `pascal_to_kebab`); `_naming.py` holds semantic classification. Step 19+ sub-resource walkers import from `_naming` cleanly. If you find yourself adding policy constants to `_utils.py`, that's the wrong module.

**Entry-class heuristic = name pattern AND method count.** Either path alone is too loose. The heuristic accepts: `name == <package>.capitalize()` (PyGithub's `Github`), name in `{"Client", "Api"}`, name ends in `Client`/`Api`, or name starts with `<package>.capitalize()` without a service suffix (catches `GithubMain`, `NotionAdmin`). All matches must also have `>= MIN_ENTRY_CLASS_METHODS` (10) public methods. `@classmethod` and `async def` methods count toward the threshold — Slack-style SDKs use both heavily, and silently undercounting would misfire the auto-detection.

**Method-skip rules emit `CB610` with reason — silent skips are forbidden.** Four filters, each names which rule fired in the diagnostic message:
1. No underscore (`close`, `withLazy`)
2. Verb not in whitelist (`render_markdown` → "verb 'render' not in whitelist")
3. Noun starts with descriptive prefix (`create_from_raw_data` → "descriptive noun prefix")
4. First non-`self` parameter has `type[T]` or `Type[T]` annotation (`register_class` → "type[T] (factory method)")

Severity is WARNING (not INFO) — symmetric with CB607/CB608 on parameter loss. Silent surface reduction was the original Stripe / PyGithub bug class; CB610 makes every drop visible.

**Singular/plural NOT normalized; verb NOT canonicalized.** `get_repo` and `list_repos` produce two resources: `repo` and `repos`. `retrieve_user` is a separate operation from `get_user` (both on resource `user`). The SDK author chose those names; the CLI reflects them faithfully. Aggressive normalization would conflate distinct operations and surprise users reading `--help` who expect the API surface they wrote.

**`discovery_mode` field on `SdkMetadata` (provenance).** Records which discovery path produced the metadata (`"multi_service"` default, `"single_client"` when fallback or explicit). JSON schema property `discoveryMode`. Downstream consumers (generator, future tooling) branch on this without re-parsing diagnostic codes. The Python generator currently uses it to emit a header comment in `cli.py` noting "sub-resources detected but not expanded" when `discovery_mode == "single_client"` AND any operation's return type is non-primitive — a documentation surface for the user that the full API isn't yet flattened into the CLI.

**`pypi_name` field on `SdkMetadata` (distribution-vs-import name).** PyGithub installs as `PyGithub` (PyPI) but imports as `github` (Python). Pre-Step-18 the generated `pyproject.toml` listed the import name as the dependency, hitting an unrelated typo-squatted PyPI package. The adapter now resolves the PyPI distribution name via `importlib.metadata.packages_distributions()`. `None` when distribution name equals import name (Stripe) — no override needed. `ModelMapper::build` in Rust core uses `pypi_name` for `sdk_package_name` when present.

**Constructor params attached to ALL resources in single-client mode.** All resources in single-client mode share the same entry-class constructor. The generator's `can_construct` gate (model_mapper.rs:281) checks per-resource ctor params; without ctor info on every resource, ops other than the first sorted resource generate `"client construction not available"` stubs. This was a real bug PR 2's PyGithub validation surfaced — the fix attaches `ctor_params` uniformly across resources from a single client class.

**Sub-resource discovery deferred to Step 19+.** PyGithub's `Github.get_repo()` returns a `Repository` with its own methods (`get_issues`, etc.). Recursively walking returned types into nested CLI commands is an architectural change with implications for CLI nesting model + parent-context flag propagation. The generated CLI surfaces a header comment when sub-resources are detected but not expanded — `--json-input` is the documented escape hatch in the meantime.

**Known limitation — `Opt[T]` sentinel-Union type aliases.** PyGithub defines `Opt[T] = Union[T, _NotSetType]` as its optional-parameter convention (analogous to `Optional[T]` but with a sentinel class instead of `None`). The adapter's type mapper doesn't recognize this pattern as Optional, so parameters annotated `Opt[X]` emit as `TypeKind.OTHER` and route through `--json-input`. PyGithub operations work end-to-end (auth + SDK call + result), but per-parameter flags aren't emitted on operations with `Opt[X]` params. Workaround: pass nested JSON via `--json-input`. Future fix: recognize 2-arg Union with one arg being a sentinel-named class (`_NotSet`, `NotSetType`, `Sentinel`, etc.) as Optional.

---

## Platform-specific notes

**Golden files:** Shared across platforms (not per-platform). Generated output must be byte-identical on Windows and Linux. Enforce by:
- Generator always emits LF (`\n`), never platform-default
- Generated `.csproj` paths use forward slashes only
- Scriban configured with `Environment.NewLine = "\n"`
- CI runs generator on both `ubuntu-latest` and `windows-latest`, asserts identical output

**Path construction in generator:** Use string concatenation with `/` for paths inside generated project files (`.csproj`, `using` directives). Use `Path.Combine` only for file I/O operations on the host machine.

---

## Adapter invocation contract (Step 12+)

Each adapter is a standalone CLI executable. The orchestrator calls it as a subprocess.

**Interface:**
1. Accepts a path to the SDK artifact (DLL, installed package, JAR, etc.)
2. Emits `SdkMetadata` JSON to stdout on success
3. Emits diagnostics to stderr (human-readable, same format as `DiagnosticsFormatter`)
4. Exit code 0 = success (Info/Warning diagnostics OK), exit code 1 = Error diagnostics, exit code 2 = environment failure

**Invocations:**
```bash
# .NET adapter (already exists as cli-builder inspect)
cli-builder inspect --assembly /path/to/Stripe.net.dll --json

# Python adapter (Step 12)
cli-builder inspect --adapter python --package stripe --json

# Future: Kotlin, Go, OpenAPI adapters follow same pattern
```

**Contract rules:**
- Adapters must NOT load or execute SDK code — metadata-only analysis (reflection, AST, type stubs)
- JSON schema matches `SdkMetadataJson.Options` (camelCase, enums as strings, indented)
- Adapters are permanent — they stay in their native language when the orchestrator migrates to Rust
- Each adapter is versioned independently (SemVer on the JSON schema version)

## Generator architecture (ADR-017)

All generators consolidated in Rust with shared core + language-specific Tera templates. Implemented in `crates/` workspace.

**Shared Rust core (`cli-builder-core`, ~900 lines, 64 tests):**
- `ModelMapper` (~310 lines) — SdkMetadata → GeneratorModel via pluggable `LanguageProfile` trait
- `ParameterFlattener` (~130 lines) — flatten options class properties into CLI flags, detect `--json-input` scenarios
- `IdentifierValidator` (~175 lines) — case conversion, validation, `sanitize_parameter` with pluggable keyword/boilerplate checks
- `GeneratorModel` (~130 lines) — language-neutral types with `Serialize` for Tera context. No C#-specific fields: `cli_type` not `CSharpType`, no `ConversionExpression`, `requires_sentinel_nullability` flag for generators to interpret.
- Sanitization split: core strips control chars only; template-engine escaping (`{{ }}`) in generators

**Per-language generators (standalone crate per language):**
- Python (`cli-builder-gen-python`, ~250 lines code + ~250 lines templates, 26 tests): `PythonProfile` + 8 Tera templates for `click`-based CLI. Golden file snapshots via `insta`.
- C# (`cli-builder-gen-csharp`, ~500 lines code + ~500 lines templates, 62 tests): `CSharpProfile` + 6 Tera templates for System.CommandLine CLI. C#-specific post-processing: `ComputeConversion`, `SanitizeDefaultValue`, `MakeValueTypesNullable`, `BuildConstructorExpression`. Compile-validated (`dotnet build`). Golden file snapshots via `insta`.
- Future: Kotlin (clikt), Go (cobra), TypeScript (commander)

**Pipeline:**
```
SDK → Native adapter (subprocess) → SdkMetadata JSON → Rust generator (ModelMapper + Tera) → CLI project
```

**Why Rust, not native per language:** 80% of generator code (ModelMapper, ParameterFlattener) is language-agnostic. Writing it once in Rust and sharing across all generators eliminates ~4000 lines of duplicated logic. Schema changes propagate to all generators via one Rust struct update. Single binary distribution: `cargo install cli-builder`.
