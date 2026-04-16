namespace CliBuilder.Core.Models;

public record GeneratorResult(
    string ProjectDirectory,
    IReadOnlyList<string> GeneratedFiles,
    IReadOnlyList<Diagnostic> Diagnostics
);
