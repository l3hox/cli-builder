using System.Text.Json;

namespace TestsdkCli.Output;

public static class JsonFormatter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IncludeFields = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static void Write(object? result, TextWriter? output = null)
    {
        output ??= Console.Out;
        // Some SDK types (e.g., OpenAI) don't expose public properties for
        // System.Text.Json. Try standard serialization first; if it produces
        // an empty object, fall back to ToString() which many SDK types implement.
        var json = JsonSerializer.Serialize(result, Options);
        if (json is "{}" or "[]" && result != null)
        {
            var str = result.ToString();
            if (str != null && str != result.GetType().ToString())
            {
                output.WriteLine(str);
                return;
            }
        }
        output.WriteLine(json);
    }
}
