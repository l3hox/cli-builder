using System.Text.Json;

namespace TestsdkCli.Output;

public static class TableFormatter
{
    private static readonly JsonSerializerOptions SerializeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void Write(object? result, TextWriter? output = null)
    {
        output ??= Console.Out;

        if (result is null)
        {
            output.WriteLine("(no result)");
            return;
        }

        // Serialize to JSON then read as document — works for any object type
        var json = JsonSerializer.Serialize(result, SerializeOptions);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
            WriteTable(root, output);
        else if (root.ValueKind == JsonValueKind.Object)
            WriteProperties(root, output);
        else
            output.WriteLine(root.ToString());
    }

    private static void WriteProperties(JsonElement obj, TextWriter output)
    {
        var maxKeyLen = 0;
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Length > maxKeyLen)
                maxKeyLen = prop.Name.Length;
        }

        foreach (var prop in obj.EnumerateObject())
        {
            var value = prop.Value.ValueKind == JsonValueKind.Null ? "-" : prop.Value.ToString();
            var useColor = !Console.IsOutputRedirected;
            if (useColor)
                output.WriteLine($"\u001b[1m{prop.Name.PadRight(maxKeyLen)}\u001b[0m  {value}");
            else
                output.WriteLine($"{prop.Name.PadRight(maxKeyLen)}  {value}");
        }
    }

    private static void WriteTable(JsonElement arr, TextWriter output)
    {
        var columns = new List<string>();
        var rows = new List<Dictionary<string, string>>();

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<string, string>();
            foreach (var prop in item.EnumerateObject())
            {
                if (!columns.Contains(prop.Name))
                    columns.Add(prop.Name);
                row[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? "-" : prop.Value.ToString();
            }
            rows.Add(row);
        }

        if (columns.Count == 0) return;

        // Calculate column widths
        var widths = columns.ToDictionary(c => c, c => c.Length);
        foreach (var row in rows)
        {
            foreach (var col in columns)
            {
                if (row.TryGetValue(col, out var val) && val.Length > widths[col])
                    widths[col] = val.Length;
            }
        }

        var useColor = !Console.IsOutputRedirected;

        // Header
        var header = string.Join("  ", columns.Select(c => c.PadRight(widths[c])));
        if (useColor)
            output.WriteLine($"\u001b[1m{header}\u001b[0m");
        else
            output.WriteLine(header);

        // Separator
        output.WriteLine(string.Join("  ", columns.Select(c => new string('-', widths[c]))));

        // Rows
        foreach (var row in rows)
        {
            var line = string.Join("  ", columns.Select(c =>
                (row.TryGetValue(c, out var val) ? val : "-").PadRight(widths[c])));
            output.WriteLine(line);
        }
    }
}
