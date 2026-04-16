using CliBuilder.Core.Adapters;
using CliBuilder.Core.Generators;
using CliBuilder.Core.Models;

namespace CliBuilder.Commands;

public static class GenerateCommand
{
    public static int Execute(ISdkAdapter adapter, ICliGenerator generator,
        string assemblyPath, string outputDir, string? name, bool overwrite)
    {
        if (!overwrite && Directory.Exists(outputDir) && Directory.EnumerateFileSystemEntries(outputDir).Any())
        {
            Console.Error.WriteLine($"Error: Output directory already exists: {outputDir}");
            Console.Error.WriteLine("Use --overwrite to replace it.");
            return 2;
        }

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

        var allDiagnostics = adapterResult.Diagnostics.Concat(genResult.Diagnostics).ToList();
        DiagnosticsFormatter.Print(allDiagnostics);

        var resourceCount = adapterResult.Metadata.Resources.Count;
        var opCount = adapterResult.Metadata.Resources.Sum(r => r.Operations.Count);
        Console.WriteLine($"Generated {resourceCount} resources with {opCount} operations to {genResult.ProjectDirectory}");

        return allDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? 1 : 0;
    }
}
