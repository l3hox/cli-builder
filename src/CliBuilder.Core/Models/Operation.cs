namespace CliBuilder.Core.Models;

public record Operation(
    string Name,
    string? Description,
    IReadOnlyList<Parameter> Parameters,
    TypeRef ReturnType,
    bool IsStreaming = false
);
