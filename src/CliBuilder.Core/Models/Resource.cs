namespace CliBuilder.Core.Models;

public record Resource(
    string Name,
    string? Description,
    IReadOnlyList<Operation> Operations,
    string? SourceClassName = null,
    string? SourceModule = null,
    IReadOnlyList<ConstructorParam>? ConstructorParams = null,
    bool HasParameterlessCtor = false
);

public record ConstructorParam(
    string Name,
    string TypeName,
    string? TypeModule,
    bool IsAuth,
    bool IsRequired
);
