namespace CliBuilder.Core.Models;

public record AdapterResult(
    SdkMetadata Metadata,
    IReadOnlyList<Diagnostic> Diagnostics
);
