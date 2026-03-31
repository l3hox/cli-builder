# Phase 7D: OpenAI Scale Fixes + Cleanup

**Prerequisite:** Phases 7A-7C complete. TestSdk CLI compiles and runs end-to-end (298 tests). The OpenAI-generated CLI has **900 compile errors** across 6 root causes.
**Output:** The OpenAI-generated CLI compiles with zero errors. Documentation updated. Coverage maintained.

---

## Error Analysis

900 compile errors from `GenerateOpenAi_Compiles`, grouped into 6 root causes:

| # | Root Cause | Errors | Example | Fix Layer |
|---|-----------|--------|---------|-----------|
| 1 | Abstract/non-instantiable options classes | 372 | `new BinaryContent()` — abstract | Adapter |
| 2 | Read-only properties assigned in handler | 240 | `stream.CanRead = ...` — get-only | Adapter |
| 3 | Multi-arg constructors missing non-auth args | 56 | `RealtimeSessionClient(model, cred)` | Adapter + Template |
| 4 | Non-generic `AsyncCollectionResult` not unwrapped | 48 | `await client.ListBatches(...)` — not awaitable | Adapter |
| 5 | Direct param type mismatch (complex types as `string`) | 52 | `string` → `IEnumerable<ChatMessage>` | Generator |
| 6 | Misc (variable collision, await void, required ctor params) | 32 | `var stream` collision, `await void`, `new GetResponseOptions()` missing required arg | Various |

---

## Council Review Findings

A Developer Council (SoftwareDeveloper, QaTester — 3-round debate) reviewed this plan and identified:

**P0 — `IsApiKeyParameter` substring match is fragile.** `Contains("key")` at `DotNetAdapter.cs:587` matches `encryptionKey`, `licenseKey`, etc. Real risk for OpenAI SDK which has multi-param constructors. Fix: replace with exact-match allowlist (`"apikey"`, `"api_key"`, `"secretkey"`, `"secret"`). Added to Fix 3.

**P0 — Fix 5 must emit a diagnostic.** Silent echo fallback creates invisible functionality loss. Emit `CB306` when `CanWireSdkCall = false`.

**P1 — Extract shared auth classification.** `DetectAuthPatterns` and `ExtractConstructorAuthType` duplicate the `IsApiKeyCredential → IsCredential → IsApiKey` priority chain. Extract `TryClassifyAuthParam`.

**P1 — Stable tiebreaker for constructor sort.** Equal-length constructors produce non-deterministic output across runtimes. Add `.ThenBy(m => m.Name)`.

**P1 — Fixture consistency test.** Verify regenerated fixture matches adapter output — no stale `BinaryContent.Properties` remaining.

**P2 — Fix 6b (`await void`) needs investigation** before assuming Fixes 1-5 resolve it.

---

## Fix Strategy Per Root Cause

### Fix 1: Abstract/Non-Instantiable Options Classes (372 errors)

**Problem:** `ExtractClassProperties` at `DotNetAdapter.cs:300` extracts ALL public instance properties without checking if the type is abstract or has a parameterless constructor. The generator emits `var binaryContent = new BinaryContent();` which fails because `BinaryContent` is abstract.

**Affected types:** `BinaryContent` (System.ClientModel), `Stream` (System.IO), `BinaryData` (System), `RealtimeItem` (OpenAI.Realtime).

**Fix:** Add `IsAbstract` flag to `TypeRef`. In the adapter, set it when the type is abstract or has no public parameterless constructor. In the ParameterFlattener/template, skip options class construction for non-instantiable types — treat them as opaque and rely on `--json-input` only.

**Changes:**
1. `TypeRef.cs`: Add `bool IsAbstract = false`
2. `DotNetAdapter.cs` `BuildTypeRef`: Set `IsAbstract = true` when `type.IsAbstract || !HasPublicParameterlessCtor(type)`
3. `DotNetAdapter.cs` `ExtractParameters`: When a class param is abstract, still extract properties (for `--json-input` schema) but mark the TypeRef
4. `ParameterFlattener.cs`: When a class param has `IsAbstract = true`, don't set `SourceOptionsClassName` — the template won't try to construct it
5. `ModelMapper.cs` `BuildMethodParams`: For abstract options class params, set `IsOptionsClass = false` so the template passes `--json-input` value instead (or emits `default` placeholder)
6. Template: The existing echo fallback will handle these since the method params won't be options classes

**Alternative approach (simpler):** Instead of adding `IsAbstract` to the model, detect non-instantiable types in the adapter and skip property extraction entirely — emit them as plain `TypeKind.Class` without `Properties`. The ParameterFlattener already treats class params without properties as direct params (not flattened). The template would then pass the `string` CLI value directly, which is wrong for the SDK call but at least compiles. However, this loses the property schema for `--json-input`.

**Recommended approach:** Skip property extraction for abstract types in the adapter. The parameters become direct `string` params in the CLI (via `forCliParam: true` mapping). The generated handler passes the string to the SDK method, which won't work at runtime but will compile. Future `--json-input` can handle deserialization. This is the simplest fix that produces compilable output.

### Fix 2: Read-Only Properties (240 errors)

**Problem:** `ExtractClassProperties` at `DotNetAdapter.cs:302` uses `type.GetProperties(BindingFlags.Public | BindingFlags.Instance)` which returns all public properties including those with no public setter. The generator emits `stream.CanRead = canReadValue;` which fails because `CanRead` is get-only.

**Affected types:** `Stream` (CanRead, CanSeek, CanWrite, CanTimeout, Length), `BinaryData` (IsEmpty, Length, MediaType), `BinaryContent` (MediaType), `GetResponseOptions.ResponseId`, `ResponseItemCollectionOptions.ResponseId`.

**Fix:** Filter out properties without a public setter in `ExtractClassProperties`.

**Changes:**
1. `DotNetAdapter.cs` `ExtractClassProperties`: Add `prop.CanWrite && prop.SetMethod?.IsPublic == true` check before including a property.

**This is a one-line fix that eliminates 240 errors.**

Note: Some of the affected types (Stream, BinaryContent) are also abstract (Fix 1). If Fix 1 skips property extraction for abstract types, it implicitly solves the read-only property errors for those types. But Fix 2 is still needed for concrete types with read-only properties (`GetResponseOptions.ResponseId`, `ResponseItemCollectionOptions.ResponseId`).

### Fix 3: Multi-Arg Constructors (56 errors)

**Problem:** OpenAI SDK clients have constructors like `ChatClient(string model, ApiKeyCredential credential)`. The adapter's `ExtractConstructorAuthType` finds the `ApiKeyCredential` parameter, but the template emits `new ChatClient(new ApiKeyCredential(credential))` — passing only one argument when the constructor requires two.

**Root cause:** The adapter doesn't store how many non-auth constructor parameters exist, and the template has no way to provide values for non-auth params like `model`.

**Affected types:** `RealtimeSessionClient` (56 errors — the only client where ALL constructors require extra args beyond the credential).

**Analysis:** Most OpenAI clients (`ChatClient`, `EmbeddingClient`, etc.) have BOTH a single-arg constructor `(ApiKeyCredential credential)` AND a multi-arg constructor `(string model, ApiKeyCredential credential)`. The adapter iterates constructors and finds the auth param on the multi-arg one first. But the single-arg constructor would work fine.

**Fix (3 parts):**

**3a. Prefer minimal constructors.** In `ExtractConstructorAuthType`, sort constructors by parameter count ascending (with stable tiebreaker on name). Pick the first constructor that has an auth-like parameter. This selects `ChatClient(ApiKeyCredential credential)` over `ChatClient(string model, ApiKeyCredential credential)`.

**3b. Tighten `IsApiKeyParameter` heuristic (council P0).** Replace `name.Contains("key") || name.Contains("secret")` with an exact-match allowlist: `name is "apikey" or "api_key" or "secretkey" or "secret" or "apiSecret" or "api_secret"`. This prevents false matches on `encryptionKey`, `licenseKey`, etc.

**3c. Extract shared auth classification (council P1).** Extract `TryClassifyAuthParam(ParameterInfo) → (AuthType, string TypeName, string? TypeNamespace)?` used by both `DetectAuthPatterns` and `ExtractConstructorAuthType`. Eliminates duplicated priority chain.

**3d. Add `CanConstruct` to ResourceModel.** Set `true` only when `ExtractConstructorAuthType` found a valid auth pattern. Template checks `resource.can_construct` instead of just `resource.source_class_name`. For `RealtimeSessionClient` (no single-arg constructor with auth), `CanConstruct = false` → echo fallback.

**Changes:**
1. `DotNetAdapter.cs` `ExtractConstructorAuthType`: Sort by param count ascending, stable tiebreaker
2. `DotNetAdapter.cs` `IsApiKeyParameter`: Exact-match allowlist
3. `DotNetAdapter.cs`: Extract `TryClassifyAuthParam` shared method
4. `GeneratorModel.cs` `ResourceModel`: Add `bool CanConstruct = false`
5. `ModelMapper.cs` `MapResource`: Set `CanConstruct = resource.ConstructorAuthTypeName != null`
6. `ResourceCommands.sbn`: Change condition to `resource.can_construct && op.source_method_name`

### Fix 4: Non-Generic `AsyncCollectionResult` (48 errors)

**Problem:** The return type unwrapping logic at `DotNetAdapter.cs:370` only handles GENERIC `AsyncCollectionResult<T>` (via `StreamingUnwrapTypes`). Non-generic `AsyncCollectionResult` (without `<T>`) falls through to `BuildTypeRef`, producing `TypeKind.Class`. The template then emits `var result = (object)await client.Method(...)` — but `AsyncCollectionResult` is not awaitable.

**Same issue with non-generic `CollectionResult`.**

**Root cause in the SDK:** Some OpenAI methods return non-generic `AsyncCollectionResult` or `CollectionResult` directly (the raw paginated result without a typed element). These are streaming/collection types that should not be directly `await`ed.

**Fix:** In `UnwrapAndBuild`, handle non-generic `AsyncCollectionResult` and `CollectionResult`:
- Non-generic `AsyncCollectionResult` → treat as streaming, return type = `object` (generic element type unknown)
- Non-generic `CollectionResult` → unwrap to `object`

**Changes:**
1. `DotNetAdapter.cs` `UnwrapAndBuild`: Extend the EXISTING non-generic `if (!type.IsGenericType)` block (which already handles `Task`/`ValueTask`) to also handle `AsyncCollectionResult` and `CollectionResult`:
```csharp
if (!type.IsGenericType)
{
    var name = type.Name;
    if (name == "Task" || name == "ValueTask")
        return (new TypeRef(TypeKind.Primitive, "void"), isStreaming);
    if (StreamingUnwrapTypes.Contains(name))
        return (new TypeRef(TypeKind.Primitive, "object"), isStreaming: true);
    if (UnwrapTypes.Contains(name))
        return (new TypeRef(TypeKind.Primitive, "object"), isStreaming);
}
```
This reuses the existing `UnwrapTypes` and `StreamingUnwrapTypes` sets, which already contain the right names.

### Fix 5: Direct Param Type Mismatch (52 errors)

**Problem:** Some SDK methods have direct (non-class) parameters of complex types: `IEnumerable<ChatMessage>`, `IEnumerable<string>`, `MessageRole`, `TimeSpan`, `IDictionary<string, string>`. The adapter stores these as `TypeKind.Generic` or `TypeKind.Enum` or `TypeKind.Primitive`. `MapTypeName(forCliParam: true)` maps them to `"string"`. But the template emits `client.Method(stringValue)` — passing a `string` where the SDK expects `IEnumerable<ChatMessage>`.

**Affected cases:**
- `IEnumerable<ChatMessage>` → `string` — can't convert
- `IEnumerable<string>` → `string` — can't convert
- `IEnumerable<ToolOutput>` → `string` — can't convert
- `MessageRole` → `string` — needs `Enum.Parse`
- `TimeSpan` → `string` — needs `TimeSpan.Parse`
- `IDictionary<string, string>` → `string` — can't convert

**Analysis:** The `ConversionExpression` system (from 7A) handles enums and TimeSpan for options class properties. But for DIRECT method params, the conversion is not applied — `MethodParamModel.ArgExpression` is just `"{varName}Value"` with no conversion.

**Fix for enums and TimeSpan:** Apply `ConversionExpression` to direct params too, not just options class properties. In `BuildMethodParams`, when a direct param has a ConversionExpression, incorporate it into the ArgExpression.

**Fix for `IEnumerable<T>` and `IDictionary`:** These are complex types that can't be parsed from a single string. The template should pass `default` or `null` for these params, which will compile but not work at runtime. Alternatively, skip SDK wiring for operations with unconvertible direct params (fall back to echo).

**Recommended approach:** In `BuildMethodParams`, check each direct param's type. If it's a type that can be converted (enum, TimeSpan, Guid, etc.), use the conversion. If it's an unconvertible complex type (Generic, Array, Dictionary, Class without properties), mark the operation as "can't wire" and fall back to the echo stub.

**Changes:**
1. `ModelMapper.cs` `BuildMethodParams`: For direct params, compute conversion. If conversion is null AND the CLI type is `"string"` AND the SDK type is complex (Generic, Class, Array, Dictionary), mark the method param as unconvertible.
2. `OperationModel`: Add `bool CanWireSdkCall = true` — set to false when any method param is unconvertible.
3. `ModelMapper.cs` `MapOperation`: When `CanWireSdkCall = false`, emit a `CB306` diagnostic: `"Operation '{name}' has unconvertible direct parameter '{paramName}' ({typeName}) — falling back to echo stub"`. (Council P0 — no silent fallback.)
4. Template: Check `op.can_wire_sdk_call` in addition to `resource.can_construct && op.source_method_name`.

### Fix 6: Miscellaneous (32 errors)

**6a. Variable collision `stream` (8 errors, CS0128)**

**Problem:** An options class named `Stream` produces a variable `stream` (via `PascalToCamelCase`). But `stream` is also used as a Scriban variable or conflicts with the `StreamAsync` method pattern. The generated code has `var stream = new Stream()` which collides with another `stream` variable in the same scope.

**Fix:** Append a suffix to options class variable names to avoid collisions. Use `{camelName}Options` or `{camelName}Param` instead of bare `{camelName}`. E.g., `streamParam` instead of `stream`.

**However**, Fix 1 (abstract types) will eliminate `Stream` as an instantiated options class since `Stream` is abstract. This fix may become unnecessary after Fix 1. Verify after applying Fix 1.

**6b. `await void` (4 errors, CS4008) — council: investigate before fixing**

**Problem:** Some operations return `void` (the adapter unwraps `Task` to `void`). The template emits `await client.Method(...)` but the method returns `void` (non-Task void), so `await` fails.

**Analysis required (council P2):** Check the fixture — these may be methods where the actual SDK return type is literal `void` (not `Task`). The adapter's `UnwrapAndBuild` handles non-generic `Task` → `void`, but the template's void branch emits `await` which is correct for `Task`-returning methods. If the method truly returns `void` (sync, non-Task), the template should not `await` it. Investigate the specific methods in the OpenAI fixture before choosing a fix.

**Likely fix:** If these are methods returning non-generic `AsyncCollectionResult` (which Fix 4 handles), they may resolve automatically. Verify after Fix 4.

**6c. Required constructor params on options classes (8 errors, CS7036)**

**Problem:** `GetResponseOptions(string responseId)` and `ResponseItemCollectionOptions(string responseId)` require a constructor argument. The template emits `new GetResponseOptions()` which fails.

**Fix:** These are covered by the same approach as Fix 1 — detect that the type has no parameterless public constructor and skip options class construction.

---

## Implementation Order

The fixes have dependencies:

```
Fix 2 (read-only props)     — standalone, adapter-only, one-line
Fix 4 (non-generic AsyncCR) — standalone, adapter-only
Fix 1 (abstract types)      — depends on Fix 2 being done (overlapping types)
Fix 3 (multi-arg ctors)     — adapter + model + template
Fix 5 (direct param types)  — model + mapper + template
Fix 6 (misc)                — verify after Fixes 1-5, may be resolved
```

**Recommended order:**
1. Fix 2 first (one-line, eliminates 240 errors)
2. Fix 4 next (small adapter change, eliminates 48 errors)
3. Fix 1 (adapter change, eliminates ~372 errors)
4. Rebuild and re-count — Fixes 1-4 may resolve many Fix 5/6 errors
5. Fix 3 (constructor ordering, eliminates remaining constructor errors)
6. Fix 5 (direct param types, any remaining type mismatch errors)
7. Fix 6 (verify and fix any remaining misc errors)

---

## Detailed Changes

### Step 1: Filter read-only properties (Fix 2)

**`src/CliBuilder.Adapter.DotNet/DotNetAdapter.cs`** — `ExtractClassProperties`:
```csharp
foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
{
    // Skip properties without a public setter — can't assign in generated handler
    if (!prop.CanWrite || prop.SetMethod?.IsPublic != true)
        continue;
    // ... rest of extraction
}
```

### Step 2: Handle non-generic AsyncCollectionResult/CollectionResult (Fix 4)

**`src/CliBuilder.Adapter.DotNet/DotNetAdapter.cs`** — `UnwrapAndBuild`:
```csharp
if (!type.IsGenericType)
{
    var name = type.Name;
    if (name == "Task" || name == "ValueTask")
        return (new TypeRef(TypeKind.Primitive, "void"), isStreaming);
    if (StreamingUnwrapTypes.Contains(name))
        return (new TypeRef(TypeKind.Primitive, "object"), isStreaming: true);
    if (UnwrapTypes.Contains(name))
        return (new TypeRef(TypeKind.Primitive, "object"), isStreaming);
}
```

### Step 3: Skip options class construction for non-instantiable types (Fix 1)

**`src/CliBuilder.Adapter.DotNet/DotNetAdapter.cs`** — `ExtractParameters`:
```csharp
if (typeRef.Kind == TypeKind.Class && !IsPrimitiveType(param.ParameterType))
{
    // Skip property extraction for abstract types and types without
    // a public parameterless constructor — can't construct them in handlers
    if (!param.ParameterType.IsAbstract && HasPublicParameterlessCtor(param.ParameterType))
    {
        var properties = ExtractClassProperties(param.ParameterType, depth: 0);
        typeRef = typeRef with { Properties = properties };
    }
}
```

Add helper:
```csharp
private static bool HasPublicParameterlessCtor(Type type)
{
    return type.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null) != null;
}
```

This means abstract classes and classes without parameterless ctors get `Properties = null`. The ParameterFlattener treats them as direct params (not flattened). The CLI accepts them as `string` (via `forCliParam: true`). The template passes the string value directly — this won't work at runtime but compiles. Future `--json-input` can handle proper deserialization.

### Step 4: Prefer minimal constructors (Fix 3)

**`src/CliBuilder.Adapter.DotNet/DotNetAdapter.cs`** — `ExtractConstructorAuthType`:
```csharp
private (string? TypeName, string? TypeNamespace) ExtractConstructorAuthType(Type type)
{
    // Sort by parameter count — prefer simpler constructors
    var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
        .OrderBy(c => c.GetParameters().Length);

    foreach (var ctor in constructors)
    {
        foreach (var param in ctor.GetParameters())
        {
            if (IsApiKeyCredentialParameter(param))
                return (param.ParameterType.Name, param.ParameterType.Namespace);
            if (IsCredentialParameter(param))
                return (param.ParameterType.Name, param.ParameterType.Namespace);
            if (IsApiKeyParameter(param))
                return ("string", null);
        }
    }
    return (null, null);
}
```

### Step 5: Handle unconvertible direct params (Fix 5)

**`src/CliBuilder.Generator.CSharp/GeneratorModel.cs`** — extend `OperationModel`:
```csharp
bool CanWireSdkCall = true  // false when any direct param is unconvertible
```

**`src/CliBuilder.Generator.CSharp/ModelMapper.cs`** — `BuildMethodParams`:
For each direct param, check if the CLI type `"string"` can be converted to the SDK type:
- Enum, TimeSpan, DateTime, Guid → convertible (has ConversionExpression)
- string, int, bool, etc. → convertible (identity)
- Generic (`IEnumerable<T>`), Class (without properties), Dictionary → unconvertible

If any param is unconvertible, set `CanWireSdkCall = false`.

**`ResourceCommands.sbn`** — update the SDK call condition:
```scriban
{{ if resource.source_class_name && op.source_method_name && op.can_wire_sdk_call }}
```

### Step 6: Regenerate fixtures and verify

After all adapter changes:
1. Regenerate `testsdk-metadata.json` (should be minimal changes — TestSdk has no abstract types)
2. Regenerate `openai-metadata.json`
3. Regenerate golden files
4. Run `GenerateOpenAi_Compiles` — target: 0 errors

---

## Tests

### Adapter tests
- `ReadOnlyProperty_IsExcluded` — property with no public setter not in extracted properties
- `InternalSetProperty_IsExcluded` — property with internal setter not in extracted properties
- `AbstractType_HasNoProperties` — abstract class param gets `Properties = null`
- `TypeWithRequiredCtor_HasNoProperties` — class with no parameterless ctor gets `Properties = null`
- `NonGenericAsyncCollectionResult_UnwrapsToObject` — return type kind is Primitive, name is "object", IsStreaming = true
- `NonGenericCollectionResult_UnwrapsToObject` — same for sync variant
- `ConstructorAuthType_PrefersMinimalConstructor` — for type with both 1-arg and 2-arg ctors, picks 1-arg
- `IsApiKeyParameter_ExactMatch_RejectsEncryptionKey` — `(string encryptionKey, string apiKey)` only matches apiKey (council P0)
- `TryClassifyAuthParam_PriorityOrder` — ApiKeyCredential > Credential > string apiKey

### Generator tests
- `MapOperation_UnconvertibleDirectParam_SetsCanWireSdkCallFalse`
- `MapOperation_UnconvertibleDirectParam_EmitsCB306Diagnostic`
- `Generate_WithUnconvertibleParam_FallsBackToEcho`
- `MapResource_NoAuthDetected_CanConstructIsFalse`

### Integration tests
- `GenerateOpenAi_Compiles` — the big one, must pass
- Fixture consistency: regenerated `openai-metadata.json` has no `BinaryContent` with properties

---

## Verification

```bash
# After each fix, re-run and count remaining errors:
dotnet test tests/CliBuilder.Integration.Tests --filter "GenerateOpenAi_Compiles" -v normal 2>&1 | grep "error CS" | wc -l

# After all fixes:
dotnet test  # full suite, all green including OpenAI compile
dotnet test --filter "MatchesGoldenFile"  # TestSdk golden files still match
./scripts/coverage.sh  # >= 80%
```

---

## Documentation + Cleanup (after compile fixes)

| File | Change |
|------|--------|
| `docs/cli-builder-spec.md` | Mark step 7 complete |
| `AGENTS.md` | Update status, point to step 8 |
| `docs/design-notes.md` | Add non-instantiable type policy, read-only property filtering, constructor preference |
| `README.md` | Update test count, note OpenAI validated |
| `docs/internal/step-07-wiring.md` | Add 7D council review section |

---

## Risk Assessment

**Fix 2 (read-only props):** Zero risk — strictly removes properties that can't be assigned.

**Fix 4 (non-generic unwrap):** Low risk — adds handling for a case that previously fell through. May reveal new downstream issues if methods returning non-generic `AsyncCollectionResult` have other problems.

**Fix 1 (abstract types):** Low risk — skipping property extraction for abstract types is correct. The parameters become plain `string` CLI options. Slight regression: `--json-input` loses the property schema for these types.

**Fix 3 (constructor ordering):** Medium risk — changing constructor iteration order could select a different constructor for some types. Must verify TestSdk still works (its constructors are single-arg, so ordering doesn't matter).

**Fix 5 (CanWireSdkCall):** Medium risk — introduces a new model field and template condition. Operations with unconvertible params fall back to echo, which is correct but reduces functionality. Need to count how many OpenAI operations become echo-only.

---

## Completion notes (2026-03-31)

Phase 7D completed with additional fixes discovered during live testing:

**Additional fixes applied after the initial 6:**
- **RequestOptions handling:** `System.ClientModel.Primitives.RequestOptions` is an infrastructure type. Property extraction skipped (bare Class → CanWireOperation detects as unconvertible). Overload selector excludes it from param count, preferring convenience methods over protocol methods. Constructing `RequestOptions()` with defaults causes `Value cannot be null` — must not be instantiated.
- **Value type properties not required:** `bool`, `int`, `enum` properties in options classes are never `Required` — they have implicit defaults and the CLI can't distinguish "not set" from "default".
- **CancellationToken properties skipped:** Same as method-level CancellationToken filtering, now applied to class properties too.
- **JsonFormatter fallback:** Added `IncludeFields` + `ToString()` fallback for SDK types with non-public properties (OpenAI SDK uses custom serialization patterns).

**Final stats:**
- 315 tests, all passing (49 Core + 240 Generator + 26 Integration)
- 82.7% line coverage, 94.6% method coverage
- OpenAI CLI: 20 resources, 169 operations, zero compile errors
- Validated live: `get-models` and `get-model` return real data from OpenAI API
