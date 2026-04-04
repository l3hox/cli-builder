# Step 9: `--json-input` Deserialization

**Prerequisite:** v1.0 complete. Generated CLIs make real SDK calls. `--json-input` option exists on commands but value is read and discarded. 338 tests, 83.8% coverage.
**Output:** `--json-input` deserializes JSON into options class properties. Flat flags override JSON values. Nested objects (Stripe `Recurring`, `ProductData`) become populatable. `CanWireSdkCall` relaxed for operations where JSON input covers complex params.

---

## Problem Statement

The `--json-input` option is declared on commands that need it (nested objects, threshold overflow) but the `jsonInputValue` variable is never used. This means:

1. **Nested Stripe objects don't work** — `price create --currency usd --json-input '{"recurring":{"interval":"month"}}'` reads the JSON but discards it. The `Recurring` property on `PriceCreateOptions` stays null.

2. **Overflow properties are inaccessible** — Operations with >10 scalar properties have some hidden behind `--json-input`, but users can't actually provide them.

3. **Complex direct params block operations** — 78 OpenAI operations with `IEnumerable<ChatMessage>` etc. are echo stubs because `CanWireSdkCall = false`. Some of these could be wired if `--json-input` deserialization existed.

### Scope

**In scope:**
- Deserialize `--json-input` JSON string into options class instances
- Merge: flat flags override JSON properties (specified in design-notes.md)
- Nested objects populated from JSON (`Recurring.Interval`, `ShippingAddress.City`)
- Overflow properties populated from JSON

**Out of scope (deferred):**
- Abstract type deserialization (`ChatMessage` — needs SDK-specific converters, step 11+)
- Direct param deserialization (`IEnumerable<ChatMessage>` — complex, deferred)
- The `CanWireSdkCall` relaxation for OpenAI operations with complex direct params

### Impact

- **Stripe:** All operations with nested objects become fully functional. Users can pass `--json-input '{"recurring":{"interval":"month"}}'` and have it populate the SDK options class.
- **TestSdk:** `order update` with `NestedOptions.ShippingAddress` becomes testable end-to-end.
- **OpenAI:** Limited — most blocked operations have complex direct params, not just nested options. The `--json-input` for options class properties will work, but the direct params remain as-is.

---

## Design

### How it works

The generated handler already has:
```csharp
var jsonInputValue = ctx.ParseResult.GetValueForOption(jsonInputOption);
```

After this line, we add:
```csharp
// 1. If --json-input provided, deserialize into the options class
if (jsonInputValue is not null)
{
    createCustomerOptions = JsonSerializer.Deserialize<CreateCustomerOptions>(jsonInputValue, jsonOptions)
        ?? createCustomerOptions;
}

// 2. Flat flags override individual properties on top
if (emailValue is not null)
    createCustomerOptions.Email = emailValue;
// ... etc (existing property assignments)
```

The key insight: **deserialize first, then overlay flat flags**. This means:
- `--json-input '{"email":"a@b.com","name":"Test"}' --name "Override"` → name="Override" (flag wins), email="a@b.com" (from JSON)
- `--json-input '{"recurring":{"interval":"month"}}'` → populates nested `Recurring.Interval` that has no flat flag

### Template changes

The merge logic goes into `ResourceCommands.sbn`. For each options class that the operation constructs:

```scriban
{{~ if op.needs_json_input }}
                    if (jsonInputValue is not null)
                    {
                        try
                        {
                            {{ mp.arg_expression }} = System.Text.Json.JsonSerializer.Deserialize<{{ mp.type_name }}>(jsonInputValue, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? {{ mp.arg_expression }};
                        }
                        catch (System.Text.Json.JsonException ex)
                        {
                            Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { error = new { code = "json_input_error", message = ex.Message } }));
                            ctx.ExitCode = 1;
                            return;
                        }
                    }
{{~ end }}
                    // Then flat flags override (existing assignments)
```

### Ordering: deserialize then override

For each options class parameter:
1. Construct empty: `var opts = new CreateOptions();`
2. If `--json-input`: deserialize into it (replaces the empty instance)
3. Apply flat flags: `if (emailValue is not null) opts.Email = emailValue;`

This means flat flag assignments need a null guard — currently they assign unconditionally (`opts.Email = emailValue`). After step 9, they must check: `if (emailValue is not null) opts.Email = emailValue;` — so the JSON-provided value isn't overwritten by a null flat flag.

### Null guards on flat flag assignments

**Current template (line ~84):**
```scriban
{{ mp.arg_expression }}.{{ param.property_name }} = {{ param.cli_flag | to_var_name | apply_conversion param.conversion_expression }};
```

**After step 9:**
```scriban
{{~ if param.csharp_type == "string" }}
                    if ({{ param.cli_flag | to_var_name }}Value is not null)
{{~ else if param.csharp_type ends_with "?" }}
                    if ({{ param.cli_flag | to_var_name }}Value is not null)
{{~ end }}
                        {{ mp.arg_expression }}.{{ param.property_name }} = {{ param.cli_flag | to_var_name | apply_conversion param.conversion_expression }};
```

For nullable types (`string`, `int?`, `bool?`), only assign if the user actually provided the flag. For non-nullable types (`int`, `bool`), always assign (they have implicit defaults and the user can't "not provide" them — System.CommandLine always gives a value).

### Which options class gets the JSON?

When an operation has multiple options classes (e.g., `CreateCustomerOptions` + `RequestOptions`), the `--json-input` applies to the **first** options class only. This matches the flattening behavior (first class is primary, second is infrastructure).

### Error handling

If `--json-input` contains invalid JSON:
- Emit structured error: `{"error":{"code":"json_input_error","message":"..."}}`
- Exit code 1 (user error)
- Do not proceed to SDK call

If `--json-input` contains valid JSON but wrong shape:
- `JsonSerializer.Deserialize<T>` returns null or a partially-populated object
- This is acceptable — the user gets what they asked for, properties they didn't provide stay at defaults
- No special error handling needed

---

## Implementation Plan

### Phase 9A: Template changes — deserialize + merge

**`src/CliBuilder.Generator.CSharp/Templates/ResourceCommands.sbn`:**

1. After `jsonInputValue` is read, add deserialization block for the first options class
2. Add null guards on flat flag assignments for nullable types
3. Add JSON error handling (try/catch, exit code 1)

**Changes:**
- The `{{~ for mp in op.method_params }}` loop that constructs options classes needs to be split:
  - First: construct empty instance
  - Second (new): if `--json-input`, deserialize and replace
  - Third: flat flag overrides with null guards

### Phase 9B: Add `using System.Text.Json` to template

The template already has `using System.Text.Json;` at the top (line 3). `JsonSerializerOptions` with `PropertyNameCaseInsensitive = true` is needed because CLI JSON input may use camelCase while SDK properties are PascalCase.

### Phase 9C: TestSdk E2E validation

**Implement TestSdk `order update` with `--json-input`:**
```bash
testsdk-cli order update --json-input '{"name":"Updated","shippingAddress":{"line1":"123 Main","city":"Springfield","country":"US"}}' --json --api-key test
```

The `NestedOptions.ShippingAddress` should be populated from JSON.

**New E2E tests:**
- `OrderUpdate_WithJsonInput_PopulatesNestedObject`
- `CustomerCreate_JsonInputMergedWithFlatFlags` — JSON provides email, flag overrides name
- `InvalidJsonInput_ExitsWithCode1`

### Phase 9D: Golden files + compile verification

- Regenerate golden files (null guards change all option assignments)
- Verify TestSdk compiles
- Verify OpenAI compiles
- Verify Stripe compiles

---

## Tests

### Unit tests
- `ParameterFlattenerTests`: Verify `NeedsJsonInput` triggers correctly (already covered)
- No new unit tests needed — the logic is in the template

### Generator tests
- `Generate_WithNestedObject_HasJsonInputDeserialization` — generated code contains `JsonSerializer.Deserialize`
- `Generate_FlatFlagAssignment_HasNullGuard` — nullable params have `if (x is not null)`

### Integration tests (E2E)
- `OrderUpdate_WithJsonInput_PopulatesNestedObject` — ShippingAddress from JSON appears in output
- `CustomerCreate_JsonInputMergedWithFlatFlags` — JSON email + flag name → both in output
- `CustomerCreate_FlatFlagOverridesJsonInput` — JSON name + flag name → flag wins
- `InvalidJsonInput_ExitsWithCode1` — malformed JSON → exit 1, structured error
- `JsonInput_WithoutFlatFlags_WorksAlone` — only --json-input, no flat flags

### Compile verification
- `GenerateOpenAi_Compiles` — must still pass
- `GenerateStripe_Compiles` — must still pass

---

## Files to modify

| File | Change |
|------|--------|
| `src/CliBuilder.Generator.CSharp/Templates/ResourceCommands.sbn` | Deserialization block, null guards, error handling |
| `tests/golden/testsdk-cli/Commands/*.cs` | Regenerated with null guards + deserialization |
| `tests/CliBuilder.Generator.Tests/CSharpCliGeneratorTests.cs` | New tests for JSON deserialization in output |
| `tests/CliBuilder.Integration.Tests/GeneratedCliTests.cs` | E2E tests for --json-input |
| `tests/CliBuilder.TestSdk/Models/Options.cs` | May need Address properties settable |
| `tests/CliBuilder.TestSdk/Services/OrderClient.cs` | UpdateAsync returns populated Address |
| `tests/fixtures/*.json` | Regenerated |

---

## Verification

```bash
# Phase 9A: template changes compile
dotnet test --filter "CompilesWithDotnetBuild"

# Phase 9C: E2E tests
dotnet test --filter "JsonInput"

# Phase 9D: all SDKs compile
dotnet test --filter "Compiles"

# Full suite
dotnet test

# Manual test against Stripe
STRIPE_API_KEY=sk_test_... dotnet run --project /tmp/stripe-cli-demo/stripe-cli --no-build -- \
  price create --currency usd --product prod_xxx \
  --json-input '{"recurring":{"interval":"month"}}' --json
```

---

## Risk Assessment

**Template complexity:** Medium. The deserialization block adds ~10 lines per options class in the template. The null guards change every flat flag assignment. Golden files will change significantly.

**Backward compatibility:** Low risk. Existing behavior (no `--json-input` provided) is identical — the deserialization block only fires when the value is non-null. Operations without `--json-input` are unaffected.

**JSON type mismatch:** Medium risk. If the user's JSON property types don't match the SDK options class (e.g., string where int expected), `JsonSerializer.Deserialize` may throw or silently skip. The error handling (exit code 1) covers the throw case. Silent skips are acceptable — partial population is by design.

**Abstract types in JSON:** Not addressed. If the SDK options class has an abstract property (e.g., `ChatToolChoice`), deserialization may fail. This is expected and documented as a known limitation. The error handling catches it gracefully.
