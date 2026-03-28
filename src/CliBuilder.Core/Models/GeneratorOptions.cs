namespace CliBuilder.Core.Models;

public record GeneratorOptions(
    string OutputDirectory,
    string? CliName = null,
    bool OverwriteExisting = false,
    string? SdkProjectPath = null
);
