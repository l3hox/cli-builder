# Step 4: Scaffold — Adapter Interface, .NET Adapter, Metadata Model

**Prerequisite:** .NET 8 SDK installed, `.gitignore` in place.
**Output:** Solution compiles, test project runs, `SdkMetadata` serializes to JSON and back.

---

## Context

Read before implementing:
- [cli-builder-spec.md](../cli-builder-spec.md) — interface signatures (lines 114-157), metadata model (lines 159-197)
- [docs/ADR.md](../ADR.md) — ADR-002 (.NET 8), ADR-004 (monolith), ADR-005 (SdkMetadata contract), ADR-009 (TDD), ADR-010 (Scriban), ADR-012 (System.CommandLine), ADR-015 (diagnostics)
- [docs/design-notes.md](../design-notes.md) — identifier validation rules, diagnostic code assignments

---

## Solution structure

```
cli-builder/
├── cli-builder.sln
├── global.json                          # pin SDK version
├── src/
│   ├── CliBuilder/                      # CLI entry point (console app)
│   │   └── CliBuilder.csproj
│   ├── CliBuilder.Core/                 # Models, interfaces, shared types
│   │   └── CliBuilder.Core.csproj
│   ├── CliBuilder.Adapter.DotNet/       # .NET reflection adapter
│   │   └── CliBuilder.Adapter.DotNet.csproj
│   └── CliBuilder.Generator.CSharp/     # C# / System.CommandLine code generator
│       └── CliBuilder.Generator.CSharp.csproj
├── tests/
│   ├── CliBuilder.Core.Tests/           # Model tests (serialization, round-trip)
│   │   └── CliBuilder.Core.Tests.csproj
│   └── CliBuilder.TestSdk/             # Purpose-built test SDK assembly (class library)
│       └── CliBuilder.TestSdk.csproj
├── templates/                           # Scriban templates (embedded in generator)
├── docs/cli-builder-spec.md
├── docs/
├── AGENTS.md
├── README.md
├── FUTURE.md
├── LICENSE
└── .gitignore
```

**Project dependency graph:**
```
CliBuilder (console app)
├── CliBuilder.Core
├── CliBuilder.Adapter.DotNet
└── CliBuilder.Generator.CSharp

CliBuilder.Adapter.DotNet
└── CliBuilder.Core

CliBuilder.Generator.CSharp
└── CliBuilder.Core

CliBuilder.Core.Tests
└── CliBuilder.Core

(CliBuilder.TestSdk has no project references — it's an independent assembly used as test input)
```

---

## Steps

### 1. Create `global.json`

Pin .NET SDK version for reproducible builds.

```json
{
  "sdk": {
    "version": "8.0.400",
    "rollForward": "latestPatch"
  }
}
```

### 2. Create solution and projects

```bash
dotnet new sln -n cli-builder

# Core library (models, interfaces)
dotnet new classlib -n CliBuilder.Core -o src/CliBuilder.Core -f net8.0
dotnet sln add src/CliBuilder.Core

# .NET adapter
dotnet new classlib -n CliBuilder.Adapter.DotNet -o src/CliBuilder.Adapter.DotNet -f net8.0
dotnet sln add src/CliBuilder.Adapter.DotNet

# C# generator
dotnet new classlib -n CliBuilder.Generator.CSharp -o src/CliBuilder.Generator.CSharp -f net8.0
dotnet sln add src/CliBuilder.Generator.CSharp

# CLI entry point
dotnet new console -n CliBuilder -o src/CliBuilder -f net8.0
dotnet sln add src/CliBuilder

# Test project
dotnet new xunit -n CliBuilder.Core.Tests -o tests/CliBuilder.Core.Tests -f net8.0
dotnet sln add tests/CliBuilder.Core.Tests

# Test SDK assembly (independent class library, no project refs)
dotnet new classlib -n CliBuilder.TestSdk -o tests/CliBuilder.TestSdk -f net8.0
dotnet sln add tests/CliBuilder.TestSdk
```

### 3. Delete auto-generated `Class1.cs` files

```bash
rm src/CliBuilder.Core/Class1.cs
rm src/CliBuilder.Adapter.DotNet/Class1.cs
rm src/CliBuilder.Generator.CSharp/Class1.cs
rm tests/CliBuilder.TestSdk/Class1.cs
```

(The xUnit template generates `UnitTest1.cs` — delete that too: `rm tests/CliBuilder.Core.Tests/UnitTest1.cs`)

### 4. Add project references

```bash
# Adapter depends on Core
dotnet add src/CliBuilder.Adapter.DotNet reference src/CliBuilder.Core

# Generator depends on Core
dotnet add src/CliBuilder.Generator.CSharp reference src/CliBuilder.Core

# CLI depends on all
dotnet add src/CliBuilder reference src/CliBuilder.Core
dotnet add src/CliBuilder reference src/CliBuilder.Adapter.DotNet
dotnet add src/CliBuilder reference src/CliBuilder.Generator.CSharp

# Tests depend on Core
dotnet add tests/CliBuilder.Core.Tests reference src/CliBuilder.Core
```

### 5. Add NuGet packages

```bash
# Core — JSON serialization
dotnet add src/CliBuilder.Core package System.Text.Json

# Adapter — metadata loading
dotnet add src/CliBuilder.Adapter.DotNet package System.Reflection.MetadataLoadContext

# Generator — templates
dotnet add src/CliBuilder.Generator.CSharp package Scriban

# CLI — command-line parsing
dotnet add src/CliBuilder package System.CommandLine
```

### 6. Create model classes in `CliBuilder.Core/Models/`

Convention: one record per file. Enums co-located with their primary record (e.g., `TypeKind` lives in `TypeRef.cs`).

**`src/CliBuilder.Core/Models/SdkMetadata.cs`**
```csharp
namespace CliBuilder.Core.Models;

public record SdkMetadata(
    string Name,
    string Version,
    IReadOnlyList<Resource> Resources,
    IReadOnlyList<AuthPattern> AuthPatterns
);
```

**`src/CliBuilder.Core/Models/Resource.cs`**
```csharp
namespace CliBuilder.Core.Models;

public record Resource(
    string Name,
    string? Description,
    IReadOnlyList<Operation> Operations
);
```

**`src/CliBuilder.Core/Models/Operation.cs`**
```csharp
namespace CliBuilder.Core.Models;

public record Operation(
    string Name,
    string? Description,
    IReadOnlyList<Parameter> Parameters,
    TypeRef ReturnType
);
```

**`src/CliBuilder.Core/Models/Parameter.cs`**
```csharp
using System.Text.Json;

namespace CliBuilder.Core.Models;

public record Parameter(
    string Name,
    TypeRef Type,
    bool Required,
    JsonElement? DefaultValue = null,
    string? Description = null
);
```

**`src/CliBuilder.Core/Models/TypeRef.cs`**
```csharp
namespace CliBuilder.Core.Models;

public record TypeRef(
    TypeKind Kind,
    string Name,
    bool IsNullable = false,
    IReadOnlyList<TypeRef>? GenericArguments = null,
    IReadOnlyList<string>? EnumValues = null,
    IReadOnlyList<Parameter>? Properties = null,
    TypeRef? ElementType = null
);

public enum TypeKind
{
    Primitive,
    Enum,
    Class,
    Generic,
    Array,
    Dictionary
}
```

**`src/CliBuilder.Core/Models/AuthPattern.cs`**
```csharp
namespace CliBuilder.Core.Models;

public record AuthPattern(
    AuthType Type,
    string EnvVar,
    string ParameterName,
    string? HeaderName = null,
    string? Description = null
);

public enum AuthType
{
    ApiKey,
    BearerToken,
    OAuth,
    Custom
}
```

**`src/CliBuilder.Core/Models/Diagnostic.cs`**
```csharp
namespace CliBuilder.Core.Models;

public record Diagnostic(
    DiagnosticSeverity Severity,
    string Code,
    string Message
);

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}
```

### 7. Create interfaces and companion records in `CliBuilder.Core`

Interfaces live in their own namespace directories. Companion records (`AdapterOptions`, `AdapterResult`, etc.) are separate files in `Models/` — not co-located inside interface files.

**`src/CliBuilder.Core/Adapters/ISdkAdapter.cs`**
```csharp
using CliBuilder.Core.Models;

namespace CliBuilder.Core.Adapters;

public interface ISdkAdapter
{
    AdapterResult Extract(AdapterOptions options);
}
```

**`src/CliBuilder.Core/Models/AdapterOptions.cs`**
```csharp
namespace CliBuilder.Core.Models;

public record AdapterOptions(
    string AssemblyPath,
    string? ConfigPath = null,
    string? XmlDocPath = null
);
```

**`src/CliBuilder.Core/Models/AdapterResult.cs`**
```csharp
namespace CliBuilder.Core.Models;

public record AdapterResult(
    SdkMetadata Metadata,
    IReadOnlyList<Diagnostic> Diagnostics
);
```

**`src/CliBuilder.Core/Generators/ICliGenerator.cs`**
```csharp
using CliBuilder.Core.Models;

namespace CliBuilder.Core.Generators;

public interface ICliGenerator
{
    GeneratorResult Generate(SdkMetadata metadata, GeneratorOptions options);
}
```

**`src/CliBuilder.Core/Models/GeneratorOptions.cs`**
```csharp
namespace CliBuilder.Core.Models;

public record GeneratorOptions(
    string OutputDirectory,
    string? CliName = null,
    bool OverwriteExisting = false
);
```

**`src/CliBuilder.Core/Models/GeneratorResult.cs`**
```csharp
namespace CliBuilder.Core.Models;

public record GeneratorResult(
    string ProjectDirectory,
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<Diagnostic> Diagnostics
);
```

### 8. Create skeleton implementations

**`src/CliBuilder.Adapter.DotNet/DotNetAdapter.cs`**
```csharp
using CliBuilder.Core.Adapters;
using CliBuilder.Core.Models;

namespace CliBuilder.Adapter.DotNet;

public class DotNetAdapter : ISdkAdapter
{
    public AdapterResult Extract(AdapterOptions options)
    {
        throw new NotImplementedException("DotNetAdapter.Extract — implementation in step 5");
    }
}
```

**`src/CliBuilder.Generator.CSharp/CSharpCliGenerator.cs`**
```csharp
using CliBuilder.Core.Generators;
using CliBuilder.Core.Models;

namespace CliBuilder.Generator.CSharp;

public class CSharpCliGenerator : ICliGenerator
{
    public GeneratorResult Generate(SdkMetadata metadata, GeneratorOptions options)
    {
        throw new NotImplementedException("CSharpCliGenerator.Generate — implementation in step 6");
    }
}
```

### 9. Write tests — SdkMetadata JSON round-trip and contract verification

**`tests/CliBuilder.Core.Tests/SdkMetadataSerializationTests.cs`**

```csharp
using System.Text.Json;
using CliBuilder.Core.Models;

namespace CliBuilder.Core.Tests;

public class SdkMetadataSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    [Fact]
    public void SdkMetadata_RoundTrips_ThroughJson()
    {
        var metadata = new SdkMetadata(
            Name: "TestSdk",
            Version: "1.0.0",
            Resources: new List<Resource>
            {
                new Resource(
                    Name: "customer",
                    Description: "Customer resource",
                    Operations: new List<Operation>
                    {
                        new Operation(
                            Name: "list",
                            Description: "List customers",
                            Parameters: new List<Parameter>
                            {
                                new Parameter(
                                    Name: "limit",
                                    Type: new TypeRef(TypeKind.Primitive, "int"),
                                    Required: false,
                                    DefaultValue: JsonDocument.Parse("10").RootElement.Clone()
                                )
                            },
                            ReturnType: new TypeRef(
                                TypeKind.Generic,
                                Name: "StripeList",
                                GenericArguments: new List<TypeRef>
                                {
                                    new TypeRef(TypeKind.Class, "Customer")
                                }
                            )
                        )
                    }
                )
            },
            AuthPatterns: new List<AuthPattern>
            {
                new AuthPattern(
                    Type: AuthType.ApiKey,
                    EnvVar: "TEST_API_KEY",
                    ParameterName: "apiKey"
                )
            }
        );

        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<SdkMetadata>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(metadata.Name, deserialized.Name);
        Assert.Equal(metadata.Version, deserialized.Version);
        Assert.Single(deserialized.Resources);
        Assert.Single(deserialized.Resources[0].Operations);
        Assert.Single(deserialized.Resources[0].Operations[0].Parameters);
        Assert.Equal(TypeKind.Generic, deserialized.Resources[0].Operations[0].ReturnType.Kind);
        Assert.Single(deserialized.AuthPatterns);
    }

    [Fact]
    public void TypeRef_WithNestedGenerics_RoundTrips()
    {
        // Two levels deep: PagedResult<List<Customer>>
        var typeRef = new TypeRef(
            TypeKind.Generic,
            Name: "PagedResult",
            GenericArguments: new List<TypeRef>
            {
                new TypeRef(
                    TypeKind.Generic,
                    Name: "List",
                    GenericArguments: new List<TypeRef>
                    {
                        new TypeRef(TypeKind.Class, "Customer")
                    }
                )
            }
        );

        var json = JsonSerializer.Serialize(typeRef, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TypeRef>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(TypeKind.Generic, deserialized.Kind);
        Assert.Single(deserialized.GenericArguments!);
        Assert.Equal(TypeKind.Generic, deserialized.GenericArguments![0].Kind);
        Assert.Single(deserialized.GenericArguments![0].GenericArguments!);
        Assert.Equal("Customer", deserialized.GenericArguments![0].GenericArguments![0].Name);
    }

    [Fact]
    public void TypeRef_WithEnumValues_RoundTrips()
    {
        var typeRef = new TypeRef(
            TypeKind.Enum,
            Name: "CustomerStatus",
            EnumValues: new List<string> { "Active", "Inactive", "Suspended" }
        );

        var json = JsonSerializer.Serialize(typeRef, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TypeRef>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(TypeKind.Enum, deserialized.Kind);
        Assert.Equal(3, deserialized.EnumValues!.Count);
        Assert.Equal("Active", deserialized.EnumValues![0]);
    }

    [Fact]
    public void DefaultValue_PreservesTypeFidelity()
    {
        var intParam = new Parameter(
            Name: "limit",
            Type: new TypeRef(TypeKind.Primitive, "int"),
            Required: false,
            DefaultValue: JsonDocument.Parse("10").RootElement.Clone()
        );

        var stringParam = new Parameter(
            Name: "name",
            Type: new TypeRef(TypeKind.Primitive, "string"),
            Required: false,
            DefaultValue: JsonDocument.Parse("\"10\"").RootElement.Clone()
        );

        var intJson = JsonSerializer.Serialize(intParam, JsonOptions);
        var stringJson = JsonSerializer.Serialize(stringParam, JsonOptions);

        var intDeserialized = JsonSerializer.Deserialize<Parameter>(intJson, JsonOptions);
        var stringDeserialized = JsonSerializer.Deserialize<Parameter>(stringJson, JsonOptions);

        // int 10 and string "10" must be distinguishable after round-trip
        Assert.Equal(JsonValueKind.Number, intDeserialized!.DefaultValue!.Value.ValueKind);
        Assert.Equal(JsonValueKind.String, stringDeserialized!.DefaultValue!.Value.ValueKind);
    }

    [Fact]
    public void DefaultValue_Null_RoundTrips()
    {
        var param = new Parameter(
            Name: "cursor",
            Type: new TypeRef(TypeKind.Primitive, "string"),
            Required: false,
            DefaultValue: null
        );

        var json = JsonSerializer.Serialize(param, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<Parameter>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.DefaultValue);
    }

    [Fact]
    public void NullOptionalFields_RoundTrip_AsNull_NotEmptyCollections()
    {
        // TypeRef with all optional fields null
        var typeRef = new TypeRef(TypeKind.Primitive, "string");

        var json = JsonSerializer.Serialize(typeRef, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<TypeRef>(json, JsonOptions);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.GenericArguments);  // must be null, not empty list
        Assert.Null(deserialized.EnumValues);
        Assert.Null(deserialized.Properties);
        Assert.Null(deserialized.ElementType);
    }

    [Fact]
    public void SkeletonAdapter_ImplementsInterface()
    {
        // Compile-time verification that DotNetAdapter implements ISdkAdapter
        CliBuilder.Core.Adapters.ISdkAdapter adapter = new CliBuilder.Adapter.DotNet.DotNetAdapter();
        Assert.Throws<NotImplementedException>(() =>
            adapter.Extract(new AdapterOptions("test.dll")));
    }

    [Fact]
    public void SkeletonGenerator_ImplementsInterface()
    {
        // Compile-time verification that CSharpCliGenerator implements ICliGenerator
        CliBuilder.Core.Generators.ICliGenerator generator = new CliBuilder.Generator.CSharp.CSharpCliGenerator();
        var metadata = new SdkMetadata("Test", "1.0", new List<Resource>(), new List<AuthPattern>());
        Assert.Throws<NotImplementedException>(() =>
            generator.Generate(metadata, new GeneratorOptions("/tmp")));
    }
}
```

### 10. Update test project references for skeleton verification

The skeleton interface tests (last two tests above) need references to the adapter and generator projects:

```bash
dotnet add tests/CliBuilder.Core.Tests reference src/CliBuilder.Adapter.DotNet
dotnet add tests/CliBuilder.Core.Tests reference src/CliBuilder.Generator.CSharp
```

### 11. Verify

```bash
dotnet build
dotnet test
```

**Expected:** solution compiles, 8 tests pass.

---

## Checkpoints (human review)

- [ ] Solution structure matches the dependency graph — no circular references
- [ ] All model records match the spec's metadata model tree (lines 159-197)
- [ ] Interfaces match the spec's signatures (lines 114-157)
- [ ] All companion records in `Models/`, not inside interface files
- [ ] All collection types are `IReadOnlyList<T>` (required) or `IReadOnlyList<T>?` (optional)
- [ ] `dotnet build` succeeds with no warnings
- [ ] `dotnet test` passes all 8 tests
- [ ] No `Class1.cs` or `UnitTest1.cs` leftover files

## What this step does NOT include

- Adapter implementation (step 5)
- Generator implementation (step 6)
- TestSdk assembly contents (step 5 — see design notes test SDK manifest)
- CLI entry point wiring (step 6)
- Scriban templates (step 6)
