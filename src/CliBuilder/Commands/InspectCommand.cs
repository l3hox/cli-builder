using System.Text.Json;
using CliBuilder.Core.Adapters;
using CliBuilder.Core.Json;
using CliBuilder.Core.Models;

namespace CliBuilder.Commands;

public static class InspectCommand
{
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
            var output = new { metadata = result.Metadata, diagnostics = result.Diagnostics };
            Console.WriteLine(JsonSerializer.Serialize(output, SdkMetadataJson.Options));
        }
        else
        {
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
}
