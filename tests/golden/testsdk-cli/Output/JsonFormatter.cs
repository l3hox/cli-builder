using System.Text.Json;

namespace TestsdkCli.Output;

public static class JsonFormatter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static void Write(object? result, TextWriter? output = null)
    {
        output ??= Console.Out;
        var json = JsonSerializer.Serialize(result, Options);
        output.WriteLine(json);
    }
}
