namespace CliBuilder.Core.Models;

public record AdapterOptions(
    string ArtifactPath,
    string? ConfigPath = null,
    string? DocsPath = null
);
