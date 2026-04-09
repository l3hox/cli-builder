namespace CliBuilder.Core.Models;

public record StaticAuthConfig(
    string TypeName,
    string TypeModule,
    string PropertyName
);
