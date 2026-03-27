# Step 6: CLI Generator — SdkMetadata → Compilable C# Project

**Prerequisite:** Step 5 complete — adapter extracts `SdkMetadata`, validated against TestSdk and OpenAI SDK (53 tests passing).
**Output:** `CSharpCliGenerator.Generate(metadata, options)` emits a compilable C# CLI project that wraps the original SDK. Generated project uses System.CommandLine, passes `dotnet build`, and satisfies all agent-readiness requirements.

Split into 5 phases with checkpoints between each.

---

## Context

Read before implementing:
- [cli-builder-spec.md](../cli-builder-spec.md) — output shape (lines 291-329), agent-readiness requirements (lines 333-368), complex parameter policy (lines 214-218), generated code safety (lines 220-226), test strategy (lines 397-427)
- [docs/design-notes.md](../design-notes.md) — auth generation contract, flattening ordering rule, `--json-input` behavior, identifier validation, exit codes, platform-specific notes (LF line endings, forward slashes)
- [docs/ADR.md](../ADR.md) — ADR-006 (generated CLI wraps SDK), ADR-010 (Scriban), ADR-011 (cross-platform), ADR-012 (System.CommandLine), ADR-015 (diagnostics)

Key constraints:
- Generated CLI depends on the original SDK NuGet + System.CommandLine 2.0.5 — **no dependency on cli-builder**
- Templates must look like the output code with `{{ }}` Scriban placeholders
- All generated files use LF (`\n`) line endings, forward slashes in `.csproj` paths
- Identifier validation: C# keyword denylist, `@` prefix for generated code, `-value` suffix for CLI flags
- String sanitization: XML doc descriptions emitted as C# verbatim string literals (`@"..."`)
- Templates are embedded resources in `CliBuilder.Generator.CSharp` (not loose files on disk)

### Security constraints (from council review)

The generator converts metadata strings into C# source code. Two-barrier defense:

1. **Primary control — input sanitization at model mapping (`ModelMapper`):**
   - Neutralize Scriban template syntax (`{{`, `}}`, `{%`, `%}`) in all string values before they reach the template engine
   - Validate all identifiers (resource names, parameter names) against the full C# keyword denylist
   - Validate `ClassName` values are path-safe (no `/`, `\`, `..`, NUL) before use in `Path.Combine`
   - Type-whitelist `DefaultValue` — only emit literal values for known primitive types (string, int, long, double, bool, null). Reject anything else with a diagnostic.
   - Strip or escape C# metacharacters in descriptions (`"`, `\`, `{`, `}`)

2. **Defense-in-depth — template-layer escaping (`escape_csharp` Scriban function):**
   - Registered as a custom function on the Scriban `TemplateContext` (not a built-in — Scriban has no `string.escape_csharp`)
   - Converts strings to C# verbatim string literals (`@"..."`)
   - Acts as a safety net in case model-layer sanitization misses an edge case

3. **Credential masking in generated exception handlers:**
   - Exception messages from SDK calls may contain API keys (common in HTTP client libraries)
   - Generated error handler must sanitize `exception.Message` before emitting to stderr
   - Never include credential values in `--json` error output or diagnostics

### System.CommandLine 2.0.5 API notes (pre-implementation research required)

**IMPORTANT:** The template sketches in this plan are conceptual. Before implementation begins, the implementer must verify the exact API surface of System.CommandLine 2.0.5 (pre-release). Known issues from council review:

- **`SetExceptionHandler` does not exist** in 2.0.x. Use middleware via `CommandLineBuilder` pipeline, or try/catch inside `SetHandler` lambdas.
- **`FromAmong` is not an instance method on `Option<T>`** in 2.0.x. The correct pattern is `option.AcceptOnlyFromAmong(...)` or constructing the option with a custom argument that restricts valid values.
- **`AddGlobalOption`** — verify this exists in 2.0.5 (it does in 2.0-beta4+).

The implementer should write a small spike (throwaway console app) that exercises the exact System.CommandLine 2.0.5 APIs needed before writing templates. This prevents discovering API mismatches in phase 6D (compile verification) after multiple phases of work.

---

## Generated project structure

For input SDK named "TestSdk" with `CliName = "testsdk-cli"`:

```
testsdk-cli/
├── testsdk-cli.csproj        # references TestSdk + System.CommandLine
├── Program.cs                # entry point, root command, subcommand registration
├── Commands/
│   ├── CustomerCommands.cs   # customer create|get|list|delete
│   ├── OrderCommands.cs      # order create|get
│   └── ProductCommands.cs    # product get|list|search
├── Output/
│   ├── JsonFormatter.cs      # --json serialization
│   └── TableFormatter.cs     # human-readable default
└── Auth/
    └── AuthHandler.cs        # env var → config file → --api-key flag
```

---

## Phase 6A: Template Infrastructure + Model Mapping + Project Skeleton

**Goal:** Build the Scriban rendering pipeline, the `SdkMetadata → GeneratorModel` mapping layer with input sanitization, create test scaffolding, generate `.csproj` and minimal `Program.cs`.

### Test project setup

Create `tests/CliBuilder.Generator.Tests/`:
- `CliBuilder.Generator.Tests.csproj` — references `CliBuilder.Generator.CSharp`, `CliBuilder.Core`, xUnit
- Tests load the TestSdk fixture from `tests/fixtures/testsdk-metadata.json`
- Remove the `ProjectReference` to `CliBuilder.Generator.CSharp` from `CliBuilder.Core.Tests.csproj` (generator tests now have their own project)

### Tests to write first

```csharp
// CSharpCliGeneratorTests.cs

[Fact]
public void Generate_CreatesProjectDirectory()
// Generate from TestSdk metadata → output directory exists

[Fact]
public void Generate_CreatesCsprojFile()
// .csproj file exists at {outputDir}/{cliName}/{cliName}.csproj

[Fact]
public void Generate_CreatesProgramCs()
// Program.cs exists at {outputDir}/{cliName}/Program.cs

[Fact]
public void Generate_CsprojReferencesSdkPackage()
// .csproj contains <PackageReference Include="CliBuilder.TestSdk" />

[Fact]
public void Generate_CsprojReferencesSystemCommandLine()
// .csproj contains <PackageReference Include="System.CommandLine" Version="2.0.5" />

[Fact]
public void Generate_ReturnsAllGeneratedFiles()
// GeneratorResult.GeneratedFiles contains all written file paths

[Fact]
public void Generate_UsesCliNameOverride()
// When CliName = "my-tool", project dir is "my-tool", .csproj is "my-tool.csproj"

[Fact]
public void Generate_DefaultCliNameFromSdkName()
// When CliName is null, derives from metadata.Name (e.g., "CliBuilder.TestSdk" → "clibuilder-testsdk-cli")

// --- Degenerate inputs ---

[Fact]
public void Generate_EmptyResources_ProducesMinimalProject()
// SdkMetadata with empty Resources → still generates .csproj + Program.cs, no Commands/

[Fact]
public void Generate_NullDescriptions_DoNotThrow()
// All descriptions null → renders without error, uses empty string or "null" literal

// --- ModelMapper tests (ModelMapperTests.cs) ---

[Fact]
public void ModelMapper_ConvertsKebabCaseToPascalCase()
// "customer" → ClassName "Customer", "payment-intent" → "PaymentIntent"

[Fact]
public void ModelMapper_SanitizesScribanSyntaxInDescriptions()
// "Use {{ template }}" → Scriban syntax neutralized before reaching template engine

[Fact]
public void ModelMapper_SanitizesPathUnsafeClassName()
// Resource name "../etc" or "foo/bar" → diagnostic + safe name, no path traversal

[Fact]
public void ModelMapper_ValidatesIdentifiers()
// Resource/param names that are C# keywords → renamed with @prefix / -value suffix

[Fact]
public void ModelMapper_WhitelistsDefaultValues()
// DefaultValue with known primitive (10, "hello", true, null) → passed through
// DefaultValue with suspicious string → rejected with diagnostic
```

### Implementation

**`ModelMapper.cs`** — the critical sanitization chokepoint (created in 6A):

```csharp
static class ModelMapper
{
    public static GeneratorModel Build(SdkMetadata metadata, GeneratorOptions options)
    {
        var cliName = options.CliName ?? DeriveCliName(metadata.Name);
        var diagnostics = new List<Diagnostic>();

        var resources = metadata.Resources.Select(r =>
        {
            var className = SanitizeClassName(KebabToPascal(r.Name), diagnostics);
            var description = SanitizeString(r.Description);
            // ... map operations, parameters
            return new ResourceModel(r.Name, className, description, operations);
        }).ToList();

        return new GeneratorModel(cliName, metadata.Name, metadata.Version, ...);
    }

    // Neutralize Scriban syntax: {{ → \{\{, }} → \}\}
    static string? SanitizeString(string? value) { ... }

    // Validate ClassName is path-safe (no /, \, .., NUL bytes)
    static string SanitizeClassName(string name, List<Diagnostic> diags) { ... }

    // Type-whitelist DefaultValue — only primitive literals pass through
    static string? SanitizeDefaultValue(object? value, TypeRef type, List<Diagnostic> diags) { ... }
}
```

**`TemplateRenderer.cs`** — Scriban template loading + rendering:

```csharp
class TemplateRenderer
{
    // Load templates from embedded resources
    // Register custom Scriban functions:
    //   escape_csharp — string → C# verbatim string literal (defense-in-depth)
    //   to_pascal — kebab-case → PascalCase
    //   to_csharp_type — TypeRef → C# type name string
    // Configure: Environment.NewLine = "\n"
    // All custom functions registered at construction time, available to all templates
}
```

**IMPORTANT:** `escape_csharp` and all custom Scriban functions are registered in `TemplateRenderer` (6A), not deferred to later phases. Templates in 6B and 6C depend on these functions.

**Template engine infrastructure** in `CSharpCliGenerator.cs`:

```csharp
public class CSharpCliGenerator : ICliGenerator
{
    public GeneratorResult Generate(SdkMetadata metadata, GeneratorOptions options)
    {
        // 1. Map + sanitize
        var (model, diagnostics) = ModelMapper.Build(metadata, options);

        // 2. Create output directory
        var projectDir = Path.Combine(options.OutputDirectory, model.CliName);
        Directory.CreateDirectory(projectDir);

        // 3. Render each template
        var renderer = new TemplateRenderer();
        var files = new List<string>();
        files.Add(renderer.Render("csproj.sbn", projectDir, $"{model.CliName}.csproj", model));
        files.Add(renderer.Render("Program.sbn", projectDir, "Program.cs", model));
        // ... more templates in later phases

        return new GeneratorResult(projectDir, files, diagnostics);
    }
}
```

**Template loading strategy:** Templates are embedded resources in the `CliBuilder.Generator.CSharp` assembly. Load via `Assembly.GetManifestResourceStream`. This keeps templates colocated with the generator and avoids loose file path dependencies.

File layout in the generator project:
```
src/CliBuilder.Generator.CSharp/
├── CSharpCliGenerator.cs          # main generator logic
├── ModelMapper.cs                 # SdkMetadata → GeneratorModel + sanitization
├── TemplateRenderer.cs            # Scriban template loading + rendering + custom functions
├── GeneratorModel.cs              # view model records passed to templates
├── IdentifierValidator.cs         # C# keyword detection + renaming
├── Templates/
│   ├── csproj.sbn                 # project file template
│   ├── Program.sbn                # entry point template
│   ├── ResourceCommands.sbn       # per-resource command file (phase 6B)
│   ├── JsonFormatter.sbn          # JSON output (phase 6C)
│   ├── TableFormatter.sbn         # table output (phase 6C)
│   └── AuthHandler.sbn            # auth handler (phase 6C)
└── CliBuilder.Generator.CSharp.csproj  # <EmbeddedResource Include="Templates\**\*.sbn" />
```

### Template: `csproj.sbn`

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>{{ root_namespace }}</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="{{ sdk_package_name }}" Version="{{ sdk_version }}" />
    <PackageReference Include="System.CommandLine" Version="2.0.5" />
  </ItemGroup>

</Project>
```

### Template: `Program.sbn` (minimal — expanded in 6B)

```csharp
using System.CommandLine;

var rootCommand = new RootCommand({{ cli_description | escape_csharp }});

{{ for resource in resources }}
rootCommand.AddCommand({{ resource.class_name }}Commands.Build());
{{ end }}

return await rootCommand.InvokeAsync(args);
```

### `IdentifierValidator.cs` (created in 6A, used by `ModelMapper`)

```csharp
static class IdentifierValidator
{
    // Full C# reserved keyword list (design-notes.md)
    private static readonly HashSet<string> CSharpKeywords = new()
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch",
        "char", "checked", "class", "const", "continue", "decimal", "default",
        "delegate", "do", "double", "else", "enum", "event", "explicit",
        "extern", "false", "finally", "fixed", "float", "for", "foreach",
        "goto", "if", "implicit", "in", "int", "interface", "internal", "is",
        "lock", "long", "namespace", "new", "null", "object", "operator",
        "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw",
        "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe",
        "ushort", "using", "virtual", "void", "volatile", "while"
    };

    // Contextual keywords (design-notes.md) + C# 9-11 additions
    private static readonly HashSet<string> ContextualKeywords = new()
    {
        "var", "dynamic", "async", "await", "value", "get", "set",
        "add", "remove", "global", "partial", "where", "when", "yield", "nameof",
        // C# 9-11 keywords (net8.0 target)
        "nint", "nuint", "record", "init",
        // C# 11 keywords
        "required", "scoped", "file"
    };

    public static (string csharpName, string cliFlag, Diagnostic? diag) Sanitize(string name)
    {
        if (CSharpKeywords.Contains(name) || ContextualKeywords.Contains(name))
        {
            return ($"@{name}", $"{name}-value",
                new Diagnostic(DiagnosticSeverity.Info, "CB004",
                    $"Parameter '{name}' is a C# keyword — renamed to '@{name}' (code) / '--{name}-value' (CLI)"));
        }
        // Regex check: must match [a-zA-Z_][a-zA-Z0-9_]*
        // Boilerplate name collision check (JsonFormatter, TableFormatter, AuthHandler, Program)
        return (name, PascalToKebab(name), null);
    }
}
```

### Checkpoint 6A

```bash
dotnet test --filter "FullyQualifiedName~Generator"
```

All 6A tests pass. Generator produces a directory with `.csproj` and `Program.cs`. Files contain correct SDK references. Template rendering pipeline works. Model mapping sanitizes inputs. Degenerate inputs handled gracefully.

---

## Phase 6B: Command Generation + Parameter Mapping

**Goal:** Generate `Commands/{Resource}Commands.cs` for each resource. Map parameters to System.CommandLine options. Implement flattening logic. Define `--json-input` precedence merge.

### Tests to write first

```csharp
// --- CSharpCliGeneratorTests.cs ---

// Command file generation
[Fact]
public void Generate_CreatesCommandFilePerResource()
// 3 resources in TestSdk → 3 files in Commands/

[Fact]
public void Generate_CommandFileNaming()
// resource "customer" → Commands/CustomerCommands.cs

[Fact]
public void Generate_GeneratedFileCount_MatchesExpected()
// Assert exact file count — catches spurious or missing files

// Command tree structure
[Fact]
public void Generate_ProgramRegistersAllResources()
// Program.cs contains AddCommand for each resource

// Parameter mapping — primitives
[Fact]
public void Generate_PrimitiveParamsMapToOptions()
// GetAsync(string id) → new Option<string>("--id") { IsRequired = true }

[Fact]
public void Generate_NullableParamIsNotRequired()
// string? cursor → IsRequired = false

[Fact]
public void Generate_DefaultValueIsSet()
// int limit = 10 → SetDefaultValue(10) — value is type-whitelisted by ModelMapper

// Flattening — options classes
[Fact]
public void Generate_SmallOptionsClass_FlattensAllProperties()
// CreateCustomerOptions (10 scalar props) → 10 individual --flags

[Fact]
public void Generate_LargeOptionsClass_FlattensFirstTenPlusJsonInput()
// CreateOrderOptions (15 props) → 10 --flags + --json-input

[Fact]
public void Generate_FlatteningOrder_RequiredFirst_ThenAlphabetical()
// Required props come first in flattened params, then optional alphabetically

[Fact]
public void Generate_NestedObject_FlattensScalarsAndAddsJsonInput()
// Options with nested Address sub-object → all scalar props flattened + --json-input

// Enum params
[Fact]
public void Generate_EnumParam_GeneratesChoices()
// CustomerStatus enum → option restricted to ("active", "inactive", "suspended")
// Uses correct System.CommandLine 2.0.5 API (NOT FromAmong instance method)

// Streaming marker (informational)
[Fact]
public void Generate_StreamingOp_MarkedInHelpText()
// IsStreaming = true → help text includes "[streaming]" marker

// --json-input precedence
[Fact]
public void Generate_JsonInputOption_PrecedenceMergeInHandler()
// When --json-input and flat flags both present, handler code applies
// --json-input first, then flat flags override on top (per design-notes.md)

// --- ParameterFlattenerTests.cs (isolated unit tests) ---

[Fact]
public void Flatten_EmptyParameters_ReturnsEmpty()
// No params → no flat params, no --json-input

[Fact]
public void Flatten_SinglePrimitive_ReturnsFlatParam()

[Fact]
public void Flatten_OptionsClassExactlyAtThreshold_FlattensAll()
// 10 scalar props → 10 flat params, needsJsonInput = false

[Fact]
public void Flatten_OptionsClassAboveThreshold_FlattensFirstTenPlusJsonInput()
// 15 scalar props → 10 flat params, needsJsonInput = true

[Fact]
public void Flatten_OptionsClassWithNested_FlattensAllScalarsPlusJsonInput()
// 5 scalar + 1 nested → 5 flat params, needsJsonInput = true

[Fact]
public void Flatten_AllRequiredBeyondThreshold_EmitsCB301()
// Synthetic: 12 required scalar props → first 10 flat, CB301 for props 11-12

[Fact]
public void Flatten_SortOrder_RequiredFirst_ThenAlphabetical()

[Fact]
public void Flatten_OptionsClassWithZeroScalarProps_OnlyJsonInput()
// All non-scalar props → needsJsonInput = true, zero flat params

[Fact]
public void Flatten_TwoOptionsClasses_CombinedThreshold()
// Two class-type params on same operation — verify combined behavior
```

### Implementation

**Flattening logic** (`ParameterFlattener.cs`):

```csharp
static class ParameterFlattener
{
    public static FlattenResult Flatten(
        IReadOnlyList<Parameter> parameters, int threshold = 10)
    {
        var flatParams = new List<FlatParameter>();
        var needsJsonInput = false;
        var diagnostics = new List<Diagnostic>();

        foreach (var param in parameters)
        {
            if (param.Type.Kind == TypeKind.Class && param.Type.Properties != null)
            {
                // Options class — flatten scalar properties
                var props = param.Type.Properties
                    .Where(p => IsScalar(p.Type))
                    .OrderBy(p => !p.Required)  // required first
                    .ThenBy(p => p.Name)         // alphabetical
                    .ToList();

                var hasNested = param.Type.Properties.Any(p => !IsScalar(p.Type));

                if (hasNested)
                {
                    // Nested objects present → always add --json-input,
                    // but still flatten ALL scalar props (no threshold truncation)
                    needsJsonInput = true;
                    flatParams.AddRange(props.Select(ToFlat));
                }
                else if (props.Count > threshold)
                {
                    // Too many scalars → flatten first {threshold}, add --json-input
                    needsJsonInput = true;
                    flatParams.AddRange(props.Take(threshold).Select(ToFlat));

                    // Emit CB301 for required props beyond threshold
                    foreach (var hidden in props.Skip(threshold).Where(p => p.Required))
                    {
                        diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, "CB301",
                            $"Required parameter '{hidden.Name}' is only accessible " +
                            "via --json-input due to flatten threshold."));
                    }
                }
                else
                {
                    // All scalar, within threshold → flatten all
                    flatParams.AddRange(props.Select(ToFlat));
                }
            }
            else
            {
                // Primitive / enum param — always flat
                flatParams.Add(ToFlat(param));
            }
        }

        return new FlattenResult(flatParams, needsJsonInput, diagnostics);
    }

    static bool IsScalar(TypeRef type) =>
        type.Kind is TypeKind.Primitive or TypeKind.Enum;
}
```

**View model** records (in `GeneratorModel.cs`, defined in 6A, populated in 6B):

```csharp
record GeneratorModel(
    string CliName,
    string SdkName,
    string SdkVersion,
    string SdkPackageName,     // NuGet package name (= assembly name for now)
    string RootNamespace,
    IReadOnlyList<ResourceModel> Resources,
    AuthModel? Auth
);

record ResourceModel(
    string Name,               // kebab-case: "customer"
    string ClassName,          // PascalCase: "Customer" (sanitized, path-safe)
    string? Description,       // sanitized (Scriban syntax neutralized)
    IReadOnlyList<OperationModel> Operations
);

record OperationModel(
    string Name,               // kebab-case: "create"
    string MethodName,         // PascalCase: "Create"
    string? Description,       // sanitized
    IReadOnlyList<FlatParameter> Parameters,
    bool NeedsJsonInput,
    string ReturnTypeName,     // C# type name for the return
    bool IsStreaming
);

record FlatParameter(
    string CliFlag,            // email, limit (without -- prefix, added in template)
    string PropertyName,       // C# property name for accessing on options class
    string CSharpType,         // string, int, bool, etc.
    bool IsRequired,
    string? DefaultValueLiteral, // pre-sanitized C# literal: "10", @"""hello""", "null"
    string? Description,       // sanitized
    IReadOnlyList<string>? EnumValues  // for restricting valid values
);
```

Note: `DefaultValueLiteral` is a pre-sanitized string ready to emit into C# source. The `ModelMapper` type-whitelists the original `JsonElement?` default value and converts it to a safe C# literal representation. No raw `object?` values reach the template.

### Template: `ResourceCommands.sbn` (sketch — verify against actual 2.0.5 API)

```csharp
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;

namespace {{ root_namespace }}.Commands;

public static class {{ resource.class_name }}Commands
{
    public static Command Build()
    {
        var command = new Command("{{ resource.name }}", {{ resource.description | escape_csharp }});
{{ for op in resource.operations }}

        // {{ op.name }}{{ if op.is_streaming }} [streaming]{{ end }}
        {
            var cmd = new Command("{{ op.name }}", {{ op.description | escape_csharp }});
{{ for param in op.parameters }}
            var {{ param.property_name | string.handleize }}Option = new Option<{{ param.csharp_type }}>(
                "--{{ param.cli_flag }}",
                {{ param.description | escape_csharp }})
            { IsRequired = {{ param.is_required | string.downcase }} };
{{ if param.default_value_literal }}
            {{ param.property_name | string.handleize }}Option.SetDefaultValue({{ param.default_value_literal }});
{{ end }}
{{## Enum restriction — use correct 2.0.5 API (verify before implementation) ##}}
{{ if param.enum_values }}
            {{ param.property_name | string.handleize }}Option.AcceptOnlyFromAmong({{ param.enum_values | array.each @(do; ret '"' + $0 + '"'; end) | array.join ", " }});
{{ end }}
            cmd.AddOption({{ param.property_name | string.handleize }}Option);
{{ end }}
{{ if op.needs_json_input }}
            var jsonInputOption = new Option<string>("--json-input", "Full input as JSON");
            cmd.AddOption(jsonInputOption);
{{ end }}
            cmd.SetHandler(async (InvocationContext ctx) =>
            {
                // Handler implementation — phase 6C
                // --json-input precedence: apply JSON first, then flat flags override
            });
            command.AddCommand(cmd);
        }
{{ end }}

        return command;
    }
}
```

**Note:** This template is a sketch. The `AcceptOnlyFromAmong`, `SetHandler`, and `InvocationContext` API calls must be verified against the actual System.CommandLine 2.0.5 API surface before implementation (see API notes in Context section).

### Checkpoint 6B

```bash
dotnet test --filter "FullyQualifiedName~Generator"
```

All 6A + 6B tests pass. Generator produces command files with correct verb structure, parameter flags, flattening behavior, and `--json-input` where needed. `ParameterFlattener` has isolated unit tests covering all boundary cases including `CB301`.

---

## Phase 6C: Output Formatters + Auth Handler + Agent Readiness

**Goal:** Generate the full set of support files. Wire command handlers with SDK calls, output formatting, and `--json-input` precedence merge. Generated CLI satisfies all agent-readiness requirements from the spec.

### Tests to write first

```csharp
// File existence
[Fact]
public void Generate_CreatesJsonFormatterCs()
[Fact]
public void Generate_CreatesTableFormatterCs()
[Fact]
public void Generate_CreatesAuthHandlerCs()

// Auth — presence based on metadata
[Fact]
public void Generate_WithApiKeyAuth_AuthHandlerReadsEnvVar()
// AuthPattern with EnvVar = "TESTSDK_API_KEY" → generated code checks that env var

[Fact]
public void Generate_WithApiKeyAuth_AuthHandlerHasPrecedenceChain()
// Generated code has env var check BEFORE config file check BEFORE flag check
// Verify structural ordering, not just string presence

[Fact]
public void Generate_WithApiKeyAuth_AuthHandlerWarnsOnFlagUsage()
// Generated code emits stderr warning when --api-key flag is the credential source

[Fact]
public void Generate_WithNoAuth_SkipsAuthHandler()
// Empty AuthPatterns → no Auth/ directory, no auth options on commands

// --json global option
[Fact]
public void Generate_ProgramHasJsonGlobalOption()
// Program.cs wires a global --json option

// Handler wiring
[Fact]
public void Generate_HandlerCallsSdkMethod()
// Generated handler code instantiates the SDK client and calls the correct method

[Fact]
public void Generate_HandlerFormatsOutputAsJson()
// When --json is set, handler uses JsonFormatter

[Fact]
public void Generate_HandlerUsesTableByDefault()
// When --json is not set, handler uses TableFormatter

[Fact]
public void Generate_HandlerMergesJsonInputWithFlatFlags()
// Generated handler: deserialize --json-input first, then overlay flat flag values

// Exit codes
[Fact]
public void Generate_HandlerSetsExitCodes()
// Generated handler uses exit codes 0, 1, 2, 3 per spec

// Error handling — structured + credential-safe
[Fact]
public void Generate_ErrorHandlerSanitizesExceptionMessage()
// Generated error handler does NOT emit exception.Message raw —
// masks credential patterns before writing to stderr

// Pipe-friendly
[Fact]
public void Generate_OutputDisablesColorWhenRedirected()
// Generated code checks Console.IsOutputRedirected

// Void return type
[Fact]
public void Generate_VoidReturnOp_DoesNotFormatOutput()
// Operation with void return → handler prints success message, no formatter call
```

### Implementation

**Template: `JsonFormatter.sbn`**

Responsibilities:
- Accept any object, serialize to JSON via `System.Text.Json`
- Write to stdout
- Include `JsonSerializerOptions` with camelCase + indented

**Template: `TableFormatter.sbn`**

Responsibilities:
- Accept any object, render as a simple property table (key-value pairs)
- For collections, render as a text table with columns from property names
- No color when `Console.IsOutputRedirected` is true

**Template: `AuthHandler.sbn`**

Must implement the contract from design-notes.md:
1. **Credential resolution** with strict precedence:
   - Environment variable (from `AuthPattern.EnvVar`) — checked first
   - Config file at `{AppData}/{cli-name}/config.json` — checked second
   - `--api-key` flag — last resort only
2. **Emit stderr warning** when `--api-key` flag is used
3. **Token caching** — write to config file (cross-platform via `SpecialFolder.ApplicationData`)
4. **Credential masking** — never include credential values in error output

**Generated handler wiring (in `ResourceCommands.sbn`, replacing the stub from 6B):**

The handler body for each command must:
1. Resolve auth credential via `AuthHandler`
2. If `--json-input` is provided, deserialize as base object
3. Apply flat flag values on top (flat flags override `--json-input` properties)
4. Instantiate the SDK client with credentials
5. Call the appropriate SDK method
6. Format output via `JsonFormatter` (if `--json`) or `TableFormatter` (default)
7. On success: exit 0
8. On user error (missing param, bad argument): exit 1
9. On auth error: exit 2
10. On SDK error: sanitize `exception.Message` (mask credentials), emit structured error JSON to stderr, exit 3

**Generated error handling (replaces the non-existent `SetExceptionHandler`):**

```csharp
// In each command handler's SetHandler body:
try
{
    // ... SDK call, formatting ...
    ctx.ExitCode = 0;
}
catch (AuthException ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new {
        error = new { code = "auth_error", message = SanitizeMessage(ex.Message) }
    }));
    ctx.ExitCode = 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new {
        error = new { code = "sdk_error", message = SanitizeMessage(ex.Message) }
    }));
    ctx.ExitCode = 3;
}

// SanitizeMessage masks potential credential patterns
static string SanitizeMessage(string message) =>
    Regex.Replace(message, @"(sk-|Bearer |api[_-]?key[=:]\s*)\S+", "$1***");
```

**Note:** The exact try/catch pattern depends on the System.CommandLine 2.0.5 API surface. If middleware is available, a global error handler is preferable to per-command try/catch. Verify during the pre-implementation API spike.

### Checkpoint 6C

```bash
dotnet test --filter "FullyQualifiedName~Generator"
```

All 6A + 6B + 6C tests pass. Generator produces all file types (`.csproj`, `Program.cs`, command files, formatters, auth handler). Command handlers wire SDK calls, output formatting, error handling, and `--json-input` merge. Auth respects metadata — only generated when auth patterns exist.

---

## Phase 6D: Golden Files + Compile Verification + Fuzz Tests

**Goal:** Golden file tests for regression detection. **The generated project compiles.** Fuzz tests for injection safety.

### Tests to write first

```csharp
// Cross-platform
[Fact]
public void Generate_AllFilesUseLfLineEndings()
// Read every generated file, assert no \r\n

[Fact]
public void Generate_CsprojUsesForwardSlashes()
// .csproj paths use / not \

// Golden file comparison
[Fact]
public void Generate_TestSdk_MatchesGoldenFile_Csproj()
[Fact]
public void Generate_TestSdk_MatchesGoldenFile_ProgramCs()
[Fact]
public void Generate_TestSdk_MatchesGoldenFile_CustomerCommands()
[Fact]
public void Generate_TestSdk_MatchesGoldenFile_JsonFormatter()
[Fact]
public void Generate_TestSdk_MatchesGoldenFile_AuthHandler()
// Compare generated output against committed golden files

[Fact]
public void Generate_TestSdk_GoldenFileCount_MatchesGeneratedFileCount()
// Catch extra/missing files — golden dir file count must match generated file count

// Compile verification — the critical test
[Fact]
public void Generate_TestSdk_CompilesWithDotnetBuild()
// Generate the project, dotnet build → exit code 0
// Uses GeneratorOptions to emit ProjectReference for testing (see below)

// --- Fuzz / injection tests ---

[Fact]
public void Generate_ScribanSyntaxInDescription_DoesNotExecute()
// Description: "Use {{ env 'SECRET' }}" → rendered as escaped string, not executed

[Fact]
public void Generate_CSharpInjectionInDescription_Escaped()
// Description: '"; Process.Start("malware");//' → safely escaped in output

[Fact]
public void Generate_PathTraversalInResourceName_Rejected()
// Resource name "../etc/passwd" → diagnostic, safe name used

[Fact]
public void Generate_DefaultValueInjection_Rejected()
// DefaultValue containing C# code → rejected by type-whitelist, diagnostic emitted

[Fact]
public void Generate_CSharpKeywordParam_RenamedWithVerbatimPrefix()
// Parameter named "class" → @class in generated C#, --class-value in CLI

[Fact]
public void Generate_CSharp11KeywordParam_RenamedCorrectly()
// Parameter named "required" or "record" → @required / --required-value
```

### Implementation

**LF enforcement:**

```csharp
// In TemplateRenderer, after Scriban produces output:
var output = rendered.Replace("\r\n", "\n");
File.WriteAllText(path, output, new UTF8Encoding(false)); // no BOM
```

**Golden file strategy:**

Golden files live in `tests/golden/testsdk-cli/` mirroring the generated output structure. Tests generate to a temp directory, then compare file-by-file against golden files. To update golden files: run generator with an env var `UPDATE_GOLDEN=1`.

The golden file comparison test also asserts that the number of golden files matches the number of generated files — this prevents regressions where the generator silently adds or drops files.

**Compile verification strategy:**

The generator accepts a `SdkAssemblyPath` option (on `GeneratorOptions` or a test-specific extension) to emit a `ProjectReference` instead of `PackageReference` when the SDK is a local project rather than a NuGet package. This is a first-class generator capability, not a post-generation hack:

```csharp
public record GeneratorOptions(
    string OutputDirectory,
    string? CliName = null,
    bool OverwriteExisting = false,
    string? SdkProjectPath = null  // if set, emit ProjectReference instead of PackageReference
);
```

The compile verification test:
1. Generate the CLI project with `SdkProjectPath` pointing to `CliBuilder.TestSdk.csproj`
2. Run `dotnet build` on the generated project
3. Assert exit code 0

This avoids fragile post-generation `.csproj` rewriting.

### Checkpoint 6D

```bash
dotnet test --filter "FullyQualifiedName~Generator"
dotnet build /path/to/generated/testsdk-cli/  # manual verification
```

All tests pass. Generated project compiles. Golden files are committed. Every generated file uses LF line endings. C# keywords (including C# 9-11 additions) are handled. Fuzz tests confirm injection safety.

---

## Phase 6E: OpenAI SDK Scale Validation

**Goal:** Validate the generator at scale against the real OpenAI SDK fixture (20 resources, ~200 operations). Fix edge cases revealed by the larger surface.

### Tests to write

```csharp
// In CliBuilder.Integration.Tests (or a new generator integration test file)

[Fact]
public void GenerateOpenAi_ProducesExpectedFileCount()
// 20 resources → 20 command files + csproj + Program.cs + formatters + auth
// Assert exact count computed from fixture, not approximated

[Fact]
public void GenerateOpenAi_AllFilesUseLfLineEndings()

[Fact]
public void GenerateOpenAi_Compiles()
// Generate from openai-metadata.json, dotnet build → exit code 0
// Requires OpenAI NuGet package as PackageReference

[Fact]
public void GenerateOpenAi_NoDiagnosticErrors()
// Generator may produce Info/Warning diagnostics, but no Errors
```

### Implementation

Steps:
1. Load `tests/fixtures/openai-metadata.json` → `SdkMetadata`
2. Run `CSharpCliGenerator.Generate(metadata, options)`
3. Inspect the generated project for:
   - Correct file count (one command file per resource)
   - All operation names are valid C# identifiers
   - All parameter names are sanitized
   - `.csproj` references `OpenAI` NuGet package
4. `dotnet build` the generated project
5. Fix any edge cases:
   - Parameters with unusual types (streaming response types, `BinaryData`, etc.)
   - Very long type names that exceed identifier limits
   - Resource names that collide with C# namespace keywords

### Expected edge cases from OpenAI fixture

Based on the adapter output (from step 5D):
- Operations returning `void` (non-generic Task unwrapping) — handler must not try to format null
- `BinaryData` parameter types — map to `string` with a note that it accepts file paths or base64
- Streaming operations — handler should note streaming behavior in help text, but actual streaming is out of scope for v1 (the command still calls the SDK, just doesn't stream output incrementally)
- ~200 operations means ~200 `SetHandler` lambdas — verify System.CommandLine doesn't have performance issues with large command trees

### Checkpoint 6E

```bash
dotnet test --filter "FullyQualifiedName~Generator"
dotnet test --filter "FullyQualifiedName~OpenAi"  # integration tests
```

All tests pass. The generated OpenAI CLI compiles. The generator handles 20 resources and ~200 operations without errors. Step 6 is complete.

---

## File manifest

Files created/modified during step 6:

**New files:**
| File | Phase | Purpose |
|------|-------|---------|
| `src/CliBuilder.Generator.CSharp/ModelMapper.cs` | 6A | SdkMetadata → GeneratorModel mapping + input sanitization |
| `src/CliBuilder.Generator.CSharp/TemplateRenderer.cs` | 6A | Scriban template loading + rendering + custom functions |
| `src/CliBuilder.Generator.CSharp/GeneratorModel.cs` | 6A | View model records for templates |
| `src/CliBuilder.Generator.CSharp/IdentifierValidator.cs` | 6A | C# keyword detection + renaming (full denylist incl. C# 9-11) |
| `src/CliBuilder.Generator.CSharp/ParameterFlattener.cs` | 6B | Flattening logic for options classes |
| `src/CliBuilder.Generator.CSharp/Templates/csproj.sbn` | 6A | .csproj template |
| `src/CliBuilder.Generator.CSharp/Templates/Program.sbn` | 6A/6B | Program.cs template |
| `src/CliBuilder.Generator.CSharp/Templates/ResourceCommands.sbn` | 6B | Per-resource commands template |
| `src/CliBuilder.Generator.CSharp/Templates/JsonFormatter.sbn` | 6C | JSON output formatter template |
| `src/CliBuilder.Generator.CSharp/Templates/TableFormatter.sbn` | 6C | Table output formatter template |
| `src/CliBuilder.Generator.CSharp/Templates/AuthHandler.sbn` | 6C | Auth handler template |
| `tests/CliBuilder.Generator.Tests/CliBuilder.Generator.Tests.csproj` | 6A | Test project |
| `tests/CliBuilder.Generator.Tests/CSharpCliGeneratorTests.cs` | 6A-6D | End-to-end generator tests |
| `tests/CliBuilder.Generator.Tests/ModelMapperTests.cs` | 6A | Isolated mapping + sanitization tests |
| `tests/CliBuilder.Generator.Tests/ParameterFlattenerTests.cs` | 6B | Isolated flattening unit tests |
| `tests/golden/testsdk-cli/**` | 6D | Golden files for TestSdk fixture |

**Modified files:**
| File | Phase | Change |
|------|-------|--------|
| `src/CliBuilder.Generator.CSharp/CSharpCliGenerator.cs` | 6A | Replace `NotImplementedException` with real implementation |
| `src/CliBuilder.Generator.CSharp/CliBuilder.Generator.CSharp.csproj` | 6A | Add `<EmbeddedResource>` for templates |
| `src/CliBuilder.Core/Models/GeneratorOptions.cs` | 6D | Add `SdkProjectPath` for ProjectReference compile testing |
| `cli-builder.sln` | 6A | Add `CliBuilder.Generator.Tests` project |
| `docs/cli-builder-spec.md` | 6E | Mark step 6 complete in First Actions |
| `AGENTS.md` | 6E | Update start pointer to step 7 |

---

## Key design decisions for the implementer

1. **Templates as embedded resources** — not loose files. This avoids "template not found" issues when running from different directories. The `.csproj` uses `<EmbeddedResource Include="Templates\**\*.sbn" />`.

2. **Two-barrier sanitization** — Primary: `ModelMapper` sanitizes all inputs (Scriban syntax, identifiers, default values, path safety) before they reach the template engine. Defense-in-depth: `escape_csharp` Scriban function in templates as a safety net. Both are implemented in 6A.

3. **View model is a separate layer** — `SdkMetadata` is the adapter's contract. `GeneratorModel` is the generator's view of that data, with pre-computed values (PascalCase class names, C# type strings, flattened parameters, sanitized descriptions). The `SdkMetadata → GeneratorModel` mapping is testable in isolation via `ModelMapperTests.cs`.

4. **Scriban custom functions** — registered on `TemplateContext` in `TemplateRenderer` constructor (not built-in Scriban functions). Includes: `escape_csharp` (string → verbatim string literal), `to_pascal` (kebab → PascalCase), `to_csharp_type` (TypeRef → C# type name). Registered in 6A, available to all templates from the start.

5. **Compile verification uses `SdkProjectPath`** — a first-class `GeneratorOptions` field that causes the generator to emit `ProjectReference` instead of `PackageReference`. Avoids fragile post-generation `.csproj` rewriting in tests.

6. **Pre-implementation API spike** — before writing templates, verify System.CommandLine 2.0.5 API surface in a throwaway console app. Known issues: `SetExceptionHandler` doesn't exist, `FromAmong` is not an instance method, enum restriction API may differ.

7. **Golden file update workflow** — `UPDATE_GOLDEN=1 dotnet test` regenerates golden files. Normal test runs compare against committed golden files. File count assertion catches spurious or missing files.

8. **Diagnostic codes for generator** — `CB3xx` range per design-notes.md:
   - `CB301` — Required parameter hidden behind `--json-input`
   - `CB302` — Template rendering warning
   - `CB303` — Generated file path exceeds platform limit

---

## Council review notes

This plan was reviewed by a Developer Council (SoftwareDeveloper, QaTester, SecurityArchitect) in a 3-round debate. Key findings incorporated:

- **System.CommandLine API mismatches:** `SetExceptionHandler`, `FromAmong` do not exist in 2.0.5. Plan now requires a pre-implementation API spike.
- **Scriban template injection:** `{{ }}` in metadata strings is parsed before filters run. Fixed by adding `ModelMapper` as the primary sanitization chokepoint in 6A.
- **`DefaultValue` injection:** Raw `object?` values in templates → type-whitelisted `string?` literals via `ModelMapper`.
- **Path traversal via resource names:** `ClassName` validated in `ModelMapper` before reaching `Path.Combine`.
- **Missing `ModelMapper.cs`:** Added to file manifest and 6A scope with isolated tests.
- **`ParameterFlattener` branch logic:** Split into three branches (nested, over-threshold, normal) instead of two.
- **C# 9-11 keywords:** Added `nint`, `nuint`, `record`, `required`, `scoped`, `file`, `init` to denylist.
- **Degenerate input tests:** Added to 6A (empty resources, null descriptions).
- **Fuzz tests:** Added to 6D (Scriban injection, C# injection, path traversal, `DefaultValue` injection).
- **`CB301` synthetic fixture:** Added to `ParameterFlattenerTests.cs` in 6B.
- **Compile verification:** `SdkProjectPath` on `GeneratorOptions` replaces fragile post-generation rewriting.
- **File count regression test:** Added to 6B and golden file tests in 6D.
- **Credential masking in exception handlers:** Generated error handlers sanitize `exception.Message` before emitting.
