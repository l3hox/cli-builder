using System.Text.Json;
using System.Text.Json.Serialization;

namespace CliBuilder.Core.Json;

public static class SdkMetadataJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
