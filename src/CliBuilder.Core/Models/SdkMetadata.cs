namespace CliBuilder.Core.Models;

public record SdkMetadata(
    string Name,
    string Version,
    IReadOnlyList<Resource> Resources,
    IReadOnlyList<AuthPattern> AuthPatterns,
    string? StaticAuthSetup = null
);
