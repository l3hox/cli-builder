# Step 11: SdkMetadata Abstraction for Multi-Language Support

**Prerequisite:** Steps 1-10 + 9B complete. 394 tests, 0 failures. `SdkMetadata` is the contract between adapter and generator, but contains .NET-specific leaks.
**Output:** `SdkMetadata` is truly language-neutral. A Python adapter could produce identical metadata for equivalent SDK constructs. All .NET-specific interpretation moves to the C# generator layer.

---

## Problem

`SdkMetadata` claims to be language-agnostic but contains 6 .NET-specific leaks:

| Leak | Current | Example value | Why it breaks Python |
|------|---------|---------------|---------------------|
| `SdkMetadata.StaticAuthSetup` | `string?` C# expression | `"Stripe.StripeConfiguration.ApiKey"` | Python has no static properties — it would be `stripe.api_key = ...` |
| `AdapterOptions.AssemblyPath` | DLL-specific path | `"Stripe.net.dll"` | Python input is a package name or wheel |
| `AdapterOptions.XmlDocPath` | .NET XML docs | `"Stripe.net.xml"` | Python uses docstrings, not XML |
| `Resource.SourceNamespace` | .NET namespace | `"Stripe.Services"` | Python has modules, not namespaces |
| `ConstructorParam.TypeNamespace` | .NET namespace | `"Stripe.Auth"` | Same |
| `TypeRef.Namespace` | .NET namespace | `"OpenAI.Chat"` | Same |

Additionally, `Resource.SourceClassName` and `Operation.SourceMethodName` use .NET naming conventions but are conceptually valid across languages (all have classes/methods).

### What's NOT a leak

These are in the C# generator layer (`GeneratorModel.cs`, `ParameterFlattener.cs`, `ModelMapper.cs`), not in `SdkMetadata`:
- `FlatParameter.CSharpType` — generator-specific, correct placement
- `FlatParameter.ConversionExpression` — C# code, correctly in generator
- `ConstructorExpression` in GeneratorModel — C# code, correctly in generator

---

## Design

### Principle: rename, don't restructure

The metadata models are working well. The fix is primarily **renaming fields** to be language-neutral, and **restructuring one field** (`StaticAuthSetup` → structured record). No architectural overhaul needed — the adapter→metadata→generator boundary is already correct.

### Changes

#### 1. `StaticAuthSetup` → `StaticAuthConfig` (structured record)

**Current:** `string? StaticAuthSetup = null` — stores `"Stripe.StripeConfiguration.ApiKey"`

**New:** Replace with a structured record in `src/CliBuilder.Core/Models/StaticAuthConfig.cs`:
```csharp
public record StaticAuthConfig(
    string TypeName,          // "StripeConfiguration"
    string TypeModule,        // "Stripe" (.NET namespace / Python module) — non-nullable, "" for global
    string PropertyName       // "ApiKey"
);
```

`TypeModule` is non-nullable — use `""` for types in the global namespace. This avoids null-guard complexity when the generator reconstructs the expression.

`SdkMetadata` changes: `StaticAuthConfig? StaticAuth = null` (was `string? StaticAuthSetup`)

**Adapter change** (`DotNetAdapter.DetectStaticAuthSetup`): return `StaticAuthConfig` instead of string.

**Generator change** (`ModelMapper`): construct the C# expression in the mapper (line 45), not in the adapter. When `TypeModule` is non-empty: `"{TypeModule}.{TypeName}.{PropertyName}"`. When empty: `"{TypeName}.{PropertyName}"`.

**Template change**: no change — the template still receives `static_auth_setup` as a string expression from `GeneratorModel`.

#### 2. `AdapterOptions.AssemblyPath` → `ArtifactPath`

Simple rename. The field is language-specific by nature (each adapter interprets it differently), but the name shouldn't assume .NET.

#### 3. `AdapterOptions.XmlDocPath` → `DocsPath`

Simple rename. Python adapter would use a different docs format.

#### 4. `Resource.SourceNamespace` → `Resource.SourceModule`

Rename. "Module" is more universal (Python modules, Java packages, .NET namespaces).

#### 5. `ConstructorParam.TypeNamespace` → `ConstructorParam.TypeModule`

Rename.

#### 6. `TypeRef.Namespace` → `TypeRef.Module`

Rename. This is used extensively in the generator for `using` directives / `import` statements.

#### 7. Add `TypeKind.Other` escape hatch

Add `Other` value to the `TypeKind` enum. Prevents Step 12 breaking change — a Python adapter may encounter types that don't map to the existing 6 kinds. The C# generator can map `Other` to `"object"` (same as the existing `_ => "object"` fallback in `MapTypeName`).

---

## Implementation Order

### Phase 1: Create `StaticAuthConfig` record + refactor adapter/generator

1. Add `StaticAuthConfig` record to `src/CliBuilder.Core/Models/`
2. Change `SdkMetadata.StaticAuthSetup` → `StaticAuth` (type `StaticAuthConfig?`)
3. Update `DotNetAdapter.DetectStaticAuthSetup` to return `StaticAuthConfig`
4. Update `ModelMapper` to construct the C# expression from `StaticAuthConfig`
5. Update `InspectCommand` human-readable output (references `StaticAuthSetup`)
6. Update all tests that reference `StaticAuthSetup`

### Phase 2: Rename `AssemblyPath` → `ArtifactPath`, `XmlDocPath` → `DocsPath`

1. Rename in `AdapterOptions`
2. Update `DotNetAdapter.Extract` (references `options.AssemblyPath`)
3. Update all tests

Note: `GenerateCommand.cs` uses a local `assemblyPath` parameter, not the `AdapterOptions` field — no change needed there. The CLI flag `--assembly` stays as-is (user-facing, describes the .NET use case).

### Phase 3: Rename namespace fields → module

1. `Resource.SourceNamespace` → `SourceModule`
2. `ConstructorParam.TypeNamespace` → `TypeModule`
3. `TypeRef.Namespace` → `Module`
4. Update `DotNetAdapter` (sets these fields)
5. Update `ModelMapper` (reads these fields for RequiredNamespaces, ConstructorAuthExpression)
6. Update `ParameterFlattener` (doesn't directly use namespace, but verify)
7. Update templates (Scriban auto-converts: `source_namespace` → `source_module`)
8. Update all tests + golden files + fixtures

### Phase 4: Add `TypeKind.Other` + new tests

1. Add `Other` to `TypeKind` enum in `TypeRef.cs`
2. Verify `MapTypeName` fallback handles it (existing `_ => "object"`)
3. Add tests:
   - `ModelMapper_StaticAuthConfig_EmptyModule_NoLeadingDot` — assert expression is `"TypeName.PropertyName"` not `".TypeName.PropertyName"`
   - `ModelMapper_StaticAuthConfig_WithModule_FullExpression` — assert `"Module.TypeName.PropertyName"`
   - `DotNetAdapter_DetectsStaticAuth_ReturnsStructuredRecord` — assert Stripe's StaticAuth has TypeModule="Stripe", TypeName="StripeConfiguration", PropertyName="ApiKey"
   - `SdkMetadata_JsonFieldNames_AreLanguageNeutral` — JSON round-trip contract test asserting property names `staticAuth`, `sourceModule`, `artifactPath` (not `staticAuthSetup`, `sourceNamespace`, `assemblyPath`)

### Phase 5: Update docs + regenerate

1. Update `docs/design-notes.md` — field name changes
2. Update `docs/cli-builder-spec.md` — model definitions
3. Regenerate all fixtures (JSON field names change)
4. Regenerate golden files
5. Update `AGENTS.md`
6. Update `docs/FUTURE.md` — log deferred items: StaticAuthConfig Style discriminator, language-neutrality reflection guard

---

## Files to modify

| File | Change |
|------|--------|
| `src/CliBuilder.Core/Models/SdkMetadata.cs` | `StaticAuthSetup` → `StaticAuth` (StaticAuthConfig?) |
| `src/CliBuilder.Core/Models/StaticAuthConfig.cs` | NEW record (TypeName, TypeModule, PropertyName) |
| `src/CliBuilder.Core/Models/AdapterOptions.cs` | `AssemblyPath` → `ArtifactPath`, `XmlDocPath` → `DocsPath` |
| `src/CliBuilder.Core/Models/Resource.cs` | `SourceNamespace` → `SourceModule` |
| `src/CliBuilder.Core/Models/Resource.cs` (ConstructorParam) | `TypeNamespace` → `TypeModule` |
| `src/CliBuilder.Core/Models/TypeRef.cs` | `Namespace` → `Module`, add `Other` to TypeKind |
| `src/CliBuilder.Adapter.DotNet/DotNetAdapter.cs` | All field references + DetectStaticAuthSetup returns StaticAuthConfig |
| `src/CliBuilder.Generator.CSharp/ModelMapper.cs` | Construct C# auth expression from StaticAuthConfig; all Namespace→Module |
| `src/CliBuilder.Generator.CSharp/Templates/ResourceCommands.sbn` | `source_namespace` → `source_module` (if used) |
| `src/CliBuilder/Commands/InspectCommand.cs` | Update StaticAuthSetup reference |
| `tests/CliBuilder.Core.Tests/DotNetAdapterTests.cs` | Field name updates + StaticAuth structured record test |
| `tests/CliBuilder.Generator.Tests/ModelMapperTests.cs` | Field name updates + expression reconstruction tests |
| `tests/CliBuilder.Generator.Tests/CSharpCliGeneratorTests.cs` | Golden file regeneration |
| `tests/CliBuilder.Integration.Tests/*.cs` | Field name updates + JSON contract test |
| `tests/fixtures/*.json` | Regenerated (JSON property names change) |
| `tests/golden/testsdk-cli/*.cs` | Regenerated |
| `docs/design-notes.md` | Field name updates in rules sections |
| `docs/cli-builder-spec.md` | Model definitions updated |

Note: `GenerateCommand.cs` does NOT need changes (uses local `assemblyPath` param, not `AdapterOptions` field). `ParameterFlattener.cs` has no direct namespace references — verify only.

---

## Risk

**Low-medium.** This is a mechanical rename-and-restructure. The one real design change (`StaticAuthSetup` → `StaticAuthConfig`) is small and well-contained.

Key risks:
- **Fixture size**: JSON fixtures regenerate with new field names — large diffs but no logic change
- **Golden file churn**: all golden files change (Scriban auto-renames snake_case properties)
- **Missing rename**: a stale field reference in tests or docs causes compile error (easy to find) or silent behavior change (harder to find in JSON)
- **StaticAuthConfig**: the C# expression construction moves from adapter to generator — verify Stripe still works

---

## What this does NOT change

- No architectural restructure of the adapter→metadata→generator pipeline
- No new interfaces or abstraction layers
- `GeneratorModel` stays C#-specific (that's correct — it's in the CSharp generator)
- `ICliGenerator` and `ISdkAdapter` interfaces unchanged
- No Python adapter implementation (that's Step 12)
- `AuthPattern.Type` enum (`ApiKey`, `BearerToken`, etc.) — already universal, no change needed

## Deferred to Step 12 (log in FUTURE.md)

- `StaticAuthConfig` Style discriminator (`StaticProperty` vs `ModuleAttribute`) — needed when Python adapter implements `import stripe; stripe.api_key = ...` pattern
- Language-neutrality reflection guard test (assert no field in SdkMetadata contains .NET-specific names)
- `TypeKind` may need further values for Python-specific concepts (tuple, union types)

---

## Verification

```bash
dotnet build                    # compile after renames
dotnet test                     # all tests pass after renames
dotnet test --filter "Stripe"   # Stripe still generates + compiles
dotnet test --filter "OpenAi"   # OpenAI still generates + compiles
dotnet test --filter "GeneratedCli"  # E2E still works

# Inspect metadata to verify JSON field names changed
dotnet run --project src/CliBuilder -- inspect --json \
  --assembly tests/CliBuilder.TestSdk/bin/Debug/net8.0/CliBuilder.TestSdk.dll \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['metadata'].keys())"
# Should show 'staticAuth' not 'staticAuthSetup'
```
