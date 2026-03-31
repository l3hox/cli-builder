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

**Precedence:** When both flat flags and `--json-input` are provided for the same command:
- `--json-input` values are applied first as the base object
- Flat flags override individual properties on top
- This allows: `--json-input '{"email":"a@b.com","name":"Test"}' --name "Override"` where `--name` wins

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

**CB4xx — Output validation (generator):**
- `CB401` — Generated project failed `dotnet build` verification
- `CB402` — Generated `--help` output missing expected sections

**CB5xx — Enrichment (future):**
- `CB501` — LLM provider unreachable
- `CB502` — Enrichment cache miss (re-enriching)
- `CB503` — Enriched text failed sanitization

---

## Test SDK assembly manifest

The purpose-built test SDK assembly must contain:

**Service classes (resource discovery):**
- `CustomerService` — standard service, matches `*Service` pattern
- `OrderClient` — matches `*Client` pattern
- `ProductApi` — matches `*Api` pattern
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

### Constructor preference rule (step 7D)

`ExtractConstructorAuthType` sorts constructors by parameter count (ascending, stable tiebreaker on param names) and only matches constructors with a single required parameter. This prefers `Client(ApiKeyCredential cred)` over `Client(string model, ApiKeyCredential cred)`. The `IsApiKeyParameter` heuristic uses an exact-match allowlist (`apikey`, `api_key`, `secretkey`, `secret`, `apisecret`, `api_secret`) — not `Contains("key")`.

### CanConstruct / CanWireSdkCall gates (step 7D)

Two gates control whether generated handlers emit real SDK calls or fall back to the echo stub:

- **`CanConstruct`** (per resource): `true` when the adapter found a valid single-param auth constructor. `false` for clients like `RealtimeSessionClient` that require multi-arg constructors.
- **`CanWireSdkCall`** (per operation): `true` when all direct parameters are convertible from CLI types AND the return type is awaitable. `false` when any direct param is `Generic`, `Array`, `Dictionary`, or bare `Class` (without properties), or when the return type matches known non-awaitable suffixes (`*Client`, `*Service`, `*Api`, `*ClientSettings`, `*Options`, `AsyncCollectionResult`, `CollectionResult`).

Operations with `CanWireSdkCall = false` emit a `CB306` warning diagnostic and fall back to the echo stub.

---

## Generator sanitization surfaces

The generator converts metadata strings into three distinct output formats, each requiring its own sanitization:

1. **C# source code** — descriptions, identifiers flow into `.cs` files. Defense: `SanitizeString` (Scriban syntax neutralization) + `escape_csharp` (verbatim string literals) + `IdentifierValidator` (keyword denylist, path safety, `IsValidIdentifier`/`IsValidNamespace` for type names and namespaces that flow into `new T()` expressions and `using` directives).
2. **XML (`.csproj`)** — `SdkName`, `SdkVersion`, `SdkPackageName` flow into `PackageReference` attributes. Defense: `SanitizeXmlValue` (escapes `<`, `>`, `"`, `&`, `'`). Without this, a crafted SDK name achieves arbitrary code execution via MSBuild injection during `dotnet build`.
3. **Scriban templates** — all metadata strings pass through the template engine before reaching output. Defense: `SanitizeString` neutralizes `{{`, `}}`, `{%`, `%}` at the model mapping layer, before strings reach the template engine. The `escape_csharp` filter in templates is defense-in-depth only.

**`DefaultValue` numeric validation:** `JsonElement.GetRawText()` output for numbers is validated against `^-?[0-9]+(\.[0-9]+)?([eE][+-]?[0-9]+)?$` before emitting into C# source. This is defense-in-depth — `System.Text.Json` constrains the format, but the assumption is unverified without the regex check.

**Template model contract:** All Scriban template models must use typed records (e.g., `GeneratorModel`, `CommandFileModel`), not anonymous types. Scriban's `ScriptObject.Import` applies a custom `MemberRenamer` (PascalCase → snake_case) only to the top-level object. Anonymous types work by naming coincidence but break if the renamer diverges. Typed records make the template contract explicit and testable.

---

## Platform-specific notes

**Golden files:** Shared across platforms (not per-platform). Generated output must be byte-identical on Windows and Linux. Enforce by:
- Generator always emits LF (`\n`), never platform-default
- Generated `.csproj` paths use forward slashes only
- Scriban configured with `Environment.NewLine = "\n"`
- CI runs generator on both `ubuntu-latest` and `windows-latest`, asserts identical output

**Path construction in generator:** Use string concatenation with `/` for paths inside generated project files (`.csproj`, `using` directives). Use `Path.Combine` only for file I/O operations on the host machine.
