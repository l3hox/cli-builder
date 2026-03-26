namespace CliBuilder.Core.Models;

public record AuthPattern(
    AuthType Type,
    string EnvVar,
    string ParameterName,
    string? HeaderName = null,
    string? Description = null
);

public enum AuthType
{
    ApiKey,
    BearerToken,
    OAuth,
    Custom
}
