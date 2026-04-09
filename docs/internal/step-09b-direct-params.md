# Step 9B: Direct Param Deserialization + Infrastructure Param Fix

**STATUS: COMPLETE (2026-04-08)** — 386 tests (385 pass). Deferred items in FUTURE.md.

**Prerequisite:** Steps 1-9 complete. `--json-input` works for options classes (nested objects, flat flag override, null guards). 347 tests, 93.4% coverage. 41/169 OpenAI operations wired.
**Output:** Complex direct params (IEnumerable<T>, Dictionary<K,V>, Array, bare Class) deserializable via `--json-input`. RequestOptions always filtered. OpenAI wired operations: 41 → ~80+.

---

## Problem Statement

88 of 169 OpenAI operations fall back to echo stubs. The current `CanWireOperation` gate rejects any operation with a complex direct parameter — a parameter that isn't an options class and isn't a primitive/enum. Three categories:

### Category 1: Infrastructure params not filtered (35 ops)

`RequestOptions` is an infrastructure type (like `CancellationToken`) but is only filtered when `param.HasDefaultValue` is true. When it's required (no default), it leaks through as a bare Class param, and `CanWireOperation` rejects the entire operation. CancellationToken is always filtered unconditionally — RequestOptions should be too.

Blocked operations example: `chat.get-chat-completions`, `vector-store.get-vector-stores`, `fine-tuning.get-jobs` — 35 operations total, all blocked by a parameter the CLI user should never touch.

### Category 2: JSON-deserializable direct params (16 ops)

Operations like `chat.complete-chat` have params like `IEnumerable<ChatMessage>` or `IEnumerable<string>`. These can't be expressed as flat CLI flags but CAN be deserialized from JSON. Currently, the entire operation is echo-stubbed.

| Inner type pattern | Count | Examples |
|---|---|---|
| `IEnumerable<string>` | 2 | `embedding.generate-embeddings`, `vector-store.add-file-batch` |
| `IEnumerable<SdkClass>` | 11 | `chat.complete-chat`, `responses.create-response` |
| `IDictionary<string,string>` | 3 | `chat.update-chat-completion` |

### Category 3: Binary/stream types (31 ops — OUT OF SCOPE)

`BinaryContent` (22 ops) and `Stream` (9 ops) are binary upload types. They need a `--file` flag pattern, not JSON deserialization. Deferred to a future step.

### Category 4: Other bare classes (6 ops — partial)

`GetResponseOptions`, `RealtimeItem`, `GeneratedSpeechVoice`, etc. Some are concrete and JSON-deserializable, some are not.

---

## Impact Analysis

| Phase | Change | Ops unblocked |
|---|---|---|
| A: Always filter infrastructure params | Adapter fix | +35 |
| B: Direct param `--json-input` | Generator + template | +5 to +16 (depends on abstract types) |
| **Total** | | **~41 → ~80+ wired** |

Note: 35 operations are also blocked by return type issues (AsyncCollectionResult, sub-client factories). These are a separate concern and remain echo-stubbed regardless of param fixes.

---

## Design

### Phase A: Always Filter Infrastructure Params

**File:** `DotNetAdapter.cs` line 382

Change:
```csharp
// Before:
if (IsInfrastructureParam(param) && param.HasDefaultValue)
    continue;

// After:
if (IsInfrastructureParam(param))
    continue;
```

This matches the CancellationToken behavior (line 377, unconditional skip). The CLI user never provides RequestOptions — it's SDK plumbing.

**Risk:** Low. The overload disambiguator already excludes infrastructure params from the count, so removing them from the parameter list won't affect which overload is selected.

### Phase B: IsAbstract Flag in TypeRef

**File:** `TypeRef.cs`

Add `IsAbstract` field:
```csharp
public record TypeRef(
    TypeKind Kind,
    string Name,
    bool IsNullable = false,
    bool IsAbstract = false,     // NEW — type cannot be instantiated directly
    IReadOnlyList<TypeRef>? GenericArguments = null,
    // ...
);
```

**File:** `DotNetAdapter.cs`, `BuildTypeRef` method

Set `IsAbstract = true` when `type.IsAbstract || type.IsInterface`. This applies in the Class branch:
```csharp
// Class (including abstract types)
return new TypeRef(TypeKind.Class, type.Name, Namespace: type.Namespace,
    IsAbstract: type.IsAbstract || type.IsInterface);
```

Note: `IsAbstract` is set on the *inner* type (e.g., `ChatMessage`), not on the generic wrapper (`IEnumerable`). The CB307 diagnostic in Phase C1 must therefore check `GenericArguments[].IsAbstract`, not the outer type's `IsAbstract`.

**Also in `BuildTypeRef`:** Fix the Dictionary branch to preserve generic arguments. Currently (line 558-559), `BuildTypeRef` constructs `TypeRef(TypeKind.Dictionary, "Dictionary")` with **no `GenericArguments`**, discarding K and V type info. This makes it impossible for `BuildDeserializationTypeName` to produce `Dictionary<string,string>`.

```csharp
// Before (line 558-559):
if (genericName == "Dictionary" && args.Length == 2)
    return new TypeRef(TypeKind.Dictionary, "Dictionary");

// After:
if (genericName == "Dictionary" && args.Length == 2)
    return new TypeRef(TypeKind.Dictionary, "Dictionary",
        GenericArguments: args.Select(a => BuildTypeRef(a, depth + 1)).ToList());
```

Apply the same fix to the `UnwrapAndBuild` Dictionary branch (line ~522).

**Purpose:** Enables the generator to distinguish `IEnumerable<string>` (definitely deserializable) from `IEnumerable<ChatMessage>` (abstract — deserialization depends on SDK converters). Both compile and are attempted, but the abstract case gets a diagnostic warning. Also enables `BuildDeserializationTypeName` to reconstruct `Dictionary<K,V>` with correct type arguments.

### Phase C: Direct Param `--json-input`

This is the core feature. When an operation has complex direct params, `--json-input` accepts a JSON object where **param names are keys**:

```bash
# IEnumerable<ChatMessage> direct param + ChatCompletionOptions options class
my-cli chat complete-chat \
  --json-input '{"messages":[{"role":"user","content":"hello"}],"temperature":0.7}' \
  --api-key $KEY
```

The JSON is parsed once. Each complex direct param extracts its named key. The options class deserializes from the full JSON (ignoring unknown keys like `messages`).

#### C1: Relax CanWireOperation

**File:** `ModelMapper.cs`, `CanWireOperation` method

Current logic rejects all Generic/Array/Dictionary/bare-Class direct params. New logic:

```csharp
if (p.Type.Kind is TypeKind.Generic or TypeKind.Array or TypeKind.Dictionary)
{
    // JSON-deserializable — allow, will use --json-input
    hasComplexDirectParams = true;
    continue;
}
if (p.Type.Kind == TypeKind.Class && p.Type.Properties == null)
{
    // Bare class — allow if not a binary type
    if (IsBinaryType(p.Type.Name))
    {
        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "CB306",
            $"Operation '{operation.Name}' has binary parameter '{p.Name}' ({p.Type.Name}) " +
            "— falling back to echo stub"));
        return false;
    }
    hasComplexDirectParams = true;
    continue;
}
```

Where `IsBinaryType` checks for: `BinaryContent`, `BinaryData`, `Stream`, `ReadOnlyMemory`, `ReadOnlySpan`.

**Abstract type diagnostic (CB307):** When a complex direct param contains an abstract type, emit a CB307 Info diagnostic. The check must inspect **`GenericArguments`**, not the outer type — `IEnumerable<ChatMessage>` resolves to `TypeKind.Generic` with `IsAbstract = false`, but `GenericArguments[0]` (ChatMessage) has `IsAbstract = true`.

Detection logic in `CanWireOperation`:
```csharp
// After allowing the complex param, check for abstract inner types
if (p.Type.GenericArguments?.Any(ga => ga.IsAbstract) == true
    || (p.Type.Kind == TypeKind.Class && p.Type.IsAbstract))
{
    var innerName = p.Type.GenericArguments?.FirstOrDefault(ga => ga.IsAbstract)?.Name ?? p.Type.Name;
    diagnostics.Add(new Diagnostic(DiagnosticSeverity.Info, "CB307",
        $"Operation '{operation.Name}' has abstract parameter '{p.Name}' ({innerName}) " +
        "— deserialization requires SDK-registered JsonConverters"));
}
```

Example output:
```
[INFO]  CB307  Operation 'complete-chat' has abstract parameter 'messages' (ChatMessage)
              — deserialization requires SDK-registered JsonConverters
```

This is informational, not a blocker. The generated code compiles regardless — `JsonSerializer.Deserialize<List<ChatMessage>>()` is valid syntax for any T. At runtime, it either works (SDK has converters) or throws JsonException (caught and reported to user).

#### C2: Extend MethodParamModel and OperationModel

**File:** `GeneratorModel.cs`

Add fields to `MethodParamModel`:
```csharp
public record MethodParamModel(
    string ArgExpression,
    string? TypeName,
    string? Namespace,
    bool IsOptionsClass,
    bool NeedsJsonDeserialization = false,    // NEW: complex direct param
    string? DeserializationTypeName = null,   // NEW: "List<ChatMessage>"
    string? JsonPropertyName = null,          // NEW: "messages" (key in --json-input)
    bool IsRequired = false                   // NEW: required param → null guard in template
);
```

Add field to `OperationModel`:
```csharp
public record OperationModel(
    // ... existing fields ...
    bool HasJsonDirectParams = false   // NEW: true when any MethodParam has NeedsJsonDeserialization
);
```

`HasJsonDirectParams` is set in `MapOperation` alongside `NeedsJsonInput` and gates the `_jsonInputDoc` parse-once block in the template.

#### C3: Update BuildMethodParams

**File:** `ModelMapper.cs`, `BuildMethodParams` method

For complex direct params (Generic/Array/Dictionary/bare Class that passed CanWireOperation):

```csharp
else if (p.Type.Kind is TypeKind.Generic or TypeKind.Array or TypeKind.Dictionary
         || (p.Type.Kind == TypeKind.Class && p.Type.Properties == null))
{
    var deserTypeName = BuildDeserializationTypeName(p.Type);
    methodParams.Add(new MethodParamModel(
        ArgExpression: KebabToCamelCase(cliFlag) + "Value",
        TypeName: deserTypeName,
        Namespace: p.Type.Namespace,
        IsOptionsClass: false,
        NeedsJsonDeserialization: true,
        DeserializationTypeName: deserTypeName,
        JsonPropertyName: p.Name));
}
```

`BuildDeserializationTypeName` maps:
- `IEnumerable<T>` → `List<T>` (concrete collection for deserialization)
- `IReadOnlyList<T>` → `List<T>`
- `IList<T>` → `List<T>`
- `IDictionary<K,V>` → `Dictionary<K,V>`
- `T[]` → `T[]` (arrays are already concrete)
- Bare Class → the class name directly

#### C4: Set NeedsJsonInput for complex direct params

**File:** `ModelMapper.cs`, `MapOperation` method

After the flattener runs:
```csharp
var needsJsonInput = flattenResult.NeedsJsonInput;
// Also enable --json-input if any direct params need JSON deserialization
if (methodParams.Any(mp => mp.NeedsJsonDeserialization))
    needsJsonInput = true;
```

#### C5: Template changes

**File:** `ResourceCommands.sbn`

Add direct param deserialization block inside the SDK call branch, after `jsonInputValue` is read:

```scriban
{{~ for mp in op.method_params ~}}
{{~ if mp.needs_json_deserialization }}
                    // Direct param: {{ mp.json_property_name }}
                    {{ mp.deserialization_type_name }} {{ mp.arg_expression }} = default!;
                    if (jsonInputValue is not null)
                    {
                        try
                        {
                            var jsonDoc = JsonDocument.Parse(jsonInputValue);
                            if (jsonDoc.RootElement.TryGetProperty("{{ mp.json_property_name }}", out var {{ mp.json_property_name }}Prop))
                            {
                                {{ mp.arg_expression }} = JsonSerializer.Deserialize<{{ mp.deserialization_type_name }}>({{ mp.json_property_name }}Prop.GetRawText(), _jsonInputOptions)!;
                            }
                        }
                        catch (JsonException ex)
                        {
                            var jsonError = new { error = new { code = "json_input_error", message = ex.Message } };
                            Console.Error.WriteLine(JsonSerializer.Serialize(jsonError));
                            ctx.ExitCode = 1;
                            return;
                        }
                    }
{{~ else if mp.is_options_class }}
                    // (existing options class deserialization — unchanged)
{{~ end ~}}
{{~ end ~}}
```

**Important:** the `JsonDocument.Parse(jsonInputValue)` is repeated for each direct param. For efficiency with multiple direct params, parse once:

```scriban
{{~ if op.has_json_direct_params }}
                    using var _jsonInputDoc = jsonInputValue is not null
                        ? (Func<JsonDocument?>)(() => { try { return JsonDocument.Parse(jsonInputValue); }
                            catch (JsonException ex) {
                                var jsonError = new { error = new { code = "json_input_error", message = ex.Message } };
                                Console.Error.WriteLine(JsonSerializer.Serialize(jsonError));
                                ctx.ExitCode = 1; return null; } })()
                        : null;
                    if (jsonInputValue is not null && _jsonInputDoc is null) return;
{{~ end ~}}
{{~ for mp in op.method_params ~}}
{{~ if mp.needs_json_deserialization }}
                    {{ mp.deserialization_type_name }} {{ mp.arg_expression }} = default!;
                    if (_jsonInputDoc is not null
                        && _jsonInputDoc.RootElement.TryGetProperty("{{ mp.json_property_name }}", out var {{ mp.json_property_name }}Prop))
                    {
                        {{ mp.arg_expression }} = JsonSerializer.Deserialize<{{ mp.deserialization_type_name }}>({{ mp.json_property_name }}Prop.GetRawText(), _jsonInputOptions)!;
                    }
{{~ if mp.is_required }}
                    if ({{ mp.arg_expression }} is null)
                    {
                        var missingError = new { error = new { code = "missing_required_param",
                            message = "Required parameter '{{ mp.json_property_name }}' must be provided via --json-input" } };
                        Console.Error.WriteLine(JsonSerializer.Serialize(missingError));
                        ctx.ExitCode = 1;
                        return;
                    }
{{~ end ~}}
{{~ end ~}}
{{~ end ~}}
```

Note: `using var` on `_jsonInputDoc` ensures the `JsonDocument` is disposed after use. The `is_required` guard on `MethodParamModel` (derived from the source `Parameter.Required` flag) prevents `default!` null values from reaching the SDK call — instead, a structured `missing_required_param` error is emitted with exit code 1.

#### C6: Required namespaces

Complex direct params may need their type's namespace in the `using` directives. `BuildDeserializationTypeName` must include full type info so `RequiredNamespaces` on the ResourceModel picks up the namespace.

For example, `List<ChatMessage>` needs `using OpenAI.Chat;`. The generic argument's namespace must be collected.

---

## Backwards Compatibility

### Options-class-only operations (step 9 behavior): UNCHANGED

For operations with only an options class and no complex direct params, `--json-input` behavior is identical:
```bash
# Step 9 behavior — still works identically
my-cli order update --json-input '{"name":"Updated","shippingAddress":{"line1":"123 Main"}}'
```

### Mixed operations (new in 9B): additive

For operations with both direct params and an options class:
```bash
# Direct param key + options class properties in same JSON
my-cli chat complete-chat --json-input '{"messages":[...],"temperature":0.7}'
```

The options class deserializer uses `PropertyNameCaseInsensitive = true` and ignores unknown properties (like `messages`). Direct params extract their named key. No conflict.

---

## TestSdk Additions

### New model: `Message` (abstract base with concrete subclasses)

**File:** `tests/CliBuilder.TestSdk/Models/Message.cs`

```csharp
[JsonDerivedType(typeof(UserMessage), "user")]
[JsonDerivedType(typeof(SystemMessage), "system")]
public abstract class Message
{
    public string Content { get; set; } = "";
}

public class UserMessage : Message { }
public class SystemMessage : Message { }
```

The `[JsonDerivedType]` attribute enables `JsonSerializer.Deserialize<List<Message>>()` to work with `{"$type":"user","content":"hello"}` JSON.

### New service: `MessageClient`

**File:** `tests/CliBuilder.TestSdk/Services/MessageClient.cs`

```csharp
public class MessageClient
{
    public MessageClient(string apiKey) { }

    // IEnumerable<AbstractType> + options class → both via --json-input
    public Task<ClientResult<Order>> SendAsync(
        IEnumerable<Message> messages,
        SendMessageOptions? options = null,
        CancellationToken ct = default)
        => Task.FromResult(new ClientResult<Order>
        {
            Value = new Order { Id = "msg_001", Name = $"Sent {messages.Count()} messages" }
        });

    // IEnumerable<string> direct param → simple concrete case
    public Task<ClientResult<Order>> BatchAsync(
        IEnumerable<string> ids,
        CancellationToken ct = default)
        => Task.FromResult(new ClientResult<Order>
        {
            Value = new Order { Id = "batch_001", Name = string.Join(",", ids) }
        });
}

public class SendMessageOptions
{
    public string? Model { get; set; }
    public float? Temperature { get; set; }
}
```

---

## Tests

### Existing tests to update (4 inversions)

These existing tests in `ModelMapperTests.cs` will break after Phase C1. They must be updated:

| Current test (line) | Current assertion | New assertion | Why |
|---|---|---|---|
| `CanWireSdkCall_GenericDirectParam_ReturnsFalse` (~719) | `Assert.False` | `Assert.True` | Generic direct params now allowed via --json-input |
| `CanWireSdkCall_ArrayDirectParam_ReturnsFalse` (~729) | `Assert.False` | `Assert.True` | Array direct params now allowed via --json-input |
| `CanWireSdkCall_DictionaryDirectParam_ReturnsFalse` (~741) | `Assert.False` | `Assert.True` | Dictionary direct params now allowed via --json-input |
| `CanWireSdkCall_BareClassDirectParam_ReturnsFalse` (~751) | stays `Assert.False` | **Rename** to `CanWireSdkCall_BinaryContentParam_ReturnsFalse` | Still false, but now testing the `IsBinaryType` guard, not the generic bare-class path |

Also add a new test: `CanWireSdkCall_NonBinaryBareClass_ReturnsTrue` — bare class that is NOT in the binary denylist should now return true.

### New unit tests (ModelMapper)

| Test | What it verifies |
|---|---|
| `CanWireSdkCall_GenericConcreteParam_ReturnsTrue` | IEnumerable<string> no longer blocks |
| `CanWireSdkCall_GenericAbstractParam_ReturnsTrue_WithCB307` | IEnumerable<AbstractClass> allowed, emits CB307 diagnostic |
| `CB307_GenericWithAbstractArgument_EmitsDiagnostic` | CB307 fires when GenericArguments[0].IsAbstract=true |
| `CB307_GenericWithConcreteArgument_NoDiagnostic` | CB307 does NOT fire for IEnumerable<string> |
| `CanWireSdkCall_DictionaryParam_ReturnsTrue` | IDictionary<K,V> allowed |
| `CanWireSdkCall_BinaryContentParam_ReturnsFalse` | BinaryContent still blocks |
| `CanWireSdkCall_StreamParam_ReturnsFalse` | Stream still blocks |
| `BuildMethodParams_GenericParam_SetsNeedsJsonDeserialization` | MethodParamModel flags set correctly |
| `BuildMethodParams_GenericParam_DeserializationTypeName_UsesList` | IEnumerable<T> → List<T> |
| `BuildMethodParams_DictionaryParam_DeserializationTypeName` | IDictionary<K,V> → Dictionary<K,V> (requires GenericArguments) |
| `NeedsJsonInput_ComplexDirectParam_ReturnsTrue` | Operation gets --json-input from direct params |
| `HasJsonDirectParams_SetWhenDirectParamPresent` | OperationModel.HasJsonDirectParams set correctly |

### Unit tests (TypeRef IsAbstract)

| Test | What it verifies |
|---|---|
| `BuildTypeRef_AbstractClass_SetsIsAbstract` | IsAbstract = true for abstract types |
| `BuildTypeRef_ConcreteClass_IsAbstractFalse` | IsAbstract = false for concrete types |
| `BuildTypeRef_Interface_SetsIsAbstract` | Interfaces treated as abstract |
| `BuildTypeRef_Dictionary_PreservesGenericArguments` | Dictionary TypeRef has K,V in GenericArguments |

### E2E tests (GeneratedCliTests)

| Test | What it verifies |
|---|---|
| `MessageBatch_WithJsonInput_DeserializesStringList` | `--json-input '{"ids":["a","b"]}'` → IEnumerable<string> populated |
| `MessageBatch_EmptyArray_Succeeds` | `--json-input '{"ids":[]}'` → empty IEnumerable<string>, no error |
| `MessageSend_WithJsonInput_DeserializesAbstractList` | `--json-input '{"messages":[{"$type":"user","content":"hi"}]}'` → IEnumerable<Message> populated |
| `MessageSend_MixedJsonInput_PopulatesBothParamsAndOptions` | `--json-input '{"messages":[...],"temperature":0.7}'` → both messages and options set |
| `MessageSend_FlatFlagOverridesOptions_NotDirectParam` | `--json-input '{"messages":[...],"temperature":0.5}' --temperature 0.9` → 0.9 wins for options, messages unchanged |
| `MessageSend_MissingRequiredDirectParam_ExitsWithError` | No `--json-input` when messages required → `missing_required_param` error, exit code 1 |
| `MessageSend_JsonInputMissingKey_ExitsWithError` | `--json-input '{"temperature":0.7}'` (no "messages" key) → `missing_required_param` error |
| `MessageSend_NullJsonValue_ExitsWithError` | `--json-input '{"messages":null}'` → `missing_required_param` error |
| `MessageBatch_TypeMismatch_ExitsWithJsonError` | `--json-input '{"ids":"not-an-array"}'` → `json_input_error` |

### Integration test (OpenAI)

| Test | What it verifies |
|---|---|
| `OpenAI_PreviouslyEchoStubbed_NowWired` | Operations that were CB306 are now CanWireSdkCall=true |
| `OpenAI_InfraParamsFiltered_WireCount` | Pin exact wired count after fixture regeneration (was 41) |

### Integration test (Stripe)

| Test | What it verifies |
|---|---|
| `Stripe_NoRegressions_WireCount` | Stripe wired count unchanged (no regressions) |

### Namespace collection test

| Test | What it verifies |
|---|---|
| `RequiredNamespaces_IncludesGenericArgumentNamespace` | `List<ChatMessage>` causes `OpenAI.Chat` to appear in RequiredNamespaces |

---

## Verification

```bash
# Run all tests
dotnet test

# Check OpenAI wire count improvement
dotnet test --filter "OpenAI" -v normal

# Generate TestSdk CLI and test new operations
dotnet test --filter "GeneratedCli" -v normal

# Regenerate fixtures and verify compile
dotnet test --filter "Stripe" -v normal
dotnet test --filter "OpenAI" -v normal
```

---

## Files to create/modify

| File | Change |
|---|---|
| `src/CliBuilder.Core/Models/TypeRef.cs` | Add `IsAbstract` field |
| `src/CliBuilder.Adapter.DotNet/DotNetAdapter.cs` | Always filter infrastructure params; set IsAbstract in BuildTypeRef; preserve Dictionary GenericArguments in both `BuildTypeRef` (line 558) and `UnwrapAndBuild` (line 522) |
| `src/CliBuilder.Generator.CSharp/GeneratorModel.cs` | Extend `MethodParamModel` with JSON deserialization fields (`NeedsJsonDeserialization`, `DeserializationTypeName`, `JsonPropertyName`, `IsRequired`); add `HasJsonDirectParams` to `OperationModel` |
| `src/CliBuilder.Generator.CSharp/ModelMapper.cs` | Relax CanWireOperation (CB307 checks GenericArguments), update BuildMethodParams, set NeedsJsonInput + HasJsonDirectParams for direct params |
| `src/CliBuilder.Generator.CSharp/Templates/ResourceCommands.sbn` | Direct param deserialization block with parse-once `using var _jsonInputDoc`, required-param null guard |
| `tests/CliBuilder.TestSdk/Models/Message.cs` | NEW: abstract Message + UserMessage + SystemMessage |
| `tests/CliBuilder.TestSdk/Services/MessageClient.cs` | NEW: service with IEnumerable direct params |
| `tests/CliBuilder.TestSdk/Models/Options.cs` | Add SendMessageOptions |
| `tests/CliBuilder.Generator.Tests/ModelMapperTests.cs` | Update 4 inverting tests; add new unit tests for relaxed wiring, CB307 diagnostics, Dictionary GenericArguments |
| `tests/CliBuilder.Integration.Tests/GeneratedCliTests.cs` | New E2E tests for direct param deserialization, null guards, edge cases |

---

## Implementation Order

1. **Phase A** — Infrastructure param fix in DotNetAdapter (1 line change + tests)
2. **Phase B** — IsAbstract flag in TypeRef + DotNetAdapter (small, enables Phase C diagnostics)
3. **Phase C1-C2** — CanWireOperation relaxation + MethodParamModel extension
4. **Phase C3** — BuildMethodParams update for complex direct params
5. **Phase C4** — NeedsJsonInput for direct params
6. **Phase C5-C6** — Template changes + namespace collection
7. **TestSdk additions** — Message model + MessageClient service
8. **Tests** — unit + E2E + integration verification
9. **Fixture regeneration** — OpenAI + Stripe fixtures with new wiring counts

---

## Risk

**Low-medium.** Phase A is trivial (1 line). Phase B is additive (new field, no behavior change). Phase C is the substantial change — template modifications for direct param deserialization.

Key risks:
- **Template correctness** — the `JsonDocument.Parse` + `TryGetProperty` pattern is new. The `using var` parse-once block, required-param null guard, and edge case tests (null values, missing keys, type mismatches) mitigate this.
- **Namespace collection** — generic arguments' namespaces must be collected for `using` directives. Missing a namespace = compile error in generated code. Covered by `RequiredNamespaces_IncludesGenericArgumentNamespace` test.
- **Abstract type runtime behavior** — `JsonSerializer.Deserialize<List<ChatMessage>>()` compiles but may throw at runtime if the SDK doesn't register JsonConverters. This is by design (fail gracefully with `json_input_error`). CB307 diagnostic fires based on `GenericArguments[].IsAbstract`, not the outer type.
- **Test inversions** — 4 existing tests change behavior (3 invert to true, 1 rename). Explicitly tracked in "Existing tests to update" section.
- **Fixture size** — regenerating OpenAI fixture with more wired operations may increase fixture file size.

---

## What this does NOT solve (future steps)

- **Binary upload** (BinaryContent/Stream) — needs `--file` flag, not JSON. Note: `ReadOnlyMemory<T>` and `ReadOnlySpan<T>` are `TypeKind.Generic` and escape the `IsBinaryType` check (which only covers `TypeKind.Class`). These are rare in current SDKs but should be handled when binary upload is implemented.
- **AsyncCollectionResult returns** — pagination/collection types need separate handling
- **Sub-client factory returns** — ChatClient, EmbeddingClient etc. are factory patterns
- **SDK-specific JsonConverters** — if an SDK doesn't register converters for abstract types, deserialization fails at runtime. Future: detect SDK serializer options or provide custom converter registration.
- **Nested generics** — `IEnumerable<IEnumerable<string>>` or `IDictionary<string, List<T>>`. `BuildDeserializationTypeName` produces a flat type name (no recursive resolution). If encountered, `JsonSerializer` will throw at runtime, caught cleanly as `json_input_error`. Not seen in current OpenAI/Stripe SDKs.
