# Step 14: Port C# Generator from .NET/Scriban to Rust/Tera

**Prerequisite:** Step 13 complete (shared Rust core + Python generator). Step 12b complete (adapter hardening).
**Output:** `cli-builder-gen-csharp` Rust binary that reads SdkMetadata JSON and emits a System.CommandLine C# CLI project. Validated by compilation (`dotnet build`) and insta golden file snapshots.

---

## Problem

The C# generator currently lives in .NET (`CliBuilder.Generator.CSharp`, ~650 lines of C# + 500 lines of Scriban templates). ADR-017 says all generators should consolidate in Rust with shared core + Tera templates. Step 13 proved the architecture with Python. Step 14 ports the C# generator, reusing the shared `ModelMapper`, `ParameterFlattener`, and `IdentifierValidator` from `cli-builder-core`.

---

## What's already shared (DON'T re-implement)

The Rust `cli-builder-core` crate already has:
- `model_mapper.rs` — SdkMetadata → GeneratorModel via `LanguageProfile` trait
- `parameter_flattener.rs` — flatten options classes into CLI flags
- `identifier_validator.rs` — case conversion, validation, `sanitize_parameter`
- `generator_model.rs` — language-neutral GeneratorModel types

These were ported from the C# `ModelMapper.cs`, `ParameterFlattener.cs`, `IdentifierValidator.cs`. The C# generator reuses them via the `LanguageProfile` trait.

---

## What's C#-specific (MUST implement)

### CSharpProfile (implements LanguageProfile)

| Method | C# behavior |
|--------|-------------|
| `map_cli_type` | string→string, int→int, Enum→"string", Class(forCli)→"string", Class(return)→preserve, nullable value types→append `?` |
| `map_primitive_type` | Int32→int, Boolean→bool, Single→float, TimeSpan/DateTime/Guid→"string", void→"void" |
| `build_deserialization_type_name` | Array→`T[]`, Generic→`List<T>`, Dictionary→`Dictionary<K,V>` |
| `is_keyword` | 42 reserved + 15 contextual C# keywords |
| `is_boilerplate_name` | JsonFormatter, TableFormatter, AuthHandler, Program, apiKey, json, credential, etc. |
| `is_binary_type` | BinaryContent, BinaryData, Stream, ReadOnlyMemory, ReadOnlySpan |
| `is_infrastructure_type` | RequestOptions, CancellationToken, *ClientOptions, *ClientSettings |
| `is_unwirable_return_type` | AsyncCollectionResult, CollectionResult, Uri, Stream, single-char, *Client, *Service, *Api, *Options, *Response, *Notification |

### C#-specific wrapper types (council fix: explicit struct definitions)

The core `GeneratorModel` is language-neutral — it has `cli_type` not `CSharpType`, `arg_name` not `ArgExpression`, no `ConversionExpression`. The C# generator needs wrapper types that add C#-specific computed fields. These are produced by post-processing the core model.

```rust
/// C#-specific FlatParameter with computed fields for templates.
#[derive(Debug, Clone, Serialize)]
pub struct CSharpFlatParameter {
    // All fields from core FlatParameter (flattened, not nested)
    pub cli_flag: String,
    pub property_name: String,
    pub csharp_type: String,           // May have ? appended by MakeValueTypesNullable
    pub is_required: bool,
    pub default_value_literal: Option<String>,  // C# literal: "true", "42", "@\"hello\""
    pub description: Option<String>,
    pub enum_values: Option<Vec<String>>,
    pub sdk_type_name: Option<String>,
    pub sdk_type_kind: Option<TypeKind>,
    pub sdk_type_is_nullable: bool,
    pub conversion_expression: Option<String>,  // "Enum.Parse<Status>({0})"
    pub source_options_class_name: Option<String>,
}

/// C#-specific MethodParamModel with pre-built arg expression.
#[derive(Debug, Clone, Serialize)]
pub struct CSharpMethodParam {
    pub arg_expression: String,         // Pre-built: "Enum.Parse<Status>(statusValue)"
    pub type_name: Option<String>,
    pub namespace: Option<String>,      // C# uses "namespace" not "module"
    pub is_options_class: bool,
    pub needs_json_deserialization: bool,
    pub deserialization_type_name: Option<String>,
    pub json_property_name: Option<String>,
    pub is_required: bool,
}

/// C#-specific ResourceModel with constructor expression.
#[derive(Debug, Clone, Serialize)]
pub struct CSharpResourceModel {
    pub name: String,
    pub class_name: String,
    pub description: Option<String>,
    pub operations: Vec<CSharpOperationModel>,
    pub source_class_name: Option<String>,
    pub source_module: Option<String>,
    pub can_construct: bool,
    pub constructor_expression: Option<String>,  // "new ApiKeyCredential(credential)"
    pub constructor_config_params: Vec<CSharpConstructorConfigParam>,
    pub required_namespaces: Vec<String>,
}

/// C#-specific GeneratorModel — full context for Tera templates.
#[derive(Debug, Clone, Serialize)]
pub struct CSharpGeneratorModel {
    pub cli_name: String,
    pub sdk_name: String,
    pub sdk_version: String,
    pub sdk_package_name: String,
    pub root_namespace: String,
    pub cli_description: String,
    pub resources: Vec<CSharpResourceModel>,
    pub auth: Option<AuthModel>,
    pub static_auth_setup: Option<String>,
    pub sdk_project_path: Option<String>,
}
```

**Post-processing pipeline** (in `csharp_model.rs`):
```
core GeneratorModel
  → build_csharp_model()
    → for each resource:
        → build_constructor_expression() from ConstructorAuth + ConfigParams
        → for each operation:
            → compute_conversion() for each FlatParameter
            → sanitize_default_value() for each FlatParameter
            → make_value_types_nullable() if requires_sentinel_nullability
            → build_arg_expression() for each MethodParamModel
  → CSharpGeneratorModel (ready for Tera context)
```

### Custom Tera filters

Register in the Tera instance:

**`escape_csharp`** — double quotes in verbatim strings: `"` → `""`
- Signature: `value | escape_csharp` → escaped string
- Example: `say "hi"` → `say ""hi""`

**`to_var_name`** — kebab-case to camelCase
- Signature: `value | to_var_name` → camelCase string
- Example: `credit-limit` → `creditLimit`

**`apply_conversion`** — substitute `{0}` in conversion expression with variable name
- Signature: `value | apply_conversion(expr=param.conversion_expression)`
- `value` is the variable name (e.g., `"statusValue"`)
- `expr` is the format string (e.g., `"Enum.Parse<Status>({0})"`)
- Returns: `"Enum.Parse<Status>(statusValue)"`
- **Null path**: if `expr` is null/absent, return `value` unchanged (identity — no conversion)

### Tera templates (6 files, ~500 lines)

Port from the 6 Scriban `.sbn` files:

| Scriban file | Tera file | Lines | Complexity |
|-------------|-----------|-------|------------|
| `csproj.sbn` | `csproj.tera` | ~25 | Low — XML with SDK reference |
| `Program.sbn` | `program.tera` | ~25 | Low — RootCommand, global options |
| `ResourceCommands.sbn` | `resource_commands.tera` | ~230 | **High** — command tree, handlers, SDK calls |
| `JsonFormatter.sbn` | `json_formatter.tera` | ~35 | Low — JSON serialization utility |
| `TableFormatter.sbn` | `table_formatter.tera` | ~110 | Medium — ASCII table with column detection |
| `AuthHandler.sbn` | `auth_handler.tera` | ~85 | Medium — credential resolution chain |

### Scriban→Tera conversion notes

| Scriban | Tera equivalent |
|---------|----------------|
| `{{ variable }}` | `{{ variable }}` (same) |
| `{{ for item in list }}...{{ end }}` | `{% for item in list %}...{% endfor %}` |
| `{{ if condition }}...{{ end }}` | `{% if condition %}...{% endif %}` |
| `{{ item.property_name }}` | `{{ item.property_name }}` (same — core model already uses snake_case) |
| `{{~ ... ~}}` (whitespace strip) | `{%- ... -%}` |
| `{{ for.first }}` / `{{ for.last }}` | `{% if loop.first %}` / `{% if loop.last %}` |
| `{{ value \| string.downcase }}` | `{{ value \| lower }}` |
| Custom functions (escape_csharp, etc.) | Custom Tera filters |

---

## Implementation Order

### Phase 1: CSharpProfile + C#-specific model wrapper

1. Create `crates/gen-csharp/` crate
2. Add to workspace `Cargo.toml`
3. `csharp_keywords.rs` — C# keyword/contextual/boilerplate lists (from IdentifierValidator.cs)
4. `csharp_mapper.rs` — `CSharpProfile` implementing `LanguageProfile` trait
5. `csharp_model.rs` — C#-specific wrapper types (`CSharpGeneratorModel`, `CSharpFlatParameter`, `CSharpMethodParam`, `CSharpResourceModel`) + post-processing functions:
   - `build_csharp_model(model: &GeneratorModel) -> CSharpGeneratorModel`
   - `compute_conversion(sdk_type: &TypeRef) -> Option<String>`
   - `sanitize_default_value(value: &serde_json::Value, type_ref: &TypeRef) -> Option<String>`
   - `make_value_types_nullable(params: &mut [CSharpFlatParameter])`
   - `build_arg_expression(param: &MethodParamModel, ...) -> String`
   - `build_constructor_expression(resource: &ResourceModel) -> Option<String>`
   - `sanitize_xml_value(value: &str) -> String`
6. Tests — unit tests for all transforms:

| Test group | Cases |
|-----------|-------|
| `map_primitive_type` | string, int/Int32, long/Int64, bool/Boolean, float/Single, double/Double, decimal, byte, short, TimeSpan→"string", DateTime→"string", Guid→"string", void→"void", unknown→"string" |
| `map_cli_type` nullable | nullable int→"int?", nullable bool→"bool?", nullable string→"string" (no ?), nullable enum→"string" |
| `map_cli_type` forCliParam | Class→"string", Array→"string", Generic→"string", Dict→"string" |
| `map_cli_type` forReturn | Class→preserve name, Generic→full signature |
| `build_deserialization_type_name` | Array→`T[]`, Dict with 2 args→`Dictionary<K,V>`, Dict no args→`Dictionary<string, object>`, Generic List→`List<T>`, Generic Dict→`Dictionary<K,V>` |
| `is_keyword` | reserved (class, int, string), contextual (var, async, record), non-keyword |
| `is_boilerplate_name` | JsonFormatter, Program, apiKey, non-boilerplate |
| `compute_conversion` | real enum non-nullable→`Enum.Parse<T>({0})`, real enum nullable→with null check, extensible enum→None, TimeSpan→`TimeSpan.Parse({0})`, DateTime nullable→with null check, Guid→`Guid.Parse({0})`, primitive string→None |
| `sanitize_default_value` | null→None, true→"true", false→"false", int 42→"42", decimal 9.99→"9.99m", double 3.14→"3.14d", float 1.5→"1.5f", string→`@"..."`, string with quotes→doubled, array→None+CB302, object→None+CB302 |
| `make_value_types_nullable` | bool→"bool?"+conversion ".Value", int→"int?", string stays string, direct params stay non-nullable |
| `build_arg_expression` | options class→`PascalToCamelCase(typeName)`, direct primitive→`varNameValue`, direct enum→`Enum.Parse<T>(varNameValue)`, JSON deser→`varNameValue` |
| `build_constructor_expression` | string auth→"credential", typed auth→"new T(credential)", multi-arg→"indexValue, new T(credential)", no auth→None |
| `sanitize_xml_value` | `&`→`&amp;`, `<`→`&lt;`, `"`→`&quot;` |
| `apply_conversion` filter | with expr→substituted, without expr→passthrough |

### Phase 2: Tera templates + renderer — DONE

1. All 6 Scriban templates ported to Tera syntax
2. Custom filters registered (`escape_csharp`, `to_var_name`, `apply_conversion`)
3. `renderer.rs` with Tera escaping (ADR-017 pattern)
4. Structural tests + insta golden file snapshots
5. Council fixes: synthetic model tests for has_auth=false, void return, echo stub; insta snapshots for csproj/Program.cs/CustomerCommands.cs/AuthHandler.cs; enriched assertions for FromAmong + constructor config params

### Phase 3: Compile gate (remaining)

1. **Compile test**: `dotnet build` on Rust-generated output — prerequisite: `dotnet pack` on `src/CliBuilder.TestSdk/` to produce NuGet package
2. Named E2E test: `rust_generated_testsdk_compiles_with_dotnet` — required gate
3. Whitespace normalization: compare Rust output with .NET golden files as **semantic reference**. Insta snapshots (already done) lock the Tera output as canonical.

### Phase 4: Real SDK validation

1. Generate from OpenAI fixture — compare resource/operation counts with .NET generator
2. Generate from Stripe fixture — same comparison
3. Compile test on OpenAI/Stripe generated output (public NuGet packages)
4. Add CLI entry point (`main.rs` with clap derive macros)

### Phase 5: Documentation

1. Update AGENTS.md, FUTURE.md, design-notes.md
2. Mark Step 14 as done in FUTURE.md

---

## Key files

| File | Purpose |
|------|---------|
| `cli-builder-gen-csharp/Cargo.toml` | Crate config, depends on cli-builder-core |
| `cli-builder-gen-csharp/src/lib.rs` | Module declarations |
| `cli-builder-gen-csharp/src/csharp_keywords.rs` | C# keyword/boilerplate lists |
| `cli-builder-gen-csharp/src/csharp_mapper.rs` | CSharpProfile (LanguageProfile impl) |
| `cli-builder-gen-csharp/src/csharp_model.rs` | C#-specific wrapper types + post-processing |
| `cli-builder-gen-csharp/src/renderer.rs` | Tera rendering + custom filters |
| `cli-builder-gen-csharp/src/main.rs` | CLI entry point (clap) |
| `cli-builder-gen-csharp/src/tests.rs` | Unit + structural + E2E tests |
| `cli-builder-gen-csharp/templates/*.tera` | 6 Tera template files |

---

## Verification

```bash
# Build
cd crates && cargo build

# Run unit + structural tests
cargo test

# Generate from TestSdk fixture
cargo run -p cli-builder-gen-csharp -- \
  --input ../tests/fixtures/testsdk-metadata.json \
  --output /tmp/testsdk-csharp-cli \
  --cli-name testsdk-cli

# Compile generated output (requires TestSdk NuGet — run dotnet pack first)
cd ../src/CliBuilder.TestSdk && dotnet pack -o /tmp/testsdk-nuget
cd /tmp/testsdk-csharp-cli && dotnet build

# Semantic comparison with .NET golden files
diff -r /tmp/testsdk-csharp-cli tests/golden/testsdk-cli/ | head -20
```

---

## Risk

**Medium.** The shared core is proven (Python generator works). The main risks:

- **Scriban→Tera syntax conversion** — The `ResourceCommands.sbn` template (229 lines) has complex nested logic with custom functions. Tera's syntax differs (no automatic member renaming, different loop/conditional syntax). This is the most error-prone phase.
- **C#-specific model wrapper complexity** — 6 post-processing transforms, each with nullable/extensible enum edge cases. Must match .NET behavior exactly. Mitigated by comprehensive unit tests in Phase 1.
- **Compile test dependency** — `dotnet build` requires TestSdk as NuGet package. Mitigated by `dotnet pack` prerequisite step.
- **Whitespace matching** — Scriban and Tera whitespace control differs. Target byte-for-byte with `{%- -%}`, allow-list documented deviations per file.

---

## What this does NOT solve

- Removing the .NET generator (keep both until Step 15 orchestrator can invoke the Rust version)
- Rust orchestrator (Step 15 — `cli-builder` binary calling adapters as subprocesses)
- New C# template features (streaming improvements, DI support — those are separate future items)
