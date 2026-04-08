# Step 10: cli-builder CLI Entry Point

**Prerequisite:** Steps 1-9 complete. cli-builder is a library — users run demo scripts or test runners. 347 tests, 93.4% coverage.
**Output:** `cli-builder generate --assembly Stripe.net.dll --output ./stripe-cli` works. Users can install and run cli-builder as a `dotnet tool`. The `inspect` command dumps metadata without generating. `--help` and `--version` work out of the box.

---

## Problem

cli-builder has no CLI. The entire pipeline (adapter → generator → build) is orchestrated by shell scripts (`demo-stripe.sh`) or test code. A user who clones the repo has no `cli-builder` command to run. This is the #1 blocker for anyone outside the repo using the tool.

---

## Design

### Commands

**`cli-builder generate`** — the primary command:
```
cli-builder generate --assembly <path> --output <dir> [--name <name>] [--overwrite]
```

| Flag | Required | Description |
|------|----------|-------------|
| `--assembly` | Yes | Path to the SDK assembly (DLL) to generate a CLI from |
| `--output` | Yes | Output directory for the generated CLI project |
| `--name` | No | CLI name (default: derived from assembly name) |
| `--overwrite` | No | Overwrite existing output directory |

Note: `--config` and `--xml-doc` from `AdapterOptions` are not yet exposed. Deferred to a future step.

Behavior:
1. Extract metadata: `adapter.Extract(assemblyPath)`
2. Generate CLI: `generator.Generate(metadata, options)`
3. Print diagnostics to stderr (grouped by severity)
4. Print summary to stdout: `Generated {N} resources with {M} operations to {path}`
5. Exit codes per `design-notes.md`:
   - **0** — success (Info/Warning diagnostics are OK)
   - **1** — partial failure (any Error-level diagnostic)
   - **2** — environment failure (exception: file not found, corrupted assembly, I/O error)

**`cli-builder inspect`** — dump metadata without generating:
```
cli-builder inspect --assembly <path> [--json]
```

| Flag | Required | Description |
|------|----------|-------------|
| `--assembly` | Yes | Path to the SDK assembly (DLL) to inspect |
| `--json` | No | Output as JSON (default: human-readable summary) |

Behavior:
1. Extract metadata: `adapter.Extract(assemblyPath)`
2. If `--json`: serialize `{ metadata, diagnostics }` to stdout (intentional schema, not raw `AdapterResult`)
3. Else: print human-readable summary (resources, operations, auth, diagnostics)
4. Exit codes: same as `generate` (0/1/2 per `design-notes.md`)

### Diagnostics output

Both commands print diagnostics to stderr:
```
[INFO]  CB202  Noun collision resolved: CustomerService (Stripe.TestHelpers) → 'test-helpers-customer'
[WARN]  CB306  Operation 'deserialize' returns non-awaitable type 'T' — falling back to echo stub
[ERROR] CB202  Noun collision unresolvable: FooService and BarService
```

Color when stderr is a terminal (not redirected). No color when piped.

### dotnet tool packaging

The project should be publishable as a `dotnet tool`:
```bash
dotnet tool install --global cli-builder
cli-builder generate --assembly ./Stripe.net.dll --output ./stripe-cli
```

The `.csproj` needs:
```xml
<PackAsTool>true</PackAsTool>
<ToolCommandName>cli-builder</ToolCommandName>
<PackageId>cli-builder</PackageId>
<Version>1.0.0</Version>
<Authors>Jakub Lehotsky</Authors>
<Description>Generates agent-ready CLIs from .NET SDK assemblies</Description>
```

`PublishSingleFile` is NOT needed — `dotnet tool` packing bundles transitive dependencies automatically.

---

## Implementation

### Existing project

There's already a `src/CliBuilder/` project in the solution (referenced in `cli-builder.sln`). It's an Exe with System.CommandLine. This is where the CLI goes.

### Files to create/modify

| File | Change |
|------|--------|
| `src/CliBuilder/Program.cs` | Root command with `generate` and `inspect` subcommands. Set `--version` via assembly version. All `Command` and `Option` constructors include `Description` strings for `--help`. Wire concrete `DotNetAdapter` + `CSharpCliGenerator` into handlers. |
| `src/CliBuilder/CliBuilder.csproj` | Add `PackAsTool`, `ToolCommandName`, `PackageId`, `Version`, `Authors`, `Description` |
| `src/CliBuilder/Commands/GenerateCommand.cs` | Generate handler — accepts `ISdkAdapter` + `ICliGenerator`, catches `FileNotFoundException`/`BadImageFormatException`/`FileLoadException` → exit 2, `IOException`/`UnauthorizedAccessException` → exit 2 |
| `src/CliBuilder/Commands/InspectCommand.cs` | Inspect handler — accepts `ISdkAdapter`, same exception handling, `--json` serializes `{ metadata, diagnostics }` |
| `src/CliBuilder/DiagnosticsFormatter.cs` | Format diagnostics with color + codes. Accepts `TextWriter` parameter for testability. Color when `Console.IsErrorRedirected == false`. |

### Generate command handler

Accepts `ISdkAdapter` and `ICliGenerator` for testability. `Program.cs` passes concrete types.

```csharp
public static int Execute(ISdkAdapter adapter, ICliGenerator generator,
    string assemblyPath, string outputDir, string? name, bool overwrite)
{
    // 1. Extract
    AdapterResult adapterResult;
    try
    {
        adapterResult = adapter.Extract(new AdapterOptions(assemblyPath));
    }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine($"Error: Assembly not found: {ex.FileName}");
        return 2;
    }
    catch (BadImageFormatException)
    {
        Console.Error.WriteLine($"Error: '{assemblyPath}' is not a valid .NET assembly");
        return 2;
    }
    catch (FileLoadException ex)
    {
        Console.Error.WriteLine($"Error: Could not load assembly: {ex.Message}");
        return 2;
    }

    // 2. Generate
    GeneratorResult genResult;
    try
    {
        genResult = generator.Generate(adapterResult.Metadata,
            new GeneratorOptions(outputDir, name, overwrite));
    }
    catch (IOException ex)
    {
        Console.Error.WriteLine($"Error: Output path problem: {ex.Message}");
        return 2;
    }
    catch (UnauthorizedAccessException ex)
    {
        Console.Error.WriteLine($"Error: Permission denied: {ex.Message}");
        return 2;
    }

    // 3. Report diagnostics
    var allDiagnostics = adapterResult.Diagnostics.Concat(genResult.Diagnostics).ToList();
    DiagnosticsFormatter.Print(allDiagnostics);

    // 4. Summary
    var resourceCount = adapterResult.Metadata.Resources.Count;
    var opCount = adapterResult.Metadata.Resources.Sum(r => r.Operations.Count);
    Console.WriteLine($"Generated {resourceCount} resources with {opCount} operations to {genResult.ProjectDirectory}");

    return allDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? 1 : 0;
}
```

### Inspect command handler

Same interface injection pattern. Same exception handling as `generate`.

```csharp
public static int Execute(ISdkAdapter adapter, string assemblyPath, bool json)
{
    AdapterResult result;
    try
    {
        result = adapter.Extract(new AdapterOptions(assemblyPath));
    }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine($"Error: Assembly not found: {ex.FileName}");
        return 2;
    }
    catch (BadImageFormatException)
    {
        Console.Error.WriteLine($"Error: '{assemblyPath}' is not a valid .NET assembly");
        return 2;
    }
    catch (FileLoadException ex)
    {
        Console.Error.WriteLine($"Error: Could not load assembly: {ex.Message}");
        return 2;
    }

    if (json)
    {
        // Intentional schema: metadata + diagnostics as separate keys
        var output = new { metadata = result.Metadata, diagnostics = result.Diagnostics };
        Console.WriteLine(JsonSerializer.Serialize(output, SdkMetadataJson.Options));
    }
    else
    {
        // Human-readable summary
        Console.WriteLine($"SDK: {result.Metadata.Name} {result.Metadata.Version}");
        Console.WriteLine($"Resources: {result.Metadata.Resources.Count}");
        Console.WriteLine($"Auth: {(result.Metadata.AuthPatterns.Count > 0 ? "detected" : "none")}");
        Console.WriteLine($"Static auth: {result.Metadata.StaticAuthSetup ?? "none"}");
        foreach (var r in result.Metadata.Resources.OrderBy(r => r.Name))
            Console.WriteLine($"  {r.Name} ({r.Operations.Count} operations)");
    }

    DiagnosticsFormatter.Print(result.Diagnostics);
    return result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? 1 : 0;
}
```

---

## Tests

### Exit code contract (7 tests)

| Test | What it verifies |
|------|-----------------|
| `GenerateCommand_ValidAssembly_ExitsZero` | Success path, exit 0 |
| `GenerateCommand_WarningDiagnostics_ExitsZero` | Warnings don't trigger exit 1 |
| `GenerateCommand_ErrorDiagnostics_ExitsOne` | Error-level diagnostic → exit 1 |
| `GenerateCommand_AssemblyNotFound_ExitsTwo` | FileNotFoundException → exit 2 |
| `GenerateCommand_CorruptedAssembly_ExitsTwo` | BadImageFormatException → exit 2 (pass non-.NET file with .dll extension) |
| `InspectCommand_ValidAssembly_ExitsZero` | Inspect success path |
| `InspectCommand_AssemblyNotFound_ExitsTwo` | Same error handling as generate |

### Output correctness (4 tests)

| Test | What it verifies |
|------|-----------------|
| `InspectCommand_ValidAssembly_PrintsSummary` | Human-readable output includes SDK name, resource count |
| `InspectCommand_Json_HasExpectedSchema` | JSON has `metadata` and `diagnostics` as separate top-level keys, `metadata.resources` is an array with known TestSdk entries |
| `GenerateCommand_NameFlag_PropagatesCliName` | `--name my-cli` → output uses `my-cli` |
| `GenerateCommand_OverwriteFalse_ExistingOutput_Rejects` | Non-overwrite on existing dir → clean error |

### Formatting (3 tests)

| Test | What it verifies |
|------|-----------------|
| `DiagnosticsFormatter_GroupsBySeverity` | Error, Warning, Info groups in output |
| `DiagnosticsFormatter_NoColorWhenRedirected` | No ANSI codes when `TextWriter` is not a terminal |
| `HelpOutput_HasDescriptions` | `--help` output includes description strings for all flags |

### Integration tests (3 tests)

| Test | What it verifies |
|------|-----------------|
| `CliTool_Generate_ProducesCompilableProject` | Run actual CLI binary against TestSdk, `dotnet build` succeeds |
| `CliTool_Inspect_Json_RoundTrips` | Inspect → parse JSON → verify resources match TestSdk |
| `CliTool_VersionFlag_ReturnsVersion` | `--version` returns non-empty version string |

Note: handlers accept `ISdkAdapter`/`ICliGenerator` interfaces, so unit tests can use the real `DotNetAdapter` with the TestSdk fixture (already available in test projects) without needing mocks. The exit code 2 tests use a non-.NET file (e.g., a text file renamed to `.dll`) to trigger `BadImageFormatException`.

---

## Verification

```bash
# Build TestSdk first (so the DLL exists)
dotnet build tests/CliBuilder.TestSdk

# Build the CLI
dotnet build src/CliBuilder

# Run it (use project-relative path, not /tmp which doesn't exist on Windows)
dotnet run --project src/CliBuilder -- generate \
  --assembly tests/CliBuilder.TestSdk/bin/Debug/net8.0/CliBuilder.TestSdk.dll \
  --output ./test-output/test-cli

# Inspect
dotnet run --project src/CliBuilder -- inspect \
  --assembly tests/CliBuilder.TestSdk/bin/Debug/net8.0/CliBuilder.TestSdk.dll

# Version
dotnet run --project src/CliBuilder -- --version

# Install as global tool
dotnet pack src/CliBuilder -o ./artifacts
dotnet tool install --global --add-source ./artifacts cli-builder
cli-builder --version
cli-builder generate --assembly Stripe.net.dll --output ./stripe-cli
```

---

## Risk

Low. The CLI is pure orchestration — it calls `ISdkAdapter.Extract()` and `ICliGenerator.Generate()` which are already tested. The new code is ~200 lines of command parsing + error handling + diagnostics formatting.

Key risks:
- **Exception handling coverage** — `BadImageFormatException`, `FileLoadException`, `IOException`, `UnauthorizedAccessException` must all be caught and mapped to exit code 2. Missing one produces a raw stack trace. Covered by 17 tests including dedicated exit-code-2 test cases.
- **`--json` schema stability** — `inspect --json` exposes a machine-readable contract (`{ metadata, diagnostics }`). Changes to this schema are breaking for agent consumers. The `InspectCommand_Json_HasExpectedSchema` test pins the shape.
- **`dotnet tool` packaging** — `<PackAsTool>` (not `<PackageAsTool>`) is the correct property. `PublishSingleFile` is NOT needed — dotnet tool packing bundles transitive dependencies (MetadataLoadContext, Scriban, System.CommandLine) automatically.
