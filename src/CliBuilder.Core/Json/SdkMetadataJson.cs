using System.Text.Json;

namespace CliBuilder.Core.Json;

public static class SdkMetadataJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
