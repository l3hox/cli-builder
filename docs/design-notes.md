# Design Notes

Behavioral rules and edge-case policies that refine the spec. These bridge the gap between the high-level spec (interfaces, models, requirements) and the implementation plans in `docs/internal/`.

Sourced from developer council review (2026-03-26).

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

## Platform-specific notes

**Golden files:** Shared across platforms (not per-platform). Generated output must be byte-identical on Windows and Linux. Enforce by:
- Generator always emits LF (`\n`), never platform-default
- Generated `.csproj` paths use forward slashes only
- Scriban configured with `Environment.NewLine = "\n"`
- CI runs generator on both `ubuntu-latest` and `windows-latest`, asserts identical output

**Path construction in generator:** Use string concatenation with `/` for paths inside generated project files (`.csproj`, `using` directives). Use `Path.Combine` only for file I/O operations on the host machine.
