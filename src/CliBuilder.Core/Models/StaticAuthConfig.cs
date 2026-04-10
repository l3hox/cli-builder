namespace CliBuilder.Core.Models;

public record StaticAuthConfig(
    string TypeName,
    string TypeModule,
    string PropertyName
)
{
    public string ToExpression() =>
        TypeModule.Length > 0
            ? $"{TypeModule}.{TypeName}.{PropertyName}"
            : $"{TypeName}.{PropertyName}";
}
