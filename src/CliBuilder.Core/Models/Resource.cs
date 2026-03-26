namespace CliBuilder.Core.Models;

public record Resource(
    string Name,
    string? Description,
    IReadOnlyList<Operation> Operations
);
