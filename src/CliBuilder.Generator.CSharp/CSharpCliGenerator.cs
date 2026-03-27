using CliBuilder.Core.Generators;
using CliBuilder.Core.Models;

namespace CliBuilder.Generator.CSharp;

public class CSharpCliGenerator : ICliGenerator
{
    public GeneratorResult Generate(SdkMetadata metadata, GeneratorOptions options)
    {
        // 1. Map + sanitize
        var (model, mapDiagnostics) = ModelMapper.Build(metadata, options);
        var diagnostics = new List<Diagnostic>(mapDiagnostics);

        // 2. Create output directory
        var projectDir = Path.Combine(options.OutputDirectory, model.CliName);
        Directory.CreateDirectory(projectDir);

        // 3. Render templates
        var renderer = new TemplateRenderer();
        var files = new List<string>();

        files.Add(renderer.RenderToFile("csproj.sbn", projectDir, $"{model.CliName}.csproj", model));
        files.Add(renderer.RenderToFile("Program.sbn", projectDir, "Program.cs", model));

        // Commands/ directory — only when resources exist (6B will add ResourceCommands.sbn)
        if (model.Resources.Count > 0)
        {
            var commandsDir = Path.Combine(projectDir, "Commands");
            Directory.CreateDirectory(commandsDir);
        }

        return new GeneratorResult(projectDir, files, diagnostics);
    }
}
