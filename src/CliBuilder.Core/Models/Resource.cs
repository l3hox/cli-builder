namespace CliBuilder.Core.Models;

public record Resource(
    string Name,
    string? Description,
    IReadOnlyList<Operation> Operations,
    string? SourceClassName = null,
    string? SourceNamespace = null,
    string? ConstructorAuthTypeName = null,
    string? ConstructorAuthTypeNamespace = null
);
