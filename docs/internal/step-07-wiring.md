# Step 7: Wire Real SDK Method Calls in Generated Handlers

**Prerequisite:** Step 6 complete — generator produces compilable CLI projects from SdkMetadata. Validated against OpenAI SDK (20 resources, 169 ops, zero errors). 227 tests, 80.6% coverage.
**Output:** Generated command handlers call real SDK methods instead of echoing parameters. Generated CLI is functional end-to-end: authenticates, calls SDK, formats real output. Validated against TestSdk and (optionally) a live API.

Split into 4 phases with checkpoints between each.

---

## Context

Read before implementing:
- [cli-builder-spec.md](../cli-builder-spec.md) — success criteria (lines 460-468), agent-readiness requirements (lines 333-368), first actions step 7 (line 456)
- [docs/design-notes.md](../design-notes.md) — auth generation contract (lines 28-49), `--json-input` behavior (lines 125-133), exit codes, credential masking
- [docs/ADR.md](../ADR.md) — ADR-006 (generated CLI wraps SDK, no reimplementation)
- [docs/internal/step-06-generator.md](step-06-generator.md) — Phase 6C handler wiring contract (lines 740-753)

### What exists today

The generated handler template (`ResourceCommands.sbn`) has **two parallel code paths**:

1. **Commented SDK call stubs** — show the intended wiring but are commented out:
   ```csharp
   // var client = new CustomerService(credential);
   // var sdkOptions = new CreateCustomerOptions();
   // sdkOptions.Email = emailValue;
   // var result = await client.CreateAsync(sdkOptions);
   ```

2. **Active echo fallback** — constructs a dictionary of parameter values:
   ```csharp
   await Task.CompletedTask;
   var result = (object)new Dictionary<string, object?> {
       ["command"] = "customer create",
       ["parameters"] = { ["Email"] = emailValue, ... },
       ["authenticated"] = true
   };
   ```

Step 7 replaces the echo fallback with real SDK calls.

### Six gaps to close

| # | Gap | Problem | Solution |
|---|-----|---------|----------|
| 1 | **Constructor auth type** | `CustomerService(string apiKey)` vs `ProductApi(TokenCredential credential)`. Template always passes raw string. | Add per-resource `ConstructorAuthTypeName` to metadata. ModelMapper computes auth expression. |
| 2 | **Options class namespaces** | Options classes live in different namespaces than service classes (e.g., `TestSdk.Models` vs `TestSdk.Services`). Template only emits `using {resource.source_namespace}`. | Add `TypeRef.Namespace`. Collect all namespaces into `RequiredNamespaces`. |
| 3 | **Type conversion** | `FlatParameter.CSharpType` is the CLI type (`string` for enums/TimeSpan). SDK property types differ (`CustomerStatus?`, `TimeSpan?`). | Compute `ConversionExpression` per parameter (e.g., `Enum.Parse<T>`, `TimeSpan.Parse`). |
| 4 | **Multi-options-class tracking** | `ParameterFlattener` merges all class properties into one list, losing which class each belongs to. | Add `SourceOptionsClassName` to `FlatParameter`. |
| 5 | **Method call reconstruction** | Template needs ordered argument list: options class vars vs direct values. | Add `MethodParamModel` list to `OperationModel`. |
| 6 | **Streaming** | `IAsyncEnumerable<T>` operations need `await foreach`, not simple `await`. | Branch on `IsStreaming` in template. |

### Design decisions

1. **Type conversions pre-computed in ModelMapper** — not template conditionals. `FlatParameter.ConversionExpression` is a format string with `{0}` placeholder. Template stays simple.
2. **`--json-input` deserialization deferred** to step 8. Flat scalar flags work for all flattened parameters. The `--json-input` option remains on the command but is not wired to deserialization yet.
3. **Method parameter order** from `Operation.Parameters` (preserves reflection order from the adapter).
4. **Options class variable names** derived from type name via `PascalToCamelCase`: `CreateCustomerOptions` -> `createCustomerOptions`.
5. **TestSdk methods return hardcoded data** for deterministic test assertions.
6. **Echo fallback preserved** for operations where `SourceClassName` is null — graceful degradation.

---

## Phase 7A: Metadata & Model Enrichment

**Goal:** Enrich the data pipeline so templates have everything needed for real SDK calls. No template changes yet. All existing tests must continue to pass (backward-compatible defaults on all new fields).

### Core model changes

**`src/CliBuilder.Core/Models/TypeRef.cs`** — add `string? Namespace = null`:
```csharp
public record TypeRef(
    TypeKind Kind, string Name,
    bool IsNullable = false,
    IReadOnlyList<TypeRef>? GenericArguments = null,
    IReadOnlyList<string>? EnumValues = null,
    IReadOnlyList<Parameter>? Properties = null,
    TypeRef? ElementType = null,
    string? Namespace = null      // NEW: e.g., "CliBuilder.TestSdk.Models"
);
```

**`src/CliBuilder.Core/Models/Resource.cs`** — add constructor auth info:
```csharp
public record Resource(
    string Name, string? Description,
    IReadOnlyList<Operation> Operations,
    string? SourceClassName = null,
    string? SourceNamespace = null,
    string? ConstructorAuthTypeName = null,       // NEW: "string", "TokenCredential", "ApiKeyCredential"
    string? ConstructorAuthTypeNamespace = null    // NEW: "CliBuilder.TestSdk.Models", "System.ClientModel"
);
```

### Adapter changes

**`src/CliBuilder.Adapter.DotNet/DotNetAdapter.cs`**:

1. In `BuildTypeRef`, add `Namespace: type.Namespace` for Class and Enum branches.
2. Add `ExtractConstructorAuthType(Type type)` method — inspects public constructors using same heuristics as `DetectAuthPatterns` (ApiKeyCredential > Credential > string with "key"/"secret"), returns `(typeName, typeNamespace)`.
3. In resource construction loop, call `ExtractConstructorAuthType` and pass to `Resource` constructor.

### Generator model changes

**`src/CliBuilder.Generator.CSharp/GeneratorModel.cs`**:

Extend `FlatParameter`:
```csharp
public record FlatParameter(
    string CliFlag, string PropertyName, string CSharpType,
    bool IsRequired, string? DefaultValueLiteral,
    string? Description, IReadOnlyList<string>? EnumValues,
    // NEW for step 7:
    string? SdkTypeName = null,              // "CustomerStatus", "TimeSpan", "int"
    TypeKind? SdkTypeKind = null,            // Enum, Primitive, etc.
    bool SdkTypeIsNullable = false,
    string? ConversionExpression = null,     // "Enum.Parse<CustomerStatus>({0})" or null
    string? SourceOptionsClassName = null    // "CreateCustomerOptions" or null for direct params
);
```

Extend `OperationModel`:
```csharp
IReadOnlyList<MethodParamModel>? MethodParams = null  // ordered params for call reconstruction
```

Add `MethodParamModel`:
```csharp
public record MethodParamModel(
    string ArgExpression,  // "createCustomerOptions" or "idValue"
    string? TypeName,      // "CreateCustomerOptions" or null
    string? Namespace,     // for using directives
    bool IsOptionsClass    // true = construct & populate; false = pass directly
);
```

Extend `ResourceModel`:
```csharp
string? ConstructorAuthExpression = null,      // "credential" or "new TokenCredential(credential)"
IReadOnlyList<string>? RequiredNamespaces = null  // all namespaces for using directives
```

### ParameterFlattener changes

**`src/CliBuilder.Generator.CSharp/ParameterFlattener.cs`**:

1. Change `FlattenOptionsClass` to accept `TypeRef classType` (not just `Properties`) so it has the class name and can pass `classType.Name` as `sourceOptionsClassName`.
2. Thread `SdkTypeName`, `SdkTypeKind`, `SdkTypeIsNullable` from the original `TypeRef` through to `FlatParameter`.
3. Add `ComputeConversion(TypeRef sdkType)` — returns conversion expression format string:
   - `string`, `int`, `bool`, `decimal`, etc. -> `null` (CLI matches SDK)
   - Enum -> `"Enum.Parse<{Name}>({0})"` (nullable: wrap with null check)
   - TimeSpan -> `"TimeSpan.Parse({0})"` (nullable: wrap)
   - DateTime, DateTimeOffset, Guid -> similar `.Parse` pattern

### ModelMapper changes

**`src/CliBuilder.Generator.CSharp/ModelMapper.cs`**:

1. Add `BuildMethodParams(IReadOnlyList<Parameter> parameters)` — iterates raw operation parameters to build ordered `MethodParamModel` list. Class params: `ArgExpression = PascalToCamelCase(typeName)`. Direct params: `ArgExpression = KebabToCamelCase(cliFlag) + "Value"`.
2. Add `PascalToCamelCase(string)` helper — lowercase first char.
3. Add `KebabToCamelCase(string)` helper — mirrors `TemplateRenderer.ToVarName`.
4. In `MapResource`: compute `ConstructorAuthExpression` from `ConstructorAuthTypeName`, collect `RequiredNamespaces` from all operations' MethodParams + SourceNamespace + ConstructorAuthTypeNamespace.

### Tests to write first

```csharp
// --- DotNetAdapterTests.cs ---

[Fact]
public void ClassTypeRef_HasNamespace()
// CreateCustomerOptions TypeRef → Namespace == "CliBuilder.TestSdk.Models"

[Fact]
public void EnumTypeRef_HasNamespace()
// CustomerStatus TypeRef → Namespace == "CliBuilder.TestSdk.Models"

[Fact]
public void ReturnTypeRef_HasNamespace()
// Customer return type → Namespace == "CliBuilder.TestSdk.Models"

[Fact]
public void CustomerService_ConstructorAuthType_IsString()
// CustomerService(string apiKey) → ConstructorAuthTypeName == "string", Namespace == null

[Fact]
public void ProductApi_ConstructorAuthType_IsTokenCredential()
// ProductApi(TokenCredential) → ConstructorAuthTypeName == "TokenCredential", Namespace == "CliBuilder.TestSdk.Models"

[Fact]
public void OrderClient_ConstructorAuthType_IsString()
// OrderClient(string apiKey) → same as CustomerService

// --- ParameterFlattenerTests.cs ---

[Fact]
public void OptionsClassParams_HaveSourceOptionsClassName()
// Params from CreateCustomerOptions → SourceOptionsClassName == "CreateCustomerOptions"

[Fact]
public void DirectParams_HaveNullSourceOptionsClassName()
// Direct "id" param → SourceOptionsClassName == null

[Fact]
public void MultipleOptionsClasses_TrackSeparateClassNames()
// Two class params → each FlatParameter has its class's name

[Fact]
public void SdkTypeName_ThreadedThrough_ForPrimitives()
// Direct string param → SdkTypeName == "string", SdkTypeKind == Primitive

[Fact]
public void SdkTypeName_ThreadedThrough_ForEnums()
// Enum param → SdkTypeName == "CustomerStatus", SdkTypeKind == Enum

[Fact]
public void ComputeConversion_String_ReturnsNull()
[Fact]
public void ComputeConversion_Int_ReturnsNull()
[Fact]
public void ComputeConversion_Bool_ReturnsNull()
[Fact]
public void ComputeConversion_Enum_ReturnsEnumParse()
[Fact]
public void ComputeConversion_NullableEnum_IncludesNullCheck()
[Fact]
public void ComputeConversion_TimeSpan_ReturnsParse()
[Fact]
public void ComputeConversion_NullableTimeSpan_IncludesNullCheck()
[Fact]
public void ComputeConversion_Guid_ReturnsParse()

// --- ModelMapperTests.cs ---

[Theory]
public void PascalToCamelCase_Works()
// "CreateCustomerOptions" → "createCustomerOptions", "A" → "a", "" → ""

[Theory]
public void KebabToCamelCase_Works()
// "credit-limit" → "creditLimit", "id" → "id", "" → "_param"

[Fact]
public void MapResource_StringAuth_CredentialExpression()
// ConstructorAuthTypeName == "string" → ConstructorAuthExpression == "credential"

[Fact]
public void MapResource_TokenCredentialAuth_WrapsInNew()
// ConstructorAuthTypeName == "TokenCredential" → "new TokenCredential(credential)"

[Fact]
public void MapResource_NullAuth_DefaultsToCredential()
// No constructor auth info → defaults to "credential"

[Fact]
public void MapResource_CollectsNamespacesFromOperations()
// Options class in "Sdk.Options", service in "Sdk.Services", auth in "Sdk.Auth" → all three collected

[Fact]
public void MapOperation_BuildsMethodParams_ForOptionsClass()
// Single options class → MethodParams[0].IsOptionsClass, ArgExpression = "createOptions"

[Fact]
public void MapOperation_BuildsMethodParams_ForDirectParam()
// Direct "id" param → MethodParams[0].ArgExpression == "idValue"

[Fact]
public void MapOperation_BuildsMethodParams_MixedOrder()
// Two options classes → both in order, both IsOptionsClass
```

### Fixture update

Regenerate `tests/fixtures/testsdk-metadata.json` by running `ExtractTestSdk_ProducesValidMetadata_AndWritesFixture`. The JSON will now contain `namespace` fields on TypeRef and `constructorAuthTypeName`/`constructorAuthTypeNamespace` on Resource.

### Checkpoint 7A

```bash
dotnet test   # all existing 227+ tests still pass, plus ~25 new tests
dotnet test --filter "ConstructorAuth"
dotnet test --filter "ComputeConversion"
dotnet test --filter "MatchesGoldenFile"  # golden files unchanged (template not modified yet)
```

### Council review findings (applied)

A Developer Council (SoftwareDeveloper, QaTester, SecurityArchitect — 3-round debate) reviewed 7A and identified fixes applied before proceeding:

**P0 — Value type nullable mismatch (adapter bug).** `IsNullableProperty` incorrectly marked non-nullable value types (`bool`, `int`, `decimal`) as nullable when the declaring class had `NullableContextAttribute(2)`. The `NullableContext` only affects reference types — value types are nullable only via `Nullable<T>`. Fixed by adding `!prop.PropertyType.IsValueType` guard in both `IsNullableProperty` and `IsNullableParameter`. Fixture and golden files regenerated.

**P1 — Deduplicated `KebabToCamelCase`/`ToVarName`.** Moved the canonical implementation to `IdentifierValidator.KebabToCamelCase`. Both `TemplateRenderer.ToVarName` and `ModelMapper.KebabToCamelCase` now delegate to the shared method, eliminating silent divergence risk.

**P1 — Documented `ConversionExpression` contract.** Added XML doc to `FlatParameter.ConversionExpression` explaining the `{0}` placeholder convention and null=identity semantics.

**P2 — Added missing tests.** 8 new tests for nullable Guid/DateTime/DateTimeOffset conversions, non-Primitive/Enum TypeKind returns null, and `@class` property round-trip assertion. Total: 269 tests (49 Core + 206 Generator + 14 Integration).

**P3 — Identifier/namespace validation at ModelMapper boundary.** Added `IdentifierValidator.IsValidIdentifier` and `IsValidNamespace` methods. `ConstructorAuthExpression` type name validated before interpolation. `RequiredNamespaces` entries filtered for valid dotted identifiers. `ComputeConversion` enum name validated, falls back to identity if invalid. Defense-in-depth — adapter inputs are already valid CLR identifiers, but the ModelMapper boundary should not trust upstream.

---

## Phase 7B: Template Rewrite

**Goal:** Replace echo stubs in `ResourceCommands.sbn` with real SDK calls. The generated CLI compiles and (for TestSdk) makes real method calls. Golden files regenerated.

### Template changes

**`src/CliBuilder.Generator.CSharp/Templates/ResourceCommands.sbn`**:

#### 1. Using directives

Replace single `using {{ resource.source_namespace }};` with a loop over all required namespaces:
```scriban
{{- for ns in resource.required_namespaces }}
using {{ ns }};
{{- end }}
```

#### 2. Client construction with auth dispatch

Replace the commented stub:
```scriban
                    var client = new {{ resource.source_class_name }}({{ resource.constructor_auth_expression }});
```

This emits `new CustomerService(credential)` for string auth or `new ProductApi(new TokenCredential(credential))` for credential wrapper auth.

#### 3. Options class construction + property assignment

For each method parameter that is an options class, emit construction and property assignments:

```scriban
{{- for mp in op.method_params }}
{{- if mp.is_options_class }}
                    var {{ mp.arg_expression }} = new {{ mp.type_name }}();
{{- for param in op.parameters }}
{{- if param.source_options_class_name == mp.type_name }}
                    {{ mp.arg_expression }}.{{ param.property_name }} = {{ param.cli_flag | to_var_name | apply_conversion param.conversion_expression }};
{{- end }}
{{- end }}
{{- end }}
{{- end }}
```

The `apply_conversion` Scriban function (registered in TemplateRenderer) takes a variable name and a conversion expression. If conversion is null, returns `{varName}Value` (identity). If conversion is present, substitutes `{0}` with `{varName}Value` in the expression.

#### 4. Method call reconstruction

Build the SDK method call from `MethodParams`:
```scriban
{{- if op.is_streaming }}
                    var items = new List<object>();
                    await foreach (var item in client.{{ op.source_method_name }}({{ for mp in op.method_params }}{{ if !for.first }}, {{ end }}{{ mp.arg_expression }}{{ end }}))
                    {
                        items.Add(item);
                    }
                    var result = (object)items;
{{- else if op.return_type_name == "void" }}
                    await client.{{ op.source_method_name }}({{ for mp in op.method_params }}{{ if !for.first }}, {{ end }}{{ mp.arg_expression }}{{ end }});
                    Console.WriteLine("OK");
{{- else }}
                    var result = (object)await client.{{ op.source_method_name }}({{ for mp in op.method_params }}{{ if !for.first }}, {{ end }}{{ mp.arg_expression }}{{ end }});
{{- end }}
```

#### 5. Keep echo fallback

Preserve the `{{ else }}` branch for operations without `resource.source_class_name`. This ensures backward compatibility for resources with incomplete source metadata.

#### 6. Output formatting (unchanged for non-streaming/non-void)

```scriban
                    var useJson = ctx.ParseResult.GetValueForOption(jsonOption);
                    if (useJson)
                        JsonFormatter.Write(result);
                    else
                        TableFormatter.Write(result);
```

### TemplateRenderer changes

**`src/CliBuilder.Generator.CSharp/TemplateRenderer.cs`**:

Register new Scriban function:
- `apply_conversion(string varName, string? conversionExpr)` — if `conversionExpr` is null, returns `{varName}Value`. Otherwise, replaces `{0}` in the expression with `{varName}Value`.

### Tests to write first

```csharp
// --- TemplateRendererTests.cs (or inline in existing test class) ---

[Fact]
public void ApplyConversion_NullExpression_ReturnsVarNameValue()
// apply_conversion("email", null) → "emailValue"

[Fact]
public void ApplyConversion_EnumExpression_SubstitutesPlaceholder()
// apply_conversion("status", "Enum.Parse<CustomerStatus>({0})") → "Enum.Parse<CustomerStatus>(statusValue)"

[Fact]
public void ApplyConversion_NullableTimeSpan_Works()
// apply_conversion("timeout", "{0} is not null ? TimeSpan.Parse({0}) : (TimeSpan?)null")
// → "timeoutValue is not null ? TimeSpan.Parse(timeoutValue) : (TimeSpan?)null"

// --- CSharpCliGeneratorTests.cs ---

[Fact]
public void Generate_CustomerCreate_HasRealSdkCall()
// Generated CustomerCommands.cs contains "new CustomerService(credential)" (not commented)

[Fact]
public void Generate_ProductList_WrapsCredentialInNew()
// Generated ProductCommands.cs contains "new TokenCredential(credential)"

[Fact]
public void Generate_CustomerCreate_HasOptionsClassConstruction()
// Generated code contains "new CreateCustomerOptions()" and ".Email = emailValue"

[Fact]
public void Generate_CustomerCreate_HasEnumConversion()
// Generated code contains "Enum.Parse<CustomerStatus>"

[Fact]
public void Generate_CustomerGet_PassesDirectParams()
// Generated code contains "client.GetAsync(idValue)"

[Fact]
public void Generate_CustomerStream_HasAwaitForeach()
// Generated code contains "await foreach"

[Fact]
public void Generate_MultipleNamespaces_AllPresent()
// Generated CustomerCommands.cs has using for both Services and Models namespaces

[Fact]
public void Generate_NoSourceClassName_FallsBackToEcho()
// Resource without SourceClassName → uses dictionary echo (backward compat)

// Compile verification
[Fact]
public void Generate_TestSdk_CompilesWithDotnetBuild()
// Must still pass — the real SDK calls must compile

// Golden files
[Theory]
[InlineData("Commands/CustomerCommands.cs")]
// ... all 8 files
public void Generate_TestSdk_MatchesGoldenFile(string relativePath)
// After regeneration with UPDATE_GOLDEN=1, these lock the new output
```

### Golden file regeneration

After template changes, run:
```bash
UPDATE_GOLDEN=1 dotnet test --filter "MatchesGoldenFile"
```

Commit the regenerated golden files. The golden files now contain real SDK calls instead of echo dictionaries.

### Council review findings (applied)

A Developer Council (SoftwareDeveloper, QaTester — 3-round debate) reviewed 7B and identified:

**P1 — Scriban whitespace (fixed).** Template control blocks emitted excessive blank lines (runs of 15-30) in golden files. Fixed by adding `{{~` whitespace suppressors to control-flow-only lines. Maximum consecutive blank lines reduced from 22 to 2.

**P2 — Stub fallback + void return tests (added).** `Generate_NoSourceClassName_FallsBackToEcho` verifies the echo dictionary path for resources without SourceClassName. `Generate_CustomerDelete_IsVoidReturn` verifies non-void SDK calls.

**Accepted deferrals (documented):**
- `jsonInputValue` declared but unused — explicit step 8 deferral. The `--json-input` option is wired to the command parser but deserialization/merge logic is not implemented. The help text describes the intended behavior for when it ships.
- OpenAI compile failures (abstract types like `BinaryContent`/`Stream`, multi-arg constructors, non-generic `AsyncCollectionResult`, read-only properties) — Phase 7D scope. Root causes: (1) adapter maps abstract classes as concrete options classes, (2) constructor auth detection doesn't handle multi-param constructors, (3) non-generic streaming return types not caught.

### Checkpoint 7B

```bash
dotnet test --filter "CompilesWithDotnetBuild"  # compile still works
dotnet test --filter "MatchesGoldenFile"         # golden files updated
dotnet test --filter "FullyQualifiedName!~GenerateOpenAi_Compiles"  # all except OpenAI compile (7D)
```

---

## Phase 7C: TestSdk Implementation + End-to-End Tests

**Goal:** Make TestSdk service methods return real data. Verify the generated CLI produces correct output end-to-end by building and running it.

### TestSdk service implementations

Replace `throw new NotImplementedException()` with hardcoded return values:

**`tests/CliBuilder.TestSdk/Services/CustomerService.cs`**:
```csharp
public Task<Customer> CreateAsync(CreateCustomerOptions options,
    RequestOptions requestOptions, CancellationToken ct = default)
    => Task.FromResult(new Customer {
        Id = "cust_001", Email = options.Email,
        Name = options.Name, Status = options.InitialStatus ?? CustomerStatus.Active
    });

public Task<Customer> GetAsync(string id, CancellationToken ct = default)
    => Task.FromResult(new Customer { Id = id, Email = "test@example.com", Status = CustomerStatus.Active });

public Task<List<Customer>> ListAsync(int limit = 10, string? cursor = null, CancellationToken ct = default)
    => Task.FromResult(new List<Customer> {
        new() { Id = "cust_001", Email = "alice@test.com", Status = CustomerStatus.Active },
        new() { Id = "cust_002", Email = "bob@test.com", Status = CustomerStatus.Inactive }
    });

public ValueTask<bool> DeleteAsync(string id, CancellationToken ct = default) => new(true);

public async IAsyncEnumerable<Customer> StreamAsync([EnumeratorCancellation] CancellationToken ct = default)
{
    yield return new Customer { Id = "cust_s1", Email = "stream1@test.com", Status = CustomerStatus.Active };
    yield return new Customer { Id = "cust_s2", Email = "stream2@test.com", Status = CustomerStatus.Active };
    await Task.CompletedTask;
}

public Task<Dictionary<string, object>> GetMetadataAsync(string id, CancellationToken ct = default)
    => Task.FromResult(new Dictionary<string, object> { ["id"] = id, ["created"] = "2024-01-01" });
```

**`tests/CliBuilder.TestSdk/Services/OrderClient.cs`** — similar implementations returning `ClientResult<Order>`.

**`tests/CliBuilder.TestSdk/Services/ProductApi.cs`** — `ListAsync` returns a `Product`.

### End-to-end integration tests

**`tests/CliBuilder.Integration.Tests/`** — new test class:

```csharp
public class GeneratedCliTests
{
    // Shared setup: generate + build the TestSdk CLI once, then run commands

    [Fact]
    public async Task CustomerGet_ReturnsCustomerJson()
    // Run: testsdk-cli customer get --id cust_123 --json --api-key test
    // Assert: stdout JSON has "id": "cust_123"

    [Fact]
    public async Task CustomerCreate_UsesOptionsClass()
    // Run: testsdk-cli customer create --email foo@bar.com --json --api-key test
    // Assert: stdout JSON has "email": "foo@bar.com"

    [Fact]
    public async Task CustomerList_ReturnsArray()
    // Run: testsdk-cli customer list --json --api-key test
    // Assert: stdout is valid JSON array with 2 items

    [Fact]
    public async Task CustomerDelete_Succeeds()
    // Run: testsdk-cli customer delete --id cust_001 --api-key test
    // Assert: exit code 0

    [Fact]
    public async Task CustomerStream_ReturnsMultipleItems()
    // Run: testsdk-cli customer stream --json --api-key test
    // Assert: stdout contains streaming output

    [Fact]
    public async Task ProductList_TokenCredentialAuth_Works()
    // Run: testsdk-cli product list --json --api-key test
    // Assert: exit code 0, output is valid JSON

    [Fact]
    public async Task NoCredential_ExitsWithCode2()
    // Run: testsdk-cli customer get --id x (no --api-key, no env var)
    // Assert: exit code 2, stderr has "auth_error"

    [Fact]
    public async Task Help_ShowsCommands()
    // Run: testsdk-cli --help
    // Assert: stdout contains "customer", "order", "product"
```

### Test infrastructure

The integration tests need a helper to:
1. Generate CLI with `SdkProjectPath` pointing to TestSdk.csproj
2. `dotnet build` the generated project
3. `dotnet run` the generated project with arguments
4. Capture stdout, stderr, and exit code

This can be a shared `CliTestHelper` class or reuse the existing compile test infrastructure.

### Checkpoint 7C

```bash
dotnet test --filter "GeneratedCli"   # end-to-end tests
dotnet test                            # full suite
```

Manual validation:
```bash
cd /tmp/generated-cli/testsdk-cli
dotnet run -- --help
dotnet run -- customer get --id test123 --json --api-key test-key
dotnet run -- customer create --email foo@bar.com --json --api-key test-key
dotnet run -- product list --json --api-key test-key
```

---

## Phase 7D: Scale Validation + Cleanup

**Goal:** Verify enriched pipeline handles OpenAI scale. Update documentation. Run coverage.

### OpenAI scale check

Run existing OpenAI integration tests (`GenerateOpenAi_*`). The enriched metadata pipeline (TypeRef.Namespace, ConstructorAuthTypeName, MethodParams) must not break anything. The generated OpenAI CLI must still compile. (The OpenAI SDK methods themselves are not implemented — the handlers call real OpenAI types, but we don't run them.)

### Documentation updates

| File | Change |
|------|--------|
| `docs/cli-builder-spec.md` | Mark step 7 complete in First Actions. Update success criteria notes. |
| `AGENTS.md` | Update "What's done" and "What's next" sections. |
| `docs/design-notes.md` | Add section on type conversion rules and constructor auth dispatch. |
| `README.md` | Update demo output to show real data instead of echo. |

### Coverage check

```bash
./scripts/coverage.sh
```

Maintain >= 80% line coverage.

### Checkpoint 7D

```bash
dotnet test                            # everything green
dotnet test --filter "OpenAi"          # scale still works
./scripts/coverage.sh                  # >= 80% coverage
```

---

## File manifest

Files created/modified during step 7:

**Modified files:**
| File | Phase | Change |
|------|-------|--------|
| `src/CliBuilder.Core/Models/TypeRef.cs` | 7A | Add `Namespace` parameter |
| `src/CliBuilder.Core/Models/Resource.cs` | 7A | Add `ConstructorAuthTypeName`, `ConstructorAuthTypeNamespace` |
| `src/CliBuilder.Adapter.DotNet/DotNetAdapter.cs` | 7A | Populate `Namespace` on TypeRef, add `ExtractConstructorAuthType`, fix value type nullability in `IsNullableProperty`/`IsNullableParameter` |
| `src/CliBuilder.Generator.CSharp/GeneratorModel.cs` | 7A | Extend `FlatParameter` (5 fields), `OperationModel` (MethodParams), `ResourceModel` (2 fields), add `MethodParamModel` |
| `src/CliBuilder.Generator.CSharp/ParameterFlattener.cs` | 7A | Thread source class/SDK type/conversion, add `ComputeConversion`, enum name validation |
| `src/CliBuilder.Generator.CSharp/ModelMapper.cs` | 7A | Build MethodParams, compute auth expression, collect namespaces, identifier/namespace validation |
| `src/CliBuilder.Generator.CSharp/IdentifierValidator.cs` | 7A | Add `KebabToCamelCase` (shared), `IsValidIdentifier`, `IsValidNamespace` |
| `src/CliBuilder.Generator.CSharp/Templates/ResourceCommands.sbn` | 7B | Replace stubs with real SDK calls |
| `src/CliBuilder.Generator.CSharp/TemplateRenderer.cs` | 7B | Add `apply_conversion` Scriban function |
| `tests/CliBuilder.TestSdk/Services/CustomerService.cs` | 7C | Implement methods with hardcoded return data |
| `tests/CliBuilder.TestSdk/Services/OrderClient.cs` | 7C | Implement methods |
| `tests/CliBuilder.TestSdk/Services/ProductApi.cs` | 7C | Implement ListAsync |
| `tests/CliBuilder.Core.Tests/DotNetAdapterTests.cs` | 7A | Tests for Namespace, ConstructorAuthType |
| `tests/CliBuilder.Generator.Tests/ParameterFlattenerTests.cs` | 7A | Tests for SourceOptionsClassName, SdkType, ComputeConversion |
| `tests/CliBuilder.Generator.Tests/ModelMapperTests.cs` | 7A | Tests for helpers, ConstructorAuthExpression, RequiredNamespaces, MethodParams |
| `tests/CliBuilder.Generator.Tests/CSharpCliGeneratorTests.cs` | 7B | Tests for real SDK call patterns in generated output |
| `tests/fixtures/testsdk-metadata.json` | 7A | Regenerated with Namespace, ConstructorAuth fields |
| `tests/golden/testsdk-cli/**` | 7B | Regenerated with real SDK calls |

**New files:**
| File | Phase | Purpose |
|------|-------|---------|
| `tests/CliBuilder.Integration.Tests/GeneratedCliTests.cs` | 7C | End-to-end tests: generate → build → run → assert output |
| `docs/internal/step-07-wiring.md` | 7A | This document |

---

## Key design decisions for the implementer

1. **ConversionExpression is a format string** — `Enum.Parse<CustomerStatus>({0})` where `{0}` is the variable name. The `apply_conversion` Scriban function does the substitution. This avoids complex template conditionals.

2. **MethodParamModel.ArgExpression is pre-computed** — the ModelMapper computes the exact expression to use in the method call. For options classes: `PascalToCamelCase(typeName)`. For direct params: `KebabToCamelCase(cliFlag) + "Value"`. The template just emits `{{ mp.arg_expression }}`.

3. **RequiredNamespaces deduplicates and sorts** — collected from SourceNamespace, ConstructorAuthTypeNamespace, and all MethodParamModel namespaces. Sorted alphabetically for deterministic output.

4. **KebabToCamelCase mirrors TemplateRenderer.ToVarName** — the ModelMapper duplicates this logic so it can pre-compute variable names for MethodParamModel. Both must produce identical output.

5. **Streaming collects into List<object>** — for v1, streaming operations enumerate all items and collect them before formatting. Incremental streaming output is deferred. This keeps the template simple and reuses existing formatters.

6. **Echo fallback preserved** — operations without SourceClassName (e.g., from hand-crafted metadata) still work via the dictionary echo path. This is important for backward compatibility during the transition.

7. **TestSdk implementations are minimal** — just enough to return predictable data for assertions. No state, no validation, no error simulation. Error testing uses the existing auth error path (no credentials → exit 2).

---

## Deferred to future steps

- **`--json-input` deserialization** — the option exists on commands that need it but is not wired to deserialize and merge with flat flags. Complex: requires deep merge, flat flags override, and options class construction from JSON. Planned for step 8.
- **Incremental streaming output** — streaming operations currently collect all items before formatting. True incremental streaming (emit each item as it arrives) is a future enhancement.
- **Stripe/OpenAI live API validation** — the spec says "Stripe test mode". Phase 7D checks that OpenAI generation still compiles, but live API calls against a real service are deferred pending API key setup.
- **Token caching** — the auth handler design notes mention writing resolved credentials to a config file. Not implemented in the generated handler; deferred.
