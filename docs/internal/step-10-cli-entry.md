# Step 10: cli-builder CLI Entry Point

**Prerequisite:** Steps 1-9 complete. cli-builder is a library — users run demo scripts or test runners. 347 tests, 93.4% coverage.
**Output:** `cli-builder generate --assembly Stripe.net.dll --output ./stripe-cli` works. Users can install and run cli-builder as a `dotnet tool`. The `inspect` command dumps metadata without generating.

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
| `--assembly` | Yes | Path to the SDK DLL |
| `--output` | Yes | Output directory for the generated CLI project |
| `--name` | No | CLI name (default: derived from assembly name) |
| `--overwrite` | No | Overwrite existing output directory |

Behavior:
1. Extract metadata: `DotNetAdapter.Extract(assemblyPath)`
2. Generate CLI: `CSharpCliGenerator.Generate(metadata, options)`
3. Print diagnostics to stderr (grouped by severity)
4. Print summary to stdout: `Generated {N} resources with {M} operations to {path}`
5. Exit code: 0 if no errors, 1 if any Error-level diagnostics

**`cli-builder inspect`** — dump metadata without generating:
```
cli-builder inspect --assembly <path> [--json]
```

| Flag | Required | Description |
|------|----------|-------------|
| `--assembly` | Yes | Path to the SDK DLL |
| `--json` | No | Output as JSON (default: human-readable summary) |

Behavior:
1. Extract metadata: `DotNetAdapter.Extract(assemblyPath)`
2. If `--json`: serialize full `AdapterResult` to stdout
3. Else: print human-readable summary (resources, operations, auth, diagnostics)
4. Exit code: 0 if no errors, 1 if any Error-level diagnostics

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
<PackageAsTool>true</PackageAsTool>
<ToolCommandName>cli-builder</ToolCommandName>
```

---

## Implementation

### Existing project

There's already a `src/CliBuilder/` project in the solution (referenced in `cli-builder.sln`). It's an Exe with System.CommandLine. This is where the CLI goes.

### Files to create/modify

| File | Change |
|------|--------|
| `src/CliBuilder/Program.cs` | Root command with `generate` and `inspect` subcommands |
| `src/CliBuilder/CliBuilder.csproj` | Add PackageAsTool, reference Adapter + Generator |
| `src/CliBuilder/Commands/GenerateCommand.cs` | Generate command handler |
| `src/CliBuilder/Commands/InspectCommand.cs` | Inspect command handler |
| `src/CliBuilder/DiagnosticsFormatter.cs` | Format diagnostics with color + codes |

### Generate command handler

```csharp
public static async Task<int> Execute(string assemblyPath, string outputDir, string? name, bool overwrite)
{
    // 1. Extract
    var adapter = new DotNetAdapter();
    AdapterResult adapterResult;
    try
    {
        adapterResult = adapter.Extract(new AdapterOptions(assemblyPath));
    }
    catch (FileNotFoundException ex)
    {
        Console.Error.WriteLine($"Assembly not found: {ex.FileName}");
        return 1;
    }

    // 2. Generate
    var generator = new CSharpCliGenerator();
    var genResult = generator.Generate(adapterResult.Metadata,
        new GeneratorOptions(outputDir, name, overwrite));

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

```csharp
public static int Execute(string assemblyPath, bool json)
{
    var adapter = new DotNetAdapter();
    var result = adapter.Extract(new AdapterOptions(assemblyPath));

    if (json)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, SdkMetadataJson.Options));
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

### Unit tests
- `GenerateCommand_MissingAssembly_ExitsWithError`
- `GenerateCommand_ValidAssembly_ExitsZero`
- `InspectCommand_ValidAssembly_PrintsSummary`
- `InspectCommand_Json_OutputsValidJson`
- `DiagnosticsFormatter_GroupsBySeverity`
- `DiagnosticsFormatter_NoColorWhenRedirected`

### Integration tests
- `CliTool_Generate_ProducesCompilableProject` — run the actual CLI binary against TestSdk
- `CliTool_Inspect_Json_RoundTrips` — inspect → parse JSON → verify resources

---

## Verification

```bash
# Build the CLI
dotnet build src/CliBuilder

# Run it
dotnet run --project src/CliBuilder -- generate --assembly tests/CliBuilder.TestSdk/bin/Debug/net8.0/CliBuilder.TestSdk.dll --output /tmp/test-cli

# Inspect
dotnet run --project src/CliBuilder -- inspect --assembly tests/CliBuilder.TestSdk/bin/Debug/net8.0/CliBuilder.TestSdk.dll

# Install as global tool
dotnet pack src/CliBuilder
dotnet tool install --global --add-source ./artifacts cli-builder
cli-builder generate --assembly Stripe.net.dll --output ./stripe-cli
```

---

## Risk

Low. The CLI is pure orchestration — it calls `DotNetAdapter.Extract()` and `CSharpCliGenerator.Generate()` which are already tested. The new code is ~150 lines of command parsing + diagnostics formatting.

The main risk is the `dotnet tool` packaging — ensuring the tool can find its dependencies at install time. This may require `PublishSingleFile` or specific packaging configuration.
