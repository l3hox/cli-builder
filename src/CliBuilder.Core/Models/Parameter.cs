using System.Text.Json;

namespace CliBuilder.Core.Models;

public record Parameter(
    string Name,
    TypeRef Type,
    bool Required,
    JsonElement? DefaultValue = null,
    string? Description = null
);
